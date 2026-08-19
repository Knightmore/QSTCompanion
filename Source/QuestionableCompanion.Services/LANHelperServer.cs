using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class LANHelperServer : IDisposable
{
	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly IFramework framework;

	private readonly Configuration config;

	private readonly PartyInviteAutoAccept partyInviteAutoAccept;

	private readonly ICommandManager commandManager;

	private readonly Plugin plugin;

	private TcpListener? listener;

	private CancellationTokenSource? cancellationTokenSource;

	private Task? startTask;

	private Task? acceptTask;

	private Task? broadcastTask;

	private int disposed;

	private readonly List<TcpClient> connectedClients = new List<TcpClient>();

	private readonly Dictionary<string, TcpClient> activeConnections = new Dictionary<string, TcpClient>();

	private readonly Dictionary<string, DateTime> knownQuesters = new Dictionary<string, DateTime>();

	private bool isRunning;

	private string? cachedPlayerName;

	private ushort cachedWorldId;

	private DateTime lastCacheRefresh = DateTime.MinValue;

	private const int CACHE_REFRESH_SECONDS = 30;

	private RuntimeEventSubscription? loginSubscription;

	private RuntimeEventSubscription? logoutSubscription;

	public bool IsRunning => isRunning;

	public int ConnectedClientCount => connectedClients.Count;

	public event Action<string, ushort>? OnPartyInviteRequested;

	public bool HasConnectionFromIP(string ipAddress)
	{
		lock (connectedClients)
		{
			return connectedClients.Any(delegate(TcpClient client)
			{
				try
				{
					return client.Client?.RemoteEndPoint is IPEndPoint iPEndPoint && iPEndPoint.Address.ToString() == ipAddress && client.Connected;
				}
				catch
				{
					return false;
				}
			});
		}
	}

	public List<string> GetConnectedClientNames()
	{
		DateTime now = DateTime.Now;
		foreach (string item in (from kvp in knownQuesters
			where (now - kvp.Value).TotalSeconds > 60.0
			select kvp.Key).ToList())
		{
			knownQuesters.Remove(item);
		}
		return knownQuesters.Keys.ToList();
	}

	public LANHelperServer(IPluginLog log, IClientState clientState, IFramework framework, Configuration config, PartyInviteAutoAccept partyInviteAutoAccept, ICommandManager commandManager, Plugin plugin)
	{
		this.log = log;
		this.clientState = clientState;
		this.framework = framework;
		this.config = config;
		this.partyInviteAutoAccept = partyInviteAutoAccept;
		this.commandManager = commandManager;
		this.plugin = plugin;
		loginSubscription = RuntimeEventSubscription.Subscribe(clientState, "Login", OnLogin, log, "LANServer.Login");
		logoutSubscription = RuntimeEventSubscription.Subscribe(clientState, "Logout", delegate
		{
			OnLogout(0, 0);
		}, log, "LANServer.Logout");
	}

	private void OnLogin()
	{
		Task.Run(async delegate
		{
			await Task.Delay(1000);
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
					if (localPlayer != null)
					{
						cachedPlayerName = localPlayer.Name.ToString();
						cachedWorldId = (ushort)localPlayer.HomeWorld.RowId;
						log.Information($"[LANServer] Character logged in: {cachedPlayerName}@{cachedWorldId}");
						BroadcastHelperStatus();
					}
				}
				catch (Exception ex)
				{
					log.Error("[LANServer] OnLogin error: " + ex.Message);
				}
			});
		});
	}

	private void OnLogout(int type, int code)
	{
		if (!string.IsNullOrEmpty(cachedPlayerName))
		{
			log.Information($"[LANServer] Character logged out: {cachedPlayerName}@{cachedWorldId}");
			cachedPlayerName = null;
			cachedWorldId = 0;
			BroadcastHelperStatus();
		}
	}

	private void BroadcastHelperStatus()
	{
		lock (connectedClients)
		{
			LANHelperStatusResponse data = ((cachedPlayerName != null && config.IsHelperAutomationActive) ? new LANHelperStatusResponse
			{
				Name = cachedPlayerName,
				WorldId = cachedWorldId,
				Status = LANHelperStatus.Available,
				CurrentActivity = "Ready"
			} : new LANHelperStatusResponse
			{
				Name = (cachedPlayerName ?? "Unknown"),
				WorldId = cachedWorldId,
				Status = LANHelperStatus.Offline,
				CurrentActivity = ((cachedPlayerName == null) ? "Character not logged in" : (config.IsHighLevelHelper ? "Helper logic is inactive" : "Client is not configured as a Helper"))
			});
			LANMessage message = new LANMessage(LANMessageType.HELPER_STATUS, data);
			foreach (TcpClient item in connectedClients.ToList())
			{
				try
				{
					if (item.Connected)
					{
						SendMessage(item, message);
					}
				}
				catch (Exception ex)
				{
					log.Error("[LANServer] Failed to broadcast status: " + ex.Message);
				}
			}
			log.Debug($"[LANServer] Broadcasted HELPER_STATUS to {connectedClients.Count} clients");
		}
	}

	public void NotifyRoleChanged()
	{
		BroadcastHelperStatus();
	}

	public void Start()
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return;
		}
		if (isRunning)
		{
			log.Warning("[LANServer] Server already running");
			return;
		}
		startTask = Task.Run(async delegate
		{
			try
			{
				if (Volatile.Read(in disposed) == 0)
				{
					framework.Update += OnFrameworkUpdate;
					int retries = 5;
					while (retries > 0)
					{
						try
						{
							listener = new TcpListener(IPAddress.Any, config.LANServerPort);
							listener.Start();
						}
						catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
						{
							retries--;
							if (retries == 0)
							{
								throw;
							}
							log.Warning($"[LANServer] Port {config.LANServerPort} in use, retrying in 1s... ({retries} retries left)");
							await Task.Delay(1000);
							if (Volatile.Read(in disposed) != 0)
							{
								return;
							}
							continue;
						}
						break;
					}
					if (Volatile.Read(in disposed) != 0)
					{
						listener?.Stop();
						listener = null;
						framework.Update -= OnFrameworkUpdate;
					}
					else
					{
						cancellationTokenSource = new CancellationTokenSource();
						if (Volatile.Read(in disposed) != 0)
						{
							cancellationTokenSource.Cancel();
							listener?.Stop();
							listener = null;
							framework.Update -= OnFrameworkUpdate;
						}
						else
						{
							isRunning = true;
							log.Information("[LANServer] ===== LAN HELPER SERVER STARTED (v2-DEBUG) =====");
							log.Information($"[LANServer] Listening on port {config.LANServerPort}");
							log.Information("[LANServer] Waiting for player info cache... (via framework update)");
							acceptTask = AcceptClientsAsync(cancellationTokenSource.Token);
							broadcastTask = BroadcastPresenceAsync(cancellationTokenSource.Token);
						}
					}
				}
			}
			catch (Exception ex2)
			{
				log.Error("[LANServer] Failed to start server: " + ex2.Message);
				isRunning = false;
				framework.Update -= OnFrameworkUpdate;
			}
		});
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (isRunning)
		{
			DateTime now = DateTime.Now;
			if ((now - lastCacheRefresh).TotalSeconds >= 30.0)
			{
				log.Debug($"[LANServer] Framework.Update triggered cache refresh (last: {(now - lastCacheRefresh).TotalSeconds:F1}s ago)");
				RefreshPlayerCache();
			}
		}
	}

	private void RefreshPlayerCache()
	{
		try
		{
			log.Debug("[LANServer] RefreshPlayerCache called");
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer != null)
			{
				string text = localPlayer.Name.ToString();
				ushort num = (ushort)localPlayer.HomeWorld.RowId;
				if (cachedPlayerName != text || cachedWorldId != num)
				{
					if (cachedPlayerName == null)
					{
						log.Information($"[LANServer] âœ“ Player info cached: {text}@{num}");
					}
					else
					{
						log.Information($"[LANServer] Player info updated: {text}@{num}");
					}
					cachedPlayerName = text;
					cachedWorldId = num;
				}
				lastCacheRefresh = DateTime.Now;
			}
			else
			{
				log.Warning("[LANServer] RefreshPlayerCache: LocalPlayer is NULL!");
			}
			lastCacheRefresh = DateTime.Now;
		}
		catch (Exception ex)
		{
			log.Error("[LANServer] RefreshPlayerCache ERROR: " + ex.Message);
			log.Error("[LANServer] Stack: " + ex.StackTrace);
		}
	}

	private async Task BroadcastPresenceAsync(CancellationToken cancellationToken)
	{
		_ = 2;
		try
		{
			using UdpClient udpClient = new UdpClient();
			udpClient.EnableBroadcast = true;
			IPEndPoint broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, 47789);
			int startupAnnouncements = 0;
			while (!cancellationToken.IsCancellationRequested)
			{
				if (string.IsNullOrEmpty(cachedPlayerName) || cachedWorldId == 0)
				{
					await Task.Delay(500, cancellationToken);
					continue;
				}
				string s = JsonConvert.SerializeObject(new
				{
					Type = "HELPER_ANNOUNCE",
					Name = cachedPlayerName,
					WorldId = cachedWorldId,
					Port = config.LANServerPort
				});
				byte[] bytes = Encoding.UTF8.GetBytes(s);
				await udpClient.SendAsync(bytes, bytes.Length, broadcastEndpoint);
				startupAnnouncements++;
				if (startupAnnouncements <= 3)
				{
					log.Information($"[LANServer] Broadcast announcement sent ({startupAnnouncements}/3)");
				}
				else
				{
					log.Debug("[LANServer] Broadcast presence updated");
				}
				await Task.Delay((startupAnnouncements < 3) ? 500 : 30000, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			log.Error("[LANServer] UDP broadcast error: " + ex2.Message);
		}
	}

	public void Stop()
	{
		int num;
		if (!isRunning)
		{
			num = ((listener != null) ? 1 : 0);
			if (num == 0)
			{
				goto IL_002c;
			}
		}
		else
		{
			num = 1;
		}
		log.Information("[LANServer] Stopping server...");
		goto IL_002c;
		IL_002c:
		isRunning = false;
		cancellationTokenSource?.Cancel();
		framework.Update -= OnFrameworkUpdate;
		loginSubscription?.Dispose();
		logoutSubscription?.Dispose();
		if (num == 0)
		{
			return;
		}
		lock (connectedClients)
		{
			foreach (TcpClient item in connectedClients.ToList())
			{
				try
				{
					if (item.Connected)
					{
						try
						{
							NetworkStream stream = item.GetStream();
							if (stream.CanWrite)
							{
								string text = JsonConvert.SerializeObject(new LANMessage(LANMessageType.DISCONNECT));
								byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
								stream.Write(bytes, 0, bytes.Length);
							}
						}
						catch
						{
						}
					}
					item.Close();
					item.Dispose();
				}
				catch
				{
				}
			}
			connectedClients.Clear();
		}
		try
		{
			listener?.Stop();
		}
		catch (Exception ex)
		{
			log.Warning("[LANServer] Error stopping listener: " + ex.Message);
		}
		listener = null;
		log.Information("[LANServer] Server stopped");
	}

	private async Task AcceptClientsAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested && isRunning)
		{
			try
			{
				TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
				string text = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
				client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, optionValue: true);
				client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TypeOfService, 30);
				client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.BlockSource, 5);
				client.SendTimeout = 5000;
				client.ReceiveTimeout = 60000;
				log.Information("[LANServer] Client connected from " + text);
				lock (connectedClients)
				{
					connectedClients.Add(client);
				}
				Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				log.Error("[LANServer] Error accepting client: " + ex2.Message);
				await Task.Delay(1000, cancellationToken);
			}
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
		try
		{
			using NetworkStream stream = client.GetStream();
			stream.ReadTimeout = 60000;
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
			while (!cancellationToken.IsCancellationRequested && client.Connected)
			{
				string value;
				try
				{
					value = await reader.ReadLineAsync(cancellationToken);
				}
				catch (IOException ex) when (ex.InnerException is SocketException)
				{
					log.Debug("[LANServer] Socket error from " + clientIP + ": " + ex.Message);
					break;
				}
				catch (OperationCanceledException)
				{
					break;
				}
				if (string.IsNullOrEmpty(value))
				{
					break;
				}
				try
				{
					LANMessage lANMessage = JsonConvert.DeserializeObject<LANMessage>(value);
					if (lANMessage != null)
					{
						await HandleMessageAsync(client, lANMessage, clientIP);
					}
				}
				catch (JsonException ex3)
				{
					log.Warning("[LANServer] Invalid JSON from " + clientIP + ": " + ex3.Message);
				}
				catch (Exception ex4)
				{
					log.Error("[LANServer] Error handling message from " + clientIP + ": " + ex4.Message);
				}
			}
		}
		catch (OperationCanceledException)
		{
			log.Debug("[LANServer] Client " + clientIP + " cancelled");
		}
		catch (Exception ex6)
		{
			log.Warning($"[LANServer] Client {clientIP} error: {ex6.GetType().Name} - {ex6.Message}");
		}
		finally
		{
			log.Information("[LANServer] Client " + clientIP + " disconnected");
			lock (connectedClients)
			{
				connectedClients.Remove(client);
			}
			try
			{
				client.Close();
			}
			catch
			{
			}
			try
			{
				client.Dispose();
			}
			catch
			{
			}
		}
	}

	private async Task HandleMessageAsync(TcpClient client, LANMessage message, string clientIP)
	{
		log.Debug($"[LANServer] Received {message.Type} from {clientIP}");
		switch (message.Type)
		{
		case LANMessageType.REQUEST_HELPER:
			await HandleHelperRequest(client, message);
			break;
		case LANMessageType.HELPER_STATUS:
			await HandleStatusRequest(client);
			break;
		case LANMessageType.INVITE_NOTIFICATION:
			await HandleInviteNotification(client, message);
			break;
		case LANMessageType.FOLLOW_COMMAND:
			await HandleFollowCommand(client, message);
			break;
		case LANMessageType.CHAUFFEUR_PICKUP_REQUEST:
			await HandleChauffeurSummon(message);
			break;
		case LANMessageType.HEARTBEAT:
		{
			LANHeartbeat data2 = message.GetData<LANHeartbeat>();
			if (data2 != null && data2.ClientRole == "Quester" && !string.IsNullOrEmpty(data2.ClientName))
			{
				string key = $"{data2.ClientName}@{data2.ClientWorldId}";
				knownQuesters[key] = DateTime.Now;
			}
			SendMessage(client, new LANMessage(LANMessageType.HEARTBEAT));
			break;
		}
		case LANMessageType.REQUEST_PARTY_INVITE:
		{
			LANPartyInviteRequest data = message.GetData<LANPartyInviteRequest>();
			if (data == null)
			{
				break;
			}
			log.Information("[LANServer] Party Invite requested for " + data.QuesterName + "@" + WorldNameHelper.GetWorldName(data.QuesterWorldId));
			string reqName = data.QuesterName;
			ushort reqWorld = data.QuesterWorldId;
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					log.Debug("[LANServer] Invoking OnPartyInviteRequested on framework thread for " + reqName);
					this.OnPartyInviteRequested?.Invoke(reqName, reqWorld);
				}
				catch (Exception ex)
				{
					log.Error("[LANServer] Error in OnPartyInviteRequested: " + ex.Message);
				}
			});
			SendMessage(client, new LANMessage(LANMessageType.INVITE_ACCEPTED));
			break;
		}
		default:
			log.Debug($"[LANServer] Unhandled message type: {message.Type}");
			break;
		}
	}

	private async Task HandleHelperRequest(TcpClient client, LANMessage message)
	{
		LANHelperRequest request = message.GetData<LANHelperRequest>();
		if (request != null)
		{
			log.Information("[LANServer] Helper requested by " + request.QuesterName + " for duty: " + request.DutyName);
			await SendCurrentStatus(client);
			if (!config.IsHelperAutomationActive)
			{
				log.Information("[LANServer] Helper automation is inactive; request was reported as unavailable and ignored.");
				return;
			}
			partyInviteAutoAccept.EnableForQuester(request.QuesterName);
			log.Information("[LANServer] Auto-accept enabled for " + request.QuesterName);
		}
	}

	private async Task HandleStatusRequest(TcpClient client)
	{
		await SendCurrentStatus(client);
	}

	private async Task SendCurrentStatus(TcpClient client)
	{
		try
		{
			log.Debug("[LANServer] SendCurrentStatus: Start");
			if (cachedPlayerName == null || !config.IsHelperAutomationActive)
			{
				log.Information("[LANServer] SendCurrentStatus: Client is offline or not a Helper.");
				LANHelperStatusResponse data = new LANHelperStatusResponse
				{
					Name = (cachedPlayerName ?? "Unknown"),
					WorldId = cachedWorldId,
					Status = LANHelperStatus.Offline,
					CurrentActivity = ((cachedPlayerName == null) ? "Waiting for character login..." : (config.IsHighLevelHelper ? "Helper logic is inactive" : "Client is not configured as a Helper"))
				};
				SendMessage(client, new LANMessage(LANMessageType.HELPER_STATUS, data));
				return;
			}
			log.Debug($"[LANServer] SendCurrentStatus: Cached Name={cachedPlayerName}, World={cachedWorldId}");
			LANHelperStatusResponse data2 = new LANHelperStatusResponse
			{
				Name = cachedPlayerName,
				WorldId = cachedWorldId,
				Status = LANHelperStatus.Available,
				CurrentActivity = "Ready"
			};
			log.Debug("[LANServer] SendCurrentStatus: Status object created");
			LANMessage message = new LANMessage(LANMessageType.HELPER_STATUS, data2);
			log.Debug("[LANServer] SendCurrentStatus: LANMessage created");
			SendMessage(client, message);
			log.Debug("[LANServer] SendCurrentStatus: Message sent");
		}
		catch (Exception ex)
		{
			log.Error("[LANServer] SendCurrentStatus CRASH: " + ex.Message);
			log.Error("[LANServer] Stack: " + ex.StackTrace);
		}
	}

	private async Task HandleInviteNotification(TcpClient client, LANMessage message)
	{
		string data = message.GetData<string>();
		log.Information("[LANServer] Invite notification from " + data);
		SendMessage(client, new LANMessage(LANMessageType.INVITE_ACCEPTED));
	}

	private async Task HandleFollowCommand(TcpClient client, LANMessage message)
	{
		LANFollowCommand data = message.GetData<LANFollowCommand>();
		if (data != null)
		{
			ChauffeurModeService chauffeurMode = plugin.GetChauffeurMode();
			if (chauffeurMode == null)
			{
				log.Warning("[LANServer] No ChauffeurModeService available for position update");
				return;
			}
			if (chauffeurMode.IsTransportingQuester)
			{
				log.Debug("[LANServer] Ignoring FOLLOW_COMMAND - Chauffeur Mode is actively transporting");
				return;
			}
			string questerName = config.AssignedQuesterForFollowing ?? "LAN Quester";
			chauffeurMode.UpdateQuesterPositionFromLAN(data.X, data.Y, data.Z, data.TerritoryId, questerName);
			log.Debug($"[LANServer] Updated quester position: ({data.X:F2}, {data.Y:F2}, {data.Z:F2}) Zone={data.TerritoryId}");
			SendMessage(client, new LANMessage(LANMessageType.FOLLOW_STARTED));
		}
	}

	private void SendMessage(TcpClient client, LANMessage message)
	{
		try
		{
			if (client.Connected)
			{
				string text = JsonConvert.SerializeObject(message);
				byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
				client.GetStream().Write(bytes, 0, bytes.Length);
			}
		}
		catch (Exception ex)
		{
			log.Error("[LANServer] Failed to send message: " + ex.Message);
		}
	}

	public void BroadcastMessage(LANMessage message)
	{
		lock (connectedClients)
		{
			foreach (TcpClient item in connectedClients.ToList())
			{
				SendMessage(item, message);
			}
		}
	}

	private async Task HandleChauffeurSummon(LANMessage message)
	{
		LANChauffeurSummon summonData = message.GetData<LANChauffeurSummon>();
		if (summonData == null)
		{
			log.Error("[LANServer] HandleChauffeurSummon: Failed to deserialize summon data!");
			return;
		}
		log.Information("[LANServer] =========================================");
		log.Information("[LANServer] *** CHAUFFEUR PICKUP REQUEST RECEIVED ***");
		log.Information("[LANServer] =========================================");
		ExcelSheet<World> excelSheet = Plugin.DataManager?.GetExcelSheet<World>();
		string text = summonData.QuesterWorldId.ToString();
		if (excelSheet != null)
		{
			foreach (World item in excelSheet)
			{
				if (item.RowId == summonData.QuesterWorldId)
				{
					text = item.Name.ExtractText();
					break;
				}
			}
		}
		log.Information("[LANServer] Quester: " + summonData.QuesterName + "@" + text);
		log.Information($"[LANServer] Zone: {summonData.ZoneId}");
		log.Information($"[LANServer] Target: ({summonData.TargetX:F2}, {summonData.TargetY:F2}, {summonData.TargetZ:F2})");
		log.Information($"[LANServer] AttuneAetheryte: {summonData.IsAttuneAetheryte}");
		ChauffeurModeService chauffeur = plugin.GetChauffeurMode();
		if (chauffeur != null)
		{
			Vector3 targetPos = new Vector3(summonData.TargetX, summonData.TargetY, summonData.TargetZ);
			Vector3 questerPos = new Vector3(summonData.QuesterX, summonData.QuesterY, summonData.QuesterZ);
			log.Information("[LANServer] Calling ChauffeurModeService.StartHelperWorkflow...");
			try
			{
				await framework.RunOnFrameworkThread(delegate
				{
					try
					{
						log.Information("[LANServer] [FrameworkThread] Executing StartHelperWorkflow on main thread");
						chauffeur.StartHelperWorkflow(summonData.QuesterName, summonData.QuesterWorldId, summonData.QuesterCurrentWorldId, summonData.ZoneId, targetPos, questerPos, summonData.IsAttuneAetheryte, summonData.NearestAetheryteName);
						log.Information("[LANServer] [FrameworkThread] StartHelperWorkflow completed successfully");
					}
					catch (Exception ex2)
					{
						log.Error("[LANServer] [FrameworkThread] StartHelperWorkflow threw exception: " + ex2.Message);
						log.Error("[LANServer] [FrameworkThread] Stack: " + ex2.StackTrace);
					}
				});
				log.Information("[LANServer] StartHelperWorkflow completed on the framework thread");
				return;
			}
			catch (Exception ex)
			{
				log.Error("[LANServer] Failed to execute StartHelperWorkflow: " + ex.Message);
				log.Error("[LANServer] Stack trace: " + ex.StackTrace);
				return;
			}
		}
		log.Error("[LANServer] ChauffeurModeService is null! Cannot start helper workflow.");
	}

	public void SendChauffeurMountReady(string questerName, ushort questerWorldId)
	{
		LANChauffeurResponse data = new LANChauffeurResponse
		{
			QuesterName = questerName,
			QuesterWorldId = questerWorldId
		};
		LANMessage message = new LANMessage(LANMessageType.CHAUFFEUR_HELPER_READY_FOR_MOUNT, data);
		log.Information("[LANServer] Sending Chauffeur Mount Ready to connected clients for " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorldId));
		lock (connectedClients)
		{
			foreach (TcpClient item in connectedClients.ToList())
			{
				if (item.Connected)
				{
					SendMessage(item, message);
				}
			}
		}
	}

	public void SendChauffeurReadyForPickup(string questerName, ushort questerWorldId)
	{
		LANChauffeurResponse data = new LANChauffeurResponse
		{
			QuesterName = questerName,
			QuesterWorldId = questerWorldId
		};
		LANMessage message = new LANMessage(LANMessageType.CHAUFFEUR_READY_FOR_PICKUP, data);
		log.Information("[LANServer] Sending Chauffeur Ready For Pickup to connected clients for " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorldId));
		lock (connectedClients)
		{
			foreach (TcpClient item in connectedClients.ToList())
			{
				if (item.Connected)
				{
					SendMessage(item, message);
				}
			}
		}
	}

	public void SendChauffeurArrived(string questerName, ushort questerWorldId)
	{
		LANChauffeurResponse data = new LANChauffeurResponse
		{
			QuesterName = questerName,
			QuesterWorldId = questerWorldId
		};
		LANMessage message = new LANMessage(LANMessageType.CHAUFFEUR_HELPER_ARRIVED_DEST, data);
		log.Information("[LANServer] Sending Chauffeur Arrived to connected clients for " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorldId));
		lock (connectedClients)
		{
			foreach (TcpClient item in connectedClients.ToList())
			{
				if (item.Connected)
				{
					SendMessage(item, message);
				}
			}
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) == 0)
		{
			Stop();
			ObserveShutdownAsync(startTask);
		}
	}

	private async Task ObserveShutdownAsync(Task? startup)
	{
		_ = 1;
		try
		{
			if (startup != null)
			{
				await startup.ConfigureAwait(continueOnCapturedContext: false);
			}
			Task[] array = new Task[2] { acceptTask, broadcastTask }.Where((Task task) => task != null).Cast<Task>().ToArray();
			if (array.Length != 0)
			{
				await Task.WhenAll(array).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			log.Debug(exception, "[LANServer] Background work ended with an error during shutdown.");
		}
		finally
		{
			CancellationTokenSource? obj = cancellationTokenSource;
			cancellationTokenSource = null;
			obj?.Dispose();
			startTask = null;
			acceptTask = null;
			broadcastTask = null;
		}
	}

	public void SendChauffeurAborted(string questerName, ushort questerWorld)
	{
		LANChauffeurResponse data = new LANChauffeurResponse
		{
			QuesterName = (cachedPlayerName ?? "Unknown"),
			QuesterWorldId = cachedWorldId
		};
		LANMessage message = new LANMessage(LANMessageType.CHAUFFEUR_ABORTED, data);
		BroadcastMessage(message);
		log.Information("[LANServer] Sent CHAUFFEUR_ABORTED notification");
	}
}
