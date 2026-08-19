using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;
using QuestionableCompanion.Services;

public class ChauffeurModeService : IDisposable
{
	private readonly Configuration config;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICondition condition;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly IDataManager dataManager;

	private readonly IPartyList partyList;

	private readonly IObjectTable objectTable;

	private readonly QuestionableIPC questionableIPC;

	private readonly CrossProcessIPC crossProcessIPC;

	private readonly PartyInviteService partyInviteService;

	private readonly PartyInviteAutoAccept partyInviteAutoAccept;

	private readonly IDalamudPluginInterface pluginInterface;

	private readonly MemoryHelper memoryHelper;

	private readonly MovementMonitorService? movementMonitor;

	private readonly HelperManager? helperManager;

	private RuntimeEventSubscription? territoryChangedSubscription;

	private readonly VNavmeshIPC vnavmeshIPC;

	private readonly LifestreamIPC lifestreamIPC;

	private bool isWaitingForHelper;

	private bool isTransportingQuester;

	private bool hasExecutedRidePillion;

	private Vector3? targetPosition;

	private uint targetZoneId;

	private string? questerName;

	private DateTime lastZoneUpdate = DateTime.MinValue;

	private bool isDisposed;

	private bool helperStatusBroadcastActive;

	private CancellationTokenSource? helperWorkflowCts;

	private DateTime? lastZoneChangeTime;

	private DateTime lastDutyExitTime = DateTime.MinValue;

	private bool isPassengerMounted;

	private bool isFollowingQuester;

	private DateTime lastFollowCheck = DateTime.MinValue;

	private Vector3? lastQuesterPosition;

	private uint lastQuesterZone;

	private ushort lastQuesterWorld;

	private string? followingQuesterName;

	private DateTime lastQuesterPositionTime = DateTime.MinValue;

	private DateTime lastTransportEndTime = DateTime.MinValue;

	private static readonly HashSet<uint> BLACKLISTED_ZONES = new HashSet<uint> { 478u };

	private static readonly Dictionary<uint, uint> FLYING_INDICATOR_QUESTS = new Dictionary<uint, uint> { { 1669u, 402u } };

	private Dictionary<string, string> helperStatuses = new Dictionary<string, string>();

	private Dictionary<string, DateTime> discoveredQuesters = new Dictionary<string, DateTime>();

	private readonly HashSet<uint> restrictedZones = new HashSet<uint>
	{
		128u, 129u, 130u, 131u, 132u, 133u, 418u, 419u, 819u, 820u,
		962u, 963u, 1185u, 1186u, 250u
	};

	private Vector3? lastFollowingTargetPos;

	public bool IsWaitingForHelper => isWaitingForHelper;

	public bool IsTransportingQuester => isTransportingQuester;

	public void UpdateQuesterPositionFromLAN(float x, float y, float z, uint zoneId, string questerName)
	{
		lastQuesterPosition = new Vector3(x, y, z);
		lastQuesterZone = zoneId;
		followingQuesterName = questerName;
		lastQuesterPositionTime = DateTime.Now;
		discoveredQuesters[questerName] = DateTime.Now;
	}

	public string? GetHelperStatus(string helperKey)
	{
		if (!helperStatuses.TryGetValue(helperKey, out string value))
		{
			return null;
		}
		return value;
	}

	public void StartHelperStatusBroadcast()
	{
		if (config.IsHelperAutomationActive && !helperStatusBroadcastActive)
		{
			helperStatusBroadcastActive = true;
			log.Information("[ChauffeurMode] Starting periodic helper status broadcast (Helper mode enabled)");
			framework.RunOnTick(delegate
			{
				BroadcastHelperStatusPeriodically();
			}, TimeSpan.FromSeconds(1L));
		}
	}

	public void OnHelperAutomationDeactivated()
	{
		helperStatusBroadcastActive = false;
		helperWorkflowCts?.Cancel();
		helperWorkflowCts?.Dispose();
		helperWorkflowCts = null;
		isWaitingForHelper = false;
		isTransportingQuester = false;
		isPassengerMounted = false;
		isFollowingQuester = false;
		followingQuesterName = null;
		lastQuesterPosition = null;
		discoveredQuesters.Clear();
		StopNavigation();
		log.Information("[ChauffeurMode] Helper automation deactivated; helper workflows and status broadcasts stopped.");
	}

	public List<string> GetDiscoveredQuesters()
	{
		DateTime now = DateTime.Now;
		foreach (string item in (from kvp in discoveredQuesters
			where (now - kvp.Value).TotalSeconds > 60.0
			select kvp.Key).ToList())
		{
			discoveredQuesters.Remove(item);
		}
		List<string> list = discoveredQuesters.Keys.ToList();
		LANHelperServer lANHelperServer = Plugin.Instance?.GetLANHelperServer();
		if (lANHelperServer != null)
		{
			foreach (string connectedClientName in lANHelperServer.GetConnectedClientNames())
			{
				if (!list.Contains(connectedClientName))
				{
					list.Add(connectedClientName);
				}
			}
		}
		return list;
	}

	public ChauffeurModeService(Configuration config, IPluginLog log, IClientState clientState, ICondition condition, IFramework framework, ICommandManager commandManager, IDataManager dataManager, IPartyList partyList, IObjectTable objectTable, QuestionableIPC questionableIPC, CrossProcessIPC crossProcessIPC, PartyInviteService partyInviteService, PartyInviteAutoAccept partyInviteAutoAccept, IDalamudPluginInterface pluginInterface, MemoryHelper memoryHelper, MovementMonitorService movementMonitor, HelperManager helperManager)
	{
		this.config = config;
		this.log = log;
		this.clientState = clientState;
		this.condition = condition;
		this.framework = framework;
		this.commandManager = commandManager;
		this.dataManager = dataManager;
		this.partyList = partyList;
		this.objectTable = objectTable;
		this.questionableIPC = questionableIPC;
		this.crossProcessIPC = crossProcessIPC;
		this.partyInviteService = partyInviteService;
		this.partyInviteAutoAccept = partyInviteAutoAccept;
		this.pluginInterface = pluginInterface;
		this.memoryHelper = memoryHelper;
		this.movementMonitor = movementMonitor;
		this.helperManager = helperManager;
		vnavmeshIPC = new VNavmeshIPC(pluginInterface);
		lifestreamIPC = new LifestreamIPC(log, pluginInterface, commandManager);
		crossProcessIPC.OnChauffeurSummonRequest += OnChauffeurSummonRequest;
		crossProcessIPC.OnChauffeurReadyForPickup += OnChauffeurReadyForPickupInternal;
		crossProcessIPC.OnChauffeurArrived += OnChauffeurArrived;
		crossProcessIPC.OnChauffeurZoneUpdate += OnChauffeurZoneUpdate;
		crossProcessIPC.OnChauffeurMountReady += OnChauffeurMountReady;
		crossProcessIPC.OnChauffeurPassengerMounted += OnChauffeurPassengerMounted;
		crossProcessIPC.OnHelperStatusUpdate += OnHelperStatusUpdate;
		crossProcessIPC.OnHelperStatusUpdate += OnHelperStatusUpdate;
		crossProcessIPC.OnQuesterPositionUpdate += OnQuesterPositionUpdate;
		crossProcessIPC.OnChauffeurAborted += OnChauffeurAborted;
		territoryChangedSubscription = RuntimeEventSubscription.Subscribe(clientState, "TerritoryChanged", OnTerritoryChanged, log, "ChauffeurMode.TerritoryChanged");
		condition.ConditionChange += OnConditionChanged;
		if (config.IsHelperAutomationActive)
		{
			StartHelperStatusBroadcast();
			log.Information("[ChauffeurMode] Periodic helper status broadcast enabled (every 10s)");
		}
		framework.Update += OnFrameworkUpdate;
		log.Information("[ChauffeurMode] Service initialized");
	}

	private void OnConditionChanged(ConditionFlag flag, bool value)
	{
		if (flag == ConditionFlag.BoundByDuty && !value)
		{
			lastDutyExitTime = DateTime.Now;
			log.Information("[ChauffeurMode] Left duty - starting 10s grace period for zone checks");
		}
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (config.IsHelperAutomationActive && config.EnableHelperFollowing && (DateTime.Now - lastFollowCheck).TotalSeconds >= (double)config.HelperFollowCheckInterval)
		{
			CheckHelperFollowing();
		}
		if (config.IsQuester && !string.IsNullOrEmpty(config.AssignedHelperForFollowing) && config.EnableHelperFollowing)
		{
			DateTime now = DateTime.Now;
			if ((now - lastFollowCheck).TotalSeconds >= 5.0)
			{
				BroadcastQuesterPosition();
				lastFollowCheck = now;
			}
		}
	}

	public void CheckWaitTerritoryTask()
	{
	}

	public void CheckTaskDistance()
	{
		if (!config.ChauffeurModeEnabled || !config.IsQuester || !questionableIPC.IsAvailable || !questionableIPC.IsRunning())
		{
			return;
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return;
		}
		if (lastZoneChangeTime.HasValue)
		{
			double totalSeconds = (DateTime.Now - lastZoneChangeTime.Value).TotalSeconds;
			if (totalSeconds < 8.0)
			{
				log.Debug($"[ChauffeurMode] Territory Load State: Waiting for zone load before checking summon (elapsed: {totalSeconds:F1}s / 8.0s)");
				return;
			}
		}
		if ((DateTime.Now - lastDutyExitTime).TotalSeconds < 10.0)
		{
			return;
		}
		uint territoryType = clientState.TerritoryType;
		if (BLACKLISTED_ZONES.Contains(territoryType))
		{
			log.Debug($"[ChauffeurMode] Zone {territoryType} is blacklisted (no flying), cannot use Chauffeur Mode");
			return;
		}
		if (IsRestrictedZone(territoryType))
		{
			log.Debug($"[ChauffeurMode] Zone {territoryType} is restricted (Main City), cannot use Chauffeur Mode");
			return;
		}
		if (IsSoloDutyOrInstance(territoryType))
		{
			log.Debug($"[ChauffeurMode] Zone {territoryType} is a Solo Duty/Instance, cannot use Chauffeur Mode");
			return;
		}
		if (!IsMountingAllowed(territoryType))
		{
			log.Debug($"[ChauffeurMode] Zone {territoryType} does not allow mounting, cannot use Chauffeur Mode");
			return;
		}
		if (HasFlyingInZone(territoryType))
		{
			log.Debug($"[ChauffeurMode] Flying already unlocked in zone {territoryType}, no helper needed");
			return;
		}
		string currentQuestId = questionableIPC.GetCurrentQuestId();
		if (!string.IsNullOrEmpty(currentQuestId) && uint.TryParse(currentQuestId, out var result))
		{
			if (FLYING_INDICATOR_QUESTS.TryGetValue(result, out var value) && value == territoryType)
			{
				log.Debug($"[ChauffeurMode] Current quest {result} indicates flying is already unlocked in zone {territoryType} - no helper needed");
				return;
			}
			StepData currentStepData = questionableIPC.GetCurrentStepData();
			if (currentStepData != null && config.ChauffeurBlacklist != null)
			{
				string item = $"{result}:{currentStepData.Sequence}";
				if (config.ChauffeurBlacklist.Contains(item))
				{
					log.Information($"[ChauffeurMode] Quest {result} Sequence {currentStepData.Sequence} is blacklisted (Sequence Block) - Chauffeur will not be summoned");
					return;
				}
				string item2 = $"{result}:{currentStepData.Sequence}:{currentStepData.Step}";
				if (config.ChauffeurBlacklist.Contains(item2))
				{
					log.Information($"[ChauffeurMode] Quest {result} Sequence {currentStepData.Sequence} Step {currentStepData.Step} is blacklisted (Step Block) - Chauffeur will not be summoned");
					return;
				}
			}
		}
		StepData currentStepData2 = questionableIPC.GetCurrentStepData();
		if (currentStepData2 == null || !currentStepData2.Position.HasValue)
		{
			log.Debug("[ChauffeurMode] Current Questionable step has no target position");
			return;
		}
		Vector3 value2 = currentStepData2.Position.Value;
		bool num = string.Equals(currentStepData2.InteractionType, "AttuneAetheryte", StringComparison.Ordinal);
		Vector3 position = localPlayer.Position;
		float num2 = Vector3.Distance(position, value2);
		float num3 = (num ? 10f : config.ChauffeurDistanceThreshold);
		log.Information($"[ChauffeurMode] Current Position: ({position.X:F2}, {position.Y:F2}, {position.Z:F2})");
		log.Information($"[ChauffeurMode] Target Position: ({value2.X:F2}, {value2.Y:F2}, {value2.Z:F2})");
		log.Information($"[ChauffeurMode] Distance to task: {num2:F2} yalms (threshold: {num3})");
		if (num2 > num3)
		{
			log.Information($"[ChauffeurMode] Task distance ({num2:F2} yalms) exceeds threshold, checking combat status");
			if (condition[ConditionFlag.InCombat])
			{
				log.Information("[ChauffeurMode] Player is in combat - waiting for combat to end before summoning helper");
				return;
			}
			log.Information("[ChauffeurMode] Not in combat - summoning helper");
			SummonHelper(value2, territoryType);
		}
		else
		{
			log.Debug($"[ChauffeurMode] Task is close enough ({num2:F2} yalms), no helper needed");
		}
	}

