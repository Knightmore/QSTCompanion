using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Data.HuntLogs;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;
using QuestionableCompanion.Utils;

namespace QuestionableCompanion.Services;

public sealed class HuntLogAutomationService : IDisposable
{
	private enum CombatBackend
	{
		None,
		Standard,
		FrenRider
	}

	private readonly record struct StandardCombatSelection(bool Rsr, bool Vbm, bool Bmr);

	private enum HuntMovementResult
	{
		Completed,
		StartRejected,
		Stopped,
		NoProgress,
		TimedOut,
		RecoveredFromDeath,
		RecoveredFromCombat
	}

	private readonly record struct HuntMovementOutcome(HuntMovementResult Result, bool StartAccepted);

	private enum HuntScanResult
	{
		NoProgress,
		LiveTargetStillPending,
		TravelBlocked,
		LogRankChanged
	}

	private enum GcHuntLogAction
	{
		Stop,
		ProcessLog,
		Promote
	}

	private readonly record struct GcHuntLogDecision(GcHuntLogAction Action, int LiveGrandCompanyRank, int LiveMonsterNoteRank, int LogIndex, string Reason);

	private readonly record struct RankMarkProcessingResult(int OpenKills, bool LogRankChanged, bool DutyBlocked);

	private readonly record struct DutyMarkProcessingResult(bool Succeeded, bool LogRankChanged);

	private enum HuntDutyAttemptStatus
	{
		Exited,
		PrematureBackendStop,
		TimedOut,
		Failed
	}

	private readonly record struct HuntDutyAttemptResult(HuntDutyAttemptStatus Status, HuntDutyBackend Backend, bool EnteredDuty, string FailureReason);

	private readonly record struct HuntDutyRuntimeState(uint TerritoryId, bool BoundByDuty, bool BetweenAreas, bool CharacterReady);

	private enum DutyUnlockResolutionState
	{
		Resolved,
		Unlocked,
		Unknown
	}

	private readonly record struct DutyUnlockResolution(uint TerritoryTypeId, uint ContentFinderConditionId, uint InstanceContentId, DutyUnlockResolutionState State, string Blocker)
	{
		public bool IsUnlocked => State == DutyUnlockResolutionState.Unlocked;
	}

	private enum MarkProcessingResult
	{
		CountDecreased,
		LogRankAdvanced,
		Unconscious,
		FateEnded,
		TimedOut
	}

	private readonly record struct MarkProcessingOutcome(MarkProcessingResult Result, int BeforeCount, int AfterCount, int BeforeLogRank, int AfterLogRank)
	{
		public bool Registered
		{
			get
			{
				MarkProcessingResult result = Result;
				if ((uint)result <= 1u)
				{
					return true;
				}
				return false;
			}
		}
	}

	private sealed record CombatGearsetSelection(int GearsetId, uint ClassJobId, int Level, int ItemLevel, int ExpArrayIndex, string JobLabel);

	private sealed record CombatGearsetSelectionResult(CombatGearsetSelection? Selection, string FailureReason);

	private sealed record CombatGearsetResolutionSnapshot(bool Ready, ulong PlayerContentId, ulong GearsetContentId, int GearsetCount, CombatGearsetSelectionResult? Resolution, string Reason);

	private sealed record StableCombatGearsetResolution(ulong CharacterContentId, CombatGearsetSelectionResult SelectionResult);

	private sealed record CombatJobSwitchReadiness(bool Ready, bool Mounted, bool Unconscious, string Reason);

	private sealed record HuntTargetEngageAttempt(bool Ready, bool AlreadyEngaged, bool Interacted, bool AttackSent, string Reason);

	private sealed record HuntTargetSearchResult(IBattleNpc? Target, int MatchingCount, int NotTargetableCount, int DeadCount, int NoHpCount, string NearbyTargetableNpcs, string CurrentTarget, bool InCombat, bool Casting, bool InFate, ushort FateId, uint TerritoryId);

	private enum HuntMovementFlightDecision
	{
		Unlocked,
		Locked,
		UnknownButMountable
	}

	private sealed record HuntMovementContext(uint TerritoryId, string TerritoryName, bool MountAllowed, uint AetherCurrentRowId, HuntMovementFlightDecision FlightDecision, string Reason);

	private sealed record HuntMovementPreparation(HuntMovementContext Context, bool UseFlight);

	private sealed record HuntObjectMovementTarget(string Name, uint NameId, uint BaseId, Vector3 Position, float Distance);

	private readonly record struct HuntMovementPolicy(bool ForceMountedGround);

	private sealed record HuntCombatTarget(IBattleNpc Target, ulong GameObjectId, uint RuntimeFateId);

	private sealed record MountDecision(bool AlreadyMounted, bool Mounting, bool ShouldMount, string? Reason);

	private sealed record MountActionResult(string Action, bool Accepted, string Detail);

	private sealed record HuntTeleportDestination(uint AetheryteId, byte SubIndex, string Name, uint TerritoryId, Vector3 Position);

	private enum TeleportArrivalOutcome
	{
		Arrived,
		CombatInterrupted,
		UnexpectedTerritory,
		TimedOut
	}

	private readonly record struct TeleportArrivalResult(TeleportArrivalOutcome Outcome, uint TerritoryId, bool StartObserved, string Detail);

	private sealed record HuntZoneTransition(uint FromTerritoryId, uint ToTerritoryId, Vector3 Position);

	private sealed record HuntTravelPlan(HuntTeleportDestination? Teleport, IReadOnlyList<HuntZoneTransition> Transitions, string Description, string FailureReason)
	{
		public bool IsValid
		{
			get
			{
				if (Teleport != null)
				{
					return string.IsNullOrWhiteSpace(FailureReason);
				}
				return false;
			}
		}
	}

	private readonly record struct HuntTravelResult(bool Arrived, string FailureReason);

	private readonly record struct MatchingFateState(bool Active, bool Joined, ushort JoinedFateId, Vector3 Position, byte MaxLevel, byte PlayerLevel, bool IsLevelSynced)
	{
		public bool RequiresLevelSync
		{
			get
			{
				if (Active)
				{
					return PlayerLevel > MaxLevel;
				}
				return false;
			}
		}

		public bool MatchingMembership
		{
			get
			{
				if (Active && Joined)
				{
					return JoinedFateId != 0;
				}
				return false;
			}
		}
	}

	private sealed record CompanionSummonAttempt(bool Accepted, float TimeLeft, int GreensCount, string? Diagnostic);

	private sealed record SelectStringState(string Prompt, IReadOnlyList<string> Options);

	private enum SelectStringSelectionResult
	{
		Waiting,
		Selected,
		NoMatchingOption
	}

	private enum GrandCompanyPromotionSelectionResult
	{
		Selected,
		NoMatchingOption,
		TimedOut
	}

	private enum SquadronCommanderUiResult
	{
		Waiting,
		TalkAdvanced,
		Accepted,
		NormalOfficerMenu,
		UnexpectedPrompt
	}

	private readonly record struct SquadronCommanderUiStep(SquadronCommanderUiResult Result, string Prompt, bool TalkVisible, bool YesNoVisible);

	private readonly record struct GrandCompanyOfficerState(bool Loaded, bool Targetable, bool InRange, float Distance, float InteractionRange, Vector3 Position);

	private readonly record struct GrandCompanyPromotionReadiness(bool CharacterReady, bool InventoryAvailable, uint GrandCompanyId, int GrandCompanyRank, uint Seals, uint RequiredSeals);

	public readonly record struct CompanionUpkeepStatus(bool Enabled, float? TimeLeft, int? GreensCount, string? Diagnostic);

	private sealed class PendingMarkWorkItem
	{
		public required HuntMark Mark { get; init; }

		public required HuntLogPendingMark Checkpoint { get; init; }
	}

	private readonly record struct PendingMarkContext(string Character, bool IsGrandCompanyLog, int Rank);

	private readonly record struct QuestionableUnlockProgressState(uint TerritoryId, bool InCombat, bool InAreaTransition, bool Accepted, bool Completed, byte Sequence, string? CurrentQuestionableQuestId);

	private sealed record GrandCompanyUnlockQuestData(uint QuestId, uint DutyId, string QuestName, uint TerritoryId, Vector3 OfficerPosition, uint OfficerNpcDataId, string OfficerName);

	private readonly record struct GrandCompanyQuestListSelectionResult(bool Visible, bool Selected, string Options);

	private readonly AutoRetainerIPC autoRetainerIpc;

	private readonly VNavmeshIPC vnavmeshIpc;

	private readonly LifestreamIPC lifestreamIpc;

	private readonly HuntDutyRunner huntDutyRunner;

	private readonly FrenRiderIPC frenRiderIpc;

	private readonly QuestionableIPC questionableIpc;

	private readonly HuntLogDatabase database;

	private readonly Configuration configuration;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly ICondition condition;

	private readonly IClientState clientState;

	private readonly IObjectTable objectTable;

	private readonly ITargetManager targetManager;

	private readonly IGameGui gameGui;

	private readonly IDataManager dataManager;

	private readonly IPlayerState dalamudPlayerState;

	private readonly JobStoneGearsetReconciliationService jobStoneGearsetReconciliation;

	private DeathHandlerService? deathHandler;

	private MovementMonitorService? movementMonitor;

	private readonly object stateLock = new object();

	private readonly object configurationSaveLock = new object();

	private HuntLogAutomationState state = new HuntLogAutomationState();

	private HuntLogAutomationState? cachedStateSnapshot;

	private long stateVersion;

	private long cachedStateVersion = -1L;

	private CancellationTokenSource? cancellationTokenSource;

	private Task? runnerTask;

	private int disposed;

	private DateTime companionSummonNotBeforeUtc = DateTime.MinValue;

	private DateTime companionDiagnosticNotBeforeUtc = DateTime.MinValue;

	private DateTime huntTargetDiagnosticNotBeforeUtc = DateTime.MinValue;

	private DateTime shortGroundApproachDiagnosticNotBeforeUtc = DateTime.MinValue;

	private DateTime frenRiderWarningNotBeforeUtc = DateTime.MinValue;

	private DateTime fateSyncRequestNotBeforeUtc = DateTime.MinValue;

	private DateTime vesperBayPromptCheckNotBeforeUtc = DateTime.MinValue;

	private CombatBackend activeCombatBackend;

	private StandardCombatSelection activeStandardCombatSelection;

	private ushort lastSyncedFateId;

	private DateTime lastConfigurationSaveUtc = DateTime.MinValue;

	private bool configurationSavePending;

	private long nextMemoryDiagnosticUtcTicks;

	private bool configuredReturnCompletedForNextCharacterSwitch;

	private string? savedQuestionablePriority;

	private static readonly TimeSpan MovementReadinessTimeout = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan MovementIdleStableTime = TimeSpan.FromMilliseconds(750L);

	private static readonly TimeSpan MovementStartRetryTimeout = TimeSpan.FromSeconds(8L);

	private static readonly TimeSpan MovementStartRetryDelay = TimeSpan.FromMilliseconds(500L);

	private static readonly TimeSpan MovementStoppedGraceTime = TimeSpan.FromSeconds(1L);

	private static readonly TimeSpan MovementNoProgressTimeout = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan MountVerificationTimeout = TimeSpan.FromSeconds(7L);

	private static readonly TimeSpan MountPollDelay = TimeSpan.FromMilliseconds(150L);

	private static readonly TimeSpan TeleportStartTimeout = TimeSpan.FromSeconds(12L);

	private static readonly TimeSpan TeleportArrivalTimeout = TimeSpan.FromSeconds(90L);

	private static readonly TimeSpan TeleportPostArrivalTimeout = TimeSpan.FromMinutes(2L);

	private static readonly TimeSpan CombatClearTimeout = TimeSpan.FromSeconds(60L);

	private static readonly TimeSpan CombatClearStatusInterval = TimeSpan.FromSeconds(15L);

	private static readonly TimeSpan CombatClearStableTime = TimeSpan.FromMilliseconds(1500L);

	private static readonly TimeSpan CombatGearsetDataAlreadyCombatTimeout = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan CombatGearsetDataSwitchRequiredTimeout = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan CombatGearsetSnapshotStableTime = TimeSpan.FromMilliseconds(1500L);

	private static readonly TimeSpan CombatGearsetDataPollInterval = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan CombatGearsetDataDiagnosticInterval = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan CombatJobSwitchReadyTimeout = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan CombatJobSwitchTimeout = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan HuntTargetEngageWaitTime = TimeSpan.FromSeconds(2.5);

	private static readonly TimeSpan HuntTargetEngageRetryDelay = TimeSpan.FromMilliseconds(500L);

	private static readonly TimeSpan LoadedTargetRetryDelay = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan CombatBackendActivationSettleTime = TimeSpan.FromMilliseconds(750L);

	private static readonly TimeSpan DismountTransitionTimeout = TimeSpan.FromSeconds(4L);

	private static readonly TimeSpan MatchingFatePollDelay = TimeSpan.FromMilliseconds(500L);

	private static readonly TimeSpan FateSyncRequestInterval = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan DeathRecoveryTimeout = TimeSpan.FromMinutes(2L);

	private static readonly TimeSpan DeferredMarkRetryDelay = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan RequiredMarkRespawnWait = TimeSpan.FromSeconds(45L);

	private static readonly TimeSpan CharacterRelogTimeout = TimeSpan.FromMinutes(5L);

	private static readonly TimeSpan CharacterRelogRetryInterval = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan CharacterRelogRejectedRetryDelay = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan CompanionUpkeepInterval = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan CompanionFailedAttemptThrottle = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan CompanionSuccessCooldown = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan CompanionDiagnosticThrottle = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan HuntTargetDiagnosticThrottle = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan ShortGroundApproachDiagnosticThrottle = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan CompanionStanceDelay = TimeSpan.FromSeconds(3L);

	private static readonly TimeSpan QuestionableStopBeforeDutyTimeout = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan QuestionableStartTimeout = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan QuestionableQuestTimeout = TimeSpan.FromMinutes(15L);

	private static readonly TimeSpan QuestionableUnlockPropagationGrace = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan GrandCompanyQuestPickupUiTimeout = TimeSpan.FromSeconds(15L);

	private static readonly TimeSpan HuntDutyTimeout = TimeSpan.FromMinutes(45L);

	private static readonly TimeSpan HuntDutyExitSettlementTimeout = TimeSpan.FromMinutes(2L);

	private static readonly TimeSpan HuntDutyStopTimeout = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan GrandCompanyPromotionReadinessTimeout = TimeSpan.FromSeconds(15L);

	private static readonly TimeSpan SquadronCommanderPromptTimeout = TimeSpan.FromSeconds(30L);

	private static readonly TimeSpan SquadronCommanderCutsceneTimeout = TimeSpan.FromMinutes(2L);

	private static readonly TimeSpan SquadronCommanderCompletionTimeout = TimeSpan.FromSeconds(15L);

	private static readonly TimeSpan ConfigurationSaveMinimumInterval = TimeSpan.FromMinutes(1L);

	private static readonly TimeSpan MemoryDiagnosticInterval = TimeSpan.FromMinutes(1L);

	private const uint GysahlGreensItemId = 4868u;

	private const float CompanionRefreshThresholdSeconds = 900f;

	private const float MovementProgressEpsilon = 1.5f;

	private const float AggroClearSearchRadius = 45f;

	private const float HuntLandingSearchRadius = 15f;

	private const float HuntLandingTolerance = 1.5f;

	private const int HuntTargetEngageAttempts = 2;

	private const int MovementRepathRetryLimit = 2;

	private const int MovementCombatRecoveryLimit = 20;

	private const int TeleportAttemptLimit = 3;

	private const int HuntDutyAttemptLimit = 3;

	private const int GrandCompanyInteractionAttemptLimit = 2;

	private const int QuestionableUnlockRecoveryAttemptLimit = 4;

	private const float GrandCompanyInteractionPadding = 2.5f;

	private const float GrandCompanyInteractionApproachMargin = 0.5f;

	private const float GrandCompanyOfficerFallbackTolerance = 2f;

	private const float GrandCompanyQuestPickupPositionTolerance = 10f;

	private const uint DismountGeneralActionId = 23u;

	private const string DutyUnlockQuestBackendName = "unlock quest via Questionable";

	private const string DutyHandoffBackendName = "duty handoff";

	private static readonly Dictionary<uint, GrandCompanyUnlockQuestData> DzemaelRank7 = new Dictionary<uint, GrandCompanyUnlockQuestData>
	{
		[1u] = new GrandCompanyUnlockQuestData(66664u, 1330u, "Shadows Uncast", 128u, new Vector3(97.520386f, 40.248554f, 81.1322f), 1003281u, "R'ashaht Rhiki"),
		[2u] = new GrandCompanyUnlockQuestData(66665u, 1330u, "Shadows Uncast", 132u, new Vector3(-75.48645f, -0.5013741f, -5.081299f), 1000168u, "Vorsaile Heuloix"),
		[3u] = new GrandCompanyUnlockQuestData(66666u, 1330u, "Shadows Uncast", 130u, new Vector3(-141.64954f, 4.1f, -114.67157f), 1004576u, "Swift")
	};

	private static readonly Dictionary<uint, GrandCompanyUnlockQuestData> AurumRank8 = new Dictionary<uint, GrandCompanyUnlockQuestData>
	{
		[1u] = new GrandCompanyUnlockQuestData(66667u, 1331u, "Gilding the Bilious", 128u, new Vector3(97.520386f, 40.248554f, 81.1322f), 1003281u, "R'ashaht Rhiki"),
		[2u] = new GrandCompanyUnlockQuestData(66668u, 1331u, "Gilding the Bilious", 132u, new Vector3(-75.48645f, -0.5013741f, -5.081299f), 1000168u, "Vorsaile Heuloix"),
		[3u] = new GrandCompanyUnlockQuestData(66669u, 1331u, "Gilding the Bilious", 130u, new Vector3(-141.64954f, 4.1f, -114.67157f), 1004576u, "Swift")
	};

	private const uint RisingToTheChallengeQuestRowId = 66967u;

	private static readonly Dictionary<uint, uint> OrdinaryDutyUnlockQuests = new Dictionary<uint, uint>
	{
		[1245u] = 66233u,
		[1267u] = 66300u,
		[1303u] = 66457u,
		[1330u] = 66515u
	};

	public bool IsRunning
	{
		get
		{
			lock (stateLock)
			{
				HuntLogPhase phase = state.Phase;
				if ((uint)(phase - 1) <= 4u)
				{
					return true;
				}
				return false;
			}
		}
	}

	public HuntLogAutomationService(AutoRetainerIPC autoRetainerIpc, VNavmeshIPC vnavmeshIpc, LifestreamIPC lifestreamIpc, HuntDutyRunner huntDutyRunner, FrenRiderIPC frenRiderIpc, QuestionableIPC questionableIpc, HuntLogDatabase database, Configuration configuration, IPluginLog log, IFramework framework, ICommandManager commandManager, ICondition condition, IClientState clientState, IObjectTable objectTable, ITargetManager targetManager, IGameGui gameGui, IDataManager dataManager, JobStoneGearsetReconciliationService jobStoneGearsetReconciliation)
	{
		this.autoRetainerIpc = autoRetainerIpc;
		this.vnavmeshIpc = vnavmeshIpc;
		this.lifestreamIpc = lifestreamIpc;
		this.huntDutyRunner = huntDutyRunner;
		this.frenRiderIpc = frenRiderIpc;
		this.questionableIpc = questionableIpc;
		this.database = database;
		this.configuration = configuration;
		this.log = log;
		this.framework = framework;
		this.commandManager = commandManager;
		this.condition = condition;
		this.clientState = clientState;
		this.objectTable = objectTable;
		this.targetManager = targetManager;
		this.gameGui = gameGui;
		this.dataManager = dataManager;
		this.jobStoneGearsetReconciliation = jobStoneGearsetReconciliation;
		dalamudPlayerState = Plugin.PlayerState;
		framework.Update += OnFrameworkUpdate;
	}

	public void SetMovementMonitor(MovementMonitorService service)
	{
		movementMonitor = service;
	}

	public HuntLogAutomationState GetCurrentState()
	{
		lock (stateLock)
		{
			if (cachedStateSnapshot == null || cachedStateVersion != stateVersion)
			{
				cachedStateSnapshot = state.Clone();
				cachedStateVersion = stateVersion;
			}
			return cachedStateSnapshot;
		}
	}

	public FrenRiderAvailability GetFrenRiderAvailability()
	{
		return frenRiderIpc.GetAvailability();
	}

	public unsafe CompanionUpkeepStatus GetCompanionUpkeepStatus()
	{
		if (!configuration.HuntLogs.SummonChocobo)
		{
			return new CompanionUpkeepStatus(Enabled: false, null, null, null);
		}
		if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
		{
			return new CompanionUpkeepStatus(Enabled: true, null, null, "Unavailable while the character is not ready.");
		}
		try
		{
			UIState* ptr = UIState.Instance();
			InventoryManager* ptr2 = InventoryManager.Instance();
			float? timeLeft = ((ptr == null) ? ((float?)null) : new float?(ptr->Buddy.CompanionInfo.TimeLeft));
			int? greensCount = ((ptr2 == null) ? ((int?)null) : new int?(GetGysahlGreensCountUnsafe(ptr2)));
			string diagnostic = ((timeLeft.HasValue && greensCount.HasValue) ? null : "Companion or inventory state is unavailable.");
			return new CompanionUpkeepStatus(Enabled: true, timeLeft, greensCount, diagnostic);
		}
		catch (Exception ex)
		{
			return new CompanionUpkeepStatus(Enabled: true, null, null, "Status unavailable: " + ex.Message);
		}
	}

	public void SetDeathHandler(DeathHandlerService service)
	{
		deathHandler = service;
	}

	private unsafe void OnFrameworkUpdate(IFramework _)
	{
		if (!IsRunning || !clientState.IsLoggedIn)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow < vesperBayPromptCheckNotBeforeUtc)
		{
			return;
		}
		vesperBayPromptCheckNotBeforeUtc = utcNow.AddMilliseconds(250.0);
		try
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectYesno");
			if (addonByName == IntPtr.Zero)
			{
				return;
			}
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr != null && ptr->IsVisible && ptr->IsReady)
			{
				string dialogText = SelectYesnoTextHandler.GetDialogText(ptr);
				if (!string.IsNullOrWhiteSpace(dialogText) && dialogText.Contains("Travel to Vesper Bay", StringComparison.OrdinalIgnoreCase) && dialogText.Contains("80 gil", StringComparison.OrdinalIgnoreCase) && (SelectYesnoTextHandler.ClickYesButton(ptr) || FireCallback("SelectYesno", 0)))
				{
					vesperBayPromptCheckNotBeforeUtc = utcNow.AddSeconds(2.0);
					log.Information("[HuntLogs] Accepted the scoped Vesper Bay ferry prompt: \"" + dialogText + "\".");
				}
			}
		}
		catch (Exception ex)
		{
			vesperBayPromptCheckNotBeforeUtc = utcNow.AddSeconds(1.0);
			log.Debug("[HuntLogs] Vesper Bay prompt handling failed: " + ex.Message);
		}
	}

	public bool Start(HuntLogMode mode, IReadOnlyList<string> selectedCharacters)
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return false;
		}
		if (IsRunning)
		{
			SetError("Hunt-log automation is already running.");
			return false;
		}
		List<string> list = selectedCharacters.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			SetError("Select at least one character before starting hunt logs.");
			return false;
		}
		if (!database.EnsureInitialized())
		{
			SetError("Hunt-log data failed to initialize. Check that Data/HuntLogs/ARRHunt.json is present in the plugin output.");
			return false;
		}
		EnableTextAdvanceForHuntLogStart();
		cancellationTokenSource?.Dispose();
		cancellationTokenSource = new CancellationTokenSource();
		huntTargetDiagnosticNotBeforeUtc = DateTime.MinValue;
		shortGroundApproachDiagnosticNotBeforeUtc = DateTime.MinValue;
		configuredReturnCompletedForNextCharacterSwitch = false;
		HuntLogRunCheckpoint resumeCheckpoint = GetResumeCheckpoint(mode, list);
		List<string> resumeCompletedCharacters = GetResumeCompletedCharacters(mode, list);
		Dictionary<string, string> preflightCompletedCharacters = GetPreflightCompletedCharacters(mode, list, resumeCompletedCharacters);
		List<string> completedCharacters = resumeCompletedCharacters.Concat(preflightCompletedCharacters.Keys).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		Dictionary<string, HuntLogCompletionProvenance> dictionary = new Dictionary<string, HuntLogCompletionProvenance>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in resumeCompletedCharacters)
		{
			HuntLogRunCheckpoint huntLogRunCheckpoint = resumeCheckpoint;
			if (huntLogRunCheckpoint != null && huntLogRunCheckpoint.CompletionProvenance.TryGetValue(item, out var value))
			{
				dictionary[item] = value;
			}
		}
		foreach (string key in preflightCompletedCharacters.Keys)
		{
			dictionary[key] = GetTrustedPreflightCompletionProvenance(key, mode);
		}
		List<string> remainingCharacters = list.Where((string x) => !completedCharacters.Contains<string>(x, StringComparer.OrdinalIgnoreCase)).ToList();
		HashSet<string> hashSet = list.Where((string x) => RequiresLiveGrandCompanyInspection(x, mode, resumeCheckpoint)).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		DateTime utcNow = DateTime.UtcNow;
		lock (stateLock)
		{
			state = new HuntLogAutomationState
			{
				Phase = HuntLogPhase.Starting,
				Mode = mode,
				SelectedCharacters = new List<string>(list),
				RemainingCharacters = new List<string>(remainingCharacters),
				CompletedCharacters = new List<string>(completedCharacters),
				CompletionProvenance = dictionary,
				SkippedCharacters = new List<string>(),
				FailedCharacters = new List<string>(),
				PendingMarks = (resumeCheckpoint?.PendingMarks.ConvertAll((HuntLogPendingMark x) => x.Clone()) ?? new List<HuntLogPendingMark>()),
				StartedAtUtc = utcNow,
				CurrentStep = "Starting hunt-log rotation"
			};
			foreach (string item2 in list)
			{
				state.CharacterStatuses[item2] = (resumeCompletedCharacters.Contains<string>(item2, StringComparer.OrdinalIgnoreCase) ? "Completed (checkpoint)" : (preflightCompletedCharacters.ContainsKey(item2) ? "Completed (preflight)" : (hashSet.Contains(item2) ? "Queued: live GC inspection required" : "Queued")));
			}
			stateVersion++;
		}
		if (resumeCompletedCharacters.Count > 0)
		{
			log.Information("[HuntLogs] Resuming checkpoint: skipping completed characters " + string.Join(", ", resumeCompletedCharacters));
		}
		if (preflightCompletedCharacters.Count > 0)
		{
			foreach (var (text3, text4) in preflightCompletedCharacters)
			{
				log.Information("[HuntLogs] Preflight completed " + text3 + ": " + text4);
			}
		}
		deathHandler?.SetSuppressedByHuntLogs(suppressed: true);
		SaveCheckpointFromState(active: true, completed: false, null, forceSave: true);
		runnerTask = Task.Run(() => RunAsync(mode, remainingCharacters, cancellationTokenSource.Token), cancellationTokenSource.Token);
		return true;
	}

	private void EnableTextAdvanceForHuntLogStart()
	{
		try
		{
			if (commandManager.ProcessCommand("/at y"))
			{
				log.Information("[HuntLogs] Sent /at y at hunt-log rotation start.");
			}
			else
			{
				log.Warning("[HuntLogs] /at y was not accepted at hunt-log rotation start.");
			}
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Could not send /at y at hunt-log rotation start: " + ex.Message);
		}
	}

	public void Stop()
	{
		huntDutyRunner.StopOwnedSession("hunt-log stop requested");
		if (IsRunning)
		{
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.Phase = HuntLogPhase.Stopping;
				s.CurrentStep = "Stopping";
			});
			cancellationTokenSource?.Cancel();
		}
	}

	private async Task RunAsync(HuntLogMode mode, List<string> characters, CancellationToken token)
	{
		try
		{
			foreach (string character in characters)
			{
				token.ThrowIfCancellationRequested();
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentCharacter = character;
					s.CurrentMarkName = string.Empty;
					s.CurrentCombatJobId = 0u;
					s.SelectedCombatJobId = 0u;
					s.CurrentCombatJobLabel = string.Empty;
					s.SelectedCombatJobLabel = string.Empty;
					s.CurrentRank = 0;
					s.CharacterStatuses[character] = "Waiting for login";
				});
				await SwitchToCharacterAsync(character, token);
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.Phase = HuntLogPhase.RunningCharacter;
					s.CurrentStep = "Running hunt logs";
					s.CharacterStatuses[character] = "Running";
				});
				bool characterComplete = await EnsureSelectedCombatJobAsync(character, token);
				if (!characterComplete)
				{
					UpdateState(delegate(HuntLogAutomationState s)
					{
						s.CurrentMarkName = string.Empty;
						s.CurrentRank = 0;
					});
					SaveCheckpointFromState(active: true, completed: false, null, forceSave: true);
					continue;
				}
				await RefreshResumePendingMarkCountsAsync(character, token);
				HuntLogMode huntLogMode = mode;
				bool flag = ((huntLogMode == HuntLogMode.Class || huntLogMode == HuntLogMode.All) ? true : false);
				bool flag2 = !flag;
				if (!flag2)
				{
					flag2 = await RunClassLogsAsync(character, token);
				}
				bool classComplete = flag2;
				huntLogMode = mode;
				flag2 = (uint)(huntLogMode - 1) <= 1u;
				flag = !flag2;
				if (!flag)
				{
					flag = await RunGrandCompanyLogsAsync(character, token);
				}
				bool flag3 = flag;
				characterComplete = classComplete && flag3;
				HuntLogCompletionProvenance liveCompletionProvenance = GetLiveCompletionProvenance(character, mode);
				if (characterComplete && configuration.HuntLogs.ReturnOnceDone)
				{
					await ReturnIfConfiguredAsync(token);
					configuredReturnCompletedForNextCharacterSwitch = true;
				}
				UpdateState(delegate(HuntLogAutomationState s)
				{
					if (characterComplete)
					{
						if (!s.CompletedCharacters.Contains<string>(character, StringComparer.OrdinalIgnoreCase))
						{
							s.CompletedCharacters.Add(character);
						}
						s.CompletionProvenance[character] = liveCompletionProvenance;
						s.RemainingCharacters.RemoveAll((string x) => string.Equals(x, character, StringComparison.OrdinalIgnoreCase));
						s.CharacterStatuses[character] = "Completed";
						s.PendingMarks.RemoveAll((HuntLogPendingMark x) => string.Equals(x.CharacterName, character, StringComparison.OrdinalIgnoreCase));
					}
					else
					{
						s.CompletionProvenance.Remove(character);
						if (!s.CharacterStatuses.TryGetValue(character, out string value) || !value.StartsWith("Blocked:", StringComparison.OrdinalIgnoreCase))
						{
							s.CharacterStatuses[character] = "Incomplete: required hunt-log work remains";
						}
					}
					s.CurrentMarkName = string.Empty;
					s.CurrentRank = 0;
				});
				SaveCheckpointFromState(active: true, completed: false, null, forceSave: true);
			}
			List<string> incompleteCharacters = GetCurrentIncompleteCharacters();
			if (incompleteCharacters.Count > 0)
			{
				await CleanupAsync(drainCombat: true, token);
				string message = "Required hunt-log work remains for: " + string.Join(", ", incompleteCharacters) + ".";
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.Phase = HuntLogPhase.Error;
					s.ErrorMessage = message;
					s.CurrentStep = "Incomplete";
					s.CurrentCharacter = string.Empty;
					s.CurrentMarkName = string.Empty;
				});
				SaveCheckpointFromState(active: true, completed: false, message, forceSave: true);
				log.Warning("[HuntLogs] " + message);
				return;
			}
			await CleanupAsync(drainCombat: true, token);
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.Phase = HuntLogPhase.Completed;
				s.CurrentStep = "Completed";
				s.CurrentCharacter = string.Empty;
			});
			SaveCheckpointFromState(active: false, completed: true, null, forceSave: true);
			log.Information("[HuntLogs] Hunt-log automation completed");
		}
		catch (OperationCanceledException)
		{
			await CleanupAsync();
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.Phase = HuntLogPhase.Idle;
				s.CurrentStep = "Stopped";
				s.CurrentMarkName = string.Empty;
			});
			SaveCheckpointFromState(active: true, completed: false, null, forceSave: true);
			log.Information("[HuntLogs] Hunt-log automation stopped");
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			await CleanupAsync(drainCombat: true, CancellationToken.None);
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.Phase = HuntLogPhase.Error;
				s.ErrorMessage = ex3.Message;
				s.CurrentStep = "Failed";
				if (!string.IsNullOrWhiteSpace(s.CurrentCharacter) && !s.FailedCharacters.Contains<string>(s.CurrentCharacter, StringComparer.OrdinalIgnoreCase))
				{
					s.FailedCharacters.Add(s.CurrentCharacter);
				}
			});
			SaveCheckpointFromState(active: true, completed: false, ex3.Message, forceSave: true);
			log.Error($"[HuntLogs] Hunt-log automation failed: {ex3}");
		}
	}

	private async Task<bool> RunClassLogsAsync(string character, CancellationToken token)
	{
		for (int loop = 0; loop < 8; loop++)
		{
			token.ThrowIfCancellationRequested();
			(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) player = await GetPlayerInfoAsync();
			if (player.ClassJobId > 255 || !JobClassification.IsCombatJob((byte)player.ClassJobId))
			{
				MarkCurrentCharacterStatus("Blocked: current job is not combat", markSkipped: true);
				log.Warning("[HuntLogs] " + character + " is not on a combat job; skipping class hunt log");
				return false;
			}
			uint monsterNoteId = await GetCurrentClassMonsterNoteIdAsync(player.ClassJobId);
			uint num = monsterNoteId;
			if ((num == 0 || num == 127) ? true : false)
			{
				log.Warning($"[HuntLogs] {character} has no class hunt log for current job {player.ClassJobId}");
				UpdateCharacterSnapshot(character, player, 0, null);
				MarkCurrentCharacterStatus("Blocked: no class hunt log for current job", markSkipped: true);
				return false;
			}
			int rank = await GetMonsterNoteRankAsync((int)monsterNoteId);
			UpdateCharacterSnapshot(character, player, rank, null);
			if (rank >= Math.Clamp(configuration.HuntLogs.StopAfterClassRank, 1, 5) || rank >= 5)
			{
				return true;
			}
			int num2 = rank switch
			{
				1 => 10, 
				2 => 20, 
				3 => 30, 
				4 => 40, 
				_ => 0, 
			};
			if (player.Level < num2)
			{
				log.Information($"[HuntLogs] {character} needs level {num2} for class hunt-log rank {rank + 1}; current level is {player.Level}");
				MarkCurrentCharacterStatus($"Blocked: class rank {rank + 1} requires level {num2}", markSkipped: true);
				return false;
			}
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Class hunt log";
				s.CurrentRank = rank + 1;
				s.CharacterStatuses[character] = $"Class rank {rank + 1}";
			});
			List<HuntMark> classRankMarks = database.GetClassRankMarks(monsterNoteId, rank, player.Level);
			if (classRankMarks.Count == 0)
			{
				throw new InvalidOperationException($"No hunt-mark data is available for class rank {rank + 1}.");
			}
			RankMarkProcessingResult rankMarkProcessingResult = await ProcessRankMarksAsync(classRankMarks, gcLog: false, token);
			if (rankMarkProcessingResult.DutyBlocked)
			{
				return false;
			}
			if (!rankMarkProcessingResult.LogRankChanged)
			{
				if (rankMarkProcessingResult.OpenKills > 0)
				{
					log.Warning($"[HuntLogs] Class rank {rank + 1} still has {rankMarkProcessingResult.OpenKills} open kills after processing; stopping this class log");
					return false;
				}
				await Task.Delay(1000, token);
			}
		}
		return false;
	}

	private async Task<bool> RunGrandCompanyLogsAsync(string character, CancellationToken token)
	{
		for (int loop = 0; loop < 24; loop++)
		{
			token.ThrowIfCancellationRequested();
			(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) player = await GetPlayerInfoAsync();
			ReconcileAdvisoryGrandCompanyRankWithLive(character, player.GrandCompanyRank);
			if (player.ClassJobId > 255 || !JobClassification.IsCombatJob((byte)player.ClassJobId))
			{
				MarkCurrentCharacterStatus("Blocked: current job is not combat", markSkipped: true);
				log.Warning("[HuntLogs] " + character + " is not on a combat job; skipping GC hunt log");
				return false;
			}
			uint item = player.GrandCompanyId;
			if ((item < 1 || item > 3) ? true : false)
			{
				log.Warning("[HuntLogs] " + character + " is not in a Grand Company; skipping GC hunt log");
				MarkCurrentCharacterStatus("Blocked: character has no Grand Company", markSkipped: true);
				return false;
			}
			if (IsInvalidGrandCompanyRank(player.GrandCompanyRank))
			{
				UpdateCharacterSnapshot(character, player, null, null);
				MarkCurrentCharacterStatus($"Blocked: invalid live GC rank {player.GrandCompanyRank} (max {11})", markSkipped: true);
				log.Warning($"[HuntLogs] {character} has invalid live Grand Company rank {player.GrandCompanyRank}; max legal rank is {11}. Live inspection did not produce " + "a usable rank, so this character will not be treated as complete or eligible for promotion.");
				return false;
			}
			uint num = await GetGrandCompanyMonsterNoteIdAsync(player.GrandCompanyId);
			if ((num == 0 || num == 127) ? true : false)
			{
				log.Warning("[HuntLogs] " + character + " has no Grand Company hunt log");
				MarkCurrentCharacterStatus("Blocked: no Grand Company hunt log", markSkipped: true);
				return false;
			}
			int gcLogRank = await GetMonsterNoteRankAsync((int)num);
			UpdateCharacterSnapshot(character, player, null, gcLogRank);
			int stopGrandCompanyRank = GetStopGrandCompanyRank();
			int unlockedLogIndex = GetGrandCompanyLogIndexForRank(player.GrandCompanyRank);
			List<HuntMark> unlockedLogMarks = database.GetGrandCompanyRankMarks(player.GrandCompanyId, unlockedLogIndex, player.Level);
			bool flag = unlockedLogMarks.Count > 0;
			if (flag)
			{
				flag = await AreAllMonsterNoteMarksCompleteAsync(unlockedLogMarks);
			}
			bool unlockedLogComplete = flag;
			GcHuntLogDecision decision = DecideGrandCompanyHuntLog(player.GrandCompanyRank, gcLogRank, stopGrandCompanyRank, unlockedLogComplete);
			log.Information($"[HuntLogs] GC decision: action={decision.Action}, liveRank={decision.LiveGrandCompanyRank}, monsterNoteRank={decision.LiveMonsterNoteRank}, logIndex={decision.LogIndex}, reason={decision.Reason}");
			if (decision.Action == GcHuntLogAction.Stop)
			{
				flag = player.GrandCompanyRank == 9 && stopGrandCompanyRank >= 9;
				if (flag)
				{
					flag = !(await EnsureSquadronCommanderUnlockAsync(player.GrandCompanyId, token));
				}
				if (flag)
				{
					return false;
				}
				DiscardPendingGrandCompanyMarks(character, null);
				return true;
			}
			if (decision.Action == GcHuntLogAction.ProcessLog)
			{
				DiscardPendingGrandCompanyMarks(character, decision.LogIndex + 1);
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = "Grand Company hunt log";
					s.CurrentRank = decision.LogIndex + 1;
					s.CharacterStatuses[character] = $"GC rank {player.GrandCompanyRank}, log {decision.LogIndex + 1}";
				});
				List<HuntMark> list = ((decision.LogIndex == unlockedLogIndex) ? unlockedLogMarks : database.GetGrandCompanyRankMarks(player.GrandCompanyId, decision.LogIndex, player.Level));
				if (list.Count == 0)
				{
					throw new InvalidOperationException($"No hunt-mark data is available for Grand Company log rank {decision.LogIndex + 1}.");
				}
				RankMarkProcessingResult rankMarkProcessingResult = await ProcessRankMarksAsync(list, gcLog: true, token);
				if (rankMarkProcessingResult.DutyBlocked)
				{
					return false;
				}
				if (rankMarkProcessingResult.LogRankChanged)
				{
					DiscardPendingGrandCompanyMarks(character, null);
				}
				else if (rankMarkProcessingResult.OpenKills > 0)
				{
					log.Warning($"[HuntLogs] GC log rank {decision.LogIndex + 1} still has {rankMarkProcessingResult.OpenKills} " + "open kills after processing; stopping this GC log.");
					return false;
				}
				continue;
			}
			DiscardPendingGrandCompanyMarks(character, null);
			if (!configuration.HuntLogs.AutoGrandCompanyRankUp)
			{
				MarkCurrentCharacterStatus($"Blocked: GC rank-up disabled before rank {stopGrandCompanyRank}", markSkipped: true);
				return false;
			}
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Preparing Grand Company promotion";
				s.CharacterStatuses[character] = $"Preparing GC promotion from rank {player.GrandCompanyRank}";
			});
			if (!(await TryWaitForCharacterReadyAsync(token)))
			{
				MarkCurrentCharacterStatus("Blocked: character did not settle before GC promotion", markSkipped: true);
				log.Warning("[HuntLogs] Character did not settle before the GC promotion handoff.");
				return false;
			}
			(uint, int, uint, int) tuple = await GetPlayerInfoAsync();
			if (tuple.Item3 != player.GrandCompanyId || tuple.Item4 != player.GrandCompanyRank)
			{
				log.Information($"[HuntLogs] GC promotion preparation observed live state change; rebuilding decision. company={player.GrandCompanyId}->{tuple.Item3}, rank={player.GrandCompanyRank}->{tuple.Item4}.");
				continue;
			}
			player = tuple;
			int item2 = player.GrandCompanyRank;
			if ((uint)(item2 - 7) <= 1u)
			{
				if (!(await HandleGrandCompanyRankQuestAsync(player.GrandCompanyId, player.GrandCompanyRank, token)))
				{
					return false;
				}
				player = await GetPlayerInfoAsync();
				num = await GetGrandCompanyMonsterNoteIdAsync(player.GrandCompanyId);
				flag = ((num == 0 || num == 127) ? true : false);
				item2 = ((!flag) ? (await GetMonsterNoteRankAsync((int)num)) : 0);
				gcLogRank = item2;
				UpdateCharacterSnapshot(character, player, null, gcLogRank);
				if (player.GrandCompanyRank >= stopGrandCompanyRank)
				{
					return true;
				}
			}
			GrandCompanyPromotionReadiness grandCompanyPromotionReadiness = await WaitForGrandCompanyPromotionReadinessAsync(token);
			if (grandCompanyPromotionReadiness.GrandCompanyId != player.GrandCompanyId || grandCompanyPromotionReadiness.GrandCompanyRank != player.GrandCompanyRank)
			{
				log.Information($"[HuntLogs] GC promotion readiness observed live state change; rebuilding decision. company={player.GrandCompanyId}->{grandCompanyPromotionReadiness.GrandCompanyId}, rank={player.GrandCompanyRank}->{grandCompanyPromotionReadiness.GrandCompanyRank}.");
				continue;
			}
			if (!grandCompanyPromotionReadiness.CharacterReady || !grandCompanyPromotionReadiness.InventoryAvailable)
			{
				string text = ((!grandCompanyPromotionReadiness.CharacterReady) ? "character did not settle after the previous handoff" : "inventory state was unavailable after the previous handoff");
				MarkCurrentCharacterStatus("Blocked: cannot rank up GC because " + text, markSkipped: true);
				log.Warning($"[HuntLogs] GC promotion readiness timed out: {text}; company={grandCompanyPromotionReadiness.GrandCompanyId}, rank={grandCompanyPromotionReadiness.GrandCompanyRank}.");
				return false;
			}
			if (grandCompanyPromotionReadiness.RequiredSeals == 0 || grandCompanyPromotionReadiness.Seals < grandCompanyPromotionReadiness.RequiredSeals)
			{
				string text2 = ((grandCompanyPromotionReadiness.RequiredSeals == 0) ? $"rank {grandCompanyPromotionReadiness.GrandCompanyRank} has no supported promotion requirement" : $"only {grandCompanyPromotionReadiness.Seals}/{grandCompanyPromotionReadiness.RequiredSeals} company seals are available");
				MarkCurrentCharacterStatus("Blocked: cannot rank up GC because " + text2, markSkipped: true);
				log.Warning($"[HuntLogs] GC promotion is not eligible: {text2}; company={grandCompanyPromotionReadiness.GrandCompanyId}, rank={grandCompanyPromotionReadiness.GrandCompanyRank}.");
				return false;
			}
			log.Information($"[HuntLogs] GC promotion handoff ready: company={grandCompanyPromotionReadiness.GrandCompanyId}, rank={grandCompanyPromotionReadiness.GrandCompanyRank}, seals={grandCompanyPromotionReadiness.Seals}, requiredSeals={grandCompanyPromotionReadiness.RequiredSeals}.");
			int previousRank = player.GrandCompanyRank;
			bool promotionCompleted = false;
			bool flag2;
			try
			{
				if (await RankUpGrandCompanyAsync(player.GrandCompanyId, token))
				{
					promotionCompleted = await WaitForGrandCompanyRankIncreaseAsync(character, previousRank, token);
				}
			}
			finally
			{
				flag2 = await CloseGrandCompanyPromotionUiAsync(CancellationToken.None);
			}
			if (!promotionCompleted)
			{
				return false;
			}
			if (!flag2)
			{
				MarkCurrentCharacterStatus("Blocked: Grand Company promotion UI did not close cleanly", markSkipped: true);
				return false;
			}
			if (previousRank == 8 && !(await EnsureSquadronCommanderUnlockAsync(player.GrandCompanyId, token)))
			{
				return false;
			}
		}
		return false;
	}

	private static GcHuntLogDecision DecideGrandCompanyHuntLog(int grandCompanyRank, int monsterNoteRank, int stopRank, bool unlockedLogComplete)
	{
		if (grandCompanyRank >= stopRank)
		{
			return new GcHuntLogDecision(GcHuntLogAction.Stop, grandCompanyRank, monsterNoteRank, -1, $"live GC rank reached configured stop {stopRank}");
		}
		int grandCompanyLogIndexForRank = GetGrandCompanyLogIndexForRank(grandCompanyRank);
		if (monsterNoteRank > grandCompanyLogIndexForRank || unlockedLogComplete)
		{
			return new GcHuntLogDecision(GcHuntLogAction.Promote, grandCompanyRank, monsterNoteRank, -1, $"log {grandCompanyLogIndexForRank + 1} is complete and the next log is not unlocked");
		}
		return new GcHuntLogDecision(GcHuntLogAction.ProcessLog, grandCompanyRank, monsterNoteRank, grandCompanyLogIndexForRank, (monsterNoteRank < grandCompanyLogIndexForRank) ? $"MonsterNote rank is stale; GC rank authoritatively unlocks log {grandCompanyLogIndexForRank + 1}" : $"log {grandCompanyLogIndexForRank + 1} is the live unlocked log");
	}

	private static int GetGrandCompanyLogIndexForRank(int grandCompanyRank)
	{
		if (grandCompanyRank < 9)
		{
			if (grandCompanyRank >= 5)
			{
				return 1;
			}
			return 0;
		}
		return 2;
	}

	private async Task<bool> AreAllMonsterNoteMarksCompleteAsync(IEnumerable<HuntMark> marks)
	{
		foreach (HuntMark mark in marks)
		{
			if (await GetOpenMonsterNoteKillsAsync(mark) > 0)
			{
				return false;
			}
		}
		return true;
	}

	private void DiscardPendingGrandCompanyMarks(string character, int? keepRank)
	{
		int removed = 0;
		UpdateState(delegate(HuntLogAutomationState s)
		{
			removed = s.PendingMarks.RemoveAll((HuntLogPendingMark x) => x.IsGrandCompanyLog && string.Equals(x.CharacterName, character, StringComparison.OrdinalIgnoreCase) && (!keepRank.HasValue || x.Rank != keepRank.Value));
		});
		if (removed > 0)
		{
			SaveCheckpointFromState(active: true);
			log.Information($"[HuntLogs] Discarded {removed} pending GC marks from the previous live context for {character}.");
		}
	}

	private async Task<RankMarkProcessingResult> ProcessRankMarksAsync(List<HuntMark> marks, bool gcLog, CancellationToken token)
	{
		HuntLogAutomationState currentState = GetCurrentState();
		PendingMarkContext context = new PendingMarkContext(currentState.CurrentCharacter, gcLog, currentState.CurrentRank);
		List<PendingMarkWorkItem> workItems = await BuildPendingMarkWorkItemsAsync(marks, context, currentState.PendingMarks);
		PersistPendingMarkQueue(workItems, context);
		int rankBeforeOverworld = await GetMonsterNoteRankAsync(marks[0].MonsterNoteId);
		if (await ProcessOverworldMarksAsync(workItems, context, rankBeforeOverworld, returnWhenAllDeferred: true, token))
		{
			IPluginLog pluginLog = log;
			string text = $"[HuntLogs] MonsterNote rank changed during overworld processing ({rankBeforeOverworld} -> ";
			pluginLog.Information(text + $"{await GetMonsterNoteRankAsync(marks[0].MonsterNoteId)}); discarding the complete old mark " + "context before duty selection or further target pursuit.");
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.PendingMarks.RemoveAll((HuntLogPendingMark x) => IsSamePendingMarkContext(x, context));
			});
			SaveCheckpointFromState(active: true);
			return new RankMarkProcessingResult(0, LogRankChanged: true, DutyBlocked: false);
		}
		await RefreshPendingMarkCountsAsync(workItems);
		List<HuntMark> dutyMarks = (from x in workItems
			where x.Checkpoint.RemainingKills > 0 && database.IsDutyTerritory(x.Mark.TerritoryId)
			select x.Mark).ToList();
		if (dutyMarks.Count > 0)
		{
			int rankBeforeDuty = await GetMonsterNoteRankAsync(marks[0].MonsterNoteId);
			DutyMarkProcessingResult dutyResult = await ProcessDutyMarksAsync(dutyMarks, context, rankBeforeDuty, token);
			if (!dutyResult.Succeeded)
			{
				await RefreshPendingMarkCountsAsync(workItems);
				PersistPendingMarkQueue(workItems, context);
				return new RankMarkProcessingResult(workItems.Sum((PendingMarkWorkItem x) => x.Checkpoint.RemainingKills), LogRankChanged: false, DutyBlocked: true);
			}
			int rankAfterDuty = await GetMonsterNoteRankAsync(marks[0].MonsterNoteId);
			await RefreshPendingMarkCountsAsync(workItems);
			if (dutyResult.LogRankChanged || rankAfterDuty != rankBeforeDuty)
			{
				log.Information($"[HuntLogs] MonsterNote rank changed during duty handoff ({rankBeforeDuty} -> {rankAfterDuty}); " + "discarding the old mark context before any overworld pursuit.");
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.PendingMarks.RemoveAll((HuntLogPendingMark x) => IsSamePendingMarkContext(x, context));
				});
				SaveCheckpointFromState(active: true);
				return new RankMarkProcessingResult(0, LogRankChanged: true, DutyBlocked: false);
			}
		}
		int rankBeforeFinalOverworld = await GetMonsterNoteRankAsync(marks[0].MonsterNoteId);
		if (await ProcessOverworldMarksAsync(workItems, context, rankBeforeFinalOverworld, returnWhenAllDeferred: false, token))
		{
			IPluginLog pluginLog = log;
			string text = $"[HuntLogs] MonsterNote rank changed during overworld processing ({rankBeforeFinalOverworld} -> ";
			pluginLog.Information(text + $"{await GetMonsterNoteRankAsync(marks[0].MonsterNoteId)}); discarding the complete old mark " + "context before further target pursuit.");
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.PendingMarks.RemoveAll((HuntLogPendingMark x) => IsSamePendingMarkContext(x, context));
			});
			SaveCheckpointFromState(active: true);
			return new RankMarkProcessingResult(0, LogRankChanged: true, DutyBlocked: false);
		}
		await RefreshPendingMarkCountsAsync(workItems);
		PersistPendingMarkQueue(workItems, context);
		return new RankMarkProcessingResult(workItems.Sum((PendingMarkWorkItem x) => x.Checkpoint.RemainingKills), LogRankChanged: false, DutyBlocked: false);
	}

	private async Task<List<PendingMarkWorkItem>> BuildPendingMarkWorkItemsAsync(List<HuntMark> marks, PendingMarkContext context, IReadOnlyList<HuntLogPendingMark> persistedQueue)
	{
		List<(HuntMark Mark, int Remaining)> currentMarks = new List<(HuntMark, int)>();
		foreach (HuntMark mark in marks)
		{
			int num = await GetOpenMonsterNoteKillsAsync(mark);
			if (num > 0)
			{
				currentMarks.Add((mark, num));
			}
		}
		List<PendingMarkWorkItem> list = new List<PendingMarkWorkItem>();
		List<(HuntMark, int)> list2 = new List<(HuntMark, int)>(currentMarks);
		foreach (HuntLogPendingMark persisted in persistedQueue.Where((HuntLogPendingMark x) => IsSamePendingMarkContext(x, context)))
		{
			int num2 = list2.FindIndex(((HuntMark Mark, int Remaining) x) => IsSamePendingMark(persisted, x.Mark));
			if (num2 >= 0)
			{
				(HuntMark, int) tuple = list2[num2];
				list2.RemoveAt(num2);
				HuntLogPendingMark huntLogPendingMark = persisted.Clone();
				if (tuple.Item2 < huntLogPendingMark.RemainingKills)
				{
					huntLogPendingMark.ConsecutiveNoProgressScans = 0;
					huntLogPendingMark.Deferred = false;
				}
				huntLogPendingMark.RemainingKills = tuple.Item2;
				list.Add(new PendingMarkWorkItem
				{
					Mark = tuple.Item1,
					Checkpoint = huntLogPendingMark
				});
			}
		}
		foreach (var item in list2)
		{
			list.Add(new PendingMarkWorkItem
			{
				Mark = item.Item1,
				Checkpoint = CreatePendingMarkCheckpoint(item.Item1, context, item.Item2)
			});
		}
		return list;
	}

	private async Task<bool> ProcessOverworldMarksAsync(List<PendingMarkWorkItem> workItems, PendingMarkContext context, int expectedLogRank, bool returnWhenAllDeferred, CancellationToken token)
	{
		uint preferredTerritory = 0u;
		while (true)
		{
			token.ThrowIfCancellationRequested();
			await RefreshPendingMarkCountsAsync(workItems);
			PersistPendingMarkQueue(workItems, context);
			List<PendingMarkWorkItem> pending = workItems.Where((PendingMarkWorkItem x) => x.Checkpoint.RemainingKills > 0 && !database.IsDutyTerritory(x.Mark.TerritoryId)).ToList();
			if (pending.Count == 0)
			{
				return false;
			}
			List<PendingMarkWorkItem> ready = pending.Where((PendingMarkWorkItem x) => !x.Checkpoint.Deferred).ToList();
			if (ready.Count == 0)
			{
				if (returnWhenAllDeferred)
				{
					break;
				}
				string finalMark = ((pending.Count == 1) ? database.GetMarkName(pending[0].Mark) : null);
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = ((finalMark == null) ? "Waiting before retrying deferred hunt marks" : ("Waiting before retrying " + finalMark + " after no-progress scans"));
					if (finalMark != null)
					{
						s.CurrentMarkName = $"{finalMark} ({pending[0].Checkpoint.RemainingKills} left)";
					}
				});
				PersistPendingMarkQueue(workItems, context);
				await Task.Delay(DeferredMarkRetryDelay, token);
				foreach (PendingMarkWorkItem item in pending)
				{
					item.Checkpoint.ConsecutiveNoProgressScans = 0;
					item.Checkpoint.Deferred = false;
				}
				PersistPendingMarkQueue(workItems, context);
				continue;
			}
			if (preferredTerritory == 0 || ready.All((PendingMarkWorkItem x) => x.Mark.TerritoryId != preferredTerritory))
			{
				preferredTerritory = ready[0].Mark.TerritoryId;
			}
			PendingMarkWorkItem current2 = ready.First((PendingMarkWorkItem x) => x.Mark.TerritoryId == preferredTerritory);
			switch (await ProcessOverworldMarkUntilDeferredAsync(current2, workItems, context, expectedLogRank, token))
			{
			case HuntScanResult.LogRankChanged:
				return true;
			case HuntScanResult.TravelBlocked:
			{
				uint currentTerritory = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
				if (ready.FirstOrDefault((PendingMarkWorkItem x) => x.Mark.TerritoryId == currentTerritory) != null)
				{
					preferredTerritory = currentTerritory;
					log.Information($"[HuntLogs] Travel to {database.GetTerritoryName(current2.Mark.TerritoryId)} was blocked; continuing pending hunt marks in current territory {database.GetTerritoryName(currentTerritory)} first.");
				}
				else
				{
					preferredTerritory = 0u;
					log.Information($"[HuntLogs] Travel to {database.GetTerritoryName(current2.Mark.TerritoryId)} was blocked and no pending marks are ready in {database.GetTerritoryName(currentTerritory)}; retrying after a short settle.");
					await Task.Delay(DeferredMarkRetryDelay, token);
				}
				break;
			}
			default:
				if (current2.Checkpoint.RemainingKills <= 0 || current2.Checkpoint.Deferred)
				{
					await ClearNearbyAggroBeforeTravelAsync("leaving " + database.GetMarkName(current2.Mark), token);
				}
				break;
			}
		}
		return false;
	}

	private async Task<HuntScanResult> ProcessOverworldMarkUntilDeferredAsync(PendingMarkWorkItem item, List<PendingMarkWorkItem> workItems, PendingMarkContext context, int expectedLogRank, CancellationToken token)
	{
		HuntMark mark = item.Mark;
		string markName = database.GetMarkName(mark);
		int noProgressLimit = Math.Clamp(configuration.HuntLogs.MaxMarkRetries, 1, 100);
		if (!item.Checkpoint.Deferred && item.Checkpoint.ConsecutiveNoProgressScans >= noProgressLimit)
		{
			item.Checkpoint.ConsecutiveNoProgressScans = 0;
			PersistPendingMarkQueue(workItems, context);
		}
		while (item.Checkpoint.RemainingKills > 0)
		{
			token.ThrowIfCancellationRequested();
			if (await GetMonsterNoteRankAsync(mark.MonsterNoteId) != expectedLogRank)
			{
				return HuntScanResult.LogRankChanged;
			}
			int before = await GetOpenMonsterNoteKillsAsync(mark);
			if (before <= 0)
			{
				item.Checkpoint.RemainingKills = 0;
				PersistPendingMarkQueue(workItems, context);
				return HuntScanResult.NoProgress;
			}
			if (before < item.Checkpoint.RemainingKills)
			{
				item.Checkpoint.ConsecutiveNoProgressScans = 0;
				item.Checkpoint.Deferred = false;
			}
			item.Checkpoint.RemainingKills = before;
			PersistPendingMarkQueue(workItems, context);
			HuntScanResult scanResult = await ScanOverworldMarkAsync(mark, token);
			if (scanResult == HuntScanResult.LogRankChanged)
			{
				return HuntScanResult.LogRankChanged;
			}
			int num = await GetOpenMonsterNoteKillsAsync(mark);
			item.Checkpoint.RemainingKills = num;
			if (num < before)
			{
				item.Checkpoint.ConsecutiveNoProgressScans = 0;
				item.Checkpoint.Deferred = false;
				log.Information($"[HuntLogs] {markName} scan made progress ({before} -> {num}); continuing with this mark.");
			}
			else
			{
				switch (scanResult)
				{
				case HuntScanResult.LiveTargetStillPending:
					item.Checkpoint.ConsecutiveNoProgressScans = 0;
					item.Checkpoint.Deferred = false;
					log.Information("[HuntLogs] " + markName + " is still loaded and alive; keeping it active instead of deferring it.");
					break;
				case HuntScanResult.TravelBlocked:
					log.Information($"[HuntLogs] Travel to {database.GetTerritoryName(mark.TerritoryId)} for {markName} did not complete; " + "leaving the mark queued and returning to hunt scheduling.");
					PersistPendingMarkQueue(workItems, context);
					return HuntScanResult.TravelBlocked;
				default:
					item.Checkpoint.ConsecutiveNoProgressScans++;
					if (item.Checkpoint.ConsecutiveNoProgressScans >= noProgressLimit)
					{
						item.Checkpoint.Deferred = true;
						log.Information($"[HuntLogs] Deferring {markName} after {item.Checkpoint.ConsecutiveNoProgressScans} consecutive scans without progress; it remains mandatory and queued.");
					}
					break;
				}
			}
			PersistPendingMarkQueue(workItems, context);
			if (num <= 0 || item.Checkpoint.Deferred)
			{
				return HuntScanResult.NoProgress;
			}
		}
		return HuntScanResult.NoProgress;
	}

	private async Task<HuntScanResult> ScanOverworldMarkAsync(HuntMark mark, CancellationToken token)
	{
		string markName = database.GetMarkName(mark);
		int expectedLogRank = await GetMonsterNoteRankAsync(mark.MonsterNoteId);
		mark.IsCurrentTarget = true;
		try
		{
			token.ThrowIfCancellationRequested();
			if (mark.Positions.Count == 0)
			{
				throw new InvalidOperationException("Hunt mark " + markName + " has no known ARR positions.");
			}
			HuntMovementPolicy movementPolicy = CreateHuntMovementPolicy(mark, markName);
			List<Vector3> orderedPositions = new List<Vector3>();
			int nextStoredPositionIndex = 0;
			while (true)
			{
				token.ThrowIfCancellationRequested();
				int openKills = await GetOpenMonsterNoteKillsAsync(mark);
				if (await GetMonsterNoteRankAsync(mark.MonsterNoteId) != expectedLogRank)
				{
					return HuntScanResult.LogRankChanged;
				}
				if (openKills <= 0)
				{
					return HuntScanResult.NoProgress;
				}
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentMarkName = $"{markName} ({openKills} left)";
					s.CurrentStep = "Scanning for " + markName;
				});
				if (await RunOnFrameworkThreadAsync(() => clientState.TerritoryType) != mark.TerritoryId)
				{
					if (orderedPositions.Count == 0)
					{
						orderedPositions = await SortPositionsByDistanceAsync(mark.Positions);
					}
					if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
					{
						return HuntScanResult.NoProgress;
					}
					HuntTravelResult huntTravelResult = await TryTravelToTerritoryAsync(mark.TerritoryId, orderedPositions[0], token);
					if (!huntTravelResult.Arrived)
					{
						log.Warning("[HuntLogs] Travel for " + markName + " is recoverable: " + huntTravelResult.FailureReason);
						return HuntScanResult.TravelBlocked;
					}
				}
				else
				{
					if (await ResolveCombatIfNeededAsync("pursuing " + markName, mark.TerritoryId, token))
					{
						continue;
					}
					await MaintainCompanionAsync(token);
					bool flag = mark.FateId != 0;
					if (flag)
					{
						flag = !(await PrepareMatchingFateForCombatAsync(mark, markName, expectedLogRank, movementPolicy, token));
					}
					if (flag)
					{
						return HuntScanResult.NoProgress;
					}
					if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
					{
						return HuntScanResult.NoProgress;
					}
					IBattleNpc battleNpc = await FindNearestMarkNpcAsync(mark, markName);
					if (battleNpc == null && mark.FateId != 0)
					{
						if (!(await WaitForMatchingFateTargetAsync(mark, markName, expectedLogRank, token)))
						{
							return HuntScanResult.NoProgress;
						}
						battleNpc = await FindNearestMarkNpcAsync(mark, markName);
					}
					if (battleNpc != null)
					{
						if (!(await StopNavigationForPursuitAsync(mark.TerritoryId, markName, "loaded hunt target is available", token)))
						{
							return HuntScanResult.NoProgress;
						}
						await KillVisibleMarkTargetsAsync(mark, markName, expectedLogRank, movementPolicy, token);
						if (await GetMonsterNoteRankAsync(mark.MonsterNoteId) != expectedLogRank)
						{
							return HuntScanResult.LogRankChanged;
						}
						await Task.Delay(LoadedTargetRetryDelay, token);
						int remainingAfterLoadedTarget = await GetOpenMonsterNoteKillsAsync(mark);
						if (remainingAfterLoadedTarget >= openKills)
						{
							if (await HasLoadedValidMarkTargetAsync(mark, markName))
							{
								log.Information($"[HuntLogs] Loaded {markName} remains alive without registered count progress ({openKills} -> {remainingAfterLoadedTarget}); retaining this mandatory target.");
								return HuntScanResult.LiveTargetStillPending;
							}
							log.Debug($"[HuntLogs] Loaded-target handoff for {markName} returned without count progress ({openKills} -> {remainingAfterLoadedTarget}); leaving this scan for bounded retry handling.");
							return HuntScanResult.NoProgress;
						}
						continue;
					}
					if (orderedPositions.Count == 0)
					{
						orderedPositions = await SortPositionsByDistanceAsync(mark.Positions);
					}
					if (nextStoredPositionIndex >= orderedPositions.Count)
					{
						return HuntScanResult.NoProgress;
					}
					Vector3 position = orderedPositions[nextStoredPositionIndex++];
					if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
					{
						return HuntScanResult.NoProgress;
					}
					await TryUseLocalHuntRouteAsync(mark.TerritoryId, position, token);
					Vector3 meshPoint;
					try
					{
						meshPoint = await ProjectHuntPositionAsync(mark.TerritoryId, position, token);
					}
					catch (InvalidOperationException ex) when (IsHuntTerritoryMismatchException(ex))
					{
						log.Warning("[HuntLogs] Stored-position scan for " + markName + " paused because territory changed: " + ex.Message);
						return HuntScanResult.TravelBlocked;
					}
					if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
					{
						return HuntScanResult.NoProgress;
					}
					if (!(await TryMoveToHuntLocationAsync(meshPoint, mark.TerritoryId, 7f, useCloseTo: true, token, markName, $"stored spawn {meshPoint}", () => HasLoadedValidMarkTargetAsync(mark, markName), movementPolicy)))
					{
						log.Warning($"[HuntLogs] vnavmesh could not reach {markName} position {meshPoint}; " + "returning to target pursuit instead of failing the scan.");
						await Task.Delay(500, token);
					}
					else
					{
						await KillVisibleMarkTargetsAsync(mark, markName, expectedLogRank, movementPolicy, token);
						if (await GetMonsterNoteRankAsync(mark.MonsterNoteId) != expectedLogRank)
						{
							break;
						}
						await Task.Delay(1000, token);
					}
				}
			}
			return HuntScanResult.LogRankChanged;
		}
		finally
		{
			mark.IsCurrentTarget = false;
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentMarkName = string.Empty;
			});
		}
	}

	private async Task<bool> KillVisibleMarkTargetsAsync(HuntMark mark, string markName, int expectedLogRank, HuntMovementPolicy movementPolicy, CancellationToken token)
	{
		bool killedAny = false;
		bool result = default(bool);
		while (await IsMarkWorkCurrentAsync(mark, expectedLogRank))
		{
			token.ThrowIfCancellationRequested();
			bool flag = mark.FateId != 0;
			if (flag)
			{
				flag = !(await PrepareMatchingFateForCombatAsync(mark, markName, expectedLogRank, movementPolicy, token));
			}
			if (flag)
			{
				return killedAny;
			}
			IBattleNpc target = await FindNearestMarkNpcAsync(mark, markName);
			if (target == null)
			{
				if (!(await WaitForMatchingFateTargetAsync(mark, markName, expectedLogRank, token)))
				{
					flag = killedAny;
					if (flag)
					{
						flag = await WaitForRequiredMarkRespawnAsync(mark, markName, expectedLogRank, token);
					}
					if (!flag)
					{
						return killedAny;
					}
				}
				continue;
			}
			int beforeOpenKills = await GetOpenMonsterNoteKillsAsync(mark);
			int beforeLogRank = await GetMonsterNoteRankAsync(mark.MonsterNoteId);
			if (beforeOpenKills <= 0 || beforeLogRank != expectedLogRank)
			{
				return killedAny;
			}
			MarkProcessingOutcome outcome = default(MarkProcessingOutcome);
			bool combatStopped = false;
			ulong expectedTargetObjectId = target.GameObjectId;
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Killing " + markName;
				s.CurrentMarkName = $"{markName} ({beforeOpenKills} left)";
			});
			try
			{
				HuntCombatTarget combatTarget;
				uint runtimeFateId;
				if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
				{
					result = killedAny;
				}
				else
				{
					bool forceFlightApproach = await RunOnFrameworkThreadAsync(() => condition[ConditionFlag.InFlight]);
					if (!(await TryMoveToObjectAsync(target, mark.TerritoryId, 4f, token, markName, () => HasLoadedValidMarkTargetAsync(mark, markName), movementPolicy, forceFlightApproach)))
					{
						log.Warning("[HuntLogs] Could not path to loaded " + markName + "; returning to target pursuit.");
						await Task.Delay(500, token);
						continue;
					}
					if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
					{
						result = killedAny;
					}
					else
					{
						if (!(await DismountForCombatAsync(token)))
						{
							log.Warning("[HuntLogs] Could not verify dismounted state before fighting " + markName + "; retrying target pursuit.");
							await Task.Delay(500, token);
							continue;
						}
						if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
						{
							result = killedAny;
						}
						else
						{
							combatTarget = await ReacquireHuntCombatTargetAsync(mark, markName, expectedTargetObjectId);
							if (combatTarget == null)
							{
								log.Information("[HuntLogs] " + markName + " despawned after movement or dismount; returning to pursuit.");
								result = killedAny;
							}
							else
							{
								runtimeFateId = combatTarget.RuntimeFateId;
								if (runtimeFateId == 0)
								{
									goto IL_0f84;
								}
								if (!(await EnsureFateSyncForTargetAsync(markName, runtimeFateId, token)))
								{
									result = killedAny;
								}
								else
								{
									HuntCombatTarget postSyncTarget = await ReacquireHuntCombatTargetAsync(mark, markName, combatTarget.GameObjectId);
									if (postSyncTarget == null)
									{
										log.Information($"[HuntLogs] {markName} despawned while validating FATE {runtimeFateId} after level sync; " + "returning to pursuit without enabling combat.");
										result = killedAny;
									}
									else if (postSyncTarget.RuntimeFateId != runtimeFateId)
									{
										log.Information($"[HuntLogs] {markName} changed FATE identity after level sync ({runtimeFateId} -> {postSyncTarget.RuntimeFateId}); returning to pursuit without enabling combat.");
										result = killedAny;
									}
									else
									{
										if (await ValidateRuntimeFateForCombatAsync(markName, runtimeFateId))
										{
											combatTarget = postSyncTarget;
											goto IL_0f84;
										}
										result = killedAny;
									}
								}
							}
						}
					}
				}
				goto end_IL_0583;
				IL_0f84:
				if (!(await SetAndVerifyHuntTargetAsync(combatTarget.Target, markName, token)))
				{
					log.Warning("[HuntLogs] Could not verify " + markName + " as the selected combat target; returning to pursuit without enabling combat.");
					result = killedAny;
				}
				else
				{
					await TryEngageHuntTargetAsync(mark, markName, combatTarget.Target, beforeOpenKills, token);
					flag = runtimeFateId != 0;
					if (flag)
					{
						flag = !(await RevalidateHuntCombatTargetAsync(mark, markName, combatTarget, runtimeFateId));
					}
					if (flag)
					{
						result = killedAny;
					}
					else
					{
						await Task.Delay(CombatBackendActivationSettleTime, token);
						if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
						{
							result = killedAny;
						}
						else if (!(await RunOnFrameworkThreadAsync(() => combatTarget.Target.IsTargetable && !combatTarget.Target.IsDead && combatTarget.Target.CurrentHp != 0 && targetManager.Target?.GameObjectId == combatTarget.Target.GameObjectId)))
						{
							log.Information("[HuntLogs] " + markName + " was no longer the valid selected target after the Attack-to-combat settlement interval; returning to pursuit without enabling combat.");
							result = killedAny;
						}
						else
						{
							await EnableCombatAsync();
							outcome = await WaitForMarkProcessingOutcomeAsync(mark, markName, runtimeFateId, combatTarget.GameObjectId, beforeOpenKills, beforeLogRank, TimeSpan.FromSeconds(Math.Clamp(configuration.HuntLogs.KillTimeoutSeconds, 15, 300)), token);
							if (outcome.Registered)
							{
								await DisableCombatAsync();
								await ResetTargetAsync();
								combatStopped = true;
								log.Information($"[HuntLogs] Registered {markName} by {outcome.Result} (count {outcome.BeforeCount}->{outcome.AfterCount}, rank {outcome.BeforeLogRank}->{outcome.AfterLogRank}); " + "combat and target were cleared immediately.");
							}
							flag = outcome.Result == MarkProcessingResult.TimedOut;
							if (flag)
							{
								flag = await RunOnFrameworkThreadAsync(() => condition[ConditionFlag.InCombat] && !IsDeadOrUnconsciousUnsafe());
							}
							if (flag)
							{
								log.Information("[HuntLogs] Kill wait timed out for " + markName + ", but combat is still active; resolving combat before deciding whether to retry.");
							}
							flag = outcome.Result == MarkProcessingResult.Unconscious;
							if (!flag)
							{
								flag = await IsUnconsciousAsync();
							}
							if (flag)
							{
								await HandleDeathRecoveryAsync(mark, token);
								result = killedAny;
							}
							else if (outcome.Result == MarkProcessingResult.FateEnded)
							{
								result = killedAny;
							}
							else
							{
								killedAny |= outcome.Registered;
							}
						}
					}
				}
				end_IL_0583:;
			}
			finally
			{
				if (!combatStopped)
				{
					await DisableCombatAsync();
					await ResetTargetAsync();
				}
			}
			int num;
			if (num == 2)
			{
				return result;
			}
			if (outcome.Registered)
			{
				await ResolveCombatIfNeededAsync("resolving unavoidable aggro after registered " + markName + " kill", mark.TerritoryId, token);
			}
			int num2 = await GetOpenMonsterNoteKillsAsync(mark);
			if (num2 < beforeOpenKills)
			{
				if (!outcome.Registered)
				{
					log.Information($"[HuntLogs] Hunt-log count updated for {markName} after combat cleanup ({beforeOpenKills} -> {num2}).");
				}
				killedAny = true;
			}
			if (!killedAny)
			{
				return false;
			}
		}
		return killedAny;
	}

	private async Task<bool> WaitForRequiredMarkRespawnAsync(HuntMark mark, string markName, int expectedLogRank, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Waiting for " + markName + " respawn";
		});
		log.Information($"[HuntLogs] No loaded {markName} remains after registered progress; waiting up to {RequiredMarkRespawnWait.TotalSeconds:F0}s at this spawn before resuming route scans.");
		while (DateTime.UtcNow - started < RequiredMarkRespawnWait)
		{
			token.ThrowIfCancellationRequested();
			if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
			{
				return false;
			}
			if (await TryHandleDeathRecoveryAsync(mark.TerritoryId, "waiting for required " + markName + " respawn", token))
			{
				return false;
			}
			if (await ResolveCombatIfNeededAsync("waiting for required " + markName + " respawn", mark.TerritoryId, token))
			{
				started = DateTime.UtcNow;
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = "Waiting for " + markName + " respawn";
				});
			}
			if (await FindNearestMarkNpcAsync(mark, markName) != null)
			{
				log.Information($"[HuntLogs] Required {markName} respawn became available after {(DateTime.UtcNow - started).TotalSeconds:F1}s; continuing this mark without changing spawn.");
				return true;
			}
			await Task.Delay(MatchingFatePollDelay, token);
		}
		log.Information($"[HuntLogs] No required {markName} respawn appeared within {RequiredMarkRespawnWait.TotalSeconds:F0}s; resuming bounded stored-position scans.");
		return false;
	}

	private async Task<bool> IsMarkWorkCurrentAsync(HuntMark mark, int expectedLogRank)
	{
		if (await GetOpenMonsterNoteKillsAsync(mark) <= 0)
		{
			return false;
		}
		return await GetMonsterNoteRankAsync(mark.MonsterNoteId) == expectedLogRank;
	}

	private unsafe async Task<MarkProcessingOutcome> WaitForMarkProcessingOutcomeAsync(HuntMark mark, string markName, uint runtimeFateId, ulong expectedTargetObjectId, int beforeCount, int beforeLogRank, TimeSpan timeout, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		bool combatStoppedForLostTarget = false;
		bool targetRepinLogged = false;
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			(int Count, int Rank, bool Unconscious, bool FateActive, bool TargetLive, bool TargetRepinned) state = await RunOnFrameworkThreadAsync(delegate
			{
				int openMonsterNoteKillsUnsafe = GetOpenMonsterNoteKillsUnsafe(mark);
				MonsterNoteManager* ptr = MonsterNoteManager.Instance();
				int item = ((ptr == null) ? beforeLogRank : ptr->RankData[mark.MonsterNoteId].Rank);
				bool item2 = IsDeadOrUnconsciousUnsafe();
				MatchingFateState matchingFateState = ((runtimeFateId == 0) ? default(MatchingFateState) : GetMatchingFateStateUnsafe(runtimeFateId));
				bool item3 = runtimeFateId == 0 || (matchingFateState.Active && matchingFateState.Joined && matchingFateState.JoinedFateId == runtimeFateId && (!matchingFateState.RequiresLevelSync || matchingFateState.IsLevelSynced));
				IBattleNpc battleNpc = objectTable.OfType<IBattleNpc>().FirstOrDefault((IBattleNpc x) => x.GameObjectId == expectedTargetObjectId);
				bool flag = battleNpc != null && IsMatchingMarkIdentityUnsafe(mark, markName, battleNpc) && battleNpc.IsTargetable && !battleNpc.IsDead && battleNpc.CurrentHp != 0;
				bool item4 = false;
				if (flag)
				{
					IGameObject? target = targetManager.Target;
					if (target == null || target.GameObjectId != expectedTargetObjectId)
					{
						targetManager.Target = battleNpc;
						item4 = true;
					}
				}
				return (Count: openMonsterNoteKillsUnsafe, Rank: item, Unconscious: item2, FateActive: item3, TargetLive: flag, TargetRepinned: item4);
			});
			if (state.Count < beforeCount)
			{
				return new MarkProcessingOutcome(MarkProcessingResult.CountDecreased, beforeCount, state.Count, beforeLogRank, state.Rank);
			}
			if (state.Rank != beforeLogRank)
			{
				return new MarkProcessingOutcome(MarkProcessingResult.LogRankAdvanced, beforeCount, state.Count, beforeLogRank, state.Rank);
			}
			if (state.Unconscious)
			{
				return new MarkProcessingOutcome(MarkProcessingResult.Unconscious, beforeCount, state.Count, beforeLogRank, state.Rank);
			}
			if (state.TargetRepinned && !targetRepinLogged)
			{
				targetRepinLogged = true;
				log.Information($"[HuntLogs] Restored the exact hunt target object {expectedTargetObjectId} after the combat backend changed targets.");
			}
			if (!state.TargetLive && !combatStoppedForLostTarget)
			{
				combatStoppedForLostTarget = true;
				await DisableCombatAsync();
				await ResetTargetAsync();
				log.Information($"[HuntLogs] Exact hunt target object {expectedTargetObjectId} is no longer live; " + "combat was stopped while awaiting authoritative Hunt Log progress.");
			}
			if (!state.FateActive)
			{
				return new MarkProcessingOutcome(MarkProcessingResult.FateEnded, beforeCount, state.Count, beforeLogRank, state.Rank);
			}
			await Task.Delay(250, token);
		}
		int afterCount = await GetOpenMonsterNoteKillsAsync(mark);
		int afterLogRank = await GetMonsterNoteRankAsync(mark.MonsterNoteId);
		return new MarkProcessingOutcome(MarkProcessingResult.TimedOut, beforeCount, afterCount, beforeLogRank, afterLogRank);
	}

	private async Task<DutyMarkProcessingResult> ProcessDutyMarksAsync(List<HuntMark> dutyMarks, PendingMarkContext context, int expectedLogRank, CancellationToken token)
	{
		List<uint> list = dutyMarks.Select((HuntMark x) => x.TerritoryId).Distinct().ToList();
		foreach (uint duty in list)
		{
			token.ThrowIfCancellationRequested();
			string dutyName = database.GetTerritoryName(duty);
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Preparing duty " + dutyName;
				s.CurrentMarkName = dutyName;
			});
			List<HuntMark> trackedMarks = dutyMarks.Where((HuntMark x) => x.TerritoryId == duty).ToList();
			int preUnlockTotal = (await ReadLiveDutyRemainingAsync(trackedMarks, token)).Sum<(HuntMark, int)>(((HuntMark Mark, int Remaining) x) => x.Remaining);
			if (preUnlockTotal == 0)
			{
				DiscardCompletedDutyContext(trackedMarks, context);
				log.Information("[HuntLogs] Skipping " + dutyName + " duty handoff: all associated Hunt Log marks are already complete in the live MonsterNote state.");
				if (context.IsGrandCompanyLog)
				{
					log.Information("[HuntLogs] Discarded stale duty context and re-evaluating GC progression without resetting completed overworld marks.");
				}
				continue;
			}
			if (configuration.HuntLogs.SkipDutyMarks)
			{
				RecordDutyBlocker(string.Empty, dutyName, "Skipped because Skip duty marks is enabled.");
				log.Information($"[HuntLogs] {"Skipped because Skip duty marks is enabled."} Duty={dutyName} ({duty}); no duty IPC was invoked.");
				return new DutyMarkProcessingResult(Succeeded: false, LogRankChanged: false);
			}
			if (!(await EnsureHuntDutyUnlockedAsync(duty, dutyName, token)))
			{
				return new DutyMarkProcessingResult(Succeeded: false, LogRankChanged: false);
			}
			HuntDutyAttemptResult lastAttempt = default(HuntDutyAttemptResult);
			int lastRemaining = preUnlockTotal;
			int attempt;
			for (attempt = 1; attempt <= 3; attempt++)
			{
				token.ThrowIfCancellationRequested();
				lastRemaining = (await ReadLiveDutyRemainingAsync(trackedMarks, token)).Sum<(HuntMark, int)>(((HuntMark Mark, int Remaining) x) => x.Remaining);
				if (lastRemaining == 0)
				{
					DiscardCompletedDutyContext(trackedMarks, context);
					log.Information("[HuntLogs] Skipping " + dutyName + " duty handoff: all associated Hunt Log marks are already complete in the live MonsterNote state.");
					if (context.IsGrandCompanyLog)
					{
						log.Information("[HuntLogs] Discarded stale duty context and re-evaluating GC progression without resetting completed overworld marks.");
					}
					break;
				}
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = $"Duty attempt {attempt}/{3}: {dutyName}";
				});
				if (!(await StopQuestionableBeforeDutyHandoffAsync(dutyName, token)))
				{
					return new DutyMarkProcessingResult(Succeeded: false, LogRankChanged: false);
				}
				lastAttempt = await RunHuntDutyAttemptAsync(duty, dutyName, attempt, token);
				List<(HuntMark Mark, int Remaining)> remainingByMark = new List<(HuntMark, int)>();
				foreach (HuntMark item2 in trackedMarks)
				{
					List<(HuntMark Mark, int Remaining)> list2 = remainingByMark;
					HuntMark item = item2;
					list2.Add((item, await GetOpenMonsterNoteKillsAsync(item2)));
				}
				lastRemaining = remainingByMark.Sum<(HuntMark, int)>(((HuntMark Mark, int Remaining) x) => x.Remaining);
				int num = await GetMonsterNoteRankAsync(dutyMarks[0].MonsterNoteId);
				log.Information($"[HuntLogs] Duty attempt {attempt}/{3} live refresh: duty={dutyName} ({duty}), status={lastAttempt.Status}, entered={lastAttempt.EnteredDuty}, rank={expectedLogRank}->{num}, remaining={lastRemaining}, marks=[" + string.Join(", ", remainingByMark.Select<(HuntMark, int), string>(((HuntMark Mark, int Remaining) x) => $"{database.GetMarkName(x.Mark)}:{x.Remaining}")) + "].");
				if (num != expectedLogRank)
				{
					return new DutyMarkProcessingResult(Succeeded: true, LogRankChanged: true);
				}
				if (lastRemaining == 0)
				{
					break;
				}
				if (attempt < 3)
				{
					log.Warning($"[HuntLogs] {dutyName} still has {lastRemaining} hunt-log kills after attempt {attempt}/{3}; resetting transient duty state and restarting the same dungeon.");
				}
			}
			if (lastRemaining <= 0)
			{
				continue;
			}
			string backendName = HuntDutyRunner.GetBackendName(lastAttempt.Backend);
			string text = $"Three duty attempts completed without registering all marks; {lastRemaining} kills remain. Last attempt status={lastAttempt.Status}, enteredDuty={lastAttempt.EnteredDuty}. " + (string.IsNullOrWhiteSpace(lastAttempt.FailureReason) ? string.Empty : ("Last failure: " + lastAttempt.FailureReason));
			RecordDutyBlocker(backendName, dutyName, text.Trim());
			log.Warning($"[HuntLogs] Blocking only the current character after bounded duty retries. Duty={dutyName} ({duty}) Backend={backendName} Blocker={text}");
			return new DutyMarkProcessingResult(Succeeded: false, LogRankChanged: false);
		}
		return new DutyMarkProcessingResult(Succeeded: true, LogRankChanged: false);
	}

	private async Task<List<(HuntMark Mark, int Remaining)>> ReadLiveDutyRemainingAsync(IReadOnlyList<HuntMark> marks, CancellationToken token)
	{
		List<(HuntMark Mark, int Remaining)> remaining = new List<(HuntMark, int)>(marks.Count);
		foreach (HuntMark mark in marks)
		{
			token.ThrowIfCancellationRequested();
			List<(HuntMark Mark, int Remaining)> list = remaining;
			HuntMark item = mark;
			list.Add((item, await GetOpenMonsterNoteKillsAsync(mark)));
		}
		return remaining;
	}

	private void DiscardCompletedDutyContext(IReadOnlyCollection<HuntMark> completedDutyMarks, PendingMarkContext context)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.PendingMarks.RemoveAll((HuntLogPendingMark pending) => IsSamePendingMarkContext(pending, context) && completedDutyMarks.Any((HuntMark mark) => IsSamePendingMark(pending, mark)));
		});
		SaveCheckpointFromState(active: true);
	}

	private async Task<bool> TryRunHuntDutyAsync(uint dutyId, string dutyName, CancellationToken token, bool ensureUnlocked = true)
	{
		if (configuration.HuntLogs.SkipDutyMarks)
		{
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.DutyBackend = string.Empty;
				s.DutyBlocker = "Skipped because Skip duty marks is enabled.";
				s.CurrentStep = "Skipped duty " + dutyName;
			});
			MarkCurrentCharacterStatus("Blocked: duty marks skipped by setting (" + dutyName + ")", markSkipped: true);
			log.Information($"[HuntLogs] {"Skipped because Skip duty marks is enabled."} Duty={dutyName} ({dutyId}); no duty IPC was invoked.");
			return false;
		}
		bool flag = ensureUnlocked;
		if (flag)
		{
			flag = !(await EnsureHuntDutyUnlockedAsync(dutyId, dutyName, token));
		}
		if (flag)
		{
			return false;
		}
		if (!(await StopQuestionableBeforeDutyHandoffAsync(dutyName, token)))
		{
			return false;
		}
		HuntDutyAttemptResult huntDutyAttemptResult = await RunHuntDutyAttemptAsync(dutyId, dutyName, 1, token);
		if (huntDutyAttemptResult.Status == HuntDutyAttemptStatus.Exited)
		{
			return true;
		}
		string backendName = HuntDutyRunner.GetBackendName(huntDutyAttemptResult.Backend);
		RecordDutyBlocker(backendName, dutyName, huntDutyAttemptResult.FailureReason);
		return false;
	}

	private async Task<HuntDutyAttemptResult> RunHuntDutyAttemptAsync(uint dutyId, string dutyName, int attemptNumber, CancellationToken token)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.DutyBackend = string.Empty;
			s.DutyBlocker = string.Empty;
			s.CurrentStep = "Selecting DAD/AutoDuty backend for " + dutyName;
		});
		HuntDutyBackend backend = HuntDutyBackend.None;
		HuntDutyAttemptResult result;
		bool cleanupSucceeded;
		try
		{
			HuntDutyStartResult start = huntDutyRunner.StartDuty(dutyId, configuration.HuntLogs.SoloUnsyncedLogDuty);
			backend = start.Backend;
			string backendName = HuntDutyRunner.GetBackendName(backend);
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.DutyBackend = backendName;
				s.DutyBlocker = start.Blocker;
			});
			if (!start.Started)
			{
				result = new HuntDutyAttemptResult(HuntDutyAttemptStatus.Failed, backend, EnteredDuty: false, string.IsNullOrWhiteSpace(start.Blocker) ? (backendName + " did not start " + dutyName + ".") : start.Blocker);
			}
			else
			{
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = $"Running {dutyName} with {backendName} (attempt {attemptNumber}/{3})";
				});
				result = await MonitorHuntDutyAttemptAsync(dutyId, dutyName, backend, token);
			}
		}
		finally
		{
			cleanupSucceeded = await StopOwnedDutyAttemptAsync(backend, $"terminal cleanup for {dutyName} attempt {attemptNumber}", CancellationToken.None);
			await ResetTransientDutyAttemptStateAsync(dutyId, $"after {dutyName} attempt {attemptNumber}", CancellationToken.None);
		}
		if (!cleanupSucceeded)
		{
			return new HuntDutyAttemptResult(HuntDutyAttemptStatus.Failed, backend, result.EnteredDuty, (result.FailureReason + " Terminal " + HuntDutyRunner.GetBackendName(backend) + " stop state was not verified.").Trim());
		}
		return result;
	}

	private async Task<HuntDutyAttemptResult> MonitorHuntDutyAttemptAsync(uint dutyId, string dutyName, HuntDutyBackend backend, CancellationToken token)
	{
		string backendName = HuntDutyRunner.GetBackendName(backend);
		DateTime started = DateTime.UtcNow;
		bool enteredDuty = false;
		DateTime? exitTransitionStarted = null;
		while (exitTransitionStarted.HasValue || DateTime.UtcNow - started < HuntDutyTimeout)
		{
			token.ThrowIfCancellationRequested();
			HuntDutyRuntimeState huntDutyRuntimeState = await RunOnFrameworkThreadAsync((Func<HuntDutyRuntimeState>)GetHuntDutyRuntimeStateUnsafe);
			if (!enteredDuty && huntDutyRuntimeState.TerritoryId == dutyId && huntDutyRuntimeState.BoundByDuty)
			{
				enteredDuty = true;
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = "Entered " + dutyName + " with " + backendName;
				});
				log.Information($"[HuntLogs] Duty entry observed. Backend={backendName} Duty={dutyName} ({dutyId}) Territory={huntDutyRuntimeState.TerritoryId} BoundByDuty={huntDutyRuntimeState.BoundByDuty}.");
			}
			if (enteredDuty)
			{
				bool flag = huntDutyRuntimeState.TerritoryId != dutyId || !huntDutyRuntimeState.BoundByDuty;
				if (huntDutyRuntimeState.TerritoryId != dutyId && !huntDutyRuntimeState.BoundByDuty && !huntDutyRuntimeState.BetweenAreas && huntDutyRuntimeState.CharacterReady)
				{
					log.Information($"[HuntLogs] Duty exit observed. Backend={backendName} Duty={dutyName} ({dutyId}) Territory={huntDutyRuntimeState.TerritoryId} BoundByDuty={huntDutyRuntimeState.BoundByDuty} BetweenAreas={huntDutyRuntimeState.BetweenAreas} CharacterReady={huntDutyRuntimeState.CharacterReady}.");
					return new HuntDutyAttemptResult(HuntDutyAttemptStatus.Exited, backend, EnteredDuty: true, string.Empty);
				}
				if (flag)
				{
					if (!exitTransitionStarted.HasValue)
					{
						exitTransitionStarted = DateTime.UtcNow;
						UpdateState(delegate(HuntLogAutomationState s)
						{
							s.CurrentStep = "Waiting for " + dutyName + " exit to settle";
						});
						log.Information($"[HuntLogs] Duty exit transition observed. Backend={backendName} Duty={dutyName} ({dutyId}) Territory={huntDutyRuntimeState.TerritoryId} BoundByDuty={huntDutyRuntimeState.BoundByDuty} BetweenAreas={huntDutyRuntimeState.BetweenAreas} CharacterReady={huntDutyRuntimeState.CharacterReady}; " + "waiting for the post-duty character state before cleanup and live refresh.");
					}
					if (DateTime.UtcNow - exitTransitionStarted.Value >= HuntDutyExitSettlementTimeout)
					{
						return new HuntDutyAttemptResult(HuntDutyAttemptStatus.TimedOut, backend, EnteredDuty: true, $"Duty exit transition did not settle within {HuntDutyExitSettlementTimeout.TotalMinutes:F0} minutes. Territory={huntDutyRuntimeState.TerritoryId}, boundByDuty={huntDutyRuntimeState.BoundByDuty}, betweenAreas={huntDutyRuntimeState.BetweenAreas}, characterReady={huntDutyRuntimeState.CharacterReady}.");
					}
					await Task.Delay(250, token);
					continue;
				}
				if (exitTransitionStarted.HasValue)
				{
					log.Warning($"[HuntLogs] Duty exit transition cleared before settlement. Backend={backendName} Duty={dutyName} ({dutyId}); continuing duty monitoring.");
					exitTransitionStarted = null;
				}
			}
			HuntDutyPollResult huntDutyPollResult = huntDutyRunner.PollOwnedSession();
			if (!huntDutyPollResult.Succeeded)
			{
				return new HuntDutyAttemptResult(HuntDutyAttemptStatus.Failed, backend, enteredDuty, huntDutyPollResult.Blocker);
			}
			if (huntDutyPollResult.IsStopped)
			{
				string failureReason = (enteredDuty ? $"{backendName} stopped while the client was still bound in territory {huntDutyRuntimeState.TerritoryId}." : (backendName + " stopped before duty entry was observed."));
				log.Warning($"[HuntLogs] Premature duty backend completion. Backend={backendName} Duty={dutyName} ({dutyId}) Entered={enteredDuty} Territory={huntDutyRuntimeState.TerritoryId} BoundByDuty={huntDutyRuntimeState.BoundByDuty}.");
				return new HuntDutyAttemptResult(HuntDutyAttemptStatus.PrematureBackendStop, backend, enteredDuty, failureReason);
			}
			await Task.Delay(250, token);
		}
		return new HuntDutyAttemptResult(HuntDutyAttemptStatus.TimedOut, backend, enteredDuty, $"Timed out after {HuntDutyTimeout.TotalMinutes:F0} minutes waiting for observed duty entry and exit.");
	}

	private HuntDutyRuntimeState GetHuntDutyRuntimeStateUnsafe()
	{
		bool boundByDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
		return new HuntDutyRuntimeState(clientState.TerritoryType, boundByDuty, condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51], IsCharacterReadyForMovementUnsafe());
	}

	private async Task<bool> StopOwnedDutyAttemptAsync(HuntDutyBackend backend, string reason, CancellationToken token)
	{
		switch (backend)
		{
		case HuntDutyBackend.None:
			return true;
		case HuntDutyBackend.AutoDuty:
		{
			bool value = await RunOnFrameworkThreadAsync(() => commandManager.ProcessCommand("/ad stop"));
			log.Information($"[HuntLogs] Issued /ad stop for {reason}; accepted={value}. " + "AutoDuty.Stop IPC remains the owned fallback and terminal verifier.");
			break;
		}
		}
		huntDutyRunner.StopOwnedSession(reason);
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < HuntDutyStopTimeout)
		{
			token.ThrowIfCancellationRequested();
			HuntDutyPollResult huntDutyPollResult = huntDutyRunner.PollOwnedSession();
			if (!huntDutyPollResult.Succeeded)
			{
				if (huntDutyPollResult.Backend == HuntDutyBackend.None)
				{
					return true;
				}
				log.Warning("[HuntLogs] Duty terminal poll failed during " + reason + ": " + huntDutyPollResult.Blocker);
				huntDutyRunner.StopOwnedSession("retry after terminal poll failure during " + reason);
			}
			else if (huntDutyPollResult.IsStopped)
			{
				huntDutyRunner.StopOwnedSession("release verified terminal ownership after " + reason);
				return true;
			}
			await Task.Delay(250, token);
		}
		log.Warning($"[HuntLogs] Timed out after {HuntDutyStopTimeout.TotalSeconds:F0}s waiting for {HuntDutyRunner.GetBackendName(backend)} IsStopped during {reason}.");
		return false;
	}

	private async Task ResetTransientDutyAttemptStateAsync(uint dutyId, string reason, CancellationToken token)
	{
		try
		{
			if (vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding())
			{
				await StopNavigationAndWaitForIdleAsync(dutyId, "duty reset " + reason, token, null, reason);
			}
			await DisableCombatAsync();
			await ResetTargetAsync();
			database.ResetCurrentTarget();
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.DutyBackend = string.Empty;
				s.DutyBlocker = string.Empty;
				s.CurrentStep = "Reset transient duty state " + reason;
			});
			log.Information("[HuntLogs] Reset transient navigation, target, combat, backend, and duty state " + reason + "; hunt-log completion and checkpoint truth were preserved.");
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Transient duty reset failed " + reason + ": " + ex.Message);
		}
	}

	private async Task<bool> EnsureHuntDutyUnlockedAsync(uint dutyId, string dutyName, CancellationToken token)
	{
		DutyUnlockResolution unlockResolution = await ResolveHuntDutyUnlockAsync(dutyId);
		if (unlockResolution.State == DutyUnlockResolutionState.Unknown)
		{
			RecordDutyBlocker("Unlock", dutyName, unlockResolution.Blocker);
			return false;
		}
		if (unlockResolution.IsUnlocked)
		{
			return true;
		}
		uint questId;
		if (dutyId == 1331)
		{
			(uint, int, uint, int) tuple = await GetPlayerInfoAsync();
			questId = AurumRank8.GetValueOrDefault(tuple.Item3)?.QuestId ?? 0;
		}
		else
		{
			questId = OrdinaryDutyUnlockQuests.GetValueOrDefault(dutyId);
		}
		if (questId == 0)
		{
			RecordDutyBlocker("Unlock", dutyName, $"Duty {dutyId} is locked and no verified non-attuning unlock quest is configured.");
			return false;
		}
		ushort normalizedQuestId = NormalizeQuestId(questId);
		log.Information($"[HuntLogs] Duty unlock quest configured for {dutyName}: LuminaQuestRow={questId}, QuestionableQuest={normalizedQuestId}.");
		if ((await GetGrandCompanyUnlockQuestStateAsync(questId)).Item2)
		{
			string text = $"Unlock quest {normalizedQuestId} (Lumina row {questId}) is already complete, but territory {dutyId} remains locked (CFC {unlockResolution.ContentFinderConditionId}, InstanceContent {unlockResolution.InstanceContentId}); Questionable was not started.";
			RecordDutyBlocker("unlock quest via Questionable", dutyName, text);
			log.Warning("[HuntLogs] " + text);
			return false;
		}
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			RecordDutyBlocker("unlock quest via Questionable", dutyName, $"Questionable is unavailable for unlock quest {questId}.");
			return false;
		}
		return await TryRunQuestionableQuestUntilDutyUnlockedAsync(questId, unlockResolution, dutyName, token);
	}

	private async Task<bool> TryRunQuestionableQuestUntilDutyUnlockedAsync(uint luminaQuestRowId, DutyUnlockResolution duty, string dutyName, CancellationToken token)
	{
		ushort questId = NormalizeQuestId(luminaQuestRowId);
		string questIdText = questId.ToString();
		log.Information($"[HuntLogs] Questionable unlock quest mapping: LuminaQuestRow={luminaQuestRowId}, QuestionableQuest={questId}, dutyTerritory={duty.TerritoryTypeId}.");
		bool result;
		try
		{
			if (!(await PrepareQuestionableQuestHandoffAsync(luminaQuestRowId, questId, "unlocking " + dutyName, token)))
			{
				RecordDutyBlocker("unlock quest via Questionable", dutyName, $"Could not stop Questionable and clear stale priority before quest {questId} (Lumina row {luminaQuestRowId}).");
				result = false;
			}
			else if (!questionableIpc.AddQuestPriority(questIdText))
			{
				RecordDutyBlocker("unlock quest via Questionable", dutyName, $"Questionable rejected unlock quest {questId} (Lumina row {luminaQuestRowId}).");
				result = false;
			}
			else
			{
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.DutyBackend = "unlock quest via Questionable";
					s.DutyBlocker = string.Empty;
					s.CurrentStep = $"Unlock quest via Questionable for {dutyName} ({questId})";
				});
				if (!(await StartQuestionableAndVerifyQuestAsync(questId, $"unlock quest {questId} (Lumina row {luminaQuestRowId})", token)))
				{
					RecordDutyBlocker("unlock quest via Questionable", dutyName, $"Questionable did not enter running state for unlock quest {questId} (Lumina row {luminaQuestRowId}).");
					result = false;
				}
				else
				{
					using MovementMonitorService.ScopedMonitoringSession movementSession = movementMonitor?.BeginScopedMonitoring($"dungeon unlock quest {questId} for {dutyName}");
					QuestionableUnlockProgressState? previousProgress = null;
					DateTime deadline = DateTime.UtcNow + QuestionableQuestTimeout;
					DateTime? questCompletedAt = null;
					while (true)
					{
						if (DateTime.UtcNow < deadline)
						{
							token.ThrowIfCancellationRequested();
							DutyUnlockResolution unlockState = await GetHuntDutyUnlockStateAsync(duty);
							if (unlockState.State == DutyUnlockResolutionState.Unknown)
							{
								RecordDutyBlocker("unlock quest via Questionable", dutyName, unlockState.Blocker);
								log.Warning("[HuntLogs] " + unlockState.Blocker);
								result = false;
								break;
							}
							QuestionableUnlockProgressState questionableUnlockProgressState = await GetQuestionableUnlockProgressStateAsync(luminaQuestRowId);
							ResetScopedMovementMonitorForProgress(movementSession, previousProgress, questionableUnlockProgressState);
							previousProgress = questionableUnlockProgressState;
							if (questionableUnlockProgressState.Completed && unlockState.IsUnlocked)
							{
								log.Information($"[HuntLogs] Questionable quest {questId} (Lumina row {luminaQuestRowId}) completed and InstanceContent {duty.InstanceContentId} unlocked.");
								result = true;
								break;
							}
							if (questionableUnlockProgressState.Completed)
							{
								if (!questCompletedAt.HasValue)
								{
									questCompletedAt = DateTime.UtcNow;
									log.Information($"[HuntLogs] Quest {questId} (Lumina row {luminaQuestRowId}) completed; allowing {QuestionableUnlockPropagationGrace.TotalSeconds:F0}s for InstanceContent unlock propagation.");
								}
								if (DateTime.UtcNow - questCompletedAt.Value >= QuestionableUnlockPropagationGrace)
								{
									string text = $"Unlock quest {questId} (Lumina row {luminaQuestRowId}) completed, but territory {duty.TerritoryTypeId} remained locked after {QuestionableUnlockPropagationGrace.TotalSeconds:F0}s (CFC {duty.ContentFinderConditionId}, InstanceContent {duty.InstanceContentId}).";
									RecordDutyBlocker("unlock quest via Questionable", dutyName, text);
									log.Warning("[HuntLogs] " + text);
									result = false;
									break;
								}
							}
							if (!(await TryRecoverScopedQuestionableUnlockAsync(movementSession, questId, $"unlock quest {questId} (Lumina row {luminaQuestRowId})", token)))
							{
								RecordDutyBlocker("unlock quest via Questionable", dutyName, $"Questionable movement recovery did not resume unlock quest {questId}.");
								result = false;
								break;
							}
							await Task.Delay(1000, token);
							continue;
						}
						DutyUnlockResolution finalUnlockState = await GetHuntDutyUnlockStateAsync(duty);
						(bool, bool, byte) tuple = await GetGrandCompanyUnlockQuestStateAsync(luminaQuestRowId);
						string blocker = $"Timed out after {QuestionableQuestTimeout.TotalMinutes:F0} minutes waiting for both quest {questId} (Lumina row {luminaQuestRowId}) completion={tuple.Item2} and territory {duty.TerritoryTypeId} unlock={finalUnlockState.IsUnlocked} (CFC {duty.ContentFinderConditionId}, InstanceContent {duty.InstanceContentId}).";
						RecordDutyBlocker("unlock quest via Questionable", dutyName, blocker);
						result = false;
						break;
					}
				}
			}
		}
		finally
		{
			await CleanupQuestionableQuestHandoffAsync($"unlock quest {questId} (Lumina row {luminaQuestRowId})", CancellationToken.None);
		}
		return result;
	}

	private async Task<bool> StopQuestionableBeforeDutyHandoffAsync(string dutyName, CancellationToken token)
	{
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			log.Debug("[HuntLogs] Questionable unavailable before duty handoff for " + dutyName + "; no Questionable stop required.");
			return true;
		}
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.DutyBackend = "duty handoff";
			s.DutyBlocker = string.Empty;
			s.CurrentStep = "Stopping Questionable before " + dutyName;
		});
		if (!(await StopQuestionableAsync("duty handoff for " + dutyName, token)))
		{
			RecordDutyBlocker("duty handoff", dutyName, "Questionable could not be stopped before handing duty execution to DAD/AutoDuty.");
			return false;
		}
		return true;
	}

	private async Task<bool> PrepareQuestionableQuestHandoffAsync(uint luminaQuestRowId, ushort questId, string context, CancellationToken token)
	{
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			return false;
		}
		if (savedQuestionablePriority != null && !RestoreSavedQuestionablePriority("before " + context))
		{
			return false;
		}
		if (!questionableIpc.TryExportQuestPriority(out string encodedQuestPriority))
		{
			log.Warning("[HuntLogs] Could not save the user's Questionable priority queue for " + context + ".");
			return false;
		}
		savedQuestionablePriority = encodedQuestPriority;
		bool num = await StopQuestionableAsync(context, token) && questionableIpc.ClearQuestPriority();
		if (num)
		{
			log.Information($"[HuntLogs] Questionable handoff prepared for quest {questId} (Lumina row {luminaQuestRowId}): stopped=true, stalePriorityCleared=true.");
		}
		return num;
	}

	private async Task<bool> StartQuestionableAndVerifyQuestAsync(ushort expectedQuestId, string context, CancellationToken token, bool tryStartSpecificQuest = true)
	{
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			log.Warning("[HuntLogs] Questionable was not ready for " + context + ".");
			return false;
		}
		string expectedQuestIdText = expectedQuestId.ToString();
		bool flag = false;
		if (tryStartSpecificQuest)
		{
			flag = await RunOnFrameworkThreadAsync(() => questionableIpc.StartSingleQuest(expectedQuestIdText));
		}
		if (!flag)
		{
			flag = await RunOnFrameworkThreadAsync(() => commandManager.ProcessCommand("/qst start"));
		}
		if (!flag && !questionableIpc.IsRunning())
		{
			log.Warning("[HuntLogs] /qst start was rejected for " + context + ".");
			return false;
		}
		if (!flag)
		{
			log.Information("[HuntLogs] /qst start was rejected for " + context + ", but Questionable remained running; continuing intended-quest verification.");
		}
		bool num = await WaitUntilFrameworkAsync(() => questionableIpc.IsRunning() && string.Equals(questionableIpc.GetCurrentQuestId(), expectedQuestIdText, StringComparison.OrdinalIgnoreCase), "Questionable running expected quest " + expectedQuestIdText + " for " + context, QuestionableStartTimeout, token);
		if (!num)
		{
			string value = questionableIpc.GetCurrentQuestId() ?? "(none)";
			log.Warning($"[HuntLogs] Questionable did not start intended quest {expectedQuestIdText} within {QuestionableStartTimeout.TotalSeconds:F0}s for {context}; isRunning={questionableIpc.IsRunning()}, currentQuest={value}.");
		}
		return num;
	}

	private async Task<bool> StopQuestionableAsync(string context, CancellationToken token)
	{
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			return false;
		}
		bool stopAccepted = true;
		if (questionableIpc.IsRunning())
		{
			stopAccepted = await RunOnFrameworkThreadAsync(() => commandManager.ProcessCommand("/qst stop"));
		}
		bool flag = await WaitUntilFrameworkAsync(() => !questionableIpc.IsRunning(), "Questionable stop for " + context, QuestionableStopBeforeDutyTimeout, token);
		if (!stopAccepted || !flag)
		{
			log.Warning($"[HuntLogs] Questionable stop incomplete for {context}: stopAccepted={stopAccepted}, stopped={flag}.");
			return false;
		}
		await Task.Delay(500, token);
		log.Information("[HuntLogs] Questionable stopped for " + context + ".");
		return true;
	}

	private async Task CleanupQuestionableQuestHandoffAsync(string context, CancellationToken token)
	{
		try
		{
			if (!questionableIpc.TryEnsureAvailableSilent())
			{
				log.Warning("[HuntLogs] Questionable cleanup could not verify availability for " + context + ".");
				return;
			}
			await StopQuestionableAsync("cleanup after " + context, token);
			RestoreSavedQuestionablePriority("after " + context);
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Questionable cleanup failed for " + context + ": " + ex.Message);
		}
	}

	private bool RestoreSavedQuestionablePriority(string context)
	{
		if (savedQuestionablePriority == null)
		{
			return true;
		}
		if (!questionableIpc.RestoreQuestPriority(savedQuestionablePriority))
		{
			log.Warning("[HuntLogs] Failed to restore the user's Questionable priority queue " + context + ".");
			return false;
		}
		savedQuestionablePriority = null;
		log.Information("[HuntLogs] Restored the user's Questionable priority queue " + context + ".");
		return true;
	}

	private static ushort NormalizeQuestId(uint luminaQuestRowId)
	{
		return (ushort)(luminaQuestRowId & 0xFFFF);
	}

	private async Task<DutyUnlockResolution> ResolveHuntDutyUnlockAsync(uint territoryTypeId)
	{
		DutyUnlockResolution result;
		try
		{
			result = await RunOnFrameworkThreadAsync(() => ResolveHuntDutyUnlockUnsafe(territoryTypeId));
		}
		catch (Exception ex)
		{
			result = new DutyUnlockResolution(territoryTypeId, 0u, 0u, DutyUnlockResolutionState.Unknown, $"Failed to resolve territory {territoryTypeId} to InstanceContent: {ex.Message}");
		}
		string value = result.State switch
		{
			DutyUnlockResolutionState.Resolved => "resolved/locked", 
			DutyUnlockResolutionState.Unlocked => "unlocked", 
			_ => "unknown", 
		};
		string value2 = (string.IsNullOrWhiteSpace(result.Blocker) ? string.Empty : (" Blocker=" + result.Blocker));
		string messageTemplate = $"[HuntLogs] Duty unlock mapping. Territory={result.TerritoryTypeId} CFC={result.ContentFinderConditionId} InstanceContent={result.InstanceContentId} Result={value}.{value2}";
		if (result.State == DutyUnlockResolutionState.Unknown)
		{
			log.Warning(messageTemplate);
		}
		else
		{
			log.Information(messageTemplate);
		}
		return result;
	}

	private unsafe DutyUnlockResolution ResolveHuntDutyUnlockUnsafe(uint territoryTypeId)
	{
		if (!dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryTypeId, out var row))
		{
			return new DutyUnlockResolution(territoryTypeId, 0u, 0u, DutyUnlockResolutionState.Unknown, $"TerritoryType row {territoryTypeId} is unavailable; the duty unlock state cannot be resolved.");
		}
		uint rowId = row.ContentFinderCondition.RowId;
		if (rowId == 0)
		{
			return new DutyUnlockResolution(territoryTypeId, 0u, 0u, DutyUnlockResolutionState.Unknown, $"TerritoryType {territoryTypeId} has no ContentFinderCondition; the duty unlock state cannot be resolved.");
		}
		if (!dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(rowId, out var row2))
		{
			return new DutyUnlockResolution(territoryTypeId, rowId, 0u, DutyUnlockResolutionState.Unknown, $"ContentFinderCondition row {rowId} for territory {territoryTypeId} is unavailable.");
		}
		RowRef content = row2.Content;
		uint rowId2 = content.RowId;
		if (!content.Is<Lumina.Excel.Sheets.InstanceContent>() || rowId2 == 0)
		{
			string value = content.RowType?.Name ?? "untyped";
			return new DutyUnlockResolution(territoryTypeId, rowId, rowId2, DutyUnlockResolutionState.Unknown, $"ContentFinderCondition {rowId} resolves to {value} row {rowId2}, not InstanceContent.");
		}
		if (!dataManager.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>().TryGetRow(rowId2, out var _))
		{
			return new DutyUnlockResolution(territoryTypeId, rowId, rowId2, DutyUnlockResolutionState.Unknown, $"InstanceContent row {rowId2} for territory {territoryTypeId} is unavailable.");
		}
		if (UIState.Instance() == null)
		{
			return new DutyUnlockResolution(territoryTypeId, rowId, rowId2, DutyUnlockResolutionState.Unknown, $"UIState is unavailable; unlock state for InstanceContent {rowId2} cannot be read.");
		}
		DutyUnlockResolutionState dutyUnlockResolutionState = (UIState.IsInstanceContentUnlocked(rowId2) ? DutyUnlockResolutionState.Unlocked : DutyUnlockResolutionState.Resolved);
		return new DutyUnlockResolution(territoryTypeId, rowId, rowId2, dutyUnlockResolutionState, string.Empty);
	}

	private unsafe async Task<DutyUnlockResolution> GetHuntDutyUnlockStateAsync(DutyUnlockResolution duty)
	{
		try
		{
			return await RunOnFrameworkThreadAsync(() => (UIState.Instance() == null) ? duty with
			{
				State = DutyUnlockResolutionState.Unknown,
				Blocker = $"UIState is unavailable while polling unlock state for InstanceContent {duty.InstanceContentId} (territory {duty.TerritoryTypeId}, CFC {duty.ContentFinderConditionId})."
			} : duty with
			{
				State = (UIState.IsInstanceContentUnlocked(duty.InstanceContentId) ? DutyUnlockResolutionState.Unlocked : DutyUnlockResolutionState.Resolved),
				Blocker = string.Empty
			});
		}
		catch (Exception ex)
		{
			return duty with
			{
				State = DutyUnlockResolutionState.Unknown,
				Blocker = $"Failed to poll unlock state for InstanceContent {duty.InstanceContentId} (territory {duty.TerritoryTypeId}, CFC {duty.ContentFinderConditionId}): {ex.Message}"
			};
		}
	}

	private void RecordDutyBlocker(string backendName, string dutyName, string blocker)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.DutyBackend = backendName;
			s.DutyBlocker = blocker;
			s.CurrentStep = backendName + " blocked for " + dutyName;
		});
		MarkCurrentCharacterStatus(FormatDutyBlockerStatus(backendName, dutyName, blocker), markSkipped: true);
	}

	private static string FormatDutyBlockerStatus(string backendName, string dutyName, string blocker)
	{
		if (string.Equals(backendName, "unlock quest via Questionable", StringComparison.OrdinalIgnoreCase))
		{
			return "Blocked: unlock quest via Questionable for " + dutyName + ": " + blocker;
		}
		if (string.Equals(backendName, "Unlock", StringComparison.OrdinalIgnoreCase))
		{
			return "Blocked: unlock quest for " + dutyName + ": " + blocker;
		}
		if (string.Equals(backendName, "duty handoff", StringComparison.OrdinalIgnoreCase))
		{
			return "Blocked: duty handoff for " + dutyName + ": " + blocker;
		}
		if (string.IsNullOrWhiteSpace(backendName) || string.Equals(backendName, HuntDutyRunner.GetBackendName(HuntDutyBackend.None), StringComparison.OrdinalIgnoreCase))
		{
			return "Blocked: duty execution " + dutyName + ": " + blocker;
		}
		return $"Blocked: {backendName} duty execution {dutyName}: {blocker}";
	}

	private async Task SwitchToCharacterAsync(string character, CancellationToken token)
	{
		if (string.Equals(autoRetainerIpc.GetCurrentCharacter(), character, StringComparison.OrdinalIgnoreCase))
		{
			configuredReturnCompletedForNextCharacterSwitch = false;
			await WaitForCharacterReadyAsync(token);
			return;
		}
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.Phase = HuntLogPhase.SwitchingCharacter;
			s.CurrentStep = "Switching to " + character;
		});
		if (await RunOnFrameworkThreadAsync(() => clientState.IsLoggedIn))
		{
			if (!configuredReturnCompletedForNextCharacterSwitch)
			{
				await StageInSafeCityBeforeCharacterSwitchAsync(token);
			}
			else
			{
				configuredReturnCompletedForNextCharacterSwitch = false;
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = "Waiting for completed return before character switch";
				});
				await WaitForTravelSettledAsync("completed configured return before character switch", TimeSpan.FromSeconds(20L), token);
				log.Information("[HuntLogs] Configured return was already completed for the previous character; skipping duplicate return before character switch.");
			}
		}
		if (!autoRetainerIpc.IsAvailable && !autoRetainerIpc.TryReinitialize())
		{
			throw new InvalidOperationException("AutoRetainer is not available for selected-character rotation.");
		}
		DateTime relogStarted = DateTime.UtcNow;
		DateTime nextRelogAttempt = DateTime.MinValue;
		int attempt = 0;
		while (!string.Equals(autoRetainerIpc.GetCurrentCharacter(), character, StringComparison.OrdinalIgnoreCase))
		{
			token.ThrowIfCancellationRequested();
			if (DateTime.UtcNow - relogStarted >= CharacterRelogTimeout)
			{
				throw new TimeoutException("Timed out switching to " + character + "; the character was not marked complete.");
			}
			if (!(await RunOnFrameworkThreadAsync((Func<bool>)IsCharacterRelogReadyUnsafe)))
			{
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.Phase = HuntLogPhase.SwitchingCharacter;
					s.CurrentStep = "Waiting for cutscene or occupied state before switching to " + character;
				});
				await Task.Delay(250, token);
				continue;
			}
			if (DateTime.UtcNow >= nextRelogAttempt)
			{
				attempt++;
				if (autoRetainerIpc.SwitchCharacter(character))
				{
					log.Information($"[HuntLogs] Relog request {attempt} accepted for {character}; " + "waiting for the requested character to become current.");
					nextRelogAttempt = DateTime.UtcNow + CharacterRelogRetryInterval;
				}
				else
				{
					log.Information($"[HuntLogs] Relog request {attempt} for {character} was not accepted; retrying when safe.");
					nextRelogAttempt = DateTime.UtcNow + CharacterRelogRejectedRetryDelay;
				}
			}
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.Phase = HuntLogPhase.WaitingForCharacterLogin;
				s.CurrentStep = "Waiting for " + character + " to log in";
			});
			await Task.Delay(250, token);
		}
		await WaitForCharacterReadyAsync(token);
	}

	private bool IsCharacterRelogReadyUnsafe()
	{
		if (IsCharacterReadyForMovementUnsafe() && !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Casting] && !condition[ConditionFlag.Occupied] && !condition[ConditionFlag.Occupied30] && !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.Occupied33] && !condition[ConditionFlag.OccupiedInCutSceneEvent] && !condition[ConditionFlag.Occupied38] && !condition[ConditionFlag.Occupied39] && !condition[ConditionFlag.WatchingCutscene] && !condition[ConditionFlag.WatchingCutscene78])
		{
			return !lifestreamIpc.IsBusy();
		}
		return false;
	}

	private async Task StageInSafeCityBeforeCharacterSwitchAsync(CancellationToken token)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Preparing safe city relog";
		});
		if (ShouldUseConfiguredReturnBeforeCharacterSwitch(configuration.HuntLogs))
		{
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = $"Returning to {configuration.HuntLogs.ReturnDestination} before character switch";
			});
			if (await TryReturnToConfiguredDestinationAsync("before character switch", token))
			{
				log.Information($"[HuntLogs] Staged at configured return destination {configuration.HuntLogs.ReturnDestination} " + "before character switch.");
				return;
			}
			log.Warning($"[HuntLogs] Lifestream rejected configured return destination {configuration.HuntLogs.ReturnDestination} " + "before character switch; falling back to starter/GC city staging.");
		}
		if (IsStarterCityTerritory(await RunOnFrameworkThreadAsync(() => clientState.TerritoryType)))
		{
			await ClearNearbyAggroBeforeTravelAsync("character switch in city", token);
			await WaitForTravelSettledAsync("before character switch in city", TimeSpan.FromSeconds(20L), token);
			return;
		}
		(uint, int, uint, int) tuple = await GetPlayerInfoAsync();
		List<string> failures = new List<string>();
		foreach (uint territoryId in GetCharacterSwitchCityTerritories(tuple.Item3))
		{
			token.ThrowIfCancellationRequested();
			try
			{
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = "Teleporting to " + database.GetTerritoryName(territoryId) + " before character switch";
				});
				await TravelToTerritoryAsync(territoryId, token);
				await WaitForTravelSettledAsync("before character switch in " + database.GetTerritoryName(territoryId), TimeSpan.FromSeconds(20L), token);
				log.Information("[HuntLogs] Staged in " + database.GetTerritoryName(territoryId) + " before character switch.");
				return;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (TimeoutException ex2) when (ex2.Message.Contains("clearing combat", StringComparison.OrdinalIgnoreCase))
			{
				throw;
			}
			catch (Exception ex3)
			{
				failures.Add(database.GetTerritoryName(territoryId) + ": " + ex3.Message);
				log.Warning("[HuntLogs] Could not stage in " + database.GetTerritoryName(territoryId) + " before character switch: " + ex3.Message);
			}
		}
		throw new InvalidOperationException("Could not stage in Limsa Lominsa, Gridania, or Ul'dah before character switch. " + string.Join("; ", failures));
	}

	private static bool ShouldUseConfiguredReturnBeforeCharacterSwitch(HuntLogSettings settings)
	{
		if (settings.ReturnOnceDone)
		{
			return settings.ReturnDestination != HuntLogReturnDestination.Auto;
		}
		return false;
	}

	public async Task<bool> PrepareCombatJobForQuestRotationAsync(uint combatJobId, CancellationToken cancellationToken)
	{
		(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) initialPlayer = await GetPlayerInfoAsync();
		string initialJobLabel = await GetClassJobLabelAsync(initialPlayer.ClassJobId);
		bool initiallyOnCombatJob = IsCombatJob(initialPlayer.ClassJobId);
		string text = ((combatJobId != 0) ? $"{await GetClassJobLabelAsync(combatJobId)} ({combatJobId})" : "highest saved combat job");
		string preferredJobLabel = text;
		if (combatJobId != 0 && (combatJobId > 255 || !JobClassification.IsCombatJob((byte)combatJobId)))
		{
			return LogQuestRotationCombatJobOutcome(preferredJobLabel, initialPlayer.ClassJobId, initialJobLabel, initiallyOnCombatJob, initiallyOnCombatJob ? $"preferred job ID {combatJobId} is invalid; continuing on the active combat job" : $"preferred job ID {combatJobId} is invalid and the active job is not combat");
		}
		HuntLogCombatJobMode mode = ((combatJobId != 0) ? HuntLogCombatJobMode.SpecificJob : HuntLogCombatJobMode.HighestCombatJob);
		StableCombatGearsetResolution stableResolution;
		try
		{
			stableResolution = await WaitForStableCombatGearsetSelectionAsync(mode, combatJobId, initiallyOnCombatJob, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			log.Warning("[QuestRotation] Character-bound gearset polling failed; the actual job will decide whether quest rotation can continue: " + ex2.Message);
			stableResolution = new StableCombatGearsetResolution(0uL, new CombatGearsetSelectionResult(null, "character-bound gearset polling failed: " + ex2.Message));
		}
		CombatGearsetSelectionResult selectionResult = stableResolution.SelectionResult;
		(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) player = await GetPlayerInfoAsync();
		string actualJobLabel = await GetClassJobLabelAsync(player.ClassJobId);
		if (combatJobId != 0 && player.ClassJobId == combatJobId && IsCombatJob(player.ClassJobId))
		{
			return LogQuestRotationCombatJobOutcome(preferredJobLabel, player.ClassJobId, actualJobLabel, canContinue: true, "already on the requested combat job; no saved gearset is required");
		}
		if (selectionResult.Selection == null)
		{
			string text2 = (string.IsNullOrWhiteSpace(selectionResult.FailureReason) ? "No usable combat gearset exists" : selectionResult.FailureReason);
			bool flag = IsCombatJob(player.ClassJobId);
			return LogQuestRotationCombatJobOutcome(preferredJobLabel, player.ClassJobId, actualJobLabel, flag, flag ? ("preferred gearset is unavailable (" + text2 + "); continuing on the active combat job") : ("preferred gearset is unavailable (" + text2 + ") and the active job is not combat"));
		}
		CombatGearsetSelection selection = selectionResult.Selection;
		if (combatJobId == 0)
		{
			preferredJobLabel = $"{selection.JobLabel} ({selection.ClassJobId}; highest saved combat job)";
		}
		if (player.ClassJobId == selection.ClassJobId && IsCombatJob(player.ClassJobId))
		{
			return LogQuestRotationCombatJobOutcome(preferredJobLabel, player.ClassJobId, actualJobLabel, canContinue: true, "preferred combat job became active while gearset data settled; no gearset change is needed");
		}
		log.Information($"[QuestRotation] Trying preferred combat job {selection.JobLabel} from saved gearset {selection.GearsetId + 1}.");
		bool switchReportedSuccess = false;
		string switchFailure = string.Empty;
		try
		{
			switchReportedSuccess = await SwitchToCombatGearsetAsync(selection, cancellationToken, updateHuntLogStatus: false, requireSelectedJob: false, "[QuestRotation]", stableResolution.CharacterContentId);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex4)
		{
			switchFailure = ex4.Message;
			log.Warning($"[QuestRotation] Preferred switch to {selection.JobLabel} raised an error; the actual job will decide whether quest rotation can continue: {ex4.Message}");
		}
		player = await GetPlayerInfoAsync();
		actualJobLabel = await GetClassJobLabelAsync(player.ClassJobId);
		if (IsCombatJob(player.ClassJobId))
		{
			string detail = ((player.ClassJobId == selection.ClassJobId) ? "preferred gearset equipped and verified" : ((!string.IsNullOrWhiteSpace(switchFailure)) ? ("preferred switch failed (" + switchFailure + "); continuing on the actual combat job") : (switchReportedSuccess ? "preferred switch settled on another combat job; continuing on that combat job" : "preferred switch was rejected or not ready; continuing on the actual combat job")));
			return LogQuestRotationCombatJobOutcome(preferredJobLabel, player.ClassJobId, actualJobLabel, canContinue: true, detail);
		}
		string detail2 = ((!string.IsNullOrWhiteSpace(switchFailure)) ? ("preferred switch failed (" + switchFailure + ") and the active job remains noncombat") : (switchReportedSuccess ? "preferred switch did not leave the character on a combat job" : "preferred switch was rejected or timed out and the active job remains noncombat"));
		return LogQuestRotationCombatJobOutcome(preferredJobLabel, player.ClassJobId, actualJobLabel, canContinue: false, detail2);
	}

	private async Task<StableCombatGearsetResolution> WaitForStableCombatGearsetSelectionAsync(HuntLogCombatJobMode mode, uint preferredJobId, bool alreadyOnCombatJob, CancellationToken token)
	{
		TimeSpan timeout = (alreadyOnCombatJob ? CombatGearsetDataAlreadyCombatTimeout : CombatGearsetDataSwitchRequiredTimeout);
		DateTime started = DateTime.UtcNow;
		DateTime nextDiagnosticUtc = DateTime.MinValue;
		CombatGearsetResolutionSnapshot stableSnapshot = null;
		DateTime? stableSince = null;
		string lastReason = "character-bound gearset data is unavailable";
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			CombatGearsetResolutionSnapshot combatGearsetResolutionSnapshot = await RunOnFrameworkThreadAsync(() => GetCombatGearsetResolutionSnapshotUnsafe(mode, preferredJobId));
			DateTime utcNow = DateTime.UtcNow;
			if (combatGearsetResolutionSnapshot.Ready && combatGearsetResolutionSnapshot.Resolution != null)
			{
				if (object.Equals(stableSnapshot, combatGearsetResolutionSnapshot))
				{
					if (stableSince.HasValue && utcNow - stableSince.Value >= CombatGearsetSnapshotStableTime)
					{
						log.Debug($"[QuestRotation] Character-bound gearset data stabilized: contentId={combatGearsetResolutionSnapshot.PlayerContentId}, gearsets={combatGearsetResolutionSnapshot.GearsetCount}, resolution={DescribeCombatGearsetResolution(combatGearsetResolutionSnapshot.Resolution)}.");
						return new StableCombatGearsetResolution(combatGearsetResolutionSnapshot.PlayerContentId, combatGearsetResolutionSnapshot.Resolution);
					}
				}
				else
				{
					stableSnapshot = combatGearsetResolutionSnapshot;
					stableSince = utcNow;
				}
				lastReason = $"gearset snapshot is settling (contentId={combatGearsetResolutionSnapshot.PlayerContentId}, gearsets={combatGearsetResolutionSnapshot.GearsetCount}, resolution={DescribeCombatGearsetResolution(combatGearsetResolutionSnapshot.Resolution)})";
			}
			else
			{
				stableSnapshot = null;
				stableSince = null;
				lastReason = (string.IsNullOrWhiteSpace(combatGearsetResolutionSnapshot.Reason) ? "character-bound gearset data is unavailable" : combatGearsetResolutionSnapshot.Reason);
			}
			if (utcNow >= nextDiagnosticUtc)
			{
				log.Debug($"[QuestRotation] Waiting for stable character-bound gearset data ({(alreadyOnCombatJob ? "combat fallback available" : "combat switch required")}, timeout={timeout.TotalSeconds:F0}s): {lastReason}.");
				nextDiagnosticUtc = utcNow + CombatGearsetDataDiagnosticInterval;
			}
			await Task.Delay(CombatGearsetDataPollInterval, token);
		}
		return new StableCombatGearsetResolution(0uL, new CombatGearsetSelectionResult(null, $"timed out after {timeout.TotalSeconds:F0}s waiting for stable character-bound gearset data: {lastReason}"));
	}

	private unsafe CombatGearsetResolutionSnapshot GetCombatGearsetResolutionSnapshotUnsafe(HuntLogCombatJobMode mode, uint preferredJobId)
	{
		if (!clientState.IsLoggedIn)
		{
			return new CombatGearsetResolutionSnapshot(Ready: false, 0uL, 0uL, 0, null, "client is not logged in");
		}
		if (!dalamudPlayerState.IsLoaded)
		{
			return new CombatGearsetResolutionSnapshot(Ready: false, 0uL, 0uL, 0, null, "Dalamud PlayerState is not loaded");
		}
		ulong contentId = dalamudPlayerState.ContentId;
		if (contentId == 0L)
		{
			return new CombatGearsetResolutionSnapshot(Ready: false, 0uL, 0uL, 0, null, "character content ID is unavailable");
		}
		PlayerState* ptr = PlayerState.Instance();
		if (ptr == null || !ptr->IsLoaded)
		{
			return new CombatGearsetResolutionSnapshot(Ready: false, contentId, 0uL, 0, null, "native PlayerState is not loaded");
		}
		RaptureGearsetModule* ptr2 = RaptureGearsetModule.Instance();
		if (ptr2 == null)
		{
			return new CombatGearsetResolutionSnapshot(Ready: false, contentId, 0uL, 0, null, "RaptureGearsetModule is unavailable");
		}
		ulong characterContentId = ptr2->CharacterContentId;
		if (characterContentId != contentId)
		{
			return new CombatGearsetResolutionSnapshot(Ready: false, contentId, characterContentId, 0, null, $"gearset data belongs to content ID {characterContentId}, expected {contentId}");
		}
		int num = 0;
		for (int i = 0; i < 100; i++)
		{
			if ((ptr2->GetGearset(i)->Flags & RaptureGearsetModule.GearsetFlag.Exists) != RaptureGearsetModule.GearsetFlag.None)
			{
				num++;
			}
		}
		return new CombatGearsetResolutionSnapshot(Ready: true, contentId, characterContentId, num, ResolveCombatGearsetSelectionUnsafe(mode, preferredJobId), string.Empty);
	}

	private static string DescribeCombatGearsetResolution(CombatGearsetSelectionResult resolution)
	{
		if (!(resolution.Selection != null))
		{
			if (!string.IsNullOrWhiteSpace(resolution.FailureReason))
			{
				return resolution.FailureReason;
			}
			return "no usable combat gearset";
		}
		return $"{resolution.Selection.JobLabel} from gearset {resolution.Selection.GearsetId + 1}";
	}

	private bool LogQuestRotationCombatJobOutcome(string preferredJobLabel, uint actualJobId, string actualJobLabel, bool canContinue, string detail)
	{
		string messageTemplate = $"[QuestRotation] Combat job outcome: preferred={preferredJobLabel}; actual={actualJobLabel} ({actualJobId}); result={(canContinue ? "continue" : "skip")}; {detail}.";
		if (canContinue)
		{
			log.Information(messageTemplate);
		}
		else
		{
			log.Warning(messageTemplate);
		}
		return canContinue;
	}

	private static bool IsCombatJob(uint classJobId)
	{
		if (classJobId <= 255)
		{
			return JobClassification.IsCombatJob((byte)classJobId);
		}
		return false;
	}

	private async Task<bool> EnsureSelectedCombatJobAsync(string character, CancellationToken token)
	{
		await jobStoneGearsetReconciliation.ReconcileCurrentAsync("Hunt Log combat-job selection", token);
		(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) player = await GetPlayerInfoAsync();
		string currentLabel = await GetClassJobLabelAsync(player.ClassJobId);
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Resolving combat job";
			s.CurrentCombatJobId = player.ClassJobId;
			s.CurrentCombatJobLabel = currentLabel;
		});
		CombatGearsetSelectionResult combatGearsetSelectionResult = await ResolveCombatGearsetSelectionForCharacterAsync(character);
		if (combatGearsetSelectionResult.Selection == null)
		{
			string text = (string.IsNullOrWhiteSpace(combatGearsetSelectionResult.FailureReason) ? "No usable combat gearset exists" : combatGearsetSelectionResult.FailureReason);
			MarkCurrentCharacterStatus("Blocked: " + text, markSkipped: true);
			log.Warning($"[HuntLogs] {character} blocked before hunt logs: {text}.");
			UpdateCharacterSnapshot(character, player, null, null, 0u, -1);
			return false;
		}
		CombatGearsetSelection selection = combatGearsetSelectionResult.Selection;
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.SelectedCombatJobId = selection.ClassJobId;
			s.SelectedCombatJobLabel = selection.JobLabel;
			s.CharacterStatuses[character] = "Combat job: " + selection.JobLabel;
		});
		UpdateCharacterSnapshot(character, player, null, null, selection.ClassJobId, selection.GearsetId);
		if (player.ClassJobId == selection.ClassJobId && player.ClassJobId <= 255 && JobClassification.IsCombatJob((byte)player.ClassJobId))
		{
			log.Information($"[HuntLogs] {character} already on selected combat job {selection.JobLabel}.");
			return true;
		}
		if (selection.GearsetId < 0)
		{
			MarkCurrentCharacterStatus("Blocked: current job changed before combat setup", markSkipped: true);
			log.Warning("[HuntLogs] " + character + " current job changed before hunt-log combat setup could continue.");
			return false;
		}
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Switching to " + selection.JobLabel;
			s.CharacterStatuses[character] = "Switching to " + selection.JobLabel;
		});
		if (!(await SwitchToCombatGearsetAsync(selection, token, updateHuntLogStatus: true, requireSelectedJob: false, "[HuntLogs]", 0uL)))
		{
			return false;
		}
		player = await GetPlayerInfoAsync();
		currentLabel = await GetClassJobLabelAsync(player.ClassJobId);
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentCombatJobId = player.ClassJobId;
			s.CurrentCombatJobLabel = currentLabel;
			s.CharacterStatuses[character] = "Combat job ready: " + currentLabel;
		});
		UpdateCharacterSnapshot(character, player, null, null, selection.ClassJobId, selection.GearsetId);
		return true;
	}

	private async Task<bool> SwitchToCombatGearsetAsync(CombatGearsetSelection selection, CancellationToken token, bool updateHuntLogStatus = true, bool requireSelectedJob = false, string logPrefix = "[HuntLogs]", ulong expectedContentId = 0uL)
	{
		if (!(await WaitForCombatJobSwitchReadyAsync(token, logPrefix, expectedContentId)))
		{
			if (updateHuntLogStatus)
			{
				MarkCurrentCharacterStatus("Blocked: character was not stable for combat job switch", markSkipped: true);
			}
			log.Warning(logPrefix + " Could not switch to " + selection.JobLabel + "; character did not become stable.");
			return false;
		}
		JobStoneGearsetDemotionGuard jobStoneGearsetDemotionGuard = await jobStoneGearsetReconciliation.GetDemotionGuardAsync(selection.ClassJobId, token);
		if (jobStoneGearsetDemotionGuard.Suppress)
		{
			if (updateHuntLogStatus)
			{
				MarkCurrentCharacterStatus($"Blocked: protected live job {jobStoneGearsetDemotionGuard.Target?.ClassJobId ?? 0} from base-class demotion", markSkipped: true);
			}
			log.Warning($"{logPrefix} Suppressed gearset {selection.GearsetId + 1} ({selection.JobLabel}): " + jobStoneGearsetDemotionGuard.Reason + ".");
			return false;
		}
		string command = $"/gs change {selection.GearsetId + 1}";
		if (!(await RunOnFrameworkThreadAsync(() => SendGameCommandUnsafe(command))))
		{
			if (updateHuntLogStatus)
			{
				MarkCurrentCharacterStatus("Blocked: gearset command failed for " + selection.JobLabel, markSkipped: true);
			}
			log.Warning($"{logPrefix} Gearset command failed for {selection.JobLabel}: {command}");
			return false;
		}
		log.Information($"{logPrefix} Sent combat gearset switch: command={command}, job={selection.JobLabel}, gearsetId={selection.GearsetId}, level={selection.Level}, itemLevel={selection.ItemLevel}");
		if (!(await WaitUntilFrameworkAsync(delegate
		{
			(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) playerInfoUnsafe = GetPlayerInfoUnsafe();
			CombatJobSwitchReadiness combatJobSwitchReadinessUnsafe = GetCombatJobSwitchReadinessUnsafe(expectedContentId);
			return playerInfoUnsafe.ClassJobId == selection.ClassJobId && combatJobSwitchReadinessUnsafe.Ready;
		}, "combat gearset switch to " + selection.JobLabel, CombatJobSwitchTimeout, token)))
		{
			(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) player = await GetPlayerInfoAsync();
			string text = await GetClassJobLabelAsync(player.ClassJobId);
			if (!requireSelectedJob && player.ClassJobId <= 255 && JobClassification.IsCombatJob((byte)player.ClassJobId))
			{
				log.Warning($"{logPrefix} Timed out switching to {selection.JobLabel}; current job is {text} ({player.ClassJobId}) and is valid combat, continuing.");
				await Task.Delay(1000, token);
				await WaitForCharacterReadyAsync(token);
				return true;
			}
			if (updateHuntLogStatus)
			{
				MarkCurrentCharacterStatus("Blocked: gearset switch timed out at " + text, markSkipped: true);
			}
			log.Warning($"{logPrefix} Timed out switching to {selection.JobLabel}; current job is {text} ({player.ClassJobId}).");
			return false;
		}
		await Task.Delay(1000, token);
		await WaitForCharacterReadyAsync(token);
		return true;
	}

	private async Task<bool> WaitForCombatJobSwitchReadyAsync(CancellationToken token, string logPrefix, ulong expectedContentId)
	{
		bool flag = !IsDismountedForCombat(await GetDismountStateAsync());
		if (flag)
		{
			flag = !(await DismountForCombatAsync(token));
		}
		if (flag)
		{
			return false;
		}
		DateTime started = DateTime.UtcNow;
		DateTime? stableSince = null;
		string lastReason = string.Empty;
		while (DateTime.UtcNow - started < CombatJobSwitchReadyTimeout)
		{
			token.ThrowIfCancellationRequested();
			if (await TryHandleDeathRecoveryAsync(await RunOnFrameworkThreadAsync(() => clientState.TerritoryType), "combat job switch readiness", token))
			{
				stableSince = null;
				started = DateTime.UtcNow;
				continue;
			}
			CombatJobSwitchReadiness combatJobSwitchReadiness = await RunOnFrameworkThreadAsync(() => GetCombatJobSwitchReadinessUnsafe(expectedContentId));
			bool flag2 = !vnavmeshIpc.IsPathRunning() && !vnavmeshIpc.IsPathfinding();
			if (combatJobSwitchReadiness.Ready && flag2)
			{
				stableSince.GetValueOrDefault();
				if (!stableSince.HasValue)
				{
					DateTime utcNow = DateTime.UtcNow;
					stableSince = utcNow;
				}
				if (DateTime.UtcNow - stableSince.Value >= MovementIdleStableTime)
				{
					return true;
				}
			}
			else
			{
				stableSince = null;
				lastReason = ((!combatJobSwitchReadiness.Ready) ? combatJobSwitchReadiness.Reason : "vnavmesh is busy");
			}
			await Task.Delay(250, token);
		}
		if (!string.IsNullOrWhiteSpace(lastReason))
		{
			log.Warning(logPrefix + " Combat job switch readiness timed out: " + lastReason + ".");
		}
		return false;
	}

	private unsafe CombatJobSwitchReadiness GetCombatJobSwitchReadinessUnsafe(ulong expectedContentId = 0uL)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (!clientState.IsLoggedIn)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "client is not logged in");
		}
		if (localPlayer == null)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "local player is unavailable");
		}
		if (!dalamudPlayerState.IsLoaded)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "Dalamud PlayerState is not loaded");
		}
		ulong contentId = dalamudPlayerState.ContentId;
		if (contentId == 0L)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "character content ID is unavailable");
		}
		if (expectedContentId != 0L && contentId != expectedContentId)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, $"active character content ID changed from {expectedContentId} to {contentId}");
		}
		PlayerState* ptr = PlayerState.Instance();
		if (ptr == null || !ptr->IsLoaded)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "native PlayerState is not loaded");
		}
		RaptureGearsetModule* ptr2 = RaptureGearsetModule.Instance();
		if (ptr2 == null)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "RaptureGearsetModule is unavailable");
		}
		if (ptr2->CharacterContentId != contentId)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, $"gearset data belongs to content ID {ptr2->CharacterContentId}, expected {contentId}");
		}
		if (condition[ConditionFlag.Unconscious] || localPlayer.CurrentHp == 0 || localPlayer.IsDead)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: true, "player is dead or unconscious");
		}
		if (condition[ConditionFlag.InCombat])
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "player is in combat");
		}
		if (condition[ConditionFlag.Casting] || localPlayer.IsCasting)
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "player is casting");
		}
		if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.InFlight])
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: true, Unconscious: false, "player is mounted or mounting");
		}
		if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "player is changing areas or logging out");
		}
		if (condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied33] || condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.Occupied38] || condition[ConditionFlag.Occupied39] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78])
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "player is occupied or watching a cutscene");
		}
		if (lifestreamIpc.IsBusy())
		{
			return new CombatJobSwitchReadiness(Ready: false, Mounted: false, Unconscious: false, "Lifestream is busy");
		}
		return new CombatJobSwitchReadiness(Ready: true, Mounted: false, Unconscious: false, string.Empty);
	}

	private async Task<CombatGearsetSelectionResult> ResolveCombatGearsetSelectionAsync()
	{
		return await ResolveCombatGearsetSelectionAsync(configuration.HuntLogs.CombatJobMode, configuration.HuntLogs.PreferredCombatJobId);
	}

	private async Task<CombatGearsetSelectionResult> ResolveCombatGearsetSelectionForCharacterAsync(string character)
	{
		if (!configuration.QuestRotationCombatJobByCharacter.TryGetValue(character, out var combatJobId))
		{
			return await ResolveCombatGearsetSelectionAsync();
		}
		if (combatJobId != 0 && (!configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot value) || (!value.CombatJobLevels.ContainsKey(combatJobId) && !value.XadbObservedCombatJobLevels.ContainsKey(combatJobId))))
		{
			configuration.QuestRotationCombatJobByCharacter.Remove(character);
			await framework.RunOnFrameworkThread((System.Action)configuration.Save);
			log.Warning($"[HuntLogs] Cleared uncorroborated per-character combat job {combatJobId} for {character}; " + "falling back to automatic live selection.");
			return await ResolveCombatGearsetSelectionAsync();
		}
		HuntLogCombatJobMode mode = ((combatJobId != 0) ? HuntLogCombatJobMode.SpecificJob : HuntLogCombatJobMode.HighestCombatJob);
		log.Information("[HuntLogs] Using per-character combat job override for " + character + ": " + ((combatJobId == 0) ? "highest saved combat job" : $"job {combatJobId}"));
		return await ResolveCombatGearsetSelectionAsync(mode, combatJobId);
	}

	private async Task<CombatGearsetSelectionResult> ResolveCombatGearsetSelectionAsync(HuntLogCombatJobMode mode, uint preferredJobId)
	{
		return await RunOnFrameworkThreadAsync(() => ResolveCombatGearsetSelectionUnsafe(mode, preferredJobId));
	}

	private unsafe CombatGearsetSelectionResult ResolveCombatGearsetSelectionUnsafe(HuntLogCombatJobMode mode, uint preferredJobId)
	{
		PlayerState* ptr = PlayerState.Instance();
		if (ptr == null || !ptr->IsLoaded)
		{
			return new CombatGearsetSelectionResult(null, "PlayerState is unavailable");
		}
		ExcelSheet<ClassJob> excelSheet = dataManager.GetExcelSheet<ClassJob>();
		if (mode == HuntLogCombatJobMode.CurrentCombatJob)
		{
			uint currentClassJobId = ptr->CurrentClassJobId;
			if (currentClassJobId == 0 || currentClassJobId > 255 || !JobClassification.IsCombatJob((byte)currentClassJobId))
			{
				return new CombatGearsetSelectionResult(null, "current job is not a combat job");
			}
			if (!excelSheet.TryGetRow(currentClassJobId, out var row))
			{
				return new CombatGearsetSelectionResult(null, $"current job {currentClassJobId} is unavailable");
			}
			sbyte expArrayIndex = row.ExpArrayIndex;
			int num = ((expArrayIndex >= 0 && expArrayIndex < ptr->ClassJobLevels.Length) ? ptr->ClassJobLevels[expArrayIndex] : ptr->CurrentLevel);
			if (num <= 0)
			{
				num = ptr->CurrentLevel;
			}
			return new CombatGearsetSelectionResult(new CombatGearsetSelection(-1, currentClassJobId, num, 0, expArrayIndex, GetClassJobLabelUnsafe(currentClassJobId)), string.Empty);
		}
		RaptureGearsetModule* ptr2 = RaptureGearsetModule.Instance();
		if (ptr2 == null)
		{
			return new CombatGearsetSelectionResult(null, "RaptureGearsetModule is unavailable");
		}
		List<CombatGearsetSelection> list = new List<CombatGearsetSelection>(100);
		for (int i = 0; i < 100; i++)
		{
			RaptureGearsetModule.GearsetEntry* gearset = ptr2->GetGearset(i);
			if ((gearset->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
			{
				continue;
			}
			uint classJob = gearset->ClassJob;
			if (classJob == 0 || classJob > 255 || !JobClassification.IsCombatJob((byte)classJob) || !excelSheet.TryGetRow(classJob, out var row2) || gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.SoulStone).ItemId != row2.ItemSoulCrystal.RowId)
			{
				continue;
			}
			sbyte expArrayIndex2 = row2.ExpArrayIndex;
			if (expArrayIndex2 >= 0 && expArrayIndex2 < ptr->ClassJobLevels.Length)
			{
				int num2 = ptr->ClassJobLevels[expArrayIndex2];
				if (num2 > 0)
				{
					list.Add(new CombatGearsetSelection(gearset->Id, classJob, num2, gearset->ItemLevel, expArrayIndex2, GetClassJobLabelUnsafe(classJob)));
				}
			}
		}
		if (mode == HuntLogCombatJobMode.SpecificJob)
		{
			if (preferredJobId == 0)
			{
				return new CombatGearsetSelectionResult(null, "no preferred combat job is selected");
			}
			if (preferredJobId > 255 || !JobClassification.IsCombatJob((byte)preferredJobId))
			{
				return new CombatGearsetSelectionResult(null, $"preferred job {preferredJobId} is not a combat job");
			}
			CombatGearsetSelection combatGearsetSelection = (from x in list
				where x.ClassJobId == preferredJobId
				orderby x.Level descending, x.ItemLevel descending, x.GearsetId
				select x).FirstOrDefault();
			if (!(combatGearsetSelection != null))
			{
				return new CombatGearsetSelectionResult(null, "no saved gearset exists for " + GetClassJobLabelUnsafe(preferredJobId));
			}
			return new CombatGearsetSelectionResult(combatGearsetSelection, string.Empty);
		}
		CombatGearsetSelection combatGearsetSelection2 = (from x in list
			group x by x.ExpArrayIndex into @group
			select (from x in @group
				orderby GetAutomaticCombatTrackPreference(x.ClassJobId) descending, x.ItemLevel descending, x.GearsetId
				select x).First() into x
			orderby x.Level descending, x.ItemLevel descending, x.ClassJobId, x.GearsetId
			select x).FirstOrDefault();
		if (!(combatGearsetSelection2 != null))
		{
			return new CombatGearsetSelectionResult(null, "no saved combat gearset exists");
		}
		return new CombatGearsetSelectionResult(combatGearsetSelection2, string.Empty);
	}

	private static int GetAutomaticCombatTrackPreference(uint classJobId)
	{
		switch (classJobId)
		{
		case 27u:
			return 300;
		case 28u:
			return 200;
		case 26u:
			return 100;
		case 19u:
		case 20u:
		case 21u:
		case 22u:
		case 23u:
		case 24u:
		case 25u:
		case 30u:
			return 300;
		case 1u:
		case 2u:
		case 3u:
		case 4u:
		case 5u:
		case 6u:
		case 7u:
		case 29u:
			return 100;
		default:
			return 200;
		}
	}

	private async Task<string> GetClassJobLabelAsync(uint classJobId)
	{
		return await RunOnFrameworkThreadAsync(() => GetClassJobLabelUnsafe(classJobId));
	}

	private string GetClassJobLabelUnsafe(uint classJobId)
	{
		if (classJobId == 0)
		{
			return "Unknown";
		}
		try
		{
			if (!dataManager.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var row))
			{
				return $"Job {classJobId}";
			}
			string text = row.Abbreviation.ToString();
			string text2 = row.Name.ToString();
			if (string.IsNullOrWhiteSpace(text))
			{
				return string.IsNullOrWhiteSpace(text2) ? $"Job {classJobId}" : text2;
			}
			return string.IsNullOrWhiteSpace(text2) ? text : (text + " (" + text2 + ")");
		}
		catch (Exception ex)
		{
			log.Warning($"[HuntLogs] Failed to resolve class-job label for {classJobId}: {ex.Message}");
			return $"Job {classJobId}";
		}
	}

	private async Task WaitForCharacterReadyAsync(CancellationToken token)
	{
		if (!(await TryWaitForCharacterReadyAsync(token)))
		{
			throw new TimeoutException("Timed out waiting for character to finish login or area change.");
		}
	}

	private async Task<bool> TryWaitForCharacterReadyAsync(CancellationToken token)
	{
		if (!(await WaitUntilFrameworkAsync(IsCharacterReadyForMovementUnsafe, "character ready", TimeSpan.FromMinutes(2L), token)))
		{
			return false;
		}
		await Task.Delay(1500, token);
		return true;
	}

	private bool IsCharacterReadyForMovementUnsafe()
	{
		if (clientState.IsLoggedIn && objectTable.LocalPlayer != null && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51])
		{
			return !condition[ConditionFlag.LoggingOut];
		}
		return false;
	}

	private async Task TravelToTerritoryAsync(uint territoryId, CancellationToken token)
	{
		HuntTravelResult huntTravelResult = await TryTravelToTerritoryAsync(territoryId, token);
		if (!huntTravelResult.Arrived)
		{
			throw new InvalidOperationException(huntTravelResult.FailureReason);
		}
	}

	private async Task<HuntTravelResult> TryTravelToTerritoryAsync(uint territoryId, CancellationToken token)
	{
		return await TryTravelToTerritoryAsync(territoryId, null, token);
	}

	private async Task<HuntTravelResult> TryTravelToTerritoryAsync(uint territoryId, Vector3? destinationPosition, CancellationToken token)
	{
		if (await RunOnFrameworkThreadAsync(() => clientState.TerritoryType == territoryId))
		{
			return new HuntTravelResult(Arrived: true, string.Empty);
		}
		await ClearNearbyAggroBeforeTravelAsync("travel to " + database.GetTerritoryName(territoryId), token);
		await WaitForCombatAndCastingToEndAsync("travel to " + database.GetTerritoryName(territoryId), CombatClearTimeout, token, keepCombatAutomationActive: true);
		await TryDisablePreviousFateLevelSyncBeforeTravelAsync(database.GetTerritoryName(territoryId), token);
		HuntTravelPlan plan = await RunOnFrameworkThreadAsync(() => ResolveHuntTravelPlanUnsafe(territoryId, destinationPosition));
		if (!plan.IsValid || plan.Teleport == null)
		{
			return new HuntTravelResult(Arrived: false, string.IsNullOrWhiteSpace(plan.FailureReason) ? $"No unlocked aetheryte or non-attuning route could be resolved for {database.GetTerritoryName(territoryId)} ({territoryId})." : plan.FailureReason);
		}
		HuntTeleportDestination destination = plan.Teleport;
		uint teleportTerritoryId = destination.TerritoryId;
		uint num = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
		int transitionStartIndex = ((num != teleportTerritoryId) ? (-1) : 0);
		if (transitionStartIndex < 0)
		{
			for (int num2 = 0; num2 < plan.Transitions.Count; num2++)
			{
				HuntZoneTransition huntZoneTransition = plan.Transitions[num2];
				if (huntZoneTransition.FromTerritoryId == num)
				{
					transitionStartIndex = num2;
					break;
				}
				if (huntZoneTransition.ToTerritoryId == num)
				{
					transitionStartIndex = num2 + 1;
					break;
				}
			}
		}
		bool resumeExistingRoute = transitionStartIndex >= 0;
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = (resumeExistingRoute ? ("Resuming " + plan.Description) : ("Traveling via " + plan.Description));
		});
		bool teleportArrived = resumeExistingRoute;
		string lastFailure = string.Empty;
		if (resumeExistingRoute)
		{
			log.Information($"[HuntLogs] Resuming non-attuning route from current territory {database.GetTerritoryName(num)} ({num}); nextTransitionIndex={transitionStartIndex}, totalTransitions={plan.Transitions.Count}. " + "The route-start teleport will not be repeated.");
		}
		int attempt = 1;
		while (!resumeExistingRoute && attempt <= 3)
		{
			await ClearNearbyAggroBeforeTravelAsync($"teleport attempt {attempt} to {destination.Name}", token);
			if (!(await TryWaitForTravelSettledAsync($"before teleport attempt {attempt} to {destination.Name}", TimeSpan.FromSeconds(10L), token)))
			{
				lastFailure = $"Timed out waiting for travel to settle before teleport attempt {attempt} to {destination.Name}.";
				if (attempt == 3)
				{
					break;
				}
			}
			else
			{
				uint originTerritory = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
				if (!(await RunOnFrameworkThreadAsync(() => lifestreamIpc.Teleport(destination.AetheryteId, destination.SubIndex, destination.Name))))
				{
					lastFailure = $"Lifestream rejected teleport to {destination.Name} (aetheryte {destination.AetheryteId}/{destination.SubIndex}, territory {territoryId}).";
					log.Warning($"[HuntLogs] {lastFailure} Attempt {attempt}/{3}." + ((attempt < 3) ? " Waiting briefly before retry." : string.Empty));
					if (attempt == 3)
					{
						break;
					}
					await Task.Delay(2500, token);
					await ClearNearbyAggroBeforeTravelAsync("retrying teleport to " + destination.Name + " after Lifestream rejection", token);
				}
				else
				{
					TeleportArrivalResult teleportArrivalResult = await ObserveTeleportArrivalAsync(originTerritory, teleportTerritoryId, destination.Name, token);
					log.Information($"[HuntLogs] Teleport observation: outcome={teleportArrivalResult.Outcome}, attempt={attempt}/{3}, targetTerritory={teleportTerritoryId}, currentTerritory={teleportArrivalResult.TerritoryId}, startObserved={teleportArrivalResult.StartObserved}, detail={teleportArrivalResult.Detail}");
					if (teleportArrivalResult.Outcome == TeleportArrivalOutcome.CombatInterrupted)
					{
						lifestreamIpc.Abort();
						log.Warning("[HuntLogs] Combat interrupted teleport to " + destination.Name + "; Lifestream was aborted and combat will be drained before retry.");
						uint returnTerritoryId = ((teleportArrivalResult.TerritoryId != 0) ? teleportArrivalResult.TerritoryId : originTerritory);
						await ResolveCombatIfNeededAsync("retrying combat-interrupted teleport to " + destination.Name, returnTerritoryId, token);
						if (!(await TryWaitForTravelSettledAsync("after combat-interrupted teleport to " + destination.Name, TimeSpan.FromSeconds(30L), token)))
						{
							lastFailure = "Travel did not reach stable non-casting/non-combat state after combat interrupted teleport to " + destination.Name + ".";
							if (attempt == 3)
							{
								break;
							}
						}
					}
					else if (teleportArrivalResult.Outcome != TeleportArrivalOutcome.Arrived)
					{
						lifestreamIpc.Abort();
						lastFailure = teleportArrivalResult.Detail;
						if (attempt < 3)
						{
							log.Warning($"[HuntLogs] Teleport to {destination.Name} did not arrive cleanly; retrying ({attempt + 1}/{3}). {teleportArrivalResult.Detail}");
							await TryWaitForTravelSettledAsync("before retrying teleport to " + destination.Name, TimeSpan.FromSeconds(30L), token);
						}
					}
					else
					{
						await ResolveCombatIfNeededAsync("settling after teleport to " + destination.Name, teleportTerritoryId, token);
						if (!(await TryWaitForTravelSettledAsync("stable arrival after teleport to " + destination.Name, TeleportPostArrivalTimeout, token)))
						{
							lastFailure = "Timed out waiting for stable character, Lifestream, casting, and combat state after teleport to " + destination.Name + ".";
							if (attempt >= 3)
							{
								break;
							}
						}
						else
						{
							uint num3 = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
							if (num3 == teleportTerritoryId)
							{
								teleportArrived = true;
								break;
							}
							lastFailure = $"Teleport to {destination.Name} settled in {database.GetTerritoryName(num3)} ({num3}) instead of {database.GetTerritoryName(teleportTerritoryId)} ({teleportTerritoryId}).";
							if (attempt >= 3)
							{
								break;
							}
						}
					}
				}
			}
			attempt++;
		}
		if (!teleportArrived)
		{
			return new HuntTravelResult(Arrived: false, string.IsNullOrWhiteSpace(lastFailure) ? $"Teleport to {destination.Name} failed after {3} attempts." : $"Teleport to {destination.Name} failed after {3} attempts: {lastFailure}");
		}
		if (!resumeExistingRoute)
		{
			log.Information($"[HuntLogs] Teleport arrival verified: territory={teleportTerritoryId} \"{database.GetTerritoryName(teleportTerritoryId)}\", aetheryteId={destination.AetheryteId}, subIndex={destination.SubIndex}");
		}
		foreach (HuntZoneTransition transition in plan.Transitions.Skip(Math.Max(0, transitionStartIndex)))
		{
			if (!(await TryMoveThroughZoneTransitionAsync(transition, token)))
			{
				uint num4 = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
				return new HuntTravelResult(Arrived: false, $"Non-attuning route to {database.GetTerritoryName(territoryId)} failed while moving {database.GetTerritoryName(transition.FromTerritoryId)} ({transition.FromTerritoryId}) -> {database.GetTerritoryName(transition.ToTerritoryId)} ({transition.ToTerritoryId}); current territory is {database.GetTerritoryName(num4)} ({num4}).");
			}
		}
		uint num5 = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
		if (num5 != territoryId)
		{
			return new HuntTravelResult(Arrived: false, $"Travel route settled in {database.GetTerritoryName(num5)} ({num5}) instead of {database.GetTerritoryName(territoryId)} ({territoryId}).");
		}
		return new HuntTravelResult(Arrived: true, string.Empty);
	}

	private async Task<TeleportArrivalResult> ObserveTeleportArrivalAsync(uint originTerritoryId, uint targetTerritoryId, string destinationName, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		bool sameTerritoryTeleport = originTerritoryId == targetTerritoryId;
		bool startObserved = false;
		while (DateTime.UtcNow - started < TeleportArrivalTimeout)
		{
			token.ThrowIfCancellationRequested();
			(uint, bool, bool, bool, bool, bool) tuple = await RunOnFrameworkThreadAsync(delegate
			{
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				return (TerritoryId: clientState.TerritoryType, InCombat: condition[ConditionFlag.InCombat], Casting: condition[ConditionFlag.Casting] || (localPlayer?.IsCasting ?? false), BetweenAreas: condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51], LoggingOut: condition[ConditionFlag.LoggingOut], LifestreamBusy: lifestreamIpc.IsBusy());
			});
			if (!sameTerritoryTeleport && tuple.Item1 == targetTerritoryId)
			{
				return new TeleportArrivalResult(TeleportArrivalOutcome.Arrived, tuple.Item1, startObserved, $"Arrived in {database.GetTerritoryName(targetTerritoryId)} ({targetTerritoryId}).");
			}
			if (tuple.Item2)
			{
				return new TeleportArrivalResult(TeleportArrivalOutcome.CombatInterrupted, tuple.Item1, startObserved, $"Combat began before arrival in {database.GetTerritoryName(targetTerritoryId)} ({targetTerritoryId}).");
			}
			startObserved |= tuple.Item1 != originTerritoryId || tuple.Item3 || tuple.Item4 || tuple.Item5 || tuple.Item6;
			if (sameTerritoryTeleport && tuple.Item1 == targetTerritoryId && startObserved && !tuple.Item3 && !tuple.Item4 && !tuple.Item5 && !tuple.Item6)
			{
				return new TeleportArrivalResult(TeleportArrivalOutcome.Arrived, tuple.Item1, startObserved, $"Arrived in {database.GetTerritoryName(targetTerritoryId)} ({targetTerritoryId}).");
			}
			if (tuple.Item1 != originTerritoryId && !tuple.Item4 && !tuple.Item3 && !tuple.Item5 && !tuple.Item6)
			{
				return new TeleportArrivalResult(TeleportArrivalOutcome.UnexpectedTerritory, tuple.Item1, startObserved, $"Teleport to {destinationName} settled in unexpected territory {database.GetTerritoryName(tuple.Item1)} ({tuple.Item1}).");
			}
			if (!startObserved && DateTime.UtcNow - started >= TeleportStartTimeout)
			{
				return new TeleportArrivalResult(TeleportArrivalOutcome.TimedOut, tuple.Item1, StartObserved: false, $"Teleport to {destinationName} was accepted but did not start within {TeleportStartTimeout.TotalSeconds:F0} seconds.");
			}
			await Task.Delay(250, token);
		}
		uint num = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
		return new TeleportArrivalResult(TeleportArrivalOutcome.TimedOut, num, startObserved, $"Teleport did not arrive in {database.GetTerritoryName(targetTerritoryId)} ({targetTerritoryId}) within {TeleportArrivalTimeout.TotalSeconds:F0} seconds; current territory is {database.GetTerritoryName(num)} ({num}).");
	}

	private unsafe async Task TryDisablePreviousFateLevelSyncBeforeTravelAsync(string destinationName, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		ushort previousFateId = lastSyncedFateId;
		(bool, bool, bool, bool, bool) tuple = await RunOnFrameworkThreadAsync(delegate
		{
			PlayerState* ptr = PlayerState.Instance();
			(bool, ushort) currentJoinedFateUnsafe = GetCurrentJoinedFateUnsafe();
			bool item = previousFateId != 0 && GetMatchingFateStateUnsafe(previousFateId).Active;
			return (IsLevelSynced: ptr != null && ptr->IsLevelSynced, PreviousFateActive: item, JoinedFate: currentJoinedFateUnsafe.Item1, InCombat: condition[ConditionFlag.InCombat], Casting: condition[ConditionFlag.Casting] || (objectTable.LocalPlayer?.IsCasting ?? false));
		});
		if (tuple.Item1 && !tuple.Item2 && (previousFateId != 0 || !tuple.Item3) && !tuple.Item4 && !tuple.Item5)
		{
			if (await RunOnFrameworkThreadAsync(() => SendGameCommandUnsafe("/levelsync off")))
			{
				lastSyncedFateId = 0;
				log.Information($"[HuntLogs] Sent best-effort /levelsync off before cross-territory travel to {destinationName}; previousFate={previousFateId}, combatClear=true.");
			}
			else
			{
				log.Warning("[HuntLogs] Best-effort /levelsync off was not accepted before travel to " + destinationName + "; combat-interruption recovery remains authoritative.");
			}
		}
	}

	private unsafe HuntTravelPlan ResolveHuntTravelPlanUnsafe(uint territoryId, Vector3? destinationPosition)
	{
		try
		{
			ExcelSheet<Lumina.Excel.Sheets.Aetheryte> aetheryteSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
			UIState* uiState = UIState.Instance();
			if (uiState == null)
			{
				return new HuntTravelPlan(null, Array.Empty<HuntZoneTransition>(), string.Empty, "Live aetheryte unlock state is unavailable.");
			}
			if (territoryId == 139 && destinationPosition.HasValue)
			{
				Vector3 valueOrDefault = destinationPosition.GetValueOrDefault();
				if (Vector2.Distance(new Vector2(valueOrDefault.X, valueOrDefault.Z), new Vector2(-460f, 150f)) <= 150f && GetUnlocked(14u) != null)
				{
					return Route(14u, "Western La Noscea to western Upper La Noscea", new HuntZoneTransition[1]
					{
						new HuntZoneTransition(138u, 139u, new Vector3(412f, 31f, -15f))
					});
				}
			}
			List<HuntTeleportDestination> list = (from x in aetheryteSheet
				where x.IsAetheryte && x.Territory.RowId == territoryId
				select GetUnlocked(x.RowId)).OfType<HuntTeleportDestination>().ToList();
			if (list.Count > 0)
			{
				HuntTeleportDestination huntTeleportDestination;
				if (destinationPosition.HasValue)
				{
					Vector3 position = destinationPosition.GetValueOrDefault();
					huntTeleportDestination = list.OrderBy((HuntTeleportDestination x) => Vector3.DistanceSquared(x.Position, position)).First();
				}
				else
				{
					huntTeleportDestination = list[0];
				}
				HuntTeleportDestination huntTeleportDestination2 = huntTeleportDestination;
				return new HuntTravelPlan(huntTeleportDestination2, Array.Empty<HuntZoneTransition>(), "nearest unlocked aetheryte " + huntTeleportDestination2.Name, string.Empty);
			}
			HuntTravelPlan result;
			switch (territoryId)
			{
			case 139u:
				if (destinationPosition.HasValue)
				{
					Vector3 valueOrDefault2 = destinationPosition.GetValueOrDefault();
					if (Vector2.Distance(new Vector2(valueOrDefault2.X, valueOrDefault2.Z), new Vector2(-460f, 150f)) <= 150f)
					{
						result = Route(14u, "Western La Noscea to western Upper La Noscea", new HuntZoneTransition[1]
						{
							new HuntZoneTransition(138u, 139u, new Vector3(412f, 31f, -15f))
						});
						break;
					}
				}
				result = Route(14u, "Western, Middle, Eastern, and Upper La Noscea", new HuntZoneTransition[3]
				{
					new HuntZoneTransition(138u, 134u, new Vector3(812f, 50f, 400f)),
					new HuntZoneTransition(134u, 137u, new Vector3(-162f, 36f, -740f)),
					new HuntZoneTransition(137u, 139u, new Vector3(82f, 80f, -125f))
				});
				break;
			case 152u:
				result = Route(3u, "Central Shroud to East Shroud", new HuntZoneTransition[1]
				{
					new HuntZoneTransition(148u, 152u, new Vector3(390f, -3.3f, -186f))
				});
				break;
			case 154u:
				result = Route(2u, "New Gridania, Old Gridania, and North Shroud", new HuntZoneTransition[2]
				{
					new HuntZoneTransition(132u, 133u, new Vector3(-106f, 1.1f, 8f)),
					new HuntZoneTransition(133u, 154u, new Vector3(-208f, 10.4f, -95f))
				});
				break;
			case 155u:
				result = ((!(GetUnlocked(7u) != null)) ? Route(2u, "New Gridania, Old Gridania, North Shroud, and Coerthas Central Highlands", new HuntZoneTransition[3]
				{
					new HuntZoneTransition(132u, 133u, new Vector3(-106f, 1.1f, 8f)),
					new HuntZoneTransition(133u, 154u, new Vector3(-208f, 10.4f, -95f)),
					new HuntZoneTransition(154u, 155u, new Vector3(-369f, -7f, 185f))
				}) : Route(7u, "North Shroud to Coerthas Central Highlands", new HuntZoneTransition[1]
				{
					new HuntZoneTransition(154u, 155u, new Vector3(-369f, -7f, 185f))
				}));
				break;
			case 180u:
				result = Route(14u, "Western, Upper, and Outer La Noscea", new HuntZoneTransition[2]
				{
					new HuntZoneTransition(138u, 139u, new Vector3(412f, 31f, -15f)),
					new HuntZoneTransition(139u, 180u, new Vector3(-339f, 48.6f, -19f))
				});
				break;
			default:
				result = new HuntTravelPlan(null, Array.Empty<HuntZoneTransition>(), string.Empty, $"No unlocked aetheryte or valid non-attuning route exists for {database.GetTerritoryName(territoryId)} ({territoryId}).");
				break;
			}
			return result;
			unsafe HuntTeleportDestination? GetUnlocked(uint aetheryteId)
			{
				if (aetheryteId == 0 || !uiState->IsAetheryteUnlocked(aetheryteId) || !aetheryteSheet.TryGetRow(aetheryteId, out var row))
				{
					return null;
				}
				string text = row.PlaceName.ValueNullable?.Name.ExtractText();
				if (string.IsNullOrWhiteSpace(text))
				{
					return null;
				}
				return new HuntTeleportDestination(aetheryteId, 0, text, row.Territory.RowId, GetAetherytePosition(row));
			}
			HuntTravelPlan Route(uint anchorId, string description, params HuntZoneTransition[] transitions)
			{
				HuntTeleportDestination huntTeleportDestination3 = GetUnlocked(anchorId);
				if (!(huntTeleportDestination3 == null))
				{
					return new HuntTravelPlan(huntTeleportDestination3, transitions, description, string.Empty);
				}
				return new HuntTravelPlan(null, Array.Empty<HuntZoneTransition>(), string.Empty, $"No unlocked destination or non-attuning route exists for {database.GetTerritoryName(territoryId)} ({territoryId}); route requires unlocked aetheryte {anchorId}.");
			}
		}
		catch (Exception ex)
		{
			log.Warning($"[HuntLogs] Failed to resolve travel plan for territory {territoryId}: {ex.Message}");
			return new HuntTravelPlan(null, Array.Empty<HuntZoneTransition>(), string.Empty, $"Travel planning failed for {database.GetTerritoryName(territoryId)} ({territoryId}): {ex.Message}");
		}
	}

	private static Vector3 GetAetherytePosition(Lumina.Excel.Sheets.Aetheryte aetheryte)
	{
		Level? valueNullable = aetheryte.Level[0].ValueNullable;
		if (valueNullable.HasValue)
		{
			Level valueOrDefault = valueNullable.GetValueOrDefault();
			return new Vector3(valueOrDefault.X, valueOrDefault.Y, valueOrDefault.Z);
		}
		return Vector3.Zero;
	}

	private async Task<bool> TryMoveThroughZoneTransitionAsync(HuntZoneTransition transition, CancellationToken token)
	{
		uint num = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
		if (num == transition.ToTerritoryId)
		{
			return true;
		}
		if (num != transition.FromTerritoryId)
		{
			return false;
		}
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Routing " + database.GetTerritoryName(transition.FromTerritoryId) + " to " + database.GetTerritoryName(transition.ToTerritoryId);
		});
		HuntMovementPreparation huntMovementPreparation = await PrepareHuntMovementAsync(transition.Position, token);
		HuntMovementOutcome movement = await TryMoveToAsync(transition.Position, transition.FromTerritoryId, huntMovementPreparation.UseFlight, 3f, useCloseTo: false, token, "zone transition", $"{transition.FromTerritoryId}->{transition.ToTerritoryId} via {transition.Position}", null, (CancellationToken recoveryToken) => PrepareHuntMovementAsync(transition.Position, recoveryToken));
		if (movement.Result != HuntMovementResult.Completed && await RunOnFrameworkThreadAsync(() => clientState.TerritoryType) != transition.ToTerritoryId)
		{
			log.Warning($"[HuntLogs] Zone-transition movement did not complete: {transition.FromTerritoryId}->{transition.ToTerritoryId}, result={movement.Result}.");
			return false;
		}
		if (!(await WaitUntilFrameworkAsync(() => clientState.TerritoryType == transition.ToTerritoryId, "zone transition to " + database.GetTerritoryName(transition.ToTerritoryId), TimeSpan.FromSeconds(30L), token)))
		{
			return false;
		}
		return await TryWaitForCharacterReadyAsync(token);
	}

	private unsafe async Task<bool> TryUseLocalHuntRouteAsync(uint territoryId, Vector3 destination, CancellationToken token)
	{
		(Vector3, bool, bool, bool) tuple = await RunOnFrameworkThreadAsync(delegate
		{
			Vector3 item = objectTable.LocalPlayer?.Position ?? Vector3.Zero;
			HuntMovementContext huntMovementContextUnsafe = GetHuntMovementContextUnsafe();
			UIState* ptr = UIState.Instance();
			return (PlayerPosition: item, CanFly: huntMovementContextUnsafe.FlightDecision == HuntMovementFlightDecision.Unlocked, Aetheryte11Unlocked: ptr != null && ptr->IsAetheryteUnlocked(11u), Aetheryte12Unlocked: ptr != null && ptr->IsAetheryteUnlocked(12u));
		});
		if (tuple.Item2)
		{
			return false;
		}
		if (territoryId == 138 && Vector2.Distance(new Vector2(destination.X, destination.Z), new Vector2(-300f, 600f)) <= 300f)
		{
			return await TryUseFerryAsync(new Vector3(-317f, -36.2f, 351f), territoryId, 1003584u, selectDestination: true, "Isle of Umbra", token);
		}
		if (territoryId != 137)
		{
			return false;
		}
		bool num = destination.X < 200f || destination.Z < 57f;
		bool flag = tuple.Item1.X > 200f && tuple.Item1.Z > 57f;
		if (num && !tuple.Item4 && flag)
		{
			return await TryUseFerryAsync(new Vector3(346f, 33f, 93f), territoryId, 1003588u, selectDestination: false, "Raincatcher Gully", token);
		}
		bool num2 = destination.X >= 200f && destination.Z >= 57f;
		bool flag2 = tuple.Item1.X < 200f || tuple.Item1.Z < 57f;
		if (num2 && !tuple.Item3 && flag2)
		{
			return await TryUseFerryAsync(new Vector3(22f, 34f, 225f), territoryId, 1003589u, selectDestination: false, "Hidden Falls", token);
		}
		return false;
	}

	private unsafe async Task<bool> TryUseFerryAsync(Vector3 ferryPosition, uint territoryId, uint npcBaseId, bool selectDestination, string destinationName, CancellationToken token)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Taking ferry to " + destinationName;
		});
		await MoveToAsync(ferryPosition, territoryId, fly: false, 4f, token);
		Vector3 originPosition = await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero);
		if (!(await RunOnFrameworkThreadAsync(delegate
		{
			IGameObject gameObject = objectTable.FirstOrDefault((IGameObject x) => x.BaseId == npcBaseId && x.IsTargetable);
			if (gameObject == null)
			{
				return false;
			}
			TargetSystem* ptr = TargetSystem.Instance();
			GameObject* address = (GameObject*)gameObject.Address;
			if (ptr == null || address == null)
			{
				return false;
			}
			ptr->InteractWithObject(address, checkLineOfSight: false);
			return true;
		})))
		{
			throw new InvalidOperationException($"Ferry route to {destinationName} is required, but NPC {npcBaseId} was unavailable.");
		}
		if (selectDestination && !(await WaitUntilFrameworkAsync(() => FireCallback("SelectString", 0), "ferry destination " + destinationName, TimeSpan.FromSeconds(15L), token)))
		{
			throw new InvalidOperationException("Ferry route to " + destinationName + " did not present its destination selection.");
		}
		if (!(await WaitUntilFrameworkAsync(() => FireCallback("SelectYesno", 0), "ferry confirmation to " + destinationName, TimeSpan.FromSeconds(15L), token)))
		{
			throw new InvalidOperationException("Ferry route to " + destinationName + " did not present its confirmation.");
		}
		if (!(await WaitUntilFrameworkAsync(delegate
		{
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			return condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || (localPlayer != null && Vector3.Distance(localPlayer.Position, originPosition) > 30f);
		}, "ferry movement to " + destinationName, TimeSpan.FromSeconds(30L), token)))
		{
			throw new InvalidOperationException("Ferry route to " + destinationName + " was confirmed but movement was not observed.");
		}
		await TryWaitForTravelSettledAsync("ferry arrival at " + destinationName, TimeSpan.FromSeconds(30L), token);
		log.Information("[HuntLogs] Completed non-attuning ferry route to " + destinationName + "; no aetheryte interaction was used.");
		return true;
	}

	private async Task<Vector3> ProjectHuntPositionAsync(uint territoryId, Vector3 position, CancellationToken token)
	{
		await AssertTerritoryAsync(territoryId, $"projecting hunt position {position}", token);
		VNavmeshIPC vNavmeshIPC = vnavmeshIpc;
		Vector3 position2 = position;
		position2.Y = position.Y + 5f;
		Vector3? vector = vNavmeshIPC.FindPointOnFloor(position2, allowUnlandable: false, 15f);
		if (vector.HasValue)
		{
			log.Debug($"[HuntLogs] Projected rough hunt position {position} to landable flight destination {vector.Value} before live-target pursuit.");
			return vector.Value;
		}
		await AssertTerritoryAsync(territoryId, $"finding nearest mesh point for hunt position {position}", token);
		return vnavmeshIpc.FindNearestPoint(position, 15f, 8f) ?? position;
	}

	private async Task WaitForCombatAndCastingToEndAsync(string description, TimeSpan timeout, CancellationToken token, bool keepCombatAutomationActive, uint returnTerritoryId = 0u)
	{
		if (keepCombatAutomationActive)
		{
			await ResolveCombatIfNeededAsync(description, returnTerritoryId, token);
		}
		else if (!(await WaitUntilFrameworkAsync(() => !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Casting], "combat and casting to end before " + description, timeout, token)))
		{
			throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F0} seconds waiting for combat and casting to end before {description}.");
		}
	}

	private async Task ClearNearbyAggroBeforeTravelAsync(string description, CancellationToken token)
	{
		await ResolveCombatIfNeededAsync(description, await RunOnFrameworkThreadAsync(() => clientState.TerritoryType), token);
	}

	private async Task<bool> ResolveCombatIfNeededAsync(string description, uint returnTerritoryId, CancellationToken token)
	{
		(bool, bool, bool, uint) tuple = await RunOnFrameworkThreadAsync(() => (InCombat: condition[ConditionFlag.InCombat], Casting: condition[ConditionFlag.Casting], Dead: IsDeadOrUnconsciousUnsafe(), TerritoryId: clientState.TerritoryType));
		if (!tuple.Item1 && !tuple.Item2 && !tuple.Item3)
		{
			return false;
		}
		uint recoveryTerritoryId = ((returnTerritoryId != 0) ? returnTerritoryId : tuple.Item4);
		DateTime started = DateTime.UtcNow;
		DateTime? combatClearSince = null;
		DateTime nextStatusLogAt = started.Add(CombatClearStatusInterval);
		ulong lastTargetObjectId = ulong.MaxValue;
		ulong ownedAggroTargetObjectId = 0uL;
		bool touchedCombatAutomation = false;
		bool timeoutLogged = false;
		log.Information("[HuntLogs] Resolving combat before " + description + "; combat automation will be enabled only for NPCs already targeting the player or a player-owned object.");
		bool flag = vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding();
		if (flag)
		{
			flag = !(await StopNavigationAndWaitForIdleAsync(recoveryTerritoryId, "combat handoff before " + description, token, null, description));
		}
		if (flag)
		{
			throw new TimeoutException("Could not confirm vnavmesh idle before resolving combat for " + description + ".");
		}
		bool result;
		try
		{
			while (true)
			{
				token.ThrowIfCancellationRequested();
				(bool InCombat, bool Casting, bool Dead, uint TerritoryId) state = await RunOnFrameworkThreadAsync(() => (InCombat: condition[ConditionFlag.InCombat], Casting: condition[ConditionFlag.Casting], Dead: IsDeadOrUnconsciousUnsafe(), TerritoryId: clientState.TerritoryType));
				if (state.Dead)
				{
					await HandleDeathRecoveryAsync(recoveryTerritoryId, description, token);
					result = true;
					break;
				}
				DateTime now = DateTime.UtcNow;
				if (now - started >= CombatClearTimeout && !timeoutLogged)
				{
					timeoutLogged = true;
					log.Warning($"[HuntLogs] Combat was still active after {CombatClearTimeout.TotalSeconds:F0}s before {description}; " + "continuing combat resolution instead of failing hunt-log automation.");
				}
				if (!state.InCombat && !state.Casting)
				{
					combatClearSince.GetValueOrDefault();
					if (!combatClearSince.HasValue)
					{
						combatClearSince = now;
					}
					if (now - combatClearSince.Value >= CombatClearStableTime)
					{
						result = true;
						break;
					}
				}
				else
				{
					combatClearSince = null;
				}
				if (state.InCombat)
				{
					if (!(await DismountForCombatAsync(token)))
					{
						await Task.Delay(500, token);
						continue;
					}
					IBattleNpc target = await FindNearestAggroNpcAsync();
					if (target == null)
					{
						if (activeCombatBackend != CombatBackend.None)
						{
							await DisableCombatAsync();
						}
						ownedAggroTargetObjectId = 0uL;
						await ResetTargetAsync();
					}
					else
					{
						ulong targetObjectId = await RunOnFrameworkThreadAsync(() => target.GameObjectId);
						ushort runtimeFateId = await RunOnFrameworkThreadAsync(() => GetGameObjectFateIdUnsafe(target));
						if (runtimeFateId != 0)
						{
							flag = (await RunOnFrameworkThreadAsync(() => GetMatchingFateStateUnsafe(runtimeFateId))).RequiresLevelSync;
							if (flag)
							{
								flag = !(await EnsureFateSyncForTargetAsync("aggro cleanup", runtimeFateId, token));
							}
							if (flag)
							{
								if (activeCombatBackend != CombatBackend.None)
								{
									await DisableCombatAsync();
								}
								ownedAggroTargetObjectId = 0uL;
								await ResetTargetAsync();
								await Task.Delay(MatchingFatePollDelay, token);
								continue;
							}
						}
						if (targetObjectId == ownedAggroTargetObjectId)
						{
							await SetTargetAsync(target);
						}
						else
						{
							if (activeCombatBackend != CombatBackend.None)
							{
								await DisableCombatAsync();
							}
							await ResetTargetAsync();
							await SetTargetAsync(target);
							if (!(await TryEngageAggroTargetAsync(target, description)))
							{
								await ResetTargetAsync();
								ownedAggroTargetObjectId = 0uL;
								await Task.Delay(250, token);
								continue;
							}
							await EnableCombatAsync();
							touchedCombatAutomation = true;
							ownedAggroTargetObjectId = targetObjectId;
						}
						if (targetObjectId != lastTargetObjectId)
						{
							lastTargetObjectId = targetObjectId;
							string value = await RunOnFrameworkThreadAsync(() => target.Name.ToString());
							log.Information($"[HuntLogs] Clearing aggro target before {description}: {value} ({targetObjectId}).");
						}
					}
				}
				if (now >= nextStatusLogAt)
				{
					TimeSpan elapsed = now - started;
					string value2 = await RunOnFrameworkThreadAsync(delegate
					{
						IGameObject target2 = targetManager.Target;
						return (!(target2 is IBattleNpc { IsDead: false, CurrentHp: not 0u })) ? "none" : target2.Name.ToString();
					});
					log.Information($"[HuntLogs] Still clearing combat before {description} after {elapsed.TotalSeconds:F0}s; inCombat={state.InCombat}, casting={state.Casting}, target={value2}.");
					nextStatusLogAt = now.Add(CombatClearStatusInterval);
				}
				await Task.Delay(250, token);
			}
		}
		finally
		{
			if (touchedCombatAutomation)
			{
				await DisableCombatAsync();
			}
			await ResetTargetAsync();
		}
		return result;
	}

	private async Task<bool> TryEngageAggroTargetAsync(IBattleNpc target, string description)
	{
		HuntTargetEngageAttempt huntTargetEngageAttempt = await RunOnFrameworkThreadAsync(() => TryEngageAggroTargetUnsafe(target));
		if (!huntTargetEngageAttempt.Ready)
		{
			log.Debug("[HuntLogs] Aggro engage skipped before " + description + ": " + huntTargetEngageAttempt.Reason);
			return false;
		}
		if (huntTargetEngageAttempt.AlreadyEngaged)
		{
			return true;
		}
		return false;
	}

	private HuntTargetEngageAttempt TryEngageAggroTargetUnsafe(IBattleNpc target)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (!clientState.IsLoggedIn)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "client is not logged in");
		}
		if (localPlayer == null)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "local player is unavailable");
		}
		if (condition[ConditionFlag.Unconscious] || localPlayer.CurrentHp == 0 || localPlayer.IsDead)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is dead or unconscious");
		}
		if (condition[ConditionFlag.Casting] || localPlayer.IsCasting)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is casting");
		}
		if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.InFlight])
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is mounted or mounting");
		}
		if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is changing areas or logging out");
		}
		if (!target.IsTargetable || target.IsDead || target.CurrentHp == 0)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "target is not attackable");
		}
		if (!IsTargetingPlayerOrOwnedObjectUnsafe(target, localPlayer.GameObjectId))
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "target is not attacking the player or a player-owned object");
		}
		return new HuntTargetEngageAttempt(Ready: true, AlreadyEngaged: true, Interacted: false, AttackSent: false, string.Empty);
	}

	private async Task<IBattleNpc?> FindNearestAggroNpcAsync()
	{
		return await RunOnFrameworkThreadAsync(delegate
		{
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer == null)
			{
				return (IBattleNpc)null;
			}
			Vector3 playerPosition = localPlayer.Position;
			ulong gameObjectId = localPlayer.GameObjectId;
			HashSet<ulong> protectedObjectIds = new HashSet<ulong> { gameObjectId };
			foreach (IGameObject item in objectTable)
			{
				if (item != null && item.OwnerId == gameObjectId)
				{
					protectedObjectIds.Add(item.GameObjectId);
				}
			}
			return (from x in objectTable.OfType<IBattleNpc>()
				where x.IsTargetable && !x.IsDead && x.CurrentHp != 0
				where protectedObjectIds.Contains(x.TargetObjectId)
				where Vector3.Distance(x.Position, playerPosition) <= 45f
				orderby Vector3.Distance(x.Position, playerPosition)
				select x).FirstOrDefault();
		});
	}

	private bool IsTargetingPlayerOrOwnedObjectUnsafe(IBattleNpc target, ulong playerObjectId)
	{
		ulong targetObjectId = target.TargetObjectId;
		if (targetObjectId == playerObjectId)
		{
			return true;
		}
		if (targetObjectId != 0L)
		{
			return objectTable.Any((IGameObject gameObject) => gameObject != null && gameObject.GameObjectId == targetObjectId && gameObject.OwnerId == playerObjectId);
		}
		return false;
	}

	private async Task AssertTerritoryAsync(uint expectedTerritoryId, string operation, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		uint actualTerritoryId = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
		if (actualTerritoryId == expectedTerritoryId)
		{
			return;
		}
		if (vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding())
		{
			await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "territory mismatch while " + operation, token, null, operation);
		}
		throw CreateTerritoryMismatchException(expectedTerritoryId, actualTerritoryId, operation);
	}

	private InvalidOperationException CreateTerritoryMismatchException(uint expectedTerritoryId, uint actualTerritoryId, string operation)
	{
		return new InvalidOperationException($"Refusing hunt navigation while {operation}: expected {database.GetTerritoryName(expectedTerritoryId)} ({expectedTerritoryId}), but the client is in {database.GetTerritoryName(actualTerritoryId)} ({actualTerritoryId}).");
	}

	private static bool IsHuntTerritoryMismatchException(InvalidOperationException ex)
	{
		return ex.Message.StartsWith("Refusing hunt navigation", StringComparison.OrdinalIgnoreCase);
	}

	private async Task<List<Vector3>> SortPositionsByDistanceAsync(List<Vector3> positions)
	{
		Vector3 playerPosition = await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero);
		return positions.OrderBy((Vector3 position) => Vector3.Distance(position, playerPosition)).ToList();
	}

	private async Task MoveToAsync(Vector3 target, uint expectedTerritoryId, bool fly, float tolerance, CancellationToken token, bool useCloseTo = false, string? diagnosticMarkName = null, string? diagnosticTarget = null, Func<Task<bool>>? loadedValidTargetExistsAsync = null)
	{
		if ((await TryMoveToAsync(target, expectedTerritoryId, fly, tolerance, useCloseTo, token, diagnosticMarkName, diagnosticTarget, loadedValidTargetExistsAsync)).Result != HuntMovementResult.Completed)
		{
			throw new InvalidOperationException($"vnavmesh could not move to {target} within {tolerance:F1} yalms.");
		}
	}

	private async Task<HuntMovementOutcome> TryMoveToAsync(Vector3 target, uint expectedTerritoryId, bool fly, float tolerance, bool useCloseTo, CancellationToken token, string? diagnosticMarkName = null, string? diagnosticTarget = null, Func<Task<bool>>? loadedValidTargetExistsAsync = null, Func<CancellationToken, Task<HuntMovementPreparation>>? recoverMovementPreparationAsync = null)
	{
		bool startAccepted = false;
		HuntMovementResult lastRecoveryResult = HuntMovementResult.Stopped;
		int movementAttempt = 1;
		int combatRecoveries = 0;
		while (movementAttempt <= 2)
		{
			bool flag;
			if (!(await WaitForMovementReadyAsync(expectedTerritoryId, token, diagnosticMarkName, diagnosticTarget ?? target.ToString(), loadedValidTargetExistsAsync)))
			{
				flag = loadedValidTargetExistsAsync != null;
				if (flag)
				{
					flag = await loadedValidTargetExistsAsync();
				}
				if (flag)
				{
					string text = (string.IsNullOrWhiteSpace(diagnosticMarkName) ? "hunt movement" : diagnosticMarkName);
					log.Information("[HuntLogs] Movement readiness yielded for " + text + "; loaded target is available, returning to pursuit.");
					return new HuntMovementOutcome(HuntMovementResult.Stopped, startAccepted);
				}
				await LogMovementReadinessTimeoutAsync(expectedTerritoryId, diagnosticMarkName, diagnosticTarget ?? target.ToString(), loadedValidTargetExistsAsync);
				return new HuntMovementOutcome(HuntMovementResult.Stopped, startAccepted);
			}
			try
			{
				await AssertTerritoryAsync(expectedTerritoryId, $"starting hunt movement to {target}", token);
			}
			catch (InvalidOperationException ex) when (diagnosticMarkName != null && IsHuntTerritoryMismatchException(ex))
			{
				log.Warning("[HuntLogs] Movement for " + diagnosticMarkName + " paused because territory changed: " + ex.Message);
				return new HuntMovementOutcome(HuntMovementResult.Stopped, startAccepted);
			}
			Vector3 currentPosition = await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero);
			if (Vector3.Distance(currentPosition, target) <= tolerance)
			{
				return new HuntMovementOutcome(HuntMovementResult.Completed, startAccepted);
			}
			flag = fly;
			if (flag)
			{
				flag = !(await RunOnFrameworkThreadAsync((Func<bool>)CanStartFlightMovementUnsafe));
			}
			if (flag)
			{
				if (recoverMovementPreparationAsync != null)
				{
					fly = (await recoverMovementPreparationAsync(token)).UseFlight;
				}
				else if (!(await EnsureMountedForMovementAsync(target, requireMounted: true, token)))
				{
					fly = false;
				}
				if (!fly)
				{
					log.Information($"[HuntLogs] Flight was no longer ready before movement to {target}; " + "continuing with the freshly prepared ground mode.");
				}
			}
			flag = fly;
			if (flag)
			{
				flag = !(await RunOnFrameworkThreadAsync((Func<bool>)CanStartFlightMovementUnsafe));
			}
			if (flag)
			{
				await LogHuntMovementFailureAsync(HuntMovementResult.StartRejected, target, tolerance, fly, useCloseTo, Vector3.Distance(currentPosition, target), Vector3.Distance(currentPosition, target), "mounted state was not verified");
				return new HuntMovementOutcome(HuntMovementResult.StartRejected, startAccepted);
			}
			if (!(await TryStartVnavmeshMoveAsync(target, expectedTerritoryId, fly, tolerance, useCloseTo, token)))
			{
				float num = Vector3.Distance(currentPosition, target);
				await LogHuntMovementFailureAsync(HuntMovementResult.StartRejected, target, tolerance, fly, useCloseTo, num, num, "vnavmesh rejected every start request");
				return new HuntMovementOutcome(HuntMovementResult.StartRejected, startAccepted);
			}
			startAccepted = true;
			HuntMovementResult huntMovementResult;
			try
			{
				huntMovementResult = await WaitForMovementCompletionAsync(target, expectedTerritoryId, tolerance, fly, useCloseTo, token);
			}
			catch (InvalidOperationException ex2) when (diagnosticMarkName != null && IsHuntTerritoryMismatchException(ex2))
			{
				log.Warning("[HuntLogs] Movement for " + diagnosticMarkName + " stopped because territory changed: " + ex2.Message);
				return new HuntMovementOutcome(HuntMovementResult.Stopped, startAccepted);
			}
			switch (huntMovementResult)
			{
			case HuntMovementResult.Completed:
				return new HuntMovementOutcome(HuntMovementResult.Completed, startAccepted);
			case HuntMovementResult.RecoveredFromDeath:
			case HuntMovementResult.RecoveredFromCombat:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (!flag)
			{
				return new HuntMovementOutcome(huntMovementResult, startAccepted);
			}
			string value = ((huntMovementResult == HuntMovementResult.RecoveredFromDeath) ? "death recovery" : "combat cleanup");
			lastRecoveryResult = huntMovementResult;
			if (huntMovementResult == HuntMovementResult.RecoveredFromDeath)
			{
				log.Information($"[HuntLogs] Retrying movement to {target} after {value} ({movementAttempt}/2).");
				movementAttempt++;
				continue;
			}
			combatRecoveries++;
			if (combatRecoveries > 20)
			{
				log.Warning($"[HuntLogs] Movement to {target} exceeded {20} consecutive combat recoveries.");
				return new HuntMovementOutcome(lastRecoveryResult, startAccepted);
			}
			if (recoverMovementPreparationAsync != null)
			{
				fly = (await recoverMovementPreparationAsync(token)).UseFlight;
			}
			else
			{
				bool flag2 = await EnsureMountedForMovementAsync(target, fly, token);
				if (fly && !flag2)
				{
					fly = false;
				}
			}
			log.Information($"[HuntLogs] Resuming movement to {target} after combat cleanup; movementAttempt={movementAttempt}/2, combatRecovery={combatRecoveries}/{20}, mode={(fly ? "flight" : "ground")}.");
		}
		return new HuntMovementOutcome(lastRecoveryResult, startAccepted);
	}

	private async Task<bool> StopNavigationAndWaitForIdleAsync(uint expectedTerritoryId, string reason, CancellationToken token, string? markName = null, string? targetDescription = null, Func<Task<bool>>? loadedValidTargetExistsAsync = null)
	{
		bool value = vnavmeshIpc.IsPathRunning();
		bool value2 = vnavmeshIpc.IsPathfinding();
		log.Information($"[HuntLogs] Requesting one vnavmesh stop and waiting for owned idle: reason={reason}, pathRunning={value}, pathfinding={value2}.");
		vnavmeshIpc.StopCompletely();
		DateTime started = DateTime.UtcNow;
		DateTime? stableSince = null;
		while (DateTime.UtcNow - started < MovementReadinessTimeout)
		{
			token.ThrowIfCancellationRequested();
			DateTime utcNow = DateTime.UtcNow;
			bool flag = vnavmeshIpc.IsReady();
			bool flag2 = vnavmeshIpc.IsPathRunning();
			bool flag3 = vnavmeshIpc.IsPathfinding();
			if (flag && !flag2 && !flag3)
			{
				stableSince.GetValueOrDefault();
				if (!stableSince.HasValue)
				{
					DateTime value3 = utcNow;
					stableSince = value3;
				}
				if (utcNow - stableSince.Value >= MovementIdleStableTime)
				{
					log.Debug($"[HuntLogs] vnavmesh idle ownership confirmed after stop: reason={reason}, stableForMs={MovementIdleStableTime.TotalMilliseconds:F0}.");
					return true;
				}
			}
			else
			{
				stableSince = null;
			}
			await Task.Delay(250, token);
		}
		await LogMovementReadinessTimeoutAsync(expectedTerritoryId, markName, targetDescription ?? reason, loadedValidTargetExistsAsync, "stop/idle confirmation failed after requesting stop once (" + reason + ")");
		return false;
	}

	private async Task<bool> WaitForMovementReadyAsync(uint expectedTerritoryId, CancellationToken token, string? markName = null, string? targetDescription = null, Func<Task<bool>>? loadedValidTargetExistsAsync = null)
	{
		DateTime started = DateTime.UtcNow;
		DateTime? stableSince = null;
		bool existingNavigationStopRequested = false;
		while (DateTime.UtcNow - started < MovementReadinessTimeout)
		{
			token.ThrowIfCancellationRequested();
			DateTime now = DateTime.UtcNow;
			if (await TryHandleDeathRecoveryAsync(expectedTerritoryId, "waiting for hunt movement", token))
			{
				stableSince = null;
				started = DateTime.UtcNow;
				continue;
			}
			if (await ResolveCombatIfNeededAsync("waiting for hunt movement", expectedTerritoryId, token))
			{
				stableSince = null;
				started = DateTime.UtcNow;
				continue;
			}
			bool flag = await RunOnFrameworkThreadAsync((Func<bool>)IsCharacterReadyForMovementUnsafe);
			bool flag2 = vnavmeshIpc.IsReady();
			bool flag3 = vnavmeshIpc.IsPathRunning();
			bool flag4 = vnavmeshIpc.IsPathfinding();
			bool flag5 = !flag3 && !flag4;
			if (!flag5)
			{
				stableSince = null;
				if (!existingNavigationStopRequested)
				{
					existingNavigationStopRequested = true;
					if (!(await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "acquiring existing navigation before replacement movement", token, markName, targetDescription, loadedValidTargetExistsAsync)))
					{
						return false;
					}
					bool flag6 = loadedValidTargetExistsAsync != null;
					if (flag6)
					{
						flag6 = await loadedValidTargetExistsAsync();
					}
					if (flag6)
					{
						return false;
					}
					started = DateTime.UtcNow;
					continue;
				}
			}
			if (flag && flag2 && flag5)
			{
				stableSince.GetValueOrDefault();
				if (!stableSince.HasValue)
				{
					stableSince = now;
				}
				if (now - stableSince.Value >= MovementIdleStableTime)
				{
					return true;
				}
			}
			else
			{
				stableSince = null;
			}
			await Task.Delay(250, token);
		}
		return false;
	}

	private async Task LogMovementReadinessTimeoutAsync(uint expectedTerritoryId, string? markName, string targetDescription, Func<Task<bool>>? loadedValidTargetExistsAsync, string? readinessContext = null)
	{
		_ = 1;
		try
		{
			bool flag = loadedValidTargetExistsAsync != null;
			if (flag)
			{
				flag = await loadedValidTargetExistsAsync();
			}
			bool loadedValidTargetExists = flag;
			bool navReady = vnavmeshIpc.IsReady();
			bool pathRunning = vnavmeshIpc.IsPathRunning();
			bool pathfinding = vnavmeshIpc.IsPathfinding();
			(uint, bool, bool, bool, bool, bool) tuple = await RunOnFrameworkThreadAsync(() => (TerritoryId: clientState.TerritoryType, CharacterReady: IsCharacterReadyForMovementUnsafe(), InCombat: condition[ConditionFlag.InCombat], Casting: condition[ConditionFlag.Casting], Dead: IsDeadOrUnconsciousUnsafe(), BetweenAreas: condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]));
			string value = (string.IsNullOrWhiteSpace(markName) ? "hunt movement" : markName);
			log.Warning($"[HuntLogs] Movement readiness timed out for {value}: context={readinessContext ?? "waiting to start movement"}, target={targetDescription}, expectedTerritory={database.GetTerritoryName(expectedTerritoryId)} ({expectedTerritoryId}), actualTerritory={database.GetTerritoryName(tuple.Item1)} ({tuple.Item1}), characterReady={tuple.Item2}, navReady={navReady}, pathRunning={pathRunning}, pathfinding={pathfinding}, inCombat={tuple.Item3}, casting={tuple.Item4}, dead={tuple.Item5}, betweenAreas={tuple.Item6}, loadedValidTarget={loadedValidTargetExists}.");
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Movement readiness timed out, but diagnostics could not be collected: " + ex.Message);
		}
	}

	private async Task<bool> TryStartVnavmeshMoveAsync(Vector3 target, uint expectedTerritoryId, bool fly, float tolerance, bool useCloseTo, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		string mode = (fly ? "flight" : "ground");
		while (DateTime.UtcNow - started < MovementStartRetryTimeout)
		{
			token.ThrowIfCancellationRequested();
			if (await TryHandleDeathRecoveryAsync(expectedTerritoryId, $"starting vnavmesh movement to {target}", token))
			{
				started = DateTime.UtcNow;
				continue;
			}
			if (await ResolveCombatIfNeededAsync($"starting vnavmesh movement to {target}", expectedTerritoryId, token))
			{
				started = DateTime.UtcNow;
				continue;
			}
			await AssertTerritoryAsync(expectedTerritoryId, $"requesting vnavmesh movement to {target}", token);
			if (useCloseTo ? vnavmeshIpc.PathfindAndMoveCloseTo(target, fly, tolerance) : vnavmeshIpc.PathfindAndMoveTo(target, fly))
			{
				log.Information($"[HuntLogs] vnavmesh started: territory={expectedTerritoryId}, mode={mode}, target={target}, closeTo={useCloseTo}, tolerance={tolerance:F1}");
				return true;
			}
			await Task.Delay(MovementStartRetryDelay, token);
		}
		log.Warning($"[HuntLogs] vnavmesh rejected {mode} movement to {target} after {MovementStartRetryTimeout.TotalSeconds:F0}s.");
		return false;
	}

	private async Task<HuntMovementResult> WaitForMovementCompletionAsync(Vector3 target, uint expectedTerritoryId, float tolerance, bool fly, bool useCloseTo, CancellationToken token)
	{
		TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.HuntLogs.MovementTimeoutSeconds, 30, 600));
		DateTime started = DateTime.UtcNow;
		DateTime commandAcceptedAt = DateTime.UtcNow;
		DateTime? stoppedSince = null;
		Vector3 progressAnchor = await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero);
		float bestDistance = Vector3.Distance(progressAnchor, target);
		DateTime lastProgressAt = commandAcceptedAt;
		bool navActivityObserved = false;
		bool wasPathfinding = false;
		int repathAttempts = 0;
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			(uint TerritoryId, Vector3 Position) playerState = await RunOnFrameworkThreadAsync(() => (TerritoryId: clientState.TerritoryType, Position: objectTable.LocalPlayer?.Position ?? Vector3.Zero));
			if (playerState.TerritoryId != expectedTerritoryId)
			{
				await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, $"territory changed while moving to {target}", token, null, target.ToString());
				throw CreateTerritoryMismatchException(expectedTerritoryId, playerState.TerritoryId, $"moving to {target}");
			}
			if (await TryHandleDeathRecoveryAsync(expectedTerritoryId, $"moving to {target}", token))
			{
				return HuntMovementResult.RecoveredFromDeath;
			}
			if (await ResolveCombatIfNeededAsync($"moving to {target}", expectedTerritoryId, token))
			{
				return HuntMovementResult.RecoveredFromCombat;
			}
			float distance = Vector3.Distance(playerState.Position, target);
			if (distance <= tolerance)
			{
				return HuntMovementResult.Completed;
			}
			bestDistance = Math.Min(bestDistance, distance);
			DateTime utcNow = DateTime.UtcNow;
			bool flag = vnavmeshIpc.IsPathRunning();
			bool flag2 = vnavmeshIpc.IsPathfinding();
			if (flag2)
			{
				stoppedSince = null;
				if (!navActivityObserved)
				{
					navActivityObserved = true;
					log.Debug($"[HuntLogs] Accepted movement became active: target={target}, distance={distance:F1}, pathRunning={flag}, pathfinding={flag2}.");
				}
				wasPathfinding = true;
			}
			else if (flag)
			{
				stoppedSince = null;
				if (!navActivityObserved || wasPathfinding)
				{
					if (!navActivityObserved)
					{
						log.Debug($"[HuntLogs] Accepted movement became active: target={target}, distance={distance:F1}, pathRunning={flag}, pathfinding={flag2}.");
					}
					navActivityObserved = true;
					progressAnchor = playerState.Position;
					lastProgressAt = utcNow;
					wasPathfinding = false;
				}
				else if (Vector3.Distance(playerState.Position, progressAnchor) >= 1.5f)
				{
					progressAnchor = playerState.Position;
					lastProgressAt = utcNow;
				}
				if (utcNow - lastProgressAt >= MovementNoProgressTimeout)
				{
					string reason = $"player moved less than {1.5f:F1} yalms for {MovementNoProgressTimeout.TotalSeconds:F0}s while a path was running";
					if (!(await TryStopAndRepathAsync(reason, distance, playerState.Position)))
					{
						await LogHuntMovementFailureAsync(HuntMovementResult.NoProgress, target, tolerance, fly, useCloseTo, distance, bestDistance, $"{reason} after {repathAttempts} re-path attempts");
						return HuntMovementResult.NoProgress;
					}
					continue;
				}
			}
			else if (!navActivityObserved)
			{
				if (utcNow - commandAcceptedAt >= MovementStartRetryTimeout)
				{
					string reason = $"accepted command never became pathfinding/path-running within {MovementStartRetryTimeout.TotalSeconds:F0}s";
					if (!(await TryStopAndRepathAsync(reason, distance, playerState.Position)))
					{
						await LogHuntMovementFailureAsync(HuntMovementResult.Stopped, target, tolerance, fly, useCloseTo, distance, bestDistance, $"{reason} after {repathAttempts} re-path attempts");
						return HuntMovementResult.Stopped;
					}
					continue;
				}
			}
			else
			{
				stoppedSince.GetValueOrDefault();
				if (!stoppedSince.HasValue)
				{
					DateTime value = utcNow;
					stoppedSince = value;
				}
				if (utcNow - stoppedSince.Value >= MovementStoppedGraceTime)
				{
					string reason = $"navigation was inactive for {MovementStoppedGraceTime.TotalSeconds:F0}s";
					if (!(await TryStopAndRepathAsync(reason, distance, playerState.Position)))
					{
						await LogHuntMovementFailureAsync(HuntMovementResult.Stopped, target, tolerance, fly, useCloseTo, distance, bestDistance, $"{reason} after {repathAttempts} re-path attempts");
						return HuntMovementResult.Stopped;
					}
					continue;
				}
			}
			await Task.Delay(250, token);
		}
		Vector3 value2 = await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position ?? Vector3.Zero);
		await LogHuntMovementFailureAsync(HuntMovementResult.TimedOut, target, tolerance, fly, useCloseTo, Vector3.Distance(value2, target), bestDistance, $"movement exceeded {timeout.TotalSeconds:F0}s");
		return HuntMovementResult.TimedOut;
		async Task<bool> TryStopAndRepathAsync(string value4, float num, Vector3 playerPosition)
		{
			if (repathAttempts >= 2)
			{
				return false;
			}
			repathAttempts++;
			string value3 = (fly ? "flight" : "ground");
			log.Information($"[HuntLogs] Movement stalled; stop/reissue {repathAttempts}/{2}: mode={value3}, reason={value4}, target={target}, distance={num:F1}, bestDistance={bestDistance:F1}.");
			if (!(await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, $"movement stop/reissue {repathAttempts}/{2}: {value4}", token, null, target.ToString())))
			{
				log.Warning($"[HuntLogs] Movement reissue {repathAttempts}/{2} was not sent because " + "vnavmesh idle ownership was not confirmed.");
				return false;
			}
			await AssertTerritoryAsync(expectedTerritoryId, $"repathing hunt movement to {target}", token);
			if (!(await TryStartVnavmeshMoveAsync(target, expectedTerritoryId, fly, tolerance, useCloseTo, token)))
			{
				return false;
			}
			stoppedSince = null;
			bestDistance = Math.Min(bestDistance, num);
			commandAcceptedAt = DateTime.UtcNow;
			lastProgressAt = commandAcceptedAt;
			progressAnchor = playerPosition;
			navActivityObserved = false;
			wasPathfinding = false;
			return true;
		}
	}

	private async Task LogHuntMovementFailureAsync(HuntMovementResult result, Vector3 target, float tolerance, bool fly, bool useCloseTo, float distance, float bestDistance, string reason)
	{
		(bool, bool) tuple = await RunOnFrameworkThreadAsync(() => (Mounted: condition[ConditionFlag.Mounted], InFlight: condition[ConditionFlag.InFlight]));
		bool value = vnavmeshIpc.IsReady();
		bool value2 = vnavmeshIpc.IsPathRunning();
		bool value3 = vnavmeshIpc.IsPathfinding();
		string value4 = (fly ? "flight" : "ground");
		string value5 = (useCloseTo ? "close-to" : "exact");
		log.Warning($"[HuntLogs] Hunt movement recoverable failure: result={result}, reason={reason}, mode={value4}, destination={target}, pathing={value5}, tolerance={tolerance:F1}, distance={distance:F1}, bestDistance={bestDistance:F1}, mounted={tuple.Item1}, inFlight={tuple.Item2}, navReady={value}, pathRunning={value2}, pathfinding={value3}.");
	}

	private async Task MoveToObjectAsync(IGameObject target, uint expectedTerritoryId, CancellationToken token)
	{
		if (!(await TryMoveToObjectAsync(target, expectedTerritoryId, 4f, token)))
		{
			throw new InvalidOperationException($"vnavmesh could not move to loaded object {target.Name}.");
		}
	}

	private async Task<bool> TryMoveToObjectAsync(IGameObject target, uint expectedTerritoryId, float tolerance, CancellationToken token, string? diagnosticMarkName = null, Func<Task<bool>>? loadedValidTargetExistsAsync = null, HuntMovementPolicy? movementPolicy = null, bool forceFlightApproach = false)
	{
		HuntObjectMovementTarget movementTarget = await RunOnFrameworkThreadAsync(delegate
		{
			uint nameId = ((target is IBattleNpc battleNpc) ? battleNpc.NameId : 0u);
			Vector3 value = objectTable.LocalPlayer?.Position ?? target.Position;
			return new HuntObjectMovementTarget(target.Name.ToString(), nameId, target.BaseId, target.Position, Vector3.Distance(value, target.Position));
		});
		string text = $"loaded target {movementTarget.Name} (nameId={movementTarget.NameId}, baseId={movementTarget.BaseId}) at {movementTarget.Position}";
		if (movementTarget.Distance <= tolerance)
		{
			if ((vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding()) && !(await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "direct combat handoff for " + movementTarget.Name, token, diagnosticMarkName, text, loadedValidTargetExistsAsync)))
			{
				return false;
			}
			log.Debug($"[HuntLogs] Loaded {movementTarget.Name} is already within combat movement tolerance ({movementTarget.Distance:F1} <= {tolerance:F1}); bypassing vnavmesh movement readiness.");
			return true;
		}
		float num = Math.Clamp(configuration.HuntLogs.GroundApproachDistance, 5f, 100f);
		bool flag = !forceFlightApproach && movementTarget.Distance <= num;
		DateTime utcNow = DateTime.UtcNow;
		if ((forceFlightApproach || flag) && utcNow >= shortGroundApproachDiagnosticNotBeforeUtc)
		{
			shortGroundApproachDiagnosticNotBeforeUtc = utcNow + ShortGroundApproachDiagnosticThrottle;
			if (forceFlightApproach)
			{
				log.Information($"[HuntLogs] Loaded {movementTarget.Name} is {movementTarget.Distance:F1} yalms away; " + "continuing the active flight directly to its live position before combat.");
			}
			else
			{
				log.Information($"[HuntLogs] Loaded {movementTarget.Name} is {movementTarget.Distance:F1} yalms away (ground-only threshold {num:F0}); " + "suppressing mount and flight for a short ground approach.");
			}
		}
		return await TryMoveToHuntLocationAsync(movementTarget.Position, expectedTerritoryId, tolerance, useCloseTo: true, token, diagnosticMarkName, text, loadedValidTargetExistsAsync, movementPolicy, flag, forceFlightApproach);
	}

	private async Task MoveToHuntLocationAsync(Vector3 target, uint expectedTerritoryId, float tolerance, bool useCloseTo, CancellationToken token)
	{
		if (!(await TryMoveToHuntLocationAsync(target, expectedTerritoryId, tolerance, useCloseTo, token)))
		{
			throw new InvalidOperationException($"vnavmesh could not move to {target} within {tolerance:F1} yalms.");
		}
	}

	private async Task<bool> TryMoveToHuntLocationAsync(Vector3 target, uint expectedTerritoryId, float tolerance, bool useCloseTo, CancellationToken token, string? diagnosticMarkName = null, string? diagnosticTarget = null, Func<Task<bool>>? loadedValidTargetExistsAsync = null, HuntMovementPolicy? movementPolicy = null, bool forceShortGroundApproach = false, bool forceFlightApproach = false)
	{
		try
		{
			await AssertTerritoryAsync(expectedTerritoryId, $"preparing hunt movement to {target}", token);
		}
		catch (InvalidOperationException ex) when (diagnosticMarkName != null && IsHuntTerritoryMismatchException(ex))
		{
			log.Warning("[HuntLogs] Movement preparation for " + diagnosticMarkName + " paused because territory changed: " + ex.Message);
			return false;
		}
		bool markScopedMineGround = movementPolicy?.ForceMountedGround ?? false;
		bool flag = !markScopedMineGround && IsOuterLaNosceaMinePosition(expectedTerritoryId, target);
		bool forceOuterLaNosceaGround = markScopedMineGround || flag;
		HuntMovementPreparation preparation = await PrepareMovementAsync(token);
		HuntMovementOutcome outcome = await TryMoveToAsync(target, expectedTerritoryId, preparation.UseFlight, tolerance, useCloseTo, token, diagnosticMarkName, diagnosticTarget, loadedValidTargetExistsAsync, PrepareMovementAsync);
		if (outcome.Result == HuntMovementResult.Completed)
		{
			return true;
		}
		if (!preparation.UseFlight || !ShouldAttemptGroundFallbackAfterFlight(outcome))
		{
			await RefreshHuntPursuitAfterMovementFailureAsync(expectedTerritoryId, diagnosticMarkName, outcome, loadedValidTargetExistsAsync, token);
			return false;
		}
		log.Warning($"[HuntLogs] Flight movement did not complete after {preparation.Context.FlightDecision} decision (result={outcome.Result}, startAccepted={outcome.StartAccepted}); using one ground attempt for destination={target}.");
		if (!(await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "flight-to-ground fallback", token, diagnosticMarkName, diagnosticTarget ?? target.ToString(), loadedValidTargetExistsAsync)))
		{
			log.Warning($"[HuntLogs] Ground fallback was not started for {target} because vnavmesh idle ownership " + "was not confirmed after the flight attempt.");
			await RefreshHuntPursuitAfterMovementFailureAsync(expectedTerritoryId, diagnosticMarkName, outcome, loadedValidTargetExistsAsync, token, ensureNavigationIdle: false);
			return false;
		}
		bool flag2 = loadedValidTargetExistsAsync != null;
		if (flag2)
		{
			flag2 = await loadedValidTargetExistsAsync();
		}
		if (flag2)
		{
			log.Information("[HuntLogs] Ground fallback yielded for " + (diagnosticMarkName ?? "hunt target") + "; a loaded valid target appeared after the flight stop/idle transition.");
			await RefreshHuntPursuitAfterMovementFailureAsync(expectedTerritoryId, diagnosticMarkName, outcome, loadedValidTargetExistsAsync, token, ensureNavigationIdle: false);
			return false;
		}
		HuntMovementOutcome outcome2 = await TryMoveToAsync(target, expectedTerritoryId, fly: false, tolerance, useCloseTo, token, diagnosticMarkName, diagnosticTarget, loadedValidTargetExistsAsync, PrepareGroundFallbackAsync);
		if (outcome2.Result == HuntMovementResult.Completed)
		{
			return true;
		}
		await RefreshHuntPursuitAfterMovementFailureAsync(expectedTerritoryId, diagnosticMarkName, outcome2, loadedValidTargetExistsAsync, token);
		return false;
		async Task<HuntMovementPreparation> PrepareGroundFallbackAsync(CancellationToken preparationToken)
		{
			HuntMovementContext context = await RunOnFrameworkThreadAsync((Func<HuntMovementContext>)GetHuntMovementContextUnsafe);
			if (!forceShortGroundApproach)
			{
				await EnsureMountedForMovementAsync(target, forceOuterLaNosceaGround, preparationToken);
			}
			return new HuntMovementPreparation(context, UseFlight: false);
		}
		async Task<HuntMovementPreparation> PrepareMovementAsync(CancellationToken preparationToken)
		{
			if (forceFlightApproach)
			{
				return new HuntMovementPreparation(await RunOnFrameworkThreadAsync((Func<HuntMovementContext>)GetHuntMovementContextUnsafe), await EnsureMountedForMovementAsync(target, requireMounted: true, preparationToken));
			}
			if (forceShortGroundApproach)
			{
				return new HuntMovementPreparation(await RunOnFrameworkThreadAsync((Func<HuntMovementContext>)GetHuntMovementContextUnsafe), UseFlight: false);
			}
			if (forceOuterLaNosceaGround)
			{
				HuntMovementContext context = await RunOnFrameworkThreadAsync((Func<HuntMovementContext>)GetHuntMovementContextUnsafe);
				string value = (markScopedMineGround ? "mark-scoped mine policy" : "coordinate fallback");
				log.Information($"[HuntLogs] Outer La Noscea mine destination requires mounted ground routing: source={value}, destination={target}, territory={expectedTerritoryId}. " + "Flight will not be requested.");
				if (!(await EnsureMountedForMovementAsync(target, requireMounted: true, preparationToken)))
				{
					log.Warning("[HuntLogs] Outer La Noscea mine ground route could not verify mounted state; continuing without flight.");
				}
				return new HuntMovementPreparation(context, UseFlight: false);
			}
			return await PrepareHuntMovementAsync(target, preparationToken);
		}
	}

	private HuntMovementPolicy CreateHuntMovementPolicy(HuntMark mark, string markName)
	{
		int num;
		if (mark.TerritoryId == 180)
		{
			num = (mark.Positions.Any((Vector3 position) => IsOuterLaNosceaMinePosition(mark.TerritoryId, position)) ? 1 : 0);
			if (num != 0)
			{
				log.Information("[HuntLogs] Mark-scoped Outer La Noscea mine ground policy enabled for " + markName + "; stored positions, loaded targets, and matching FATE centers will remain mounted-ground routed until this mark is complete.");
			}
		}
		else
		{
			num = 0;
		}
		return new HuntMovementPolicy((byte)num != 0);
	}

	private static bool IsOuterLaNosceaMinePosition(uint territoryId, Vector3 position)
	{
		if (territoryId == 180 && position.X >= 0f)
		{
			return position.Z <= -450f;
		}
		return false;
	}

	private static bool ShouldAttemptGroundFallbackAfterFlight(HuntMovementOutcome outcome)
	{
		if (outcome.Result == HuntMovementResult.StartRejected)
		{
			return !outcome.StartAccepted;
		}
		bool flag = outcome.StartAccepted;
		if (flag)
		{
			HuntMovementResult result = outcome.Result;
			bool flag2 = (uint)(result - 2) <= 2u;
			flag = flag2;
		}
		return flag;
	}

	private async Task RefreshHuntPursuitAfterMovementFailureAsync(uint expectedTerritoryId, string? diagnosticMarkName, HuntMovementOutcome outcome, Func<Task<bool>>? loadedValidTargetExistsAsync, CancellationToken token, bool ensureNavigationIdle = true)
	{
		if (ensureNavigationIdle && outcome.StartAccepted && (vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding()))
		{
			await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "returning to hunt pursuit after movement failure", token, diagnosticMarkName, null, loadedValidTargetExistsAsync);
		}
		bool flag = loadedValidTargetExistsAsync != null;
		if (flag)
		{
			flag = await loadedValidTargetExistsAsync();
		}
		bool value = flag;
		string value2 = (string.IsNullOrWhiteSpace(diagnosticMarkName) ? "hunt target" : diagnosticMarkName);
		log.Information($"[HuntLogs] Returning to pursuit for {value2}: movementResult={outcome.Result}, startAccepted={outcome.StartAccepted}, loadedValidTarget={value}; " + "the next scan will refresh hunt-log counts and loaded targets.");
	}

	private async Task<HuntMovementPreparation> PrepareHuntMovementAsync(Vector3 destination, CancellationToken token)
	{
		HuntMovementContext context = await RunOnFrameworkThreadAsync((Func<HuntMovementContext>)GetHuntMovementContextUnsafe);
		log.Information($"[HuntLogs] Hunt move: territory={context.TerritoryId} \"{context.TerritoryName}\", mountAllowed={context.MountAllowed}, aetherCurrentRowId={FormatAetherCurrentRowId(context.AetherCurrentRowId)}, flightDecision={context.FlightDecision}, reason={context.Reason}, destination={destination}");
		if (context.FlightDecision == HuntMovementFlightDecision.Unlocked)
		{
			if (await EnsureMountedForMovementAsync(destination, requireMounted: true, token))
			{
				return new HuntMovementPreparation(context, UseFlight: true);
			}
			log.Warning("[HuntLogs] Flight is unlocked in this territory, but mounted state could not be verified; using ground movement.");
			return new HuntMovementPreparation(context, UseFlight: false);
		}
		if (context.FlightDecision == HuntMovementFlightDecision.UnknownButMountable)
		{
			if (await EnsureMountedForMovementAsync(destination, requireMounted: true, token))
			{
				log.Information("[HuntLogs] Flight availability is unknown but mountable; probing vnavmesh flight once.");
				return new HuntMovementPreparation(context, UseFlight: true);
			}
			log.Warning("[HuntLogs] Flight availability is unknown, but mounted state could not be verified; using ground movement.");
			return new HuntMovementPreparation(context, UseFlight: false);
		}
		await EnsureMountedForMovementAsync(destination, requireMounted: false, token);
		return new HuntMovementPreparation(context, UseFlight: false);
	}

	private async Task<bool> EnsureMountedForMovementAsync(Vector3 destination, bool requireMounted, CancellationToken token)
	{
		MountDecision mountDecision = await RunOnFrameworkThreadAsync(() => GetMountDecisionUnsafe(destination, requireMounted));
		if (mountDecision.AlreadyMounted)
		{
			return true;
		}
		if (mountDecision.Mounting)
		{
			log.Debug("[HuntLogs] Mount action already in progress; waiting for Mounted.");
			return await WaitForMountedAsync(token);
		}
		if (!mountDecision.ShouldMount)
		{
			if (requireMounted && !string.IsNullOrEmpty(mountDecision.Reason))
			{
				log.Warning("[HuntLogs] Cannot mount for flight movement: " + mountDecision.Reason);
			}
			return false;
		}
		MountActionResult mountActionResult = await SendMountActionAsync();
		log.Information($"[HuntLogs] Mount action: chosen={mountActionResult.Action}, accepted={mountActionResult.Accepted}, detail={mountActionResult.Detail}");
		if (!mountActionResult.Accepted)
		{
			return false;
		}
		bool flag = await WaitForMountedAsync(token);
		if (!flag && requireMounted)
		{
			log.Warning("[HuntLogs] Mount command did not produce a verified mounted state before timeout.");
		}
		log.Information($"[HuntLogs] Mount verification: mounted={flag}");
		return flag;
	}

	private MountDecision GetMountDecisionUnsafe(Vector3 destination, bool requireMounted)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (!clientState.IsLoggedIn)
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, "client is not logged in");
		}
		if (localPlayer == null)
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, "local player is unavailable");
		}
		if (condition[ConditionFlag.Mounted])
		{
			return new MountDecision(AlreadyMounted: true, Mounting: false, ShouldMount: false, null);
		}
		if (condition[ConditionFlag.Mounting71])
		{
			return new MountDecision(AlreadyMounted: false, Mounting: true, ShouldMount: false, null);
		}
		if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, "player is changing areas or logging out");
		}
		if (condition[ConditionFlag.Unconscious])
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, "player is unconscious");
		}
		if (condition[ConditionFlag.InCombat])
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, "player is in combat");
		}
		if (condition[ConditionFlag.Casting])
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, "player is casting");
		}
		if (!IsMountingAllowedInCurrentTerritoryUnsafe())
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, $"mounting is not allowed in territory {clientState.TerritoryType}");
		}
		if (!requireMounted)
		{
			if (!configuration.HuntLogs.UseMountBetweenMarks)
			{
				return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, null);
			}
			if (Vector3.Distance(localPlayer.Position, destination) < Math.Clamp(configuration.HuntLogs.MountDistance, 10f, 200f))
			{
				return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, null);
			}
		}
		(bool, string) generalMountActionAvailabilityUnsafe = GetGeneralMountActionAvailabilityUnsafe();
		if (!generalMountActionAvailabilityUnsafe.Item1)
		{
			return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: false, generalMountActionAvailabilityUnsafe.Item2);
		}
		return new MountDecision(AlreadyMounted: false, Mounting: false, ShouldMount: true, null);
	}

	private async Task<MountActionResult> SendMountActionAsync()
	{
		return await RunOnFrameworkThreadAsync(delegate
		{
			(bool, string) generalMountActionAvailabilityUnsafe = GetGeneralMountActionAvailabilityUnsafe();
			if (!generalMountActionAvailabilityUnsafe.Item1)
			{
				return new MountActionResult("mountCommand", Accepted: false, generalMountActionAvailabilityUnsafe.Item2);
			}
			string selectedMount = configuration.HuntLogs.SelectedMount;
			string configuredMountCommand = GetConfiguredMountCommand(selectedMount);
			bool flag = SendGameCommandUnsafe(configuredMountCommand);
			return new MountActionResult(string.Equals(selectedMount, "Mount Roulette", StringComparison.OrdinalIgnoreCase) ? "mountRouletteCommand" : "mountNameCommand", flag, flag ? ("sent " + configuredMountCommand) : ("failed to send " + configuredMountCommand));
		});
	}

	private static string GetConfiguredMountCommand(string selectedMount)
	{
		if (string.Equals(selectedMount, "Mount Roulette", StringComparison.OrdinalIgnoreCase))
		{
			return "/generalaction \"Mount Roulette\"";
		}
		if (string.IsNullOrWhiteSpace(selectedMount))
		{
			return "/mount \"Company Chocobo\"";
		}
		return "/mount \"" + selectedMount + "\"";
	}

	private async Task<bool> WaitForMountedAsync(CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < MountVerificationTimeout)
		{
			token.ThrowIfCancellationRequested();
			(bool, bool, string) tuple = await RunOnFrameworkThreadAsync(delegate
			{
				if (condition[ConditionFlag.Mounted])
				{
					return ((bool Mounted, bool CanContinue, string Reason))(Mounted: true, CanContinue: true, Reason: null);
				}
				if (condition[ConditionFlag.Mounting71])
				{
					return ((bool Mounted, bool CanContinue, string Reason))(Mounted: false, CanContinue: true, Reason: null);
				}
				if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
				{
					return (Mounted: false, CanContinue: false, Reason: "player started changing areas or logging out");
				}
				if (condition[ConditionFlag.Unconscious])
				{
					return (Mounted: false, CanContinue: false, Reason: "player became unconscious");
				}
				if (condition[ConditionFlag.InCombat])
				{
					return (Mounted: false, CanContinue: false, Reason: "player entered combat");
				}
				return (!IsMountingAllowedInCurrentTerritoryUnsafe()) ? (Mounted: false, CanContinue: false, Reason: $"mounting is no longer allowed in territory {clientState.TerritoryType}") : (Mounted: false, CanContinue: true, Reason: null);
			});
			if (tuple.Item1)
			{
				return true;
			}
			if (!tuple.Item2)
			{
				log.Warning("[HuntLogs] Mount verification stopped: " + tuple.Item3);
				return false;
			}
			await Task.Delay(MountPollDelay, token);
		}
		return await RunOnFrameworkThreadAsync(() => condition[ConditionFlag.Mounted]);
	}

	private bool IsMountingAllowedInCurrentTerritoryUnsafe()
	{
		try
		{
			TerritoryType row;
			return dataManager.GetExcelSheet<TerritoryType>().TryGetRow(clientState.TerritoryType, out row) && row.Mount;
		}
		catch (Exception ex)
		{
			log.Warning($"[HuntLogs] Failed to check mount permission for territory {clientState.TerritoryType}: {ex.Message}");
			return false;
		}
	}

	private unsafe (bool Available, string Reason) GetGeneralMountActionAvailabilityUnsafe()
	{
		try
		{
			ActionManager* ptr = ActionManager.Instance();
			if (ptr == null)
			{
				return (Available: false, Reason: "ActionManager unavailable");
			}
			uint actionStatus = ptr->GetActionStatus(ActionType.GeneralAction, 9u, 3758096384uL, checkRecastActive: true, checkCastingActive: true, null);
			return (actionStatus == 0) ? (Available: true, Reason: "general mount action available") : (Available: false, Reason: $"general mount action unavailable (status={actionStatus})");
		}
		catch (Exception ex)
		{
			return (Available: false, Reason: "general mount action check failed: " + ex.Message);
		}
	}

	private unsafe bool SendGameCommandUnsafe(string command)
	{
		try
		{
			if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
			{
				return false;
			}
			UIModule* ptr = UIModule.Instance();
			if (ptr == null)
			{
				log.Error("[HuntLogs] UIModule is null; cannot send command " + command);
				return false;
			}
			Utf8String* ptr2 = Utf8String.FromSequence(Encoding.UTF8.GetBytes(command));
			if (ptr2 == null)
			{
				log.Error("[HuntLogs] Failed to allocate Utf8String for command " + command);
				return false;
			}
			try
			{
				ptr->ProcessChatBoxEntry(ptr2, IntPtr.Zero);
				return true;
			}
			finally
			{
				ptr2->Dtor(free: true);
			}
		}
		catch (Exception ex)
		{
			log.Error("[HuntLogs] Game command failed [" + command + "]: " + ex.Message);
			return false;
		}
	}

	private static string FormatAetherCurrentRowId(uint rowId)
	{
		if (rowId != 0)
		{
			return rowId.ToString();
		}
		return "none";
	}

	private unsafe HuntMovementContext GetHuntMovementContextUnsafe()
	{
		uint territoryType = clientState.TerritoryType;
		string text = $"Territory {territoryType}";
		bool flag = false;
		uint num = 0u;
		try
		{
			if (!dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryType, out var row))
			{
				return new HuntMovementContext(territoryType, text, flag, num, HuntMovementFlightDecision.Locked, "territory row unavailable");
			}
			text = row.PlaceName.ValueNullable?.Name.ExtractText() ?? text;
			flag = row.Mount;
			if (!flag)
			{
				return new HuntMovementContext(territoryType, text, flag, num, HuntMovementFlightDecision.Locked, "mounting is not allowed");
			}
			RowRef<AetherCurrentCompFlgSet> aetherCurrentCompFlgSet = row.AetherCurrentCompFlgSet;
			num = (aetherCurrentCompFlgSet.IsValid ? aetherCurrentCompFlgSet.RowId : 0u);
			if (!aetherCurrentCompFlgSet.IsValid || num == 0)
			{
				return new HuntMovementContext(territoryType, text, flag, num, HuntMovementFlightDecision.UnknownButMountable, "no standard aether-current set");
			}
			PlayerState* ptr = PlayerState.Instance();
			if (ptr == null || !ptr->IsLoaded)
			{
				return new HuntMovementContext(territoryType, text, flag, num, HuntMovementFlightDecision.UnknownButMountable, "player state unavailable");
			}
			bool flag2 = ptr->IsAetherCurrentZoneComplete(num);
			return new HuntMovementContext(territoryType, text, flag, num, (!flag2) ? HuntMovementFlightDecision.Locked : HuntMovementFlightDecision.Unlocked, flag2 ? "aether-current set complete" : "aether-current set incomplete");
		}
		catch (Exception ex)
		{
			log.Warning($"[HuntLogs] Failed to check flight unlock for territory {territoryType}: {ex.Message}");
			return new HuntMovementContext(territoryType, text, flag, num, (!flag) ? HuntMovementFlightDecision.Locked : HuntMovementFlightDecision.UnknownButMountable, "flight check failed");
		}
	}

	private bool CanStartFlightMovementUnsafe()
	{
		return condition[ConditionFlag.Mounted];
	}

	private async Task<bool> DismountForCombatAsync(CancellationToken token, uint landingTerritoryId = 0u, Vector3? landingReference = null, string? landingContext = null)
	{
		if ((vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding()) && !(await StopNavigationAndWaitForIdleAsync(await RunOnFrameworkThreadAsync(() => clientState.TerritoryType), "dismount handoff before combat", token, null, "combat target")))
		{
			return false;
		}
		(bool, bool, bool) tuple = await GetDismountStateAsync();
		if (IsDismountedForCombat(tuple))
		{
			return true;
		}
		if (tuple.Item3 && landingTerritoryId != 0 && landingReference.HasValue)
		{
			if (!(await MoveToLandableSpotBeforeCombatAsync(landingTerritoryId, landingReference.Value, landingContext ?? "hunt target", token)))
			{
				return false;
			}
			tuple = await GetDismountStateAsync();
			if (IsDismountedForCombat(tuple))
			{
				return true;
			}
		}
		log.Information($"[HuntLogs] Dismount required before combat: mounted={tuple.Item1}, mounting71={tuple.Item2}, inFlight={tuple.Item3}");
		if (tuple.Item3)
		{
			for (int attempt = 1; attempt <= 3; attempt++)
			{
				if (!tuple.Item3)
				{
					break;
				}
				token.ThrowIfCancellationRequested();
				bool accepted = await UseDismountGeneralActionAsync();
				await WaitUntilFrameworkAsync(() => !condition[ConditionFlag.InFlight], "flight to end before dismounting", DismountTransitionTimeout, token);
				tuple = await GetDismountStateAsync();
				log.Information($"[HuntLogs] Landing attempt {attempt}/3: accepted={accepted}, mounted={tuple.Item1}, mounting71={tuple.Item2}, inFlight={tuple.Item3}");
			}
			if (tuple.Item3)
			{
				log.Warning($"[HuntLogs] Landing failed after retries: mounted={tuple.Item1}, mounting71={tuple.Item2}, inFlight={tuple.Item3}");
				return false;
			}
		}
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			if (!tuple.Item1 && !tuple.Item2)
			{
				break;
			}
			token.ThrowIfCancellationRequested();
			bool accepted = await UseDismountGeneralActionAsync();
			await WaitUntilFrameworkAsync(() => !condition[ConditionFlag.Mounted] && !condition[ConditionFlag.Mounting71], "mounted state to end before combat", DismountTransitionTimeout, token);
			tuple = await GetDismountStateAsync();
			log.Information($"[HuntLogs] Dismount attempt {attempt}/3: accepted={accepted}, mounted={tuple.Item1}, mounting71={tuple.Item2}, inFlight={tuple.Item3}");
		}
		if (IsDismountedForCombat(tuple))
		{
			return true;
		}
		log.Warning($"[HuntLogs] Dismount failed after retries: mounted={tuple.Item1}, mounting71={tuple.Item2}, inFlight={tuple.Item3}");
		return false;
	}

	private async Task<bool> MoveToLandableSpotBeforeCombatAsync(uint expectedTerritoryId, Vector3 landingReference, string context, CancellationToken token)
	{
		await AssertTerritoryAsync(expectedTerritoryId, "finding a landable combat position near " + context, token);
		if (!vnavmeshIpc.IsReady())
		{
			log.Warning("[HuntLogs] Refusing an airborne combat handoff for " + context + ": vnavmesh is not ready to resolve a landable floor point.");
			return false;
		}
		Vector3 vector = landingReference;
		vector.Y = landingReference.Y + 5f;
		Vector3 position = vector;
		Vector3? landableSpot = vnavmeshIpc.FindPointOnFloor(position, allowUnlandable: false, 15f);
		if (!landableSpot.HasValue)
		{
			log.Warning($"[HuntLogs] Refusing an airborne combat handoff for {context}: no landable vnavmesh floor point was found within {15f:F0} yalms of {landingReference}.");
			return false;
		}
		float num = Vector3.Distance(await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position ?? landingReference), landableSpot.Value);
		log.Information($"[HuntLogs] Airborne combat handoff for {context}: resolved landable point {landableSpot.Value} ({num:F1} yalms from player, reference={landingReference}).");
		if (num > 1.5f)
		{
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Landing near " + context;
			});
			HuntMovementOutcome huntMovementOutcome = await TryMoveToAsync(landableSpot.Value, expectedTerritoryId, fly: true, 1.5f, useCloseTo: true, token, context, $"landable floor point {landableSpot.Value}");
			if (huntMovementOutcome.Result != HuntMovementResult.Completed)
			{
				log.Warning($"[HuntLogs] Could not reach the landable combat point for {context}: result={huntMovementOutcome.Result}, startAccepted={huntMovementOutcome.StartAccepted}.");
				return false;
			}
		}
		bool flag = vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding();
		if (flag)
		{
			flag = !(await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "landable combat handoff for " + context, token, context, $"landable floor point {landableSpot.Value}"));
		}
		if (flag)
		{
			return false;
		}
		return true;
	}

	private unsafe async Task<bool> UseDismountGeneralActionAsync()
	{
		return await RunOnFrameworkThreadAsync(delegate
		{
			ActionManager* ptr = ActionManager.Instance();
			return ptr != null && ptr->UseAction(ActionType.GeneralAction, 23u, 3758096384uL, 0u, ActionManager.UseActionMode.None, 0u, null);
		});
	}

	private async Task<(bool Mounted, bool Mounting, bool InFlight)> GetDismountStateAsync()
	{
		return await RunOnFrameworkThreadAsync(() => (condition[ConditionFlag.Mounted], condition[ConditionFlag.Mounting71], condition[ConditionFlag.InFlight]));
	}

	private static bool IsDismountedForCombat((bool Mounted, bool Mounting, bool InFlight) state)
	{
		if (!state.Mounted && !state.Mounting)
		{
			return !state.InFlight;
		}
		return false;
	}

	private async Task MaintainCompanionAsync(CancellationToken token)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!configuration.HuntLogs.SummonChocobo || utcNow < companionSummonNotBeforeUtc || vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding())
		{
			return;
		}
		companionSummonNotBeforeUtc = utcNow + CompanionUpkeepInterval;
		try
		{
			CompanionSummonAttempt companionSummonAttempt = await RunOnFrameworkThreadAsync((Func<CompanionSummonAttempt>)TrySummonCompanionUnsafe);
			if (!string.IsNullOrEmpty(companionSummonAttempt.Diagnostic))
			{
				companionSummonNotBeforeUtc = DateTime.UtcNow + CompanionFailedAttemptThrottle;
				LogCompanionDiagnostic(companionSummonAttempt.Diagnostic);
			}
			else if (companionSummonAttempt.Accepted)
			{
				companionSummonNotBeforeUtc = DateTime.UtcNow + CompanionSuccessCooldown;
				log.Information($"[HuntLogs] Companion summon accepted: timer={companionSummonAttempt.TimeLeft:F0}s, greens={companionSummonAttempt.GreensCount}");
				await Task.Delay(CompanionStanceDelay, token);
				string stanceCommand = GetCompanionStanceCommand(configuration.HuntLogs.CompanionStance);
				if (await RunOnFrameworkThreadAsync(() => SendGameCommandUnsafe(stanceCommand)))
				{
					log.Information("[HuntLogs] Companion stance sent: " + stanceCommand);
				}
				else
				{
					LogCompanionDiagnostic("Companion was summoned, but the stance command could not be sent: " + stanceCommand);
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			companionSummonNotBeforeUtc = DateTime.UtcNow + CompanionFailedAttemptThrottle;
			LogCompanionDiagnostic("Companion summon check failed: " + ex2.Message);
		}
	}

	private unsafe CompanionSummonAttempt TrySummonCompanionUnsafe()
	{
		if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
		{
			return new CompanionSummonAttempt(Accepted: false, 0f, 0, null);
		}
		if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.Casting] || objectTable.LocalPlayer.IsCasting || condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.InFlight] || condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95] || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut] || condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.Occupied33] || condition[ConditionFlag.Occupied39])
		{
			return new CompanionSummonAttempt(Accepted: false, 0f, 0, null);
		}
		UIState* ptr = UIState.Instance();
		if (ptr == null)
		{
			return new CompanionSummonAttempt(Accepted: false, 0f, 0, "Companion state is unavailable.");
		}
		float timeLeft = ptr->Buddy.CompanionInfo.TimeLeft;
		if (timeLeft > 900f)
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, 0, null);
		}
		if (IsInSanctuaryUnsafe())
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, 0, null);
		}
		InventoryManager* ptr2 = InventoryManager.Instance();
		if (ptr2 == null)
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, 0, "Inventory is unavailable while checking Gysahl Greens.");
		}
		int gysahlGreensCountUnsafe = GetGysahlGreensCountUnsafe(ptr2);
		if (gysahlGreensCountUnsafe <= 0)
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, gysahlGreensCountUnsafe, "No Gysahl Greens are available; continuing hunt automation without a companion.");
		}
		ActionManager* ptr3 = ActionManager.Instance();
		if (ptr3 == null)
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, gysahlGreensCountUnsafe, "ActionManager is unavailable for companion summoning.");
		}
		uint actionStatus = ptr3->GetActionStatus(ActionType.Item, 4868u, 3758096384uL, checkRecastActive: true, checkCastingActive: true, null);
		if (actionStatus != 0)
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, gysahlGreensCountUnsafe, $"Gysahl Greens action is unavailable (status={actionStatus}).");
		}
		if (!ptr3->UseAction(ActionType.Item, 4868u, 3758096384uL, 65535u, ActionManager.UseActionMode.None, 0u, null))
		{
			return new CompanionSummonAttempt(Accepted: false, timeLeft, gysahlGreensCountUnsafe, "Gysahl Greens action was ready, but the summon request was rejected.");
		}
		return new CompanionSummonAttempt(Accepted: true, timeLeft, gysahlGreensCountUnsafe, null);
	}

	private unsafe static int GetGysahlGreensCountUnsafe(InventoryManager* inventoryManager)
	{
		return inventoryManager->GetInventoryItemCount(4868u, isHq: false, checkEquipped: true, checkArmory: true, 0) + inventoryManager->GetInventoryItemCount(4868u, isHq: true, checkEquipped: true, checkArmory: true, 0);
	}

	private bool IsInSanctuaryUnsafe()
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return true;
		}
		foreach (IGameObject item in objectTable)
		{
			if (item.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte && Vector3.DistanceSquared(localPlayer.Position, item.Position) <= 2500f)
			{
				return true;
			}
		}
		return false;
	}

	private static string GetCompanionStanceCommand(string? configuredStance)
	{
		return configuredStance switch
		{
			"Defender Stance" => "/cac \"Defender Stance\"", 
			"Attacker Stance" => "/cac \"Attacker Stance\"", 
			"Healer Stance" => "/cac \"Healer Stance\"", 
			"Follow" => "/cac \"Follow\"", 
			_ => "/cac \"Free Stance\"", 
		};
	}

	private void LogCompanionDiagnostic(string message)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!(utcNow < companionDiagnosticNotBeforeUtc))
		{
			companionDiagnosticNotBeforeUtc = utcNow + CompanionDiagnosticThrottle;
			log.Warning("[HuntLogs] " + message);
		}
	}

	private async Task<bool> StopNavigationForPursuitAsync(uint expectedTerritoryId, string markName, string reason, CancellationToken token)
	{
		bool flag = vnavmeshIpc.IsPathRunning();
		bool flag2 = vnavmeshIpc.IsPathfinding();
		if (!flag && !flag2)
		{
			return true;
		}
		return await StopNavigationAndWaitForIdleAsync(expectedTerritoryId, "loaded-target handoff for " + markName + ": " + reason, token, markName, reason);
	}

	private async Task<IBattleNpc?> FindNearestMarkNpcAsync(HuntMark mark, string markName)
	{
		HuntTargetSearchResult huntTargetSearchResult = await RunOnFrameworkThreadAsync((Func<HuntTargetSearchResult>)delegate
		{
			Vector3 playerPosition = objectTable.LocalPlayer?.Position ?? Vector3.Zero;
			(bool, ushort) currentJoinedFateUnsafe = GetCurrentJoinedFateUnsafe();
			List<IBattleNpc> list = (from x in objectTable.OfType<IBattleNpc>()
				where IsMatchingMarkIdentityUnsafe(mark, markName, x)
				select x).ToList();
			IBattleNpc target = null;
			float num = float.MaxValue;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			foreach (IBattleNpc item in list)
			{
				if (!item.IsTargetable)
				{
					num2++;
				}
				else if (item.IsDead)
				{
					num3++;
				}
				else if (item.CurrentHp == 0)
				{
					num4++;
				}
				else
				{
					float num5 = Vector3.DistanceSquared(item.Position, playerPosition);
					if (!(num5 >= num))
					{
						target = item;
						num = num5;
					}
				}
			}
			return new HuntTargetSearchResult(target, list.Count, num2, num3, num4, string.Join(", ", from x in (from x in objectTable.OfType<IBattleNpc>()
					where x.IsTargetable && !x.IsDead && x.CurrentHp != 0
					select (Npc: x, Distance: Vector3.Distance(x.Position, playerPosition)) into x
					where x.Distance <= 80f
					orderby x.Distance
					select x).Take(8)
				select FormatNpc(x.Npc)), (targetManager.Target is IBattleNpc npc) ? FormatNpc(npc) : (targetManager.Target?.Name.ToString() ?? "none"), condition[ConditionFlag.InCombat], condition[ConditionFlag.Casting], currentJoinedFateUnsafe.Item1, currentJoinedFateUnsafe.Item2, clientState.TerritoryType);
			string FormatNpc(IBattleNpc battleNpc)
			{
				float value4 = Vector3.Distance(battleNpc.Position, playerPosition);
				return $"{battleNpc.Name} (NameId={battleNpc.NameId}, BaseId={battleNpc.BaseId}, RuntimeFateId={GetGameObjectFateIdUnsafe(battleNpc)}, {value4:F1}y)";
			}
		});
		DateTime utcNow = DateTime.UtcNow;
		if (huntTargetSearchResult.Target == null && utcNow >= huntTargetDiagnosticNotBeforeUtc)
		{
			huntTargetDiagnosticNotBeforeUtc = utcNow + HuntTargetDiagnosticThrottle;
			bool value = vnavmeshIpc.IsReady();
			bool value2 = vnavmeshIpc.IsPathRunning();
			bool value3 = vnavmeshIpc.IsPathfinding();
			log.Information($"[HuntLogs] No loaded eligible target for {markName}: matching={huntTargetSearchResult.MatchingCount}, notTargetable={huntTargetSearchResult.NotTargetableCount}, dead={huntTargetSearchResult.DeadCount}, noHp={huntTargetSearchResult.NoHpCount}, nearbyTargetable=[{(string.IsNullOrWhiteSpace(huntTargetSearchResult.NearbyTargetableNpcs) ? "none" : huntTargetSearchResult.NearbyTargetableNpcs)}], currentTarget={huntTargetSearchResult.CurrentTarget}, inCombat={huntTargetSearchResult.InCombat}, casting={huntTargetSearchResult.Casting}, joinedFate={huntTargetSearchResult.InFate}/{huntTargetSearchResult.FateId}, markFateId={mark.FateId}, navReady={value}, pathRunning={value2}, pathfinding={value3}, territory={database.GetTerritoryName(huntTargetSearchResult.TerritoryId)} ({huntTargetSearchResult.TerritoryId}).");
		}
		return huntTargetSearchResult.Target;
	}

	private unsafe static ushort GetGameObjectFateIdUnsafe(IGameObject gameObject)
	{
		GameObject* address = (GameObject*)gameObject.Address;
		if (address != null)
		{
			return address->FateId;
		}
		return 0;
	}

	private static bool IsMatchingMarkIdentityUnsafe(HuntMark mark, string markName, IBattleNpc target)
	{
		if (target.NameId != mark.BNpcNameRowId && !target.Name.ToString().Equals(markName, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ushort gameObjectFateIdUnsafe = GetGameObjectFateIdUnsafe(target);
		if (mark.FateId != 0)
		{
			return gameObjectFateIdUnsafe == mark.FateId;
		}
		return gameObjectFateIdUnsafe == 0;
	}

	private async Task<HuntCombatTarget?> ReacquireHuntCombatTargetAsync(HuntMark mark, string markName, ulong? expectedGameObjectId = null)
	{
		if (!expectedGameObjectId.HasValue)
		{
			IBattleNpc nearest = await FindNearestMarkNpcAsync(mark, markName);
			if (nearest == null)
			{
				return null;
			}
			expectedGameObjectId = await RunOnFrameworkThreadAsync(() => nearest.GameObjectId);
		}
		return await RunOnFrameworkThreadAsync(delegate
		{
			IBattleNpc battleNpc = objectTable.OfType<IBattleNpc>().FirstOrDefault((IBattleNpc x) => x.GameObjectId == expectedGameObjectId.Value && IsMatchingMarkIdentityUnsafe(mark, markName, x) && x.IsTargetable && !x.IsDead && x.CurrentHp != 0);
			return (battleNpc != null) ? new HuntCombatTarget(battleNpc, battleNpc.GameObjectId, GetGameObjectFateIdUnsafe(battleNpc)) : null;
		});
	}

	private async Task<bool> ValidateRuntimeFateForCombatAsync(string markName, uint runtimeFateId)
	{
		MatchingFateState matchingFateState = await RunOnFrameworkThreadAsync(() => GetMatchingFateStateUnsafe(runtimeFateId));
		if (!matchingFateState.Active)
		{
			if (lastSyncedFateId == runtimeFateId)
			{
				lastSyncedFateId = 0;
			}
			log.Information($"[HuntLogs] Refusing combat for {markName}: runtime FATE {runtimeFateId} is no longer active.");
			return false;
		}
		if (!matchingFateState.Joined || matchingFateState.JoinedFateId != runtimeFateId)
		{
			log.Information($"[HuntLogs] Refusing combat for {markName}: runtime FATE {runtimeFateId} is active, but joined FATE is {matchingFateState.JoinedFateId}.");
			return false;
		}
		if (matchingFateState.RequiresLevelSync && !matchingFateState.IsLevelSynced)
		{
			log.Information($"[HuntLogs] Refusing combat for {markName}: runtime FATE {runtimeFateId} requires level sync, " + "but PlayerState has not confirmed it.");
			return false;
		}
		return true;
	}

	private async Task<bool> RevalidateHuntCombatTargetAsync(HuntMark mark, string markName, HuntCombatTarget expectedTarget, uint runtimeFateId)
	{
		HuntCombatTarget huntCombatTarget = await ReacquireHuntCombatTargetAsync(mark, markName, expectedTarget.GameObjectId);
		if (huntCombatTarget == null)
		{
			log.Information("[HuntLogs] " + markName + " despawned before combat activation; returning to pursuit without enabling combat.");
			return false;
		}
		if (huntCombatTarget.RuntimeFateId != runtimeFateId)
		{
			log.Information($"[HuntLogs] {markName} changed FATE identity before combat activation ({runtimeFateId} -> {huntCombatTarget.RuntimeFateId}); returning to pursuit without enabling combat.");
			return false;
		}
		return await ValidateRuntimeFateForCombatAsync(markName, runtimeFateId);
	}

	private async Task<bool> HasLoadedValidMarkTargetAsync(HuntMark mark, string markName)
	{
		return await FindNearestMarkNpcAsync(mark, markName) != null;
	}

	private async Task<bool> TryMoveToLoadedMarkTargetAsync(HuntMark mark, string markName, CancellationToken token)
	{
		IBattleNpc battleNpc = await FindNearestMarkNpcAsync(mark, markName);
		if (battleNpc == null)
		{
			return false;
		}
		log.Information("[HuntLogs] Found loaded " + markName + " away from stored spawn; moving directly to target.");
		if (await TryMoveToObjectAsync(battleNpc, mark.TerritoryId, 4f, token, markName, () => HasLoadedValidMarkTargetAsync(mark, markName)))
		{
			return true;
		}
		log.Warning("[HuntLogs] Could not path directly to loaded " + markName + "; continuing the scan.");
		return false;
	}

	private async Task<bool> WaitForMatchingFateTargetAsync(HuntMark mark, string markName, int expectedLogRank, CancellationToken token)
	{
		if (mark.FateId == 0)
		{
			return false;
		}
		bool waitingLogged = false;
		while (true)
		{
			token.ThrowIfCancellationRequested();
			if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
			{
				return false;
			}
			if (await TryHandleDeathRecoveryAsync(mark.TerritoryId, "waiting for matching FATE target " + markName, token))
			{
				continue;
			}
			MatchingFateState matchingFateState = await RunOnFrameworkThreadAsync(() => GetMatchingFateStateUnsafe(mark.FateId));
			if (!matchingFateState.Active || !matchingFateState.Joined || matchingFateState.JoinedFateId != mark.FateId)
			{
				if (lastSyncedFateId == mark.FateId)
				{
					lastSyncedFateId = 0;
				}
				if (waitingLogged)
				{
					log.Information($"[HuntLogs] Matching FATE {mark.FateId} ended or changed while waiting for {markName}; resuming hunt scheduling.");
				}
				return false;
			}
			await MaintainCompanionAsync(token);
			if (await FindNearestMarkNpcAsync(mark, markName) != null)
			{
				break;
			}
			if (!(await ResolveCombatIfNeededAsync("waiting for matching FATE target " + markName, mark.TerritoryId, token)))
			{
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = "Waiting unsynced for " + markName;
				});
				if (!waitingLogged)
				{
					log.Information($"[HuntLogs] Joined matching FATE {mark.FateId}; remaining unsynced while waiting for the concrete runtime-FATE target {markName}. " + "This local wait has no timeout and does not consume a no-progress scan.");
					waitingLogged = true;
				}
				await Task.Delay(MatchingFatePollDelay, token);
			}
		}
		log.Information("[HuntLogs] Required FATE target " + markName + " is now available.");
		return true;
	}

	private async Task<bool> PrepareMatchingFateForCombatAsync(HuntMark mark, string markName, int expectedLogRank, HuntMovementPolicy movementPolicy, CancellationToken token)
	{
		if (mark.FateId == 0)
		{
			return true;
		}
		MatchingFateState fateState = await RunOnFrameworkThreadAsync(() => GetMatchingFateStateUnsafe(mark.FateId));
		if (!fateState.Active)
		{
			lastSyncedFateId = 0;
			return false;
		}
		if (!fateState.Joined || fateState.JoinedFateId != mark.FateId)
		{
			if (!(await IsMarkWorkCurrentAsync(mark, expectedLogRank)))
			{
				return false;
			}
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Approaching matching FATE for " + markName;
			});
			Vector3 vector = await ProjectHuntPositionAsync(mark.TerritoryId, fateState.Position, token);
			if (!(await TryMoveToHuntLocationAsync(vector, mark.TerritoryId, 7f, useCloseTo: true, token, markName, $"matching FATE {mark.FateId} center {vector}", null, movementPolicy)))
			{
				return false;
			}
		}
		if (!(await DismountForCombatAsync(token, mark.TerritoryId, fateState.Position, markName + " FATE")))
		{
			return false;
		}
		fateState = await RunOnFrameworkThreadAsync(() => GetMatchingFateStateUnsafe(mark.FateId));
		if (!fateState.Active || !fateState.Joined || fateState.JoinedFateId != mark.FateId)
		{
			log.Information($"[HuntLogs] Reached FATE {mark.FateId} for {markName}, but matching membership was not confirmed.");
			return false;
		}
		return true;
	}

	private async Task<bool> EnsureFateSyncForTargetAsync(string markName, uint runtimeFateId, CancellationToken token)
	{
		while (true)
		{
			token.ThrowIfCancellationRequested();
			MatchingFateState matchingFateState = await RunOnFrameworkThreadAsync(() => GetMatchingFateStateUnsafe(runtimeFateId));
			if (!matchingFateState.Active || !matchingFateState.Joined || matchingFateState.JoinedFateId != runtimeFateId)
			{
				lastSyncedFateId = 0;
				return false;
			}
			if (!matchingFateState.RequiresLevelSync || matchingFateState.IsLevelSynced)
			{
				if (matchingFateState.IsLevelSynced)
				{
					lastSyncedFateId = (ushort)runtimeFateId;
				}
				return true;
			}
			if (!configuration.HuntLogs.AutoSyncFateTargets)
			{
				break;
			}
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Waiting for confirmed level sync for " + markName;
			});
			DateTime now = DateTime.UtcNow;
			if (now >= fateSyncRequestNotBeforeUtc)
			{
				bool num = await RunOnFrameworkThreadAsync(() => !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && !condition[ConditionFlag.Mounted] && !condition[ConditionFlag.Mounting71] && !condition[ConditionFlag.InFlight] && SendGameCommandUnsafe("/levelsync on"));
				fateSyncRequestNotBeforeUtc = now + FateSyncRequestInterval;
				if (num)
				{
					log.Information($"[HuntLogs] Sent throttled /levelsync on for {markName} in runtime FATE {runtimeFateId}; " + "combat remains disabled until PlayerState confirms level sync.");
				}
			}
			await Task.Delay(MatchingFatePollDelay, token);
		}
		log.Warning("[HuntLogs] Refusing to engage over-level FATE target " + markName + ": automatic level sync is disabled.");
		return false;
	}

	private unsafe static (bool InFate, ushort FateId) GetCurrentJoinedFateUnsafe()
	{
		FateManager* ptr = FateManager.Instance();
		if (ptr == null || ptr->FateJoined == 0 || ptr->CurrentFate == null)
		{
			return (InFate: false, FateId: 0);
		}
		ushort currentFateId = ptr->GetCurrentFateId();
		if (currentFateId != 0)
		{
			return (InFate: true, FateId: currentFateId);
		}
		return (InFate: false, FateId: 0);
	}

	private unsafe MatchingFateState GetMatchingFateStateUnsafe(uint fateId)
	{
		FateManager* ptr = FateManager.Instance();
		FateContext* ptr2 = ((ptr == null) ? null : ptr->GetFateById((ushort)fateId));
		PlayerState* ptr3 = PlayerState.Instance();
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (ptr2 == null)
		{
			return new MatchingFateState(Active: false, Joined: false, 0, Vector3.Zero, 0, localPlayer?.Level ?? 0, ptr3 != null && ptr3->IsLevelSynced);
		}
		ushort num = (ushort)((ptr != null && ptr->FateJoined != 0 && ptr->CurrentFate != null) ? ptr->GetCurrentFateId() : 0);
		return new MatchingFateState(Active: true, num != 0, num, ptr2->Location, ptr2->MaxLevel, localPlayer?.Level ?? 0, ptr3 != null && ptr3->IsLevelSynced);
	}

	private async Task<bool> TryEngageHuntTargetAsync(HuntMark mark, string markName, IBattleNpc combatTarget, int beforeOpenKills, CancellationToken token)
	{
		long initialHp = await RunOnFrameworkThreadAsync((Func<long>)(() => combatTarget.CurrentHp));
		int attempt;
		for (attempt = 1; attempt <= 2; attempt++)
		{
			token.ThrowIfCancellationRequested();
			HuntTargetEngageAttempt huntTargetEngageAttempt = await RunOnFrameworkThreadAsync(() => TryEngageHuntTargetUnsafe(mark, combatTarget, beforeOpenKills, initialHp, attempt > 1));
			if (!huntTargetEngageAttempt.Ready)
			{
				log.Debug("[HuntLogs] Passive engage skipped for " + markName + ": " + huntTargetEngageAttempt.Reason);
				return false;
			}
			if (huntTargetEngageAttempt.AlreadyEngaged)
			{
				return true;
			}
			log.Debug($"[HuntLogs] Passive engage attempt {attempt}/{2} for {markName}: interacted={huntTargetEngageAttempt.Interacted}, attackSent={huntTargetEngageAttempt.AttackSent}");
			if (await WaitForHuntTargetEngagedAsync(mark, combatTarget, beforeOpenKills, initialHp, token))
			{
				return true;
			}
			if (attempt < 2)
			{
				await Task.Delay(HuntTargetEngageRetryDelay, token);
			}
		}
		log.Information("[HuntLogs] Passive engage did not start combat for " + markName + "; continuing with configured combat backend.");
		return false;
	}

	private unsafe HuntTargetEngageAttempt TryEngageHuntTargetUnsafe(HuntMark mark, IBattleNpc target, int beforeOpenKills, long initialHp, bool useAttackFallback)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (!clientState.IsLoggedIn)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "client is not logged in");
		}
		if (localPlayer == null)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "local player is unavailable");
		}
		if (condition[ConditionFlag.Unconscious] || localPlayer.CurrentHp == 0 || localPlayer.IsDead)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is dead or unconscious");
		}
		if (condition[ConditionFlag.Casting] || localPlayer.IsCasting)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is casting");
		}
		if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.InFlight])
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is mounted or mounting");
		}
		if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "player is changing areas or logging out");
		}
		if (!target.IsTargetable || target.IsDead || target.CurrentHp == 0)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "target is not attackable");
		}
		if (IsHuntTargetEngagedUnsafe(mark, target, beforeOpenKills, initialHp))
		{
			return new HuntTargetEngageAttempt(Ready: true, AlreadyEngaged: true, Interacted: false, AttackSent: false, string.Empty);
		}
		bool flag = false;
		TargetSystem* ptr = TargetSystem.Instance();
		if (ptr != null && target.Address != IntPtr.Zero)
		{
			GameObject* address = (GameObject*)target.Address;
			ptr->InteractWithObject(address, checkLineOfSight: false);
			flag = true;
		}
		bool flag2 = false;
		if (useAttackFallback || !flag)
		{
			flag2 = SendGameCommandUnsafe("/generalaction \"Attack\"");
		}
		if (!flag && !flag2)
		{
			return new HuntTargetEngageAttempt(Ready: false, AlreadyEngaged: false, Interacted: false, AttackSent: false, "interact and Attack command both failed");
		}
		return new HuntTargetEngageAttempt(Ready: true, AlreadyEngaged: false, flag, flag2, string.Empty);
	}

	private async Task<bool> WaitForHuntTargetEngagedAsync(HuntMark mark, IBattleNpc target, int beforeOpenKills, long initialHp, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < HuntTargetEngageWaitTime)
		{
			token.ThrowIfCancellationRequested();
			if (await RunOnFrameworkThreadAsync(() => IsHuntTargetEngagedUnsafe(mark, target, beforeOpenKills, initialHp)))
			{
				return true;
			}
			await Task.Delay(250, token);
		}
		return false;
	}

	private bool IsHuntTargetEngagedUnsafe(HuntMark mark, IBattleNpc target, int beforeOpenKills, long initialHp)
	{
		try
		{
			if (GetOpenMonsterNoteKillsUnsafe(mark) < beforeOpenKills)
			{
				return true;
			}
			if (condition[ConditionFlag.InCombat])
			{
				return true;
			}
			if (target.IsDead || target.CurrentHp == 0)
			{
				return true;
			}
			if (initialHp > 0 && target.CurrentHp != 0 && target.CurrentHp < initialHp)
			{
				return true;
			}
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			return localPlayer != null && target.TargetObjectId == localPlayer.GameObjectId;
		}
		catch (Exception ex)
		{
			log.Debug("[HuntLogs] Hunt target engagement check failed: " + ex.Message);
			return GetOpenMonsterNoteKillsUnsafe(mark) < beforeOpenKills;
		}
	}

	private async Task SetTargetAsync(IGameObject target)
	{
		await RunOnFrameworkThreadAsync(() => targetManager.Target = target);
	}

	private async Task<bool> SetAndVerifyHuntTargetAsync(IBattleNpc target, string markName, CancellationToken token)
	{
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			token.ThrowIfCancellationRequested();
			if (await RunOnFrameworkThreadAsync(delegate
			{
				if (!target.IsTargetable || target.IsDead || target.CurrentHp == 0)
				{
					return false;
				}
				targetManager.Target = target;
				return targetManager.Target?.GameObjectId == target.GameObjectId;
			}))
			{
				log.Debug($"[HuntLogs] Selected {markName} as combat target on attempt {attempt}/3 (objectId={target.GameObjectId}).");
				return true;
			}
			await Task.Delay(150, token);
		}
		return false;
	}

	private async Task ResetTargetAsync()
	{
		await RunOnFrameworkThreadAsync(() => targetManager.Target = null);
	}

	private async Task EnableCombatAsync()
	{
		if (activeCombatBackend != CombatBackend.None)
		{
			return;
		}
		if (configuration.HuntLogs.CombatMode == HuntLogCombatMode.FrenRider)
		{
			if (frenRiderIpc.TryPrepareCombat())
			{
				if (await TrySendCombatCommandAsync("/fr on"))
				{
					activeCombatBackend = CombatBackend.FrenRider;
					log.Information("[HuntLogs] Enabled FrenRider combat backend after clearing FrenName.");
					return;
				}
				LogFrenRiderFallback("FrenRider unloaded or rejected /fr on");
			}
			else
			{
				LogFrenRiderFallback(frenRiderIpc.LastFailure);
			}
		}
		activeStandardCombatSelection = new StandardCombatSelection(configuration.HuntLogs.EnableRotationSolverReborn, configuration.HuntLogs.EnableVBMAI, configuration.HuntLogs.EnableBMRAI);
		activeCombatBackend = CombatBackend.Standard;
		List<Task<bool>> list = new List<Task<bool>>();
		if (activeStandardCombatSelection.Rsr)
		{
			list.Add(TrySendCombatCommandAsync("/rsr manual", TimeSpan.Zero));
		}
		if (activeStandardCombatSelection.Vbm)
		{
			list.Add(TrySendCombatCommandAsync("/vbmai on", TimeSpan.FromMilliseconds(100L)));
		}
		if (activeStandardCombatSelection.Bmr)
		{
			list.Add(TrySendCombatCommandAsync("/bmrai on", TimeSpan.FromMilliseconds(200L)));
		}
		if (list.Count > 0)
		{
			await Task.WhenAll(list);
		}
	}

	private async Task DisableCombatAsync()
	{
		CombatBackend combatBackend = activeCombatBackend;
		StandardCombatSelection standardCombatSelection = activeStandardCombatSelection;
		activeCombatBackend = CombatBackend.None;
		activeStandardCombatSelection = default(StandardCombatSelection);
		switch (combatBackend)
		{
		case CombatBackend.FrenRider:
			await TrySendCombatCommandAsync("/fr off");
			break;
		case CombatBackend.Standard:
		{
			List<Task<bool>> list = new List<Task<bool>>();
			if (standardCombatSelection.Rsr)
			{
				list.Add(TrySendCombatCommandAsync("/rsr off", TimeSpan.Zero));
			}
			if (standardCombatSelection.Vbm)
			{
				list.Add(TrySendCombatCommandAsync("/vbmai off", TimeSpan.FromMilliseconds(100L)));
			}
			if (standardCombatSelection.Bmr)
			{
				list.Add(TrySendCombatCommandAsync("/bmrai off", TimeSpan.FromMilliseconds(200L)));
			}
			if (list.Count > 0)
			{
				await Task.WhenAll(list);
			}
			break;
		}
		}
	}

	private async Task<bool> TrySendCombatCommandAsync(string command, TimeSpan delay = default(TimeSpan))
	{
		TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			framework.RunOnTick(delegate
			{
				try
				{
					bool flag = commandManager.ProcessCommand(command);
					if (!flag)
					{
						log.Warning("[HuntLogs] Combat command '" + command + "' was not registered.");
					}
					completion.TrySetResult(flag);
				}
				catch (Exception ex2)
				{
					log.Warning("[HuntLogs] Combat command '" + command + "' failed: " + ex2.Message);
					completion.TrySetResult(result: false);
				}
			}, delay);
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Could not schedule combat command '" + command + "': " + ex.Message);
			return false;
		}
		if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5L))) == completion.Task)
		{
			return await completion.Task;
		}
		log.Warning("[HuntLogs] Timed out waiting for scheduled combat command '" + command + "'.");
		return false;
	}

	private void LogFrenRiderFallback(string reason)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!(utcNow < frenRiderWarningNotBeforeUtc))
		{
			frenRiderWarningNotBeforeUtc = utcNow.AddSeconds(30.0);
			log.Warning("[HuntLogs] FrenRider combat setup unavailable; using configured Standard setup. " + reason);
		}
	}

	private async Task HandleDeathRecoveryAsync(HuntMark mark, CancellationToken token)
	{
		await HandleDeathRecoveryAsync(mark.TerritoryId, "processing " + database.GetMarkName(mark), token);
	}

	private async Task<bool> TryHandleDeathRecoveryAsync(uint returnTerritoryId, string description, CancellationToken token)
	{
		if (!(await IsUnconsciousAsync()))
		{
			return false;
		}
		await HandleDeathRecoveryAsync(returnTerritoryId, description, token);
		return true;
	}

	private async Task HandleDeathRecoveryAsync(uint returnTerritoryId, string description, CancellationToken token)
	{
		log.Warning("[HuntLogs] Player died while " + description + "; attempting hunt-local recovery.");
		if (vnavmeshIpc.IsPathRunning() || vnavmeshIpc.IsPathfinding())
		{
			await StopNavigationAndWaitForIdleAsync(returnTerritoryId, "death recovery while " + description, token, null, description);
		}
		await DisableCombatAsync();
		await ResetTargetAsync();
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < DeathRecoveryTimeout)
		{
			token.ThrowIfCancellationRequested();
			if (!(await RunOnFrameworkThreadAsync((Func<bool>)IsDeadOrUnconsciousUnsafe)))
			{
				break;
			}
			await RunOnFrameworkThreadAsync(() => FireCallback("SelectYesno", 0));
			await Task.Delay(1000, token);
		}
		if (!(await WaitUntilFrameworkAsync(() => !IsDeadOrUnconsciousUnsafe(), "respawn after hunt-log death", DeathRecoveryTimeout, token)))
		{
			throw new TimeoutException("Timed out waiting for respawn after hunt-log death.");
		}
		await Task.Delay(Math.Max(1, configuration.DeathRespawnDelay) * 1000, token);
		await TryWaitForTravelSettledAsync("after hunt-log death respawn", TimeSpan.FromSeconds(60L), token);
		if (returnTerritoryId != 0)
		{
			HuntTravelResult huntTravelResult = await TryTravelToTerritoryAsync(returnTerritoryId, token);
			if (!huntTravelResult.Arrived)
			{
				log.Warning($"[HuntLogs] Return travel after death did not complete while {description}: {huntTravelResult.FailureReason}. " + "Hunt scheduling will continue from the current territory.");
			}
		}
	}

	private async Task ReturnIfConfiguredAsync(CancellationToken token)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.Phase = HuntLogPhase.Returning;
			s.CurrentStep = $"Returning to {configuration.HuntLogs.ReturnDestination}";
		});
		if (!(await TryReturnToConfiguredDestinationAsync("completed character return", token)))
		{
			throw new InvalidOperationException($"Lifestream rejected return destination {configuration.HuntLogs.ReturnDestination}.");
		}
	}

	private async Task<bool> TryReturnToConfiguredDestinationAsync(string context, CancellationToken token)
	{
		HuntLogReturnDestination destination = configuration.HuntLogs.ReturnDestination;
		await ClearNearbyAggroBeforeTravelAsync($"{context}: returning to {destination}", token);
		bool accepted = false;
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			await ClearNearbyAggroBeforeTravelAsync($"{context}: return attempt {attempt} to {destination}", token);
			await WaitForTravelSettledAsync($"before return attempt {attempt}", TimeSpan.FromSeconds(10L), token);
			accepted = await RunOnFrameworkThreadAsync(() => lifestreamIpc.ReturnTo(destination));
			if (accepted)
			{
				break;
			}
			if (attempt == 1)
			{
				log.Warning($"[HuntLogs] Lifestream rejected return destination {destination} during {context}; retrying once.");
				await Task.Delay(2500, token);
				await ClearNearbyAggroBeforeTravelAsync($"retrying return to {destination} after Lifestream rejection", token);
			}
		}
		if (!accepted)
		{
			return false;
		}
		await Task.Delay(1000, token);
		await WaitForTravelStartAsync($"return to {destination}", TimeSpan.FromSeconds(8L), token);
		await WaitForTravelSettledAsync("Lifestream return", TimeSpan.FromSeconds(120L), token);
		return true;
	}

	private async Task<bool> HandleGrandCompanyRankQuestAsync(uint grandCompanyId, int grandCompanyRank, CancellationToken token)
	{
		GrandCompanyUnlockQuestData data = grandCompanyRank switch
		{
			7 => DzemaelRank7.GetValueOrDefault(grandCompanyId), 
			8 => AurumRank8.GetValueOrDefault(grandCompanyId), 
			_ => null, 
		};
		if (data == null)
		{
			return true;
		}
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			(bool, bool, byte) tuple = await GetGrandCompanyUnlockQuestStateAsync(data.QuestId);
			if (tuple.Item2)
			{
				return true;
			}
			if (tuple.Item1 && tuple.Item3 == 2)
			{
				if (!(await TryRunGrandCompanyUnlockDutyAsync(data.DutyId, token)))
				{
					return false;
				}
				return await ResumeQuestionableAndCompleteGrandCompanyQuestAsync(data.QuestId, grandCompanyRank, token);
			}
			if (attempt == 2)
			{
				MarkCurrentCharacterStatus($"Blocked: GC quest {data.QuestId} not ready for duty", markSkipped: true);
				log.Warning($"[HuntLogs] GC rank {grandCompanyRank} quest {data.QuestId} is not ready for duty (accepted={tuple.Item1}, sequence={tuple.Item3}); skipping rank-up.");
				return false;
			}
			if (!(await TryQueueQuestionableUnlockQuestAsync(data, token)))
			{
				return false;
			}
		}
		return false;
	}

	private unsafe async Task<(bool Accepted, bool Completed, byte Sequence)> GetGrandCompanyUnlockQuestStateAsync(uint luminaQuestRowId)
	{
		ushort questId = NormalizeQuestId(luminaQuestRowId);
		return await RunOnFrameworkThreadAsync(delegate
		{
			QuestManager* ptr = QuestManager.Instance();
			bool num = ptr != null && ptr->IsQuestAccepted(questId);
			bool item = QuestManager.IsQuestComplete(questId);
			byte item2 = (byte)(num ? QuestManager.GetQuestSequence(questId) : 0);
			return (accepted: num, completed: item, sequence: item2);
		});
	}

	private unsafe async Task<QuestionableUnlockProgressState> GetQuestionableUnlockProgressStateAsync(uint luminaQuestRowId)
	{
		ushort questId = NormalizeQuestId(luminaQuestRowId);
		return await RunOnFrameworkThreadAsync(delegate
		{
			QuestManager* ptr = QuestManager.Instance();
			bool flag = ptr != null && ptr->IsQuestAccepted(questId);
			return new QuestionableUnlockProgressState(clientState.TerritoryType, condition[ConditionFlag.InCombat], condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51], flag, QuestManager.IsQuestComplete(questId), (byte)(flag ? QuestManager.GetQuestSequence(questId) : 0), questionableIpc.GetCurrentQuestId());
		});
	}

	private void ResetScopedMovementMonitorForProgress(MovementMonitorService.ScopedMonitoringSession? movementSession, QuestionableUnlockProgressState? previous, QuestionableUnlockProgressState current)
	{
		if (movementSession == null || !movementSession.Enabled)
		{
			return;
		}
		if (!current.InCombat && !current.InAreaTransition)
		{
			if (!previous.HasValue)
			{
				return;
			}
			QuestionableUnlockProgressState valueOrDefault = previous.GetValueOrDefault();
			if (valueOrDefault.TerritoryId == current.TerritoryId && valueOrDefault.Accepted == current.Accepted && valueOrDefault.Completed == current.Completed && valueOrDefault.Sequence == current.Sequence && string.Equals(valueOrDefault.CurrentQuestionableQuestId, current.CurrentQuestionableQuestId, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		movementSession.ResetMovementTimer();
	}

	private async Task<bool> TryRecoverScopedQuestionableUnlockAsync(MovementMonitorService.ScopedMonitoringSession? movementSession, ushort questId, string context, CancellationToken token)
	{
		if (movementSession == null || !movementSession.ConsumeRecoveryRequest())
		{
			return true;
		}
		int recoveryAttempt = movementSession.RegisterRecoveryAttempt();
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = $"Recovering stalled Questionable quest {questId}";
		});
		log.Warning($"[HuntLogs] Questionable movement monitor detected a stalled {context}; recovery attempt {recoveryAttempt}/{4}, " + "reloading without clearing the required priority quest.");
		if (await StartQuestionableAndVerifyQuestAsync(questId, context, token))
		{
			movementSession.ResetMovementTimer();
			return true;
		}
		if (recoveryAttempt < 4)
		{
			movementSession.ResetMovementTimer();
			log.Warning($"[HuntLogs] Questionable recovery attempt {recoveryAttempt}/{4} did not yet resume intended quest {questId}; " + "keeping its priority entry and continuing the unlock workflow.");
			return true;
		}
		log.Warning($"[HuntLogs] Questionable failed to resume intended quest {questId} after {4} movement-monitor recoveries.");
		return false;
	}

	private async Task<bool> TryRunGrandCompanyUnlockDutyAsync(uint dutyId, CancellationToken token)
	{
		return await TryRunHuntDutyAsync(dutyId, $"GC unlock duty {dutyId}", token, ensureUnlocked: false);
	}

	private async Task<bool> ResumeQuestionableAndCompleteGrandCompanyQuestAsync(uint luminaQuestRowId, int grandCompanyRank, CancellationToken token)
	{
		ushort questId = NormalizeQuestId(luminaQuestRowId);
		string questIdText = questId.ToString();
		log.Information($"[HuntLogs] Resuming GC quest after duty: LuminaQuestRow={luminaQuestRowId}, QuestionableQuest={questId}.");
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			MarkCurrentCharacterStatus($"Blocked: Questionable unavailable after GC duty for quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
			log.Warning($"[HuntLogs] Questionable is unavailable after GC unlock duty for quest {questId} (Lumina row {luminaQuestRowId}).");
			return false;
		}
		bool result;
		try
		{
			if (!(await PrepareQuestionableQuestHandoffAsync(luminaQuestRowId, questId, $"resuming GC quest {questId} after duty", token)))
			{
				MarkCurrentCharacterStatus($"Blocked: could not prepare Questionable after GC duty for quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
				result = false;
			}
			else if (!questionableIpc.AddQuestPriority(questIdText))
			{
				MarkCurrentCharacterStatus($"Blocked: Questionable rejected GC quest {questId} (Lumina {luminaQuestRowId}) after duty", markSkipped: true);
				log.Warning($"[HuntLogs] Questionable rejected GC quest {questId} (Lumina row {luminaQuestRowId}) " + "while resuming after duty.");
				result = false;
			}
			else if (!(await StartQuestionableAndVerifyQuestAsync(questId, $"GC quest {questId} (Lumina row {luminaQuestRowId}) after duty", token)))
			{
				MarkCurrentCharacterStatus($"Blocked: /qst did not start after GC duty for quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
				result = false;
			}
			else
			{
				UpdateState(delegate(HuntLogAutomationState s)
				{
					s.CurrentStep = $"Completing GC rank {grandCompanyRank} quest {questId}";
				});
				using MovementMonitorService.ScopedMonitoringSession movementSession = movementMonitor?.BeginScopedMonitoring($"GC dungeon unlock quest {questId} after duty");
				QuestionableUnlockProgressState? previousProgress = null;
				DateTime started = DateTime.UtcNow;
				while (true)
				{
					if (DateTime.UtcNow - started < QuestionableQuestTimeout)
					{
						token.ThrowIfCancellationRequested();
						QuestionableUnlockProgressState questionableUnlockProgressState = await GetQuestionableUnlockProgressStateAsync(luminaQuestRowId);
						ResetScopedMovementMonitorForProgress(movementSession, previousProgress, questionableUnlockProgressState);
						previousProgress = questionableUnlockProgressState;
						if (questionableUnlockProgressState.Completed)
						{
							log.Information($"[HuntLogs] Questionable completed GC quest {questId} (Lumina row {luminaQuestRowId}) " + "after its duty handoff.");
							result = true;
							break;
						}
						if (!(await TryRecoverScopedQuestionableUnlockAsync(movementSession, questId, $"GC quest {questId} (Lumina row {luminaQuestRowId}) after duty", token)))
						{
							MarkCurrentCharacterStatus($"Blocked: movement recovery did not resume GC quest {questId} after duty", markSkipped: true);
							result = false;
							break;
						}
						await Task.Delay(1000, token);
						continue;
					}
					MarkCurrentCharacterStatus($"Blocked: GC quest {questId} (Lumina {luminaQuestRowId}) did not complete after duty", markSkipped: true);
					log.Warning($"[HuntLogs] Timed out waiting for Questionable to complete GC quest {questId} (Lumina row {luminaQuestRowId}) after duty.");
					result = false;
					break;
				}
			}
		}
		finally
		{
			await CleanupQuestionableQuestHandoffAsync($"GC quest {questId} (Lumina row {luminaQuestRowId}) after duty", CancellationToken.None);
		}
		return result;
	}

	private async Task<bool> TryQueueQuestionableUnlockQuestAsync(GrandCompanyUnlockQuestData quest, CancellationToken token)
	{
		uint luminaQuestRowId = quest.QuestId;
		ushort questId = NormalizeQuestId(luminaQuestRowId);
		string questIdText = questId.ToString();
		log.Information($"[HuntLogs] Queueing GC unlock quest: LuminaQuestRow={luminaQuestRowId}, QuestionableQuest={questId}.");
		if (!questionableIpc.TryEnsureAvailableSilent())
		{
			MarkCurrentCharacterStatus($"Blocked: Questionable unavailable for GC quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
			log.Warning($"[HuntLogs] Questionable is unavailable; cannot queue GC unlock quest {questId} (Lumina row {luminaQuestRowId}).");
			return false;
		}
		(bool Accepted, bool Completed, byte Sequence) initialState = await GetGrandCompanyUnlockQuestStateAsync(luminaQuestRowId);
		if (initialState.Completed)
		{
			return true;
		}
		bool result;
		try
		{
			if (!(await PrepareQuestionableQuestHandoffAsync(luminaQuestRowId, questId, $"advancing GC quest {questId} to its duty step", token)))
			{
				MarkCurrentCharacterStatus($"Blocked: could not prepare Questionable for GC quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
				result = false;
			}
			else
			{
				bool flag = !initialState.Accepted;
				if (flag)
				{
					flag = !(await TryPickUpGrandCompanyUnlockQuestAsync(quest, token));
				}
				if (flag)
				{
					result = false;
				}
				else if (!questionableIpc.AddQuestPriority(questIdText))
				{
					MarkCurrentCharacterStatus($"Blocked: Questionable rejected GC quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
					log.Warning($"[HuntLogs] Questionable rejected GC unlock quest {questId} (Lumina row {luminaQuestRowId}).");
					result = false;
				}
				else if (!(await StartQuestionableAndVerifyQuestAsync(questId, $"GC quest {questId} (Lumina row {luminaQuestRowId}) before duty", token, tryStartSpecificQuest: false)))
				{
					MarkCurrentCharacterStatus($"Blocked: /qst did not start for GC quest {questId} (Lumina {luminaQuestRowId})", markSkipped: true);
					result = false;
				}
				else
				{
					log.Information($"[HuntLogs] Queued Questionable GC unlock quest {questId} (Lumina row {luminaQuestRowId}); waiting for duty step once.");
					using MovementMonitorService.ScopedMonitoringSession movementSession = movementMonitor?.BeginScopedMonitoring($"GC dungeon unlock quest {questId} before duty");
					QuestionableUnlockProgressState? previousProgress = null;
					DateTime started = DateTime.UtcNow;
					while (true)
					{
						if (DateTime.UtcNow - started < QuestionableQuestTimeout)
						{
							token.ThrowIfCancellationRequested();
							QuestionableUnlockProgressState questionableUnlockProgressState = await GetQuestionableUnlockProgressStateAsync(luminaQuestRowId);
							ResetScopedMovementMonitorForProgress(movementSession, previousProgress, questionableUnlockProgressState);
							previousProgress = questionableUnlockProgressState;
							if (questionableUnlockProgressState.Completed || (questionableUnlockProgressState.Accepted && questionableUnlockProgressState.Sequence == 2))
							{
								result = true;
								break;
							}
							if (!(await TryRecoverScopedQuestionableUnlockAsync(movementSession, questId, $"GC quest {questId} (Lumina row {luminaQuestRowId}) before duty", token)))
							{
								MarkCurrentCharacterStatus($"Blocked: movement recovery did not resume GC quest {questId} before duty", markSkipped: true);
								result = false;
								break;
							}
							await Task.Delay(1000, token);
							continue;
						}
						MarkCurrentCharacterStatus($"Blocked: GC quest {questId} (Lumina {luminaQuestRowId}) did not reach duty step", markSkipped: true);
						log.Warning($"[HuntLogs] Timed out waiting for Questionable to advance GC unlock quest {questId} (Lumina row {luminaQuestRowId}) to the duty step.");
						result = false;
						break;
					}
				}
			}
		}
		finally
		{
			await CleanupQuestionableQuestHandoffAsync($"GC quest {questId} (Lumina row {luminaQuestRowId}) before duty", CancellationToken.None);
		}
		return result;
	}

	private async Task<bool> TryPickUpGrandCompanyUnlockQuestAsync(GrandCompanyUnlockQuestData quest, CancellationToken token)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Traveling to " + quest.OfficerName + " for " + quest.QuestName;
		});
		HuntTravelResult huntTravelResult = await TryTravelToGrandCompanyHeadquartersAsync(quest, token);
		if (!huntTravelResult.Arrived)
		{
			MarkCurrentCharacterStatus("Blocked: could not travel to " + quest.OfficerName + " for " + quest.QuestName, markSkipped: true);
			log.Warning($"[HuntLogs] Companion could not travel to {quest.OfficerName} for GC quest '{quest.QuestName}': {huntTravelResult.FailureReason}");
			return false;
		}
		if (!(await TryWaitForCharacterReadyAsync(token)))
		{
			MarkCurrentCharacterStatus("Blocked: character did not settle before picking up " + quest.QuestName, markSkipped: true);
			return false;
		}
		string value = string.Empty;
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			GrandCompanyOfficerState grandCompanyOfficerState;
			try
			{
				grandCompanyOfficerState = await MoveIntoGrandCompanyOfficerInteractionRangeAsync((TerritoryId: quest.TerritoryId, Position: quest.OfficerPosition, NpcDataId: quest.OfficerNpcDataId), attempt, token);
			}
			catch (InvalidOperationException ex)
			{
				value = ex.Message;
				if (attempt < 2)
				{
					continue;
				}
				break;
			}
			if (!grandCompanyOfficerState.Loaded || !grandCompanyOfficerState.Targetable || !grandCompanyOfficerState.InRange)
			{
				value = $"quest officer was not interaction-ready (loaded={grandCompanyOfficerState.Loaded}, targetable={grandCompanyOfficerState.Targetable}, distance={grandCompanyOfficerState.Distance:F1}, interactionRange={grandCompanyOfficerState.InteractionRange:F1})";
				if (attempt >= 2)
				{
					break;
				}
				continue;
			}
			UpdateState(delegate(HuntLogAutomationState s)
			{
				s.CurrentStep = "Talking to " + quest.OfficerName + " for " + quest.QuestName;
			});
			if (!(await RunOnFrameworkThreadAsync(() => TryInteractWithGrandCompanyOfficerUnsafe(quest.OfficerNpcDataId))))
			{
				value = "quest officer interaction was rejected after range verification";
				if (attempt >= 2)
				{
					break;
				}
				continue;
			}
			bool selectedQuest = false;
			bool acceptClicked = false;
			string visibleOptions = string.Empty;
			DateTime pickupStarted = DateTime.UtcNow;
			while (DateTime.UtcNow - pickupStarted < GrandCompanyQuestPickupUiTimeout)
			{
				token.ThrowIfCancellationRequested();
				(bool, bool, byte) tuple = await GetGrandCompanyUnlockQuestStateAsync(quest.QuestId);
				if (tuple.Item1 || tuple.Item2)
				{
					log.Information($"[HuntLogs] Companion accepted GC quest '{quest.QuestName}' from {quest.OfficerName}; Questionable will now receive the priority handoff.");
					return true;
				}
				if (!selectedQuest)
				{
					GrandCompanyQuestListSelectionResult grandCompanyQuestListSelectionResult = await RunOnFrameworkThreadAsync(() => TrySelectGrandCompanyQuestFromListUnsafe(quest));
					if (grandCompanyQuestListSelectionResult.Visible)
					{
						visibleOptions = grandCompanyQuestListSelectionResult.Options;
					}
					if (grandCompanyQuestListSelectionResult.Selected)
					{
						selectedQuest = true;
						UpdateState(delegate(HuntLogAutomationState s)
						{
							s.CurrentStep = "Accepting " + quest.QuestName + " from " + quest.OfficerName;
						});
						log.Information($"[HuntLogs] Selected GC quest '{quest.QuestName}' from {quest.OfficerName} (NPC {quest.OfficerNpcDataId}, territory {quest.TerritoryId}).");
					}
				}
				else if (!acceptClicked)
				{
					acceptClicked = await RunOnFrameworkThreadAsync((Func<bool>)TryAcceptJournalQuestUnsafe);
					if (acceptClicked)
					{
						log.Information("[HuntLogs] Clicked the JournalAccept button for GC quest '" + quest.QuestName + "'.");
					}
				}
				await Task.Delay(250, token);
			}
			value = ((!selectedQuest) ? (string.IsNullOrWhiteSpace(visibleOptions) ? "SelectIconString did not become ready after interacting with the quest officer" : $"'{quest.QuestName}' was not offered; visible entries=[{visibleOptions}]") : (acceptClicked ? "quest did not become accepted after clicking JournalAccept" : "JournalAccept did not become ready after selecting the quest"));
			if (attempt < 2)
			{
				log.Warning($"[HuntLogs] GC quest pickup attempt {attempt}/{2} failed for '{quest.QuestName}'; retrying. {value}");
			}
		}
		string text = (string.IsNullOrWhiteSpace(value) ? ("could not pick up " + quest.QuestName + " from " + quest.OfficerName) : $"could not pick up {quest.QuestName} from {quest.OfficerName}: {value}");
		MarkCurrentCharacterStatus("Blocked: " + text, markSkipped: true);
		log.Warning("[HuntLogs] Companion " + text + ".");
		return false;
	}

	private async Task<HuntTravelResult> TryTravelToGrandCompanyHeadquartersAsync(GrandCompanyUnlockQuestData quest, CancellationToken token)
	{
		if (await RunOnFrameworkThreadAsync(() => clientState.TerritoryType) == quest.TerritoryId)
		{
			return new HuntTravelResult(Arrived: true, string.Empty);
		}
		string destination = quest.OfficerName + " in " + database.GetTerritoryName(quest.TerritoryId);
		await ClearNearbyAggroBeforeTravelAsync("travel to Grand Company headquarters for " + quest.QuestName, token);
		await WaitForCombatAndCastingToEndAsync("travel to Grand Company headquarters for " + quest.QuestName, CombatClearTimeout, token, keepCombatAutomationActive: true);
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Using Lifestream to reach " + quest.OfficerName;
		});
		if (!(await RunOnFrameworkThreadAsync(() => lifestreamIpc.ExecuteCommand("gc"))))
		{
			return new HuntTravelResult(Arrived: false, "Lifestream rejected the Grand Company command while traveling to " + destination + ".");
		}
		log.Information($"[HuntLogs] Lifestream Grand Company travel accepted for '{quest.QuestName}'; waiting for territory {quest.TerritoryId} ({database.GetTerritoryName(quest.TerritoryId)}).");
		if (!(await WaitUntilFrameworkAsync(() => clientState.TerritoryType == quest.TerritoryId, "Lifestream Grand Company arrival for " + quest.QuestName, TimeSpan.FromSeconds(90L), token)))
		{
			uint num = await RunOnFrameworkThreadAsync(() => clientState.TerritoryType);
			return new HuntTravelResult(Arrived: false, $"Lifestream Grand Company travel did not reach {destination}; current territory is {database.GetTerritoryName(num)} ({num}).");
		}
		if (!(await TryWaitForTravelSettledAsync("Lifestream Grand Company arrival for " + quest.QuestName, TimeSpan.FromSeconds(60L), token)))
		{
			return new HuntTravelResult(Arrived: false, "Travel did not settle after Lifestream reached " + destination + ".");
		}
		return new HuntTravelResult(Arrived: true, string.Empty);
	}

	private unsafe GrandCompanyQuestListSelectionResult TrySelectGrandCompanyQuestFromListUnsafe(GrandCompanyUnlockQuestData quest)
	{
		if (clientState.TerritoryType != quest.TerritoryId)
		{
			return new GrandCompanyQuestListSelectionResult(Visible: false, Selected: false, string.Empty);
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return new GrandCompanyQuestListSelectionResult(Visible: false, Selected: false, string.Empty);
		}
		IGameObject? target = targetManager.Target;
		bool flag = Vector3.Distance(localPlayer.Position, quest.OfficerPosition) <= 10f;
		bool flag2 = objectTable.Any((IGameObject x) => x.BaseId == quest.OfficerNpcDataId);
		if (target?.BaseId != quest.OfficerNpcDataId && !(flag && flag2))
		{
			return new GrandCompanyQuestListSelectionResult(Visible: false, Selected: false, string.Empty);
		}
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectIconString");
		if (addonByName == IntPtr.Zero)
		{
			return new GrandCompanyQuestListSelectionResult(Visible: false, Selected: false, string.Empty);
		}
		AddonSelectIconString* ptr = (AddonSelectIconString*)(nint)addonByName;
		if (ptr == null)
		{
			return new GrandCompanyQuestListSelectionResult(Visible: false, Selected: false, string.Empty);
		}
		AtkUnitBase* ptr2 = &ptr->AtkUnitBase;
		if (!ptr2->IsVisible || !ptr2->IsReady || ptr2->AtkValuesCount <= 7)
		{
			return new GrandCompanyQuestListSelectionResult(Visible: false, Selected: false, string.Empty);
		}
		int num = Math.Clamp(ptr2->AtkValues[5].Int, 0, 100);
		List<string> list = new List<string>(num);
		for (int num2 = 0; num2 < num; num2++)
		{
			int num3 = num2 * 3 + 7;
			if (num3 >= ptr2->AtkValuesCount)
			{
				break;
			}
			string text = ReadAtkValueString(ptr2->AtkValues[num3]);
			list.Add(text);
			if (text.Contains(quest.QuestName, StringComparison.OrdinalIgnoreCase))
			{
				AtkValue* ptr3 = stackalloc AtkValue[1];
				*ptr3 = default(AtkValue);
				ptr3->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
				ptr3->Int = num2;
				ptr2->FireCallback(1u, ptr3);
				return new GrandCompanyQuestListSelectionResult(Visible: true, Selected: true, string.Join(" | ", list.Select((string entry, int index) => $"{index}:\"{entry}\"")));
			}
		}
		return new GrandCompanyQuestListSelectionResult(Visible: true, Selected: false, string.Join(" | ", list.Select((string entry, int index) => $"{index}:\"{entry}\"")));
	}

	private unsafe bool TryAcceptJournalQuestUnsafe()
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName("JournalAccept");
		if (addonByName == IntPtr.Zero)
		{
			return false;
		}
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
		if (ptr == null || !ptr->IsVisible || !ptr->IsReady)
		{
			return false;
		}
		AtkComponentButton* componentButtonById = ptr->GetComponentButtonById(44u);
		if (componentButtonById == null || !componentButtonById->IsEnabled || componentButtonById->AtkResNode == null || !componentButtonById->AtkResNode->IsVisible())
		{
			return false;
		}
		AtkEvent* ptr2 = componentButtonById->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event;
		if (ptr2 == null)
		{
			return false;
		}
		AtkEvent* ptr3 = ptr2;
		ptr->ReceiveEvent(ptr3->State.EventType, (int)ptr3->Param, ptr2, null);
		return true;
	}

	private async Task<GrandCompanyPromotionReadiness> WaitForGrandCompanyPromotionReadinessAsync(CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		GrandCompanyPromotionReadiness readiness = default(GrandCompanyPromotionReadiness);
		while (DateTime.UtcNow - started < GrandCompanyPromotionReadinessTimeout)
		{
			token.ThrowIfCancellationRequested();
			readiness = await RunOnFrameworkThreadAsync((Func<GrandCompanyPromotionReadiness>)GetGrandCompanyPromotionReadinessUnsafe);
			if (readiness.CharacterReady && readiness.InventoryAvailable)
			{
				return readiness;
			}
			await Task.Delay(250, token);
		}
		return readiness;
	}

	private unsafe GrandCompanyPromotionReadiness GetGrandCompanyPromotionReadinessUnsafe()
	{
		(uint, int, uint, int) playerInfoUnsafe = GetPlayerInfoUnsafe();
		uint requiredGrandCompanyPromotionSeals = GetRequiredGrandCompanyPromotionSeals(playerInfoUnsafe.Item4);
		bool characterReady = IsCharacterReadyForMovementUnsafe();
		uint item = playerInfoUnsafe.Item3;
		if ((item < 1 || item > 3) ? true : false)
		{
			return new GrandCompanyPromotionReadiness(characterReady, InventoryAvailable: false, playerInfoUnsafe.Item3, playerInfoUnsafe.Item4, 0u, requiredGrandCompanyPromotionSeals);
		}
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return new GrandCompanyPromotionReadiness(characterReady, InventoryAvailable: false, playerInfoUnsafe.Item3, playerInfoUnsafe.Item4, 0u, requiredGrandCompanyPromotionSeals);
		}
		return new GrandCompanyPromotionReadiness(characterReady, InventoryAvailable: true, playerInfoUnsafe.Item3, playerInfoUnsafe.Item4, ptr->GetCompanySeals((byte)playerInfoUnsafe.Item3), requiredGrandCompanyPromotionSeals);
	}

	private static uint GetRequiredGrandCompanyPromotionSeals(int grandCompanyRank)
	{
		return grandCompanyRank switch
		{
			1 => 2000u, 
			2 => 3000u, 
			3 => 4000u, 
			4 => 5000u, 
			5 => 6000u, 
			6 => 7000u, 
			7 => 8000u, 
			8 => 9000u, 
			9 => 10000u, 
			_ => 0u, 
		};
	}

	private async Task<bool> RankUpGrandCompanyAsync(uint grandCompanyId, CancellationToken token)
	{
		(uint TerritoryId, Vector3 Position, uint NpcDataId)? officer = GetGrandCompanyOfficer(grandCompanyId);
		if (!officer.HasValue)
		{
			return false;
		}
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Ranking up Grand Company";
		});
		if (await RunOnFrameworkThreadAsync(() => clientState.TerritoryType != officer.Value.TerritoryId))
		{
			lifestreamIpc.ExecuteCommand("gc");
			if (!(await WaitUntilFrameworkAsync(() => clientState.TerritoryType == officer.Value.TerritoryId, "Grand Company teleport", TimeSpan.FromSeconds(90L), token)))
			{
				MarkCurrentCharacterStatus("Blocked: Grand Company teleport did not arrive", markSkipped: true);
				log.Warning($"[HuntLogs] Grand Company teleport did not arrive in territory {officer.Value.TerritoryId}.");
				return false;
			}
		}
		if (!(await TryWaitForCharacterReadyAsync(token)))
		{
			MarkCurrentCharacterStatus("Blocked: character did not settle after Grand Company travel", markSkipped: true);
			log.Warning("[HuntLogs] Character did not settle after Grand Company travel; officer movement was not started.");
			return false;
		}
		bool selectedPromotion = false;
		string lastFailure = string.Empty;
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			GrandCompanyOfficerState grandCompanyOfficerState;
			try
			{
				grandCompanyOfficerState = await MoveIntoGrandCompanyOfficerInteractionRangeAsync(officer.Value, attempt, token);
			}
			catch (InvalidOperationException ex)
			{
				lastFailure = ex.Message;
				if (attempt < 2)
				{
					log.Warning($"[HuntLogs] Grand Company officer movement attempt {attempt}/{2} failed; reacquiring once. {ex.Message}");
					continue;
				}
				break;
			}
			if (!grandCompanyOfficerState.Loaded || !grandCompanyOfficerState.Targetable || !grandCompanyOfficerState.InRange)
			{
				lastFailure = $"personnel officer was not interaction-ready (loaded={grandCompanyOfficerState.Loaded}, targetable={grandCompanyOfficerState.Targetable}, distance={grandCompanyOfficerState.Distance:F1}, interactionRange={grandCompanyOfficerState.InteractionRange:F1})";
				if (attempt >= 2)
				{
					break;
				}
				log.Warning("[HuntLogs] " + lastFailure + "; retrying movement and reacquisition once.");
				continue;
			}
			if (!(await RunOnFrameworkThreadAsync(() => TryInteractWithGrandCompanyOfficerUnsafe(officer.Value.NpcDataId))))
			{
				lastFailure = "personnel officer interaction was rejected after range verification";
				if (attempt >= 2)
				{
					break;
				}
				log.Warning("[HuntLogs] " + lastFailure + "; retrying movement and reacquisition once.");
				continue;
			}
			switch (await SelectGrandCompanyPromotionOptionAsync(token))
			{
			case GrandCompanyPromotionSelectionResult.Selected:
				break;
			case GrandCompanyPromotionSelectionResult.NoMatchingOption:
				return false;
			default:
				lastFailure = "SelectString promotion menu did not appear within 15 seconds";
				if (attempt < 2)
				{
					log.Warning("[HuntLogs] " + lastFailure + "; retrying officer movement, reacquisition, and interaction once.");
				}
				continue;
			}
			selectedPromotion = true;
			break;
		}
		if (!selectedPromotion)
		{
			string text = (string.IsNullOrWhiteSpace(lastFailure) ? "Grand Company personnel officer handoff timed out twice" : ("Grand Company personnel officer handoff timed out twice: " + lastFailure));
			MarkCurrentCharacterStatus("Blocked: " + text, markSkipped: true);
			log.Warning("[HuntLogs] " + text + ".");
			return false;
		}
		if (!(await WaitUntilFrameworkAsync(() => FireCallback("GrandCompanyRankUp", 0), "Grand Company promotion confirmation", TimeSpan.FromSeconds(15L), token)))
		{
			return false;
		}
		await Task.Delay(1500, token);
		return true;
	}

	private async Task<bool> EnsureSquadronCommanderUnlockAsync(uint grandCompanyId, CancellationToken token)
	{
		uint questRowId = GetSquadronCommanderQuestRowId(grandCompanyId);
		if (questRowId == 0)
		{
			MarkCurrentCharacterStatus($"Blocked: unsupported Grand Company {grandCompanyId} for Squadron and Commander", markSkipped: true);
			return false;
		}
		if ((await GetGrandCompanyUnlockQuestStateAsync(questRowId)).Item2)
		{
			log.Information($"[HuntLogs] Squadron and Commander is already complete: company={grandCompanyId}, questRow={questRowId}, questId={NormalizeQuestId(questRowId)}.");
			return true;
		}
		if (!(await GetGrandCompanyUnlockQuestStateAsync(66967u)).Item2)
		{
			MarkCurrentCharacterStatus("Blocked: Rising to the Challenge is required before Squadron and Commander", markSkipped: true);
			log.Warning($"[HuntLogs] Squadron and Commander is not available because Rising to the Challenge is incomplete: prerequisiteRow={66967u}, prerequisiteId={NormalizeQuestId(66967u)}.");
			return false;
		}
		log.Information($"[HuntLogs] Live Grand Company rank 9 reached, but Squadron and Commander is incomplete; delaying completion and relog. company={grandCompanyId}, questRow={questRowId}, questId={NormalizeQuestId(questRowId)}.");
		if (!(await CompleteSquadronCommanderUnlockAsync(grandCompanyId, token)))
		{
			return false;
		}
		if (await WaitForSquadronCommanderQuestCompletionAsync(questRowId, token))
		{
			return true;
		}
		MarkCurrentCharacterStatus($"Blocked: Squadron and Commander quest {questRowId} did not become complete", markSkipped: true);
		log.Warning($"[HuntLogs] Personnel Officer flow ended, but the native quest completion bit did not update: company={grandCompanyId}, questRow={questRowId}, questId={NormalizeQuestId(questRowId)}.");
		return false;
	}

	private async Task<bool> CompleteSquadronCommanderUnlockAsync(uint grandCompanyId, CancellationToken token)
	{
		(uint TerritoryId, Vector3 Position, uint NpcDataId)? officer = GetGrandCompanyOfficer(grandCompanyId);
		if (!officer.HasValue)
		{
			return false;
		}
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.CurrentStep = "Accepting Squadron and Commander";
		});
		log.Information("[HuntLogs] Grand Company rank 9 reached; starting the scoped Squadron and Commander follow-up.");
		if (!(await TryWaitForCharacterReadyAsync(token)))
		{
			MarkCurrentCharacterStatus("Blocked: character did not settle before Squadron and Commander", markSkipped: true);
			return false;
		}
		string lastFailure = string.Empty;
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			GrandCompanyOfficerState grandCompanyOfficerState;
			try
			{
				grandCompanyOfficerState = await MoveIntoGrandCompanyOfficerInteractionRangeAsync(officer.Value, attempt, token);
			}
			catch (InvalidOperationException ex)
			{
				lastFailure = ex.Message;
				if (attempt < 2)
				{
					continue;
				}
				break;
			}
			if (!grandCompanyOfficerState.Loaded || !grandCompanyOfficerState.Targetable || !grandCompanyOfficerState.InRange)
			{
				lastFailure = $"personnel officer was not interaction-ready (loaded={grandCompanyOfficerState.Loaded}, targetable={grandCompanyOfficerState.Targetable}, distance={grandCompanyOfficerState.Distance:F1}, interactionRange={grandCompanyOfficerState.InteractionRange:F1})";
				if (attempt >= 2)
				{
					break;
				}
				continue;
			}
			if (!(await RunOnFrameworkThreadAsync(() => TryInteractWithGrandCompanyOfficerUnsafe(officer.Value.NpcDataId))))
			{
				lastFailure = "personnel officer interaction was rejected after range verification";
				if (attempt >= 2)
				{
					break;
				}
				continue;
			}
			DateTime promptStarted = DateTime.UtcNow;
			bool accepted = false;
			while (DateTime.UtcNow - promptStarted < SquadronCommanderPromptTimeout)
			{
				token.ThrowIfCancellationRequested();
				SquadronCommanderUiStep squadronCommanderUiStep = await RunOnFrameworkThreadAsync((Func<SquadronCommanderUiStep>)TryAdvanceSquadronCommanderUiUnsafe);
				if (squadronCommanderUiStep.Result == SquadronCommanderUiResult.Accepted)
				{
					accepted = true;
					log.Information("[HuntLogs] Accepted the scoped Squadron and Commander prompt: \"" + squadronCommanderUiStep.Prompt + "\".");
					break;
				}
				if (squadronCommanderUiStep.Result == SquadronCommanderUiResult.UnexpectedPrompt)
				{
					lastFailure = "an unexpected confirmation was shown instead of Squadron and Commander: \"" + squadronCommanderUiStep.Prompt + "\"";
					break;
				}
				if (squadronCommanderUiStep.Result == SquadronCommanderUiResult.NormalOfficerMenu)
				{
					log.Information("[HuntLogs] Personnel Officer interaction opened the normal menu; Squadron and Commander was already accepted on this character.");
					if (await CloseGrandCompanyPromotionUiAsync(CancellationToken.None))
					{
						return true;
					}
					lastFailure = "the normal personnel-officer menu proved the unlock was already complete, but the menu did not close cleanly";
					break;
				}
				if (await RunOnFrameworkThreadAsync((Func<bool>)IsSquadronCommanderCutsceneActiveUnsafe))
				{
					accepted = true;
					log.Information("[HuntLogs] Squadron and Commander cutscene became active; the confirmation was accepted externally (for example by YesAlready).");
					break;
				}
				await Task.Delay(250, token);
			}
			if (accepted)
			{
				if (await WaitForSquadronCommanderCutsceneAsync(token))
				{
					log.Information("[HuntLogs] Squadron and Commander follow-up finished; the personnel officer is released.");
					return true;
				}
				lastFailure = $"Squadron and Commander did not finish within {SquadronCommanderCutsceneTimeout.TotalSeconds:F0} seconds";
				break;
			}
			if (string.IsNullOrWhiteSpace(lastFailure))
			{
				lastFailure = "the Squadron and Commander prompt did not appear within 30 seconds";
			}
			await CloseGrandCompanyPromotionUiAsync(CancellationToken.None);
			if (attempt < 2)
			{
				log.Warning($"[HuntLogs] Squadron and Commander attempt {attempt}/{2} failed; retrying the owned officer interaction once. " + lastFailure);
				await Task.Delay(750, token);
			}
		}
		MarkCurrentCharacterStatus("Blocked: could not complete Squadron and Commander because " + lastFailure, markSkipped: true);
		log.Warning("[HuntLogs] Could not complete Squadron and Commander: " + lastFailure + ".");
		return false;
	}

	private async Task<bool> WaitForSquadronCommanderQuestCompletionAsync(uint questRowId, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < SquadronCommanderCompletionTimeout)
		{
			token.ThrowIfCancellationRequested();
			if ((await GetGrandCompanyUnlockQuestStateAsync(questRowId)).Item2)
			{
				log.Information($"[HuntLogs] Native Squadron and Commander completion verified: questRow={questRowId}, questId={NormalizeQuestId(questRowId)}.");
				return true;
			}
			await Task.Delay(250, token);
		}
		return false;
	}

	private async Task<bool> WaitForSquadronCommanderCutsceneAsync(CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		DateTime? clearSince = null;
		while (DateTime.UtcNow - started < SquadronCommanderCutsceneTimeout)
		{
			token.ThrowIfCancellationRequested();
			SquadronCommanderUiStep ui = await RunOnFrameworkThreadAsync((Func<SquadronCommanderUiStep>)TryAdvanceSquadronCommanderUiUnsafe);
			if (ui.Result == SquadronCommanderUiResult.UnexpectedPrompt)
			{
				log.Warning("[HuntLogs] Squadron and Commander showed an unexpected follow-up confirmation: \"" + ui.Prompt + "\".");
				return false;
			}
			if (ui.Result == SquadronCommanderUiResult.NormalOfficerMenu)
			{
				await CloseGrandCompanyPromotionUiAsync(CancellationToken.None);
				return true;
			}
			bool flag = await RunOnFrameworkThreadAsync((Func<bool>)IsGrandCompanyEventOccupiedUnsafe);
			bool flag2 = ui.TalkVisible || ui.YesNoVisible;
			if (DateTime.UtcNow - started >= TimeSpan.FromSeconds(2L) && !flag && !flag2)
			{
				clearSince.GetValueOrDefault();
				if (!clearSince.HasValue)
				{
					DateTime utcNow = DateTime.UtcNow;
					clearSince = utcNow;
				}
				if (DateTime.UtcNow - clearSince.Value >= TimeSpan.FromMilliseconds(1500L))
				{
					return true;
				}
			}
			else
			{
				clearSince = null;
			}
			await Task.Delay(250, token);
		}
		return false;
	}

	private unsafe SquadronCommanderUiStep TryAdvanceSquadronCommanderUiUnsafe()
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectYesno");
		if (addonByName != IntPtr.Zero)
		{
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr != null && ptr->IsVisible)
			{
				if (!ptr->IsReady)
				{
					return new SquadronCommanderUiStep(SquadronCommanderUiResult.Waiting, string.Empty, TalkVisible: false, YesNoVisible: true);
				}
				string text = SelectYesnoTextHandler.GetDialogText(ptr) ?? string.Empty;
				if (!text.Contains("Become a squadron commander", StringComparison.OrdinalIgnoreCase))
				{
					return new SquadronCommanderUiStep(SquadronCommanderUiResult.UnexpectedPrompt, text, TalkVisible: false, YesNoVisible: true);
				}
				return new SquadronCommanderUiStep((SelectYesnoTextHandler.ClickYesButton(ptr) || FireCallback("SelectYesno", 0)) ? SquadronCommanderUiResult.Accepted : SquadronCommanderUiResult.Waiting, text, TalkVisible: false, YesNoVisible: true);
			}
		}
		AtkUnitBasePtr addonByName2 = gameGui.GetAddonByName("Talk");
		if (addonByName2 != IntPtr.Zero)
		{
			AtkUnitBase* ptr2 = (AtkUnitBase*)(nint)addonByName2;
			if (ptr2 != null && ptr2->IsVisible)
			{
				if (ptr2->IsReady)
				{
					new AddonMaster.Talk((nint)addonByName2).Click();
					return new SquadronCommanderUiStep(SquadronCommanderUiResult.TalkAdvanced, string.Empty, TalkVisible: true, YesNoVisible: false);
				}
				return new SquadronCommanderUiStep(SquadronCommanderUiResult.Waiting, string.Empty, TalkVisible: true, YesNoVisible: false);
			}
		}
		AtkUnitBasePtr addonByName3 = gameGui.GetAddonByName("SelectString");
		if (addonByName3 != IntPtr.Zero)
		{
			AtkUnitBase* ptr3 = (AtkUnitBase*)(nint)addonByName3;
			if (ptr3 != null && ptr3->IsVisible && ptr3->IsReady)
			{
				return new SquadronCommanderUiStep(SquadronCommanderUiResult.NormalOfficerMenu, string.Empty, TalkVisible: false, YesNoVisible: false);
			}
		}
		return new SquadronCommanderUiStep(SquadronCommanderUiResult.Waiting, string.Empty, TalkVisible: false, YesNoVisible: false);
	}

	private bool IsGrandCompanyEventOccupiedUnsafe()
	{
		if (!condition[ConditionFlag.Occupied] && !condition[ConditionFlag.Occupied30] && !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.Occupied33] && !condition[ConditionFlag.OccupiedInCutSceneEvent] && !condition[ConditionFlag.Occupied38])
		{
			return condition[ConditionFlag.Occupied39];
		}
		return true;
	}

	private bool IsSquadronCommanderCutsceneActiveUnsafe()
	{
		if (!condition[ConditionFlag.OccupiedInCutSceneEvent] && !condition[ConditionFlag.WatchingCutscene])
		{
			return condition[ConditionFlag.WatchingCutscene78];
		}
		return true;
	}

	private async Task<GrandCompanyOfficerState> MoveIntoGrandCompanyOfficerInteractionRangeAsync((uint TerritoryId, Vector3 Position, uint NpcDataId) officer, int attempt, CancellationToken token)
	{
		string targetDescription = $"personnel officer {officer.NpcDataId}";
		GrandCompanyOfficerState result = await RunOnFrameworkThreadAsync(() => GetGrandCompanyOfficerStateUnsafe(officer.NpcDataId));
		if (!result.Loaded)
		{
			log.Information($"[HuntLogs] Grand Company personnel officer is not loaded; moving to the configured approach point before live reacquisition. Attempt={attempt}/{2}.");
			await MoveToAsync(officer.Position, officer.TerritoryId, fly: false, 2f, token, useCloseTo: true, "Grand Company personnel officer", $"configured approach {officer.Position}");
			if (!(await StopNavigationAndWaitForIdleAsync(officer.TerritoryId, $"Grand Company officer fallback approach attempt {attempt}", token, null, targetDescription)))
			{
				throw new InvalidOperationException("vnavmesh did not remain idle for 750 ms after the officer fallback approach");
			}
			result = await RunOnFrameworkThreadAsync(() => GetGrandCompanyOfficerStateUnsafe(officer.NpcDataId));
			if (!result.Loaded)
			{
				return result;
			}
		}
		float num = Math.Max(0.5f, result.InteractionRange - 0.5f);
		log.Information($"[HuntLogs] Approaching live Grand Company personnel officer position. Attempt={attempt}/{2}, distance={result.Distance:F1}, interactionRange={result.InteractionRange:F1}, approachTolerance={num:F1}, position={result.Position}.");
		await MoveToAsync(result.Position, officer.TerritoryId, fly: false, num, token, useCloseTo: true, "Grand Company personnel officer", $"live officer position {result.Position}");
		if (!(await StopNavigationAndWaitForIdleAsync(officer.TerritoryId, $"Grand Company officer live approach attempt {attempt}", token, null, targetDescription)))
		{
			throw new InvalidOperationException("vnavmesh did not remain idle for 750 ms after the live officer approach");
		}
		return await RunOnFrameworkThreadAsync(() => GetGrandCompanyOfficerStateUnsafe(officer.NpcDataId));
	}

	private async Task<GrandCompanyPromotionSelectionResult> SelectGrandCompanyPromotionOptionAsync(CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < TimeSpan.FromSeconds(15L))
		{
			token.ThrowIfCancellationRequested();
			(SelectStringSelectionResult, SelectStringState, int) tuple = await RunOnFrameworkThreadAsync((Func<(SelectStringSelectionResult, SelectStringState, int)>)TrySelectGrandCompanyPromotionOptionUnsafe);
			if (tuple.Item1 == SelectStringSelectionResult.Selected)
			{
				string value = tuple.Item2?.Options.ElementAtOrDefault(tuple.Item3) ?? "(unknown)";
				log.Information($"[HuntLogs] Selected Grand Company promotion option {tuple.Item3}: \"{value}\".");
				return GrandCompanyPromotionSelectionResult.Selected;
			}
			if (tuple.Item1 == SelectStringSelectionResult.NoMatchingOption)
			{
				string text = FormatSelectStringState(tuple.Item2);
				MarkCurrentCharacterStatus("Blocked: GC promotion option was not present", markSkipped: true);
				log.Warning("[HuntLogs] Grand Company personnel officer menu did not contain a promotion option; " + text + ". Expert Delivery was not selected.");
				return GrandCompanyPromotionSelectionResult.NoMatchingOption;
			}
			await Task.Delay(250, token);
		}
		log.Warning("[HuntLogs] Timed out waiting for the Grand Company promotion SelectString menu.");
		return GrandCompanyPromotionSelectionResult.TimedOut;
	}

	private async Task<bool> CloseGrandCompanyPromotionUiAsync(CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		DateTime? clearSince = null;
		HashSet<string> closedAddons = new HashSet<string>(StringComparer.Ordinal);
		while (DateTime.UtcNow - started < TimeSpan.FromSeconds(8L))
		{
			token.ThrowIfCancellationRequested();
			TimeSpan timeSpan = DateTime.UtcNow - started;
			int selectStringCloseStage = ((!(timeSpan < TimeSpan.FromSeconds(1L))) ? ((timeSpan < TimeSpan.FromSeconds(2L)) ? 1 : 2) : 0);
			(bool, string) tuple = await RunOnFrameworkThreadAsync(() => TryCloseGrandCompanyPromotionUiUnsafe(selectStringCloseStage));
			if (!string.IsNullOrEmpty(tuple.Item2))
			{
				closedAddons.Add(tuple.Item2);
			}
			if (tuple.Item1)
			{
				clearSince = null;
			}
			else
			{
				clearSince.GetValueOrDefault();
				if (!clearSince.HasValue)
				{
					DateTime utcNow = DateTime.UtcNow;
					clearSince = utcNow;
				}
				if (DateTime.UtcNow - clearSince.Value >= TimeSpan.FromMilliseconds(500L))
				{
					if (closedAddons.Count > 0)
					{
						log.Information("[HuntLogs] Closed Grand Company promotion UI: " + string.Join(", ", closedAddons) + ".");
					}
					return true;
				}
			}
			await Task.Delay(200, token);
		}
		IReadOnlyList<string> values = await RunOnFrameworkThreadAsync((Func<IReadOnlyList<string>>)GetVisibleGrandCompanyPromotionAddonsUnsafe);
		log.Warning("[HuntLogs] Timed out closing Grand Company promotion UI; remaining=[" + string.Join(", ", values) + "].");
		return false;
	}

	private unsafe (bool AnyVisible, string? ClosedAddon) TryCloseGrandCompanyPromotionUiUnsafe(int selectStringCloseStage)
	{
		string[] array = new string[3] { "Talk", "GrandCompanyRankUp", "SelectString" };
		foreach (string text in array)
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName(text);
			if (addonByName == IntPtr.Zero)
			{
				continue;
			}
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr == null || !ptr->IsVisible)
			{
				continue;
			}
			if (text == "SelectString")
			{
				if (ptr->IsReady)
				{
					int num = -1;
					if (selectStringCloseStage >= 1)
					{
						SelectStringState selectStringState = ReadSelectStringStateUnsafe(ptr);
						if (selectStringState.Options.Count > 0)
						{
							num = selectStringState.Options.Count - 1;
						}
					}
					AtkValue* ptr2 = stackalloc AtkValue[1];
					*ptr2 = default(AtkValue);
					ptr2->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
					ptr2->Int = num;
					ptr->FireCallback(1u, ptr2);
				}
				if (selectStringCloseStage >= 2)
				{
					ptr->Close(fireCallback: true);
				}
			}
			else
			{
				ptr->Close(fireCallback: true);
			}
			return (AnyVisible: true, ClosedAddon: text);
		}
		return (AnyVisible: false, ClosedAddon: null);
	}

	private unsafe IReadOnlyList<string> GetVisibleGrandCompanyPromotionAddonsUnsafe()
	{
		List<string> list = new List<string>();
		string[] array = new string[3] { "Talk", "GrandCompanyRankUp", "SelectString" };
		foreach (string text in array)
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName(text);
			if (!(addonByName == IntPtr.Zero))
			{
				AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
				if (ptr != null && ptr->IsVisible)
				{
					list.Add(text);
				}
			}
		}
		return list;
	}

	private GrandCompanyOfficerState GetGrandCompanyOfficerStateUnsafe(uint npcDataId)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		IGameObject gameObject = objectTable.FirstOrDefault((IGameObject x) => x.BaseId == npcDataId);
		if (gameObject == null)
		{
			return new GrandCompanyOfficerState(Loaded: false, Targetable: false, InRange: false, float.PositiveInfinity, 0f, Vector3.Zero);
		}
		float num = 2.5f + gameObject.HitboxRadius;
		if (localPlayer == null)
		{
			return new GrandCompanyOfficerState(Loaded: true, gameObject.IsTargetable, InRange: false, float.PositiveInfinity, num, gameObject.Position);
		}
		float num2 = Vector3.Distance(localPlayer.Position, gameObject.Position);
		return new GrandCompanyOfficerState(Loaded: true, gameObject.IsTargetable, gameObject.IsTargetable && num2 <= num, num2, num, gameObject.Position);
	}

	private unsafe bool TryInteractWithGrandCompanyOfficerUnsafe(uint npcDataId)
	{
		GrandCompanyOfficerState grandCompanyOfficerStateUnsafe = GetGrandCompanyOfficerStateUnsafe(npcDataId);
		if (!grandCompanyOfficerStateUnsafe.Loaded || !grandCompanyOfficerStateUnsafe.Targetable || !grandCompanyOfficerStateUnsafe.InRange)
		{
			return false;
		}
		IGameObject gameObject = objectTable.FirstOrDefault((IGameObject x) => x.BaseId == npcDataId && x.IsTargetable);
		TargetSystem* ptr = TargetSystem.Instance();
		if (gameObject == null || gameObject.Address == IntPtr.Zero || ptr == null)
		{
			return false;
		}
		GameObject* address = (GameObject*)gameObject.Address;
		ptr->InteractWithObject(address, checkLineOfSight: false);
		return true;
	}

	private unsafe (SelectStringSelectionResult Result, SelectStringState? State, int SelectedIndex) TrySelectGrandCompanyPromotionOptionUnsafe()
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectString");
		if (addonByName == IntPtr.Zero)
		{
			return (Result: SelectStringSelectionResult.Waiting, State: null, SelectedIndex: -1);
		}
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
		if (ptr == null || !ptr->IsVisible || !ptr->IsReady)
		{
			return (Result: SelectStringSelectionResult.Waiting, State: null, SelectedIndex: -1);
		}
		SelectStringState selectStringState = ReadSelectStringStateUnsafe(ptr);
		int num = -1;
		for (int i = 0; i < selectStringState.Options.Count; i++)
		{
			string text = selectStringState.Options[i];
			if (!text.Contains("Expert Delivery", StringComparison.OrdinalIgnoreCase) && text.Contains("promotion", StringComparison.OrdinalIgnoreCase))
			{
				num = i;
				break;
			}
		}
		if (num < 0)
		{
			return (Result: SelectStringSelectionResult.NoMatchingOption, State: selectStringState, SelectedIndex: -1);
		}
		AtkValue* ptr2 = stackalloc AtkValue[1];
		*ptr2 = default(AtkValue);
		ptr2->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
		ptr2->Int = num;
		ptr->FireCallback(1u, ptr2);
		return (Result: SelectStringSelectionResult.Selected, State: selectStringState, SelectedIndex: num);
	}

	private unsafe static SelectStringState ReadSelectStringStateUnsafe(AtkUnitBase* addon)
	{
		string prompt = ((addon->AtkValuesCount > 2) ? ReadAtkValueString(addon->AtkValues[2]) : string.Empty);
		List<string> list = new List<string>();
		for (int i = 7; i < addon->AtkValuesCount; i++)
		{
			if (addon->AtkValues[i].Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String)
			{
				string text = ReadAtkValueString(addon->AtkValues[i]);
				if (!string.IsNullOrWhiteSpace(text))
				{
					list.Add(text);
				}
			}
		}
		return new SelectStringState(prompt, list);
	}

	private unsafe static string ReadAtkValueString(AtkValue atkValue)
	{
		try
		{
			if (atkValue.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Undefined || !atkValue.String.HasValue)
			{
				return string.Empty;
			}
			return Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated(new IntPtr((byte*)atkValue.String)).TextValue.Replace('\n', ' ').Trim();
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string FormatSelectStringState(SelectStringState? state)
	{
		if (state == null)
		{
			return "prompt=(unavailable), options=[]";
		}
		string value = string.Join(" | ", state.Options.Select((string option, int index) => $"{index}:\"{option}\""));
		return $"prompt=\"{state.Prompt}\", options=[{value}]";
	}

	private static (uint TerritoryId, Vector3 Position, uint NpcDataId)? GetGrandCompanyOfficer(uint grandCompanyId)
	{
		return grandCompanyId switch
		{
			1u => (128u, new Vector3(93f, 40f, 74f), 1002388u), 
			2u => (132u, new Vector3(-68f, -0.5f, -7f), 1002394u), 
			3u => (130u, new Vector3(-142f, 4f, -105f), 1002391u), 
			_ => null, 
		};
	}

	private static uint GetSquadronCommanderQuestRowId(uint grandCompanyId)
	{
		return grandCompanyId switch
		{
			1u => 67926u, 
			2u => 67925u, 
			3u => 67927u, 
			_ => 0u, 
		};
	}

	private static bool IsStarterCityTerritory(uint territoryId)
	{
		switch (territoryId)
		{
		case 128u:
		case 130u:
		case 132u:
			return true;
		default:
			return false;
		}
	}

	private static IEnumerable<uint> GetCharacterSwitchCityTerritories(uint grandCompanyId)
	{
		uint preferred = grandCompanyId switch
		{
			1u => 128u, 
			2u => 132u, 
			3u => 130u, 
			_ => 0u, 
		};
		if (preferred != 0)
		{
			yield return preferred;
		}
		uint[] array = new uint[3] { 128u, 132u, 130u };
		foreach (uint num in array)
		{
			if (num != preferred)
			{
				yield return num;
			}
		}
	}

	private int GetStopGrandCompanyRank()
	{
		return Math.Clamp(configuration.HuntLogs.StopAfterGrandCompanyRank, 1, 11);
	}

	private async Task<bool> WaitForGrandCompanyRankIncreaseAsync(string character, int previousRank, CancellationToken token)
	{
		bool changed = await WaitUntilFrameworkAsync(() => GetPlayerInfoUnsafe().GrandCompanyRank > previousRank, "Grand Company rank refresh", TimeSpan.FromSeconds(10L), token);
		(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) refreshedPlayer = await GetPlayerInfoAsync();
		uint num = await GetGrandCompanyMonsterNoteIdAsync(refreshedPlayer.GrandCompanyId);
		bool flag = ((num == 0 || num == 127) ? true : false);
		int num2 = ((!flag) ? (await GetMonsterNoteRankAsync((int)num)) : 0);
		int num3 = num2;
		UpdateCharacterSnapshot(character, refreshedPlayer, null, num3, null, null, HuntLogCompletionProvenance.SuccessfulPromotion);
		if (changed)
		{
			log.Information($"[HuntLogs] Grand Company rank refreshed after promotion: {previousRank} -> {refreshedPlayer.GrandCompanyRank}, log rank {num3 + 1}.");
			return true;
		}
		MarkCurrentCharacterStatus($"Blocked: GC promotion did not update rank from {previousRank}", markSkipped: true);
		log.Warning($"[HuntLogs] GC promotion callback completed, but rank remained {refreshedPlayer.GrandCompanyRank}.");
		return false;
	}

	private async Task<(uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank)> GetPlayerInfoAsync()
	{
		return await RunOnFrameworkThreadAsync((Func<(uint, int, uint, int)>)GetPlayerInfoUnsafe);
	}

	private unsafe static (uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) GetPlayerInfoUnsafe()
	{
		PlayerState* ptr = PlayerState.Instance();
		if (ptr == null || !ptr->IsLoaded)
		{
			return (ClassJobId: 0u, Level: 0, GrandCompanyId: 0u, GrandCompanyRank: -1);
		}
		byte grandCompany = ptr->GrandCompany;
		int item = grandCompany switch
		{
			1 => ptr->GCRanks[0], 
			2 => ptr->GCRanks[1], 
			3 => ptr->GCRanks[2], 
			_ => -1, 
		};
		return (ClassJobId: ptr->CurrentClassJobId, Level: ptr->CurrentLevel, GrandCompanyId: grandCompany, GrandCompanyRank: item);
	}

	private async Task<uint> GetCurrentClassMonsterNoteIdAsync(uint classJobId)
	{
		return await RunOnFrameworkThreadAsync(() => dataManager.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var row) ? row.MonsterNote.RowId : 0u);
	}

	private async Task<uint> GetGrandCompanyMonsterNoteIdAsync(uint grandCompanyId)
	{
		return await RunOnFrameworkThreadAsync(() => dataManager.GetExcelSheet<GrandCompany>().TryGetRow(grandCompanyId, out var row) ? row.MonsterNote.RowId : 0u);
	}

	private unsafe async Task<int> GetMonsterNoteRankAsync(int monsterNoteId)
	{
		return await RunOnFrameworkThreadAsync(delegate
		{
			MonsterNoteManager* ptr = MonsterNoteManager.Instance();
			return (ptr != null) ? ptr->RankData[monsterNoteId].Rank : 0;
		});
	}

	private async Task<int> GetOpenMonsterNoteKillsAsync(HuntMark mark)
	{
		return await RunOnFrameworkThreadAsync(() => GetOpenMonsterNoteKillsUnsafe(mark));
	}

	private unsafe static int GetOpenMonsterNoteKillsUnsafe(HuntMark mark)
	{
		try
		{
			MonsterNoteManager* ptr = MonsterNoteManager.Instance();
			if (ptr == null)
			{
				return mark.NeededKills;
			}
			byte b = ptr->RankData[mark.MonsterNoteId].RankData[mark.MonsterNoteSubRank].Counts[mark.MonsterNoteCount];
			return Math.Max(mark.NeededKills - b, 0);
		}
		catch
		{
			return mark.NeededKills;
		}
	}

	private async Task<bool> IsUnconsciousAsync()
	{
		return await RunOnFrameworkThreadAsync((Func<bool>)IsDeadOrUnconsciousUnsafe);
	}

	private bool IsDeadOrUnconsciousUnsafe()
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (!condition[ConditionFlag.Unconscious] && (localPlayer == null || localPlayer.CurrentHp != 0))
		{
			return localPlayer?.IsDead ?? false;
		}
		return true;
	}

	private unsafe bool FireCallback(string addonName, params object[] args)
	{
		try
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName(addonName);
			if (addonByName == IntPtr.Zero)
			{
				return false;
			}
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr == null || !ptr->IsVisible)
			{
				return false;
			}
			AtkValue* ptr2 = stackalloc AtkValue[args.Length];
			for (int i = 0; i < args.Length; i++)
			{
				ptr2[i] = default(AtkValue);
				object obj = args[i];
				if (!(obj is bool flag))
				{
					if (!(obj is uint uInt))
					{
						if (obj is int num)
						{
							ptr2[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
							ptr2[i].Int = num;
						}
						else
						{
							ptr2[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
							ptr2[i].Int = 0;
						}
					}
					else
					{
						ptr2[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt;
						ptr2[i].UInt = uInt;
					}
				}
				else
				{
					ptr2[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool;
					ptr2[i].Byte = (flag ? ((byte)1) : ((byte)0));
				}
			}
			ptr->FireCallback((uint)args.Length, ptr2);
			return true;
		}
		catch (Exception ex)
		{
			log.Debug("[HuntLogs] FireCallback(" + addonName + ") failed: " + ex.Message);
			return false;
		}
	}

	private async Task CleanupAsync(bool drainCombat = false, CancellationToken token = default(CancellationToken))
	{
		huntDutyRunner.StopOwnedSession("hunt-log cleanup");
		try
		{
			await StopNavigationAndWaitForIdleAsync(await RunOnFrameworkThreadAsync(() => clientState.TerritoryType), "hunt-log cleanup", token, null, "cleanup");
			database.ResetCurrentTarget();
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Cleanup pre-stop failed: " + ex.Message);
		}
		Exception drainException = null;
		bool flag = drainCombat;
		if (flag)
		{
			flag = await RunOnFrameworkThreadAsync(() => clientState.IsLoggedIn);
		}
		if (flag)
		{
			try
			{
				await ResolveCombatIfNeededAsync("hunt-log cleanup", await RunOnFrameworkThreadAsync(() => clientState.TerritoryType), token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				drainException = new OperationCanceledException(token);
			}
			catch (Exception ex3)
			{
				log.Warning("[HuntLogs] Combat drain during cleanup failed: " + ex3.Message);
				if (token.CanBeCanceled)
				{
					drainException = ex3;
				}
			}
		}
		try
		{
			await DisableCombatAsync();
			await ResetTargetAsync();
		}
		catch (Exception ex4)
		{
			log.Warning("[HuntLogs] Cleanup failed: " + ex4.Message);
		}
		finally
		{
			deathHandler?.SetSuppressedByHuntLogs(suppressed: false);
		}
		if (drainException != null)
		{
			throw drainException;
		}
	}

	private HuntLogRunCheckpoint? GetResumeCheckpoint(HuntLogMode mode, List<string> selectedCharacters)
	{
		HuntLogRunCheckpoint currentCheckpoint = configuration.HuntLogs.CurrentCheckpoint;
		if (!configuration.HuntLogs.ResumeIncompleteRuns || currentCheckpoint == null || !currentCheckpoint.IsActive || currentCheckpoint.Mode != mode || !SameCharacterSet(currentCheckpoint.SelectedCharacters, selectedCharacters))
		{
			return null;
		}
		return currentCheckpoint;
	}

	private List<string> GetResumeCompletedCharacters(HuntLogMode mode, List<string> selectedCharacters)
	{
		List<string> list = GetResumeCheckpoint(mode, selectedCharacters)?.CompletedCharacters.Where((string x) => selectedCharacters.Contains<string>(x, StringComparer.OrdinalIgnoreCase)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
		if (!RequiresGrandCompanyLogs(mode))
		{
			return list;
		}
		if (list.Count > 0)
		{
			log.Information("[HuntLogs] Ignoring stored GC checkpoint completions; live inspection is required for every selected character.");
		}
		return new List<string>();
	}

	private Dictionary<string, string> GetPreflightCompletedCharacters(HuntLogMode mode, IReadOnlyList<string> selectedCharacters, IReadOnlyCollection<string> alreadyCompleted)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string selectedCharacter in selectedCharacters)
		{
			if (!alreadyCompleted.Contains<string>(selectedCharacter, StringComparer.OrdinalIgnoreCase) && IsCharacterCompleteByPreflight(selectedCharacter, mode, out string reason))
			{
				dictionary[selectedCharacter] = reason;
			}
		}
		return dictionary;
	}

	private bool IsCharacterCompleteByPreflight(string character, HuntLogMode mode, out string reason)
	{
		reason = string.Empty;
		bool flag = ((mode == HuntLogMode.Class || mode == HuntLogMode.All) ? true : false);
		bool flag2 = flag;
		flag = (uint)(mode - 1) <= 1u;
		bool flag3 = flag;
		string text = null;
		string text2 = null;
		string reason2 = string.Empty;
		string reason3 = string.Empty;
		if (flag2 && !IsClassHuntLogCompleteBySnapshot(character, out reason2))
		{
			reason = reason2;
			return false;
		}
		if (flag2)
		{
			text = reason2;
		}
		if (flag3 && !IsGrandCompanyHuntLogCompleteByPreflight(character, out reason3))
		{
			reason = reason3;
			return false;
		}
		if (flag3)
		{
			text2 = reason3;
		}
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(text))
		{
			list.Add(text);
		}
		if (!string.IsNullOrWhiteSpace(text2))
		{
			list.Add(text2);
		}
		reason = string.Join("; ", list.Where((string x) => !string.IsNullOrWhiteSpace(x)));
		return true;
	}

	private bool IsClassHuntLogCompleteBySnapshot(string character, out string reason)
	{
		int num = Math.Clamp(configuration.HuntLogs.StopAfterClassRank, 1, 5);
		if (configuration.HuntLogs.CharacterSnapshots.TryGetValue(character, out HuntLogCharacterSnapshot value) && value.ClassLogRank >= num)
		{
			reason = $"class log rank {value.ClassLogRank} >= stop rank {num}";
			return true;
		}
		reason = "class log requires live check";
		return false;
	}

	private bool IsGrandCompanyHuntLogCompleteByPreflight(string character, out string reason)
	{
		int num = (from x in GetAdvisoryGrandCompanyRanks(character)
			select x.Rank).Where(IsPlausibleGrandCompanyRank).DefaultIfEmpty(0).Max();
		reason = ((num == 0) ? "GC rank requires live inspection" : $"stored GC rank {num} requires live inspection");
		return false;
	}

	private static bool RequiresGrandCompanyLogs(HuntLogMode mode)
	{
		if ((uint)(mode - 1) <= 1u)
		{
			return true;
		}
		return false;
	}

	private List<(string Source, int Rank)> GetAdvisoryGrandCompanyRanks(string character)
	{
		List<(string, int)> list = new List<(string, int)>();
		if (configuration.HuntLogs.CharacterSnapshots.TryGetValue(character, out HuntLogCharacterSnapshot value))
		{
			list.Add(("hunt snapshot", value.GrandCompanyRank));
		}
		if (configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot value2))
		{
			list.Add(("job snapshot", value2.GrandCompanyRank));
		}
		list.Add(("AutoRetainer", autoRetainerIpc.GetGrandCompanyRank(character)));
		return list;
	}

	private static bool TryFindInvalidGrandCompanyRank(IEnumerable<(string Source, int Rank)> ranks, out int invalidRank, out string invalidSource)
	{
		foreach (var (text, num) in ranks)
		{
			if (IsInvalidGrandCompanyRank(num))
			{
				invalidRank = num;
				invalidSource = text;
				return true;
			}
		}
		invalidRank = 0;
		invalidSource = string.Empty;
		return false;
	}

	private static bool IsInvalidGrandCompanyRank(int rank)
	{
		return rank > 11;
	}

	private static bool IsPlausibleGrandCompanyRank(int rank)
	{
		if (rank > 0)
		{
			return !IsInvalidGrandCompanyRank(rank);
		}
		return false;
	}

	private static bool IsTrustedGrandCompanyCompletionProvenance(HuntLogCompletionProvenance provenance)
	{
		if ((uint)(provenance - 2) <= 1u)
		{
			return true;
		}
		return false;
	}

	private HuntLogCompletionProvenance GetTrustedPreflightCompletionProvenance(string character, HuntLogMode mode)
	{
		if (!RequiresGrandCompanyLogs(mode))
		{
			return HuntLogCompletionProvenance.LiveInspection;
		}
		if (!configuration.HuntLogs.CharacterSnapshots.TryGetValue(character, out HuntLogCharacterSnapshot value) || !IsTrustedGrandCompanyCompletionProvenance(value.GrandCompanyRankProvenance))
		{
			return HuntLogCompletionProvenance.AdvisoryPreflight;
		}
		return value.GrandCompanyRankProvenance;
	}

	private HuntLogCompletionProvenance GetLiveCompletionProvenance(string character, HuntLogMode mode)
	{
		if (!RequiresGrandCompanyLogs(mode))
		{
			return HuntLogCompletionProvenance.LiveInspection;
		}
		if (!configuration.HuntLogs.CharacterSnapshots.TryGetValue(character, out HuntLogCharacterSnapshot value) || value.GrandCompanyRankProvenance != HuntLogCompletionProvenance.SuccessfulPromotion)
		{
			return HuntLogCompletionProvenance.LiveInspection;
		}
		return HuntLogCompletionProvenance.SuccessfulPromotion;
	}

	private bool RequiresLiveGrandCompanyInspection(string character, HuntLogMode mode, HuntLogRunCheckpoint? checkpoint)
	{
		if (!RequiresGrandCompanyLogs(mode))
		{
			return false;
		}
		return true;
	}

	private static bool SameCharacterSet(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
	{
		if (left.Count == right.Count)
		{
			return left.All((string x) => right.Contains<string>(x, StringComparer.OrdinalIgnoreCase));
		}
		return false;
	}

	private void SaveCheckpointFromState(bool active, bool completed = false, string? lastError = null, bool forceSave = false)
	{
		HuntLogAutomationState huntLogAutomationState;
		lock (stateLock)
		{
			huntLogAutomationState = state.Clone();
		}
		DateTime utcNow = DateTime.UtcNow;
		configuration.HuntLogs.CurrentCheckpoint = new HuntLogRunCheckpoint
		{
			IsActive = active,
			Mode = huntLogAutomationState.Mode,
			SelectedCharacters = new List<string>(huntLogAutomationState.SelectedCharacters),
			CompletedCharacters = new List<string>(huntLogAutomationState.CompletedCharacters.Distinct<string>(StringComparer.OrdinalIgnoreCase)),
			CompletionProvenance = new Dictionary<string, HuntLogCompletionProvenance>(huntLogAutomationState.CompletionProvenance, StringComparer.OrdinalIgnoreCase),
			SkippedCharacters = new List<string>(huntLogAutomationState.SkippedCharacters.Distinct<string>(StringComparer.OrdinalIgnoreCase)),
			FailedCharacters = new List<string>(huntLogAutomationState.FailedCharacters.Distinct<string>(StringComparer.OrdinalIgnoreCase)),
			PendingMarks = huntLogAutomationState.PendingMarks.ConvertAll((HuntLogPendingMark x) => x.Clone()),
			CurrentCharacter = huntLogAutomationState.CurrentCharacter,
			LastError = (lastError ?? huntLogAutomationState.ErrorMessage),
			StartedAtUtc = huntLogAutomationState.StartedAtUtc,
			UpdatedAtUtc = utcNow,
			CompletedAtUtc = (completed ? utcNow : DateTime.MinValue)
		};
		SaveConfiguration(forceSave);
	}

	private void SaveConfiguration(bool force = false)
	{
		lock (configurationSaveLock)
		{
			DateTime utcNow = DateTime.UtcNow;
			if (!force && utcNow - lastConfigurationSaveUtc < ConfigurationSaveMinimumInterval)
			{
				configurationSavePending = true;
				return;
			}
			configuration.Save();
			lastConfigurationSaveUtc = utcNow;
			configurationSavePending = false;
		}
	}

	private void FlushConfigurationSave()
	{
		lock (configurationSaveLock)
		{
			if (configurationSavePending)
			{
				configuration.Save();
				lastConfigurationSaveUtc = DateTime.UtcNow;
				configurationSavePending = false;
			}
		}
	}

	private List<string> GetCurrentIncompleteCharacters()
	{
		lock (stateLock)
		{
			return state.RemainingCharacters.Where((string x) => !state.CompletedCharacters.Contains<string>(x, StringComparer.OrdinalIgnoreCase)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		}
	}

	private async Task RefreshResumePendingMarkCountsAsync(string character, CancellationToken token)
	{
		List<HuntLogPendingMark> list = (from x in GetCurrentState().PendingMarks
			where string.Equals(x.CharacterName, character, StringComparison.OrdinalIgnoreCase)
			select x.Clone()).ToList();
		if (list.Count == 0)
		{
			return;
		}
		List<(HuntLogPendingMark Pending, int Remaining)> refreshed = new List<(HuntLogPendingMark, int)>();
		foreach (HuntLogPendingMark item3 in list)
		{
			token.ThrowIfCancellationRequested();
			HuntMark huntMark = ResolvePendingMark(item3);
			if (huntMark != null)
			{
				List<(HuntLogPendingMark Pending, int Remaining)> list2 = refreshed;
				HuntLogPendingMark item = item3;
				list2.Add((item, await GetOpenMonsterNoteKillsAsync(huntMark)));
			}
		}
		if (refreshed.Count == 0)
		{
			return;
		}
		int removed = 0;
		int updated = 0;
		UpdateState(delegate(HuntLogAutomationState s)
		{
			foreach (var item4 in refreshed)
			{
				HuntLogPendingMark pending = item4.Pending;
				int item2 = item4.Remaining;
				int num = s.PendingMarks.FindIndex((HuntLogPendingMark x) => IsSamePendingMark(x, pending));
				if (num >= 0)
				{
					if (item2 <= 0)
					{
						s.PendingMarks.RemoveAt(num);
						removed++;
					}
					else
					{
						HuntLogPendingMark huntLogPendingMark = s.PendingMarks[num];
						if (item2 < huntLogPendingMark.RemainingKills)
						{
							huntLogPendingMark.ConsecutiveNoProgressScans = 0;
							huntLogPendingMark.Deferred = false;
						}
						if (huntLogPendingMark.RemainingKills != item2)
						{
							updated++;
						}
						huntLogPendingMark.RemainingKills = item2;
					}
				}
			}
		});
		if (removed != 0 || updated != 0)
		{
			SaveCheckpointFromState(active: true);
			log.Information($"[HuntLogs] Refreshed resume pending marks for {character}: removedCompleted={removed}, updatedCounts={updated}.");
		}
	}

	private HuntMark? ResolvePendingMark(HuntLogPendingMark pending)
	{
		List<HuntMark> source = (from x in (pending.IsGrandCompanyLog ? database.GrandCompanyHuntRanks.Values : database.ClassHuntRanks.Values).SelectMany(EnumerateHuntLogMarks)
			where IsSamePendingMark(pending, x)
			select x).ToList();
		return source.FirstOrDefault((HuntMark x) => x.TerritoryId == pending.TerritoryId) ?? source.FirstOrDefault();
	}

	private static IEnumerable<HuntMark> EnumerateHuntLogMarks(HuntLog huntLog)
	{
		for (int row = 0; row < huntLog.HuntMarks.GetLength(0); row++)
		{
			for (int col = 0; col < huntLog.HuntMarks.GetLength(1); col++)
			{
				HuntMark huntMark = huntLog.HuntMarks[row, col];
				if (huntMark != null)
				{
					yield return huntMark;
				}
			}
		}
	}

	private static HuntLogPendingMark CreatePendingMarkCheckpoint(HuntMark mark, PendingMarkContext context, int remainingKills)
	{
		return new HuntLogPendingMark
		{
			CharacterName = context.Character,
			IsGrandCompanyLog = context.IsGrandCompanyLog,
			Rank = context.Rank,
			BNpcNameRowId = mark.BNpcNameRowId,
			TerritoryId = mark.TerritoryId,
			MonsterNoteId = mark.MonsterNoteId,
			MonsterNoteSubRank = mark.MonsterNoteSubRank,
			MonsterNoteCount = mark.MonsterNoteCount,
			RemainingKills = remainingKills
		};
	}

	private static bool IsSamePendingMarkContext(HuntLogPendingMark pending, PendingMarkContext context)
	{
		if (string.Equals(pending.CharacterName, context.Character, StringComparison.OrdinalIgnoreCase) && pending.IsGrandCompanyLog == context.IsGrandCompanyLog)
		{
			return pending.Rank == context.Rank;
		}
		return false;
	}

	private static bool IsSamePendingMark(HuntLogPendingMark pending, HuntMark mark)
	{
		if (pending.BNpcNameRowId == mark.BNpcNameRowId && pending.MonsterNoteId == mark.MonsterNoteId && pending.MonsterNoteSubRank == mark.MonsterNoteSubRank)
		{
			return pending.MonsterNoteCount == mark.MonsterNoteCount;
		}
		return false;
	}

	private static bool IsSamePendingMark(HuntLogPendingMark left, HuntLogPendingMark right)
	{
		if (IsSamePendingMarkContext(left, new PendingMarkContext(right.CharacterName, right.IsGrandCompanyLog, right.Rank)) && left.BNpcNameRowId == right.BNpcNameRowId && left.TerritoryId == right.TerritoryId && left.MonsterNoteId == right.MonsterNoteId && left.MonsterNoteSubRank == right.MonsterNoteSubRank)
		{
			return left.MonsterNoteCount == right.MonsterNoteCount;
		}
		return false;
	}

	private async Task RefreshPendingMarkCountsAsync(List<PendingMarkWorkItem> workItems)
	{
		foreach (PendingMarkWorkItem item in workItems)
		{
			int num = await GetOpenMonsterNoteKillsAsync(item.Mark);
			if (num < item.Checkpoint.RemainingKills)
			{
				item.Checkpoint.ConsecutiveNoProgressScans = 0;
				item.Checkpoint.Deferred = false;
			}
			item.Checkpoint.RemainingKills = Math.Max(0, num);
		}
	}

	private void PersistPendingMarkQueue(List<PendingMarkWorkItem> workItems, PendingMarkContext context)
	{
		List<HuntLogPendingMark> pending = (from x in workItems
			where x.Checkpoint.RemainingKills > 0
			select x.Checkpoint.Clone()).ToList();
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.PendingMarks.RemoveAll((HuntLogPendingMark x) => IsSamePendingMarkContext(x, context));
			s.PendingMarks.AddRange(pending);
		});
		SaveCheckpointFromState(active: true);
	}

	private void UpdateCharacterSnapshot(string character, (uint ClassJobId, int Level, uint GrandCompanyId, int GrandCompanyRank) player, int? classLogRank, int? grandCompanyLogRank, uint? selectedCombatJobId = null, int? selectedCombatGearsetId = null, HuntLogCompletionProvenance grandCompanyRankProvenance = HuntLogCompletionProvenance.LiveInspection)
	{
		if (!string.IsNullOrWhiteSpace(character))
		{
			if (!configuration.HuntLogs.CharacterSnapshots.TryGetValue(character, out HuntLogCharacterSnapshot value))
			{
				value = new HuntLogCharacterSnapshot
				{
					CharacterName = character
				};
				configuration.HuntLogs.CharacterSnapshots[character] = value;
			}
			(value.ClassJobId, _, _, _) = player;
			if (selectedCombatJobId.HasValue)
			{
				value.SelectedCombatJobId = selectedCombatJobId.Value;
			}
			if (selectedCombatGearsetId.HasValue)
			{
				value.SelectedCombatGearsetId = selectedCombatGearsetId.Value;
			}
			value.Level = player.Level;
			value.GrandCompanyId = player.GrandCompanyId;
			if (grandCompanyRankProvenance == HuntLogCompletionProvenance.LiveInspection && value.GrandCompanyRankProvenance == HuntLogCompletionProvenance.SuccessfulPromotion && value.GrandCompanyRank == player.GrandCompanyRank)
			{
				grandCompanyRankProvenance = HuntLogCompletionProvenance.SuccessfulPromotion;
			}
			value.GrandCompanyRank = player.GrandCompanyRank;
			value.GrandCompanyRankProvenance = grandCompanyRankProvenance;
			if (classLogRank.HasValue)
			{
				value.ClassLogRank = classLogRank.Value;
			}
			if (grandCompanyLogRank.HasValue)
			{
				value.GrandCompanyLogRank = grandCompanyLogRank.Value;
			}
			value.LastUpdatedUtc = DateTime.UtcNow;
			if (configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot value2))
			{
				value2.GrandCompanyId = player.GrandCompanyId;
				value2.GrandCompanyRank = player.GrandCompanyRank;
				value2.LastUpdatedUtc = value.LastUpdatedUtc;
			}
			SaveConfiguration();
		}
	}

	private void ReconcileAdvisoryGrandCompanyRankWithLive(string character, int liveRank)
	{
		if (IsPlausibleGrandCompanyRank(liveRank))
		{
			int stopGrandCompanyRank = GetStopGrandCompanyRank();
			HuntLogRunCheckpoint currentCheckpoint = configuration.HuntLogs.CurrentCheckpoint;
			bool num = currentCheckpoint.CompletedCharacters.Contains<string>(character, StringComparer.OrdinalIgnoreCase);
			HuntLogCompletionProvenance value;
			HuntLogCompletionProvenance checkpointProvenance = (currentCheckpoint.CompletionProvenance.TryGetValue(character, out value) ? value : HuntLogCompletionProvenance.Unknown);
			int num2 = (from x in GetAdvisoryGrandCompanyRanks(character)
				select x.Rank).Where(IsPlausibleGrandCompanyRank).DefaultIfEmpty(0).Max();
			if (((num && !IsTrustedGrandCompanyCompletionProvenance(checkpointProvenance)) || num2 >= stopGrandCompanyRank) && liveRank < stopGrandCompanyRank && num2 != liveRank)
			{
				log.Information($"[HuntLogs] Invalidated advisory-only completion for {character}: checkpoint rank={num2}, live rank={liveRank}.");
			}
			currentCheckpoint.CompletedCharacters.RemoveAll((string x) => !IsTrustedGrandCompanyCompletionProvenance(checkpointProvenance) && string.Equals(x, character, StringComparison.OrdinalIgnoreCase));
			if (!IsTrustedGrandCompanyCompletionProvenance(checkpointProvenance))
			{
				currentCheckpoint.CompletionProvenance.Remove(character);
			}
		}
	}

	private void MarkCurrentCharacterStatus(string status, bool markSkipped = false)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			if (!string.IsNullOrWhiteSpace(s.CurrentCharacter))
			{
				s.CharacterStatuses[s.CurrentCharacter] = status;
				if (markSkipped && !s.SkippedCharacters.Contains<string>(s.CurrentCharacter, StringComparer.OrdinalIgnoreCase))
				{
					s.SkippedCharacters.Add(s.CurrentCharacter);
				}
			}
		});
		SaveCheckpointFromState(active: true);
	}

	private async Task WaitForTravelSettledAsync(string description, TimeSpan timeout, CancellationToken token)
	{
		if (!(await TryWaitForTravelSettledAsync(description, timeout, token)))
		{
			throw new TimeoutException("Timed out waiting for travel to settle: " + description + ".");
		}
	}

	private async Task<bool> TryWaitForTravelSettledAsync(string description, TimeSpan timeout, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		DateTime? stableSince = null;
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			if (await RunOnFrameworkThreadAsync(() => clientState.IsLoggedIn && objectTable.LocalPlayer != null && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && !condition[ConditionFlag.LoggingOut] && !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Casting] && !lifestreamIpc.IsBusy()))
			{
				stableSince.GetValueOrDefault();
				if (!stableSince.HasValue)
				{
					DateTime utcNow = DateTime.UtcNow;
					stableSince = utcNow;
				}
				if (DateTime.UtcNow - stableSince.Value >= TimeSpan.FromMilliseconds(1500L))
				{
					return true;
				}
			}
			else
			{
				stableSince = null;
			}
			await Task.Delay(250, token);
		}
		log.Warning("[HuntLogs] Timed out waiting for travel to settle: " + description + ".");
		return false;
	}

	private async Task<bool> WaitForTravelStartAsync(string description, TimeSpan timeout, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			if (await RunOnFrameworkThreadAsync(() => condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.Casting] || condition[ConditionFlag.LoggingOut] || lifestreamIpc.IsBusy()))
			{
				return true;
			}
			await Task.Delay(250, token);
		}
		log.Information("[HuntLogs] No travel start observed for " + description + "; assuming destination was already valid or command settled immediately.");
		return false;
	}

	private async Task<bool> WaitUntilAsync(Func<bool> predicate, string description, TimeSpan timeout, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			if (predicate())
			{
				return true;
			}
			await Task.Delay(250, token);
		}
		log.Warning("[HuntLogs] Timed out waiting for " + description);
		return false;
	}

	private async Task<bool> WaitUntilFrameworkAsync(Func<bool> predicate, string description, TimeSpan timeout, CancellationToken token)
	{
		DateTime started = DateTime.UtcNow;
		while (DateTime.UtcNow - started < timeout)
		{
			token.ThrowIfCancellationRequested();
			if (await RunOnFrameworkThreadAsync(predicate))
			{
				return true;
			}
			await Task.Delay(250, token);
		}
		log.Warning("[HuntLogs] Timed out waiting for " + description);
		return false;
	}

	private async Task RunOnFrameworkThreadAsync(System.Action action)
	{
		Exception exception = null;
		await framework.RunOnFrameworkThread(delegate
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				exception = ex;
			}
		});
		if (exception != null)
		{
			throw exception;
		}
	}

	private async Task<T> RunOnFrameworkThreadAsync<T>(Func<T> func)
	{
		T result = default(T);
		Exception exception = null;
		await framework.RunOnFrameworkThread(delegate
		{
			try
			{
				result = func();
			}
			catch (Exception ex)
			{
				exception = ex;
			}
		});
		if (exception != null)
		{
			throw exception;
		}
		return result;
	}

	private void SetError(string message)
	{
		UpdateState(delegate(HuntLogAutomationState s)
		{
			s.Phase = HuntLogPhase.Error;
			s.ErrorMessage = message;
			s.CurrentStep = message;
		});
		log.Warning("[HuntLogs] " + message);
	}

	private void UpdateState(Action<HuntLogAutomationState> update)
	{
		lock (stateLock)
		{
			update(state);
			stateVersion++;
		}
		LogMemoryDiagnosticsIfDue();
	}

	private void LogMemoryDiagnosticsIfDue()
	{
		long ticks = DateTime.UtcNow.Ticks;
		long num = Volatile.Read(in nextMemoryDiagnosticUtcTicks);
		if (ticks < num)
		{
			return;
		}
		long value = ticks + MemoryDiagnosticInterval.Ticks;
		if (Interlocked.CompareExchange(ref nextMemoryDiagnosticUtcTicks, value, num) != num)
		{
			return;
		}
		try
		{
			GCMemoryInfo gCMemoryInfo = GC.GetGCMemoryInfo();
			using Process process = Process.GetCurrentProcess();
			log.Information("[HuntLogs] Memory diagnostic: " + $"managedHeap={(double)gCMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0:F1} MB, " + $"managedCommitted={(double)gCMemoryInfo.TotalCommittedBytes / 1024.0 / 1024.0:F1} MB, " + $"managedFragmented={(double)gCMemoryInfo.FragmentedBytes / 1024.0 / 1024.0:F1} MB, " + $"totalManagedAllocated={(double)GC.GetTotalAllocatedBytes() / 1024.0 / 1024.0:F1} MB, " + $"workingSet={(double)process.WorkingSet64 / 1024.0 / 1024.0:F1} MB, " + $"privateBytes={(double)process.PrivateMemorySize64 / 1024.0 / 1024.0:F1} MB.");
		}
		catch (Exception ex)
		{
			log.Debug("[HuntLogs] Memory diagnostic unavailable: " + ex.Message);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) == 0)
		{
			framework.Update -= OnFrameworkUpdate;
			Stop();
			CancellationTokenSource cancellationTokenSource = this.cancellationTokenSource;
			this.cancellationTokenSource = null;
			cancellationTokenSource?.Cancel();
			Task task = runnerTask;
			runnerTask = null;
			if (task == null || task.IsCompleted)
			{
				ObserveCompletedTask(task);
				cancellationTokenSource?.Dispose();
			}
			else
			{
				ObserveShutdownAsync(task, cancellationTokenSource);
			}
			FlushConfigurationSave();
		}
	}

	private void ObserveCompletedTask(Task? task)
	{
		if (task != null && task.IsFaulted)
		{
			log.Debug(task.Exception, "[HuntLogs] Runner ended with an error during shutdown.");
		}
	}

	private async Task ObserveShutdownAsync(Task task, CancellationTokenSource? source)
	{
		try
		{
			await task.ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			log.Debug(exception, "[HuntLogs] Runner ended with an error during shutdown.");
		}
		finally
		{
			source?.Dispose();
		}
	}
}
