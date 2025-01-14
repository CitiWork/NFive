using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using NFive.SDK.Core.Diagnostics;
using NFive.SDK.Core.Events;
using NFive.SDK.Core.Helpers;
using NFive.SDK.Core.Models.Player;
using NFive.SDK.Server.Communications;
using NFive.SDK.Server.Configuration;
using NFive.SDK.Server.Controllers;
using NFive.SDK.Server.Events;
using NFive.SDK.Server.Storage;
using NFive.Server.Configuration;
using NFive.Server.Events;
using NFive.Server.Rpc;
using NFive.Server.Storage;
using TimeZoneConverter;

namespace NFive.Server.Controllers
{
	public class SessionController : ConfigurableController<SessionConfiguration>
	{
		private readonly ICommunicationManager comms;
		private readonly List<Action> sessionCallbacks = new List<Action>();
		private readonly ConcurrentDictionary<Guid, Session> sessions = new ConcurrentDictionary<Guid, Session>();
		private readonly object threadLock = new object();
		private readonly ConcurrentDictionary<Session, Tuple<Task, CancellationTokenSource>> threads = new ConcurrentDictionary<Session, Tuple<Task, CancellationTokenSource>>();

		private int? CurrentHost { get; set; } // TODO: Make available

		public SessionController(ILogger logger, SessionConfiguration configuration, ICommunicationManager comms) : base(logger, configuration)
		{
			this.comms = comms;
		}

		public override Task Loaded()
		{
			// Rebroadcast raw FiveM RPC events as wrapped server events
			RpcManager.OnRaw(FiveMServerEvents.HostingSession, new Action<Player>(OnHostingSessionRaw));
			RpcManager.OnRaw(FiveMServerEvents.HostedSession, new Action<Player>(OnHostedSessionRaw));

			this.comms.Event(ServerEvents.HostedSession).FromServer().On<IClient>(OnHostedSession);
			this.comms.Event(ServerEvents.PlayerConnecting).FromServer().On<IClient, ConnectionDeferrals>(OnConnecting);
			this.comms.Event(ServerEvents.PlayerDropped).FromServer().On<IClient, string>(OnDropped);

			this.comms.Event(CoreEvents.ClientInitialize).FromClients().OnRequest<string>(OnInitialize);
			this.comms.Event(CoreEvents.ClientInitialized).FromClients().On(OnInitialized);
			this.comms.Event(SessionEvents.DisconnectPlayer).FromClients().On<string>(OnDisconnect);
			this.comms.Event(ServerEvents.ServerInitialized).FromServer().On(OnSeverInitialized);

			this.comms.Event(SessionEvents.GetMaxPlayers).FromServer().On(e => e.Reply(this.Configuration.MaxClients));
			this.comms.Event(SessionEvents.GetCurrentSessionsCount).FromServer().On(e => e.Reply(this.sessions.Count));
			this.comms.Event(SessionEvents.GetCurrentSessions).FromServer().On(e => e.Reply(this.sessions.ToList()));

			return base.Loaded();
		}

		public override Task Started()
		{
			API.EnableEnhancedHostSupport(true);

			return base.Started();
		}

		private async void OnHostingSessionRaw([FromSource] Player player)
		{
			var client = new Client(player.Handle);

			this.comms.Event(ServerEvents.HostingSession).ToServer().Emit(client);

			if (this.CurrentHost != null)
			{
				player.TriggerEvent("sessionHostResult", "wait");

				this.sessionCallbacks.Add(() => player.TriggerEvent("sessionHostResult", "free"));

				return;
			}

			string hostId;

			try
			{
				hostId = API.GetHostId();
			}
			catch (NullReferenceException)
			{
				hostId = null;
			}

			if (!string.IsNullOrEmpty(hostId) && API.GetPlayerLastMsg(API.GetHostId()) < 1000)
			{
				player.TriggerEvent("sessionHostResult", "conflict");

				return;
			}

			this.sessionCallbacks.Clear();
			this.CurrentHost = client.Handle;

			this.Logger.Debug($"Game host is now {this.CurrentHost}");

			player.TriggerEvent("sessionHostResult", "go");

			await BaseScript.Delay(5000);

			this.sessionCallbacks.ForEach(c => c());
			this.CurrentHost = null;
		}

		private void OnHostedSessionRaw([FromSource] Player player)
		{
			this.comms.Event(ServerEvents.HostedSession).ToServer().Emit(new Client(player.Handle));
		}