	private string MapTerritoryName(string territoryName)
	{
		if (territoryName.Contains("Dravanian Hinterlands", StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[ChauffeurMode] Mapping 'Dravanian Hinterlands' â†’ 'Epilogue Gate'");
			return "Epilogue Gate";
		}
		if (territoryName.Contains("Old Gridania", StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[ChauffeurMode] Mapping 'Old Gridania' â†’ 'Mih Khetto'");
			return "Mih Khetto";
		}
		if (territoryName.Contains("Upper Decks", StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[ChauffeurMode] Mapping 'Upper Decks' â†’ 'Aftcastle'");
			return "Aftcastle";
		}
		if (territoryName.Contains("Coerthas Central Highlands", StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[ChauffeurMode] Mapping 'Coerthas Central Highlands' â†’ 'Camp Dragonhead'");
			return "Camp Dragonhead";
		}
		if (territoryName.Contains("The Pillars", StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[ChauffeurMode] Mapping 'The Pillars' â†’ 'The Last Vigil'");
			return "The Last Vigil";
		}
		if (territoryName.Contains("Steps of Thal", StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[ChauffeurMode] Mapping 'Steps of Thal' â†’ 'The Chamber of Rule'");
			return "The Chamber of Rule";
		}
		return territoryName;
	}

	private unsafe bool HasFlyingInZone(uint zoneId)
	{
		try
		{
			ExcelSheet<TerritoryType> excelSheet = dataManager.GetExcelSheet<TerritoryType>();
			if (excelSheet == null)
			{
				log.Debug("[ChauffeurMode] TerritoryType sheet is null");
				return false;
			}
			TerritoryType? rowOrDefault = excelSheet.GetRowOrDefault(zoneId);
			if (!rowOrDefault.HasValue)
			{
				log.Debug($"[ChauffeurMode] Territory {zoneId} not found");
				return false;
			}
			if (!rowOrDefault.Value.Mount)
			{
				log.Debug($"[ChauffeurMode] Zone {zoneId} does not allow mounting");
				return false;
			}
			RowRef<AetherCurrentCompFlgSet> aetherCurrentCompFlgSet = rowOrDefault.Value.AetherCurrentCompFlgSet;
			if (!aetherCurrentCompFlgSet.IsValid || aetherCurrentCompFlgSet.RowId == 0)
			{
				log.Debug($"[ChauffeurMode] Zone {zoneId} has no aether currents (AetherCurrentCompFlgSet invalid or 0)");
				return false;
			}
			PlayerState* ptr = PlayerState.Instance();
			if (ptr == null)
			{
				log.Debug("[ChauffeurMode] PlayerState is null");
				return false;
			}
			byte b = (byte)aetherCurrentCompFlgSet.RowId;
			bool flag = ptr->IsAetherCurrentZoneComplete(b);
			log.Debug($"[ChauffeurMode] Zone {zoneId} (AetherCurrentId: {b}) flying check: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Error checking flying availability: " + ex.Message);
			return false;
		}
	}

	private async void SummonHelper(Vector3 targetPos, uint zoneId)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return;
		}
		if (condition[ConditionFlag.InCombat])
		{
			log.Warning("[ChauffeurMode] Still in combat - cannot summon helper yet");
			return;
		}
		log.Information("[ChauffeurMode] ========================================");
		log.Information("[ChauffeurMode] === SUMMONING HELPER ===");
		log.Information("[ChauffeurMode] ========================================");
		if (config.HelperSelection == HelperSelectionMode.ManualInput)
		{
			log.Warning("[ChauffeurMode] [QUESTER] Manual Input mode is selected!");
			log.Warning("[ChauffeurMode] [QUESTER] Chauffeur Mode requires IPC communication and cannot work with Manual Input.");
			log.Warning("[ChauffeurMode] [QUESTER] Please switch to 'Auto' or 'Dropdown' mode to use Chauffeur.");
			log.Warning("[ChauffeurMode] [QUESTER] Walking to destination instead.");
			return;
		}
		if (!string.IsNullOrEmpty(config.PreferredHelper))
		{
			string preferredHelper = config.PreferredHelper;
			log.Information("[ChauffeurMode] [QUESTER] Preferred Helper: " + preferredHelper);
			if (!helperStatuses.TryGetValue(preferredHelper, out string value))
			{
				log.Warning("[ChauffeurMode] [QUESTER] No status received from preferred helper yet - walking to destination");
				return;
			}
			log.Information("[ChauffeurMode] [QUESTER] Helper status: " + value);
			if (value != "Available")
			{
				log.Warning("[ChauffeurMode] [QUESTER] Preferred helper is " + value + " - walking to destination instead");
				log.Warning("[ChauffeurMode] [QUESTER] Continuing quest without helper");
				return;
			}
		}
		log.Information("[ChauffeurMode] Stopping Questionable to wait for helper");
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			log.Information("[ChauffeurMode] [QUESTER] Stopping Movement Monitor during transport");
			movementMonitor.StopMonitoring();
		}
		log.Information("[ChauffeurMode] Stopping Questionable to wait for helper");
		TaskCompletionSource<bool> stopTask = new TaskCompletionSource<bool>();
		framework.RunOnFrameworkThread(delegate
		{
			try
			{
				commandManager.ProcessCommand("/qst stop");
				stopTask.SetResult(result: true);
			}
			catch (Exception ex2)
			{
				log.Error("[ChauffeurMode] Error stopping Questionable: " + ex2.Message);
				stopTask.SetResult(result: false);
			}
		});
		await stopTask.Task;
		log.Information("[ChauffeurMode] [QUESTER] Enabling auto-accept for party invites");
		log.Information($"[ChauffeurMode] [QUESTER] Current role - IsQuester: {config.IsQuester}, IsHelperActive: {config.IsHelperAutomationActive}");
		partyInviteAutoAccept.EnableAutoAccept();
		log.Information("[ChauffeurMode] [QUESTER] Auto-accept enabled - will accept Helper's invite");
		string text = localPlayer.Name.ToString();
		ushort num = (ushort)localPlayer.HomeWorld.RowId;
		if (!string.IsNullOrEmpty(config.PreferredHelper))
		{
			log.Information("[ChauffeurMode] [QUESTER] Starting bidirectional invite loop");
			string[] array = config.PreferredHelper.Split('@');
			if (array.Length == 2)
			{
				string helperName = array[0];
				if (ushort.TryParse(array[1], out var helperWorld))
				{
					Task.Run(async delegate
					{
						for (int i = 0; i < 10; i++)
						{
							try
							{
								TaskCompletionSource<bool> inPartyTask = new TaskCompletionSource<bool>();
								framework.RunOnFrameworkThread(delegate
								{
									bool result = partyList.Length > 0;
									inPartyTask.SetResult(result);
								});
								if (await inPartyTask.Task)
								{
									log.Information("[ChauffeurMode] [QUESTER] Party formed - stopping invite loop");
									break;
								}
								if (!isWaitingForHelper)
								{
									log.Information("[ChauffeurMode] [QUESTER] Transport started (isWaitingForHelper=false) - stopping invite loop");
									break;
								}
								log.Information($"[ChauffeurMode] [QUESTER] Sending invite to {helperName}@{helperWorld} (attempt {i + 1}/10)");
								TaskCompletionSource<bool> inviteTask = new TaskCompletionSource<bool>();
								framework.RunOnFrameworkThread(delegate
								{
									try
									{
										bool flag3 = partyInviteService.InviteToParty(helperName, helperWorld);
										log.Information($"[ChauffeurMode] [QUESTER] Invite result: {flag3}");
										inviteTask.SetResult(flag3);
									}
									catch (Exception ex3)
									{
										log.Error("[ChauffeurMode] [QUESTER] Error sending invite: " + ex3.Message);
										inviteTask.SetResult(result: false);
									}
								});
								await inviteTask.Task;
								await Task.Delay(2000);
							}
							catch (Exception ex2)
							{
								log.Error("[ChauffeurMode] [QUESTER] Error in invite loop: " + ex2.Message);
							}
						}
					});
				}
			}
		}
		targetPosition = targetPos;
		targetZoneId = zoneId;
		isWaitingForHelper = true;
		Vector3 position = localPlayer.Position;
		bool flag = false;
		try
		{
			StepData currentStepData = questionableIPC.GetCurrentStepData();
			if (currentStepData != null && currentStepData.InteractionType == "AttuneAetheryte")
			{
				flag = true;
				log.Information("[ChauffeurMode] Current step is AttuneAetheryte - Helper will find landable spot");
			}
			else
			{
				log.Information("[ChauffeurMode] Current step InteractionType: " + (currentStepData?.InteractionType ?? "null") + " - Helper will go to exact position");
			}
		}
		catch (Exception ex)
		{
			log.Warning("[ChauffeurMode] Failed to get step data: " + ex.Message);
		}
		log.Information("[ChauffeurMode] Requesting helper pickup");
		log.Information("[ChauffeurMode]   Quester: " + text + "@" + WorldNameHelper.GetWorldName(num));
		log.Information($"[ChauffeurMode]   Zone: {zoneId}");
		log.Information($"[ChauffeurMode]   Quester Position: ({position.X:F2}, {position.Y:F2}, {position.Z:F2})");
		log.Information($"[ChauffeurMode]   Target: ({targetPos.X:F2}, {targetPos.Y:F2}, {targetPos.Z:F2})");
		log.Information($"[ChauffeurMode]   AttuneAetheryte: {flag}");
		bool flag2 = false;
		string text2 = null;
		log.Information("[ChauffeurMode] Checking if preferred helper '" + config.PreferredHelper + "' is a LAN helper...");
		LANHelperClient lANHelperClient = Plugin.Instance?.GetLANHelperClient();
		if (lANHelperClient != null)
		{
			IReadOnlyList<LANHelperInfo> discoveredHelpers = lANHelperClient.DiscoveredHelpers;
			log.Information($"[ChauffeurMode] Found {discoveredHelpers.Count} LAN helpers in discovery list");
			if (!string.IsNullOrEmpty(config.PreferredHelper))
			{
				foreach (LANHelperInfo item in discoveredHelpers)
				{
					string text3 = $"{item.Name}@{item.WorldId}";
					log.Information("[ChauffeurMode]   Checking LAN helper: " + text3 + " at " + item.IPAddress);
					if (text3 == config.PreferredHelper)
					{
						flag2 = true;
						text2 = item.IPAddress;
						log.Information("[ChauffeurMode]   âœ“ MATCHED! This is a LAN helper at " + text2);
						break;
					}
				}
				if (!flag2)
				{
					log.Information("[ChauffeurMode]   No match found - PreferredHelper '" + config.PreferredHelper + "' not in LAN list");
				}
			}
			else if (discoveredHelpers.Any((LANHelperInfo h) => h.Status == LANHelperStatus.Available))
			{
				LANHelperInfo lANHelperInfo = discoveredHelpers.FirstOrDefault((LANHelperInfo h) => h.Status == LANHelperStatus.Available);
				if (lANHelperInfo != null)
				{
					flag2 = true;
					text2 = lANHelperInfo.IPAddress;
					string text4 = $"{lANHelperInfo.Name}@{lANHelperInfo.WorldId}";
					log.Information("[ChauffeurMode] AUTO-SELECTED LAN helper: " + text4 + " at " + text2);
				}
			}
			else if (discoveredHelpers.Count > 0)
			{
				LANHelperInfo lANHelperInfo2 = discoveredHelpers.First();
				flag2 = true;
				text2 = lANHelperInfo2.IPAddress;
				string text5 = $"{lANHelperInfo2.Name}@{lANHelperInfo2.WorldId}";
				log.Information("[ChauffeurMode] AUTO-SELECTED first LAN helper (no Available status): " + text5 + " at " + text2);
			}
			else
			{
				log.Information("[ChauffeurMode] No PreferredHelper configured and no LAN helpers available - using local IPC");
			}
		}
		else
		{
			log.Warning("[ChauffeurMode] LANHelperClient is null!");
			log.Information("[ChauffeurMode] Falling back to local IPC");
		}
		string nearestAetheryteName = FindNearestAetheryteInZone();
		if (flag2 && !string.IsNullOrEmpty(text2))
		{
			log.Information("[ChauffeurMode] Selected helper is on LAN (" + text2 + ") - Sending LAN Summon Request");
			if (lANHelperClient != null)
			{
				LANChauffeurSummon summonData = new LANChauffeurSummon
				{
					QuesterName = text,
					QuesterWorldId = num,
					QuesterCurrentWorldId = (ushort)localPlayer.CurrentWorld.RowId,
					ZoneId = zoneId,
					TargetX = targetPos.X,
					TargetY = targetPos.Y,
					TargetZ = targetPos.Z,
					QuesterX = position.X,
					QuesterY = position.Y,
					QuesterZ = position.Z,
					IsAttuneAetheryte = flag,
					NearestAetheryteName = nearestAetheryteName
				};
				lANHelperClient.SendChauffeurSummonAsync(text2, summonData);
			}
		}
		else
		{
			log.Information("[ChauffeurMode] Sending local IPC Summon Request");
			crossProcessIPC.SendChauffeurSummonRequest(text, num, (ushort)localPlayer.CurrentWorld.RowId, zoneId, targetPos, position, flag, nearestAetheryteName);
		}
	}

	public bool IsRestrictedZone(uint zoneId)
	{
		return restrictedZones.Contains(zoneId);
	}

	public bool IsSoloDutyOrInstance(uint zoneId)
	{
		try
		{
			ExcelSheet<TerritoryType> excelSheet = dataManager.GetExcelSheet<TerritoryType>();
			if (excelSheet == null)
			{
				return false;
			}
			TerritoryType? rowOrDefault = excelSheet.GetRowOrDefault(zoneId);
			if (!rowOrDefault.HasValue)
			{
				return false;
			}
			uint rowId = rowOrDefault.Value.TerritoryIntendedUse.RowId;
			switch (rowId)
			{
			case 8u:
			case 9u:
				log.Debug($"[ChauffeurMode] Zone {zoneId} is Solo Duty/Quest Battle (IntendedUse: {rowId})");
				return true;
			case 2u:
			case 3u:
			case 4u:
			case 5u:
				log.Debug($"[ChauffeurMode] Zone {zoneId} is party content (IntendedUse: {rowId})");
				return true;
			default:
				if (rowId == 13 || rowId == 16 || rowId == 17)
				{
					log.Debug($"[ChauffeurMode] Zone {zoneId} is special content (IntendedUse: {rowId})");
					return true;
				}
				return false;
			}
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Error checking solo duty status: " + ex.Message);
			return false;
		}
	}

	public bool IsMountingAllowed(uint zoneId)
	{
		try
		{
			ExcelSheet<TerritoryType> excelSheet = dataManager.GetExcelSheet<TerritoryType>();
			if (excelSheet == null)
			{
				return false;
			}
			TerritoryType? rowOrDefault = excelSheet.GetRowOrDefault(zoneId);
			if (!rowOrDefault.HasValue)
			{
				return false;
			}
			return rowOrDefault.Value.Mount;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Error checking mount permission: " + ex.Message);
			return false;
		}
	}

	public List<(uint Id, string Name, byte Seats)> GetMultiSeaterMounts()
	{
		List<(uint, string, byte)> list = new List<(uint, string, byte)>();
		try
		{
			ExcelSheet<Mount> excelSheet = dataManager.GetExcelSheet<Mount>();
			if (excelSheet == null)
			{
				log.Error("[ChauffeurMode] Could not load Mount sheet");
				return list;
			}
			foreach (Mount item in excelSheet)
			{
				if (item.ExtraSeats > 0)
				{
					string text = item.Singular.ToString();
					if (!string.IsNullOrEmpty(text))
					{
						list.Add((item.RowId, text, item.ExtraSeats));
					}
				}
			}
		}
		catch (Exception)
		{
		}
		return list;
	}

	public void StartHelperWorkflow(string questerName, ushort questerWorld, ushort questerCurrentWorld, uint zoneId, Vector3 targetPos, Vector3 questerPos, bool isAttuneAetheryte, string? nearestAetheryteName = null)
	{
		log.Information("[ChauffeurMode] =========================================");
		log.Information("[ChauffeurMode] *** StartHelperWorkflow CALLED ***");
		log.Information("[ChauffeurMode] =========================================");
		log.Information($"[ChauffeurMode] Quester: {questerName}@{WorldNameHelper.GetWorldName(questerWorld)} (Current: {questerCurrentWorld})");
		log.Information($"[ChauffeurMode] Zone: {zoneId}");
		log.Information($"[ChauffeurMode] Target: ({targetPos.X:F2}, {targetPos.Y:F2}, {targetPos.Z:F2})");
		log.Information($"[ChauffeurMode] AttuneAetheryte: {isAttuneAetheryte}");
		log.Information("[ChauffeurMode] NearestAetheryte: " + nearestAetheryteName);
		OnChauffeurSummonRequest(questerName, questerWorld, questerCurrentWorld, zoneId, targetPos, questerPos, isAttuneAetheryte, nearestAetheryteName);
	}

	public void OnChauffeurSummonRequest(string questerName, ushort questerWorld, ushort questerCurrentWorld, uint zoneId, Vector3 targetPos, Vector3 questerPos, bool isAttuneAetheryte, string? nearestAetheryteName = null)
	{
		framework.RunOnFrameworkThread(delegate
		{
			if (config.ChauffeurModeEnabled)
			{
				if (!config.IsHelperAutomationActive)
				{
					log.Debug("[ChauffeurMode] Not a helper, ignoring summon");
				}
				else
				{
					if (config.CurrentHelperStatus == HelperStatus.Transporting)
					{
						string text = questerName + "@" + WorldNameHelper.GetWorldName(questerWorld);
						if (!(config.AssignedQuester == text))
						{
							log.Warning($"[ChauffeurMode] [HELPER] Already transporting {config.AssignedQuester} - rejecting summon from {questerName}@{WorldNameHelper.GetWorldName(questerWorld)}");
							return;
						}
						log.Warning("[ChauffeurMode] [HELPER] RE-SUMMON received from " + text + " - RESTARTING WORKFLOW");
						helperWorkflowCts?.Cancel();
						helperWorkflowCts?.Dispose();
						helperWorkflowCts = null;
						isTransportingQuester = false;
					}
					if (config.CurrentHelperStatus == HelperStatus.InDungeon)
					{
						log.Warning("[ChauffeurMode] [HELPER] Currently in dungeon - rejecting summon from " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
					}
					else
					{
						this.questerName = questerName + "@" + WorldNameHelper.GetWorldName(questerWorld);
						targetZoneId = zoneId;
						targetPosition = targetPos;
						IPlayerCharacter localPlayer = objectTable.LocalPlayer;
						if (localPlayer != null)
						{
							string text2 = localPlayer.Name.ToString();
							ushort num = (ushort)localPlayer.HomeWorld.RowId;
							if (text2 == questerName && num == questerWorld)
							{
								log.Debug("[ChauffeurMode] Ignoring own summon request");
								return;
							}
						}
						log.Information("[ChauffeurMode] ========================================");
						log.Information("[ChauffeurMode] === HELPER SUMMON REQUEST ===");
						log.Information("[ChauffeurMode] ========================================");
						log.Information("[ChauffeurMode] Quester: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
						log.Information($"[ChauffeurMode] Zone: {zoneId}");
						log.Information($"[ChauffeurMode] Target: ({targetPos.X:F2}, {targetPos.Y:F2}, {targetPos.Z:F2})");
						log.Information($"[ChauffeurMode] Quester Position: ({questerPos.X:F2}, {questerPos.Y:F2}, {questerPos.Z:F2})");
						if (BLACKLISTED_ZONES.Contains(zoneId))
						{
							log.Warning($"[ChauffeurMode] Zone {zoneId} is blacklisted (no flying available), cannot use Chauffeur Mode");
						}
						else if (IsRestrictedZone(zoneId))
						{
							log.Warning($"[ChauffeurMode] Zone {zoneId} is restricted (Main City), cannot follow");
						}
						else if (IsSoloDutyOrInstance(zoneId))
						{
							log.Warning($"[ChauffeurMode] Zone {zoneId} is a Solo Duty/Instance, cannot follow");
						}
						else if (!IsMountingAllowed(zoneId))
						{
							log.Warning($"[ChauffeurMode] Zone {zoneId} does not allow mounting, cannot use Chauffeur Mode");
						}
						else if (config.ChauffeurMountId == 0)
						{
							log.Error("[ChauffeurMode] No mount configured! Please select a multi-seater mount in settings");
						}
						else
						{
							if (isTransportingQuester)
							{
								string text3 = questerName + "@" + WorldNameHelper.GetWorldName(questerWorld);
								if (!(config.AssignedQuester == text3))
								{
									log.Warning("[ChauffeurMode] [HELPER] Already transporting a quester! Ignoring new request from " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
									return;
								}
								log.Information("[ChauffeurMode] [HELPER] Re-summon confirmed (isTransportingQuester was true, resetting)");
								isTransportingQuester = false;
							}
							this.questerName = questerName;
							targetPosition = targetPos;
							targetZoneId = zoneId;
							isTransportingQuester = true;
							config.AssignedQuester = questerName + "@" + WorldNameHelper.GetWorldName(questerWorld);
							config.AssignedQuesterForFollowing = config.AssignedQuester;
							config.CurrentHelperStatus = HelperStatus.Transporting;
							config.Save();
							log.Information("[ChauffeurMode] [HELPER] Assigned to quester: " + config.AssignedQuester + " (Status: Transporting)");
							if (localPlayer != null)
							{
								string helperName = localPlayer.Name.ToString();
								ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
								crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "Transporting");
							}
							helperWorkflowCts?.Cancel();
							helperWorkflowCts?.Dispose();
							helperWorkflowCts = new CancellationTokenSource();
							CancellationTokenSource cts = helperWorkflowCts;
							Task.Run(async delegate
							{
								await HelperWorkflow(questerName, questerWorld, questerCurrentWorld, zoneId, targetPos, questerPos, isAttuneAetheryte, nearestAetheryteName, cts.Token);
							}, cts.Token);
						}
					}
				}
			}
		});
	}

	private async Task<bool> DCTravelToWorld(ushort targetWorldId)
	{
		_ = 1;
		try
		{
			if (lifestreamIPC != null)
			{
				lifestreamIPC.ForceCheckAvailability();
			}
			if (lifestreamIPC == null || !lifestreamIPC.IsAvailable)
			{
				log.Error("[ChauffeurMode] Lifestream IPC not available for DC Travel!");
				return false;
			}
			string worldName = (dataManager.GetExcelSheet<World>()?.GetRowOrDefault(targetWorldId))?.Name.ExtractText() ?? targetWorldId.ToString();
			log.Information($"[ChauffeurMode] Starting DC Travel to: {worldName} ({targetWorldId})");
			if (!lifestreamIPC.ChangeWorldById(targetWorldId))
			{
				log.Error("[ChauffeurMode] Lifestream.ChangeWorldById failed for " + worldName);
				return false;
			}
			for (int i = 0; i < 60; i++)
			{
				await Task.Delay(1000);
				TaskCompletionSource<ushort> currentWorldTask = new TaskCompletionSource<ushort>();
				framework.RunOnFrameworkThread(delegate
				{
					IPlayerCharacter localPlayer = objectTable.LocalPlayer;
					if (localPlayer != null)
					{
						currentWorldTask.SetResult((ushort)localPlayer.CurrentWorld.RowId);
					}
					else
					{
						currentWorldTask.SetResult(0);
					}
				});
				if (await currentWorldTask.Task == targetWorldId)
				{
					log.Information("[ChauffeurMode] DC Travel completed! Now on " + worldName);
					return true;
				}
			}
			log.Error("[ChauffeurMode] DC Travel timeout after 60 seconds");
			return false;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] DC Travel error: " + ex.Message);
			return false;
		}
	}

	private unsafe async Task HelperWorkflow(string questerName, ushort questerWorld, ushort questerCurrentWorld, uint zoneId, Vector3 targetPos, Vector3 questerPos, bool isAttuneAetheryte, string? nearestAetheryteName, CancellationToken cancellationToken)
	{
		try
		{
			log.Information("[ChauffeurMode] [WORKFLOW] Starting helper workflow");
			log.Information($"[ChauffeurMode] [WORKFLOW] Thread ID: {Thread.CurrentThread.ManagedThreadId}");
			if (cancellationToken.IsCancellationRequested)
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Workflow cancelled before start");
				framework.RunOnFrameworkThread(delegate
				{
					ResetHelperTransportState();
				});
				return;
			}
			framework.RunOnFrameworkThread(delegate
			{
				vnavmeshIPC.StopPathfinding();
			});
			TaskCompletionSource<(bool success, ushort helperWorld, uint helperZone, uint questerZone)> worldCheckTask = new TaskCompletionSource<(bool, ushort, uint, uint)>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					IPlayerCharacter localPlayer2 = objectTable.LocalPlayer;
					if (localPlayer2 == null)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] LocalPlayer is null!");
						worldCheckTask.SetResult((false, 0, 0u, 0u));
					}
					else
					{
						ushort item3 = (ushort)localPlayer2.CurrentWorld.RowId;
						uint territoryType = clientState.TerritoryType;
						worldCheckTask.SetResult((true, item3, territoryType, zoneId));
					}
				}
				catch (Exception ex2)
				{
					log.Error("[ChauffeurMode] [WORKFLOW] Error checking world: " + ex2.Message);
					worldCheckTask.SetResult((false, 0, 0u, 0u));
				}
			});
			var (flag, num, helperCurrentZone, value) = await worldCheckTask.Task;
			if (!flag)
			{
				log.Error("[ChauffeurMode] [WORKFLOW] Failed to check helper world!");
				framework.RunOnFrameworkThread(delegate
				{
					ResetHelperTransportState();
				});
				return;
			}
			log.Information($"[ChauffeurMode] [WORKFLOW] Helper on world {num}, zone {helperCurrentZone}");
			log.Information($"[ChauffeurMode] [WORKFLOW] Quester needs pickup in zone {value}");
			log.Information("[ChauffeurMode] [WORKFLOW] Quester invite ID: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
			log.Information($"[ChauffeurMode] [WORKFLOW] Quester current world: {questerCurrentWorld}");
			if (num != questerCurrentWorld && questerCurrentWorld != 0)
			{
				log.Information($"[ChauffeurMode] [WORKFLOW] Helper world ({num}) != Quester CURRENT world ({questerCurrentWorld})");
				log.Information("[ChauffeurMode] [WORKFLOW] Initiating DC Travel to Quester's current world...");
				if (!(await DCTravelToWorld(questerCurrentWorld)))
				{
					log.Error("[ChauffeurMode] [WORKFLOW] DC Travel failed!");
					framework.RunOnFrameworkThread(delegate
					{
						ResetHelperTransportState();
					});
					return;
				}
				log.Information("[ChauffeurMode] [WORKFLOW] DC Travel completed successfully");
				await Task.Delay(3000);
			}
			else
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Helper already on same world as Quester (or Quester world unknown)");
			}
			uint currentZone = clientState.TerritoryType;
			log.Information("[ChauffeurMode] [WORKFLOW] Step 1: Checking mount status");
			TaskCompletionSource<bool> isMountedTask = new TaskCompletionSource<bool>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					bool flag6 = IsMounted();
					log.Information($"[ChauffeurMode] [WORKFLOW] Currently mounted: {flag6}");
					isMountedTask.SetResult(flag6);
				}
				catch (Exception ex2)
				{
					log.Error("[ChauffeurMode] [WORKFLOW] Error checking mount: " + ex2.Message);
					isMountedTask.SetResult(result: false);
				}
			});
			if (!(await isMountedTask.Task))
			{
				TaskCompletionSource<bool> canMountTask = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						bool flag6 = !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Mounted] && !condition[ConditionFlag.Casting] && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.Jumping] && !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.OccupiedInCutSceneEvent] && !condition[ConditionFlag.BoundByDuty] && !condition[ConditionFlag.BoundByDuty56] && !condition[ConditionFlag.BoundByDuty95];
						log.Information($"[ChauffeurMode] [WORKFLOW] Can mount in Helper's zone {helperCurrentZone}: {flag6}");
						canMountTask.SetResult(flag6);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Error checking mount conditions: " + ex2.Message);
						canMountTask.SetResult(result: false);
					}
				});
				if (!(await canMountTask.Task))
				{
					log.Warning("[ChauffeurMode] [WORKFLOW] Cannot mount in Helper's current zone - will try after teleport");
				}
				else
				{
					log.Information($"[ChauffeurMode] [WORKFLOW] Not mounted, summoning mount {config.ChauffeurMountId}");
					TaskCompletionSource<bool> mountTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						log.Information("[ChauffeurMode] [WORKFLOW] Executing mount summon on framework thread");
						bool result = SummonMountDirect(config.ChauffeurMountId);
						mountTask.SetResult(result);
					});
					if (!(await mountTask.Task))
					{
						log.Warning("[ChauffeurMode] [WORKFLOW] Failed to summon mount - will try after teleport");
					}
					log.Information("[ChauffeurMode] [WORKFLOW] Mount summon command sent, waiting for mount animation");
					await Task.Delay(3000);
					TaskCompletionSource<bool> verifyTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							bool flag6 = IsMounted();
							log.Information($"[ChauffeurMode] [WORKFLOW] Mount verification: {flag6}");
							verifyTask.SetResult(flag6);
						}
						catch (Exception ex2)
						{
							log.Error("[ChauffeurMode] [WORKFLOW] Error verifying mount: " + ex2.Message);
							verifyTask.SetResult(result: false);
						}
					});
					if (!(await verifyTask.Task))
					{
						log.Warning("[ChauffeurMode] [WORKFLOW] Mount verification failed - will try after teleport");
					}
				}
				log.Information("[ChauffeurMode] [WORKFLOW] Mount verified successfully");
			}
			else
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Already mounted, skipping mount summon");
			}
			bool flag2 = false;
			if (currentZone != zoneId)
			{
				log.Information($"[ChauffeurMode] [WORKFLOW] Step 2: Teleporting to zone {zoneId}");
				bool flag3 = false;
				if (!string.IsNullOrEmpty(nearestAetheryteName))
				{
					log.Information("[ChauffeurMode] [WORKFLOW] Attempting teleport to nearest aetheryte: " + nearestAetheryteName);
					flag3 = await TeleportToAetheryte(nearestAetheryteName, zoneId);
					if (!flag3)
					{
						log.Warning("[ChauffeurMode] [WORKFLOW] Teleport to " + nearestAetheryteName + " failed, falling back to zone teleport");
					}
				}
				if (!flag3 && !(await TeleportToZone(zoneId)))
				{
					log.Error("[ChauffeurMode] [WORKFLOW] Failed to teleport to zone");
					return;
				}
				log.Information("[ChauffeurMode] [WORKFLOW] Waiting 10s for zone load and player spawn");
				await Task.Delay(10000);
				flag2 = true;
			}
			else
			{
				log.Information($"[ChauffeurMode] [WORKFLOW] Already in zone {zoneId}");
			}
			if (flag2)
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Step 2.5: Waiting additional 3s for player spawn and loading screen to complete");
				await Task.Delay(3000);
				log.Information("[ChauffeurMode] [WORKFLOW] Verifying zone after teleport");
				TaskCompletionSource<bool> verifyZoneTask = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						uint territoryType = clientState.TerritoryType;
						bool flag6 = territoryType == zoneId;
						log.Information($"[ChauffeurMode] [WORKFLOW] Current zone: {territoryType}, Target zone: {zoneId}, Match: {flag6}");
						verifyZoneTask.SetResult(flag6);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Error verifying zone: " + ex2.Message);
						verifyZoneTask.SetResult(result: false);
					}
				});
				bool flag4 = await verifyZoneTask.Task;
				if (!flag4)
				{
					log.Warning("[ChauffeurMode] [WORKFLOW] Not in correct zone yet! Will keep checking for up to 30 seconds...");
					int maxAttempts = 10;
					for (int attempt = 1; attempt <= maxAttempts; attempt++)
					{
						if (flag4)
						{
							break;
						}
						await Task.Delay(3000);
						TaskCompletionSource<bool> verifyZoneTaskRetry = new TaskCompletionSource<bool>();
						framework.RunOnFrameworkThread(delegate
						{
							try
							{
								uint territoryType = clientState.TerritoryType;
								bool flag6 = territoryType == zoneId;
								log.Information($"[ChauffeurMode] [WORKFLOW] Zone check attempt {attempt}/{maxAttempts} - Current zone: {territoryType}, Target zone: {zoneId}, Match: {flag6}");
								verifyZoneTaskRetry.SetResult(flag6);
							}
							catch (Exception ex2)
							{
								log.Error($"[ChauffeurMode] [WORKFLOW] Error verifying zone (attempt {attempt}): {ex2.Message}");
								verifyZoneTaskRetry.SetResult(result: false);
							}
						});
						flag4 = await verifyZoneTaskRetry.Task;
						if (flag4)
						{
							log.Information($"[ChauffeurMode] [WORKFLOW] Zone verified after {attempt} attempts!");
							break;
						}
					}
					if (!flag4)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Still not in correct zone after 30 seconds! Aborting.");
						framework.RunOnFrameworkThread(delegate
						{
							ResetHelperTransportState();
						});
						return;
					}
				}
				log.Information("[ChauffeurMode] [WORKFLOW] Zone verified - waiting 2s for loading screen to fully complete");
				await Task.Delay(2000);
				log.Information("[ChauffeurMode] [WORKFLOW] In correct zone - checking mount status");
				TaskCompletionSource<bool> checkMountTask = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						bool flag6 = IsMounted();
						log.Information($"[ChauffeurMode] [WORKFLOW] Mount status after teleport: {flag6}");
						checkMountTask.SetResult(flag6);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Error checking mount: " + ex2.Message);
						checkMountTask.SetResult(result: false);
					}
				});
				if (!(await checkMountTask.Task))
				{
					log.Information("[ChauffeurMode] [WORKFLOW] Not mounted after teleport - waiting 1s then re-mounting");
					await Task.Delay(1000);
					log.Information("[ChauffeurMode] [WORKFLOW] Re-mounting now");
					TaskCompletionSource<bool> remountTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						log.Information("[ChauffeurMode] [WORKFLOW] Executing re-mount on framework thread");
						bool result = SummonMountDirect(config.ChauffeurMountId);
						remountTask.SetResult(result);
					});
					if (!(await remountTask.Task))
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Failed to re-summon mount after teleport");
						return;
					}
					log.Information("[ChauffeurMode] [WORKFLOW] Re-mount command sent, waiting for mount animation");
					await Task.Delay(3000);
					TaskCompletionSource<bool> verifyRemountTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							bool flag6 = IsMounted();
							log.Information($"[ChauffeurMode] [WORKFLOW] Re-mount verification: {flag6}");
							verifyRemountTask.SetResult(flag6);
						}
						catch (Exception ex2)
						{
							log.Error("[ChauffeurMode] [WORKFLOW] Error verifying re-mount: " + ex2.Message);
							verifyRemountTask.SetResult(result: false);
						}
					});
					if (!(await verifyRemountTask.Task))
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Re-mount verification failed - not mounted after teleport!");
						framework.RunOnFrameworkThread(delegate
						{
							ResetHelperTransportState();
						});
						return;
					}
					log.Information("[ChauffeurMode] [WORKFLOW] Re-mount verified successfully");
				}
				else
				{
					log.Information("[ChauffeurMode] [WORKFLOW] Still mounted after teleport - no need to re-mount");
				}
			}
			Vector3 finalTargetPos = targetPos;
			if (isAttuneAetheryte)
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Step 2.9: AttuneAetheryte detected - finding landable spot");
				if (vnavmeshIPC.IsReady())
				{
					log.Information($"[ChauffeurMode] [WORKFLOW] Searching for landable spot near target ({targetPos.X:F2}, {targetPos.Y:F2}, {targetPos.Z:F2})");
					Vector3? vector = vnavmeshIPC.FindPointOnFloor(targetPos, allowUnlandable: false, 15f);
					if (vector.HasValue)
					{
						float value2 = Vector3.Distance(targetPos, vector.Value);
						log.Information($"[ChauffeurMode] [WORKFLOW] Found landable spot {value2:F2} yalms from target: ({vector.Value.X:F2}, {vector.Value.Y:F2}, {vector.Value.Z:F2})");
						finalTargetPos = vector.Value;
					}
					else
					{
						log.Warning("[ChauffeurMode] [WORKFLOW] No landable spot found, using original target position");
					}
				}
				else
				{
					log.Warning("[ChauffeurMode] [WORKFLOW] vnavmesh not ready, using original target position");
				}
			}
			else
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Step 2.9: Not AttuneAetheryte - using exact target position");
			}
			Vector3 targetPickupPos = questerPos;
			if (vnavmeshIPC != null)
			{
				Vector3? vector2 = vnavmeshIPC.FindNearestPoint(questerPos, 5f, 3f);
				if (!vector2.HasValue)
				{
					log.Error("[ChauffeurMode] [WORKFLOW] VNavmesh could not find accessible position - quester location is inaccessible!");
					log.Error("[ChauffeurMode] [WORKFLOW] Aborting transport - sending signal to quester to continue without chauffeur");
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							crossProcessIPC.SendChauffeurAborted(questerName, questerWorld);
							log.Information("[ChauffeurMode] [WORKFLOW] Abort signal sent to " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
						}
						catch (Exception ex2)
						{
							log.Error("[ChauffeurMode] [WORKFLOW] Error sending abort signal: " + ex2.Message);
						}
					});
					ResetHelperTransportState();
					return;
				}
				targetPickupPos = vector2.Value;
				log.Information($"[ChauffeurMode] [WORKFLOW] VNavmesh adjusted pickup position: ({targetPickupPos.X:F2}, {targetPickupPos.Y:F2}, {targetPickupPos.Z:F2})");
			}
			log.Information($"[ChauffeurMode] [WORKFLOW] Step 3: Navigating to quester at ({targetPickupPos.X:F2}, {targetPickupPos.Y:F2}, {targetPickupPos.Z:F2})");
			NavigateToPosition(targetPickupPos);
			log.Information("[ChauffeurMode] [WORKFLOW] Monitoring distance to quester for arrival...");
			DateTime arrivalTimeout = DateTime.Now.AddSeconds(45.0);
			bool hasStoppedNearQuester = false;
			float currentDist = 999f;
			while (DateTime.Now < arrivalTimeout && !cancellationToken.IsCancellationRequested)
			{
				TaskCompletionSource<(Vector3? helperPos, float dist)> posCheckTask = new TaskCompletionSource<(Vector3?, float)>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						IPlayerCharacter localPlayer2 = objectTable.LocalPlayer;
						if (localPlayer2 != null)
						{
							float item3 = Vector3.Distance(localPlayer2.Position, targetPickupPos);
							posCheckTask.SetResult((localPlayer2.Position, item3));
						}
						else
						{
							posCheckTask.SetResult((null, 999f));
						}
					}
					catch
					{
						posCheckTask.SetResult((null, 999f));
					}
				});
				(Vector3?, float) obj = await posCheckTask.Task;
				Vector3? item = obj.Item1;
				float item2 = obj.Item2;
				currentDist = item2;
				if (currentDist <= 1.5f && !hasStoppedNearQuester)
				{
					log.Information($"[ChauffeurMode] [WORKFLOW] Within proximity ({currentDist:F2}y <= 1.5y) - Stopping navigation");
					framework.RunOnFrameworkThread(delegate
					{
						vnavmeshIPC.StopPathfinding();
					});
					hasStoppedNearQuester = true;
				}
				if (currentDist <= 5f && item.HasValue)
				{
					float num2 = Math.Abs(item.Value.Y - targetPickupPos.Y);
					if (!(num2 > 3f))
					{
						log.Information($"[ChauffeurMode] [WORKFLOW] Arrived at quester (Distance: {currentDist:F2}y, Vertical: {num2:F2}y)");
						break;
					}
					Vector3 lowerTarget = new Vector3(targetPickupPos.X, targetPickupPos.Y + 0.5f, targetPickupPos.Z);
					log.Warning($"[ChauffeurMode] [WORKFLOW] Too high! Vertical distance: {num2:F2}y (max 2.0y) - forcing descent to Y={lowerTarget.Y:F2}");
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							string content = $"/vnav flyto {lowerTarget.X:F2} {lowerTarget.Y:F2} {lowerTarget.Z:F2}";
							commandManager.ProcessCommand(content);
						}
						catch (Exception ex2)
						{
							log.Error("[ChauffeurMode] [WORKFLOW] Error sending descent command: " + ex2.Message);
						}
					});
					await Task.Delay(1000, cancellationToken);
				}
				await Task.Delay(250, cancellationToken);
			}
			if (currentDist > 5f)
			{
				log.Error($"[ChauffeurMode] [WORKFLOW] PICKUP FAILED! Stuck at {currentDist:F2}y from quester - ABORTING");
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						if (objectTable.LocalPlayer != null)
						{
							crossProcessIPC.SendChauffeurAborted(questerName, questerWorld);
							log.Information($"[ChauffeurMode] [WORKFLOW] Abort signal sent to {questerName}@{questerWorld}");
						}
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Error sending abort signal: " + ex2.Message);
					}
				});
				framework.RunOnFrameworkThread(() => commandManager.ProcessCommand("/vnav stop"));
				isTransportingQuester = false;
				config.CurrentHelperStatus = HelperStatus.Available;
				config.AssignedQuester = "";
				config.Save();
				log.Warning("[ChauffeurMode] [WORKFLOW] Helper workflow aborted - quester can continue without helper");
				return;
			}
			if (cancellationToken.IsCancellationRequested)
			{
				log.Information("[ChauffeurMode] [WORKFLOW] Workflow cancelled before party formation");
				framework.RunOnFrameworkThread(delegate
				{
					ResetHelperTransportState();
				});
				return;
			}
			log.Information("[ChauffeurMode] [WORKFLOW] Step 4: Ensuring party formation");
			log.Information("[ChauffeurMode] [HELPER] Enabling auto-accept for party invites");
			partyInviteAutoAccept.EnableAutoAccept();
			log.Information("[ChauffeurMode] [HELPER] Auto-accept enabled - will accept Quester's invite");
			bool inParty = false;
			for (int partyAttempt = 0; partyAttempt < 10; partyAttempt++)
			{
				TaskCompletionSource<bool> partyCheckTask = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						bool result = partyList.Length > 0;
						partyCheckTask.SetResult(result);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Error checking party: " + ex2.Message);
						partyCheckTask.SetResult(result: false);
					}
				});
				inParty = await partyCheckTask.Task;
				if (inParty)
				{
					log.Information($"[ChauffeurMode] [WORKFLOW] Party formed! ({partyList.Length} members)");
					break;
				}
				log.Information($"[ChauffeurMode] [HELPER] Sending invite to {questerName}@{WorldNameHelper.GetWorldName(questerWorld)} (attempt {partyAttempt + 1}/10)");
				string myHelperName = "Unknown";
				ushort myHelperWorld = 0;
				framework.RunOnFrameworkThread(delegate
				{
					myHelperName = objectTable.LocalPlayer?.Name.ToString() ?? "Unknown";
					myHelperWorld = (ushort)(objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
				});
				log.Information("[ChauffeurMode] [HELPER] Signaling Ready For Pickup to " + questerName);
				crossProcessIPC.SendChauffeurReadyForPickup(myHelperName, myHelperWorld);
				Plugin.Instance?.GetLANHelperServer()?.SendChauffeurReadyForPickup(questerName, questerWorld);
				TaskCompletionSource<bool> inviteTask = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						bool flag6 = partyInviteService.InviteToParty(questerName, questerWorld);
						log.Information($"[ChauffeurMode] [HELPER] Invite result: {flag6}");
						inviteTask.SetResult(flag6);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [WORKFLOW] Error inviting: " + ex2.Message);
						inviteTask.SetResult(result: false);
					}
				});
				await inviteTask.Task;
				await Task.Delay(2000);
			}
			if (!inParty)
			{
				log.Error("[ChauffeurMode] [WORKFLOW] Failed to form party after 10 attempts (20s)");
				log.Error("[ChauffeurMode] [WORKFLOW] Resetting helper state");
				ResetChauffeurState();
				return;
			}
			log.Information("[ChauffeurMode] [HELPER] ========================================");
			log.Information("[ChauffeurMode] [HELPER] === SIGNALING MOUNT READY ===");
			log.Information("[ChauffeurMode] [HELPER] ========================================");
			log.Information("[ChauffeurMode] [HELPER] Sending mount ready signal to: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
			IPluginLog pluginLog = log;
			pluginLog.Information($"[ChauffeurMode] [HELPER] Helper is mounted: {await IsMountedAsync()}");
			log.Information($"[ChauffeurMode] [HELPER] Helper position: ({objectTable.LocalPlayer?.Position.X:F2}, {objectTable.LocalPlayer?.Position.Y:F2}, {objectTable.LocalPlayer?.Position.Z:F2})");
			crossProcessIPC.SendChauffeurMountReady(questerName, questerWorld);
			log.Information("[ChauffeurMode] [HELPER] Mount ready signal sent via IPC");
			isPassengerMounted = false;
			log.Information("[ChauffeurMode] [WORKFLOW] Waiting for quester to mount (max 30s)...");
			DateTime mountWaitStart = DateTime.Now;
			bool passengerMounted = false;
			while ((DateTime.Now - mountWaitStart).TotalSeconds < 30.0)
			{
				if (isPassengerMounted)
				{
					passengerMounted = true;
					log.Information("[ChauffeurMode] [WORKFLOW] âœ“ Passenger mount signal received!");
					break;
				}
				if ((DateTime.Now - mountWaitStart).TotalSeconds % 5.0 < 0.1)
				{
					crossProcessIPC.SendChauffeurMountReady(questerName, questerWorld);
					Plugin.Instance?.GetLANHelperServer()?.SendChauffeurMountReady(questerName, questerWorld);
				}
				await Task.Delay(100);
			}
			if (passengerMounted)
			{
				await Task.Delay(500);
			}
			else
			{
				log.Warning("[ChauffeurMode] [WORKFLOW] Timed out waiting for passenger to mount. Proceeding anyway (checking distance)...");
			}
			log.Information($"[ChauffeurMode] [WORKFLOW] Step 6: Transporting to target ({finalTargetPos.X:F2}, {finalTargetPos.Y:F2}, {finalTargetPos.Z:F2})");
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			bool transportSuccess = false;
			for (int partyAttempt = 0; partyAttempt < 5; partyAttempt++)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				log.Information($"[ChauffeurMode] [TRANSPORT] === ATTEMPT {partyAttempt + 1}/5 ===");
				if (!(await IsMountedAsync()))
				{
					log.Warning("[ChauffeurMode] [TRANSPORT] Helper is not mounted! Remounting...");
					if (!(await MountUp()))
					{
						log.Error("[ChauffeurMode] [TRANSPORT] Failed to remount.");
						continue;
					}
					await Task.Delay(1000);
				}
				isTransportingQuester = true;
				transportSuccess = await NavigateToPositionWithPassengerMonitoring(finalTargetPos, questerPos, questerName, questerWorld, cancellationToken);
				isTransportingQuester = false;
				if (transportSuccess)
				{
					log.Information("[ChauffeurMode] [TRANSPORT] Transport successful!");
					break;
				}
				log.Warning("[ChauffeurMode] [TRANSPORT] Transport failed or aborted. Retrying...");
				await Task.Delay(2000);
			}
			if (!transportSuccess)
			{
				log.Error("[ChauffeurMode] [HELPER] Transport FAILED/ABORTED after multiple attempts - Stopping workflow cleanup");
				await PerformTransportCleanup(success: false);
				return;
			}
			log.Information("[ChauffeurMode] [HELPER] Arrived at destination");
			log.Information("[ChauffeurMode] [HELPER] Dismounting at destination");
			for (int partyAttempt = 0; partyAttempt < 3; partyAttempt++)
			{
				TaskCompletionSource<bool> checkMountTask2 = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					bool result = condition[ConditionFlag.Mounted];
					checkMountTask2.SetResult(result);
				});
				if (!(await checkMountTask2.Task))
				{
					log.Information("[ChauffeurMode] [HELPER] Already dismounted");
					break;
				}
				log.Information($"[ChauffeurMode] [HELPER] Dismount attempt {partyAttempt + 1}/3");
				await framework.RunOnFrameworkThread(delegate
				{
					ActionManager* ptr = ActionManager.Instance();
					if (ptr != null)
					{
						ptr->UseAction(ActionType.Mount, 0u, 3758096384uL, 0u, ActionManager.UseActionMode.None, 0u, null);
						log.Information("[ChauffeurMode] [HELPER] Dismount action executed via ActionManager");
					}
					else
					{
						log.Error("[ChauffeurMode] [HELPER] ActionManager is null!");
					}
				});
				await Task.Delay(2000);
			}
			TaskCompletionSource<bool> dismountTask = new TaskCompletionSource<bool>();
			framework.RunOnFrameworkThread(delegate
			{
				bool flag6 = condition[ConditionFlag.Mounted];
				log.Information($"[ChauffeurMode] [HELPER] After dismount - Still mounted: {flag6}");
				dismountTask.SetResult(!flag6);
			});
			await dismountTask.Task;
			isTransportingQuester = false;
			hasExecutedRidePillion = false;
			isFollowingQuester = false;
			followingQuesterName = null;
			lastQuesterPosition = null;
			lastQuesterZone = 0u;
			lastTransportEndTime = DateTime.Now;
			config.AssignedQuester = "";
			config.CurrentHelperStatus = HelperStatus.Available;
			config.Save();
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer != null)
			{
				string helperName = localPlayer.Name.ToString();
				ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
				crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "Available");
			}
			log.Information("[ChauffeurMode] [HELPER] Transport complete - FLAGS RESET + STATUS AVAILABLE (before notification)");
			log.Information("[ChauffeurMode] [HELPER] Notifying Quester of arrival: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
			crossProcessIPC.SendChauffeurArrived(questerName, questerWorld);
			LANHelperServer lANHelperServer = Plugin.Instance?.GetLANHelperServer();
			if (lANHelperServer != null)
			{
				log.Information("[ChauffeurMode] [HELPER] Also sending arrival via LAN to connected clients");
				lANHelperServer.SendChauffeurArrived(questerName, questerWorld);
			}
			log.Information("[ChauffeurMode] [HELPER] Waiting for quester to restart Questionable and checking for AttuneAetheryte task...");
			await Task.Delay(3000);
			bool isAttuneAetheryteTask = false;
			await framework.RunOnFrameworkThread(delegate
			{
				try
				{
					StepData currentStepData = questionableIPC.GetCurrentStepData();
					if (currentStepData != null && string.Equals(currentStepData.InteractionType, "AttuneAetheryte", StringComparison.Ordinal))
					{
						isAttuneAetheryteTask = true;
					}
				}
				catch (Exception ex2)
				{
					log.Error("[ChauffeurMode] [HELPER] Error checking quester task: " + ex2.Message);
				}
			});
			if (isAttuneAetheryteTask && targetPosition.HasValue)
			{
				log.Information("[ChauffeurMode] [HELPER] AttuneAetheryte detected - flying 10 yalms away from target before dismount");
				Vector3 vector3 = Vector3.Normalize(await framework.RunOnFrameworkThread(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero) - targetPosition.Value);
				Vector3 flyAwayPosition = targetPosition.Value + vector3 * 10f;
				log.Information($"[ChauffeurMode] [HELPER] Flying to position 10 yalms away: ({flyAwayPosition.X:F2}, {flyAwayPosition.Y:F2}, {flyAwayPosition.Z:F2})");
				await framework.RunOnFrameworkThread(delegate
				{
					try
					{
						string text = $"/vnav flyto {flyAwayPosition.X:F2} {flyAwayPosition.Y:F2} {flyAwayPosition.Z:F2}";
						commandManager.ProcessCommand(text);
						log.Information("[ChauffeurMode] [HELPER] Sent vnav flyto command: " + text);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [HELPER] Failed to send vnav flyto command: " + ex2.Message);
					}
				});
				DateTime timeout = DateTime.Now.AddSeconds(10.0);
				while (DateTime.Now < timeout)
				{
					float num3 = Vector3.Distance(await framework.RunOnFrameworkThread(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero), targetPosition.Value);
					if (num3 >= 10f)
					{
						log.Information($"[ChauffeurMode] [HELPER] Successfully flew away (distance: {num3:F2} yalms)");
						break;
					}
					await Task.Delay(500);
				}
				await framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/vnav stop");
				});
				await Task.Delay(1000);
			}
			log.Information("[ChauffeurMode] [HELPER] Disbanding party");
			await framework.RunOnFrameworkThread(delegate
			{
				memoryHelper.SendChatMessage("/leave");
				log.Information("[ChauffeurMode] [HELPER] /leave command sent via UIModule");
			});
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Helper workflow error: " + ex.Message);
			log.Error("[ChauffeurMode] Stack trace: " + ex.StackTrace);
			bool flag5 = helperWorkflowCts != null && !helperWorkflowCts.IsCancellationRequested;
			if (ex is TaskCanceledException && flag5)
			{
				log.Information("[ChauffeurMode] [HELPER] Workflow cancelled but new workflow already running (re-summon) - skipping cleanup");
				return;
			}
			framework.RunOnFrameworkThread(delegate
			{
				ResetHelperTransportState();
			});
		}
		unsafe async Task PerformTransportCleanup(bool success)
		{
			log.Information($"[ChauffeurMode] [HELPER] Executing cleanup (Success={success})");
			if (!success)
			{
				log.Warning("[ChauffeurMode] [HELPER] Transport failed/aborted. Sending ABORT signal to " + questerName);
				crossProcessIPC.SendChauffeurAborted(questerName, questerWorld);
				(Plugin.Instance?.GetLANHelperServer())?.SendChauffeurAborted(questerName, questerWorld);
			}
			for (int i = 0; i < 3; i++)
			{
				TaskCompletionSource<bool> checkMountTask3 = new TaskCompletionSource<bool>();
				framework.RunOnFrameworkThread(delegate
				{
					bool result = condition[ConditionFlag.Mounted];
					checkMountTask3.SetResult(result);
				});
				if (!(await checkMountTask3.Task))
				{
					log.Information("[ChauffeurMode] [HELPER] Already dismounted (or dismount successful)");
					break;
				}
				if (i == 0)
				{
					log.Information("[ChauffeurMode] [HELPER] Dismounting...");
				}
				await framework.RunOnFrameworkThread(delegate
				{
					ActionManager* ptr = ActionManager.Instance();
					if (ptr != null)
					{
						ptr->UseAction(ActionType.Mount, 0u, 3758096384uL, 0u, ActionManager.UseActionMode.None, 0u, null);
						log.Information($"[ChauffeurMode] [HELPER] Dismount attempt {i + 1}/3");
					}
				});
				await Task.Delay(2000);
			}
			log.Information("[ChauffeurMode] [HELPER] Disbanding party");
			await framework.RunOnFrameworkThread(delegate
			{
				memoryHelper.SendChatMessage("/leave");
			});
			await Task.Delay(500);
			isTransportingQuester = false;
			hasExecutedRidePillion = false;
			isFollowingQuester = false;
			followingQuesterName = null;
			lastQuesterPosition = null;
			lastQuesterZone = 0u;
			config.AssignedQuester = "";
			config.CurrentHelperStatus = HelperStatus.Available;
			config.Save();
			framework.RunOnFrameworkThread(delegate
			{
				IPlayerCharacter localPlayer2 = objectTable.LocalPlayer;
				if (localPlayer2 != null)
				{
					string helperName2 = localPlayer2.Name.ToString();
					ushort helperWorld2 = (ushort)localPlayer2.HomeWorld.RowId;
					crossProcessIPC.BroadcastHelperStatus(helperName2, helperWorld2, "Available");
					log.Information("[ChauffeurMode] [HELPER] Cleanup Complete - Status set to Available");
				}
			});
		}
	}

	private string? FindNearestAetheryteInZone()
	{
		TaskCompletionSource<Vector3?> playerPosTask = new TaskCompletionSource<Vector3?>();
		TaskCompletionSource<uint> territoryIdTask = new TaskCompletionSource<uint>();
		framework.RunOnFrameworkThread(delegate
		{
			try
			{
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				if (localPlayer != null)
				{
					playerPosTask.SetResult(localPlayer.Position);
					territoryIdTask.SetResult(clientState.TerritoryType);
				}
				else
				{
					playerPosTask.SetResult(null);
					territoryIdTask.SetResult(0u);
				}
			}
			catch (Exception ex2)
			{
				log.Error("[ChauffeurMode] Error getting player position for aetheryte search: " + ex2.Message);
				playerPosTask.SetResult(null);
				territoryIdTask.SetResult(0u);
			}
		});
		Vector3? result = playerPosTask.Task.Result;
		uint result2 = territoryIdTask.Task.Result;
		if (!result.HasValue || result2 == 0)
		{
			return null;
		}
		try
		{
			ExcelSheet<Lumina.Excel.Sheets.Aetheryte> excelSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
			if (excelSheet == null)
			{
				log.Warning("[ChauffeurMode] Could not load Aetheryte sheet");
				return null;
			}
			float num = float.MaxValue;
			string text = null;
			foreach (Lumina.Excel.Sheets.Aetheryte item in excelSheet)
			{
				if (item.Territory.RowId != result2 || !item.IsAetheryte)
				{
					continue;
				}
				PlaceName? valueNullable = item.PlaceName.ValueNullable;
				if (!valueNullable.HasValue)
				{
					continue;
				}
				string text2 = valueNullable.Value.Name.ExtractText();
				if (string.IsNullOrWhiteSpace(text2))
				{
					continue;
				}
				float num2 = 0f;
				float num3 = 0f;
				bool flag = false;
				foreach (RowRef<Level> item2 in item.Level)
				{
					if (item2.IsValid)
					{
						Level value = item2.Value;
						if (value.Territory.RowId == result2)
						{
							num2 = value.X;
							num3 = value.Z;
							flag = true;
							break;
						}
					}
				}
				if (!flag)
				{
					Lumina.Excel.Sheets.Map? valueNullable2 = item.Map.ValueNullable;
					if (valueNullable2.HasValue)
					{
						_ = (float)(int)valueNullable2.Value.SizeFactor / 100f;
						_ = valueNullable2.Value.OffsetX;
						_ = valueNullable2.Value.OffsetY;
						log.Warning("[ChauffeurMode] Aetheryte " + text2 + " has no Level data - skipping precise distance check (Map fallback not reliable)");
					}
				}
				if (flag)
				{
					float num4 = result.Value.X - num2;
					float num5 = result.Value.Z - num3;
					float num6 = (float)Math.Sqrt(num4 * num4 + num5 * num5);
					log.Information($"[ChauffeurMode] Candidate Aetheryte: {text2} | Pos: ({num2:F1}, {num3:F1}) | Dist: {num6:F1}");
					if (num6 < num)
					{
						num = num6;
						text = text2;
					}
				}
			}
			if (text != null)
			{
				log.Information($"[ChauffeurMode] Found nearest aetheryte: {text} ({num:F2} yalms away)");
			}
			return text;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Error finding nearest aetheryte: " + ex.Message);
			return null;
		}
	}

	private async Task<bool> TeleportToZone(uint zoneId)
	{
		try
		{
			ExcelSheet<TerritoryType> excelSheet = dataManager.GetExcelSheet<TerritoryType>();
			if (excelSheet == null)
			{
				return false;
			}
			TerritoryType? rowOrDefault = excelSheet.GetRowOrDefault(zoneId);
			if (!rowOrDefault.HasValue)
			{
				return false;
			}
			uint rowId = rowOrDefault.Value.Aetheryte.RowId;
			if (rowId == 0)
			{
				log.Warning($"[ChauffeurMode] No aetheryte found for zone {zoneId}");
				return false;
			}
			ExcelSheet<Lumina.Excel.Sheets.Aetheryte> excelSheet2 = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
			if (excelSheet2 == null)
			{
				log.Warning("[ChauffeurMode] Could not load Aetheryte sheet");
				return false;
			}
			Lumina.Excel.Sheets.Aetheryte? rowOrDefault2 = excelSheet2.GetRowOrDefault(rowId);
			if (!rowOrDefault2.HasValue)
			{
				log.Warning($"[ChauffeurMode] Aetheryte {rowId} not found");
				return false;
			}
			string value = rowOrDefault2.Value.PlaceName.ValueNullable?.Name.ToString() ?? "";
			if (string.IsNullOrEmpty(value))
			{
				log.Warning($"[ChauffeurMode] Aetheryte {rowId} has no name");
				return false;
			}
			string text = rowOrDefault.Value.PlaceName.ValueNullable?.Name.ToString() ?? "";
			string mappedName = MapTerritoryName(text);
			log.Information($"[ChauffeurMode] Teleporting to {mappedName} (Territory: {text}, Aetheryte: {value}, ID: {rowId})");
			TaskCompletionSource<bool> tpTask = new TaskCompletionSource<bool>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					commandManager.ProcessCommand("/li " + mappedName);
					tpTask.SetResult(result: true);
				}
				catch (Exception ex2)
				{
					log.Error("[ChauffeurMode] Error teleporting: " + ex2.Message);
					tpTask.SetResult(result: false);
				}
			});
			await tpTask.Task;
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Teleport error: " + ex.Message);
			return false;
		}
	}

	private async Task<bool> TeleportToAetheryte(string aetheryteName, uint targetZoneId)
	{
		try
		{
			if (string.IsNullOrEmpty(aetheryteName))
			{
				return false;
			}
			log.Information("[ChauffeurMode] Teleporting to aetheryte: " + aetheryteName);
			framework.RunOnFrameworkThread(delegate
			{
				commandManager.ProcessCommand("/li \"" + aetheryteName + "\"");
			});
			for (int attempts = 0; attempts < 20; attempts++)
			{
				if (clientState.IsGPosing)
				{
					break;
				}
				if (clientState.IsPvP)
				{
					break;
				}
				if (Plugin.ObjectTable.LocalPlayer?.IsCasting ?? false)
				{
					log.Debug("[ChauffeurMode] Casting teleport...");
					break;
				}
				await Task.Delay(500);
			}
			log.Information("[ChauffeurMode] Waiting for teleport to complete...");
			await Task.Delay(5000);
			for (int attempts = 0; attempts < 60; attempts++)
			{
				if (clientState.TerritoryType == targetZoneId)
				{
					log.Information($"[ChauffeurMode] Arrived in target zone: {targetZoneId}");
					await Task.Delay(2000);
					return true;
				}
				await Task.Delay(500);
			}
			log.Warning($"[ChauffeurMode] Timed out waiting for zone change to {targetZoneId}");
			return false;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] Error executing teleport: " + ex.Message);
			return false;
		}
	}

	private unsafe bool SummonMountDirect(uint mountId)
	{
		try
		{
			log.Information($"[ChauffeurMode] [MOUNT] Summoning mount ID: {mountId}");
			ActionManager* ptr = ActionManager.Instance();
			if (ptr == null)
			{
				log.Error("[ChauffeurMode] [MOUNT] ActionManager is null");
				return false;
			}
			bool flag = ptr->UseAction(ActionType.Mount, mountId, 3758096384uL, 0u, ActionManager.UseActionMode.None, 0u, null);
			log.Information($"[ChauffeurMode] [MOUNT] ActionManager.UseAction result: {flag}");
			if (!flag)
			{
				log.Warning("[ChauffeurMode] [MOUNT] ActionManager failed, trying command fallback");
				string mountName = GetMountName(mountId);
				if (!string.IsNullOrEmpty(mountName))
				{
					commandManager.ProcessCommand("/mount \"" + mountName + "\"");
					log.Information("[ChauffeurMode] [MOUNT] Command sent: /mount \"" + mountName + "\"");
					return true;
				}
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] [MOUNT] Exception: " + ex.Message);
			log.Error("[ChauffeurMode] [MOUNT] StackTrace: " + ex.StackTrace);
			return false;
		}
	}

	private string GetMountName(uint mountId)
	{
		try
		{
			ExcelSheet<Mount> excelSheet = dataManager.GetExcelSheet<Mount>();
			if (excelSheet == null)
			{
				return "";
			}
			return excelSheet.GetRowOrDefault(mountId)?.Singular.ToString() ?? "";
		}
		catch
		{
			return "";
		}
	}

	private async Task<bool> IsMountedAsync()
	{
		TaskCompletionSource<bool> task = new TaskCompletionSource<bool>();
		framework.RunOnFrameworkThread(delegate
		{
			try
			{
				if (objectTable.LocalPlayer == null)
				{
					task.SetResult(result: false);
				}
				else
				{
					bool result = condition[ConditionFlag.Mounted];
					task.SetResult(result);
				}
			}
			catch (Exception ex)
			{
				log.Error("[ChauffeurMode] IsMounted error: " + ex.Message);
				task.SetResult(result: false);
			}
		});
		return await task.Task;
	}

	private bool IsMounted()
	{
		try
		{
			if (objectTable.LocalPlayer == null)
			{
				return false;
			}
			return condition[ConditionFlag.Mounted];
		}
		catch
		{
			return false;
		}
	}

	private async Task NavigateToPosition(Vector3 targetPos)
	{
		try
		{
			log.Information("[ChauffeurMode] [NAV] ========================================");
			log.Information($"[ChauffeurMode] [NAV] Starting navigation to ({targetPos.X:F2}, {targetPos.Y:F2}, {targetPos.Z:F2})");
			log.Information($"[ChauffeurMode] [NAV] Thread ID: {Thread.CurrentThread.ManagedThreadId}");
			TaskCompletionSource<bool> stopNavTask = new TaskCompletionSource<bool>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					vnavmeshIPC?.StopPathfinding();
					log.Information("[ChauffeurMode] [NAV] Stopped any existing navigation");
					stopNavTask.SetResult(result: true);
				}
				catch (Exception ex2)
				{
					log.Error("[ChauffeurMode] [NAV] Error stopping nav: " + ex2.Message);
					stopNavTask.SetResult(result: false);
				}
			});
			await stopNavTask.Task;
			await Task.Delay(500);
			TaskCompletionSource<bool> flyTask = new TaskCompletionSource<bool>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					bool flag2 = vnavmeshIPC?.PathfindAndMoveTo(targetPos) ?? false;
					if (flag2)
					{
						log.Information($"[ChauffeurMode] [NAV] IPC PathfindAndMoveTo started successfully to ({targetPos.X:F2}, {targetPos.Y:F2}, {targetPos.Z:F2})");
					}
					else
					{
						log.Error("[ChauffeurMode] [NAV] IPC PathfindAndMoveTo failed to start");
					}
					flyTask.SetResult(flag2);
				}
				catch (Exception ex2)
				{
					log.Error("[ChauffeurMode] [NAV] IPC error: " + ex2.Message);
					flyTask.SetResult(result: false);
				}
			});
			await flyTask.Task;
			await Task.Delay(1000);
			DateTime startTime = DateTime.Now;
			TimeSpan timeout = TimeSpan.FromMinutes(5L);
			DateTime lastLogTime = DateTime.Now;
			int stuckCounter = 0;
			float lastDistance = float.MaxValue;
			while (DateTime.Now - startTime < timeout)
			{
				if (!isTransportingQuester)
				{
					log.Information("[ChauffeurMode] [NAV] Transport cancelled, stopping navigation");
					TaskCompletionSource<bool> cancelTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							vnavmeshIPC?.StopPathfinding();
							cancelTask.SetResult(result: true);
						}
						catch
						{
							cancelTask.SetResult(result: false);
						}
					});
					await cancelTask.Task;
					return;
				}
				TaskCompletionSource<Vector3?> posTask = new TaskCompletionSource<Vector3?>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						IPlayerCharacter localPlayer = objectTable.LocalPlayer;
						posTask.SetResult(localPlayer?.Position);
					}
					catch (Exception ex2)
					{
						log.Error("[ChauffeurMode] [NAV] Error getting player position: " + ex2.Message);
						posTask.SetResult(null);
					}
				});
				Vector3? vector = await posTask.Task;
				if (!vector.HasValue)
				{
					log.Warning("[ChauffeurMode] [NAV] Could not get player position");
					break;
				}
				float num = Vector3.Distance(vector.Value, targetPos);
				bool flag = vnavmeshIPC?.IsPathfinding() ?? false;
				if (Math.Abs(num - lastDistance) < 1f)
				{
					if (!flag)
					{
						stuckCounter++;
						if (stuckCounter > 120)
						{
							log.Warning($"[ChauffeurMode] [NAV] Stuck at distance {num:F2}, aborting");
							break;
						}
					}
					else
					{
						if (stuckCounter > 0)
						{
							log.Information($"[ChauffeurMode] [NAV] Still pathfinding (distance: {num:F2}y, stuck counter reset)");
						}
						stuckCounter = 0;
					}
				}
				else
				{
					stuckCounter = 0;
				}
				lastDistance = num;
				float num2 = Math.Clamp(config.ChauffeurStopDistance, 2f, 15f);
				if (num < num2)
				{
					log.Information($"Arrived at destination, distance {num:F2} yalms");
					TaskCompletionSource<bool> arrivedTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							vnavmeshIPC?.StopPathfinding();
							log.Information("[ChauffeurMode] [NAV] Navigation stopped");
							arrivedTask.SetResult(result: true);
						}
						catch
						{
							arrivedTask.SetResult(result: false);
						}
					});
					await arrivedTask.Task;
					return;
				}
				if ((DateTime.Now - lastLogTime).TotalSeconds >= 5.0)
				{
					log.Information($"[ChauffeurMode] [NAV] Distance to target: {num:F2} yalms");
					lastLogTime = DateTime.Now;
				}
				await Task.Delay(1000);
			}
			log.Warning("[ChauffeurMode] [NAV] Navigation timeout");
			TaskCompletionSource<bool> timeoutTask = new TaskCompletionSource<bool>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					commandManager.ProcessCommand("/vnav stop");
					log.Information("[ChauffeurMode] [NAV] Navigation stopped (timeout)");
					timeoutTask.SetResult(result: true);
				}
				catch
				{
					timeoutTask.SetResult(result: false);
				}
			});
			await timeoutTask.Task;
		}
		catch (Exception ex)
		{
			log.Error("[ChauffeurMode] [NAV] Navigation error: " + ex.Message);
			log.Error("[ChauffeurMode] [NAV] StackTrace: " + ex.StackTrace);
		}
	}

	private async Task<bool> WaitForPathfindingAsync(CancellationToken token, int timeoutMs = 30000)
	{
		_ = 3;
		try
		{
			log.Information("[ChauffeurMode] Waiting for pathfinding...");
			await Task.Delay(250, token);
			bool hasStarted = false;
			for (int i = 0; i < 50; i++)
			{
				if (token.IsCancellationRequested)
				{
					return false;
				}
				if (vnavmeshIPC.IsPathfinding())
				{
					hasStarted = true;
					break;
				}
				await Task.Delay(100, token);
			}
			if (!hasStarted)
			{
				log.Debug("[ChauffeurMode] IsPathfinding never became true (fast path or calc failed?)");
				return true;
			}
			Stopwatch sw = Stopwatch.StartNew();
			while (sw.ElapsedMilliseconds < timeoutMs)
			{
				if (token.IsCancellationRequested)
				{
					return false;
				}
				if (!vnavmeshIPC.IsPathfinding())
				{
					log.Information($"[ChauffeurMode] Pathfinding finished in {sw.Elapsed.TotalSeconds:F2}s");
					await Task.Delay(500, token);
					return true;
				}
				await Task.Delay(200, token);
			}
			log.Error($"[ChauffeurMode] Pathfinding timed out after {timeoutMs / 1000}s");
			return false;
		}
		catch (TaskCanceledException)
		{
			return false;
		}
		catch (Exception ex2)
		{
			log.Error("[ChauffeurMode] Error in WaitForPathfinding: " + ex2.Message);
			return false;
		}
	}

	private unsafe async Task<bool> MountUp()
	{
		if (await IsMountedAsync())
		{
			return true;
		}
		log.Information($"[ChauffeurMode] Mounting up (Mount ID: {config.ChauffeurMountId})...");
		await framework.RunOnFrameworkThread(delegate
		{
			ActionManager* ptr = ActionManager.Instance();
			if (ptr != null)
			{
				ptr->UseAction(ActionType.Mount, config.ChauffeurMountId, 3758096384uL, 0u, ActionManager.UseActionMode.None, 0u, null);
			}
		});
		for (int i = 0; i < 50; i++)
		{
			await Task.Delay(100);
			if (await IsMountedAsync())
			{
				return true;
			}
		}
		return false;
	}

	private async Task<bool> NavigateToPositionWithPassengerMonitoring(Vector3 targetPos, Vector3 questerStartPos, string questerName, ushort questerWorld, CancellationToken token)
	{
		float arrivalThreshold = Math.Clamp(config.ChauffeurStopDistance, 2f, 3f);
		int stuckCounter = 0;
		float lastDistance = 0f;
		int questerMissingCounter = 0;
		int helperStuckCounter = 0;
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			if (token.IsCancellationRequested)
			{
				return false;
			}
			log.Information($"[ChauffeurMode] [TRANSPORT] === ATTEMPT {attempt}/{5} ===");
			log.Information("[ChauffeurMode] [TRANSPORT] Starting navigation to target");
			NavigateToPosition(targetPos);
			if (!(await WaitForPathfindingAsync(token, 45000)))
			{
				log.Error("[ChauffeurMode] [TRANSPORT] Pathfinding failed/timeout. Aborting transport attempt.");
				continue;
			}
			await Task.Delay(2000, token);
			TaskCompletionSource<Vector3?> helperStartPosTask = new TaskCompletionSource<Vector3?>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					Vector3? result = objectTable.LocalPlayer?.Position;
					helperStartPosTask.SetResult(result);
				}
				catch
				{
					helperStartPosTask.SetResult(null);
				}
			});
			Vector3? helperStartPos = await helperStartPosTask.Task;
			if (!helperStartPos.HasValue)
			{
				log.Error("[ChauffeurMode] [TRANSPORT] Could not get helper position");
				continue;
			}
			log.Information($"[ChauffeurMode] [TRANSPORT] Helper start: ({helperStartPos.Value.X:F2}, {helperStartPos.Value.Y:F2}, {helperStartPos.Value.Z:F2})");
			log.Information("[ChauffeurMode] [TRANSPORT] Waiting 2 seconds to check if helper moved...");
			await Task.Delay(2000, token);
			TaskCompletionSource<Vector3?> helperCurrentPosTask = new TaskCompletionSource<Vector3?>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					Vector3? result = objectTable.LocalPlayer?.Position;
					helperCurrentPosTask.SetResult(result);
				}
				catch
				{
					helperCurrentPosTask.SetResult(null);
				}
			});
			Vector3? helperCurrentPos = await helperCurrentPosTask.Task;
			bool questerMovingWithUs = true;
			bool helperIsStuck = false;
			TaskCompletionSource<Vector3?> verifyQuesterPosTask = new TaskCompletionSource<Vector3?>();
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					IGameObject gameObject = objectTable.FirstOrDefault((IGameObject o) => o.Name.ToString() == questerName);
					if (gameObject != null)
					{
						verifyQuesterPosTask.SetResult(gameObject.Position);
					}
					else
					{
						verifyQuesterPosTask.SetResult(null);
					}
				}
				catch
				{
					verifyQuesterPosTask.SetResult(null);
				}
			});
			Vector3? verifyQuesterPos = await verifyQuesterPosTask.Task;
			if (!verifyQuesterPos.HasValue && lastQuesterPosition.HasValue && followingQuesterName == questerName && (DateTime.Now - lastQuesterPositionTime).TotalSeconds < 15.0)
			{
				verifyQuesterPos = lastQuesterPosition;
			}
			if (helperCurrentPos.HasValue)
			{
				float num = Vector3.Distance(helperStartPos.Value, helperCurrentPos.Value);
				log.Information($"[ChauffeurMode] [TRANSPORT] Helper moved {num:F2} yalms after measurement interval");
				if (num < 3f)
				{
					helperStuckCounter++;
					log.Warning($"[ChauffeurMode] [TRANSPORT] â\u009dŒ Helper barely moved ({num:F2}y) - stuck counter: {helperStuckCounter}/3");
					helperIsStuck = true;
					if (helperStuckCounter >= 3)
					{
						log.Error($"[ChauffeurMode] [TRANSPORT] â\u009dŒ Helper stuck against obstacle after {helperStuckCounter} attempts - ABORTING TRANSPORT");
						framework.RunOnFrameworkThread(delegate
						{
							try
							{
								if (objectTable.LocalPlayer != null)
								{
									crossProcessIPC.SendChauffeurAborted(questerName, questerWorld);
									log.Information($"[ChauffeurMode] [TRANSPORT] Abort signal sent to {questerName}@{questerWorld}");
								}
							}
							catch (Exception ex)
							{
								log.Error("[ChauffeurMode] [TRANSPORT] Error sending abort signal: " + ex.Message);
							}
						});
						return false;
					}
				}
				else if (verifyQuesterPos.HasValue)
				{
					float num2 = Vector3.Distance(helperCurrentPos.Value, verifyQuesterPos.Value);
					log.Information($"[ChauffeurMode] [TRANSPORT] Distance to Quester: {num2:F2} yalms");
					if (num2 > 30f)
					{
						log.Warning($"[ChauffeurMode] [TRANSPORT] â\u009dŒ Helper moved but Quester stayed behind! Distance: {num2:F2}y");
						questerMovingWithUs = false;
					}
					else
					{
						log.Information($"[ChauffeurMode] [TRANSPORT] âœ“ Helper moved AND Quester is close ({num2:F2}y) - Success!");
						helperStuckCounter = 0;
						questerMovingWithUs = true;
					}
				}
				else
				{
					log.Warning("[ChauffeurMode] [TRANSPORT] âš\u00a0ï\u00b8\u008f Helper moved but Quester position unknown! Assuming lost/lag.");
					questerMovingWithUs = false;
				}
			}
			if (helperIsStuck)
			{
				log.Information("[ChauffeurMode] [TRANSPORT] Helper stuck/delayed - adjusting and retrying navigation...");
				continue;
			}
			if (!questerMovingWithUs)
			{
				log.Warning("[ChauffeurMode] [TRANSPORT] Quester failed to stay on mount - returning to quester position");
				framework.RunOnFrameworkThread(delegate
				{
					vnavmeshIPC.StopPathfinding();
				});
				await Task.Delay(1000, token);
				log.Information("[ChauffeurMode] [TRANSPORT] Returning to quester for retry...");
				Vector3 targetPos2 = questerStartPos;
				if (verifyQuesterPos.HasValue)
				{
					targetPos2 = verifyQuesterPos.Value;
					log.Information($"[ChauffeurMode] [TRANSPORT] Using LIVE quester position for retry: ({targetPos2.X:F2}, {targetPos2.Y:F2}, {targetPos2.Z:F2})");
				}
				else if (lastQuesterPosition.HasValue && followingQuesterName == questerName && (DateTime.Now - lastQuesterPositionTime).TotalSeconds < 30.0)
				{
					targetPos2 = lastQuesterPosition.Value;
					log.Information($"[ChauffeurMode] [TRANSPORT] Using CACHED LAN quester position for retry: ({targetPos2.X:F2}, {targetPos2.Y:F2}, {targetPos2.Z:F2})");
				}
				else
				{
					log.Warning("[ChauffeurMode] [TRANSPORT] Could not find live quester position - using original start position");
				}
				await NavigateToPosition(targetPos2);
				await Task.Delay(2000);
				await framework.RunOnFrameworkThread(delegate
				{
					log.Information("[ChauffeurMode] [TRANSPORT] Signaling mount ready for retry...");
					crossProcessIPC.SendChauffeurMountReady(questerName, questerWorld);
					LANHelperServer lANHelperServer = Plugin.Instance?.GetLANHelperServer();
					if (lANHelperServer != null)
					{
						log.Information("[ChauffeurMode] [TRANSPORT] also sending mount ready via LAN for retry");
						lANHelperServer.SendChauffeurMountReady(questerName, questerWorld);
					}
				});
				DateTime waitStart = DateTime.Now;
				TimeSpan waitMax = TimeSpan.FromSeconds(30L);
				bool mounted = false;
				while (DateTime.Now - waitStart < waitMax)
				{
					TaskCompletionSource<bool> checkTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							bool result = condition[ConditionFlag.RidingPillion];
							checkTask.SetResult(result);
						}
						catch
						{
							checkTask.SetResult(result: false);
						}
					});
					if (await checkTask.Task)
					{
						log.Information("[ChauffeurMode] [TRANSPORT] âœ“ Quester mounted for retry!");
						mounted = true;
						break;
					}
					await Task.Delay(1000);
				}
				if (!mounted)
				{
					log.Error("[ChauffeurMode] [TRANSPORT] Quester failed to mount after retry - aborting");
					return false;
				}
				log.Information("[ChauffeurMode] [TRANSPORT] âœ“ Quester remounted successfully - continuing transport");
			}
			log.Information("[ChauffeurMode] [TRANSPORT] âœ“ Quester confirmed on mount - continuing to destination");
			DateTime arrivalStart = DateTime.Now;
			TimeSpan arrivalTimeout = TimeSpan.FromMinutes(5L);
			while (DateTime.Now - arrivalStart < arrivalTimeout)
			{
				if (!isTransportingQuester)
				{
					log.Information("[ChauffeurMode] [TRANSPORT] Transport cancelled");
					TaskCompletionSource<bool> cancelTask = new TaskCompletionSource<bool>();
					framework.RunOnFrameworkThread(delegate
					{
						try
						{
							commandManager.ProcessCommand("/vnav stop");
							cancelTask.SetResult(result: true);
						}
						catch
						{
							cancelTask.SetResult(result: false);
						}
					});
					await cancelTask.Task;
					return false;
				}
				TaskCompletionSource<Vector3?> posTask = new TaskCompletionSource<Vector3?>();
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						Vector3? result = objectTable.LocalPlayer?.Position;
						posTask.SetResult(result);
					}
					catch
					{
						posTask.SetResult(null);
					}
				});
				Vector3? currentPos = await posTask.Task;
				if (currentPos.HasValue)
				{
					float distance = Vector3.Distance(currentPos.Value, targetPos);
					Vector3? currentQuesterPos = null;
					bool questerFound = false;
					await framework.RunOnFrameworkThread(delegate
					{
						try
						{
							IGameObject gameObject = objectTable.FirstOrDefault((IGameObject o) => o.Name.ToString() == questerName);
							if (gameObject != null)
							{
								currentQuesterPos = gameObject.Position;
								questerFound = true;
							}
						}
						catch
						{
						}
					});
					if (!questerFound && lastQuesterPosition.HasValue && followingQuesterName == questerName && (DateTime.Now - lastQuesterPositionTime).TotalSeconds < 15.0)
					{
						currentQuesterPos = lastQuesterPosition;
						questerFound = true;
					}
					if (questerFound && currentQuesterPos.HasValue)
					{
						float num3 = Vector3.Distance(currentPos.Value, currentQuesterPos.Value);
						if (num3 > 35f)
						{
							log.Warning($"[ChauffeurMode] [TRANSPORT] â\u009dŒ LOST QUESTER! Distance: {num3:F2}y > 35y");
							log.Warning($"[ChauffeurMode] [TRANSPORT] Quester Pos: {currentQuesterPos.Value} vs Helper: {currentPos.Value}");
							await framework.RunOnFrameworkThread(delegate
							{
								commandManager.ProcessCommand("/vnav stop");
							});
							questerMovingWithUs = false;
							break;
						}
					}
					if (!questerFound)
					{
						questerMissingCounter++;
						if (questerMissingCounter >= 5)
						{
							log.Warning("[ChauffeurMode] [TRANSPORT] â\u009dŒ LOST QUESTER! Missing for 5+ seconds (LAN/ObjectTable)");
							await framework.RunOnFrameworkThread(delegate
							{
								commandManager.ProcessCommand("/vnav stop");
							});
							questerMovingWithUs = false;
							break;
						}
					}
					else
					{
						questerMissingCounter = 0;
					}
					if (lastDistance > 0f && Math.Abs(distance - lastDistance) < 1f)
					{
						stuckCounter++;
						if (stuckCounter >= 5)
						{
							log.Warning($"[ChauffeurMode] [TRANSPORT] Stuck for 5 seconds at distance {distance:F2}");
							log.Information("[ChauffeurMode] [TRANSPORT] Moving 5 yalms backwards to unstuck");
							await framework.RunOnFrameworkThread(delegate
							{
								commandManager.ProcessCommand("/vnav stop");
							});
							await Task.Delay(500);
							Vector3 vector = Vector3.Normalize(currentPos.Value - targetPos);
							Vector3 backwardsPos = currentPos.Value + vector * 5f;
							log.Information($"[ChauffeurMode] [TRANSPORT] Moving to backwards position: ({backwardsPos.X:F2}, {backwardsPos.Y:F2}, {backwardsPos.Z:F2})");
							await framework.RunOnFrameworkThread(delegate
							{
								commandManager.ProcessCommand($"/vnav {backwardsPos.X} {backwardsPos.Y} {backwardsPos.Z}");
							});
							await Task.Delay(3000);
							await framework.RunOnFrameworkThread(delegate
							{
								commandManager.ProcessCommand("/vnav stop");
							});
							log.Information("[ChauffeurMode] [TRANSPORT] Unstuck complete, considering arrived");
							return true;
						}
					}
					else
					{
						stuckCounter = 0;
					}
					lastDistance = distance;
					if (distance <= arrivalThreshold)
					{
						log.Information($"[ChauffeurMode] [TRANSPORT] Arrived at destination (distance: {distance:F2} yalms, threshold: {arrivalThreshold:F1})");
						TaskCompletionSource<bool> arrivedTask = new TaskCompletionSource<bool>();
						framework.RunOnFrameworkThread(delegate
						{
							try
							{
								commandManager.ProcessCommand("/vnav stop");
								arrivedTask.SetResult(result: true);
							}
							catch
							{
								arrivedTask.SetResult(result: false);
							}
						});
						await arrivedTask.Task;
						return true;
					}
				}
				await Task.Delay(1000);
			}
			if (!questerMovingWithUs)
			{
				log.Warning("[ChauffeurMode] [TRANSPORT] Transport failed (lost quester) - retrying...");
				continue;
			}
			log.Warning("[ChauffeurMode] [TRANSPORT] Arrival timeout - but quester was on mount, so considering it success");
			return true;
		}
		log.Error($"[ChauffeurMode] [TRANSPORT] Failed after {5} attempts - giving up");
		ResetChauffeurState();
		return false;
	}

	public void OnChauffeurReadyForPickup(string questerName, ushort questerWorld)
	{
		framework.RunOnFrameworkThread(delegate
		{
			if (config.ChauffeurModeEnabled && config.IsQuester)
			{
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				if (localPlayer != null && !(localPlayer.Name.ToString() != questerName) && localPlayer.HomeWorld.RowId == questerWorld)
				{
					string helperName = "Unknown";
					ushort helperWorld = 0;
					if (!string.IsNullOrEmpty(config.PreferredHelper))
					{
						string[] array = config.PreferredHelper.Split('@');
						if (array.Length >= 1)
						{
							helperName = array[0];
						}
						if (array.Length >= 2 && ushort.TryParse(array[1], out var result))
						{
							helperWorld = result;
						}
					}
					else
					{
						Plugin? instance = Plugin.Instance;
						if (instance != null && instance.LANHelperClient?.DiscoveredHelpers.Any() == true)
						{
							LANHelperInfo? obj = Plugin.Instance.LANHelperClient.DiscoveredHelpers.FirstOrDefault((LANHelperInfo h) => h.Status == LANHelperStatus.Available) ?? Plugin.Instance.LANHelperClient.DiscoveredHelpers.First();
							helperName = obj.Name;
							helperWorld = obj.WorldId;
						}
					}
					OnChauffeurReadyForPickupInternal(helperName, helperWorld);
				}
			}
		});
	}

	public void OnChauffeurReadyForPickupInternal(string helperName, ushort helperWorld)
	{
		if (!config.ChauffeurModeEnabled || !config.IsQuester || !isWaitingForHelper)
		{
			return;
		}
		log.Information("[ChauffeurMode] ========================================");
		log.Information("[ChauffeurMode] === HELPER READY FOR PICKUP ===");
		log.Information("[ChauffeurMode] ========================================");
		log.Information("[ChauffeurMode] Helper " + helperName + " reported ready for pickup");
		log.Information("[ChauffeurMode] [QUESTER] Sending Party Invite to Helper");
		framework.RunOnFrameworkThread(delegate
		{
			log.Information("[ChauffeurMode] [QUESTER] Triggering InviteToParty for " + helperName);
			if (helperManager != null)
			{
				LANHelperInfo lANHelperInfo = (Plugin.Instance?.LANHelperClient)?.DiscoveredHelpers.FirstOrDefault((LANHelperInfo h) => h.Name == helperName);
				if (lANHelperInfo != null)
				{
					log.Information($"[ChauffeurMode] [QUESTER] Found LAN IP for {helperName}: {lANHelperInfo.IPAddress} - Using InviteLANHelper");
					helperManager.InviteLANHelper(lANHelperInfo.IPAddress, helperName, lANHelperInfo.WorldId);
				}
				else
				{
					partyInviteService.InviteToParty(helperName, helperWorld);
				}
			}
			else
			{
				commandManager.ProcessCommand("/invite \"" + helperName + "\"");
			}
		});
	}

	public unsafe void OnChauffeurMountReady(string questerName, ushort questerWorld)
	{
		framework.RunOnFrameworkThread(delegate
		{
			if (config.ChauffeurModeEnabled && config.IsQuester && isWaitingForHelper)
			{
				if (hasExecutedRidePillion)
				{
					log.Information("[ChauffeurMode] [QUESTER] Resetting RidePillion flag for new mount attempt");
					hasExecutedRidePillion = false;
				}
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				if (localPlayer != null)
				{
					string myName = localPlayer.Name.ToString();
					ushort myWorld = (ushort)localPlayer.HomeWorld.RowId;
					if (myName != questerName || myWorld != questerWorld)
					{
						log.Debug($"[ChauffeurMode] [QUESTER] Mount ready signal is for {questerName}@{WorldNameHelper.GetWorldName(questerWorld)}, not for me ({myName}@{myWorld}) - ignoring");
					}
					else
					{
						log.Information("[ChauffeurMode] ========================================");
						log.Information("[ChauffeurMode] === MOUNT READY FOR RIDEPILLION ===");
						log.Information("[ChauffeurMode] ========================================");
						log.Information($"[ChauffeurMode] [QUESTER] This signal is for ME: {myName}@{myWorld}");
						hasExecutedRidePillion = true;
						Task.Run(async delegate
						{
							_ = 12;
							try
							{
								TaskCompletionSource<(bool mounted, bool isPassenger)> mountCheckTask = new TaskCompletionSource<(bool, bool)>();
								framework.RunOnFrameworkThread(delegate
								{
									try
									{
										bool flag3 = IsMounted();
										bool flag4 = condition[ConditionFlag.RidingPillion];
										log.Information($"[ChauffeurMode] [QUESTER] Currently mounted: {flag3}, Passenger: {flag4}");
										mountCheckTask.SetResult((flag3, flag4));
									}
									catch (Exception ex2)
									{
										log.Error("[ChauffeurMode] [QUESTER] Error checking mount: " + ex2.Message);
										mountCheckTask.SetResult((false, false));
									}
								});
								var (flag, flag2) = await mountCheckTask.Task;
								if (flag && !flag2)
								{
									log.Information("[ChauffeurMode] [QUESTER] Mounted on OWN mount - dismounting before RidePillion");
									for (int i = 0; i < 3; i++)
									{
										log.Information($"[ChauffeurMode] [QUESTER] Dismount attempt {i + 1}/3 using ActionManager");
										await framework.RunOnFrameworkThread(delegate
										{
											ActionManager* ptr = ActionManager.Instance();
											if (ptr != null)
											{
												ptr->UseAction(ActionType.Mount, 0u, 3758096384uL, 0u, ActionManager.UseActionMode.None, 0u, null);
												log.Information("[ChauffeurMode] [QUESTER] Dismount action executed via ActionManager");
											}
											else
											{
												log.Error("[ChauffeurMode] [QUESTER] ActionManager is null!");
											}
										});
										await Task.Delay(2000);
										TaskCompletionSource<bool> checkTask = new TaskCompletionSource<bool>();
										framework.RunOnFrameworkThread(delegate
										{
											bool flag3 = condition[ConditionFlag.Mounted];
											log.Information($"[ChauffeurMode] [QUESTER] After dismount attempt {i + 1} - Still mounted: {flag3}");
											checkTask.SetResult(flag3);
										});
										if (!(await checkTask.Task))
										{
											log.Information("[ChauffeurMode] [QUESTER] Successfully dismounted!");
											break;
										}
									}
								}
								else
								{
									log.Information("[ChauffeurMode] [QUESTER] Not mounted, no dismount needed");
								}
								log.Information("[ChauffeurMode] [QUESTER] Finding Helper in party using IPartyList (with retry)");
								TaskCompletionSource<string?> helperNameTask = new TaskCompletionSource<string>();
								framework.RunOnFrameworkThread(delegate
								{
									Task.Run(async delegate
									{
										_ = 1;
										try
										{
											string foundHelperName = null;
											int attempt;
											for (attempt = 1; attempt <= 10; attempt++)
											{
												await framework.RunOnFrameworkThread(delegate
												{
													IPlayerCharacter localPlayer2 = objectTable.LocalPlayer;
													if (localPlayer2 != null)
													{
														string text = localPlayer2.Name.ToString();
														if (partyList != null && partyList.Length > 0)
														{
															if (attempt == 1 || attempt == 10)
															{
																log.Information($"[ChauffeurMode] [QUESTER] Party check attempt {attempt}/10 - Size: {partyList.Length}");
															}
															for (int j = 0; j < partyList.Length; j++)
															{
																IPartyMember partyMember = partyList[j];
																if (partyMember != null)
																{
																	string text2 = partyMember.Name.ToString();
																	if (text2 != text)
																	{
																		if (attempt > 1)
																		{
																			log.Information($"[ChauffeurMode] [QUESTER] Found Helper in party on attempt {attempt}: {text2}");
																		}
																		else
																		{
																			log.Information("[ChauffeurMode] [QUESTER] Found Helper in party: " + text2);
																		}
																		foundHelperName = text2;
																		break;
																	}
																}
															}
														}
													}
												});
												if (foundHelperName != null)
												{
													break;
												}
												if (attempt < 10)
												{
													await Task.Delay(500);
												}
											}
											if (foundHelperName == null)
											{
												log.Warning("[ChauffeurMode] [QUESTER] No Helper found in party after 10 attempts (5s)");
											}
											helperNameTask.SetResult(foundHelperName);
										}
										catch (Exception ex2)
										{
											log.Error("[ChauffeurMode] [QUESTER] Error finding Helper: " + ex2.Message);
											helperNameTask.SetResult(null);
										}
									});
								});
								string helperName = await helperNameTask.Task;
								if (string.IsNullOrEmpty(helperName))
								{
									log.Error("[ChauffeurMode] [QUESTER] Cannot execute RidePillion - Helper not found in party");
								}
								else
								{
									log.Information("[ChauffeurMode] [QUESTER] Targeting Helper: " + helperName);
									await framework.RunOnFrameworkThread(delegate
									{
										TargetSystem* ptr = TargetSystem.Instance();
										if (ptr == null)
										{
											log.Error("[ChauffeurMode] [QUESTER] TargetSystem is null!");
										}
										else
										{
											if (partyList != null)
											{
												for (int j = 0; j < partyList.Length; j++)
												{
													IPartyMember partyMember = partyList[j];
													if (partyMember != null && partyMember.Name.ToString() == helperName)
													{
														IGameObject gameObject = partyMember.GameObject;
														if (gameObject != null)
														{
															ptr->Target = (GameObject*)gameObject.Address;
															log.Information("[ChauffeurMode] [QUESTER] Targeted Helper via TargetSystem: " + helperName);
															return;
														}
													}
												}
											}
											log.Warning("[ChauffeurMode] [QUESTER] Could not find Helper GameObject to target");
										}
									});
									await Task.Delay(1000);
									log.Information("[ChauffeurMode] [QUESTER] ========================================");
									log.Information("[ChauffeurMode] [QUESTER] === EXECUTING RIDEPILLION ===");
									log.Information("[ChauffeurMode] [QUESTER] ========================================");
									log.Information("[ChauffeurMode] [QUESTER] Helper name: " + helperName);
									log.Information($"[ChauffeurMode] [QUESTER] Party size: {partyList.Length}");
									for (int i2 = 0; i2 < 3; i2++)
									{
										log.Information($"[ChauffeurMode] [QUESTER] --- RidePillion attempt {i2 + 1}/3 ---");
										await framework.RunOnFrameworkThread(delegate
										{
											log.Information("[ChauffeurMode] [QUESTER] Searching for Helper in party...");
											if (partyList != null)
											{
												for (int j = 0; j < partyList.Length; j++)
												{
													IPartyMember partyMember = partyList[j];
													if (partyMember != null)
													{
														string text = partyMember.Name.ToString();
														log.Information($"[ChauffeurMode] [QUESTER] Party member {j}: {text}");
														if (text == helperName)
														{
															log.Information("[ChauffeurMode] [QUESTER] Found Helper: " + helperName);
															IGameObject gameObject = partyMember.GameObject;
															if (gameObject != null)
															{
																log.Information($"[ChauffeurMode] [QUESTER] Helper GameObject address: 0x{gameObject.Address:X}");
																log.Information($"[ChauffeurMode] [QUESTER] Helper ObjectKind: {gameObject.ObjectKind}");
																log.Information($"[ChauffeurMode] [QUESTER] Helper Position: ({gameObject.Position.X:F2}, {gameObject.Position.Y:F2}, {gameObject.Position.Z:F2})");
																BattleChara* address = (BattleChara*)gameObject.Address;
																log.Information($"[ChauffeurMode] [QUESTER] BattleChara pointer: 0x{(nint)address:X}");
																log.Information("[ChauffeurMode] [QUESTER] Calling MemoryHelper.ExecuteRidePillion(battleChara, 10)...");
																bool value = memoryHelper.ExecuteRidePillion(address);
																log.Information($"[ChauffeurMode] [QUESTER] RidePillion Memory call result: {value}");
																return;
															}
															log.Warning("[ChauffeurMode] [QUESTER] Helper GameObject is NULL!");
														}
													}
												}
											}
											log.Warning("[ChauffeurMode] [QUESTER] Could not find Helper in party to execute RidePillion");
										});
										await Task.Delay(2000);
									}
									bool isRiding = false;
									DateTime passengerCheckStart = DateTime.Now;
									while ((DateTime.Now - passengerCheckStart).TotalSeconds < 5.0)
									{
										TaskCompletionSource<bool> mountedTask = new TaskCompletionSource<bool>();
										framework.RunOnFrameworkThread(delegate
										{
											bool result = condition[ConditionFlag.RidingPillion];
											mountedTask.SetResult(result);
										});
										isRiding = await mountedTask.Task;
										if (isRiding)
										{
											log.Information($"[ChauffeurMode] [QUESTER] Successfully mounted as passenger! (Condition 10: true after {(DateTime.Now - passengerCheckStart).TotalSeconds:F1}s)");
											break;
										}
										await Task.Delay(500);
									}
									if (isRiding)
									{
										log.Information("[ChauffeurMode] [QUESTER] Sending mounted signal to Helper");
										TaskCompletionSource<bool> signalTask = new TaskCompletionSource<bool>();
										framework.RunOnFrameworkThread(delegate
										{
											try
											{
												crossProcessIPC.SendChauffeurPassengerMounted(myName, myWorld);
												log.Information("[ChauffeurMode] [QUESTER] Passenger mounted signal sent");
												signalTask.SetResult(result: true);
											}
											catch (Exception ex2)
											{
												log.Error("[ChauffeurMode] [QUESTER] Signal error: " + ex2.Message);
												signalTask.SetResult(result: false);
											}
										});
										await signalTask.Task;
										if (helperManager != null)
										{
											LANHelperClient lANHelperClient = Plugin.Instance?.LANHelperClient;
											if (lANHelperClient != null)
											{
												LANHelperInfo lANHelperInfo = lANHelperClient.DiscoveredHelpers.FirstOrDefault((LANHelperInfo h) => h.Name == helperName);
												if (lANHelperInfo != null)
												{
													log.Information($"[ChauffeurMode] [QUESTER] Sending LAN Passenger Mounted signal to {helperName} ({lANHelperInfo.IPAddress})");
													await lANHelperClient.SendChauffeurPassengerMountedAsync(lANHelperInfo.IPAddress, myName, myWorld);
												}
											}
										}
										isWaitingForHelper = false;
										log.Information("[ChauffeurMode] [QUESTER] âœ“ Transport started - isWaitingForHelper reset to false");
									}
									else
									{
										log.Warning("[ChauffeurMode] [QUESTER] RidePillion may have failed - not detected as passenger");
									}
									log.Information("[ChauffeurMode] [QUESTER] Waiting for transport to complete...");
									log.Information("[ChauffeurMode] [QUESTER] Movement Monitor is stopped during transport");
								}
							}
							catch (Exception ex)
							{
								log.Error("[ChauffeurMode] [QUESTER] Error during mount ready: " + ex.Message);
							}
						});
					}
				}
			}
		});
	}

	private void OnChauffeurPassengerMounted(string questerName, ushort questerWorld)
	{
		framework.RunOnFrameworkThread(delegate
		{
			if (config.IsHelperAutomationActive && (isTransportingQuester || isWaitingForHelper))
			{
				string text = questerName + "@" + WorldNameHelper.GetWorldName(questerWorld);
				if (config.AssignedQuester != text)
				{
					log.Debug("[ChauffeurMode] [HELPER] Ignoring mounted signal from " + text + " - assigned to " + config.AssignedQuester);
				}
				else
				{
					log.Information("[ChauffeurMode] [HELPER] âœ“ Valid passenger mounted signal received from " + text);
					isPassengerMounted = true;
				}
			}
		});
	}

	public void OnLANPassengerMounted(string questerName, ushort questerWorld)
	{
		if (!config.IsHelperAutomationActive)
		{
			return;
		}
		if (this.questerName == questerName)
		{
			log.Information("[ChauffeurMode] [HELPER] LAN Passenger Mounted Signal received from " + questerName);
			OnChauffeurPassengerMounted(questerName, questerWorld);
			return;
		}
		log.Debug($"[ChauffeurMode] [HELPER] LAN Passenger Mounted Signal from {questerName} ignored (Expected: {this.questerName})");
		if (!string.IsNullOrEmpty(config.AssignedQuester) && config.AssignedQuester.StartsWith(questerName))
		{
			OnChauffeurPassengerMounted(questerName, questerWorld);
		}
	}

	private void OnHelperStatusUpdate(string helperName, ushort helperWorld, string status)
	{
		if (!config.IsQuester)
		{
			return;
		}
		ExcelSheet<World> excelSheet = dataManager.GetExcelSheet<World>();
		string text = "Unknown";
		if (excelSheet != null)
		{
			foreach (World item in excelSheet)
			{
				if (item.RowId == helperWorld)
				{
					text = item.Name.ExtractText();
					break;
				}
			}
		}
		string text2 = helperName + "@" + text;
		helperStatuses[text2] = status;
		log.Debug("[ChauffeurMode] [QUESTER] Helper status updated: " + text2 + " = " + status);
	}

	private void BroadcastHelperStatusPeriodically()
	{
		if (isDisposed)
		{
			log.Debug("[ChauffeurMode] [HELPER] Periodic broadcast stopped (service disposed)");
			return;
		}
		if (!config.IsHelperAutomationActive)
		{
			helperStatusBroadcastActive = false;
			return;
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer != null)
		{
			try
			{
				string helperName = localPlayer.Name.ToString();
				ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
				string text = config.CurrentHelperStatus switch
				{
					HelperStatus.Available => "Available", 
					HelperStatus.Transporting => "Transporting", 
					HelperStatus.InDungeon => "InDungeon", 
					_ => "Available", 
				};
				crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, text);
				log.Debug("[ChauffeurMode] [HELPER] Periodic status broadcast: " + text);
			}
			catch (ObjectDisposedException)
			{
				log.Debug("[ChauffeurMode] [HELPER] Periodic broadcast stopped (IPC disposed)");
				return;
			}
			catch (Exception ex2)
			{
				log.Error("[ChauffeurMode] [HELPER] Error in periodic broadcast: " + ex2.Message);
			}
		}
		if (!isDisposed && helperStatusBroadcastActive)
		{
			framework.RunOnTick(delegate
			{
				BroadcastHelperStatusPeriodically();
			}, TimeSpan.FromSeconds(10L));
		}
	}

	public void OnChauffeurArrived(string questerName, ushort questerWorld)
	{
		framework.RunOnFrameworkThread(delegate
		{
			if (config.ChauffeurModeEnabled && config.IsQuester)
			{
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				if (localPlayer != null)
				{
					string text = localPlayer.Name.ToString();
					ushort num = (ushort)localPlayer.HomeWorld.RowId;
					if (text != questerName || num != questerWorld)
					{
						log.Debug($"[ChauffeurMode] [QUESTER] Arrived signal is for {questerName}@{WorldNameHelper.GetWorldName(questerWorld)}, not for me ({text}@{num}) - ignoring");
					}
					else
					{
						log.Information("[ChauffeurMode] ========================================");
						log.Information("[ChauffeurMode] === HELPER ARRIVED AT FINAL DESTINATION ===");
						log.Information("[ChauffeurMode] ========================================");
						log.Information($"[ChauffeurMode] [QUESTER] Signal confirmed for me: {text}@{num}");
						if (helperManager != null)
						{
							LANHelperClient lANHelperClient = Plugin.Instance.LANHelperClient;
							if (lANHelperClient != null)
							{
								IReadOnlyList<LANHelperInfo> discoveredHelpers = lANHelperClient.DiscoveredHelpers;
								LANHelperInfo lANHelperInfo = discoveredHelpers.FirstOrDefault((LANHelperInfo h) => h.Status == LANHelperStatus.Available) ?? discoveredHelpers.FirstOrDefault();
								if (lANHelperInfo != null)
								{
									bool flag = false;
									if (partyList != null)
									{
										foreach (IPartyMember party in partyList)
										{
											if (party.Name.ToString() == lANHelperInfo.Name && party.World.RowId == lANHelperInfo.WorldId)
											{
												flag = true;
												break;
											}
										}
									}
									if (flag)
									{
										log.Information("[ChauffeurMode] [QUESTER] Helper " + lANHelperInfo.Name + " already in party - skipping invite to avoid double disband");
									}
									else
									{
										log.Information($"[ChauffeurMode] [QUESTER] Helper arrived - Sending INVITE to {lANHelperInfo.Name}@{lANHelperInfo.WorldId} ({lANHelperInfo.IPAddress})");
										helperManager.InviteLANHelper(lANHelperInfo.IPAddress, lANHelperInfo.Name, lANHelperInfo.WorldId);
									}
								}
								else
								{
									log.Warning("[ChauffeurMode] [QUESTER] Helper arrived but no LAN helper found in discovery list to invite!");
								}
							}
						}
						Task.Run(async delegate
						{
							_ = 6;
							try
							{
								TaskCompletionSource<bool> isMountedTask = new TaskCompletionSource<bool>();
								framework.RunOnFrameworkThread(delegate
								{
									try
									{
										bool flag2 = IsMounted();
										log.Information($"[ChauffeurMode] [QUESTER] Currently mounted: {flag2}");
										isMountedTask.SetResult(flag2);
									}
									catch (Exception ex2)
									{
										log.Error("[ChauffeurMode] [QUESTER] Error checking mount: " + ex2.Message);
										isMountedTask.SetResult(result: false);
									}
								});
								if (await isMountedTask.Task)
								{
									log.Information("[ChauffeurMode] [QUESTER] Dismounting from RidePillion (Condition 10 active)");
									TaskCompletionSource<bool> dismountTask = new TaskCompletionSource<bool>();
									framework.RunOnFrameworkThread(delegate
									{
										try
										{
											commandManager.ProcessCommand("/ridepillion");
											log.Information("[ChauffeurMode] [QUESTER] /ridepillion command sent to dismount");
											dismountTask.SetResult(result: true);
										}
										catch (Exception ex2)
										{
											log.Error("[ChauffeurMode] [QUESTER] Command error: " + ex2.Message);
											dismountTask.SetResult(result: false);
										}
									});
									await dismountTask.Task;
									log.Information("[ChauffeurMode] [QUESTER] Waiting 3 seconds for dismount...");
									await Task.Delay(3000);
									TaskCompletionSource<bool> verifyTask = new TaskCompletionSource<bool>();
									framework.RunOnFrameworkThread(delegate
									{
										bool flag2 = condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion];
										log.Information($"[ChauffeurMode] [QUESTER] After dismount - Still mounted: {flag2}");
										verifyTask.SetResult(!flag2);
									});
									await verifyTask.Task;
								}
								else
								{
									log.Information("[ChauffeurMode] [QUESTER] Not mounted as passenger, skipping dismount");
								}
								log.Information("[ChauffeurMode] [QUESTER] Leaving party");
								await framework.RunOnFrameworkThread(delegate
								{
									memoryHelper.SendChatMessage("/leave");
									log.Information("[ChauffeurMode] [QUESTER] /leave command sent via UIModule");
								});
								await Task.Delay(2500);
								log.Information("[ChauffeurMode] [QUESTER] ========================================");
								log.Information("[ChauffeurMode] [QUESTER] === RESUMING QUESTIONABLE ===");
								log.Information("[ChauffeurMode] [QUESTER] ========================================");
								await framework.RunOnFrameworkThread(delegate
								{
									ResetChauffeurState();
									if (Plugin.Instance.MovementMonitor != null && config.EnableMovementMonitor && questionableIPC.IsRunning())
									{
										Plugin.Instance.MovementMonitor.StartMonitoring();
										log.Information("[ChauffeurMode] [QUESTER] Movement Monitor resumed");
									}
									else
									{
										log.Information("[ChauffeurMode] [QUESTER] Movement Monitor NOT resumed (disabled or rotation stopped)");
									}
									commandManager.ProcessCommand("/qst start");
									log.Information("[ChauffeurMode] [QUESTER] Sent /qst start command to resume automation");
									log.Information("[ChauffeurMode] Chauffeur transport complete - FLAGS RESET");
								});
							}
							catch (Exception ex)
							{
								log.Error("[ChauffeurMode] [QUESTER] Error during arrival: " + ex.Message);
							}
						});
					}
				}
			}
		});
	}

	private void OnTerritoryChanged()
	{
		uint territoryType = clientState.TerritoryType;
		if (!config.ChauffeurModeEnabled)
		{
			return;
		}
		if (isWaitingForHelper && targetZoneId != 0 && targetZoneId != territoryType)
		{
			log.Information($"[ChauffeurMode] Zone changed ({targetZoneId} -> {territoryType}) while waiting for helper, resetting state");
			ResetChauffeurState();
		}
		if (config.IsQuester && !((DateTime.Now - lastZoneUpdate).TotalSeconds < 5.0))
		{
			lastZoneUpdate = DateTime.Now;
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer != null)
			{
				string zoneName = GetZoneName(territoryType);
				log.Information($"[ChauffeurMode] Zone changed: {zoneName} ({territoryType})");
				log.Information("[ChauffeurMode] Territory Load State: Zone change detected, starting 8-second broadcast delay");
				lastZoneChangeTime = DateTime.Now;
				log.Information("[ChauffeurMode] [QUESTER] Sending zone update to helper");
				crossProcessIPC.SendChauffeurZoneUpdate(localPlayer.Name.ToString(), (ushort)localPlayer.HomeWorld.RowId, territoryType, zoneName);
			}
		}
	}

	public void OnChauffeurZoneUpdate(string questerName, ushort questerWorld, uint zoneId, string zoneName)
	{
		if (!config.ChauffeurModeEnabled || !config.IsHelperAutomationActive)
		{
			return;
		}
		log.Debug($"[ChauffeurMode] Zone update received: {questerName}@{WorldNameHelper.GetWorldName(questerWorld)} -> {zoneName} ({zoneId})");
		log.Debug("[ChauffeurMode] Auto-follow disabled, waiting for explicit summon");
		if (isTransportingQuester)
		{
			log.Information("[ChauffeurMode] Quester moved to different zone (" + zoneName + "), cancelling transport");
			ResetChauffeurState();
			framework.RunOnFrameworkThread(delegate
			{
				commandManager.ProcessCommand("/vnav stop");
				log.Information("[ChauffeurMode] Navigation stopped");
			});
		}
	}

	private string GetZoneName(uint territoryId)
	{
		try
		{
			ExcelSheet<TerritoryType> excelSheet = dataManager.GetExcelSheet<TerritoryType>();
			if (excelSheet == null)
			{
				return $"Zone {territoryId}";
			}
			TerritoryType? rowOrDefault = excelSheet.GetRowOrDefault(territoryId);
			if (!rowOrDefault.HasValue)
			{
				return $"Zone {territoryId}";
			}
			return rowOrDefault.Value.PlaceName.ValueNullable?.Name.ToString() ?? $"Zone {territoryId}";
		}
		catch
		{
			return $"Zone {territoryId}";
		}
	}

	private void ResetHelperTransportState()
	{
		log.Warning("[ChauffeurMode] [HELPER] Resetting transport state due to workflow abort");
		isTransportingQuester = false;
		if (config.IsHelperAutomationActive && !string.IsNullOrEmpty(config.AssignedQuester))
		{
			log.Information("[ChauffeurMode] [HELPER] Clearing assigned quester: " + config.AssignedQuester);
			config.AssignedQuester = "";
			config.CurrentHelperStatus = HelperStatus.Available;
			config.Save();
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer != null)
			{
				string helperName = localPlayer.Name.ToString();
				ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
				crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "Available");
			}
		}
	}

	public void ResetChauffeurState()
	{
		log.Warning("[ChauffeurMode] ========================================");
		log.Warning("[ChauffeurMode] === RESETTING CHAUFFEUR STATE ===");
		log.Warning("[ChauffeurMode] ========================================");
		log.Warning($"[ChauffeurMode] IsWaitingForHelper: {isWaitingForHelper}, IsTransportingQuester: {isTransportingQuester}");
		if (helperWorkflowCts != null)
		{
			log.Information("[ChauffeurMode] Cancelling running helper workflow");
			helperWorkflowCts.Cancel();
			helperWorkflowCts.Dispose();
			helperWorkflowCts = null;
		}
		isWaitingForHelper = false;
		isTransportingQuester = false;
		hasExecutedRidePillion = false;
		targetPosition = null;
		targetZoneId = 0u;
		questerName = null;
		isFollowingQuester = false;
		followingQuesterName = null;
		lastQuesterPosition = null;
		lastQuesterZone = 0u;
		StopNavigation();
		if (!config.IsHelperAutomationActive)
		{
			return;
		}
		if (!string.IsNullOrEmpty(config.AssignedQuester))
		{
			log.Information("[ChauffeurMode] [HELPER] Clearing assigned quester: " + config.AssignedQuester);
			config.AssignedQuester = "";
		}
		config.CurrentHelperStatus = HelperStatus.Available;
		config.Save();
		log.Information("[ChauffeurMode] [HELPER] Status: Available");
		framework.RunOnFrameworkThread(delegate
		{
			try
			{
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				if (localPlayer != null)
				{
					string helperName = localPlayer.Name.ToString();
					ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
					crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "Available");
				}
			}
			catch (Exception ex)
			{
				log.Error("[ChauffeurMode] Error broadcasting helper status: " + ex.Message);
			}
		});
	}

	public void CheckHelperFollowing()
	{
		if (!config.EnableHelperFollowing)
		{
			if (isFollowingQuester)
			{
				StopFollowingQuester();
			}
		}
		else
		{
			if (!config.IsHelperAutomationActive)
			{
				return;
			}
			if (string.IsNullOrEmpty(config.AssignedQuesterForFollowing))
			{
				if (isFollowingQuester)
				{
					log.Warning("[HelperFollowing] Stopped - no assigned quester configured!");
					StopFollowingQuester();
				}
				return;
			}
			if (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95])
			{
				log.Debug("[HelperFollowing] Skipping - in duty/dungeon");
				if (isFollowingQuester)
				{
					log.Information("[HelperFollowing] Stopping - entered duty/dungeon");
					StopFollowingQuester();
				}
				return;
			}
			if (isTransportingQuester)
			{
				if (isFollowingQuester)
				{
					log.Information("[HelperFollowing] Stopping - Chauffeur Mode active");
					StopFollowingQuester(stopMovement: false);
				}
				lastTransportEndTime = DateTime.Now;
				return;
			}
			if ((DateTime.Now - lastTransportEndTime).TotalSeconds < 10.0)
			{
				if (isFollowingQuester)
				{
					log.Information("[HelperFollowing] Stopping - post-transport grace period");
					StopFollowingQuester();
				}
				return;
			}
			uint territoryType = clientState.TerritoryType;
			if (restrictedZones.Contains(territoryType))
			{
				log.Debug($"[HelperFollowing] Skipping - in restricted zone {territoryType}");
				if (isFollowingQuester)
				{
					log.Information($"[HelperFollowing] Stopping - entered restricted zone {territoryType}");
					StopFollowingQuester();
				}
				return;
			}
			if (BLACKLISTED_ZONES.Contains(territoryType))
			{
				log.Debug($"[HelperFollowing] Skipping - in blacklisted zone {territoryType}");
				if (isFollowingQuester)
				{
					log.Information($"[HelperFollowing] Stopping - entered blacklisted zone {territoryType}");
					StopFollowingQuester();
				}
				return;
			}
			DateTime now = DateTime.Now;
			if ((now - lastFollowCheck).TotalSeconds < (double)config.HelperFollowCheckInterval)
			{
				return;
			}
			lastFollowCheck = now;
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer == null)
			{
				return;
			}
			if (!lastQuesterPosition.HasValue || lastQuesterZone == 0)
			{
				if (isFollowingQuester)
				{
					log.Information("[HelperFollowing] Stopped - no quester position data");
					StopFollowingQuester();
				}
				return;
			}
			if (restrictedZones.Contains(lastQuesterZone))
			{
				log.Debug($"[HelperFollowing] Skipping - Quester is in restricted zone {lastQuesterZone}");
				if (isFollowingQuester)
				{
					log.Information($"[HelperFollowing] Stopping - Quester entered restricted zone {lastQuesterZone}");
					StopFollowingQuester();
				}
				return;
			}
			if (BLACKLISTED_ZONES.Contains(lastQuesterZone))
			{
				log.Debug($"[HelperFollowing] Skipping - Quester is in blacklisted zone {lastQuesterZone}");
				if (isFollowingQuester)
				{
					log.Information($"[HelperFollowing] Stopping - Quester entered blacklisted zone {lastQuesterZone}");
					StopFollowingQuester();
				}
				return;
			}
			uint questerZone = lastQuesterZone;
			ushort num = lastQuesterWorld;
			if (num != 0)
			{
				uint rowId = localPlayer.CurrentWorld.RowId;
				if (num != rowId)
				{
					log.Information($"[HelperFollowing] Quester on different world ({num} vs {rowId}) - visiting world");
					LifestreamIPC lifestreamIPC = Plugin.Instance?.LifestreamIPC;
					if (lifestreamIPC != null && lifestreamIPC.IsAvailable)
					{
						string text = (dataManager.GetExcelSheet<World>()?.GetRowOrDefault(num))?.Name.ToString() ?? num.ToString();
						log.Information($"[HelperFollowing] Using Lifestream to visit world: {text} ({num})");
						if (lifestreamIPC.ChangeWorldById(num))
						{
							log.Information("[HelperFollowing] World visit initiated to " + text);
						}
						else
						{
							log.Warning("[HelperFollowing] Failed to initiate world visit to " + text);
						}
					}
					else
					{
						log.Warning("[HelperFollowing] Lifestream not available - cannot visit different world");
					}
					return;
				}
			}
			if (questerZone != territoryType)
			{
				if (!IsMountingAllowed(questerZone))
				{
					log.Debug($"[HelperFollowing] Skipping - Quester's zone {questerZone} does not allow mounting/flying");
					if (isFollowingQuester)
					{
						log.Information($"[HelperFollowing] Stopping - Quester entered non-flying zone {questerZone}");
						StopFollowingQuester();
					}
					return;
				}
				log.Information($"[HelperFollowing] Quester in different zone ({questerZone} vs {territoryType}) - teleporting");
				Task.Run(async delegate
				{
					_ = 1;
					try
					{
						if (await TeleportToZone(questerZone))
						{
							log.Information($"[HelperFollowing] Successfully teleported to zone {questerZone}");
							await Task.Delay(5000);
						}
						else
						{
							log.Warning($"[HelperFollowing] Failed to teleport to zone {questerZone}");
						}
					}
					catch (Exception ex)
					{
						log.Error("[HelperFollowing] Error teleporting to zone: " + ex.Message);
					}
				});
				return;
			}
			Vector3 value = lastQuesterPosition.Value;
			float num2 = Vector3.Distance(localPlayer.Position, value);
			if (num2 > config.HelperFollowDistance)
			{
				log.Information($"[HelperFollowing] Distance {num2:F1} > {config.HelperFollowDistance} - navigating to quester");
				if (!condition[ConditionFlag.Mounted] && config.ChauffeurMountId != 0)
				{
					log.Information("[HelperFollowing] Not mounted - summoning Chauffeur mount");
					framework.RunOnFrameworkThread(delegate
					{
						SummonMountDirect(config.ChauffeurMountId);
					});
					return;
				}
				NavigateToQuester(value);
				if (!isFollowingQuester)
				{
					isFollowingQuester = true;
					log.Information("[HelperFollowing] Started following " + followingQuesterName);
				}
			}
			else if (isFollowingQuester)
			{
				StopNavigation();
			}
			lastQuesterPosition = value;
			lastQuesterZone = questerZone;
		}
	}

	private void BroadcastQuesterPosition()
	{
		if (!config.IsQuester || string.IsNullOrEmpty(config.AssignedHelperForFollowing) || !config.EnableHelperFollowing)
		{
			return;
		}
		if (!questionableIPC.IsRunning())
		{
			log.Debug("[HelperFollowing] Skipping broadcast - Questionable not running");
			return;
		}
		if (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95])
		{
			log.Debug("[HelperFollowing] Skipping broadcast - in duty/dungeon");
			return;
		}
		if (lastZoneChangeTime.HasValue)
		{
			double totalSeconds = (DateTime.Now - lastZoneChangeTime.Value).TotalSeconds;
			if (totalSeconds < 8.0)
			{
				log.Debug($"[ChauffeurMode] [HelperFollowing] Territory Load State: Waiting for zone load (elapsed: {totalSeconds:F1}s / 8.0s)");
				return;
			}
			log.Information($"[ChauffeurMode] [HelperFollowing] Territory Load State: Zone load complete ({totalSeconds:F1}s) - resuming position broadcasts");
			lastZoneChangeTime = null;
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return;
		}
		string text = localPlayer.Name.ToString();
		ushort questerWorld = (ushort)localPlayer.HomeWorld.RowId;
		uint territoryType = clientState.TerritoryType;
		Vector3 position = localPlayer.Position;
		if (HasFlyingInZone(territoryType))
		{
			log.Debug($"[HelperFollowing] Quester can fly in zone {territoryType} - blocking position broadcast (Unnecessary)");
			return;
		}
		crossProcessIPC.BroadcastQuesterPosition(text, questerWorld, territoryType, position);
		LANHelperClient lANHelperClient = Plugin.Instance?.GetLANHelperClient();
		if (lANHelperClient != null)
		{
			IReadOnlyList<LANHelperInfo> discoveredHelpers = lANHelperClient.DiscoveredHelpers;
			if (discoveredHelpers.Count > 0)
			{
				LANHelperInfo lANHelperInfo = discoveredHelpers.First();
				lANHelperClient.SendFollowCommandAsync(lANHelperInfo.IPAddress, position.X, position.Y, position.Z, territoryType);
			}
		}
	}

	private void OnQuesterPositionUpdate(string questerName, ushort questerWorld, uint zoneId, Vector3 position)
	{
		framework.RunOnFrameworkThread(delegate
		{
			if (config.IsHelperAutomationActive)
			{
				string worldName = WorldNameHelper.GetWorldName(questerWorld);
				string text = questerName + "@" + worldName;
				discoveredQuesters[text] = DateTime.Now;
				if (config.EnableHelperFollowing && !string.IsNullOrEmpty(config.AssignedQuesterForFollowing) && !(text != config.AssignedQuesterForFollowing))
				{
					lastQuesterPosition = position;
					lastQuesterZone = zoneId;
					lastQuesterWorld = questerWorld;
					followingQuesterName = text;
				}
			}
		});
	}

	private IPartyMember? FindQuesterInParty()
	{
		if (partyList == null || partyList.Length == 0)
		{
			return null;
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return null;
		}
		string text = localPlayer.Name.ToString();
		uint rowId = localPlayer.HomeWorld.RowId;
		for (int i = 0; i < partyList.Length; i++)
		{
			IPartyMember partyMember = partyList[i];
			if (partyMember == null)
			{
				continue;
			}
			string text2 = partyMember.Name.ToString();
			uint rowId2 = partyMember.World.RowId;
			if (!(text2 == text) || rowId2 != rowId)
			{
				if (string.IsNullOrEmpty(config.AssignedQuester))
				{
					log.Debug("[HelperFollowing] No assigned quester - following first party member: " + text2);
					return partyMember;
				}
				string assignedQuester = config.AssignedQuester;
				string text3 = $"{text2}@{rowId2}";
				if (assignedQuester == text3)
				{
					log.Debug("[HelperFollowing] Found assigned quester: " + text2);
					return partyMember;
				}
			}
		}
		return null;
	}

	private void NavigateToQuester(Vector3 position)
	{
		try
		{
			if (!lastFollowingTargetPos.HasValue || !(Vector3.Distance(position, lastFollowingTargetPos.Value) < 5f))
			{
				lastFollowingTargetPos = position;
				framework.RunOnFrameworkThread(delegate
				{
					vnavmeshIPC.PathfindAndMoveTo(position);
				});
			}
		}
		catch (Exception ex)
		{
			log.Error("[HelperFollowing] Error navigating to quester: " + ex.Message);
		}
	}

	private void StopNavigation()
	{
		try
		{
			lastFollowingTargetPos = null;
			framework.RunOnFrameworkThread(delegate
			{
				vnavmeshIPC.StopPathfinding();
			});
		}
		catch (Exception ex)
		{
			log.Error("[HelperFollowing] Error stopping navigation: " + ex.Message);
		}
	}

	private void StopFollowingQuester(bool stopMovement = true)
	{
		if (isFollowingQuester)
		{
			log.Information($"[HelperFollowing] Stopped following {followingQuesterName} (StopMovement: {stopMovement})");
			if (stopMovement)
			{
				StopNavigation();
			}
			isFollowingQuester = false;
			followingQuesterName = null;
			lastQuesterPosition = null;
		}
	}

	public void Dispose()
	{
		isDisposed = true;
		if (isFollowingQuester)
		{
			StopFollowingQuester();
		}
		if (crossProcessIPC != null)
		{
			crossProcessIPC.OnChauffeurSummonRequest -= OnChauffeurSummonRequest;
			crossProcessIPC.OnChauffeurReadyForPickup -= OnChauffeurReadyForPickupInternal;
			crossProcessIPC.OnChauffeurArrived -= OnChauffeurArrived;
			crossProcessIPC.OnChauffeurZoneUpdate -= OnChauffeurZoneUpdate;
			crossProcessIPC.OnChauffeurMountReady -= OnChauffeurMountReady;
			crossProcessIPC.OnChauffeurPassengerMounted -= OnChauffeurPassengerMounted;
			crossProcessIPC.OnHelperStatusUpdate -= OnHelperStatusUpdate;
			crossProcessIPC.OnQuesterPositionUpdate -= OnQuesterPositionUpdate;
			crossProcessIPC.OnChauffeurAborted -= OnChauffeurAborted;
		}
		territoryChangedSubscription?.Dispose();
		territoryChangedSubscription = null;
		if (framework != null)
		{
			framework.Update -= OnFrameworkUpdate;
		}
		log.Information("[ChauffeurMode] Service disposed");
	}

	internal void OnChauffeurAborted(string qName, ushort qWorld)
	{
		if (!config.IsQuester)
		{
			return;
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return;
		}
		string text = localPlayer.Name.ToString();
		ushort num = (ushort)localPlayer.HomeWorld.RowId;
		if (!(qName == text) || qWorld != num)
		{
			return;
		}
		log.Warning("[ChauffeurMode] [QUESTER] Received CHAUFFEUR_ABORTED signal! Transport failed.");
		log.Information("[ChauffeurMode] [QUESTER] Resuming automation via '/qst start'...");
		if (isWaitingForHelper)
		{
			isWaitingForHelper = false;
		}
		framework.RunOnFrameworkThread(delegate
		{
			try
			{
				commandManager.ProcessCommand("/qst start");
			}
			catch (Exception ex)
			{
				log.Error("[ChauffeurMode] [QUESTER] Failed to execute /qst start: " + ex.Message);
			}
		});
	}
}
