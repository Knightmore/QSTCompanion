using System;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Helpers;

namespace QuestionableCompanion.Services;

public class CrossProcessIPC : IDisposable
{
	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly Configuration configuration;

	private MemoryMappedFile? mmf;

	private Thread? listenerThread;

	private volatile bool isRunning;

	private int disposed;

	private const string MMF_NAME = "QSTCompanion_IPC";

	private const int MMF_SIZE = 4096;

	private const int POLLING_INTERVAL_MS = 10;

	public event Action<string, ushort>? OnHelperAvailable;

	public event Action<string, ushort>? OnHelperRequested;

	public event Action? OnHelperDismissed;

	public event Action<string>? OnChatMessageReceived;

	public event Action<string>? OnCommandReceived;

	public event Action<string, ushort>? OnHelperInParty;

	public event Action<string, ushort>? OnHelperInDuty;

	public event Action<string, ushort>? OnHelperReady;

	public event Action? OnRequestHelperAnnouncements;

	public event Action<string, ushort, string, ushort>? OnPartyInviteRequested;

	public event Action<string, ushort, ushort, uint, Vector3, Vector3, bool, string?>? OnChauffeurSummonRequest;

	public event Action<string, ushort>? OnChauffeurReadyForPickup;

	public event Action<string, ushort>? OnChauffeurArrived;

	public event Action<string, ushort, uint, string>? OnChauffeurZoneUpdate;

	public event Action<string, ushort>? OnChauffeurMountReady;

	public event Action<string, ushort>? OnChauffeurPassengerMounted;

	public event Action<string, ushort>? OnChauffeurAborted;

	public event Action<string, ushort, string>? OnHelperStatusUpdate;

	public event Action<string, ushort, uint, Vector3>? OnQuesterPositionUpdate;

	public CrossProcessIPC(IPluginLog log, IFramework framework, Configuration configuration)
	{
		this.log = log;
		this.framework = framework;
		this.configuration = configuration;
		InitializeIPC();
	}

	private void InitializeIPC()
	{
		try
		{
			mmf = MemoryMappedFile.CreateOrOpen("QSTCompanion_IPC", 4096L, MemoryMappedFileAccess.ReadWrite);
			isRunning = true;
			listenerThread = new Thread(ListenerLoop)
			{
				IsBackground = true,
				Name = "QSTCompanion IPC Listener"
			};
			listenerThread.Start();
			log.Information("[CrossProcessIPC] Initialized with Memory-Mapped File");
			if (configuration.IsHelperAutomationActive)
			{
				framework.RunOnFrameworkThread(delegate
				{
					AnnounceHelper();
				});
			}
		}
		catch (Exception ex)
		{
			log.Error("[CrossProcessIPC] Failed to initialize: " + ex.Message);
		}
	}

	private void ListenerLoop()
	{
		string text = "";
		while (isRunning)
		{
			try
			{
				if (mmf == null)
				{
					break;
				}
				using (MemoryMappedViewAccessor memoryMappedViewAccessor = mmf.CreateViewAccessor(0L, 4096L, MemoryMappedFileAccess.Read))
				{
					byte[] array = new byte[4096];
					memoryMappedViewAccessor.ReadArray(0L, array, 0, 4096);
					string text2 = Encoding.UTF8.GetString(array).TrimEnd('\0');
					if (!string.IsNullOrEmpty(text2) && text2 != text)
					{
						text = text2;
						ProcessMessage(text2);
					}
				}
				Thread.Sleep(10);
			}
			catch (Exception ex)
			{
				log.Error("[CrossProcessIPC] Listener error: " + ex.Message);
				Thread.Sleep(1000);
			}
		}
	}