		private async void OnSeverInitialized(ICommunicationMessage e)
		{
			var lastActive = await this.comms.Event(BootEvents.GetLastActiveTime).ToServer().Request<DateTime>();

			using (var context = new StorageContext())
			using (var transaction = await context.Database.BeginTransactionAsync())
			{
				lastActive = lastActive == default ? DateTime.UtcNow : lastActive;
				var disconnectedSessions = context.Sessions.Where(s => s.Disconnected == null).ToList();
				foreach (var disconnectedSession in disconnectedSessions)
				{
					disconnectedSession.Disconnected = lastActive;
					disconnectedSession.DisconnectReason = "Session killed, disconnect time set to last server active time";
					context.Sessions.Update(disconnectedSession);
				}

				await context.SaveChangesAsync();
				await transaction.CommitAsync();
			}
		}

		private void OnHostedSession(ICommunicationMessage e, IClient client)
		{
			if (this.CurrentHost != null && this.CurrentHost != client.Handle) return;

			this.sessionCallbacks.ForEach(c => c());
			this.CurrentHost = null;
		}

		private async void OnConnecting(ICommunicationMessage e, IClient client, ConnectionDeferrals deferrals)
		{
			Session session = null;
			User user = null;

			this.comms.Event(SessionEvents.ClientConnecting).ToServer().Emit(client, deferrals);

			using (var context = new StorageContext())
			using (var transaction = await context.Database.BeginTransactionAsync())
			{
				context.ChangeTracker.LazyLoadingEnabled = false;

				try
				{
					user = context.Users.SingleOrDefault(u => u.License == client.License);

					if (user == default(User))
					{
						this.comms.Event(SessionEvents.UserCreating).ToServer().Emit(client);

						// Create user
						user = new User
						{
							Id = GuidGenerator.GenerateTimeBasedGuid(),
							License = client.License,
							SteamId = client.SteamId,
							Name = client.Name
						};

						context.Users.Add(user);
						await context.SaveChangesAsync();

						this.comms.Event(SessionEvents.UserCreated).ToServer().Emit(client, user);
					}
					else
					{
						// Update details
						user.Name = client.Name;
						if (client.SteamId.HasValue) user.SteamId = client.SteamId;
					}

					this.comms.Event(SessionEvents.SessionCreating).ToServer().Emit(client);

					// Create session
					session = new Session
					{
						Id = GuidGenerator.GenerateTimeBasedGuid(),
						User = user,
						IpAddress = client.EndPoint,
						Created = DateTime.UtcNow,
						Handle = client.Handle
					};

					context.Sessions.Add(session);

					// Save changes
					await context.SaveChangesAsync();
					await transaction.CommitAsync();
				}
				catch (EntityValidationException ex)
				{
					var errorMessages = ex.ValidationErrors.SelectMany(x => x.ErrorMessage);

					var fullErrorMessage = string.Join("; ", errorMessages);

					var exceptionMessage = string.Concat(ex.Message, " The Validation errors are: ", fullErrorMessage);
					await transaction.RollbackAsync();
					throw new EntityValidationException(exceptionMessage, ex.ValidationErrors);
				}
				catch (Exception ex)
				{
					await transaction.RollbackAsync();

					this.Logger.Error(ex);
				}
			}

			if (user == null || session == null) throw new Exception($"Failed to create session for {client.Name}");

			this.sessions[session.Id] = session;
			var threadCancellationToken = new CancellationTokenSource();

			lock (this.threads)
			{
				this.threads.TryAdd(session, new Tuple<Task, CancellationTokenSource>(Task.Factory.StartNew(() => MonitorSession(session, client), threadCancellationToken.Token), threadCancellationToken));
			}

			this.comms.Event(SessionEvents.SessionCreated).ToServer().Emit(client, session, deferrals);

			if (this.sessions.Any(s => s.Value.User.Id == user.Id && s.Key != session.Id)) OnReconnecting(client, session);

			this.comms.Event(SessionEvents.ClientConnected).ToServer().Emit(client, session);

			this.Logger.Debug($"[{session.Id}] Player \"{user.Name}\" connected from {session.IpAddress}");
		}

		private void OnReconnecting(IClient client, Session session)
		{
			this.Logger.Trace($"Client reconnecting: {session.UserId}");
			var oldSession = this.sessions.Select(s => s.Value).OrderBy(s => s.Created).FirstOrDefault(s => s.User.Id == session.UserId);
			if (oldSession == null) return;

			this.comms.Event(SessionEvents.ClientReconnecting).ToServer().Emit(client, session, oldSession);

			lock (this.threadLock)
			{
				var oldThread = this.threads.OrderBy(t => t.Key.Created).FirstOrDefault(t => t.Key.UserId == session.UserId).Key;
				if (oldThread != null)
				{
					this.Logger.Trace($"Disposing of old thread: {oldThread.User.Name}");
					this.threads[oldThread].Item2.Cancel();
					this.threads[oldThread].Item1.Wait();
					this.threads[oldThread].Item2.Dispose();
					this.threads[oldThread].Item1.Dispose();
					this.threads.TryRemove(oldThread, out var _);
				}
			}

			this.sessions.TryRemove(oldSession.Id, out oldSession);

			this.comms.Event(SessionEvents.ClientReconnected).ToServer().Emit(client, session, oldSession);
		}

