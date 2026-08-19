using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class LANHelperClient : IDisposable
{
	public class ChauffeurMessageEventArgs : EventArgs
	{
		public LANMessageType Type { get; }

		public LANChauffeurResponse Data { get; }

		public ChauffeurMessageEventArgs(LANMessageType type, LANChauffeurResponse data)
		{
			Type = type;
			Data = data;
		}
	}

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly IFramework framework;

	private readonly Configuration config;

	private LANHelperServer? lanHelperServer;

	private readonly Dictionary<string, TcpClient> activeConnections = new Dictionary<string, TcpClient>();

	private readonly Dictionary<string, LANHelperInfo> discoveredHelpers = new Dictionary<string, LANHelperInfo>();

	private readonly Dictionary<string, DateTime> lastReconnectAttempt = new Dictionary<string, DateTime>();

	private readonly Dictionary<string, int> reconnectFailCount = new Dictionary<string, int>();

	private readonly List<Task> listenerTasks = new List<Task>();

	private CancellationTokenSource? cancellationTokenSource;

	private Task? heartbeatTask;

	private int disposed;

	private const int RECONNECT_DELAY_MS = 5000;

	private const int MAX_RECONNECT_FAIL_COUNT = 3;

	private string cachedPlayerName = string.Empty;

	private ushort cachedWorldId;

	private RuntimeEventSubscription? loginSubscription;

	private RuntimeEventSubscription? logoutSubscription;

	private string RolePrefix
	{
		get
		{
			if (!config.IsQuester)
			{
				return "[Helper]";
			}
			return "[Quester]";
		}
	}

	public IReadOnlyList<LANHelperInfo> DiscoveredHelpers => discoveredHelpers.Values.ToList();

	public event EventHandler<ChauffeurMessageEventArgs>? OnChauffeurMessageReceived;

	public void SetLANHelperServer(LANHelperServer server)
	{
		lanHelperServer = server;
		log.Debug(RolePrefix + " [LANClient] LANHelperServer reference set");
	}

	public LANHelperClient(IPluginLog log, IClientState clientState, IFramework framework, Configuration config)
	{
		this.log = log;
		this.clientState = clientState;
		this.framework = framework;
		this.config = config;
		loginSubscription = RuntimeEventSubscription.Subscribe(clientState, "Login", OnLogin, log, "LANClient.Login");
		logoutSubscription = RuntimeEventSubscription.Subscribe(clientState, "Logout", delegate
		{
			OnLogout(0, 0);
		}, log, "LANClient.Logout");
		InitializePlayerCache();
	}

	private void InitializePlayerCache()
	{
		try
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer != null)
			{
				cachedPlayerName = localPlayer.Name.ToString();
				cachedWorldId = (ushort)localPlayer.HomeWorld.RowId;
				log.Information($"{RolePrefix} [LANClient] Player cache initialized: {cachedPlayerName}@{cachedWorldId}");
			}
			else
			{
				log.Debug(RolePrefix + " [LANClient] No player logged in yet, cache will be set on login");
			}
		}
		catch (Exception ex)
		{
			log.Warning(RolePrefix + " [LANClient] Failed to initialize player cache: " + ex.Message);
		}
	}

	private void OnLogin()
	{
		Task.Run(async delegate
		{
			await Task.Delay(1000);
			framework.RunOnFrameworkThread(delegate
			{
				IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
				if (localPlayer != null)
				{
					string text = localPlayer.Name.ToString();
					ushort num = (ushort)localPlayer.HomeWorld.RowId;
					if (cachedPlayerName != text || cachedWorldId != num)
					{
						if (!string.IsNullOrEmpty(cachedPlayerName))
						{
							log.Information($"{RolePrefix} [LANClient] Character switch detected: {cachedPlayerName}@{cachedWorldId} -> {text}@{num}");
						}
						else
						{
							log.Information($"{RolePrefix} [LANClient] Character logged in: {text}@{num}");
						}
						cachedPlayerName = text;
						cachedWorldId = num;
					}
				}
			});
		});
	}

	private void OnLogout(int type, int code)
	{
		if (!string.IsNullOrEmpty(cachedPlayerName))
		{
			log.Information($"{RolePrefix} [LANClient] Player logged out: {cachedPlayerName}@{cachedWorldId}");
			cachedPlayerName = string.Empty;
			cachedWorldId = 0;
		}
	}

	public async Task Initialize()
	{
		if (!config.EnableLANHelpers || Volatile.Read(in disposed) != 0)
		{
			return;
		}
		CancellationTokenSource source = new CancellationTokenSource();
		cancellationTokenSource = source;
		if (Volatile.Read(in disposed) != 0)
		{
			source.Cancel();
			source.Dispose();
			cancellationTokenSource = null;
			return;
		}
		log.Information(RolePrefix + " [LANClient] Initializing LAN Helper Client...");
		foreach (string lANHelperIP in config.LANHelperIPs)
		{
			if (Volatile.Read(in disposed) != 0)
			{
				return;
			}
			await ConnectToHelperAsync(lANHelperIP);
		}
		if (Volatile.Read(in disposed) == 0)
		{
			heartbeatTask = Task.Run(() => HeartbeatMonitorAsync(source.Token));
		}
	}

	private async Task HeartbeatMonitorAsync(CancellationToken cancellationToken)
	{
		log.Information(RolePrefix + " [LANClient] Heartbeat monitor started (30s interval)");
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(30000, cancellationToken);
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
						if (localPlayer != null)
						{
							string text = localPlayer.Name.ToString();
							ushort num = (ushort)localPlayer.HomeWorld.RowId;
							if (string.IsNullOrEmpty(cachedPlayerName) || cachedWorldId == 0)
							{
								if (!string.IsNullOrEmpty(text) && num > 0)
								{
									cachedPlayerName = text;
									cachedWorldId = num;
									log.Information($"{RolePrefix} [LANClient] Cache initialized from heartbeat: {cachedPlayerName}@{cachedWorldId}");
								}
							}
							else if (cachedPlayerName != text || cachedWorldId != num)
							{
								log.Warning($"{RolePrefix} [LANClient] Character switch detected in heartbeat (Login missed!): {cachedPlayerName}@{cachedWorldId} -> {text}@{num}");
								cachedPlayerName = text;
								cachedWorldId = num;
							}
						}
					}
					catch (Exception ex3)
					{
						log.Error(RolePrefix + " [LANClient] Error validating cache: " + ex3.Message);
					}
				});
				foreach (string ip in config.LANHelperIPs.ToList())
				{
					bool flag = false;
					lock (activeConnections)
					{
						flag = !activeConnections.ContainsKey(ip) || !activeConnections[ip].Connected;
					}
					if (flag)
					{
						if (lanHelperServer != null && lanHelperServer.HasConnectionFromIP(ip))
						{
							log.Debug(RolePrefix + " [LANClient] Skipping outgoing connect to " + ip + " - LANServer already has incoming connection");
							continue;
						}
						log.Debug(RolePrefix + " [LANClient] Heartbeat: " + ip + " disconnected, reconnecting...");
						await ConnectToHelperAsync(ip);
						continue;
					}
					if (string.IsNullOrEmpty(cachedPlayerName) || cachedWorldId == 0)
					{
						log.Debug(RolePrefix + " [LANClient] Skipping heartbeat to " + ip + " - player info not cached yet");
						continue;
					}
					LANHeartbeat heartbeatData = new LANHeartbeat
					{
						ClientName = cachedPlayerName,
						ClientWorldId = cachedWorldId,
						ClientRole = (config.IsQuester ? "Quester" : "Helper")
					};
					await SendMessageAsync(ip, new LANMessage(LANMessageType.HEARTBEAT, heartbeatData));
					log.Debug($"{RolePrefix} [LANClient] Heartbeat sent to {ip} (as {heartbeatData.ClientName}@{heartbeatData.ClientWorldId}, Role={heartbeatData.ClientRole})");
				}
				foreach (LANHelperInfo helper in discoveredHelpers.Values.ToList())
				{
					if (!string.IsNullOrEmpty(helper.IPAddress))
					{
						if (string.IsNullOrEmpty(cachedPlayerName) || cachedWorldId == 0)
						{
							log.Debug(RolePrefix + " [LANClient] Skipping heartbeat to " + helper.IPAddress + " - player info not cached yet");
							continue;
						}
						LANHeartbeat heartbeatData = new LANHeartbeat
						{
							ClientName = cachedPlayerName,
							ClientWorldId = cachedWorldId,
							ClientRole = (config.IsQuester ? "Quester" : "Helper")
						};
						await SendMessageAsync(helper.IPAddress, new LANMessage(LANMessageType.HEARTBEAT, heartbeatData));
						string value = (config.IsQuester ? "helper" : "quester");
						log.Information($"{RolePrefix} [LANClient] Heartbeat sent to discovered {value} {helper.Name}@{helper.IPAddress} (as {heartbeatData.ClientName}@{heartbeatData.ClientWorldId}, Role={heartbeatData.ClientRole})");
					}
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				log.Error(RolePrefix + " [LANClient] Heartbeat monitor error: " + ex2.Message);
			}
		}
		log.Information(RolePrefix + " [LANClient] Heartbeat monitor stopped");
	}

	public async Task<bool> ConnectToHelperAsync(string ipAddress)
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return false;
		}
		lock (activeConnections)
		{
			if (activeConnections.ContainsKey(ipAddress))
			{
				log.Debug(RolePrefix + " [LANClient] Already connected to " + ipAddress);
				return true;
			}
		}
		try
		{
			if (lastReconnectAttempt.TryGetValue(ipAddress, out var value))
			{
				int valueOrDefault = reconnectFailCount.GetValueOrDefault(ipAddress, 0);
				int num = ((valueOrDefault >= 3) ? 30000 : 5000);
				if ((DateTime.Now - value).TotalMilliseconds < (double)num)
				{
					log.Debug($"{RolePrefix} [LANClient] Skipping reconnect to {ipAddress} - too soon (fail count: {valueOrDefault})");
					return false;
				}
			}
			lastReconnectAttempt[ipAddress] = DateTime.Now;
			log.Information($"{RolePrefix} [LANClient] Connecting to helper at {ipAddress}:{config.LANServerPort}...");
			TcpClient client = new TcpClient();
			client.SendTimeout = 5000;
			client.ReceiveTimeout = 30000;
			client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, optionValue: true);
			client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TypeOfService, 30);
			client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.BlockSource, 5);
			Task connectTask = client.ConnectAsync(ipAddress, config.LANServerPort);
			if (await Task.WhenAny(connectTask, Task.Delay(10000)) != connectTask)
			{
				client.Close();
				throw new TimeoutException("Connection timeout");
			}
			await connectTask;
			lock (activeConnections)
			{
				if (Volatile.Read(in disposed) != 0)
				{
					client.Dispose();
					return false;
				}
				activeConnections[ipAddress] = client;
			}
			reconnectFailCount[ipAddress] = 0;
			log.Information(RolePrefix + " [LANClient] âœ“ Connected to " + ipAddress);
			LANHelperStatusResponse lANHelperStatusResponse = await RequestHelperStatusAsync(ipAddress);
			if (lANHelperStatusResponse != null)
			{
				discoveredHelpers[ipAddress] = new LANHelperInfo
				{
					Name = lANHelperStatusResponse.Name,
					WorldId = lANHelperStatusResponse.WorldId,
					IPAddress = ipAddress,
					Status = lANHelperStatusResponse.Status,
					LastSeen = DateTime.Now
				};
				log.Information($"{RolePrefix} [LANClient] Helper discovered: {lANHelperStatusResponse.Name} ({lANHelperStatusResponse.Status})");
			}
			TrackListenerTask(Task.Run(() => ListenToHelperAsync(ipAddress, client), cancellationTokenSource.Token));
			return true;
		}
		catch (Exception ex)
		{
			reconnectFailCount[ipAddress] = reconnectFailCount.GetValueOrDefault(ipAddress, 0) + 1;
			int num2 = reconnectFailCount[ipAddress];
			if (num2 >= 3)
			{
				log.Warning($"{RolePrefix} [LANClient] Failed to connect to {ipAddress} ({num2} attempts): {ex.Message} - will retry in 30s");
			}
			else
			{
				log.Error($"{RolePrefix} [LANClient] Failed to connect to {ipAddress}: {ex.Message}");
			}
			return false;
		}
	}

	private async Task ListenToHelperAsync(string ipAddress, TcpClient client)
	{
		try
		{
			using NetworkStream stream = client.GetStream();
			stream.ReadTimeout = 60000;
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
			while (client.Connected && !cancellationTokenSource.Token.IsCancellationRequested)
			{
				string value;
				try
				{
					value = await reader.ReadLineAsync(cancellationTokenSource.Token);
				}
				catch (IOException ex) when (ex.InnerException is SocketException)
				{
					log.Warning($"{RolePrefix} [LANClient] Socket error reading from {ipAddress}: {ex.Message}");
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
						HandleHelperMessage(ipAddress, lANMessage);
					}
				}
				catch (JsonException ex3)
				{
					log.Error($"{RolePrefix} [LANClient] Invalid message from {ipAddress}: {ex3.Message}");
				}
			}
		}
		catch (OperationCanceledException)
		{
			log.Debug(RolePrefix + " [LANClient] Connection to " + ipAddress + " cancelled");
		}
		catch (Exception ex5)
		{
			log.Warning($"{RolePrefix} [LANClient] Connection to {ipAddress} lost: {ex5.GetType().Name} - {ex5.Message}");
		}
		finally
		{
			log.Information(RolePrefix + " [LANClient] Disconnected from " + ipAddress);
			lock (activeConnections)
			{
				activeConnections.Remove(ipAddress);
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

	private void HandleHelperMessage(string ipAddress, LANMessage message)
	{
		log.Debug($"{RolePrefix} [LANClient] Received {message.Type} from {ipAddress}");
		switch (message.Type)
		{
		case LANMessageType.HELPER_STATUS:
		{
			LANHelperStatusResponse data2 = message.GetData<LANHelperStatusResponse>();
			if (data2 == null)
			{
				break;
			}
			if (!discoveredHelpers.ContainsKey(ipAddress))
			{
				string value = (config.IsQuester ? "Helper" : "Quester");
				log.Information($"{RolePrefix} [LANClient] New {value} discovered via status: {data2.Name}@{WorldNameHelper.GetWorldName(data2.WorldId)} ({ipAddress})");
				discoveredHelpers[ipAddress] = new LANHelperInfo
				{
					Name = data2.Name,
					WorldId = data2.WorldId,
					IPAddress = ipAddress,
					Status = data2.Status,
					LastSeen = DateTime.Now
				};
			}
			else
			{
				discoveredHelpers[ipAddress].Status = data2.Status;
				discoveredHelpers[ipAddress].LastSeen = DateTime.Now;
				if (discoveredHelpers[ipAddress].Name != data2.Name)
				{
					discoveredHelpers[ipAddress].Name = data2.Name;
					discoveredHelpers[ipAddress].WorldId = data2.WorldId;
				}
			}
			break;
		}
		case LANMessageType.INVITE_ACCEPTED:
			log.Information(RolePrefix + " [LANClient] âœ“ Helper at " + ipAddress + " accepted invite");
			break;
		case LANMessageType.FOLLOW_STARTED:
			log.Information(RolePrefix + " [LANClient] âœ“ Helper at " + ipAddress + " started following");
			break;
		case LANMessageType.FOLLOW_ARRIVED:
			log.Information(RolePrefix + " [LANClient] âœ“ Helper at " + ipAddress + " arrived at destination");
			break;
		case LANMessageType.HELPER_READY:
			log.Information(RolePrefix + " [LANClient] âœ“ Helper at " + ipAddress + " is ready");
			break;
		case LANMessageType.HELPER_IN_PARTY:
			log.Information(RolePrefix + " [LANClient] âœ“ Helper at " + ipAddress + " joined party");
			break;
		case LANMessageType.HELPER_IN_DUTY:
			log.Information(RolePrefix + " [LANClient] âœ“ Helper at " + ipAddress + " entered duty");
			break;
		case LANMessageType.CHAUFFEUR_READY_FOR_PICKUP:
		{
			LANChauffeurResponse data5 = message.GetData<LANChauffeurResponse>();
			if (data5 != null)
			{
				log.Information("[LANClient] Received CHAUFFEUR_READY_FOR_PICKUP from " + data5.QuesterName + "@" + WorldNameHelper.GetWorldName(data5.QuesterWorldId));
				this.OnChauffeurMessageReceived?.Invoke(this, new ChauffeurMessageEventArgs(LANMessageType.CHAUFFEUR_READY_FOR_PICKUP, data5));
			}
			break;
		}
		case LANMessageType.CHAUFFEUR_HELPER_READY_FOR_MOUNT:
		{
			LANChauffeurResponse data4 = message.GetData<LANChauffeurResponse>();
			if (data4 != null)
			{
				log.Information("[LANClient] Received Chauffeur Mount Ready from " + data4.QuesterName + "@" + WorldNameHelper.GetWorldName(data4.QuesterWorldId));
				this.OnChauffeurMessageReceived?.Invoke(this, new ChauffeurMessageEventArgs(LANMessageType.CHAUFFEUR_HELPER_READY_FOR_MOUNT, data4));
			}
			break;
		}
		case LANMessageType.CHAUFFEUR_HELPER_ARRIVED_DEST:
		{
			LANChauffeurResponse data3 = message.GetData<LANChauffeurResponse>();
			if (data3 != null)
			{
				log.Information("[LANClient] Received Chauffeur Arrived from " + data3.QuesterName + "@" + WorldNameHelper.GetWorldName(data3.QuesterWorldId));
				this.OnChauffeurMessageReceived?.Invoke(this, new ChauffeurMessageEventArgs(LANMessageType.CHAUFFEUR_HELPER_ARRIVED_DEST, data3));
			}
			break;
		}
		case LANMessageType.CHAUFFEUR_ABORTED:
		{
			LANChauffeurResponse data = message.GetData<LANChauffeurResponse>();
			if (data != null)
			{
				log.Warning("[LANClient] Received Chauffeur ABORTED from Helper " + data.QuesterName + "@" + WorldNameHelper.GetWorldName(data.QuesterWorldId));
				this.OnChauffeurMessageReceived?.Invoke(this, new ChauffeurMessageEventArgs(LANMessageType.CHAUFFEUR_ABORTED, data));
			}
			break;
		}
		case LANMessageType.INVITE_NOTIFICATION:
		case LANMessageType.REQUEST_PARTY_INVITE:
		case LANMessageType.DUTY_COMPLETE:
		case LANMessageType.FOLLOW_COMMAND:
		case LANMessageType.CHAUFFEUR_PICKUP_REQUEST:
			break;
		}
	}

	public async Task<bool> RequestPartyInviteAsync(string ipAddress)
	{
		if (string.IsNullOrEmpty(cachedPlayerName) || cachedWorldId == 0)
		{
			log.Warning(RolePrefix + " [LANClient] RequestPartyInviteAsync: Player info not cached yet");
			return false;
		}
		LANPartyInviteRequest lANPartyInviteRequest = new LANPartyInviteRequest
		{
			QuesterName = cachedPlayerName,
			QuesterWorldId = cachedWorldId
		};
		log.Information($"{RolePrefix} [LANClient] Requesting Party Invite from {ipAddress} for {lANPartyInviteRequest.QuesterName}");
		return await SendMessageAsync(ipAddress, new LANMessage(LANMessageType.REQUEST_PARTY_INVITE, lANPartyInviteRequest));
	}

	public async Task<bool> RequestHelperAsync(string ipAddress, string dutyName = "")
	{
		if (string.IsNullOrEmpty(cachedPlayerName) || cachedWorldId == 0)
		{
			log.Warning(RolePrefix + " [LANClient] RequestHelperAsync: Player info not cached yet");
			return false;
		}
		LANHelperRequest data = new LANHelperRequest
		{
			QuesterName = cachedPlayerName,
			QuesterWorldId = cachedWorldId,
			DutyName = dutyName
		};
		return await SendMessageAsync(ipAddress, new LANMessage(LANMessageType.REQUEST_HELPER, data));
	}

	public async Task<LANHelperStatusResponse?> RequestHelperStatusAsync(string ipAddress)
	{
		if (!(await SendMessageAsync(ipAddress, new LANMessage(LANMessageType.HELPER_STATUS))))
		{
			return null;
		}
		await Task.Delay(500);
		if (discoveredHelpers.TryGetValue(ipAddress, out LANHelperInfo value))
		{
			return new LANHelperStatusResponse
			{
				Name = value.Name,
				WorldId = value.WorldId,
				Status = value.Status
			};
		}
		return null;
	}

	public async Task<bool> SendFollowCommandAsync(string ipAddress, float x, float y, float z, uint territoryId)
	{
		LANFollowCommand data = new LANFollowCommand
		{
			X = x,
			Y = y,
			Z = z,
			TerritoryId = territoryId
		};
		log.Information($"[LANClient] Sending follow command to {ipAddress}: ({x:F2}, {y:F2}, {z:F2})");
		return await SendMessageAsync(ipAddress, new LANMessage(LANMessageType.FOLLOW_COMMAND, data));
	}

	public async Task<bool> NotifyInviteSentAsync(string ipAddress, string helperName)
	{
		log.Information("[LANClient] Notifying " + ipAddress + " of invite sent to " + helperName);
		return await SendMessageAsync(ipAddress, new LANMessage(LANMessageType.INVITE_NOTIFICATION, helperName));
	}

	private async Task<bool> SendMessageAsync(string ipAddress, LANMessage message)
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return false;
		}
		try
		{
			TcpClient value;
			lock (activeConnections)
			{
				activeConnections.TryGetValue(ipAddress, out value);
			}
			if (value == null)
			{
				if (!(await ConnectToHelperAsync(ipAddress)))
				{
					return false;
				}
				lock (activeConnections)
				{
					activeConnections.TryGetValue(ipAddress, out value);
				}
			}
			if (value == null)
			{
				return false;
			}
			if (!value.Connected)
			{
				log.Warning("[LANClient] Not connected to " + ipAddress + ", reconnecting...");
				lock (activeConnections)
				{
					activeConnections.Remove(ipAddress);
				}
				if (!(await ConnectToHelperAsync(ipAddress)))
				{
					return false;
				}
				lock (activeConnections)
				{
					activeConnections.TryGetValue(ipAddress, out value);
				}
				if (value == null)
				{
					return false;
				}
			}
			string text = JsonConvert.SerializeObject(message);
			byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
			await value.GetStream().WriteAsync(bytes, 0, bytes.Length);
			log.Debug($"[LANClient] Sent {message.Type} to {ipAddress}");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[LANClient] Failed to send message to " + ipAddress + ": " + ex.Message);
			return false;
		}
	}

	public async Task<int> ScanNetworkAsync(int timeoutSeconds = 5)
	{
		log.Information($"[LANClient] ðŸ“¡ Scanning network for helpers (timeout: {timeoutSeconds}s)...");
		int foundCount = 0;
		try
		{
			using UdpClient udpClient = new UdpClient(47789);
			udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
			CancellationTokenSource cancellation = new CancellationTokenSource();
			cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
			while (!cancellation.Token.IsCancellationRequested)
			{
				try
				{
					UdpReceiveResult udpReceiveResult = await udpClient.ReceiveAsync(cancellation.Token);
					dynamic val = JsonConvert.DeserializeObject<object>(Encoding.UTF8.GetString(udpReceiveResult.Buffer));
					if (val?.Type == "HELPER_ANNOUNCE")
					{
						string text = udpReceiveResult.RemoteEndPoint.Address.ToString();
						string value = (string)val.Name;
						_ = (int)val.Port;
						log.Information($"{RolePrefix} [LANClient] Found helper: {value} at {text}");
						if (!config.LANHelperIPs.Contains(text))
						{
							config.LANHelperIPs.Add(text);
							config.Save();
							log.Information(RolePrefix + " [LANClient] Added " + text + " to configuration");
							log.Information("[LANClient] â†’ Added " + text + " to configuration");
							foundCount++;
						}
						await ConnectToHelperAsync(text);
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex2)
				{
					log.Debug("[LANClient] Scan error: " + ex2.Message);
				}
			}
		}
		catch (Exception ex3)
		{
			log.Error("[LANClient] Network scan failed: " + ex3.Message);
		}
		if (foundCount > 0)
		{
			log.Information($"[LANClient] âœ“ Scan complete: Found {foundCount} new helper(s)");
		}
		else
		{
			log.Information("[LANClient] Scan complete: No new helpers found");
		}
		return foundCount;
	}

	public LANHelperInfo? GetFirstAvailableHelper()
	{
		return (from h in discoveredHelpers.Values
			where h.Status == LANHelperStatus.Available
			orderby h.LastSeen
			select h).FirstOrDefault();
	}

	public async Task<bool> SendChauffeurSummonAsync(string ipAddress, LANChauffeurSummon summonData)
	{
		log.Information("[LANClient] *** SENDING CHAUFFEUR_PICKUP_REQUEST to " + ipAddress + " ***");
		log.Information($"[LANClient] Summon data: Quester={summonData.QuesterName}@{WorldNameHelper.GetWorldName(summonData.QuesterWorldId)}, Zone={summonData.ZoneId}");
		LANMessage message = new LANMessage(LANMessageType.CHAUFFEUR_PICKUP_REQUEST, summonData);
		bool num = await SendMessageAsync(ipAddress, message);
		if (num)
		{
			log.Information("[LANClient] âœ“ CHAUFFEUR_PICKUP_REQUEST sent successfully to " + ipAddress);
		}
		else
		{
			log.Error("[LANClient] âœ— FAILED to send CHAUFFEUR_PICKUP_REQUEST to " + ipAddress);
		}
		return num;
	}

	public async Task<bool> SendChauffeurPassengerMountedAsync(string ipAddress, string questerName, ushort questerWorld)
	{
		log.Information("[LANClient] Sending CHAUFFEUR_PASSENGER_MOUNTED to " + ipAddress);
		LANChauffeurResponse data = new LANChauffeurResponse
		{
			QuesterName = questerName,
			QuesterWorldId = questerWorld
		};
		LANMessage message = new LANMessage(LANMessageType.CHAUFFEUR_PASSENGER_MOUNTED, data);
		return await SendMessageAsync(ipAddress, message);
	}

	public void DisconnectAll()
	{
		log.Information("[LANClient] Disconnecting from all helpers...");
		List<TcpClient> list;
		lock (activeConnections)
		{
			list = activeConnections.Values.ToList();
			activeConnections.Clear();
		}
		foreach (TcpClient item in list)
		{
			try
			{
				item.Close();
				item.Dispose();
			}
			catch
			{
			}
		}
		lock (discoveredHelpers)
		{
			discoveredHelpers.Clear();
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) == 0)
		{
			CancellationTokenSource cancellationTokenSource = this.cancellationTokenSource;
			cancellationTokenSource?.Cancel();
			DisconnectAll();
			loginSubscription?.Dispose();
			logoutSubscription?.Dispose();
			loginSubscription = null;
			logoutSubscription = null;
			List<Task> list;
			lock (listenerTasks)
			{
				list = listenerTasks.ToList();
			}
			if (heartbeatTask != null)
			{
				list.Add(heartbeatTask);
			}
			ObserveShutdownAsync(list, cancellationTokenSource);
		}
	}

	private void TrackListenerTask(Task task)
	{
		lock (listenerTasks)
		{
			listenerTasks.Add(task);
		}
		task.ContinueWith(delegate(Task completed)
		{
			lock (listenerTasks)
			{
				listenerTasks.Remove(completed);
			}
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	private async Task ObserveShutdownAsync(IReadOnlyCollection<Task> tasks, CancellationTokenSource? source)
	{
		try
		{
			if (tasks.Count > 0)
			{
				await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			log.Debug(exception, "[LANClient] Background work ended with an error during shutdown.");
		}
		finally
		{
			source?.Dispose();
			cancellationTokenSource = null;
			heartbeatTask = null;
		}
	}
}
