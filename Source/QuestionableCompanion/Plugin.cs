using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Data.HuntLogs;
using QuestionableCompanion.Models;
using QuestionableCompanion.Services;
using QuestionableCompanion.Windows;

namespace QuestionableCompanion;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
	private const string CommandName = "/qstcomp";

	private bool eCommonsInitialized;

	private int disposeStarted;

	private static Plugin? instance;

	private readonly List<Window> windows = new List<Window>();

	private readonly Dictionary<Window, bool> windowOpenStates = new Dictionary<Window, bool>();

	private DateTime lastChauffeurCheck = DateTime.MinValue;

	private bool autoStartAttempted;

	private DateTime autoStartCheckTime = DateTime.MinValue;

	[PluginService]
	internal static IDalamudPluginInterface PluginInterface { get; private set; }

	[PluginService]
	internal static ITextureProvider TextureProvider { get; private set; }

	[PluginService]
	internal static ICommandManager CommandManager { get; private set; }

	[PluginService]
	internal static IClientState ClientState { get; private set; }

	[PluginService]
	internal static IDataManager DataManager { get; private set; }

	[PluginService]
	internal static IPluginLog Log { get; private set; }

	[PluginService]
	internal static IFramework Framework { get; private set; }

	[PluginService]
	internal static IGameGui GameGui { get; private set; }

	[PluginService]
	internal static ICondition Condition { get; private set; }

	[PluginService]
	internal static IPartyList PartyList { get; private set; }

	[PluginService]
	internal static IGameInteropProvider GameInterop { get; private set; }

	[PluginService]
	internal static IObjectTable ObjectTable { get; private set; }

	[PluginService]
	internal static IAddonLifecycle AddonLifecycle { get; private set; }

	[PluginService]
	internal static IChatGui ChatGui { get; private set; }

	[PluginService]
	internal static IPlayerState PlayerState { get; private set; }

	[PluginService]
	internal static ITargetManager TargetManager { get; private set; }

	[PluginService]
	internal static IDutyState DutyState { get; private set; }

	[PluginService]
	internal static IUnlockState UnlockState { get; private set; }

	public static Plugin? Instance => instance;

	public Configuration Configuration { get; init; }

	public QuestionableIPC QuestionableIPC { get; init; }

	private AutoRetainerIPC AutoRetainerIPC { get; init; }

	public LifestreamIPC LifestreamIPC { get; init; }

	public YesAlreadyIPC YesAlreadyIPC { get; init; }

	private PartyInviteService PartyInviteService { get; init; }

	private MultiClientIPC MultiClientIPC { get; init; }

	private CrossProcessIPC CrossProcessIPC { get; init; }

	private PartyInviteAutoAccept PartyInviteAutoAccept { get; init; }

	private HelperManager HelperManager { get; init; }

	private QuestDetectionService QuestDetection { get; init; }

	private EventQuestExecutionService EventQuestService { get; init; }

	private QuestTrackingService QuestTrackingService { get; init; }

	private QuestRotationExecutionService QuestRotationService { get; init; }

	private SubmarineManager SubmarineManager { get; init; }

	private CharacterSafeWaitService SafeWaitService { get; init; }

	private QuestPreCheckService PreCheckService { get; init; }

	private DCTravelService DCTravelService { get; init; }

	public MovementMonitorService MovementMonitor { get; init; }

	private CombatDutyDetectionService CombatDutyDetection { get; init; }

	private DeathHandlerService DeathHandler { get; init; }

	private DungeonAutomationService DungeonAutomation { get; init; }

	private StepsOfFaithHandler StepsOfFaithHandler { get; init; }

	private MSQProgressionService MSQProgressionService { get; init; }

	private XADatabaseIPC XADatabaseIPC { get; init; }

	private MemoryHelper MemoryHelper { get; init; }

	private ChauffeurModeService ChauffeurMode { get; init; }

	private ARPostProcessEventQuestService ARPostProcessService { get; init; }

	private AlliedSocietyDatabase AlliedSocietyDatabase { get; init; }

	private AlliedSocietyQuestSelector AlliedSocietyQuestSelector { get; init; }

	private AlliedSocietyRotationService AlliedSocietyRotationService { get; init; }

	private AlliedSocietyPriorityWindow AlliedSocietyPriorityWindow { get; init; }

	private ErrorRecoveryService ErrorRecoveryService { get; init; }

	public LANHelperServer? LANHelperServer { get; private set; }

	public LANHelperClient? LANHelperClient { get; private set; }

	private ARRTrialAutomationService ARRTrialAutomation { get; init; }

	public PostMoogleService PostMoogleService { get; init; }

	private SoloDutyTargetingService SoloDutyTargeting { get; init; }

	private AutoEquipHeadgearService AutoEquipHeadgear { get; init; }

	private VNavmeshIPC VNavmeshIPC { get; init; }

	private HuntDutyRunner HuntDutyRunner { get; init; }

	private FrenRiderIPC FrenRiderIPC { get; init; }

	private HuntLogDatabase HuntLogDatabase { get; init; }

	private HuntLogAutomationService HuntLogAutomationService { get; init; }

	private CompanionIPC CompanionIPC { get; init; }

	private RetainerGameInteractionService RetainerGameInteractionService { get; init; }

	private RetainerNameGenerator RetainerNameGenerator { get; init; }

	private RetainerCreationService RetainerCreationService { get; init; }

	private CombatJobResolver CombatJobResolver { get; init; }

	private JobStoneGearsetReconciliationService JobStoneGearsetReconciliation { get; init; }

	private ClassUnlockRotationService ClassUnlockRotationService { get; init; }

	private ConfigWindow ConfigWindow { get; init; }

	private NewMainWindow NewMainWindow { get; init; }

	private DebugWindow DebugWindow { get; init; }

	public Plugin()
	{
		instance = this;
		try
		{
			ECommonsMain.Init(PluginInterface, this, ECommons.Module.DalamudReflector);
			eCommonsInitialized = true;
			Configuration = (PluginInterface.GetPluginConfig() as Configuration) ?? new Configuration();
			HuntLogSettings huntLogs = Configuration.HuntLogs;
			bool num = huntLogs != null && huntLogs.MaxMarkRetries == 3;
			bool flag = Configuration.CharacterFilters == null || Configuration.CharacterFilters.MigrationVersion < 2;
			bool flag2 = Configuration.RetainerSetup == null || Configuration.RetainerSetup.MigrationVersion < 2;
			bool flag3 = Configuration.NormalizeChauffeurBlacklist();
			Configuration.EnsureDefaultProfile();
			bool flag4 = RetainerNameLogic.InvalidateGeneratedSampleCacheOnLoad(Configuration.RetainerSetup);
			bool flag5 = Configuration.HuntLogs.EnsureSelectedMount(ResolveLegacyHuntLogMountName);
			if (num || flag5 || flag3 || flag || flag2 || flag4)
			{
				Configuration.Save();
			}
			Log.Debug("[Plugin] Initializing services...");
			CombatJobResolver = new CombatJobResolver(DataManager, Log);
			if (CombatJobResolver.MigrateSavedSnapshots(Configuration))
			{
				Configuration.Save();
			}
			JobStoneGearsetReconciliation = new JobStoneGearsetReconciliationService(CombatJobResolver, Framework, ClientState, Condition, PlayerState, ObjectTable, Log);
			QuestionableIPC = new QuestionableIPC(PluginInterface, Log);
			AutoRetainerIPC = new AutoRetainerIPC(PluginInterface, Log, ClientState, CommandManager, Framework, ObjectTable, PlayerState, CombatJobResolver);
			LifestreamIPC = new LifestreamIPC(Log, PluginInterface, CommandManager);
			Log.Debug("[Plugin] Initializing YesAlreadyIPC...");
			YesAlreadyIPC = new YesAlreadyIPC(PluginInterface, Log);
			PartyInviteService = new PartyInviteService(Log, ObjectTable, ClientState);
			MultiClientIPC = new MultiClientIPC(PluginInterface, Log);
			CrossProcessIPC = new CrossProcessIPC(Log, Framework, Configuration);
			PartyInviteAutoAccept = new PartyInviteAutoAccept(Log, Framework, GameGui, PartyList, Configuration);
			QuestDetection = new QuestDetectionService(Framework, Log, ClientState);
			EventQuestService = new EventQuestExecutionService(AutoRetainerIPC, QuestionableIPC, Log, Framework, CommandManager, Condition, Configuration, DataManager, delegate
			{
				SaveEventQuestCompletionData();
			});
			QuestTrackingService = new QuestTrackingService(Log);
			Log.Debug("[Plugin] Initializing SubmarineManager...");
			SubmarineManager = new SubmarineManager(Log, AutoRetainerIPC, Configuration, CommandManager, Framework, PluginInterface);
			Log.Debug("[Plugin] Initializing VNavmeshIPC...");
			VNavmeshIPC = new VNavmeshIPC(PluginInterface);
			Log.Debug("[Plugin] Initializing HuntDutyRunner...");
			HuntDutyRunner = new HuntDutyRunner(PluginInterface, Log);
			Log.Debug("[Plugin] Initializing FrenRider IPC...");
			FrenRiderIPC = new FrenRiderIPC(PluginInterface, Log);
			Log.Debug("[Plugin] Initializing Hunt Log services...");
			HuntLogDatabase = new HuntLogDatabase(PluginInterface, DataManager, Log);
			HuntLogAutomationService = new HuntLogAutomationService(AutoRetainerIPC, VNavmeshIPC, LifestreamIPC, HuntDutyRunner, FrenRiderIPC, QuestionableIPC, HuntLogDatabase, Configuration, Log, Framework, CommandManager, Condition, ClientState, ObjectTable, TargetManager, GameGui, DataManager, JobStoneGearsetReconciliation);
			Log.Debug("[Plugin] Initializing retainer setup services...");
			RetainerNameGenerator = new RetainerNameGenerator(DataManager);
			RetainerGameInteractionService = new RetainerGameInteractionService(Framework, ClientState, Condition, PlayerState, ObjectTable, TargetManager, GameGui, AddonLifecycle, DataManager, VNavmeshIPC, LifestreamIPC, QuestionableIPC, HuntLogAutomationService, JobStoneGearsetReconciliation, YesAlreadyIPC);
			Log.Debug("[Plugin] Initializing PostMoogleService...");
			PostMoogleService = new PostMoogleService(Condition, Log, ClientState, CommandManager, Framework, GameGui, TargetManager, ObjectTable, DataManager, ChatGui, VNavmeshIPC, LifestreamIPC);
			Log.Debug("[Plugin] Initializing AutoEquipHeadgearService...");
			AutoEquipHeadgear = new AutoEquipHeadgearService(Log, ClientState, Framework);
			Log.Debug("[Plugin] Initializing QuestRotationService...");
			QuestRotationService = new QuestRotationExecutionService(AutoRetainerIPC, QuestTrackingService, SubmarineManager, QuestionableIPC, Configuration, DataManager, Log, Framework, CommandManager, Condition, ClientState, PlayerState, GameGui, ChatGui, LifestreamIPC, PostMoogleService, HuntLogAutomationService, CombatJobResolver, JobStoneGearsetReconciliation, delegate
			{
				SaveQuestCompletionData();
			});
			AutoEquipHeadgear.IsRotationActive = () => QuestRotationService.GetCurrentState().Phase != RotationPhase.Idle;
			AutoEquipHeadgear.IsEnabled = () => Configuration.EnableFriendshipCirclet;
			Log.Debug("[Plugin] Initializing SafeWaitService...");
			SafeWaitService = new CharacterSafeWaitService(ClientState, Log, Framework, Condition, GameGui);
			Log.Debug("[Plugin] Initializing QuestPreCheckService...");
			PreCheckService = new QuestPreCheckService(Log, ClientState, Configuration, AutoRetainerIPC, PluginInterface);
			Log.Debug("[Plugin] Initializing DCTravelService...");
			DCTravelService = new DCTravelService(Log, Configuration, LifestreamIPC, QuestionableIPC, SafeWaitService, ClientState, CommandManager, Framework, ObjectTable, PlayerState);
			Log.Debug("[Plugin] Initializing MovementMonitor...");
			MovementMonitor = new MovementMonitorService(ClientState, Log, CommandManager, Framework, Configuration);
			HuntLogAutomationService.SetMovementMonitor(MovementMonitor);
			Log.Debug("[Plugin] Movement monitor initialized (will start with rotation)");
			Log.Debug("[Plugin] Initializing CombatDutyDetection...");
			CombatDutyDetection = new CombatDutyDetectionService(Condition, Log, ClientState, CommandManager, Framework, Configuration, ObjectTable, YesAlreadyIPC);
			Log.Debug("[Plugin] Initializing DeathHandler...");
			DeathHandler = new DeathHandlerService(Condition, Log, ClientState, CommandManager, Framework, Configuration, GameGui, DataManager, ObjectTable);
			HuntLogAutomationService.SetDeathHandler(DeathHandler);
			Log.Debug("[Plugin] Initializing MemoryHelper...");
			MemoryHelper = new MemoryHelper(Log, GameInterop);
			if (Configuration.EnableLANHelpers)
			{
				Log.Information("[Plugin] LAN Helper System ENABLED - Initializing...");
				LANHelperClient = new LANHelperClient(Log, ClientState, Framework, Configuration);
				if (Configuration.StartLANServer)
				{
					Log.Information("[Plugin] Starting LAN Helper Server...");
					LANHelperServer = new LANHelperServer(Log, ClientState, Framework, Configuration, PartyInviteAutoAccept, CommandManager, this);
					LANHelperServer.Start();
					LANHelperClient.SetLANHelperServer(LANHelperServer);
				}
				Task.Run(async delegate
				{
					await Task.Delay(2000);
					await LANHelperClient.Initialize();
				});
			}
			else
			{
				Log.Debug("[Plugin] LAN Helper System disabled");
			}
			Log.Debug("[Plugin] Initializing HelperManager...");
			HelperManager = new HelperManager(Configuration, Log, CommandManager, Condition, ClientState, Framework, PartyInviteService, MultiClientIPC, CrossProcessIPC, PartyInviteAutoAccept, MemoryHelper, LANHelperClient, PartyList, GameGui);
			Log.Debug("[Plugin] Initializing DungeonAutomation...");
			DungeonAutomation = new DungeonAutomationService(Condition, Log, ClientState, CommandManager, Framework, GameGui, Configuration, HelperManager, MemoryHelper, QuestionableIPC, CrossProcessIPC, MultiClientIPC, DutyState);
			Log.Debug("[Plugin] Initializing StepsOfFaithHandler...");
			StepsOfFaithHandler = new StepsOfFaithHandler(Condition, Log, ClientState, CommandManager, Framework, Configuration);
			Log.Debug("[Plugin] Initializing MSQProgressionService...");
			MSQProgressionService = new MSQProgressionService(DataManager, Log, QuestDetection, ObjectTable, Framework);
			Log.Debug("[Plugin] Initializing XA Database IPC...");
			XADatabaseIPC = new XADatabaseIPC(PluginInterface, Log, MSQProgressionService, CombatJobResolver, Framework);
			RetainerCreationService = new RetainerCreationService(Configuration, AutoRetainerIPC, RetainerGameInteractionService, QuestionableIPC, RetainerNameGenerator, Framework, CommandManager, Log);
			ClassUnlockRotationService = new ClassUnlockRotationService(Configuration, AutoRetainerIPC, QuestionableIPC, QuestRotationService, HuntLogAutomationService, RetainerCreationService, PostMoogleService, JobStoneGearsetReconciliation, CombatJobResolver, DataManager, Framework, CommandManager, Condition, ClientState, PlayerState, Log);
			QuestRotationService.SetRetainerBatchRecoveryGuard(() => RetainerCreationService.HasPendingRecovery);
			Log.Debug("[Plugin] Initializing ChauffeurMode...");
			ChauffeurMode = new ChauffeurModeService(Configuration, Log, ClientState, Condition, Framework, CommandManager, DataManager, PartyList, ObjectTable, QuestionableIPC, CrossProcessIPC, PartyInviteService, PartyInviteAutoAccept, PluginInterface, MemoryHelper, MovementMonitor, HelperManager);
			Log.Debug("[Plugin] Initializing ARRTrialAutomation...");
			ARRTrialAutomation = new ARRTrialAutomationService(Log, Framework, CommandManager, ChatGui, Configuration, QuestionableIPC, SubmarineManager, HelperManager, PartyList, Condition, MemoryHelper, ClientState, QuestDetection);
			QuestDetection.QuestCompleted += delegate(uint questId, string questName)
			{
				ClassUnlockRotationService.OnQuestCompleted(questId, questName);
				if (questId == 89)
				{
					Log.Information("[Plugin] Quest 89 completed - triggering ARR Primal check");
					ARRTrialAutomation.OnTriggerQuestComplete();
				}
				ARRTrialAutomation.OnQuestComplete(questId);
			};
			Log.Debug("[Plugin] ARRTrialAutomation wired to QuestDetection.QuestCompleted");
			MovementMonitor.SetChauffeurMode(ChauffeurMode);
			if (LANHelperClient != null)
			{
				LANHelperClient.OnChauffeurMessageReceived += delegate(object? sender, LANHelperClient.ChauffeurMessageEventArgs args)
				{
					Framework.RunOnFrameworkThread(delegate
					{
						if (args.Type == LANMessageType.CHAUFFEUR_HELPER_READY_FOR_MOUNT)
						{
							ChauffeurMode.OnChauffeurMountReady(args.Data.QuesterName, args.Data.QuesterWorldId);
						}
						else if (args.Type == LANMessageType.CHAUFFEUR_HELPER_ARRIVED_DEST)
						{
							ChauffeurMode.OnChauffeurArrived(args.Data.QuesterName, args.Data.QuesterWorldId);
						}
						else if (args.Type == LANMessageType.CHAUFFEUR_READY_FOR_PICKUP)
						{
							ChauffeurMode.OnChauffeurReadyForPickup(args.Data.QuesterName, args.Data.QuesterWorldId);
						}
						else if (args.Type == LANMessageType.CHAUFFEUR_ABORTED)
						{
							ChauffeurMode.OnChauffeurAborted(args.Data.QuesterName, args.Data.QuesterWorldId);
						}
					});
				};
				Log.Debug("[Plugin] LANHelperClient Chauffeur events wired to ChauffeurMode");
			}
			Log.Debug("[Plugin] Initializing AR Post Process Event Quest Service...");
			EventQuestResolver eventQuestResolver = new EventQuestResolver(DataManager, Log);
			ARPostProcessService = new ARPostProcessEventQuestService(PluginInterface, QuestionableIPC, eventQuestResolver, Configuration, Log, Framework, CommandManager, LifestreamIPC);
			Log.Debug("[Plugin] Initializing SoloDutyTargetingService...");
			SoloDutyTargeting = new SoloDutyTargetingService(Condition, Log, ClientState, CommandManager, Framework, QuestionableIPC, TargetManager, ObjectTable);
			Log.Debug("[Plugin] Initializing Allied Society Services...");
			AlliedSocietyDatabase = new AlliedSocietyDatabase(Configuration, Log);
			AlliedSocietyQuestSelector = new AlliedSocietyQuestSelector(QuestionableIPC, Log);
			AlliedSocietyRotationService = new AlliedSocietyRotationService(QuestionableIPC, AlliedSocietyDatabase, AlliedSocietyQuestSelector, AutoRetainerIPC, Configuration, Log, Framework, CommandManager, Condition, ClientState, PlayerState);
			AlliedSocietyPriorityWindow = new AlliedSocietyPriorityWindow(Configuration, AlliedSocietyDatabase);
			Log.Debug("[Plugin] Initializing Error Recovery Service...");
			ErrorRecoveryService = new ErrorRecoveryService(Log, GameInterop, ClientState, Framework, GameGui, AutoRetainerIPC);
			QuestRotationService.SetErrorRecoveryService(ErrorRecoveryService);
			MultiClientIPC.OnChatMessageReceived += OnMultiClientChatReceived;
			CrossProcessIPC.OnChatMessageReceived += OnMultiClientChatReceived;
			CrossProcessIPC.OnCommandReceived += OnCommandReceived;
			QuestRotationService.SetDCTravelService(DCTravelService);
			QuestRotationService.SetSafeWaitService(SafeWaitService);
			QuestRotationService.SetPreCheckService(PreCheckService);
			QuestRotationService.SetMovementMonitor(MovementMonitor);
			QuestRotationService.SetCombatDutyDetection(CombatDutyDetection);
			QuestRotationService.SetDeathHandler(DeathHandler);
			QuestRotationService.SetDungeonAutomation(DungeonAutomation);
			QuestRotationService.SetStepsOfFaithHandler(StepsOfFaithHandler);
			QuestRotationService.SetHelperManager(HelperManager);
			DungeonAutomation.SetRotationActiveChecker(() => QuestRotationService.IsRotationActive);
			SoloDutyTargeting.SetRotationActiveChecker(() => QuestRotationService.IsRotationActive);
			QuestRotationService.SetARRTrialAutomationService(ARRTrialAutomation);
			Log.Debug("[Plugin] Initializing DataCenterService...");
			DataCenterService dataCenterService = new DataCenterService(DataManager, Log);
			Log.Debug("[Plugin] Initializing CompanionIPC...");
			CompanionIPC = new CompanionIPC(PluginInterface, Log, Framework, QuestRotationService, HuntLogAutomationService, Configuration, ClientState);
			Log.Debug($"[Plugin] Loaded {Configuration.StopPoints?.Count ?? 0} stop points from config");
			if (Configuration.StopPoints != null && Configuration.StopPoints.Count > 0)
			{
				QuestRotationService.LoadStopPoints(Configuration.StopPoints);
			}
			if (Configuration.QuestCompletionByCharacter != null)
			{
				QuestRotationService.LoadQuestCompletionData(Configuration.QuestCompletionByCharacter);
			}
			if (Configuration.EventQuestCompletionByCharacter != null)
			{
				EventQuestService.LoadEventQuestCompletionData(Configuration.EventQuestCompletionByCharacter);
			}
			Log.Debug("[Plugin] Initializing windows...");
			ConfigWindow = new ConfigWindow(this);
			NewMainWindow = new NewMainWindow(this, AutoRetainerIPC, QuestTrackingService, QuestRotationService, EventQuestService, AlliedSocietyRotationService, AlliedSocietyPriorityWindow, dataCenterService, MSQProgressionService, Configuration, Log, PluginInterface.UiBuilder, DataManager, ClientState, ObjectTable, HuntLogAutomationService, XADatabaseIPC, RetainerCreationService, ClassUnlockRotationService);
			DebugWindow = new DebugWindow(this, CombatDutyDetection, DeathHandler, DungeonAutomation);
			windows.Add(ConfigWindow);
			windows.Add(NewMainWindow);
			windows.Add(DebugWindow);
			windows.Add(AlliedSocietyPriorityWindow);
			CommandManager.AddHandler("/qstcomp", new CommandInfo(OnCommand)
			{
				HelpMessage = "Opens main window."
			});
			string[] array = new string[2] { "/qstc", "/qstcompanion" };
			foreach (string command in array)
			{
				CommandManager.AddHandler(command, new CommandInfo(OnCommand)
				{
					HelpMessage = "Open the Quest Sequence Manager"
				});
			}
			CommandManager.AddHandler("/qsthelper", new CommandInfo(OnHelperCommand)
			{
				HelpMessage = "Helper commands: /qsthelper reset - Reset helper status to Available"
			});
			CommandManager.AddHandler("/qstmoogle", new CommandInfo(OnMoogleCommand)
			{
				HelpMessage = "Triggers Post Moogle Processing"
			});
			PluginInterface.UiBuilder.Draw += DrawWindows;
			PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
			PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
			Log.Information("[QuestionableCompanion] Plugin loaded successfully");
			Log.Information("[QuestionableCompanion] IPC services initialized with lazy-loading (will connect when other plugins are ready)");
			Framework.RunOnFrameworkThread(delegate
			{
				HelperManager.AnnounceIfHelper();
			});
			Framework.Update += OnFrameworkUpdate;
		}
		catch (Exception ex)
		{
			Log.Error("[Plugin] Failed to initialize: " + ex.Message);
			Log.Error("[Plugin] Stack trace: " + ex.StackTrace);
			throw;
		}
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (Volatile.Read(in disposeStarted) != 0)
		{
			return;
		}
		try
		{
			if (!autoStartAttempted && RetainerCreationService.HasPendingRecovery)
			{
				autoStartAttempted = true;
				Log.Information("[AutoStart] Suppressed because a valid interrupted retainer batch has recovery priority.");
			}
			if (!autoStartAttempted && QuestRotationService.HasPendingRotationHandoff)
			{
				autoStartAttempted = true;
				Log.Information("[AutoStart] Suppressed because a valid interrupted rotation handoff has recovery priority.");
			}
			if (Configuration.AutoStartOnLogin && !autoStartAttempted)
			{
				if (autoStartCheckTime == DateTime.MinValue)
				{
					autoStartCheckTime = DateTime.Now;
					Log.Information("[AutoStart] Waiting for plugins to initialize...");
				}
				if ((DateTime.Now - autoStartCheckTime).TotalSeconds >= 15.0)
				{
					AutoRetainerIPC.TryReinitialize();
					QuestionableIPC.TryEnsureAvailableSilent();
					if (AutoRetainerIPC.IsAvailable && QuestionableIPC.IsAvailable)
					{
						autoStartAttempted = true;
						List<string> selectedCharactersForUI = Configuration.SelectedCharactersForUI;
						if (selectedCharactersForUI != null && selectedCharactersForUI.Count > 0)
						{
							Log.Information("[AutoStart] Dependencies ready - starting rotation...");
							CommandManager.ProcessCommand("/ays multi d");
							Log.Information("[AutoStart] âœ“ /ays multi d (disable multi mode)");
							bool num = QuestRotationService.GetAllStopPoints().Count > 0;
							bool flag = QuestionableIPC.GetLevelStopCondition()?.Enabled ?? false;
							if (num)
							{
								Log.Information("[AutoStart] Starting Quest/Combined Rotation (Stop Points configured)");
								QuestRotationService.StartNextAvailableRotation();
							}
							else if (flag)
							{
								Log.Information("[AutoStart] Starting Level-Only Rotation (only Level Stop Condition configured)");
								QuestRotationService.StartRotationLevelOnly(Configuration.SelectedCharactersForUI);
							}
							else
							{
								Log.Warning("[AutoStart] No rotation configuration found (no Stop Points or Level Stop Condition)");
							}
							NewMainWindow.IsOpen = true;
						}
						else
						{
							Log.Warning("[AutoStart] No characters configured - skipping auto-start");
						}
					}
					else if ((DateTime.Now - autoStartCheckTime).TotalSeconds >= 120.0)
					{
						autoStartAttempted = true;
						Log.Warning($"[AutoStart] Timeout waiting for dependencies (AR={AutoRetainerIPC.IsAvailable}, Q={QuestionableIPC.IsAvailable})");
					}
				}
			}
			if (QuestRotationService != null && QuestRotationService.IsRotationActive && (DateTime.Now - lastChauffeurCheck).TotalSeconds >= 5.0)
			{
				lastChauffeurCheck = DateTime.Now;
				if (Configuration.ChauffeurModeEnabled && Configuration.IsQuester)
				{
					ChauffeurMode?.CheckTaskDistance();
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error("[Plugin] Framework update error: " + ex.Message);
		}
	}

	private void SaveQuestCompletionData()
	{
		if (QuestRotationService != null)
		{
			Configuration.QuestCompletionByCharacter = QuestRotationService.GetQuestCompletionData();
			Configuration.Save();
			Log.Debug("[Plugin] Quest completion data saved to config");
		}
	}

	public LANHelperClient? GetLANHelperClient()
	{
		return LANHelperClient;
	}

	public void ToggleLANServer(bool enable)
	{
		if (enable)
		{
			if (LANHelperServer == null)
			{
				Log.Information("[Plugin] Starting LAN Helper Server (Runtime)...");
				LANHelperServer = new LANHelperServer(Log, ClientState, Framework, Configuration, PartyInviteAutoAccept, CommandManager, this);
				LANHelperServer.Start();
				HelperManager?.RegisterLANHelperServer(LANHelperServer);
				if (LANHelperClient != null)
				{
					LANHelperClient.SetLANHelperServer(LANHelperServer);
				}
			}
			else if (!LANHelperServer.IsRunning)
			{
				LANHelperServer.Start();
				HelperManager?.RegisterLANHelperServer(LANHelperServer);
			}
		}
		else if (LANHelperServer != null)
		{
			Log.Information("[Plugin] Stopping LAN Helper Server (Runtime)...");
			LANHelperServer.Stop();
			LANHelperServer.Dispose();
			LANHelperServer = null;
		}
	}

	private static string? ResolveLegacyHuntLogMountName(uint mountId)
	{
		try
		{
			if (!DataManager.GetExcelSheet<Mount>().TryGetRow(mountId, out var row))
			{
				return null;
			}
			string text = row.Singular.ToString();
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}
		catch (Exception ex)
		{
			Log.Warning($"[HuntLogs] Failed to migrate legacy mount ID {mountId}: {ex.Message}");
			return null;
		}
	}

	private void SaveEventQuestCompletionData()
	{
		if (EventQuestService != null)
		{
			Configuration.EventQuestCompletionByCharacter = EventQuestService.GetEventQuestCompletionData();
			Configuration.Save();
			Log.Debug("[Plugin] Event quest completion data saved to config");
		}
	}

	private void OnMultiClientChatReceived(string message)
	{
		Log.Information("========================================");
		Log.Information("[MULTI-CLIENT] Message received from other client:");
		Log.Information("[MULTI-CLIENT] " + message);
		Log.Information("========================================");
		try
		{
			CommandManager.ProcessCommand("/echo [Multi-Client] " + message);
		}
		catch (Exception ex)
		{
			Log.Error("[MULTI-CLIENT] Failed to send to chat: " + ex.Message);
		}
	}

	private void OnCommandReceived(string command)
	{
		if (Volatile.Read(in disposeStarted) != 0)
		{
			return;
		}
		if (!Configuration.IsHelperAutomationActive)
		{
			Log.Debug("[CHAUFFEUR] Helper automation is inactive, ignoring command from other client");
			return;
		}
		Log.Information("========================================");
		Log.Information("[CHAUFFEUR] Command received from other client:");
		Log.Information("[CHAUFFEUR] " + command);
		Log.Information("========================================");
		Framework.RunOnFrameworkThread(delegate
		{
			try
			{
				Log.Information("[CHAUFFEUR] Executing: " + command);
				CommandManager.ProcessCommand(command);
				Log.Information("[CHAUFFEUR] Command executed successfully");
			}
			catch (Exception ex)
			{
				Log.Error("[CHAUFFEUR] Failed to execute command: " + ex.Message);
			}
		});
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposeStarted, 1) == 0)
		{
			instance = null;
			DetachPublicCallbacks();
			SaveShutdownState();
			DisposeOwnedServices();
			DisposeUiAndCommands();
			DisposeECommonsLast();
			Log.Information("[QuestionableCompanion] Plugin disposed successfully");
		}
	}

	private void DetachPublicCallbacks()
	{
		RunShutdownStep(delegate
		{
			Framework.Update -= OnFrameworkUpdate;
		}, "framework update callback");
		RunShutdownStep(delegate
		{
			MultiClientIPC.OnChatMessageReceived -= OnMultiClientChatReceived;
		}, "multi-client chat callback");
		RunShutdownStep(delegate
		{
			CrossProcessIPC.OnChatMessageReceived -= OnMultiClientChatReceived;
			CrossProcessIPC.OnCommandReceived -= OnCommandReceived;
		}, "cross-process callbacks");
		RunShutdownStep(delegate
		{
			PluginInterface.UiBuilder.Draw -= DrawWindows;
			PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
			PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
		}, "UI callbacks");
	}

	private void SaveShutdownState()
	{
		RunShutdownStep(delegate
		{
			Configuration.StopPoints = QuestRotationService.GetAllStopPoints();
			Configuration.QuestCompletionByCharacter = QuestRotationService.GetQuestCompletionData();
			Configuration.Save();
			Log.Debug("[Plugin] Configuration saved");
		}, "configuration save");
	}

	private void DisposeOwnedServices()
	{
		DisposeOne(ClassUnlockRotationService, "ClassUnlockRotationService");
		DisposeOne(RetainerCreationService, "RetainerCreationService");
		DisposeOne(ARRTrialAutomation, "ARRTrialAutomation");
		DisposeOne(AlliedSocietyRotationService, "AlliedSocietyRotationService");
		DisposeOne(ARPostProcessService, "ARPostProcessService");
		DisposeOne(PostMoogleService, "PostMoogleService");
		DisposeOne(AutoEquipHeadgear, "AutoEquipHeadgear");
		DisposeOne(ChauffeurMode, "ChauffeurMode");
		DisposeOne(StepsOfFaithHandler, "StepsOfFaithHandler");
		DisposeOne(SoloDutyTargeting, "SoloDutyTargeting");
		DisposeOne(DungeonAutomation, "DungeonAutomation");
		DisposeOne(DeathHandler, "DeathHandler");
		DisposeOne(CombatDutyDetection, "CombatDutyDetection");
		DisposeOne(MovementMonitor, "MovementMonitor");
		DisposeOne(DCTravelService, "DCTravelService");
		DisposeOne(PreCheckService, "PreCheckService");
		DisposeOne(QuestRotationService, "QuestRotationService");
		DisposeOne(EventQuestService, "EventQuestService");
		DisposeOne(SubmarineManager, "SubmarineManager");
		DisposeOne(QuestTrackingService, "QuestTrackingService");
		DisposeOne(QuestDetection, "QuestDetection");
		DisposeOne(HelperManager, "HelperManager");
		DisposeOne(HuntLogAutomationService, "HuntLogAutomationService");
		DisposeOne(JobStoneGearsetReconciliation, "JobStoneGearsetReconciliation");
		DisposeOne(LANHelperClient, "LANHelperClient");
		DisposeOne(LANHelperServer, "LANHelperServer");
		DisposeOne(PartyInviteAutoAccept, "PartyInviteAutoAccept");
		DisposeOne(CrossProcessIPC, "CrossProcessIPC");
		DisposeOne(MultiClientIPC, "MultiClientIPC");
		DisposeOne(ErrorRecoveryService, "ErrorRecoveryService");
		DisposeOne(MemoryHelper, "MemoryHelper");
		DisposeOne(YesAlreadyIPC, "YesAlreadyIPC");
		DisposeOne(LifestreamIPC, "LifestreamIPC");
		DisposeOne(HuntDutyRunner, "HuntDutyRunner");
		DisposeOne(FrenRiderIPC, "FrenRiderIPC");
		DisposeOne(XADatabaseIPC, "XADatabaseIPC");
		DisposeOne(VNavmeshIPC, "VNavmeshIPC");
		DisposeOne(AutoRetainerIPC, "AutoRetainerIPC");
		DisposeOne(QuestionableIPC, "QuestionableIPC");
		DisposeOne(CompanionIPC, "CompanionIPC");
		Log.Debug("[Plugin] Services disposed");
	}

	private void DisposeUiAndCommands()
	{
		RunShutdownStep(delegate
		{
			windows.Clear();
			windowOpenStates.Clear();
		}, "window registry cleanup");
		DisposeOne(ConfigWindow, "ConfigWindow");
		DisposeOne(NewMainWindow, "NewMainWindow");
		DisposeOne(DebugWindow, "DebugWindow");
		string[] array = new string[5] { "/qstcomp", "/qstc", "/qstcompanion", "/qsthelper", "/qstmoogle" };
		foreach (string command in array)
		{
			RunShutdownStep(delegate
			{
				CommandManager.RemoveHandler(command);
			}, "command handler " + command);
		}
		Log.Debug("[Plugin] UI and command handlers disposed");
	}

	private void DisposeECommonsLast()
	{
		if (eCommonsInitialized)
		{
			RunShutdownStep(delegate
			{
				ECommonsMain.Dispose();
				eCommonsInitialized = false;
			}, "ECommons");
		}
	}

	private static void DisposeOne(IDisposable? value, string name)
	{
		if (value != null)
		{
			RunShutdownStep(value.Dispose, name);
		}
	}

	private static void RunShutdownStep(System.Action action, string name)
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			Log.Error(exception, "[Plugin] Failed to release " + name + " during shutdown.");
		}
	}

	private void OnCommand(string command, string args)
	{
		string text = args.Trim().ToLower();
		if (text == "hunt" || text.StartsWith("hunt "))
		{
			HandleHuntLogCommand(args);
			return;
		}
		switch (text)
		{
		case "arrtrials":
			ARRTrialAutomation.StartTrialChain();
			return;
		case "dbg":
			DebugWindow.Toggle();
			return;
		case "task":
			TestGetCurrentTask();
			return;
		}
		if (text.StartsWith("invite "))
		{
			TestPartyInvite(args.Substring(7).Trim());
			return;
		}
		switch (text)
		{
		case "invitehelpers":
			TestInviteHelpers();
			return;
		case "invitelanhelpers":
			HelperManager?.InviteLANHelpers();
			return;
		case "disband":
			TestDisband();
			return;
		}
		if (text.StartsWith("multi "))
		{
			string message = args.Substring(6).Trim();
			TestMultiClientChat(message);
			return;
		}
		if (text.StartsWith("cmd "))
		{
			string command2 = args.Substring(4).Trim();
			TestSendCommand(command2);
			return;
		}
		if (text == "chauffeur")
		{
			TestChauffeurMode();
			return;
		}
		if (text.StartsWith("rotation "))
		{
			if (!QuestionableIPC.IsAvailable)
			{
				ChatGui.Print("[QuestionableCompanion] Questionable plugin is not available. Please check the Warning tab.");
				return;
			}
			string text2 = text.Substring(9).Trim();
			if (text2 == "start")
			{
				QuestRotationService.StartNextAvailableRotation();
			}
			else if (text2 == "stop")
			{
				QuestRotationService.AbortRotation();
			}
			else
			{
				ChatGui.Print("[QuestionableCompanion] Usage: /qstc rotation [start|stop]");
			}
			return;
		}
		if (text.StartsWith("society "))
		{
			if (!QuestionableIPC.IsAvailable)
			{
				ChatGui.Print("[QuestionableCompanion] Questionable plugin is not available. Please check the Warning tab.");
				return;
			}
			string text3 = text.Substring(8).Trim();
			if (text3 == "start")
			{
				AlliedSocietyRotationService.StartRotation(Configuration.SelectedCharactersForUI);
			}
			else if (text3 == "stop")
			{
				AlliedSocietyRotationService.StopRotation();
			}
			else
			{
				ChatGui.Print("[QuestionableCompanion] Usage: /qstc society [start|stop]");
			}
			return;
		}
		if (text.StartsWith("event "))
		{
			if (!QuestionableIPC.IsAvailable)
			{
				ChatGui.Print("[QuestionableCompanion] Questionable plugin is not available. Please check the Warning tab.");
				return;
			}
			string text4 = text.Substring(6).Trim();
			if (text4 == "start")
			{
				if (!string.IsNullOrEmpty(Configuration.CurrentEventQuestId))
				{
					List<string> list = ((Configuration.SelectedCharactersForEventQuest.Count > 0) ? Configuration.SelectedCharactersForEventQuest : Configuration.SelectedCharactersForUI);
					if (list.Count > 0)
					{
						EventQuestService.StartEventQuestRotation(Configuration.CurrentEventQuestId, list);
					}
					else
					{
						ChatGui.Print("[QuestionableCompanion] No characters selected for Event Quest rotation.");
					}
				}
				else
				{
					ChatGui.Print("[QuestionableCompanion] No Event Quest selected in UI.");
				}
			}
			else if (text4 == "stop")
			{
				EventQuestService.AbortRotation();
			}
			else
			{
				ChatGui.Print("[QuestionableCompanion] Usage: /qstc event [start|stop]");
			}
			return;
		}
		switch (text)
		{
		case "society":
			TestAlliedSociety();
			break;
		case "stopcon":
			TestStopConditions();
			break;
		case "mounts":
			TestListMounts();
			break;
		case "aetheryte":
			TestFindNearestAetheryte();
			break;
		default:
			NewMainWindow.Toggle();
			break;
		}
	}

	private void OnHelperCommand(string command, string args)
	{
		string text = args.Trim().ToLower();
		if (text == "reset")
		{
			if (!Configuration.IsHighLevelHelper)
			{
				ChatGui.Print("[QSTHelper] You are not configured as a Helper!");
				return;
			}
			IPlayerCharacter localPlayer = ObjectTable.LocalPlayer;
			if (localPlayer == null)
			{
				ChatGui.Print("[QSTHelper] Not logged in!");
				return;
			}
			ChauffeurModeService chauffeurMode = ChauffeurMode;
			if (chauffeurMode != null)
			{
				chauffeurMode.ResetChauffeurState();
				ChatGui.Print("[QSTHelper] Status reset to Available (full reset)");
				Log.Information("[QSTHelper] Helper status manually reset to Available (full reset via ChauffeurMode)");
				return;
			}
			Configuration.CurrentHelperStatus = HelperStatus.Available;
			Configuration.AssignedQuester = string.Empty;
			Configuration.Save();
			string helperName = localPlayer.Name.ToString();
			ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
			CrossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "Available");
			ChatGui.Print("[QSTHelper] Status reset to Available (config only)");
			Log.Information("[QSTHelper] Helper status manually reset to Available (config only)");
		}
		else if (text == "status")
		{
			if (!Configuration.IsHighLevelHelper)
			{
				ChatGui.Print("[QSTHelper] You are not configured as a Helper!");
				return;
			}
			string text2 = Configuration.CurrentHelperStatus switch
			{
				HelperStatus.Available => "Available", 
				HelperStatus.Transporting => "Transporting", 
				HelperStatus.InDungeon => "In Dungeon", 
				_ => "Unknown", 
			};
			string text3 = (string.IsNullOrEmpty(Configuration.AssignedQuester) ? "None" : Configuration.AssignedQuester);
			ChatGui.Print("[QSTHelper] Status: " + text2);
			ChatGui.Print("[QSTHelper] Assigned Quester: " + text3);
		}
		else
		{
			ChatGui.Print("[QSTHelper] Commands:");
			ChatGui.Print("  /qsthelper reset - Reset status to Available");
			ChatGui.Print("  /qsthelper status - Show current status");
		}
	}

	private void TestGetCurrentTask()
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing Questionable.GetCurrentTask IPC");
		Log.Information("========================================");
		if (QuestionableIPC == null)
		{
			Log.Error("[TEST] QuestionableIPC is null!");
			return;
		}
		QuestionableIPC.ForceCheckAvailability();
		if (!QuestionableIPC.IsAvailable)
		{
			Log.Warning("[TEST] Questionable is not available!");
			return;
		}
		bool value = QuestionableIPC.IsRunning();
		Log.Information($"[TEST] Questionable IsRunning: {value}");
		TaskData currentTask = QuestionableIPC.GetCurrentTask();
		if (currentTask == null)
		{
			Log.Information("[TEST] GetCurrentTask returned NULL (no task active)");
		}
		else
		{
			Log.Information("[TEST] Current Task Found!");
			Log.Information("[TEST]   - Type: " + currentTask.GetType().FullName);
			Log.Information($"[TEST]   - Value: {currentTask}");
			Log.Information("[TEST]   - ToString(): " + currentTask.ToString());
			PropertyInfo[] properties = currentTask.GetType().GetProperties();
			if (properties.Length != 0)
			{
				Log.Information($"[TEST] Properties found: {properties.Length}");
				PropertyInfo[] array = properties;
				foreach (PropertyInfo propertyInfo in array)
				{
					try
					{
						object value2 = propertyInfo.GetValue(currentTask);
						Log.Information($"[TEST]   - {propertyInfo.Name}: {value2 ?? "null"} (Type: {propertyInfo.PropertyType.Name})");
					}
					catch (Exception ex)
					{
						Log.Warning("[TEST]   - " + propertyInfo.Name + ": ERROR - " + ex.Message);
					}
				}
			}
			else
			{
				Log.Information("[TEST] No properties found - might be a primitive type or string");
			}
		}
		Log.Information("========================================");
	}

	private void TestPartyInvite(string characterNameWithWorld)
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing PartyInvite Service");
		Log.Information("========================================");
		if (string.IsNullOrEmpty(characterNameWithWorld))
		{
			Log.Error("[TEST] Usage: /qstcomp invite <CharacterName@WorldName>");
			Log.Error("[TEST] Example: /qstcomp invite Firstname Lastname@Odin");
			Log.Information("========================================");
			return;
		}
		string[] array = characterNameWithWorld.Split('@');
		if (array.Length != 2)
		{
			Log.Error("[TEST] Invalid format! Use: CharacterName@WorldName");
			Log.Error("[TEST] Example: Firstname Lastname@Odin");
			Log.Information("========================================");
			return;
		}
		string text = array[0].Trim();
		string text2 = array[1].Trim();
		Log.Information("[TEST] Character: " + text);
		Log.Information("[TEST] World: " + text2);
		ExcelSheet<World> excelSheet = DataManager.GetExcelSheet<World>();
		if (excelSheet == null)
		{
			Log.Error("[TEST] Failed to load World sheet!");
			Log.Information("========================================");
			return;
		}
		ushort num = 0;
		foreach (World item in excelSheet)
		{
			if (item.Name.ExtractText().Equals(text2, StringComparison.OrdinalIgnoreCase))
			{
				num = (ushort)item.RowId;
				break;
			}
		}
		if (num == 0)
		{
			Log.Error("[TEST] World '" + text2 + "' not found!");
			Log.Information("========================================");
			return;
		}
		Log.Information($"[TEST] World ID: {num}");
		Log.Information("[TEST] Sending party invite...");
		if (PartyInviteService.InviteToParty(text, num))
		{
			Log.Information("[TEST] Party invite sent successfully!");
		}
		else
		{
			Log.Error("[TEST] Failed to send party invite!");
		}
		Log.Information("========================================");
	}

	private void TestInviteHelpers()
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing Helper Invite System");
		Log.Information("========================================");
		if (!Configuration.IsQuester)
		{
			Log.Error("[TEST] This client is not configured as a Quester!");
			Log.Error("[TEST] Please enable 'I'm a Quester' in Settings > Multi-Client Role");
			Log.Information("========================================");
			return;
		}
		string text = Configuration.HelperSelection switch
		{
			HelperSelectionMode.Auto => "Auto (First Available)", 
			HelperSelectionMode.Dropdown => "Dropdown (Select Specific Helper)", 
			HelperSelectionMode.ManualInput => "Manual Input", 
			_ => "Unknown", 
		};
		Log.Information("[TEST] Current Selection Mode: " + text);
		Log.Information("[TEST] ----------------------------------------");
		if (Configuration.HelperSelection == HelperSelectionMode.ManualInput)
		{
			if (string.IsNullOrEmpty(Configuration.ManualHelperName))
			{
				Log.Error("[TEST] Manual Input mode selected, but no helper name configured!");
				Log.Error("[TEST] Please configure a helper name in Settings (format: CharacterName@WorldName)");
			}
			else
			{
				Log.Information("[TEST] Manual Helper: " + Configuration.ManualHelperName);
				Log.Information("[TEST] This helper will be invited directly (no IPC wait required)");
			}
		}
		else if (Configuration.HelperSelection == HelperSelectionMode.Dropdown)
		{
			List<(string, ushort)> availableHelpers = HelperManager.GetAvailableHelpers();
			if (availableHelpers.Count == 0)
			{
				Log.Warning("[TEST] No helpers discovered via IPC!");
				Log.Warning("[TEST] Make sure helper clients are running with 'I'm a High-Level Helper' enabled");
			}
			else
			{
				Log.Information($"[TEST] Auto-discovered helpers: {availableHelpers.Count}");
				foreach (var (value, value2) in availableHelpers)
				{
					Log.Information($"[TEST]   - {value}@{value2}");
				}
			}
			if (string.IsNullOrEmpty(Configuration.PreferredHelper))
			{
				Log.Warning("[TEST] Dropdown mode selected, but no specific helper chosen!");
				Log.Warning("[TEST] Please select a helper from the dropdown in Settings");
			}
			else
			{
				Log.Information("[TEST] Selected Helper: " + Configuration.PreferredHelper);
			}
		}
		else
		{
			List<(string, ushort)> availableHelpers2 = HelperManager.GetAvailableHelpers();
			if (availableHelpers2.Count == 0)
			{
				Log.Error("[TEST] No helpers discovered via IPC!");
				Log.Error("[TEST] Make sure helper clients are running with 'I'm a High-Level Helper' enabled");
				Log.Information("========================================");
				return;
			}
			Log.Information($"[TEST] Auto-discovered helpers: {availableHelpers2.Count}");
			foreach (var (value3, value4) in availableHelpers2)
			{
				Log.Information($"[TEST]   - {value3}@{value4}");
			}
		}
		Log.Information("[TEST] Invoking HelperManager.InviteHelpers()...");
		HelperManager.InviteHelpers();
		Log.Information("========================================");
	}

	private void TestDisband()
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing Party Disband");
		Log.Information("========================================");
		Log.Information("[TEST] Disbanding party...");
		HelperManager.DisbandParty();
		Log.Information("========================================");
	}

	private void TestMultiClientChat(string message)
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing Multi-Client Chat IPC");
		Log.Information("========================================");
		if (string.IsNullOrEmpty(message))
		{
			Log.Error("[TEST] Usage: /qstcomp multi <message>");
			Log.Error("[TEST] Example: /qstcomp multi Hello from Client 1!");
			Log.Information("========================================");
		}
		else
		{
			Log.Information("[TEST] Sending message via both IPC systems: " + message);
			MultiClientIPC.SendChatMessage(message);
			CrossProcessIPC.SendChatMessage(message);
			Log.Information("[TEST] Message sent! Check other clients for receipt.");
			Log.Information("========================================");
		}
	}

	private void TestSendCommand(string command)
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing Cross-Process Command (Chauffeur Mode)");
		Log.Information("========================================");
		if (string.IsNullOrEmpty(command))
		{
			Log.Error("[TEST] Usage: /qstcomp cmd <command>");
			Log.Error("[TEST] Example: /qstcomp cmd /teleport Limsa Lominsa");
			Log.Information("========================================");
		}
		else
		{
			Log.Information("[TEST] Sending command to other client: " + command);
			CrossProcessIPC.SendCommand(command);
			Log.Information("[TEST] Command sent! Check other client for execution.");
			Log.Information("========================================");
		}
	}

	private void TestChauffeurMode()
	{
		Log.Information("========================================");
		Log.Information("[TEST] Testing Chauffeur Mode");
		Log.Information("========================================");
		if (!Configuration.ChauffeurModeEnabled)
		{
			Log.Warning("[TEST] Chauffeur Mode is DISABLED in settings!");
			Log.Information("========================================");
			return;
		}
		if (Configuration.IsQuester)
		{
			Log.Information("[TEST] Role: QUESTER");
			Log.Information($"[TEST] Distance Threshold: {Configuration.ChauffeurDistanceThreshold} yalms");
			Log.Information("[TEST] Checking current task distance...");
			ChauffeurMode?.CheckTaskDistance();
		}
		else if (Configuration.IsHighLevelHelper)
		{
			Log.Information("[TEST] Role: HELPER");
			Log.Information($"[TEST] Mount ID: {Configuration.ChauffeurMountId}");
			if (Configuration.ChauffeurMountId == 0)
			{
				Log.Warning("[TEST] No mount configured! Please select a multi-seater mount in settings.");
			}
		}
		else
		{
			Log.Warning("[TEST] No role configured! Please select Quester or Helper in settings.");
		}
		Log.Information("========================================");
	}

	private void TestListMounts()
	{
		Log.Information("========================================");
		Log.Information("[TEST] Listing Multi-Seater Mounts");
		Log.Information("========================================");
		List<(uint, string, byte)> list = ChauffeurMode?.GetMultiSeaterMounts() ?? new List<(uint, string, byte)>();
		if (list.Count == 0)
		{
			Log.Warning("[TEST] No multi-seater mounts found!");
		}
		else
		{
			Log.Information($"[TEST] Found {list.Count} multi-seater mounts:");
			foreach (var (value, value2, value3) in list)
			{
				Log.Information($"[TEST]   - {value2} (ID: {value}, Seats: {value3})");
			}
		}
		Log.Information("========================================");
	}

	private void TestFindNearestAetheryte()
	{
		Log.Information("========================================");
		Log.Information("[TEST] Finding Nearest Aetheryte (Map Data)");
		Log.Information("========================================");
		IPlayerCharacter localPlayer = ObjectTable.LocalPlayer;
		if (localPlayer == null)
		{
			Log.Error("[TEST] Player not logged in!");
			Log.Information("========================================");
			return;
		}
		Vector3 position = localPlayer.Position;
		uint territoryType = ClientState.TerritoryType;
		Log.Information($"[TEST] Player Position: ({position.X:F2}, {position.Y:F2}, {position.Z:F2})");
		Log.Information($"[TEST] Territory ID: {territoryType}");
		ExcelSheet<Aetheryte> excelSheet = DataManager.GetExcelSheet<Aetheryte>();
		ExcelSheet<Map> excelSheet2 = DataManager.GetExcelSheet<Map>();
		if (excelSheet == null || excelSheet2 == null)
		{
			Log.Error("[TEST] Failed to load sheets!");
			Log.Information("========================================");
			return;
		}
		float num = float.MaxValue;
		string text = "Unknown";
		uint value = 0u;
		int num2 = 0;
		foreach (Aetheryte item in excelSheet)
		{
			if (item.Territory.RowId != territoryType || !item.IsAetheryte)
			{
				continue;
			}
			num2++;
			PlaceName? valueNullable = item.PlaceName.ValueNullable;
			string text2 = (valueNullable.HasValue ? valueNullable.Value.Name.ExtractText() : $"Aetheryte #{item.RowId}");
			Map? valueNullable2 = item.Map.ValueNullable;
			if (valueNullable2.HasValue)
			{
				float x = ConvertMapCoordinateToRawPosition(item.AetherstreamX, valueNullable2.Value.SizeFactor);
				float z = ConvertMapCoordinateToRawPosition(item.AetherstreamY, valueNullable2.Value.SizeFactor);
				Vector3 vector = new Vector3(x, 0f, z);
				float num3 = position.X - vector.X;
				float num4 = position.Z - vector.Z;
				float num5 = (float)Math.Sqrt(num3 * num3 + num4 * num4);
				Log.Information($"[TEST] Aetheryte #{num2}: {text2}");
				Log.Information($"[TEST]   Position: ({vector.X:F2}, ?, {vector.Z:F2})");
				Log.Information($"[TEST]   Distance (2D): {num5:F2} yalms");
				Log.Information($"[TEST]   Aetheryte ID: {item.RowId}");
				if (num5 < num)
				{
					num = num5;
					text = text2;
					value = item.RowId;
				}
			}
		}
		if (num2 == 0)
		{
			Log.Warning("[TEST] No aetherytes found in this territory!");
		}
		else
		{
			Log.Information("========================================");
			Log.Information("[TEST] NEAREST AETHERYTE: " + text);
			Log.Information($"[TEST] Distance (2D): {num:F2} yalms");
			Log.Information($"[TEST] Aetheryte ID: {value}");
			Log.Information("========================================");
		}
		Log.Information("========================================");
	}

	private float ConvertMapCoordinateToRawPosition(int coordinate, ushort scale)
	{
		float num = (float)(int)scale / 100f;
		return ((((float)coordinate - 1024f) / 2048f + 1f) * num / 2f - 1f) * (2048f / num) * 1000f / 2048f;
	}

	private void TestAlliedSociety()
	{
		Log.Information("========================================");
		Log.Information("[AlliedSociety] Testing Allied Society IPC Methods");
		Log.Information("========================================");
		if (QuestionableIPC == null)
		{
			Log.Error("[AlliedSociety] QuestionableIPC is null!");
			Log.Information("========================================");
			return;
		}
		QuestionableIPC.ForceCheckAvailability();
		if (!QuestionableIPC.IsAvailable)
		{
			Log.Warning("[AlliedSociety] Questionable is not available!");
			Log.Information("========================================");
			return;
		}
		int alliedSocietyRemainingAllowances = QuestionableIPC.GetAlliedSocietyRemainingAllowances();
		Log.Information("[AlliedSociety] ========================================");
		Log.Information($"[AlliedSociety] Daily Allowances Remaining: {alliedSocietyRemainingAllowances}/12");
		Log.Information("[AlliedSociety] ========================================");
		List<byte> alliedSocietiesWithAvailableQuests = QuestionableIPC.GetAlliedSocietiesWithAvailableQuests();
		Log.Information($"[AlliedSociety] Societies with available quests: {alliedSocietiesWithAvailableQuests.Count}");
		foreach (byte item in alliedSocietiesWithAvailableQuests)
		{
			Log.Information($"[AlliedSociety]   - Society ID: {item}");
		}
		Log.Information("[AlliedSociety] ========================================");
		Dictionary<byte, int> alliedSocietyAllAvailableQuestCounts = QuestionableIPC.GetAlliedSocietyAllAvailableQuestCounts();
		Log.Information("[AlliedSociety] Quest counts by society:");
		foreach (var (value, value2) in alliedSocietyAllAvailableQuestCounts)
		{
			Log.Information($"[AlliedSociety]   - Society {value}: {value2} quests");
		}
		Log.Information("[AlliedSociety] ========================================");
		string[] array = new string[20]
		{
			"Amalj'aa", "Sylphs", "Kobolds", "Sahagin", "Ixal", "Vanu Vanu", "Vath", "Moogles", "Kojin", "Ananta",
			"Namazu", "Pixies", "Qitari", "Dwarves", "Arkasodara", "Omicrons", "Loporrits", "Pelupelu", "Mamool Ja", "Yok Huy"
		};
		for (byte b2 = 1; b2 <= 20; b2++)
		{
			string value3 = array[b2 - 1];
			Log.Information("[AlliedSociety] ----------------------------------------");
			Log.Information($"[AlliedSociety] Testing Society ID {b2}: {value3}");
			Log.Information("[AlliedSociety] ----------------------------------------");
			int alliedSocietyCurrentRank = QuestionableIPC.GetAlliedSocietyCurrentRank(b2);
			Log.Information($"[AlliedSociety]   Current Rank: {alliedSocietyCurrentRank}");
			bool alliedSocietyIsMaxRank = QuestionableIPC.GetAlliedSocietyIsMaxRank(b2);
			Log.Information($"[AlliedSociety]   Is Max Rank: {alliedSocietyIsMaxRank}");
			List<string> alliedSocietyAvailableQuestIds = QuestionableIPC.GetAlliedSocietyAvailableQuestIds(b2);
			Log.Information($"[AlliedSociety]   Available Quests: {alliedSocietyAvailableQuestIds.Count}");
			foreach (string item2 in alliedSocietyAvailableQuestIds)
			{
				bool num2 = QuestionableIPC.IsQuestComplete(item2);
				bool flag = QuestionableIPC.IsReadyToAcceptQuest(item2);
				string text = (num2 ? "âœ“ Completed" : "âœ— Not Completed");
				string text2 = (flag ? "âœ“ Ready" : "âœ— Not Ready");
				Log.Information("[AlliedSociety]     - Quest ID: " + item2);
				Log.Information("[AlliedSociety]       Completed: " + text + " | Ready to Accept: " + text2);
			}
			List<string> alliedSocietyOptimalQuests = QuestionableIPC.GetAlliedSocietyOptimalQuests(b2);
			Log.Information($"[AlliedSociety]   Optimal Quests: {alliedSocietyOptimalQuests.Count}");
			foreach (string item3 in alliedSocietyOptimalQuests)
			{
				bool num3 = QuestionableIPC.IsQuestComplete(item3);
				bool flag2 = QuestionableIPC.IsReadyToAcceptQuest(item3);
				string text3 = (num3 ? "âœ“ Completed" : "âœ— Not Completed");
				string text4 = (flag2 ? "âœ“ Ready" : "âœ— Not Ready");
				Log.Information("[AlliedSociety]     - Optimal Quest ID: " + item3);
				Log.Information("[AlliedSociety]       Completed: " + text3 + " | Ready to Accept: " + text4);
			}
			int value4 = QuestionableIPC.AddAlliedSocietyOptimalQuests(b2);
			Log.Information($"[AlliedSociety]   Added {value4} optimal quests to priority queue");
		}
		Log.Information("========================================");
		Log.Information("[AlliedSociety] All Allied Society tests completed!");
		Log.Information("========================================");
	}

	private void TestStopConditions()
	{
		Log.Information("========================================");
		Log.Information("[StopCondition] Testing Stop Condition IPC Methods");
		Log.Information("========================================");
		if (QuestionableIPC == null)
		{
			Log.Error("[StopCondition] QuestionableIPC is null!");
			Log.Information("========================================");
			return;
		}
		QuestionableIPC.ForceCheckAvailability();
		if (!QuestionableIPC.IsAvailable)
		{
			Log.Warning("[StopCondition] Questionable is not available!");
			Log.Information("========================================");
			return;
		}
		bool stopConditionsEnabled = QuestionableIPC.GetStopConditionsEnabled();
		Log.Information($"[StopCondition] Stop Conditions Enabled: {stopConditionsEnabled}");
		List<string> stopQuestList = QuestionableIPC.GetStopQuestList();
		Log.Information($"[StopCondition] Stop Quest Count: {stopQuestList.Count}");
		foreach (string item in stopQuestList)
		{
			Log.Information("[StopCondition]   - Quest ID: " + item);
		}
		StopConditionData levelStopCondition = QuestionableIPC.GetLevelStopCondition();
		if (levelStopCondition != null)
		{
			Log.Information("[StopCondition] Level Stop Condition:");
			Log.Information($"[StopCondition]   Enabled: {levelStopCondition.Enabled}");
			Log.Information($"[StopCondition]   Target Level: {levelStopCondition.TargetValue}");
		}
		else
		{
			Log.Information("[StopCondition] Level Stop Condition: Not configured");
		}
		StopConditionData sequenceStopCondition = QuestionableIPC.GetSequenceStopCondition();
		if (sequenceStopCondition != null)
		{
			Log.Information("[StopCondition] Sequence Stop Condition:");
			Log.Information($"[StopCondition]   Enabled: {sequenceStopCondition.Enabled}");
			Log.Information($"[StopCondition]   Target Sequence: {sequenceStopCondition.TargetValue}");
		}
		else
		{
			Log.Information("[StopCondition] Sequence Stop Condition: Not configured");
		}
		Log.Information("[StopCondition] ========================================");
		Log.Information("[StopCondition] Testing Quest Sequence Stop Conditions");
		Log.Information("[StopCondition] ========================================");
		Log.Information("[StopCondition] Test 1: GetAllQuestSequenceStopConditions");
		Dictionary<string, int> allQuestSequenceStopConditions = QuestionableIPC.GetAllQuestSequenceStopConditions();
		if (allQuestSequenceStopConditions.Count > 0)
		{
			Log.Information($"[StopCondition]   âœ“ Found {allQuestSequenceStopConditions.Count} condition(s)");
			foreach (KeyValuePair<string, int> item2 in allQuestSequenceStopConditions)
			{
				Log.Information($"[StopCondition]     - Quest: {item2.Key} => {item2.Value}");
			}
		}
		else
		{
			Log.Information("[StopCondition]   â„¹ No quest sequence stop conditions configured (this is normal if none are set)");
		}
		if (stopQuestList.Count > 0)
		{
			string text = stopQuestList[0];
			Log.Information("[StopCondition] Test 2: GetQuestSequenceStopCondition for quest " + text);
			int questSequenceStopCondition = QuestionableIPC.GetQuestSequenceStopCondition(text);
			if (questSequenceStopCondition >= 0)
			{
				Log.Information($"[StopCondition]   âœ“ Found condition: {questSequenceStopCondition}");
			}
			else
			{
				Log.Information("[StopCondition]   â„¹ No condition found for " + text + " (Seq: 1, Step: 1)");
			}
		}
		else
		{
			Log.Information("[StopCondition] Test 2: Skipped (no stop quests available)");
		}
		if (allQuestSequenceStopConditions.Count > 0)
		{
			string text2 = "";
			using (Dictionary<string, int>.KeyCollection.Enumerator enumerator3 = allQuestSequenceStopConditions.Keys.GetEnumerator())
			{
				if (enumerator3.MoveNext())
				{
					text2 = enumerator3.Current;
				}
			}
			Log.Information("[StopCondition] Read-only check complete for quest " + text2);
		}
		else
		{
			Log.Information("[StopCondition] Test 3: Skipped (no conditions to remove)");
		}
		Log.Information("========================================");
		Log.Information("[StopCondition] All Stop Condition tests completed!");
		Log.Information("========================================");
	}

	public void ToggleConfigUi()
	{
		ConfigWindow.Toggle();
	}

	public void ToggleMainUi()
	{
		NewMainWindow.Toggle();
	}

	private void DrawWindows()
	{
		foreach (Window window in windows)
		{
			DrawWindow(window);
		}
	}

	private void HandleHuntLogCommand(string args)
	{
		string[] array = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length < 2)
		{
			ChatGui.Print("[QuestionableCompanion] Usage: /qstcomp hunt [start] <class|gc|all> | /qstcomp hunt stop");
			return;
		}
		string text = array[1].ToLowerInvariant();
		if (text == "stop")
		{
			HuntLogAutomationService.Stop();
			ChatGui.Print("[QuestionableCompanion] Hunt-log automation stop requested.");
			return;
		}
		if (text == "start")
		{
			if (array.Length < 3)
			{
				ChatGui.Print("[QuestionableCompanion] Usage: /qstcomp hunt start <class|gc|all>");
				return;
			}
			text = array[2].ToLowerInvariant();
		}
		HuntLogMode? huntLogMode = text switch
		{
			"class" => HuntLogMode.Class, 
			"gc" => HuntLogMode.GrandCompany, 
			"grandcompany" => HuntLogMode.GrandCompany, 
			"grand-company" => HuntLogMode.GrandCompany, 
			"all" => HuntLogMode.All, 
			_ => null, 
		};
		if (!huntLogMode.HasValue)
		{
			ChatGui.Print("[QuestionableCompanion] Usage: /qstcomp hunt [start] <class|gc|all> | /qstcomp hunt stop");
			return;
		}
		if (QuestRotationService.IsRotationActive)
		{
			ChatGui.PrintError("[QuestionableCompanion] Stop the active quest rotation before starting Hunt Logs.");
			return;
		}
		if (HuntLogAutomationService.IsRunning)
		{
			ChatGui.Print("[QuestionableCompanion] Hunt-log automation is already running.");
			return;
		}
		List<string> list = Configuration.SelectedCharactersForUI ?? new List<string>();
		if (list.Count == 0)
		{
			ChatGui.Print("[QuestionableCompanion] Select at least one character in the Characters tab before starting hunt logs.");
		}
		else if (HuntLogAutomationService.Start(huntLogMode.Value, list))
		{
			ChatGui.Print($"[QuestionableCompanion] Started hunt-log automation: {huntLogMode.Value}.");
		}
		else
		{
			ChatGui.PrintError("[QuestionableCompanion] Could not start hunt logs: " + HuntLogAutomationService.GetCurrentState().ErrorMessage);
		}
	}

	private void DrawWindow(Window window)
	{
		bool value;
		bool flag = windowOpenStates.TryGetValue(window, out value) && value;
		if (!window.IsOpen)
		{
			if (flag)
			{
				window.OnClose();
				windowOpenStates[window] = false;
			}
			return;
		}
		if (!flag)
		{
			window.OnOpen();
			windowOpenStates[window] = true;
		}
		window.PreDraw();
		if (window == NewMainWindow)
		{
			ImGui.SetNextWindowSizeConstraints(NewMainWindow.MinimumWindowSize, new Vector2(float.MaxValue, float.MaxValue));
			Vector2? forcedWindowSize = NewMainWindow.ForcedWindowSize;
			if (forcedWindowSize.HasValue)
			{
				Vector2 valueOrDefault = forcedWindowSize.GetValueOrDefault();
				ImGui.SetNextWindowSize(valueOrDefault, ImGuiCond.Always);
			}
		}
		if (window.Size.HasValue)
		{
			ImGui.SetNextWindowSize(window.Size.Value, window.SizeCondition);
		}
		if (window.Position.HasValue)
		{
			ImGui.SetNextWindowPos(window.Position.Value, window.PositionCondition);
		}
		if (window.Collapsed.HasValue)
		{
			ImGui.SetNextWindowCollapsed(window.Collapsed.Value, window.CollapsedCondition);
		}
		bool open = window.IsOpen;
		bool num = (window.ShowCloseButton ? ImGui.Begin(window.WindowName, ref open, window.Flags) : ImGui.Begin(window.WindowName, window.Flags));
		window.IsOpen = open;
		if (num && window.IsOpen)
		{
			window.Draw();
		}
		ImGui.End();
		if (window.IsOpen)
		{
			window.PostDraw();
		}
		if (!window.IsOpen)
		{
			window.OnClose();
			windowOpenStates[window] = false;
		}
	}

	public List<(string Name, ushort WorldId)> GetAvailableHelpers()
	{
		return HelperManager?.GetAvailableHelpers() ?? new List<(string, ushort)>();
	}

	public ChauffeurModeService? GetChauffeurMode()
	{
		return ChauffeurMode;
	}

	public HelperManager? GetHelperManager()
	{
		return HelperManager;
	}

	public LANHelperServer? GetLANHelperServer()
	{
		return LANHelperServer;
	}

	public DungeonAutomationService? GetDungeonAutomation()
	{
		return DungeonAutomation;
	}

	private void OnMoogleCommand(string command, string args)
	{
		if (PostMoogleService == null)
		{
			ChatGui.PrintError("[Questionable] Post Moogle Service is null.");
			return;
		}
		string[] array = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 0)
		{
			PostMoogleService.StartProcessing();
			ChatGui.Print("[Questionable] Started Post Moogle sequence.");
			return;
		}
		string text = array[0].ToLower();
		if (text == "inspect" && array.Length > 1)
		{
			string text2 = array[1];
			ChatGui.Print("[Questionable] Inspecting addon: " + text2);
			PostMoogleService.DebugInspect(text2);
		}
		else if (text == "start")
		{
			PostMoogleService.StartProcessing();
			ChatGui.Print("[Questionable] Started Post Moogle sequence.");
		}
		else if (text == "stop")
		{
			PostMoogleService.StopProcessing();
			ChatGui.Print("[Questionable] Stopped Post Moogle sequence.");
		}
		else
		{
			ChatGui.Print("Usage: /qstmoogle <start|stop|inspect [AddonName]>");
		}
	}
}