		private void OnDropped(ICommunicationMessage e, IClient client, string disconnectReason)
		{
			OnDisconnecting(client, disconnectReason);
		}

		private static void OnDisconnect(ICommunicationMessage e, string disconnectReason)
		{
			API.DropPlayer(e.Client.Handle.ToString(), disconnectReason);
		}

		private async void OnDisconnecting(IClient client, string disconnectReason)
		{
			this.comms.Event(SessionEvents.ClientDisconnecting).ToServer().Emit(client);

			using (var context = new StorageContext())
			{
				context.ChangeTracker.LazyLoadingEnabled = false;

				var session = this.sessions.Select(s => s.Value).OrderBy(s => s.Created).FirstOrDefault(s => s.User.License == client.License && s.Disconnected == null && s.DisconnectReason == null);
				if (session == null) throw new Exception($"No session to end for disconnected user \"{client.Name}\""); // TODO: SessionException

				session.Disconnected = DateTime.UtcNow;
				session.DisconnectReason = disconnectReason;
				context.Sessions.Update(session);
				await context.SaveChangesAsync();

				lock (this.threadLock)
				{
					var oldThread = this.threads.SingleOrDefault(t => t.Key.UserId == session.UserId).Key;
					if (oldThread != null)
					{
						this.threads[oldThread].Item2.Cancel();
						this.threads[oldThread].Item1.Wait();
						this.threads[oldThread].Item2.Dispose();
						this.threads[oldThread].Item1.Dispose();
						this.threads.TryRemove(oldThread, out var _);
					}

					var threadCancellationToken = new CancellationTokenSource();
					this.threads.TryAdd(session, Tuple.Create<Task, CancellationTokenSource>(Task.Factory.StartNew(() => MonitorSession(session, client), threadCancellationToken.Token), threadCancellationToken));
				}

				this.comms.Event(SessionEvents.ClientDisconnected).ToServer().Emit(client, session);

				this.Logger.Debug($"[{session.Id}] Player \"{client.Name}\" disconnected: {session.DisconnectReason}");
			}
		}

		private void OnInitialize(ICommunicationMessage e, string clientVersion)
		{
			if (clientVersion != typeof(Program).Assembly.GetName().Version.ToString())
			{
				this.Logger.Warn($"Client version does not match server version, got {clientVersion}, expecting {typeof(Program).Assembly.GetName().Version}, dropping client: {e.Client.Handle}");

				API.DropPlayer(e.Client.Handle.ToString(), "Please reconnect to get the latest NFive client version");

				return;
			}

			this.comms.Event(SessionEvents.ClientInitializing).ToServer().Emit(e.Client);

			var logs = new Tuple<LogLevel, LogLevel>(
				ServerLogConfiguration.Output.ClientConsole,
				ServerLogConfiguration.Output.ClientMirror
			);

			var locale = new Tuple<List<string>, string>(
				ServerConfiguration.Locale.Culture.Select(c => c.Name).ToList(),
				TZConvert.WindowsToIana(ServerConfiguration.Locale.TimeZone.Id, new RegionInfo(ServerConfiguration.Locale.Culture.First().Name).TwoLetterISORegionName)
			);

			e.Reply(e.User, logs, locale);
		}

		private async void OnInitialized(ICommunicationMessage e)
		{
			var session = this.sessions.Select(s => s.Value).Single(s => s.User.Id == e.User.Id);

			using (var context = new StorageContext())
			using (var transaction = await context.Database.BeginTransactionAsync())
			{
				session.Connected = DateTime.UtcNow;

				context.Sessions.Update(session);
				await context.SaveChangesAsync();
				await transaction.CommitAsync();
			}

			this.comms.Event(SessionEvents.ClientInitialized).ToServer().Emit(e.Client, session);
		}

		private async Task MonitorSession(Session session, IClient client)
		{
			while (session.IsConnected && this.threads.ContainsKey(session) && !this.threads[session].Item2.Token.IsCancellationRequested)
			{
				await BaseScript.Delay(100);

				if (API.GetPlayerLastMsg(client.Handle.ToString()) <= this.Configuration.ConnectionTimeout.TotalMilliseconds) continue;

				this.comms.Event(SessionEvents.SessionTimedOut).ToServer().Emit(client, session);

				session.Disconnected = DateTime.UtcNow;
				OnDisconnecting(client, "Session Timed Out");
			}

			while (DateTime.UtcNow.Subtract(session.Disconnected ?? DateTime.UtcNow) < this.Configuration.ReconnectGrace && this.threads.ContainsKey(session) && !this.threads[session].Item2.Token.IsCancellationRequested)
			{
				await BaseScript.Delay(100);
			}

			this.sessions.TryRemove(session.Id, out session);
		}
	}
}