	private void ProcessMessage(string message)
	{
		try
		{
			string[] parts = message.Split('|');
			if (parts.Length < 2)
			{
				return;
			}
			string command = parts[0];
			_003C_003Ec__DisplayClass70_0 CS_0024_003C_003E8__locals0;
			framework.RunOnFrameworkThread(delegate
			{
				if (!isRunning)
				{
					return;
				}
				try
				{
					string text = command;
					if (text != null)
					{
						switch (text.Length)
						{
						case 16:
							switch (text[0])
							{
							case 'H':
								if (text == "HELPER_AVAILABLE" && parts.Length >= 3)
								{
									string text16 = parts[1];
									if (ushort.TryParse(parts[2], out var result19))
									{
										log.Information($"[CrossProcessIPC] Helper available: {text16}@{result19}");
										this.OnHelperAvailable?.Invoke(text16, result19);
									}
								}
								break;
							case 'C':
								if (text == "CHAUFFEUR_SUMMON" && parts.Length >= 12)
								{
									ushort questerWorld = ushort.Parse(parts[2]);
									ushort questerCurrentWorld = ushort.Parse(parts[3]);
									uint zoneId = uint.Parse(parts[4]);
									Vector3 targetPos = new Vector3(float.Parse(parts[5]), float.Parse(parts[6]), float.Parse(parts[7]));
									Vector3 questerPos = new Vector3(float.Parse(parts[8]), float.Parse(parts[9]), float.Parse(parts[10]));
									bool isAttuneAetheryte = bool.Parse(parts[11]);
									string nearestAetheryteName = null;
									if (parts.Length >= 13)
									{
										nearestAetheryteName = parts[12];
									}
									framework.RunOnFrameworkThread(delegate
									{
										this.OnChauffeurSummonRequest?.Invoke((string)(object)CS_0024_003C_003E8__locals0, questerWorld, questerCurrentWorld, zoneId, targetPos, questerPos, isAttuneAetheryte, nearestAetheryteName);
									});
								}
								break;
							case 'Q':
								if (text == "QUESTER_POSITION" && parts.Length >= 7)
								{
									string arg3 = parts[1];
									if (ushort.TryParse(parts[2], out var result14) && uint.TryParse(parts[3], out var result15) && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var result16) && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var result17) && float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var result18))
									{
										Vector3 arg4 = new Vector3(result16, result17, result18);
										this.OnQuesterPositionUpdate?.Invoke(arg3, result14, result15, arg4);
									}
								}
								break;
							}
							break;
						case 14:
							switch (text[7])
							{
							case 'R':
								if (text == "HELPER_REQUEST" && parts.Length >= 3)
								{
									string text13 = parts[1];
									if (ushort.TryParse(parts[2], out var result12))
									{
										log.Information($"[CrossProcessIPC] Helper request: {text13}@{result12}");
										this.OnHelperRequested?.Invoke(text13, result12);
									}
								}
								break;
							case 'D':
								if (text == "HELPER_DISMISS")
								{
									log.Information("[CrossProcessIPC] Helper dismiss");
									this.OnHelperDismissed?.Invoke();
								}
								break;
							case 'I':
								if (text == "HELPER_IN_DUTY" && parts.Length >= 3)
								{
									string text12 = parts[1];
									if (ushort.TryParse(parts[2], out var result11))
									{
										log.Information($"[CrossProcessIPC] Helper in duty: {text12}@{result11}");
										this.OnHelperInDuty?.Invoke(text12, result11);
									}
								}
								break;
							}
							break;
						case 15:
							switch (text[0])
							{
							case 'H':
								if (text == "HELPER_IN_PARTY" && parts.Length >= 3)
								{
									string text11 = parts[1];
									if (ushort.TryParse(parts[2], out var result10))
									{
										log.Information($"[CrossProcessIPC] Helper in party: {text11}@{result10}");
										this.OnHelperInParty?.Invoke(text11, result10);
									}
								}
								break;
							case 'C':
								if (text == "CHAUFFEUR_READY" && parts.Length >= 3)
								{
									string text10 = parts[1];
									if (ushort.TryParse(parts[2], out var result9))
									{
										log.Information($"[CrossProcessIPC] Chauffeur ready: {text10}@{result9}");
										this.OnChauffeurReadyForPickup?.Invoke(text10, result9);
									}
								}
								break;
							}
							break;
						case 17:
							switch (text[11])
							{
							case 'R':
								if (text == "CHAUFFEUR_ARRIVED" && parts.Length >= 3)
								{
									string text9 = parts[1];
									if (ushort.TryParse(parts[2], out var result8))
									{
										log.Information("[CrossProcessIPC] Chauffeur arrived for: " + text9 + "@" + WorldNameHelper.GetWorldName(result8));
										this.OnChauffeurArrived?.Invoke(text9, result8);
									}
								}
								break;
							case 'B':
								if (text == "CHAUFFEUR_ABORTED" && parts.Length >= 3)
								{
									_ = parts[1];
									string text8 = parts[1];
									if (ushort.TryParse(parts[2], out var result7))
									{
										log.Warning($"[CrossProcessIPC] Chauffeur ABORTED signal from: {text8}@{result7}");
										this.OnChauffeurAborted?.Invoke(text8, result7);
									}
								}
								break;
							}
							break;
						case 21:
							switch (text[10])
							{
							case 'Z':
								if (text == "CHAUFFEUR_ZONE_UPDATE" && parts.Length >= 5)
								{
									string text5 = parts[1];
									if (ushort.TryParse(parts[2], out var result4) && uint.TryParse(parts[3], out var result5))
									{
										string text6 = parts[4];
										log.Information($"[CrossProcessIPC] Zone update: {text5}@{result4} -> {text6} ({result5})");
										this.OnChauffeurZoneUpdate?.Invoke(text5, result4, result5, text6);
									}
								}
								break;
							case 'M':
								if (text == "CHAUFFEUR_MOUNT_READY" && parts.Length >= 3)
								{
									string text4 = parts[1];
									if (ushort.TryParse(parts[2], out var result3))
									{
										log.Information("[CrossProcessIPC] Chauffeur mount ready for: " + text4 + "@" + WorldNameHelper.GetWorldName(result3));
										IPluginLog pluginLog = log;
										DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(53, 1);
										defaultInterpolatedStringHandler.AppendLiteral("[CrossProcessIPC] OnChauffeurMountReady subscribers: ");
										Action<string, ushort>? action = this.OnChauffeurMountReady;
										defaultInterpolatedStringHandler.AppendFormatted((action != null) ? action.GetInvocationList().Length : 0);
										pluginLog.Information(defaultInterpolatedStringHandler.ToStringAndClear());
										this.OnChauffeurMountReady?.Invoke(text4, result3);
									}
								}
								break;
							}
							break;
						case 4:
							if (text == "CHAT" && parts.Length >= 2)
							{
								string text14 = parts[1];
								log.Information("[CrossProcessIPC] Chat: " + text14);
								this.OnChatMessageReceived?.Invoke(text14);
							}
							break;
						case 7:
							if (text == "COMMAND" && parts.Length >= 2)
							{
								string text15 = parts[1];
								log.Information("[CrossProcessIPC] Command: " + text15);
								this.OnCommandReceived?.Invoke(text15);
							}
							break;
						case 12:
							if (text == "HELPER_READY" && parts.Length >= 3)
							{
								string text7 = parts[1];
								if (ushort.TryParse(parts[2], out var result6))
								{
									log.Information($"[CrossProcessIPC] Helper ready: {text7}@{result6}");
									this.OnHelperReady?.Invoke(text7, result6);
								}
							}
							break;
						case 28:
							if (text == "REQUEST_HELPER_ANNOUNCEMENTS")
							{
								log.Information("[CrossProcessIPC] Request for helper announcements received");
								this.OnRequestHelperAnnouncements?.Invoke();
							}
							break;
						case 27:
							if (text == "CHAUFFEUR_PASSENGER_MOUNTED" && parts.Length >= 3)
							{
								string text17 = parts[1];
								if (ushort.TryParse(parts[2], out var result20))
								{
									log.Information("[CrossProcessIPC] Chauffeur passenger mounted signal received from: " + text17 + "@" + WorldNameHelper.GetWorldName(result20));
									IPluginLog pluginLog2 = log;
									DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(59, 1);
									defaultInterpolatedStringHandler2.AppendLiteral("[CrossProcessIPC] OnChauffeurPassengerMounted subscribers: ");
									Action<string, ushort>? action2 = this.OnChauffeurPassengerMounted;
									defaultInterpolatedStringHandler2.AppendFormatted((action2 != null) ? action2.GetInvocationList().Length : 0);
									pluginLog2.Information(defaultInterpolatedStringHandler2.ToStringAndClear());
									this.OnChauffeurPassengerMounted?.Invoke(text17, result20);
								}
							}
							break;
						case 13:
							if (text == "HELPER_STATUS" && parts.Length >= 4)
							{
								string arg = parts[1];
								if (ushort.TryParse(parts[2], out var result13))
								{
									string arg2 = parts[3];
									this.OnHelperStatusUpdate?.Invoke(arg, result13, arg2);
								}
							}
							break;
						case 20:
							if (text == "REQUEST_PARTY_INVITE" && parts.Length >= 5)
							{
								string text2 = parts[1];
								if (ushort.TryParse(parts[2], out var result))
								{
									string text3 = parts[3];
									if (ushort.TryParse(parts[4], out var result2))
									{
										log.Information("[CrossProcessIPC] Party Invite Request received: " + text2 + " from " + text3);
										this.OnPartyInviteRequested?.Invoke(text2, result, text3, result2);
									}
								}
							}
							break;
						}
					}
				}
				catch (Exception ex2)
				{
					log.Error("[CrossProcessIPC] Error in event handler: " + ex2.Message);
				}
			});
		}
		catch (Exception ex)
		{
			log.Error("[CrossProcessIPC] Error processing message: " + ex.Message);
		}
	}

	private void SendMessage(string message)
	{
		try
		{
			if (mmf == null)
			{
				return;
			}
			using MemoryMappedViewAccessor memoryMappedViewAccessor = mmf.CreateViewAccessor(0L, 4096L, MemoryMappedFileAccess.Write);
			byte[] bytes = Encoding.UTF8.GetBytes(message);
			if (bytes.Length > 4095)
			{
				log.Warning($"[CrossProcessIPC] Message too large: {bytes.Length} bytes");
			}
			else
			{
				byte[] array = new byte[4096];
				memoryMappedViewAccessor.WriteArray(0L, array, 0, 4096);
				memoryMappedViewAccessor.WriteArray(0L, bytes, 0, bytes.Length);
			}
		}
		catch (ObjectDisposedException)
		{
		}
		catch (Exception ex2)
		{
			log.Error("[CrossProcessIPC] Failed to send message: " + ex2.Message);
		}
	}

	public void AnnounceHelper()
	{
		if (configuration.IsHelperAutomationActive)
		{
			IPlayerCharacter playerCharacter = Plugin.ObjectTable?.LocalPlayer;
			if (playerCharacter != null)
			{
				string value = playerCharacter.Name.ToString();
				ushort value2 = (ushort)playerCharacter.HomeWorld.RowId;
				SendMessage($"HELPER_AVAILABLE|{value}|{value2}");
				log.Information($"[CrossProcessIPC] Announced as helper: {value}@{value2}");
			}
		}
	}

	public void RequestHelper(string characterName, ushort worldId)
	{
		SendMessage($"HELPER_REQUEST|{characterName}|{worldId}");
		log.Information($"[CrossProcessIPC] Requested helper: {characterName}@{worldId}");
	}

	public void DismissHelper()
	{
		SendMessage("HELPER_DISMISS");
		log.Information("[CrossProcessIPC] Dismissed helper");
	}

	public void SendChatMessage(string message)
	{
		SendMessage("CHAT|" + message);
		log.Information("[CrossProcessIPC] Chat: " + message);
	}

	public void SendCommand(string command)
	{
		SendMessage("COMMAND|" + command);
		log.Information("[CrossProcessIPC] Command: " + command);
	}

	public void NotifyHelperInParty(string name, ushort worldId)
	{
		SendMessage($"HELPER_IN_PARTY|{name}|{worldId}");
		log.Information($"[CrossProcessIPC] Notified: Helper in party {name}@{worldId}");
	}

	public void NotifyHelperInDuty(string name, ushort worldId)
	{
		SendMessage($"HELPER_IN_DUTY|{name}|{worldId}");
		log.Information($"[CrossProcessIPC] Notified: Helper in duty {name}@{worldId}");
	}

	public void NotifyHelperReady(string name, ushort worldId)
	{
		SendMessage($"HELPER_READY|{name}|{worldId}");
		log.Information($"[CrossProcessIPC] Notified: Helper ready {name}@{worldId}");
	}

	public void RequestHelperAnnouncements()
	{
		SendMessage("REQUEST_HELPER_ANNOUNCEMENTS");
		log.Information("[CrossProcessIPC] Requesting helper announcements from all clients");
	}

	public void BroadcastRequestHelperAnnouncements()
	{
		SendMessage("REQUEST_HELPER_ANNOUNCEMENTS");
		log.Information("[CrossProcessIPC] Broadcasting request for helper announcements");
	}

	public void RequestPartyInvite(string targetHelperName, ushort targetHelperWorld, string questerName, ushort questerWorld)
	{
		SendMessage($"REQUEST_PARTY_INVITE|{targetHelperName}|{targetHelperWorld}|{questerName}|{questerWorld}");
		log.Information($"[CrossProcessIPC] Requesting party invite from: {targetHelperName}@{targetHelperWorld}");
	}

	public void SendChauffeurSummonRequest(string questerName, ushort questerWorld, ushort questerCurrentWorld, uint zoneId, Vector3 targetPos, Vector3 questerPos, bool isAttuneAetheryte, string? nearestAetheryteName)
	{
		string value = nearestAetheryteName ?? "";
		SendMessage($"CHAUFFEUR_SUMMON|{questerName}|{questerWorld}|{questerCurrentWorld}|{zoneId}|{targetPos.X}|{targetPos.Y}|{targetPos.Z}|{questerPos.X}|{questerPos.Y}|{questerPos.Z}|{isAttuneAetheryte}|{value}");
		log.Information($"[CrossProcessIPC] Chauffeur summon: {questerName}@{WorldNameHelper.GetWorldName(questerWorld)} (Cur:{questerCurrentWorld}) zone {zoneId} quester@({questerPos.X:F2},{questerPos.Y:F2},{questerPos.Z:F2}) AttuneAetheryte={isAttuneAetheryte}, NearestAetheryte={value}");
	}

	public void SendChauffeurMountReady(string questerName, ushort questerWorld)
	{
		SendMessage($"CHAUFFEUR_MOUNT_READY|{questerName}|{questerWorld}");
		log.Information("[CrossProcessIPC] Chauffeur mount ready for RidePillion: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
	}

	public void SendChauffeurPassengerMounted(string questerName, ushort questerWorld)
	{
		SendMessage($"CHAUFFEUR_PASSENGER_MOUNTED|{questerName}|{questerWorld}");
		log.Debug($"[CrossProcessIPC] Sent: CHAUFFEUR_PASSENGER_MOUNTED|{questerName}|{questerWorld}");
	}

	public void SendChauffeurReadyForPickup(string helperName, ushort helperWorld)
	{
		SendMessage($"CHAUFFEUR_READY|{helperName}|{helperWorld}");
		log.Information($"[CrossProcessIPC] Chauffeur ready: {helperName}@{helperWorld}");
	}

	public void SendChauffeurArrived(string questerName, ushort questerWorld)
	{
		SendMessage($"CHAUFFEUR_ARRIVED|{questerName}|{questerWorld}");
		log.Information("[CrossProcessIPC] Chauffeur arrived for: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
	}

	public void SendChauffeurAborted(string questerName, ushort questerWorld)
	{
		SendMessage($"CHAUFFEUR_ABORTED|{questerName}|{questerWorld}");
		log.Information("[CrossProcessIPC] Sent CHAUFFEUR_ABORTED for: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
	}

	public void SendChauffeurZoneUpdate(string characterName, ushort worldId, uint zoneId, string zoneName)
	{
		SendMessage($"CHAUFFEUR_ZONE_UPDATE|{characterName}|{worldId}|{zoneId}|{zoneName}");
		log.Information($"[CrossProcessIPC] Zone update: {characterName}@{worldId} -> {zoneName} ({zoneId})");
	}

	public void BroadcastHelperStatus(string helperName, ushort helperWorld, string status)
	{
		SendMessage($"HELPER_STATUS|{helperName}|{helperWorld}|{status}");
	}

	public void BroadcastQuesterPosition(string questerName, ushort questerWorld, uint zoneId, Vector3 position)
	{
		SendMessage($"QUESTER_POSITION|{questerName}|{questerWorld}|{zoneId}|{position.X.ToString(CultureInfo.InvariantCulture)}|{position.Y.ToString(CultureInfo.InvariantCulture)}|{position.Z.ToString(CultureInfo.InvariantCulture)}");
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) == 0)
		{
			isRunning = false;
			Thread thread = listenerThread;
			listenerThread = null;
			if (thread != null && thread != Thread.CurrentThread && !thread.Join(1500))
			{
				log.Warning("[CrossProcessIPC] Listener did not stop within the shutdown timeout.");
			}
			Interlocked.Exchange(ref mmf, null)?.Dispose();
			log.Information("[CrossProcessIPC] Disposed");
		}
	}
}
