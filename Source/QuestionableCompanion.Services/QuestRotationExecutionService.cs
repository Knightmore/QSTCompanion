using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class QuestRotationExecutionService : IDisposable
{
	private sealed class StopPointClipboardPayload
	{
		public string Format { get; set; } = "QuestionableCompanion.StopPoints";

		public int Version { get; set; } = 1;

		public List<StopPointClipboardEntry> StopPoints { get; set; } = new List<StopPointClipboardEntry>();
	}

	private sealed class StopPointClipboardEntry
	{
		public uint QuestId { get; set; }

		public byte? Sequence { get; set; }
	}

	private const string StopPointClipboardFormat = "QuestionableCompanion.StopPoints";

	private const int StopPointClipboardVersion = 1;

	private static readonly JsonSerializerOptions StopPointClipboardJsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	private RotationState currentState = new RotationState();

	private readonly AutoRetainerIPC autoRetainerIpc;

	private readonly IGameGui gameGui;

	private readonly QuestTrackingService questTrackingService;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly ICondition condition;

	private readonly IClientState clientState;

	private readonly IPlayerState playerState;

	private readonly SubmarineManager submarineManager;

	private readonly QuestionableIPC questionableIPC;

	private readonly HuntLogAutomationService huntLogAutomationService;

	private readonly IDataManager dataManager;

	private readonly CombatJobResolver combatJobResolver;

	private readonly JobStoneGearsetReconciliationService jobStoneGearsetReconciliation;

	private readonly IChatGui chatGui;

	private ARRTrialAutomationService? arrTrialAutomationService;

	private readonly Configuration configuration;

	private DCTravelService? dcTravelService;

	private CharacterSafeWaitService? safeWaitService;

	private QuestPreCheckService? preCheckService;

	private MovementMonitorService? movementMonitor;

	private CombatDutyDetectionService? combatDutyDetection;

	private DeathHandlerService? deathHandler;

	private DungeonAutomationService? dungeonAutomation;

	private StepsOfFaithHandler? stepsOfFaithHandler;

	private ErrorRecoveryService? errorRecoveryService;

	private readonly LifestreamIPC? lifestreamIPC;

	private HelperManager? helperManager;

	private readonly List<StopPoint> stopPoints = new List<StopPoint>();

	private Dictionary<uint, Dictionary<string, byte?>> questCompletionByCharacter = new Dictionary<uint, Dictionary<string, byte?>>();

	private System.Action? onDataChanged;

	private DateTime lastCheckTime = DateTime.MinValue;

	private DateTime lastJobLevelSnapshotTime = DateTime.MinValue;

	private const double CheckIntervalMs = 250.0;

	private DateTime lastFrameworkUpdateErrorLogTime = DateTime.MinValue;

	private int consecutiveFrameworkUpdateErrors;

	private bool frameworkUpdateDisabledByCriticalError;

	private static readonly TimeSpan FrameworkUpdateErrorLogCooldown = TimeSpan.FromSeconds(30L);

	private const int MaxConsecutiveFrameworkUpdateErrors = 3;

	private DateTime lastSubmarineCheckTime = DateTime.MinValue;

	private bool waitingForQuestAcceptForSubmarines;

	private uint? lastSoloDutyQuestId;

	private DateTime _lastRelogCommandTime = DateTime.MinValue;

	private DateTime lastQuestionableStartCommandTimeUtc = DateTime.MinValue;

	private string lastQuestionableStartCharacter = string.Empty;

	private bool _homeworldCommandSent;

	private bool _homeworldTravelStarted;

	private bool _preSwitchTasksStarted;

	private bool _returnHomeworldForPostMoogle;

	private bool _relogProcessStarted;

	private int skippedRetryAttempts;

	private const double CharacterLoginTimeoutSeconds = 120.0;

	private const double PhaseTimeoutSeconds = 120.0;

	private DateTime lastLevelingLogTime = DateTime.MinValue;

	private bool isLevelingModeActive;

	private bool submarinesReadyDuringDuty;

	private bool isRotationActive;

	private bool automaticClassUnlockInterruptionActive;

	private bool _consumablesProcessedThisStopPoint;

	private readonly PostMoogleService postMoogleService;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private CancellationTokenSource? combatJobSetupCancellationTokenSource;

	private Task<bool>? combatJobSetupTask;

	private string combatJobSetupCharacter = string.Empty;

	private bool combatJobSetupPassedForCurrentLogin;

	private string handoffStableWorldKey = string.Empty;

	private int handoffStableWorldReads;

	private bool handoffRecoveryAnnounced;

	private Func<bool>? isRetainerBatchRecoveryActive;

	private int currentStuckCount;

	private int _lastStuckCheckExperience;

	private DateTime _moogleCheckStartTime = DateTime.MinValue;

	private bool _postMoogleGateWasLocked;

	public bool IsRotationActive => isRotationActive;

	public bool HasPendingRotationHandoff => RotationHandoffLogic.Validate(configuration.RotationHandoff, DateTime.UtcNow) == RotationHandoffValidation.Valid;

	public QuestRotationExecutionService(AutoRetainerIPC autoRetainerIpc, QuestTrackingService questTrackingService, SubmarineManager submarineManager, QuestionableIPC questionableIPC, Configuration configuration, IDataManager dataManager, IPluginLog log, IFramework framework, ICommandManager commandManager, ICondition condition, IClientState clientState, IPlayerState playerState, IGameGui gameGui, IChatGui chatGui, LifestreamIPC lifestreamIPC, PostMoogleService postMoogleService, HuntLogAutomationService huntLogAutomationService, CombatJobResolver combatJobResolver, JobStoneGearsetReconciliationService jobStoneGearsetReconciliation, System.Action? onDataChanged = null)
	{
		this.autoRetainerIpc = autoRetainerIpc;
		this.questTrackingService = questTrackingService;
		this.submarineManager = submarineManager;
		this.questionableIPC = questionableIPC;
		this.configuration = configuration;
		this.dataManager = dataManager;
		this.log = log;
		this.framework = framework;
		this.commandManager = commandManager;
		this.condition = condition;
		this.clientState = clientState;
		this.playerState = playerState;
		this.gameGui = gameGui;
		this.postMoogleService = postMoogleService;
		this.lifestreamIPC = lifestreamIPC;
		this.huntLogAutomationService = huntLogAutomationService;
		this.combatJobResolver = combatJobResolver;
		this.jobStoneGearsetReconciliation = jobStoneGearsetReconciliation;
		this.chatGui = chatGui;
		this.onDataChanged = onDataChanged;
		framework.Update += OnFrameworkUpdate;
		log.Information("[QuestRotation] Service initialized");
		MovementMonitorService.OnStuckDetected += HandleStuckDetected;
	}

	internal void SetRetainerBatchRecoveryGuard(Func<bool> guard)
	{
		isRetainerBatchRecoveryActive = guard;
	}

	private unsafe int GetCurrentExperience()
	{
		try
		{
			PlayerState* ptr = PlayerState.Instance();
			if (ptr == null)
			{
				return 0;
			}
			byte currentClassJobId = ptr->CurrentClassJobId;
			ExcelSheet<ClassJob> excelSheet = dataManager.GetExcelSheet<ClassJob>();
			if (excelSheet == null)
			{
				return 0;
			}
			ClassJob row = excelSheet.GetRow(currentClassJobId);
			if (row.RowId == 0)
			{
				return 0;
			}
			ClassJob value = row.ClassJobParent.Value;
			uint num = ((value.RowId != 0) ? value.RowId : currentClassJobId);
			int num2 = 0;
			if (num <= 18)
			{
				num2 = (int)(num - 1);
			}
			else if (num == 26)
			{
				num2 = 18;
			}
			else if (num == 29)
			{
				num2 = 19;
			}
			else
			{
				if (num < 31)
				{
					return 0;
				}
				num2 = (int)(num - 11);
			}
			if (num2 < 0 || num2 >= 40)
			{
				return 0;
			}
			int num3 = ptr->ClassJobExperience[num2];
			if (num3 == 0)
			{
				_ = Plugin.ObjectTable.LocalPlayer?.Level < 100;
			}
			return num3;
		}
		catch (Exception ex)
		{
			log.Debug("[QuestRotation] Error getting EXP: " + ex.Message);
			return 0;
		}
	}

	public unsafe bool UpdateCurrentCharacterJobLevels(bool forceSave = false)
	{
		try
		{
			if (!clientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
			{
				return false;
			}
			string localCharacterKey = GetLocalCharacterKey();
			if (string.IsNullOrEmpty(localCharacterKey))
			{
				return false;
			}
			PlayerState* ptr = PlayerState.Instance();
			if (ptr == null)
			{
				return false;
			}
			Dictionary<uint, int> dictionary = new Dictionary<uint, int>();
			byte grandCompany = ptr->GrandCompany;
			int num = grandCompany switch
			{
				1 => ptr->GCRanks[0], 
				2 => ptr->GCRanks[1], 
				3 => ptr->GCRanks[2], 
				_ => 0, 
			};
			foreach (CombatJobDefinition definition in combatJobResolver.Definitions)
			{
				int expArrayIndex = definition.ExpArrayIndex;
				if (expArrayIndex >= 0 && expArrayIndex < ptr->ClassJobLevels.Length)
				{
					dictionary[definition.ClassJobId] = ptr->ClassJobLevels[expArrayIndex];
				}
			}
			IReadOnlySet<uint> itemIds;
			bool flag = combatJobResolver.TryReadLiveSoulCrystalItems(out itemIds);
			CombatJobResolution combatJobResolution = combatJobResolver.Resolve(dictionary, itemIds, flag);
			if (combatJobResolution.Levels.Count == 0)
			{
				return false;
			}
			configuration.CharacterJobLevels.TryGetValue(localCharacterKey, out CharacterJobLevelSnapshot previous);
			uint[] array = itemIds.OrderBy((uint itemId) => itemId).ToArray();
			if (!forceSave && previous != null && previous.JobEvidenceVersion == 1 && previous.HighestCombatJobLevel == combatJobResolution.HighestLevel && previous.HighestCombatJobId == combatJobResolution.HighestJobId && previous.GrandCompanyId == grandCompany && previous.GrandCompanyRank == num && previous.InventoryEvidenceValid == flag && previous.VerifiedSoulCrystalItemIds.OrderBy((uint itemId) => itemId).SequenceEqual(array) && previous.CombatJobLevels.Count == combatJobResolution.Levels.Count && !combatJobResolution.Levels.Any((KeyValuePair<uint, int> kvp) => !previous.CombatJobLevels.TryGetValue(kvp.Key, out var value) || value != kvp.Value))
			{
				return false;
			}
			configuration.CharacterJobLevels[localCharacterKey] = new CharacterJobLevelSnapshot
			{
				JobEvidenceVersion = 1,
				HighestCombatJobLevel = combatJobResolution.HighestLevel,
				HighestCombatJobId = combatJobResolution.HighestJobId,
				GrandCompanyId = grandCompany,
				GrandCompanyRank = num,
				CombatJobLevels = combatJobResolution.Levels.ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value),
				XadbObservedCombatJobLevels = (previous?.XadbObservedCombatJobLevels ?? new Dictionary<uint, int>()).ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value),
				XadbObservedCombatJobLevelsUpdatedUtc = (previous?.XadbObservedCombatJobLevelsUpdatedUtc ?? DateTime.MinValue),
				AllClassJobLevels = (previous?.AllClassJobLevels ?? new Dictionary<uint, int>()).ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value),
				HasAllClassJobLevels = (previous?.HasAllClassJobLevels ?? false),
				AllClassJobLevelsUpdatedUtc = (previous?.AllClassJobLevelsUpdatedUtc ?? DateTime.MinValue),
				InventoryEvidenceValid = flag,
				VerifiedSoulCrystalItemIds = array.ToList(),
				JobEvidenceSource = "LiveInventory",
				JobEvidenceUpdatedUtc = DateTime.UtcNow,
				LastUpdatedUtc = DateTime.UtcNow
			};
			configuration.Save();
			log.Information($"[QuestRotation] Updated trustworthy job snapshot for {localCharacterKey}: Lv. {combatJobResolution.HighestLevel} (ClassJob {combatJobResolution.HighestJobId})");
			return true;
		}
		catch (Exception ex)
		{
			log.Debug("[QuestRotation] Failed to update job level snapshot: " + ex.Message);
			return false;
		}
	}

	private void HandleStuckDetected(object? sender, StuckDetectedEventArgs e)
	{
		if (!configuration.EnableStuckRotation)
		{
			return;
		}
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer != null && localPlayer.Level < 100)
		{
			int currentExperience = GetCurrentExperience();
			if (currentExperience != _lastStuckCheckExperience)
			{
				log.Information($"[QuestRotation] Stuck detected but EXP gained ({_lastStuckCheckExperience} -> {currentExperience}) - Resetting stuck counter");
				_lastStuckCheckExperience = currentExperience;
				currentStuckCount = 0;
				return;
			}
			log.Warning($"[QuestRotation] Stuck detected & NO EXP GAIN (Level {localPlayer.Level}) - EXP: {currentExperience}");
		}
		currentStuckCount++;
		log.Warning($"[QuestRotation] Stuck count: {currentStuckCount}/{configuration.StuckRotationThreshold}");
		if (currentStuckCount >= configuration.StuckRotationThreshold)
		{
			log.Error($"[QuestRotation] Stuck threshold reached ({currentStuckCount}) - Skipping current character!");
			framework.RunOnTick(delegate
			{
				SkipToNextCharacter("Stuck Rotation (Position + No EXP)");
				currentStuckCount = 0;
				_lastStuckCheckExperience = 0;
			});
			e.Handled = true;
		}
	}

	public bool AddStopPoint(uint questId, byte? sequence = null)
	{
		if (stopPoints.Any((StopPoint sp) => sp.QuestId == questId && sp.Sequence == sequence))
		{
			log.Warning($"[QuestRotation] Stop point {questId}" + (sequence.HasValue ? $" Seq {sequence.Value}" : "") + " already exists");
			return false;
		}
		StopPoint item = new StopPoint
		{
			QuestId = questId,
			Sequence = sequence,
			IsActive = false,
			CreatedAt = DateTime.Now,
			QuestName = GetQuestName(questId)
		};
		stopPoints.Add(item);
		log.Information($"[QuestRotation] Added stop point: Quest {questId}");
		return true;
	}

	private string? GetQuestName(uint questId)
	{
		try
		{
			ExcelSheet<Quest> excelSheet = dataManager.GetExcelSheet<Quest>();
			if (excelSheet == null)
			{
				log.Warning("[QuestRotation] Quest sheet is null!");
				return null;
			}
			uint num = questId + 65536;
			if (!excelSheet.TryGetRow(num, out var row))
			{
				log.Debug($"[QuestRotation] Quest {questId} (Excel: {num}) not found in sheet");
				return null;
			}
			if (row.RowId == 0)
			{
				log.Debug($"[QuestRotation] Quest {questId} has RowId 0");
				return null;
			}
			string text = row.Name.ExtractText();
			if (string.IsNullOrEmpty(text))
			{
				log.Debug($"[QuestRotation] Quest {questId} has empty name after ExtractText");
				return null;
			}
			log.Information($"[QuestRotation] Successfully loaded quest name for {questId}: '{text}'");
			return text;
		}
		catch (Exception ex)
		{
			log.Error($"[QuestRotation] Exception getting quest name for {questId}: {ex.Message}");
			log.Error("[QuestRotation] Stack trace: " + ex.StackTrace);
			return null;
		}
	}

	public void ImportStopPointsFromQuestionable()
	{
		try
		{
			log.Information("[QuestRotation] Importing stop points from Questionable...");
			List<string> stopQuestList = questionableIPC.GetStopQuestList();
			Dictionary<string, int> dictionary = null;
			try
			{
				dictionary = questionableIPC.GetAllQuestSequenceStopConditions();
			}
			catch (Exception ex)
			{
				log.Error("[QuestRotation] Wrong Questionable Version ");
				log.Error("[QuestRotation] Import failed: " + ex.Message);
				return;
			}
			stopPoints.Clear();
			if (stopQuestList != null)
			{
				foreach (string item in stopQuestList)
				{
					if (!uint.TryParse(item, out var result))
					{
						continue;
					}
					byte? sequence = null;
					if (dictionary != null && dictionary.ContainsKey(item))
					{
						try
						{
							sequence = Convert.ToByte(dictionary[result.ToString()]);
						}
						catch
						{
						}
					}
					stopPoints.Add(new StopPoint
					{
						QuestId = result,
						Sequence = sequence,
						IsActive = false,
						CreatedAt = DateTime.Now,
						QuestName = GetQuestName(result)
					});
				}
			}
			if (dictionary != null)
			{
				foreach (KeyValuePair<string, int> item2 in dictionary)
				{
					if (uint.TryParse(item2.Key, out var questId))
					{
						byte? sequence2 = null;
						try
						{
							sequence2 = Convert.ToByte(item2.Value);
						}
						catch
						{
						}
						if (!stopPoints.Any((StopPoint sp) => sp.QuestId == questId && sp.Sequence == sequence2))
						{
							stopPoints.Add(new StopPoint
							{
								QuestName = GetQuestName(questId),
								QuestId = questId,
								Sequence = sequence2,
								IsActive = false,
								CreatedAt = DateTime.Now
							});
						}
					}
				}
			}
			foreach (StopPoint item3 in stopPoints.DistinctBy((StopPoint point) => point.QuestId))
			{
				if (!questionableIPC.SetQuestStopMode(item3.QuestId.ToString(), QuestionableIPC.EStopConditionMode.Pause))
				{
					log.Warning($"[QuestRotation] Failed to set quest {item3.QuestId} stop mode to Pause");
				}
			}
			log.Information($"[QuestRotation] Imported {stopPoints.Count} stop points");
		}
		catch (Exception ex2)
		{
			log.Error("[QuestRotation] Failed to import stop points: " + ex2.Message);
		}
	}

	public StopPointImportResult ImportCompanionStopPointsIntoQuestionable()
	{
		if (isRotationActive)
		{
			return new StopPointImportResult
			{
				ErrorMessage = "Stop the active quest rotation before importing stop points."
			};
		}
		List<StopPoint> list = (from point in stopPoints
			group point by point.QuestId into @group
			select @group.OrderByDescending((StopPoint point) => point.Sequence.HasValue).First() into point
			orderby point.CreatedAt
			select point).ToList();
		if (list.Count == 0)
		{
			return new StopPointImportResult
			{
				ErrorMessage = "There are no Companion stop points to import."
			};
		}
		questionableIPC.TryEnsureAvailableSilent();
		if (!questionableIPC.IsAvailable)
		{
			return new StopPointImportResult
			{
				Total = list.Count,
				Failed = list.Count,
				ErrorMessage = questionableIPC.CompatibilityMessage
			};
		}
		HashSet<string> hashSet = questionableIPC.GetStopQuestList().ToHashSet<string>(StringComparer.Ordinal);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		foreach (StopPoint item in list)
		{
			string text = item.QuestId.ToString();
			bool flag = hashSet.Contains(text);
			if (!flag)
			{
				questionableIPC.AddStopQuest(text);
			}
			bool flag2 = questionableIPC.SetQuestStopMode(text, QuestionableIPC.EStopConditionMode.Pause);
			bool flag3 = !item.Sequence.HasValue || questionableIPC.SetQuestSequenceStopCondition(text, item.Sequence.Value);
			if (!flag2 || !flag3)
			{
				num4++;
				log.Warning($"[QuestRotation] Could not fully import Companion stop point {item.DisplayName} (mode: {flag2}, sequence: {flag3})");
			}
			else
			{
				if (flag)
				{
					num2++;
				}
				else
				{
					num++;
				}
				if (item.Sequence.HasValue)
				{
					num3++;
				}
			}
		}
		int num5 = num + num2;
		bool flag4 = num5 > 0 && questionableIPC.SetStopConditionsEnabled(enabled: true);
		log.Information($"[QuestRotation] Companion -> Questionable stop-point import finished: {num} added, {num2} updated, {num3} sequences, {num4} failed, enabled={flag4}");
		return new StopPointImportResult
		{
			Total = list.Count,
			Added = num,
			Updated = num2,
			SequencesImported = num3,
			Failed = num4,
			StopConditionsEnabled = flag4,
			ErrorMessage = ((num5 > 0 && !flag4) ? "The stop points were imported, but Questionable's stop conditions could not be enabled." : null)
		};
	}

	public bool RemoveStopPoint(uint questId)
	{
		if (isRotationActive)
		{
			log.Error($"[QuestRotation] Cannot remove stop point {questId} during active rotation!");
			return false;
		}
		StopPoint stopPoint = stopPoints.FirstOrDefault((StopPoint sp) => sp.QuestId == questId);
		if (stopPoint == null)
		{
			log.Warning($"[QuestRotation] Stop point {questId} not found");
			return false;
		}
		stopPoints.Remove(stopPoint);
		configuration.StopPoints = GetAllStopPoints();
		configuration.Save();
		log.Information($"[QuestRotation] Removed stop point: Quest {questId}");
		return true;
	}

	public bool MoveStopPoint(int currentIndex, int newIndex)
	{
		if (isRotationActive)
		{
			log.Warning("[QuestRotation] Cannot reorder stop points during an active rotation");
			return false;
		}
		if (currentIndex < 0 || currentIndex >= stopPoints.Count || newIndex < 0 || newIndex >= stopPoints.Count || currentIndex == newIndex)
		{
			return false;
		}
		StopPoint stopPoint = stopPoints[currentIndex];
		stopPoints.RemoveAt(currentIndex);
		stopPoints.Insert(newIndex, stopPoint);
		configuration.StopPoints = GetAllStopPoints();
		configuration.Save();
		log.Information($"[QuestRotation] Reordered local stop point {stopPoint.QuestId}: {currentIndex + 1} -> {newIndex + 1} (Questionable unchanged)");
		return true;
	}

	public bool StartRotation(uint questId, List<string> characters)
	{
		if (characters == null || characters.Count == 0)
		{
			log.Error("[QuestRotation] Cannot start rotation: No characters selected");
			return false;
		}
		arrTrialAutomationService?.Reset();
		_consumablesProcessedThisStopPoint = false;
		StopPoint stopPoint = stopPoints.FirstOrDefault((StopPoint sp) => sp.QuestId == questId);
		if (stopPoint == null)
		{
			log.Error($"[QuestRotation] Cannot start rotation: Quest {questId} not in stop points");
			return false;
		}
		ClearRotationHandoff("new explicit quest rotation", RotationHandoffLifecycleEvent.NewExplicitRotation);
		ResetQuestionableStartRetry();
		log.Information("[QuestRotation] Found stop point: " + stopPoint.DisplayName);
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		if (questCompletionByCharacter.TryGetValue(questId, out Dictionary<string, byte?> value))
		{
			log.Debug($"[QuestRotation] Quest {questId} has {value.Count} characters marked as completed in saved data");
		}
		else
		{
			log.Debug($"[QuestRotation] Quest {questId} has NO saved completion data");
		}
		foreach (string character in characters)
		{
			if (HasCharacterCompletedQuest(questId, character, stopPoint.Sequence))
			{
				list2.Add(character);
				log.Debug($"[QuestRotation] {character} already completed quest {questId} (or passed sequence {stopPoint.Sequence})");
			}
			else
			{
				list.Add(character);
				log.Debug($"[QuestRotation] {character} needs to complete quest {questId}");
			}
		}
		if (list.Count == 0)
		{
			log.Information($"[QuestRotation] All characters have already completed quest {questId}");
			return false;
		}
		EnableTextAdvanceForStopPointRotationStart();
		ResetCombatJobSetup();
		currentState = new RotationState
		{
			CurrentStopQuestId = questId,
			SelectedCharacters = new List<string>(characters),
			RemainingCharacters = list,
			CompletedCharacters = list2,
			Phase = RotationPhase.InitializingFirstCharacter,
			PhaseStartTime = DateTime.Now,
			RotationStartTime = DateTime.Now,
			HasQuestBeenAccepted = false
		};
		stopPoint.IsActive = true;
		isRotationActive = true;
		skippedRetryAttempts = 0;
		dungeonAutomation?.SetDutyModeBasedOnConfig();
		TriggerHelperAutoDiscovery();
		combatDutyDetection?.SetRotationActive(active: true);
		deathHandler?.SetRotationActive(active: true);
		if (configuration.EnableMovementMonitor && movementMonitor != null && !movementMonitor.IsMonitoring)
		{
			movementMonitor.StartMonitoring();
			log.Information("[QuestRotation] Movement monitor started");
		}
		log.Information("[QuestRotation] â•\u0090â•\u0090â•\u0090 Starting Rotation â•\u0090â•\u0090â•\u0090");
		log.Information($"[QuestRotation] Quest ID: {questId}");
		log.Information($"[QuestRotation] Total Characters: {characters.Count}");
		log.Information($"[QuestRotation] Remaining: {list.Count} | Completed: {list2.Count}");
		log.Information("[QuestRotation] Characters to process: " + string.Join(", ", list));
		if (questionableIPC.SetLevelingModeEnabled(enabled: true))
		{
			log.Information("[QuestRotation] Leveling Mode enabled");
		}
		else
		{
			log.Warning("[QuestRotation] Failed to enable Leveling Mode");
		}
		return true;
	}

	public bool StartRotationLevelOnly(List<string> characters)
	{
		if (characters == null || characters.Count == 0)
		{
			log.Error("[QuestRotation] Cannot start rotation: No characters selected");
			return false;
		}
		_consumablesProcessedThisStopPoint = false;
		StopConditionData levelStopCondition = questionableIPC.GetLevelStopCondition();
		if (levelStopCondition == null || !levelStopCondition.Enabled)
		{
			log.Error("[QuestRotation] Cannot start level-only rotation: Level stop condition not configured");
			return false;
		}
		ClearRotationHandoff("new explicit level-only rotation", RotationHandoffLifecycleEvent.NewExplicitRotation);
		ResetQuestionableStartRetry();
		log.Information($"[QuestRotation] Starting level-only rotation (target level: {levelStopCondition.TargetValue})");
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		foreach (string character in characters)
		{
			(int, uint, string) knownHighestCombatJobLevel = GetKnownHighestCombatJobLevel(character);
			if (knownHighestCombatJobLevel.Item1 >= levelStopCondition.TargetValue)
			{
				list2.Add(character);
				log.Information($"[QuestRotation] Level-Only Mode: {character} already at target level from {knownHighestCombatJobLevel.Item3} (Lv. {knownHighestCombatJobLevel.Item1} >= {levelStopCondition.TargetValue}) - skipping");
			}
			else
			{
				list.Add(character);
			}
		}
		EnableTextAdvanceForStopPointRotationStart();
		ResetCombatJobSetup();
		currentState = new RotationState
		{
			CurrentStopQuestId = 0u,
			SelectedCharacters = new List<string>(characters),
			RemainingCharacters = list,
			CompletedCharacters = list2,
			Phase = RotationPhase.InitializingFirstCharacter,
			PhaseStartTime = DateTime.Now,
			RotationStartTime = DateTime.Now,
			HasQuestBeenAccepted = false
		};
		isRotationActive = true;
		skippedRetryAttempts = 0;
		dungeonAutomation?.SetDutyModeBasedOnConfig();
		TriggerHelperAutoDiscovery();
		combatDutyDetection?.SetRotationActive(active: true);
		deathHandler?.SetRotationActive(active: true);
		if (configuration.EnableMovementMonitor && movementMonitor != null && !movementMonitor.IsMonitoring)
		{
			movementMonitor.StartMonitoring();
			log.Information("[QuestRotation] Movement monitor started");
		}
		log.Information("[QuestRotation] â•\u0090â•\u0090â•\u0090 Starting Level-Only Rotation â•\u0090â•\u0090â•\u0090");
		log.Information($"[QuestRotation] Target Level: {levelStopCondition.TargetValue}");
		log.Information($"[QuestRotation] Total Characters: {characters.Count}");
		log.Information($"[QuestRotation] Remaining: {list.Count} | Completed by level: {list2.Count}");
		log.Information("[QuestRotation] Characters to process: " + string.Join(", ", list));
		if (questionableIPC.SetLevelingModeEnabled(enabled: true))
		{
			log.Information("[QuestRotation] Leveling Mode enabled");
		}
		else
		{
			log.Warning("[QuestRotation] Failed to enable Leveling Mode");
		}
		return true;
	}

	public bool StartSyncRotation(List<string> characters, bool filterCharactersWithExistingQuestData = true)
	{
		if (characters == null || characters.Count == 0)
		{
			log.Error("[QuestRotation] Cannot start sync rotation: No characters provided");
			return false;
		}
		ClearRotationHandoff("new explicit sync rotation", RotationHandoffLifecycleEvent.NewExplicitRotation);
		ResetQuestionableStartRetry();
		List<string> list = new List<string>();
		foreach (string item in characters.Where((string character) => !string.IsNullOrWhiteSpace(character)).Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			List<uint> completedQuestsByCharacter = GetCompletedQuestsByCharacter(item);
			if (!filterCharactersWithExistingQuestData || completedQuestsByCharacter.Count == 0)
			{
				list.Add(item);
			}
		}
		if (list.Count == 0)
		{
			log.Information("[QuestRotation] No characters need sync - all have existing data");
			return false;
		}
		log.Information("[QuestRotation] â•\u0090â•\u0090â•\u0090 Starting Sync Rotation â•\u0090â•\u0090â•\u0090");
		log.Information($"[QuestRotation] Characters to sync: {list.Count}");
		log.Information("[QuestRotation] Characters: " + string.Join(", ", list));
		ResetCombatJobSetup();
		currentState = new RotationState
		{
			CurrentStopQuestId = 0u,
			SelectedCharacters = new List<string>(list),
			RemainingCharacters = new List<string>(list),
			CompletedCharacters = new List<string>(),
			Phase = RotationPhase.InitializingFirstCharacter,
			PhaseStartTime = DateTime.Now,
			RotationStartTime = DateTime.Now,
			HasQuestBeenAccepted = false,
			IsSyncOnlyMode = true
		};
		isRotationActive = true;
		dungeonAutomation?.SetDutyModeBasedOnConfig();
		log.Information("[QuestRotation] Sync rotation started successfully!");
		return true;
	}

	public RotationState GetCurrentState()
	{
		return currentState;
	}

	internal bool TryBeginAutomaticClassUnlockInterruption(uint completedQuestId, string character)
	{
		bool flag = !isRotationActive || automaticClassUnlockInterruptionActive || currentState.IsSyncOnlyMode || completedQuestId == currentState.CurrentStopQuestId || !string.Equals(currentState.CurrentCharacter, character, StringComparison.OrdinalIgnoreCase) || !IsLoggedInAsRotationCharacter();
		if (!flag)
		{
			RotationPhase phase = currentState.Phase;
			bool flag2 = (((uint)(phase - 7) <= 1u || phase == RotationPhase.QuestActive) ? true : false);
			flag = !flag2;
		}
		if (flag)
		{
			return false;
		}
		automaticClassUnlockInterruptionActive = true;
		MovementMonitorService? movementMonitorService = movementMonitor;
		if (movementMonitorService != null && movementMonitorService.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
		}
		try
		{
			if (!commandManager.ProcessCommand("/qst stop"))
			{
				automaticClassUnlockInterruptionActive = false;
				log.Warning("[QuestRotation] /qst stop was not accepted for automatic Class Unlock.");
				return false;
			}
			log.Information($"[QuestRotation] Paused after quest {completedQuestId} for automatic Class Unlock on {character}.");
		}
		catch (Exception ex)
		{
			automaticClassUnlockInterruptionActive = false;
			log.Warning("[QuestRotation] Could not pause for automatic Class Unlock: " + ex.Message);
			return false;
		}
		return true;
	}

	internal void EndAutomaticClassUnlockInterruption(string character, bool resume)
	{
		if (automaticClassUnlockInterruptionActive)
		{
			automaticClassUnlockInterruptionActive = false;
			if (resume && isRotationActive && string.Equals(currentState.CurrentCharacter, character, StringComparison.OrdinalIgnoreCase))
			{
				ResetQuestionableStartRetry();
				currentState.Phase = RotationPhase.WaitingForQuestStart;
				currentState.HasQuestBeenAccepted = false;
				currentState.PhaseStartTime = DateTime.Now;
				TryIssueQuestionableStart("after automatic Class Unlock");
				log.Information($"[QuestRotation] Resumed {character} toward Stop Point {currentState.CurrentStopQuestId} after Class Unlock.");
			}
		}
	}

	public (int completed, int total) GetRotationProgress(StopPoint stopPoint)
	{
		if (currentState.SelectedCharacters.Count == 0)
		{
			return (completed: 0, total: 0);
		}
		return GetRotationProgress(stopPoint, currentState.SelectedCharacters);
	}

	public (int completed, int total) GetRotationProgress(StopPoint stopPoint, List<string> characters)
	{
		if (characters == null || characters.Count == 0)
		{
			return (completed: 0, total: 0);
		}
		int num = 0;
		foreach (string character in characters)
		{
			if (HasCharacterCompletedQuest(stopPoint.QuestId, character, stopPoint.Sequence))
			{
				num++;
			}
		}
		return (completed: num, total: characters.Count);
	}

	public List<StopPoint> GetAllStopPoints()
	{
		foreach (StopPoint stopPoint in stopPoints)
		{
			if (string.IsNullOrEmpty(stopPoint.QuestName))
			{
				stopPoint.QuestName = GetQuestName(stopPoint.QuestId);
			}
		}
		return new List<StopPoint>(stopPoints);
	}

	public (string Payload, int Count) CreateStopPointClipboardPayload()
	{
		List<StopPointClipboardEntry> list = (from point in stopPoints
			group point by point.QuestId into @group
			select @group.OrderByDescending((StopPoint point) => point.Sequence.HasValue).First() into point
			select new StopPointClipboardEntry
			{
				QuestId = point.QuestId,
				Sequence = point.Sequence
			}).ToList();
		return (Payload: JsonSerializer.Serialize(new StopPointClipboardPayload
		{
			StopPoints = list
		}, StopPointClipboardJsonOptions), Count: list.Count);
	}

	public bool TryPasteStopPointClipboardPayload(string? clipboardText, out string message)
	{
		if (isRotationActive)
		{
			message = "Stop the active quest rotation before pasting stop points.";
			return false;
		}
		if (string.IsNullOrWhiteSpace(clipboardText))
		{
			message = "The clipboard is empty.";
			return false;
		}
		StopPointClipboardPayload stopPointClipboardPayload;
		try
		{
			stopPointClipboardPayload = JsonSerializer.Deserialize<StopPointClipboardPayload>(clipboardText, StopPointClipboardJsonOptions);
		}
		catch (JsonException ex)
		{
			log.Warning("[QuestRotation] Invalid stop-point clipboard JSON: " + ex.Message);
			message = "The clipboard does not contain valid Companion stop points.";
			return false;
		}
		if (stopPointClipboardPayload == null || !string.Equals(stopPointClipboardPayload.Format, "QuestionableCompanion.StopPoints", StringComparison.Ordinal) || stopPointClipboardPayload.Version != 1)
		{
			message = "The clipboard contains an unsupported Companion stop-point format.";
			return false;
		}
		if (stopPointClipboardPayload.StopPoints.Count == 0)
		{
			message = "The copied stop-point list is empty.";
			return false;
		}
		if (stopPointClipboardPayload.StopPoints.Count > 1000)
		{
			message = "The copied stop-point list is unexpectedly large and was rejected.";
			return false;
		}
		List<StopPointClipboardEntry> list = (from stopPointClipboardEntry in stopPointClipboardPayload.StopPoints
			where stopPointClipboardEntry.QuestId != 0
			group stopPointClipboardEntry by stopPointClipboardEntry.QuestId into @group
			select @group.Last()).ToList();
		if (list.Count == 0)
		{
			message = "The clipboard contains no valid quest IDs.";
			return false;
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (StopPointClipboardEntry entry in list)
		{
			List<StopPoint> source = stopPoints.Where((StopPoint point) => point.QuestId == entry.QuestId).ToList();
			StopPoint stopPoint = source.FirstOrDefault();
			if (stopPoint == null)
			{
				stopPoints.Add(new StopPoint
				{
					QuestId = entry.QuestId,
					Sequence = entry.Sequence,
					QuestName = GetQuestName(entry.QuestId),
					IsActive = false,
					CreatedAt = DateTime.Now
				});
				num++;
				continue;
			}
			foreach (StopPoint item in source.Skip(1))
			{
				stopPoints.Remove(item);
			}
			if (stopPoint.Sequence == entry.Sequence)
			{
				num3++;
				continue;
			}
			stopPoint.Sequence = entry.Sequence;
			stopPoint.IsActive = false;
			num2++;
		}
		configuration.StopPoints = GetAllStopPoints();
		configuration.Save();
		message = $"Pasted {list.Count} stop point(s): {num} added, {num2} updated, {num3} unchanged.";
		log.Information("[QuestRotation] " + message);
		return true;
	}

	public StopPoint? GetCurrentStopPoint()
	{
		uint currentQuestId = currentState.CurrentStopQuestId;
		if (currentQuestId == 0)
		{
			return null;
		}
		return stopPoints.FirstOrDefault((StopPoint sp) => sp.QuestId == currentQuestId);
	}

	public List<string> GetRotationCharacters()
	{
		return new List<string>(currentState.SelectedCharacters);
	}

	public void LoadStopPoints(List<StopPoint> savedStopPoints)
	{
		if (savedStopPoints == null || savedStopPoints.Count <= 0)
		{
			return;
		}
		stopPoints.Clear();
		foreach (StopPoint savedStopPoint in savedStopPoints)
		{
			if (string.IsNullOrEmpty(savedStopPoint.QuestName))
			{
				savedStopPoint.QuestName = GetQuestName(savedStopPoint.QuestId);
			}
			stopPoints.Add(savedStopPoint);
		}
		log.Information($"[QuestRotation] Loaded {stopPoints.Count} stop points from configuration");
	}

	public void LoadQuestCompletionData(Dictionary<uint, List<string>> data)
	{
		if (data != null && data.Count > 0)
		{
			questCompletionByCharacter.Clear();
			foreach (KeyValuePair<uint, List<string>> datum in data)
			{
				Dictionary<string, byte?> dictionary = new Dictionary<string, byte?>();
				foreach (string item in datum.Value)
				{
					dictionary[item] = null;
				}
				questCompletionByCharacter[datum.Key] = dictionary;
			}
			log.Information($"[QuestRotation] Loaded quest completion data for {data.Count} quests");
			int num = 0;
			foreach (KeyValuePair<uint, List<string>> datum2 in data)
			{
				if (num < 5)
				{
					log.Information($"[QuestRotation] DEBUG: Quest {datum2.Key} -> {datum2.Value.Count} characters: {string.Join(", ", datum2.Value)}");
					num++;
				}
			}
			if (data.Count > 5)
			{
				log.Information($"[QuestRotation] DEBUG: ... and {data.Count - 5} more quests");
			}
		}
		else
		{
			log.Information("[QuestRotation] No quest completion data to load (empty or null)");
		}
	}

	public Dictionary<uint, List<string>> GetQuestCompletionData()
	{
		Dictionary<uint, List<string>> dictionary = new Dictionary<uint, List<string>>();
		foreach (KeyValuePair<uint, Dictionary<string, byte?>> item in questCompletionByCharacter)
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, byte?> item2 in item.Value)
			{
				if (!item2.Value.HasValue)
				{
					list.Add(item2.Key);
				}
			}
			if (list.Count > 0)
			{
				dictionary[item.Key] = list;
			}
		}
		return dictionary;
	}

	public void SetDCTravelService(DCTravelService service)
	{
		dcTravelService = service;
		log.Information("[QuestRotation] DC Travel service linked");
	}

	public void SetSafeWaitService(CharacterSafeWaitService service)
	{
		safeWaitService = service;
		log.Information("[QuestRotation] Safe Wait service linked");
	}

	public void SetPreCheckService(QuestPreCheckService service)
	{
		preCheckService = service;
		log.Information("[QuestRotation] PreCheck service linked");
	}

	public void SetMovementMonitor(MovementMonitorService service)
	{
		movementMonitor = service;
		log.Information("[QuestRotation] Movement Monitor service linked");
	}

	public void SetCombatDutyDetection(CombatDutyDetectionService service)
	{
		combatDutyDetection = service;
		log.Information("[QuestRotation] Combat/Duty Detection service linked");
	}

	public void SetDeathHandler(DeathHandlerService service)
	{
		deathHandler = service;
		log.Information("[QuestRotation] Death Handler service linked");
	}

	public void SetDungeonAutomation(DungeonAutomationService service)
	{
		dungeonAutomation = service;
		log.Information("[QuestRotation] Dungeon Automation service linked");
	}

	public void SetStepsOfFaithHandler(StepsOfFaithHandler service)
	{
		stepsOfFaithHandler = service;
		log.Information("[QuestRotation] Steps of Faith Handler service linked");
	}

	public void SetARRTrialAutomationService(ARRTrialAutomationService service)
	{
		arrTrialAutomationService = service;
	}

	public void SetErrorRecoveryService(ErrorRecoveryService service)
	{
		errorRecoveryService = service;
		log.Information("[QuestRotation] Error Recovery service linked");
	}

	public void SetHelperManager(HelperManager service)
	{
		helperManager = service;
		log.Information("[QuestRotation] Helper Manager service linked");
	}

	private void TriggerHelperAutoDiscovery()
	{
		if (helperManager == null)
		{
			log.Debug("[HelperDiscovery] HelperManager not available - skipping auto-discovery");
			return;
		}
		log.Information("[HelperDiscovery] â•\u0090â•\u0090â•\u0090 Starting Helper Auto-Discovery â•\u0090â•\u0090â•\u0090");
		Task.Run(async delegate
		{
			try
			{
				log.Information("[HelperDiscovery] Broadcasting helper announcements request...");
				helperManager.BroadcastRequestHelperAnnouncements();
				log.Information("[HelperDiscovery] Waiting 3 seconds for helper responses...");
				await Task.Delay(3000);
				List<(string, ushort)> availableHelpers = helperManager.GetAvailableHelpers();
				if (availableHelpers != null && availableHelpers.Count > 0)
				{
					log.Information($"[HelperDiscovery] âœ“ Found {availableHelpers.Count} helper(s):");
					foreach (var (value, value2) in availableHelpers)
					{
						log.Information($"[HelperDiscovery]   - {value} (World: {value2})");
					}
				}
				else
				{
					log.Information("[HelperDiscovery] No helpers responded (silent fail - continuing rotation)");
				}
				log.Information("[HelperDiscovery] â•\u0090â•\u0090â•\u0090 Helper Auto-Discovery Complete â•\u0090â•\u0090â•\u0090");
			}
			catch (Exception ex)
			{
				log.Error("[HelperDiscovery] Auto-discovery failed: " + ex.Message);
			}
		});
	}

	private void MarkQuestCompleted(uint questId, string characterName, byte? sequence = null, bool saveConfig = true)
	{
		if (!questCompletionByCharacter.ContainsKey(questId))
		{
			questCompletionByCharacter[questId] = new Dictionary<string, byte?>();
		}
		Dictionary<string, byte?> dictionary = questCompletionByCharacter[questId];
		if (!dictionary.ContainsKey(characterName))
		{
			dictionary[characterName] = sequence;
			log.Debug($"[QuestRotation] Marked {characterName} as completed quest {questId}" + (sequence.HasValue ? $" (Sequence {sequence.Value})" : " (Fully Completed)"));
			if (saveConfig)
			{
				onDataChanged?.Invoke();
			}
			return;
		}
		byte? value = dictionary[characterName];
		if (!sequence.HasValue && value.HasValue)
		{
			dictionary[characterName] = null;
			log.Debug($"[QuestRotation] Updated {characterName} quest {questId}: Sequence {value} -> Fully Completed");
			if (saveConfig)
			{
				onDataChanged?.Invoke();
			}
		}
		else if (sequence.HasValue && value.HasValue && sequence.Value > value.Value)
		{
			dictionary[characterName] = sequence;
			log.Debug($"[QuestRotation] Updated {characterName} quest {questId}: Sequence {value} -> {sequence}");
			if (saveConfig)
			{
				onDataChanged?.Invoke();
			}
		}
	}

	public List<uint> GetCompletedQuestsByCharacter(string characterName)
	{
		List<uint> list = new List<uint>();
		foreach (KeyValuePair<uint, Dictionary<string, byte?>> item in questCompletionByCharacter)
		{
			if (item.Value.ContainsKey(characterName))
			{
				list.Add(item.Key);
			}
		}
		return list;
	}

	private bool HasCharacterCompletedQuest(uint questId, string characterName, byte? sequence = null)
	{
		uint num = questId % 65536;
		uint key = num + 65536;
		if ((questCompletionByCharacter.TryGetValue(questId, out Dictionary<string, byte?> value) || questCompletionByCharacter.TryGetValue(num, out value) || questCompletionByCharacter.TryGetValue(key, out value)) && value.TryGetValue(characterName, out var value2))
		{
			if (!value2.HasValue)
			{
				return true;
			}
			if (sequence.HasValue)
			{
				return value2.Value >= sequence.Value;
			}
		}
		if (preCheckService != null)
		{
			bool? questStatus = preCheckService.GetQuestStatus(characterName, num);
			if (questStatus.HasValue && questStatus.Value)
			{
				return true;
			}
			if (Plugin.ObjectTable.LocalPlayer != null && $"{Plugin.ObjectTable.LocalPlayer.Name}@{Plugin.ObjectTable.LocalPlayer.HomeWorld.Value.Name}" == characterName)
			{
				if (preCheckService.IsLiveQuestCompleted(questId))
				{
					return true;
				}
				if (sequence.HasValue)
				{
					byte? currentQuestSequence = preCheckService.GetCurrentQuestSequence(questId);
					if (currentQuestSequence.HasValue && currentQuestSequence.Value > 0 && currentQuestSequence.Value >= sequence.Value)
					{
						return true;
					}
				}
			}
			if (sequence.HasValue)
			{
				byte questSequence = preCheckService.GetQuestSequence(characterName, questId);
				if (questSequence > 0 && questSequence >= sequence.Value)
				{
					return true;
				}
			}
		}
		return false;
	}

	public void ClearCharacterQuestData(string characterName)
	{
		log.Information("[QuestRotation] Clearing all quest data for " + characterName);
		int num = 0;
		foreach (KeyValuePair<uint, Dictionary<string, byte?>> item in questCompletionByCharacter.ToList())
		{
			if (item.Value.Remove(characterName))
			{
				num++;
			}
			if (item.Value.Count == 0)
			{
				questCompletionByCharacter.Remove(item.Key);
			}
		}
		log.Information($"[QuestRotation] Removed {characterName} from {num} quests in rotation tracking");
		if (preCheckService != null)
		{
			preCheckService.ClearCharacterData(characterName);
			log.Information("[QuestRotation] Cleared " + characterName + " data from PreCheck service");
		}
		if (configuration.XadbMsqProgressByCharacter.Remove(characterName))
		{
			log.Information("[QuestRotation] Cleared imported XA Database MSQ data for " + characterName);
		}
		onDataChanged?.Invoke();
		log.Information("[QuestRotation] Quest data reset complete for " + characterName);
	}

	private void ScanAndSaveAllCompletedQuests(string characterName, string localCharacter)
	{
		if (string.IsNullOrEmpty(characterName))
		{
			return;
		}
		if (string.IsNullOrEmpty(localCharacter) || !string.Equals(localCharacter, characterName, StringComparison.OrdinalIgnoreCase))
		{
			log.Warning($"[QuestRotation] Refusing live quest scan for '{characterName}' because local player is '{localCharacter}'. This prevents cross-character progress contamination.");
			return;
		}
		int num = 0;
		int num2 = 0;
		try
		{
			for (uint num3 = 0u; num3 <= 65535; num3++)
			{
				try
				{
					if (QuestManager.IsQuestComplete((ushort)num3))
					{
						uint questId = 65536 + num3;
						MarkQuestCompleted(questId, characterName, null, saveConfig: false);
						num2++;
					}
					num++;
				}
				catch
				{
				}
			}
			log.Information($"[QuestRotation] Scanned {num} quests, found {num2} completed for {characterName}");
			if (num2 > 0)
			{
				configuration.XadbMsqProgressByCharacter.Remove(characterName);
			}
			onDataChanged?.Invoke();
			framework.RunOnFrameworkThread(delegate
			{
				if (currentState.Phase == RotationPhase.ScanningQuests)
				{
					currentState.Phase = RotationPhase.CheckingQuestCompletion;
					currentState.PhaseStartTime = DateTime.Now;
					_moogleCheckStartTime = DateTime.MinValue;
					log.Information("[QuestRotation] Quest scan complete - moving to quest check");
				}
			});
		}
		catch (Exception ex)
		{
			log.Error("[QuestRotation] Error scanning quests for " + characterName + ": " + ex.Message);
		}
	}

	public void SyncQuestDataForCurrentCharacter()
	{
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer == null)
		{
			log.Warning("[QuestRotation] No character logged in - cannot sync quest data");
			return;
		}
		string text = $"{localPlayer.Name}@{localPlayer.HomeWorld.Value.Name}";
		log.Information("[QuestRotation] Starting quest data sync for " + text);
		ScanAndSaveAllCompletedQuests(text, text);
	}

	public void StartNextAvailableRotation()
	{
		if (stopPoints.Count == 0)
		{
			chatGui.Print("[QuestionableCompanion] No stop points configured.");
			return;
		}
		List<string> selectedCharactersForUI = configuration.SelectedCharactersForUI;
		if (selectedCharactersForUI == null || selectedCharactersForUI.Count == 0)
		{
			chatGui.Print("[QuestionableCompanion] No characters selected for rotation.");
			return;
		}
		foreach (StopPoint stopPoint in stopPoints)
		{
			if (StartRotation(stopPoint.QuestId, selectedCharactersForUI))
			{
				return;
			}
		}
		chatGui.Print("[QuestionableCompanion] All stop points are completed for the selected characters.");
	}

	public void AbortRotation()
	{
		log.Information("[QuestRotation] Aborting rotation");
		ClearRotationHandoff("explicit rotation abort", RotationHandoffLifecycleEvent.ExplicitAbort);
		ResetQuestionableStartRetry();
		ResetCombatJobSetup();
		automaticClassUnlockInterruptionActive = false;
		foreach (StopPoint stopPoint in stopPoints)
		{
			stopPoint.IsActive = false;
		}
		currentState = new RotationState
		{
			Phase = RotationPhase.Idle
		};
		isRotationActive = false;
		combatDutyDetection?.SetRotationActive(active: false);
		combatDutyDetection?.Reset();
		deathHandler?.SetRotationActive(active: false);
		deathHandler?.Reset();
		if (dungeonAutomation != null)
		{
			dungeonAutomation.Reset();
			dungeonAutomation.SetSupportDutyMode();
			log.Information("[QuestRotation] Dungeon automation reset");
		}
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
			log.Information("[QuestRotation] Movement monitor stopped");
		}
		submarineManager.Reset();
		log.Information("[QuestRotation] Submarine state reset");
		if (configuration.EnableSubmarineCheck)
		{
			try
			{
				commandManager.ProcessCommand("/ays set MultiModeType 2");
				log.Information("[QuestRotation] Submarine Monitoring enabled - executing '/ays set MultiModeType 2'");
			}
			catch (Exception ex)
			{
				log.Error("[QuestRotation] Failed to execute Submarine MultiMode command: " + ex.Message);
			}
		}
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (frameworkUpdateDisabledByCriticalError)
		{
			return;
		}
		try
		{
			DateTime now = DateTime.Now;
			if ((now - lastCheckTime).TotalMilliseconds < 250.0)
			{
				return;
			}
			lastCheckTime = now;
			if (clientState.IsLoggedIn && (now - lastJobLevelSnapshotTime).TotalSeconds >= 10.0)
			{
				lastJobLevelSnapshotTime = now;
				UpdateCurrentCharacterJobLevels();
			}
			if (!isRotationActive && configuration.RotationHandoff != null)
			{
				Func<bool>? func = isRetainerBatchRecoveryActive;
				if (func != null && func())
				{
					return;
				}
				TryRecoverRotationHandoff();
				if (!isRotationActive)
				{
					return;
				}
			}
			if (automaticClassUnlockInterruptionActive)
			{
				return;
			}
			if (isRotationActive && errorRecoveryService != null && errorRecoveryService.IsErrorDisconnect)
			{
				string text = errorRecoveryService.LastDisconnectedCharacter ?? currentState.CurrentCharacter;
				if (!string.IsNullOrEmpty(text))
				{
					log.Warning("[ErrorRecovery] Disconnect detected for " + text);
					log.Information("[ErrorRecovery] Automatically relogging to " + text + "...");
					currentState.CurrentCharacter = text;
					if (!EnsureRotationHandoff(text))
					{
						currentState.Phase = RotationPhase.Error;
						currentState.ErrorMessage = "Could not persist exact relog handoff for " + text;
						return;
					}
					MarkRelogCommandPending();
					errorRecoveryService.RequestRelog();
					UpdateRotationHandoffStage(RotationHandoffRecoveryStage.WaitingForExactLogin);
					errorRecoveryService.Reset();
					ResetCombatJobSetup();
					currentState.Phase = RotationPhase.WaitingForCharacterLogin;
					currentState.PhaseStartTime = DateTime.Now;
					log.Information("[ErrorRecovery] Relog initiated for " + text);
					return;
				}
				log.Warning("[ErrorRecovery] Disconnect detected but no character to relog to");
				errorRecoveryService.Reset();
			}
			if (deathHandler != null && combatDutyDetection != null && !combatDutyDetection.IsInDuty)
			{
				deathHandler.Update();
			}
			if (dungeonAutomation != null && !submarineManager.IsSubmarinePaused)
			{
				dungeonAutomation.Update();
				if (isRotationActive && configuration.EnableAutoDutyUnsynced && !dungeonAutomation.IsWaitingForParty && currentState.Phase != RotationPhase.WaitingForCharacterLogin && currentState.Phase != RotationPhase.WaitingBeforeCharacterSwitch && currentState.Phase != RotationPhase.WaitingForPreCharacterSwitchTasks && currentState.Phase != RotationPhase.WaitingForHomeworldReturn && currentState.Phase != RotationPhase.ScanningQuests && currentState.Phase != RotationPhase.CheckingQuestCompletion && currentState.Phase != RotationPhase.InitializingFirstCharacter)
				{
					_ = submarineManager.IsSubmarinePaused;
				}
			}
			if (combatDutyDetection != null)
			{
				if (combatDutyDetection.JustEnteredDuty && isRotationActive)
				{
					string currentQuestId = questionableIPC.GetCurrentQuestId();
					uint result = 0u;
					if (!string.IsNullOrEmpty(currentQuestId))
					{
						uint.TryParse(currentQuestId, out result);
					}
					combatDutyDetection.SetCurrentQuestId(result);
					waitingForQuestAcceptForSubmarines = false;
					bool flag = dungeonAutomation != null && dungeonAutomation.IsInAutoDutyDungeon;
					combatDutyDetection.SetAutoDutyDungeon(flag);
					if (flag)
					{
						log.Information("[QuestRotation] AutoDuty Dungeon entered - /ad stop after 1s");
						dungeonAutomation?.OnDutyEntered();
					}
					else
					{
						if (stepsOfFaithHandler != null)
						{
							stepsOfFaithHandler.PrepareForNewDuty();
						}
						if (result != 0)
						{
							bool flag2 = lastSoloDutyQuestId != result;
							if (result == 811)
							{
								if (flag2)
								{
									log.Information("[QuestRotation] Quest 811 Solo Duty - disabling RSR Auto; other configured Solo Duty combat handling remains available");
									lastSoloDutyQuestId = result;
								}
							}
							else if (flag2)
							{
								log.Information("[QuestRotation] Solo Duty entered - combat commands will activate after 8s");
								lastSoloDutyQuestId = result;
							}
							if (result == 4591 && stepsOfFaithHandler != null)
							{
								IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
								string characterName = ((localPlayer != null) ? $"{localPlayer.Name}@{localPlayer.HomeWorld.Value.Name}" : string.Empty);
								if (stepsOfFaithHandler.ShouldActivate(result, isInSoloDuty: true))
								{
									log.Information("[QuestRotation] Triggering Steps of Faith handler (will wait for conditions inside)...");
									Task.Run(delegate
									{
										stepsOfFaithHandler.Execute(characterName);
									});
								}
							}
							if (result == 811)
							{
								try
								{
									commandManager.ProcessCommand("/rotation off");
									log.Information("[QuestRotation] âœ“ /rotation off sent for Quest 811");
								}
								catch (Exception ex)
								{
									log.Error("[QuestRotation] Failed to stop RSR: " + ex.Message);
								}
							}
						}
					}
					combatDutyDetection.AcknowledgeDutyEntry();
				}
				if (combatDutyDetection.JustExitedDuty && isRotationActive && combatJobSetupPassedForCurrentLogin && dungeonAutomation != null)
				{
					dungeonAutomation.OnDutyExited();
					if ((DateTime.Now - combatDutyDetection.DutyExitTime).TotalSeconds >= 8.0)
					{
						if (dungeonAutomation.IsInAutoDutyDungeon)
						{
							dungeonAutomation.DisbandParty();
						}
						if (submarinesReadyDuringDuty)
						{
							log.Information("[QuestRotation] Submarines were ready during duty - deferring quest restart to handle submarines");
							if (isLevelingModeActive)
							{
								log.Information("[QuestRotation] Stopping Leveling Mode for deferred submarines");
								questionableIPC.StopLevelingMode();
								isLevelingModeActive = false;
							}
							submarinesReadyDuringDuty = false;
							lastSubmarineCheckTime = DateTime.MinValue;
						}
						else
						{
							MsqLevelLockData msqLevelLockInfo = questionableIPC.GetMsqLevelLockInfo();
							if (msqLevelLockInfo != null && msqLevelLockInfo.IsLevelLocked)
							{
								log.Debug("[QuestRotation] Leveling Mode active (Level Locked) - Skipping standard Quest restart");
							}
							else
							{
								if (!CanIssueQuestionableStart("after duty exit"))
								{
									return;
								}
								try
								{
									TryIssueQuestionableStart("after duty exit");
								}
								catch (Exception ex2)
								{
									log.Error("[QuestRotation] Failed to restart quest: " + ex2.Message);
								}
							}
						}
						combatDutyDetection.ClearDutyExitFlag();
					}
				}
				if (combatDutyDetection.ShouldPauseAutomation && movementMonitor != null && movementMonitor.IsMonitoring)
				{
					movementMonitor.StopMonitoring();
					log.Debug("[QuestRotation] Movement monitor paused for combat/duty");
				}
				else if (!combatDutyDetection.ShouldPauseAutomation && movementMonitor != null && !movementMonitor.IsMonitoring && isRotationActive && configuration.EnableMovementMonitor && currentState.Phase != RotationPhase.WaitingForCharacterLogin && currentState.Phase != RotationPhase.WaitingBeforeCharacterSwitch && currentState.Phase != RotationPhase.WaitingForPreCharacterSwitchTasks && currentState.Phase != RotationPhase.WaitingForHomeworldReturn && currentState.Phase != RotationPhase.ScanningQuests && currentState.Phase != RotationPhase.CheckingQuestCompletion && currentState.Phase != RotationPhase.InitializingFirstCharacter && currentState.Phase != RotationPhase.DCTraveling && currentState.Phase != RotationPhase.ProcessingPostMoogle && currentState.Phase != RotationPhase.WaitingForSafeLocation)
				{
					movementMonitor.StartMonitoring();
					log.Debug("[QuestRotation] Movement monitor resumed after combat/duty");
				}
			}
			if (submarineManager.IsSubmarineJustCompleted && !submarineManager.IsSubmarineCooldownActive())
			{
				log.Information("[QuestRotation] Submarine cooldown expired - re-enabling submarine checks");
				submarineManager.ClearSubmarineJustCompleted();
			}
			if (!isRotationActive)
			{
				return;
			}
			CheckLevelStopCondition();
			if (isRotationActive && (currentState.Phase == RotationPhase.Questing || currentState.Phase == RotationPhase.QuestActive || currentState.Phase == RotationPhase.WaitingForQuestStart) && dcTravelService != null && dcTravelService.HasUnlockedDCTravel() && configuration.EnableDCTravel && !dcTravelService.IsDCTravelCompleted())
			{
				log.Information("[QuestRotation] Dynamic Unlock Detected: DC Travel/Post Moogle is now available!");
				log.Information("[QuestRotation] Interrupting current quest to perform DC Travel/Post Moogle sequence...");
				try
				{
					commandManager.ProcessCommand("/qst stop");
				}
				catch (Exception ex3)
				{
					log.Error("[QuestRotation] Failed to stop quest for dynamic unlock: " + ex3.Message);
				}
				currentState.Phase = RotationPhase.DCTraveling;
				currentState.PhaseStartTime = DateTime.Now;
				Task.Delay(1000, _cts.Token).ContinueWith(delegate
				{
					if (!_cts.Token.IsCancellationRequested)
					{
						framework.RunOnFrameworkThread(delegate
						{
							PerformDCTravelAndStartQuest();
						});
					}
				}, _cts.Token);
				return;
			}
			switch (currentState.Phase)
			{
			case RotationPhase.InitializingFirstCharacter:
				HandleInitializingFirstCharacter();
				break;
			case RotationPhase.WaitingForCharacterLogin:
				HandleWaitingForCharacterLogin();
				break;
			case RotationPhase.ScanningQuests:
				HandleScanningQuests();
				break;
			case RotationPhase.CheckingQuestCompletion:
				HandleCheckingQuestCompletion();
				break;
			case RotationPhase.ProcessingPostMoogle:
				HandlePostMoogleProcessing();
				break;
			case RotationPhase.DCTraveling:
				if ((DateTime.Now - currentState.PhaseStartTime).TotalMinutes > 3.0)
				{
					log.Error("[QuestRotation] DC Travel timeout after 3 minutes - skipping character");
					SkipToNextCharacter();
				}
				else if (currentState.Phase == RotationPhase.DCTraveling && dcTravelService != null && dcTravelService.IsDCTravelCompleted() && Plugin.ObjectTable.LocalPlayer != null)
				{
					log.Information("[QuestRotation] DC Travel completed flag detected - transition to WaitingForQuestStart");
					currentState.Phase = RotationPhase.WaitingForQuestStart;
					currentState.PhaseStartTime = DateTime.Now;
				}
				break;
			case RotationPhase.WaitingForQuestStart:
			case RotationPhase.Questing:
			case RotationPhase.QuestActive:
				HandleQuestMonitoring();
				break;
			case RotationPhase.WaitingBeforeCharacterSwitch:
				HandleWaitingBeforeCharacterSwitch();
				break;
			case RotationPhase.WaitingForPreCharacterSwitchTasks:
				HandleWaitingForPreCharacterSwitchTasks();
				break;
			case RotationPhase.WaitingForHomeworldReturn:
				HandleWaitingForHomeworldReturn();
				break;
			case RotationPhase.Completed:
				HandleCompleted();
				break;
			}
			consecutiveFrameworkUpdateErrors = 0;
		}
		catch (Exception ex4)
		{
			consecutiveFrameworkUpdateErrors++;
			DateTime now2 = DateTime.Now;
			if (lastFrameworkUpdateErrorLogTime == DateTime.MinValue || now2 - lastFrameworkUpdateErrorLogTime >= FrameworkUpdateErrorLogCooldown)
			{
				log.Error("[QuestRotation] CRITICAL ERROR in OnFrameworkUpdate: " + ex4.Message);
				log.Debug("[QuestRotation] Stack Trace: " + ex4.StackTrace);
				lastFrameworkUpdateErrorLogTime = now2;
			}
			if (consecutiveFrameworkUpdateErrors >= 3)
			{
				frameworkUpdateDisabledByCriticalError = true;
				isRotationActive = false;
				dungeonAutomation?.SetSupportDutyMode();
				currentState.Phase = RotationPhase.Error;
				currentState.ErrorMessage = ex4.Message;
				log.Error($"[QuestRotation] Framework update loop disabled after {consecutiveFrameworkUpdateErrors} consecutive errors. Rotation stopped to prevent log spam.");
			}
			if ((DateTime.Now - lastCheckTime).TotalSeconds > 5.0)
			{
				lastCheckTime = DateTime.Now.AddSeconds(5.0);
			}
		}
	}

	private void CheckLevelStopCondition()
	{
		if ((currentState.Phase == RotationPhase.Questing || currentState.Phase == RotationPhase.QuestActive || currentState.Phase == RotationPhase.WaitingForQuestStart) && !IsLoggedInAsRotationCharacter())
		{
			log.Warning($"[QuestRotation] Skipping level stop check because local player '{GetLocalCharacterKey()}' does not match rotation character '{currentState.CurrentCharacter}'.");
			return;
		}
		StopConditionData levelStopCondition = questionableIPC.GetLevelStopCondition();
		if (levelStopCondition == null || !levelStopCondition.Enabled)
		{
			return;
		}
		int targetValue = levelStopCondition.TargetValue;
		UpdateCurrentCharacterJobLevels();
		(int, uint, string) knownHighestCombatJobLevel = GetKnownHighestCombatJobLevel(currentState.CurrentCharacter);
		if (knownHighestCombatJobLevel.Item1 <= 0)
		{
			log.Debug("[QuestRotation] Skipping level stop check - no known combat level for " + currentState.CurrentCharacter);
		}
		else
		{
			if (knownHighestCombatJobLevel.Item1 < targetValue)
			{
				return;
			}
			log.Information($"[QuestRotation] Level Stop Condition reached ({knownHighestCombatJobLevel.Item3} Lv. {knownHighestCombatJobLevel.Item1} >= {targetValue})");
			if (currentState.Phase == RotationPhase.Questing || currentState.Phase == RotationPhase.QuestActive || currentState.Phase == RotationPhase.WaitingForQuestStart)
			{
				log.Information("[QuestRotation] Level reached - stopping quest and switching character...");
				try
				{
					commandManager.ProcessCommand("/qst stop");
					log.Information("[QuestRotation] âœ“ /qst stop (level stop condition)");
				}
				catch (Exception ex)
				{
					log.Error("[QuestRotation] Failed to send /qst stop: " + ex.Message);
				}
				MarkCharacterCompleted(currentState.CurrentCharacter, "level reached");
				BeginPreCharacterSwitchTasks();
			}
		}
	}

	private unsafe bool IsTerritoryLoaded()
	{
		GameMain* ptr = GameMain.Instance();
		if (ptr == null)
		{
			return false;
		}
		return ptr->TerritoryLoadState == 2;
	}

	private static string GetLocalCharacterKey()
	{
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return string.Empty;
		}
		return $"{localPlayer.Name}@{localPlayer.HomeWorld.Value.Name}";
	}

	private bool IsLoggedInAsRotationCharacter()
	{
		string localCharacterKey = GetLocalCharacterKey();
		string currentCharacter = currentState.CurrentCharacter;
		if (string.IsNullOrEmpty(localCharacterKey) || string.IsNullOrEmpty(currentCharacter))
		{
			return false;
		}
		if (!string.Equals(localCharacterKey, currentCharacter, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		RotationHandoffCheckpoint rotationHandoff = configuration.RotationHandoff;
		ulong contentId;
		ulong num = ((rotationHandoff != null && string.Equals(rotationHandoff.ExpectedCharacterKey, currentCharacter, StringComparison.OrdinalIgnoreCase)) ? rotationHandoff.ExpectedContentId : (autoRetainerIpc.TryGetContentId(currentCharacter, out contentId) ? contentId : 0));
		if (num != 0L)
		{
			return playerState.ContentId == num;
		}
		return false;
	}

	private (int Level, uint JobId, string Source) GetKnownHighestCombatJobLevel(string characterName)
	{
		if (string.IsNullOrWhiteSpace(characterName))
		{
			return (Level: 0, JobId: 0u, Source: "none");
		}
		if (configuration.CharacterJobLevels.TryGetValue(characterName, out CharacterJobLevelSnapshot value))
		{
			CombatJobResolution combatJobResolution = CombatJobResolverLogic.MergeTrustedAndObservedLevels(value.CombatJobLevels, value.XadbObservedCombatJobLevels);
			if ((object)combatJobResolution != null && combatJobResolution.HighestLevel > 0)
			{
				return (Level: combatJobResolution.HighestLevel, JobId: combatJobResolution.HighestJobId, Source: "plugin snapshot");
			}
		}
		try
		{
			(int, uint) highestCombatJobLevelAndId = autoRetainerIpc.GetHighestCombatJobLevelAndId(characterName);
			if (highestCombatJobLevelAndId.Item1 > 0)
			{
				return (Level: highestCombatJobLevelAndId.Item1, JobId: highestCombatJobLevelAndId.Item2, Source: "AutoRetainer");
			}
		}
		catch (Exception ex)
		{
			log.Debug("[QuestRotation] Failed to get AR combat level for " + characterName + ": " + ex.Message);
		}
		return (Level: 0, JobId: 0u, Source: "unknown");
	}

	private bool HandleCombatJobSetupAfterLogin(string characterName)
	{
		if (combatJobSetupTask == null)
		{
			RotationHandoffCheckpoint? matchingRotationHandoff = GetMatchingRotationHandoff(characterName);
			bool flag = matchingRotationHandoff?.CombatJobPreparationRequired ?? false;
			uint value = matchingRotationHandoff?.PreferredCombatJobId ?? 0;
			if (matchingRotationHandoff == null)
			{
				flag = configuration.QuestRotationCombatJobByCharacter.TryGetValue(characterName, out value);
			}
			if (!flag)
			{
				combatJobSetupPassedForCurrentLogin = true;
				UpdateRotationHandoffStage(RotationHandoffRecoveryStage.CombatJobPrepared);
				return true;
			}
			UpdateRotationHandoffStage(RotationHandoffRecoveryStage.PreparingCombatJob);
			combatJobSetupCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
			combatJobSetupCharacter = characterName;
			combatJobSetupTask = RunCombatJobSetupAsync(characterName, value, combatJobSetupCancellationTokenSource.Token);
			_relogProcessStarted = true;
			string value2 = ((value == 0) ? "the highest combat job" : $"combat job {value}");
			log.Information($"[QuestRotation] Login validated for {characterName}; preparing {value2} before quest startup.");
			return false;
		}
		if (!string.Equals(combatJobSetupCharacter, characterName, StringComparison.OrdinalIgnoreCase))
		{
			log.Warning($"[QuestRotation] Cancelling combat job setup for '{combatJobSetupCharacter}' because the active rotation character changed to '{characterName}'.");
			ResetCombatJobSetup();
			return false;
		}
		if (!combatJobSetupTask.IsCompleted)
		{
			return false;
		}
		bool result = combatJobSetupTask.GetAwaiter().GetResult();
		ClearCombatJobSetup();
		if (result)
		{
			combatJobSetupPassedForCurrentLogin = true;
			UpdateRotationHandoffStage(RotationHandoffRecoveryStage.CombatJobPrepared);
			log.Information("[QuestRotation] Combat job setup completed for " + characterName + ".");
			return true;
		}
		if (!isRotationActive)
		{
			return false;
		}
		log.Error("[QuestRotation] Combat job setup failed for " + characterName + "; skipping before quest startup.");
		SkipToNextCharacter("combat job setup failed");
		return false;
	}

	private async Task<bool> RunCombatJobSetupAsync(string characterName, uint combatJobId, CancellationToken cancellationToken)
	{
		_ = 1;
		try
		{
			await jobStoneGearsetReconciliation.ReconcileCurrentAsync("quest-rotation combat-job selection", cancellationToken);
			return await huntLogAutomationService.PrepareCombatJobForQuestRotationAsync(combatJobId, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			log.Debug("[QuestRotation] Combat job setup cancelled for " + characterName + ".");
			return false;
		}
		catch (Exception ex2)
		{
			log.Error("[QuestRotation] Combat job setup crashed for " + characterName + ": " + ex2.Message);
			return false;
		}
	}

	private void ResetCombatJobSetup()
	{
		combatJobSetupPassedForCurrentLogin = false;
		try
		{
			combatJobSetupCancellationTokenSource?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		ClearCombatJobSetup();
	}

	private void ClearCombatJobSetup()
	{
		combatJobSetupCancellationTokenSource?.Dispose();
		combatJobSetupCancellationTokenSource = null;
		combatJobSetupTask = null;
		combatJobSetupCharacter = string.Empty;
	}

	private bool CanIssueQuestionableStart(string context)
	{
		if (automaticClassUnlockInterruptionActive)
		{
			log.Debug("[QuestRotation] Suppressed /qst start " + context + ": automatic Class Unlock is active.");
			return false;
		}
		if (!combatJobSetupPassedForCurrentLogin)
		{
			log.Warning("[QuestRotation] Suppressed /qst start " + context + ": combat job setup has not passed for this login.");
			return false;
		}
		if (!IsLoggedInAsRotationCharacter())
		{
			log.Warning($"[QuestRotation] Suppressed /qst start {context}: local character '{GetLocalCharacterKey()}' does not match rotation character '{currentState.CurrentCharacter}'.");
			return false;
		}
		return true;
	}

	private bool TryIssueQuestionableStart(string context)
	{
		if (!CanIssueQuestionableStart(context))
		{
			return false;
		}
		if (ConfirmRotationHandoffStartupIfObserved())
		{
			return true;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (lastQuestionableStartCommandTimeUtc != DateTime.MinValue && string.Equals(lastQuestionableStartCharacter, currentState.CurrentCharacter, StringComparison.OrdinalIgnoreCase) && utcNow - lastQuestionableStartCommandTimeUtc < RotationHandoffLogic.QuestStartRetryInterval)
		{
			return false;
		}
		RotationHandoffCheckpoint matchingRotationHandoff = GetMatchingRotationHandoff(currentState.CurrentCharacter);
		if (!RotationHandoffLogic.ShouldIssueQuestStart(matchingRotationHandoff, utcNow, IsLoggedInAsRotationCharacter(), combatJobSetupPassedForCurrentLogin, questStartupObserved: false))
		{
			log.Debug("[QuestRotation] Suppressed duplicate /qst start " + context + "; waiting for the persisted command window.");
			if (matchingRotationHandoff == null)
			{
				return false;
			}
			return matchingRotationHandoff.RecoveryStage == RotationHandoffRecoveryStage.QuestStartRequested;
		}
		lastQuestionableStartCommandTimeUtc = utcNow;
		lastQuestionableStartCharacter = currentState.CurrentCharacter;
		if (matchingRotationHandoff != null)
		{
			matchingRotationHandoff.RecoveryStage = RotationHandoffRecoveryStage.QuestStartRequested;
			matchingRotationHandoff.QuestStartCommandIssuedUtc = utcNow;
			matchingRotationHandoff.UpdatedUtc = utcNow;
			configuration.Save();
		}
		try
		{
			commandManager.ProcessCommand("/qst start");
			log.Information("[QuestRotation] Sent /qst start " + context);
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[QuestRotation] Failed to send /qst start " + context + ": " + ex.Message);
			return false;
		}
	}

	private bool ConfirmRotationHandoffStartupIfObserved()
	{
		if (!IsLoggedInAsRotationCharacter())
		{
			return false;
		}
		RotationHandoffCheckpoint matchingRotationHandoff = GetMatchingRotationHandoff(currentState.CurrentCharacter);
		bool flag = HasLocalQuestionableStartRequest();
		bool flag2 = matchingRotationHandoff != null && matchingRotationHandoff.RecoveryStage == RotationHandoffRecoveryStage.QuestStartRequested && matchingRotationHandoff.QuestStartCommandIssuedUtc != DateTime.MinValue;
		if (!currentState.HasQuestBeenAccepted && (!(flag || flag2) || !IsQuestionableStartupObserved()))
		{
			return false;
		}
		if (configuration.RotationHandoff != null)
		{
			ClearRotationHandoff("Questionable quest startup confirmed");
		}
		return true;
	}

	private bool IsQuestionableStartupObserved()
	{
		if (questionableIPC.IsRunning())
		{
			return true;
		}
		return questionableIPC.GetCurrentTask() != null;
	}

	private bool HasLocalQuestionableStartRequest()
	{
		if (lastQuestionableStartCommandTimeUtc != DateTime.MinValue)
		{
			return string.Equals(lastQuestionableStartCharacter, currentState.CurrentCharacter, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private void ResetQuestionableStartRetry()
	{
		lastQuestionableStartCommandTimeUtc = DateTime.MinValue;
		lastQuestionableStartCharacter = string.Empty;
	}

	private RotationHandoffCheckpoint? GetMatchingRotationHandoff(string characterKey)
	{
		RotationHandoffCheckpoint rotationHandoff = configuration.RotationHandoff;
		if (rotationHandoff == null || !string.Equals(rotationHandoff.ExpectedCharacterKey, characterKey, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return rotationHandoff;
	}

	private bool EnsureRotationHandoff(string characterKey)
	{
		if (!autoRetainerIpc.TryGetContentId(characterKey, out var contentId) || contentId == 0L)
		{
			log.Error("[QuestRotation] Cannot persist a handoff for " + characterKey + ": exact ContentId is unavailable.");
			return false;
		}
		uint value;
		bool flag = configuration.QuestRotationCombatJobByCharacter.TryGetValue(characterKey, out value);
		RotationRunMode rotationRunMode = (currentState.IsSyncOnlyMode ? RotationRunMode.SyncOnly : ((currentState.CurrentStopQuestId == 0) ? RotationRunMode.LevelOnly : RotationRunMode.Quest));
		RotationHandoffCheckpoint matchingRotationHandoff = GetMatchingRotationHandoff(characterKey);
		if (matchingRotationHandoff != null && matchingRotationHandoff.ExpectedContentId == contentId && RotationHandoffLogic.Validate(matchingRotationHandoff, DateTime.UtcNow) == RotationHandoffValidation.Valid && matchingRotationHandoff.RunMode == rotationRunMode && matchingRotationHandoff.StopQuestId == currentState.CurrentStopQuestId && matchingRotationHandoff.CombatJobPreparationRequired == flag && matchingRotationHandoff.PreferredCombatJobId == value && matchingRotationHandoff.SelectedCharacters.SequenceEqual<string>(currentState.SelectedCharacters, StringComparer.OrdinalIgnoreCase) && matchingRotationHandoff.CompletedCharacters.SequenceEqual<string>(currentState.CompletedCharacters, StringComparer.OrdinalIgnoreCase) && matchingRotationHandoff.RemainingCharacters.SequenceEqual<string>(currentState.RemainingCharacters, StringComparer.OrdinalIgnoreCase))
		{
			return true;
		}
		ResetQuestionableStartRetry();
		configuration.RotationHandoff = RotationHandoffLogic.Create(rotationRunMode, contentId, characterKey, flag, value, currentState.SelectedCharacters, currentState.CompletedCharacters, currentState.RemainingCharacters, currentState.CurrentStopQuestId, DateTime.UtcNow);
		configuration.Save();
		ResetHandoffStabilityGate();
		log.Information($"[QuestRotation] Persisted {rotationRunMode} handoff for {characterKey} ({contentId}) before relog.");
		return true;
	}

	private void MarkRelogCommandPending()
	{
		RotationHandoffCheckpoint matchingRotationHandoff = GetMatchingRotationHandoff(currentState.CurrentCharacter);
		if (matchingRotationHandoff != null)
		{
			DateTime utcNow = DateTime.UtcNow;
			matchingRotationHandoff.RecoveryStage = RotationHandoffRecoveryStage.WaitingForExactLogin;
			matchingRotationHandoff.RelogCommandIssuedUtc = utcNow;
			matchingRotationHandoff.UpdatedUtc = utcNow;
			configuration.Save();
		}
	}

	private bool IssueCharacterSwitchWithHandoff(string characterKey)
	{
		if (!EnsureRotationHandoff(characterKey))
		{
			return false;
		}
		if (IsLoggedInAsRotationCharacter())
		{
			UpdateRotationHandoffStage(RotationHandoffRecoveryStage.ExactLoginConfirmed);
			return true;
		}
		MarkRelogCommandPending();
		if (autoRetainerIpc.SwitchCharacter(characterKey))
		{
			return true;
		}
		log.Warning("[QuestRotation] AutoRetainer did not accept the relog to " + characterKey + "; the persisted handoff is retained for bounded recovery.");
		return false;
	}

	private void UpdateRotationHandoffStage(RotationHandoffRecoveryStage stage)
	{
		RotationHandoffCheckpoint matchingRotationHandoff = GetMatchingRotationHandoff(currentState.CurrentCharacter);
		if (matchingRotationHandoff != null && matchingRotationHandoff.RecoveryStage != stage)
		{
			matchingRotationHandoff.RecoveryStage = stage;
			matchingRotationHandoff.UpdatedUtc = DateTime.UtcNow;
			configuration.Save();
		}
	}

	private void ClearRotationHandoff(string reason, RotationHandoffLifecycleEvent? lifecycleEvent = null)
	{
		if ((!lifecycleEvent.HasValue || RotationHandoffLogic.ShouldClearForLifecycle(lifecycleEvent.Value)) && configuration.RotationHandoff != null)
		{
			configuration.RotationHandoff = null;
			configuration.Save();
			ResetHandoffStabilityGate();
			handoffRecoveryAnnounced = false;
			log.Information("[QuestRotation] Cleared durable rotation handoff: " + reason + ".");
		}
	}

	private void TryRecoverRotationHandoff()
	{
		RotationHandoffCheckpoint rotationHandoff = configuration.RotationHandoff;
		switch (RotationHandoffLogic.Validate(rotationHandoff, DateTime.UtcNow))
		{
		case RotationHandoffValidation.Expired:
			ClearRotationHandoff("checkpoint expired after 30 minutes");
			break;
		default:
			if (rotationHandoff != null)
			{
				if (!handoffRecoveryAnnounced)
				{
					handoffRecoveryAnnounced = true;
					log.Information($"[QuestRotation] Recovering interrupted handoff to {rotationHandoff.ExpectedCharacterKey} ({rotationHandoff.ExpectedContentId}) at {rotationHandoff.RecoveryStage}; Auto Start remains suppressed.");
				}
				autoRetainerIpc.TryReinitialize();
				questionableIPC.TryEnsureAvailableSilent();
				bool flag = autoRetainerIpc.IsAvailable && questionableIPC.IsAvailable;
				bool transitionActive = IsHandoffTransitionActive();
				string localCharacterKey = GetLocalCharacterKey();
				ulong num = (clientState.IsLoggedIn ? playerState.ContentId : 0);
				(bool, bool) tuple = ObserveStableHandoffWorld(rotationHandoff, transitionActive, num, localCharacterKey);
				bool questStartupObserved = tuple.Item2 && IsQuestionableStartupObserved();
				switch (RotationHandoffLogic.DecideResumeAction(rotationHandoff, DateTime.UtcNow, flag, transitionActive, tuple.Item2, tuple.Item1 ? num : 0, tuple.Item1 ? localCharacterKey : string.Empty, questStartupObserved))
				{
				case RotationHandoffResumeAction.ClearExpired:
					ClearRotationHandoff("checkpoint expired after 30 minutes");
					break;
				case RotationHandoffResumeAction.ClearMalformed:
					ClearRotationHandoff("checkpoint was malformed");
					break;
				case RotationHandoffResumeAction.ClearIdentityMismatch:
					ClearRotationHandoff($"stable identity {localCharacterKey} ({num}) definitively differed from the persisted destination");
					break;
				case RotationHandoffResumeAction.ClearStartupConfirmed:
					if (ReconstructRotationFromHandoff(rotationHandoff, startupAlreadyObserved: true))
					{
						ClearRotationHandoff("quest startup was already active after reload");
					}
					break;
				case RotationHandoffResumeAction.ReconstructAtLogin:
				case RotationHandoffResumeAction.ReconstructAtJobPreparation:
				case RotationHandoffResumeAction.ReconstructAtQuestStartup:
					ReconstructRotationFromHandoff(rotationHandoff, startupAlreadyObserved: false);
					break;
				case RotationHandoffResumeAction.WaitForDestination:
				{
					if (!flag || !autoRetainerIpc.TryGetContentId(rotationHandoff.ExpectedCharacterKey, out var contentId))
					{
						break;
					}
					if (contentId != rotationHandoff.ExpectedContentId)
					{
						ClearRotationHandoff($"AutoRetainer now maps {rotationHandoff.ExpectedCharacterKey} to {contentId}, not the persisted ContentId {rotationHandoff.ExpectedContentId}");
						break;
					}
					bool exactDestination = RotationHandoffLogic.IsExactDestination(rotationHandoff, num, localCharacterKey);
					if (RotationHandoffLogic.ShouldIssueRecoveryRelog(rotationHandoff, DateTime.UtcNow, exactDestination, transitionActive, IsHandoffExternalBusy()))
					{
						currentState.CurrentCharacter = rotationHandoff.ExpectedCharacterKey;
						MarkRelogCommandPending();
						if (!autoRetainerIpc.SwitchCharacter(rotationHandoff.ExpectedCharacterKey))
						{
							log.Warning("[QuestRotation] AutoRetainer did not accept the recovered relog request; the durable checkpoint remains available for the next bounded retry.");
						}
						else
						{
							log.Information("[QuestRotation] Reissued the persisted relog to " + rotationHandoff.ExpectedCharacterKey + " after live-state reconciliation.");
						}
					}
					break;
				}
				case RotationHandoffResumeAction.WaitForStableWorld:
					break;
				}
				break;
			}
			goto case RotationHandoffValidation.Malformed;
		case RotationHandoffValidation.Malformed:
			ClearRotationHandoff("checkpoint was malformed");
			break;
		}
	}

	private bool ReconstructRotationFromHandoff(RotationHandoffCheckpoint checkpoint, bool startupAlreadyObserved)
	{
		if (checkpoint.RunMode == RotationRunMode.Quest)
		{
			StopPoint stopPoint = stopPoints.FirstOrDefault((StopPoint point) => point.QuestId == checkpoint.StopQuestId);
			if (stopPoint == null)
			{
				ClearRotationHandoff($"persisted stop quest {checkpoint.StopQuestId} is no longer configured");
				return false;
			}
			foreach (StopPoint stopPoint2 in stopPoints)
			{
				stopPoint2.IsActive = stopPoint2 == stopPoint;
			}
		}
		ResetCombatJobSetup();
		currentState = new RotationState
		{
			CurrentStopQuestId = checkpoint.StopQuestId,
			SelectedCharacters = checkpoint.SelectedCharacters.ToList(),
			CompletedCharacters = checkpoint.CompletedCharacters.ToList(),
			RemainingCharacters = checkpoint.RemainingCharacters.ToList(),
			CurrentCharacter = checkpoint.ExpectedCharacterKey,
			NextCharacter = checkpoint.ExpectedCharacterKey,
			IsSyncOnlyMode = (checkpoint.RunMode == RotationRunMode.SyncOnly),
			Phase = (startupAlreadyObserved ? RotationPhase.WaitingForQuestStart : RotationPhase.WaitingForCharacterLogin),
			PhaseStartTime = DateTime.Now,
			RotationStartTime = checkpoint.CreatedUtc.ToLocalTime(),
			HasQuestBeenAccepted = false
		};
		isRotationActive = true;
		dungeonAutomation?.SetDutyModeBasedOnConfig();
		_lastRelogCommandTime = DateTime.Now;
		_relogProcessStarted = true;
		combatDutyDetection?.SetRotationActive(active: true);
		deathHandler?.SetRotationActive(active: true);
		if (startupAlreadyObserved)
		{
			combatJobSetupPassedForCurrentLogin = true;
		}
		else
		{
			UpdateRotationHandoffStage(RotationHandoffRecoveryStage.ExactLoginConfirmed);
		}
		log.Information($"[QuestRotation] Reconstructed {checkpoint.RunMode} rotation handoff for {checkpoint.ExpectedCharacterKey}; remaining={checkpoint.RemainingCharacters.Count}.");
		return true;
	}

	private (bool StableIdentity, bool StableWorld) ObserveStableHandoffWorld(RotationHandoffCheckpoint checkpoint, bool transitionActive, ulong observedContentId, string observedCharacterKey)
	{
		if (transitionActive || observedContentId == 0L || string.IsNullOrWhiteSpace(observedCharacterKey))
		{
			ResetHandoffStabilityGate();
			return (StableIdentity: false, StableWorld: false);
		}
		bool flag = clientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null && IsTerritoryLoaded() && !condition[ConditionFlag.BetweenAreas] && !condition[35] && !condition[ConditionFlag.LoggingOut];
		string a = $"{observedContentId}:{observedCharacterKey}:{clientState.TerritoryType}:{flag}";
		if (!string.Equals(a, handoffStableWorldKey, StringComparison.OrdinalIgnoreCase))
		{
			handoffStableWorldKey = a;
			handoffStableWorldReads = 1;
		}
		else
		{
			handoffStableWorldReads++;
		}
		bool num = handoffStableWorldReads >= 4;
		bool flag2 = RotationHandoffLogic.IsExactDestination(checkpoint, observedContentId, observedCharacterKey);
		return (StableIdentity: num, StableWorld: num && flag2 && flag);
	}

	private bool IsHandoffTransitionActive()
	{
		if (clientState.IsLoggedIn && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.LoggingOut] && !condition[35])
		{
			return clientState.TerritoryType == 0;
		}
		return true;
	}

	private bool IsHandoffExternalBusy()
	{
		if (!autoRetainerIpc.TryGetBusy(out var busy) || busy)
		{
			return true;
		}
		if (!autoRetainerIpc.GetSuppressed() && !condition[ConditionFlag.LoggingOut])
		{
			LifestreamIPC? obj = lifestreamIPC;
			if (obj != null && obj.IsAvailable)
			{
				return lifestreamIPC.IsBusy();
			}
			return false;
		}
		return true;
	}

	private void ResetHandoffStabilityGate()
	{
		handoffStableWorldKey = string.Empty;
		handoffStableWorldReads = 0;
	}

	private void HandleInitializingFirstCharacter()
	{
		if (currentState.RemainingCharacters.Count == 0)
		{
			log.Information("[QuestRotation] No remaining characters - rotation complete");
			currentState.Phase = RotationPhase.Completed;
			isRotationActive = false;
			dungeonAutomation?.SetSupportDutyMode();
			ClearRotationHandoff("rotation completed with no remaining characters", RotationHandoffLifecycleEvent.NormalCompletion);
			combatDutyDetection?.SetRotationActive(active: false);
			deathHandler?.SetRotationActive(active: false);
			return;
		}
		string text = currentState.RemainingCharacters[0];
		ResetCombatJobSetup();
		currentState.CurrentCharacter = text;
		log.Information("[QuestRotation] >>> Phase 1: Initializing first character: " + text);
		if (!EnsureRotationHandoff(text))
		{
			currentState.Phase = RotationPhase.Error;
			currentState.ErrorMessage = "Could not resolve exact ContentId for " + text;
		}
		else if (IsLoggedInAsRotationCharacter())
		{
			UpdateRotationHandoffStage(RotationHandoffRecoveryStage.ExactLoginConfirmed);
			currentState.Phase = RotationPhase.WaitingForCharacterLogin;
			currentState.PhaseStartTime = DateTime.Now;
			_lastRelogCommandTime = DateTime.Now;
			_relogProcessStarted = true;
			log.Information("[QuestRotation] " + text + " is already active; validating login before quest startup.");
		}
		else if (IssueCharacterSwitchWithHandoff(text))
		{
			currentState.Phase = RotationPhase.WaitingForCharacterLogin;
			currentState.PhaseStartTime = DateTime.Now;
			_lastRelogCommandTime = DateTime.Now;
			_relogProcessStarted = false;
			log.Information("[QuestRotation] Character switch initiated to " + text);
		}
		else
		{
			log.Error("[QuestRotation] Failed to switch to " + text);
			currentState.Phase = RotationPhase.Error;
			currentState.ErrorMessage = "Failed to switch to " + text;
		}
	}

	private void HandleWaitingForCharacterLogin()
	{
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
			log.Debug("[QuestRotation] Movement monitor stopped during character login");
		}
		_ = (DateTime.Now - currentState.PhaseStartTime).TotalSeconds;
		bool flag = false;
		if (lifestreamIPC != null && lifestreamIPC.IsAvailable && lifestreamIPC.IsBusy())
		{
			flag = true;
		}
		if (!flag)
		{
			AutoRetainerIPC autoRetainerIPC = autoRetainerIpc;
			if (autoRetainerIPC != null && autoRetainerIPC.GetSuppressed())
			{
				flag = true;
			}
		}
		if (!flag && condition[ConditionFlag.LoggingOut])
		{
			flag = true;
		}
		if (flag && !_relogProcessStarted)
		{
			_relogProcessStarted = true;
			log.Information("[QuestRotation] Relog/Logout process detected (Busy). Disabling retry to avoid queue spam.");
		}
		if (!_relogProcessStarted)
		{
			if ((DateTime.Now - _lastRelogCommandTime).TotalSeconds > 5.0)
			{
				log.Warning("[QuestRotation] Lifestream is NOT busy and 5s passed. Retrying relog command for " + currentState.CurrentCharacter + "...");
				if (IssueCharacterSwitchWithHandoff(currentState.CurrentCharacter))
				{
					_lastRelogCommandTime = DateTime.Now;
					log.Information("[QuestRotation] Relog command retried.");
				}
			}
		}
		else if ((DateTime.Now - _lastRelogCommandTime).TotalSeconds > 15.0 && !IsLoggedInAsRotationCharacter())
		{
			log.Warning("[QuestRotation] Relog still not completed after 15s. Retrying relog command for " + currentState.CurrentCharacter + "...");
			if (IssueCharacterSwitchWithHandoff(currentState.CurrentCharacter))
			{
				_lastRelogCommandTime = DateTime.Now;
				log.Information("[QuestRotation] Relog command retried while waiting for login.");
			}
		}
		string currentCharacter = autoRetainerIpc.GetCurrentCharacter();
		if (string.IsNullOrEmpty(currentCharacter) || !(currentCharacter == currentState.CurrentCharacter) || Plugin.ObjectTable.LocalPlayer == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.LoggingOut] || condition[35])
		{
			return;
		}
		if (!IsLoggedInAsRotationCharacter())
		{
			if (combatJobSetupTask != null)
			{
				ResetCombatJobSetup();
			}
		}
		else
		{
			if ((DateTime.Now - currentState.PhaseStartTime).TotalSeconds < 0.5 || (DateTime.Now - currentState.PhaseStartTime).TotalSeconds < 1.5)
			{
				return;
			}
			UpdateRotationHandoffStage(RotationHandoffRecoveryStage.ExactLoginConfirmed);
			if (!HandleCombatJobSetupAfterLogin(currentCharacter))
			{
				return;
			}
			log.Information("[QuestRotation] >>> Phase 2: Successfully logged in as " + currentCharacter);
			if (submarineManager.IsReloginInProgress)
			{
				submarineManager.CompleteSubmarineRelog();
			}
			arrTrialAutomationService?.Reset();
			if (preCheckService != null)
			{
				log.Information("[QuestRotation] Scanning quest status for current character...");
				try
				{
					preCheckService.ScanCurrentCharacterQuestStatus();
				}
				catch (Exception ex)
				{
					log.Error("[QuestRotation] Error scanning quest status: " + ex.Message);
				}
			}
			currentState.Phase = RotationPhase.ScanningQuests;
			currentState.PhaseStartTime = DateTime.Now;
			RotationHandoffCheckpoint? rotationHandoff = configuration.RotationHandoff;
			if (rotationHandoff != null && rotationHandoff.RunMode == RotationRunMode.SyncOnly)
			{
				ClearRotationHandoff("sync handoff completed after exact login and job preparation");
			}
			log.Information("[QuestRotation] Starting quest scan for " + currentState.CurrentCharacter + "...");
			string localCharName = GetLocalCharacterKey();
			Task.Run(delegate
			{
				ScanAndSaveAllCompletedQuests(currentState.CurrentCharacter, localCharName);
			});
		}
	}

	private void HandleScanningQuests()
	{
		if ((DateTime.Now - currentState.PhaseStartTime).TotalSeconds > 30.0)
		{
			log.Warning("[QuestRotation] Quest scan timeout - proceeding anyway");
			currentState.Phase = RotationPhase.CheckingQuestCompletion;
			currentState.PhaseStartTime = DateTime.Now;
		}
	}

	private bool ShouldReturnHomeworldBeforePostMoogle()
	{
		try
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer == null)
			{
				return false;
			}
			return localPlayer.CurrentWorld.RowId != localPlayer.HomeWorld.RowId;
		}
		catch
		{
			return false;
		}
	}

	private bool BeginHomeworldReturnForPostMoogle(string source)
	{
		if (!ShouldReturnHomeworldBeforePostMoogle())
		{
			return false;
		}
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		log.Information($"[QuestRotation] {source}: Character is DC traveled (Current: {localPlayer?.CurrentWorld.Value.Name}, Home: {localPlayer?.HomeWorld.Value.Name}). Returning home before Post Moogle.");
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
			log.Information("[QuestRotation] Movement monitor paused for homeworld return before Post Moogle");
		}
		_returnHomeworldForPostMoogle = true;
		currentState.Phase = RotationPhase.WaitingForHomeworldReturn;
		currentState.PhaseStartTime = DateTime.Now;
		_homeworldCommandSent = false;
		_homeworldTravelStarted = false;
		return true;
	}

	private void EnableTextAdvanceForStopPointRotationStart()
	{
		try
		{
			if (commandManager.ProcessCommand("/at y"))
			{
				log.Information("[QuestRotation] Sent /at y at Stop Points rotation start.");
			}
			else
			{
				log.Warning("[QuestRotation] /at y was not accepted at Stop Points rotation start.");
			}
		}
		catch (Exception ex)
		{
			log.Warning("[QuestRotation] Could not send /at y at Stop Points rotation start: " + ex.Message);
		}
	}

	private unsafe void HandleCheckingQuestCompletion()
	{
		if (currentState.CurrentCharacter == null || Plugin.ObjectTable.LocalPlayer == null || condition[ConditionFlag.BetweenAreas] || condition[35] || condition[ConditionFlag.LoggingOut])
		{
			return;
		}
		if ((submarineManager.IsSubmarinePaused || submarineManager.IsSubmarineJustCompleted) && !IsLoggedInAsRotationCharacter())
		{
			string localCharacterKey = GetLocalCharacterKey();
			if (!string.IsNullOrEmpty(localCharacterKey))
			{
				log.Warning($"[QuestRotation] Character mismatch during quest completion check (post-submarine). Expected '{currentState.CurrentCharacter}', local player is '{localCharacterKey}'. Returning to login validation.");
				ResetCombatJobSetup();
				currentState.Phase = RotationPhase.WaitingForCharacterLogin;
				currentState.PhaseStartTime = DateTime.Now;
				_lastRelogCommandTime = DateTime.MinValue;
				_relogProcessStarted = false;
			}
			return;
		}
		if (configuration.EnablePostMoogleMailCheck && dcTravelService.HasUnlockedDCTravel())
		{
			if (_moogleCheckStartTime == DateTime.MinValue)
			{
				_moogleCheckStartTime = DateTime.Now;
			}
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName("_DTR");
			bool num = addonByName != IntPtr.Zero && ((AtkUnitBase*)(nint)addonByName)->IsVisible;
			double totalSeconds = (DateTime.Now - _moogleCheckStartTime).TotalSeconds;
			if (!num)
			{
				if (totalSeconds < 10.0)
				{
					return;
				}
				log.Warning("[QuestRotation] Timed out waiting for _DTR addon (10s). Proceeding with check (might be missed).");
			}
			else if (totalSeconds < 0.5)
			{
				return;
			}
		}
		uint currentStopQuestId = currentState.CurrentStopQuestId;
		bool flag = false;
		try
		{
			flag = QuestManager.IsQuestComplete((ushort)(currentStopQuestId % 65536));
		}
		catch (Exception ex)
		{
			log.Error("[QuestRotation] Error checking quest completion: " + ex.Message);
		}
		if (flag)
		{
			MarkCharacterCompleted(currentState.CurrentCharacter, $"quest {currentStopQuestId} already complete");
			MarkQuestCompleted(currentStopQuestId, currentState.CurrentCharacter);
			SkipToNextCharacter();
			return;
		}
		if (currentStopQuestId == 0)
		{
			if (currentState.IsSyncOnlyMode)
			{
				log.Information("[QuestRotation] Sync-Only Mode: Quest scan complete for " + currentState.CurrentCharacter + " - moving to next character");
				MarkCharacterCompleted(currentState.CurrentCharacter, "quest data synchronized");
				SkipToNextCharacter();
				return;
			}
			StopConditionData levelStopCondition = questionableIPC.GetLevelStopCondition();
			if (levelStopCondition != null && levelStopCondition.Enabled)
			{
				UpdateCurrentCharacterJobLevels();
				(int, uint, string) knownHighestCombatJobLevel = GetKnownHighestCombatJobLevel(currentState.CurrentCharacter);
				if (knownHighestCombatJobLevel.Item1 > 0)
				{
					if (knownHighestCombatJobLevel.Item1 >= levelStopCondition.TargetValue)
					{
						log.Information($"[QuestRotation] {currentState.CurrentCharacter} already at target combat level from {knownHighestCombatJobLevel.Item3} (Lv. {knownHighestCombatJobLevel.Item1} >= {levelStopCondition.TargetValue}) - skipping");
						MarkCharacterCompleted(currentState.CurrentCharacter, "target level reached");
						SkipToNextCharacter();
						return;
					}
					log.Information($"[QuestRotation] Level-Only Mode: {currentState.CurrentCharacter} highest combat level is {knownHighestCombatJobLevel.Item1} from {knownHighestCombatJobLevel.Item3}, targeting {levelStopCondition.TargetValue}");
				}
				else
				{
					log.Information("[QuestRotation] Level-Only Mode: No known combat level for " + currentState.CurrentCharacter + ", starting quest flow so level can be detected.");
				}
			}
		}
		log.Information($"[QuestRotation] {currentState.CurrentCharacter} needs to complete quest {currentStopQuestId}");
		if (configuration.EnablePostMoogleMailCheck && dcTravelService != null && dcTravelService.HasUnlockedDCTravel())
		{
			_postMoogleGateWasLocked = false;
			if (postMoogleService == null)
			{
				log.Error("[QuestRotation] CRITICAL: PostMoogleService is NULL!");
			}
			else
			{
				bool flag2 = false;
				try
				{
					flag2 = postMoogleService.IsMailNotificationVisible();
					log.Information($"[QuestRotation] Post Moogle mail notification check: {flag2}");
				}
				catch (Exception ex2)
				{
					log.Error("[QuestRotation] Error checking mail notification: " + ex2.Message);
				}
				if (!flag2)
				{
					log.Information("[QuestRotation] No mail notification detected. Skipping Post Moogle travel.");
				}
				else
				{
					if (BeginHomeworldReturnForPostMoogle("Post Moogle pre-check"))
					{
						return;
					}
					bool? flag3 = postMoogleService.EnsureInCityOrTeleport();
					if (flag3 == true)
					{
						log.Information("[QuestRotation] Starting Post Moogle sequence at supported city (mail + consumables)...");
						currentState.Phase = RotationPhase.ProcessingPostMoogle;
						currentState.PhaseStartTime = DateTime.Now;
						if (movementMonitor != null && movementMonitor.IsMonitoring)
						{
							movementMonitor.StopMonitoring();
						}
						_consumablesProcessedThisStopPoint = true;
						postMoogleService.StartProcessing();
						return;
					}
					if (flag3 == false)
					{
						log.Information("[QuestRotation] Moving to supported Post Moogle city before mail check...");
						if (movementMonitor != null && movementMonitor.IsMonitoring)
						{
							movementMonitor.StopMonitoring();
						}
						currentState.Phase = RotationPhase.WaitingForSafeLocation;
						currentState.PhaseStartTime = DateTime.Now;
						Task.Delay(5000, _cts.Token).ContinueWith((Task _) => framework.RunOnFrameworkThread(delegate
						{
							currentState.Phase = RotationPhase.CheckingQuestCompletion;
						}), _cts.Token);
						return;
					}
					log.Information("[QuestRotation] Cannot teleport to a supported Post Moogle city - skipping mail processing.");
				}
			}
		}
		else if (configuration.EnablePostMoogleMailCheck && dcTravelService != null)
		{
			_postMoogleGateWasLocked = true;
			log.Debug("[QuestRotation] Post Moogle gate locked - no gate quests completed yet, will activate when unlocked");
		}
		if (!_consumablesProcessedThisStopPoint && postMoogleService != null && postMoogleService.HasConsumablesInInventory())
		{
			log.Information("[QuestRotation] Consumables detected in inventory (no mail) - using them before starting quest...");
			currentState.Phase = RotationPhase.ProcessingPostMoogle;
			currentState.PhaseStartTime = DateTime.Now;
			if (movementMonitor != null && movementMonitor.IsMonitoring)
			{
				movementMonitor.StopMonitoring();
				log.Information("[QuestRotation] Movement monitor paused for consumables processing");
			}
			_consumablesProcessedThisStopPoint = true;
			postMoogleService.StartConsumablesOnly();
			return;
		}
		if (configuration.EnableDCTravel && dcTravelService != null && dcTravelService.ShouldPerformDCTravel())
		{
			log.Information("[QuestRotation] === DC TRAVEL REQUIRED ===");
			log.Information("[QuestRotation] Character switch initiated to " + currentState.CurrentCharacter);
			_lastRelogCommandTime = DateTime.Now;
			_relogProcessStarted = false;
			currentState.Phase = RotationPhase.DCTraveling;
			currentState.PhaseStartTime = DateTime.Now;
			PerformDCTravelAndStartQuest();
			return;
		}
		log.Information("[QuestRotation] >>> Phase 4: Waiting for quest to start...");
		if (configuration.EnableMovementMonitor && movementMonitor != null && !movementMonitor.IsMonitoring)
		{
			movementMonitor.StartMonitoring();
			log.Information("[QuestRotation] Movement monitor started for quest");
		}
		if (CanIssueQuestionableStart("after quest prechecks"))
		{
			try
			{
				TryIssueQuestionableStart("after quest prechecks");
			}
			catch (Exception ex3)
			{
				log.Error("[QuestRotation] Failed to send /qst start: " + ex3.Message);
			}
			currentState.Phase = RotationPhase.WaitingForQuestStart;
			currentState.HasQuestBeenAccepted = false;
			currentState.PhaseStartTime = DateTime.Now;
		}
	}

	private void PerformDCTravelAndStartQuest()
	{
		if (!isRotationActive || dcTravelService == null)
		{
			return;
		}
		if (configuration.EnablePostMoogleMailCheck && dcTravelService.HasUnlockedDCTravel())
		{
			if (postMoogleService == null)
			{
				log.Error("[QuestRotation] CRITICAL: PostMoogleService is NULL!");
			}
			else
			{
				bool flag = false;
				try
				{
					flag = postMoogleService.IsMailNotificationVisible();
					log.Information($"[QuestRotation] Post Moogle mail notification before DC Travel: {flag}");
				}
				catch (Exception ex)
				{
					log.Error("[QuestRotation] Error checking mail notification: " + ex.Message);
				}
				if (!flag)
				{
					log.Information("[QuestRotation] No mail notification before DC Travel. Skipping Post Moogle travel.");
				}
				else
				{
					if (BeginHomeworldReturnForPostMoogle("Post Moogle before DC Travel"))
					{
						return;
					}
					bool? flag2 = postMoogleService.EnsureInCityOrTeleport();
					if (flag2 == true)
					{
						log.Information("[QuestRotation] Starting Post Moogle sequence before DC Travel (mail + consumables)...");
						currentState.Phase = RotationPhase.ProcessingPostMoogle;
						currentState.PhaseStartTime = DateTime.Now;
						if (movementMonitor != null && movementMonitor.IsMonitoring)
						{
							movementMonitor.StopMonitoring();
							log.Information("[QuestRotation] Movement monitor paused for Post Moogle processing");
						}
						_consumablesProcessedThisStopPoint = true;
						postMoogleService.StartProcessing();
						return;
					}
					if (flag2 == false)
					{
						log.Information("[QuestRotation] Moving to supported Post Moogle city before DC Travel...");
						if (movementMonitor != null && movementMonitor.IsMonitoring)
						{
							movementMonitor.StopMonitoring();
							log.Information("[QuestRotation] Movement monitor paused for Post Moogle travel");
						}
						log.Information("[QuestRotation] Waiting 5s for teleport to complete, then retrying...");
						Task.Delay(5000, _cts.Token).ContinueWith((Task _) => framework.RunOnFrameworkThread((System.Action)PerformDCTravelAndStartQuest), _cts.Token);
						return;
					}
					log.Warning("[QuestRotation] Cannot teleport for Post Moogle processing (no unlocked aetherytes). Skipping mail.");
				}
			}
		}
		if (!_consumablesProcessedThisStopPoint && postMoogleService != null && postMoogleService.HasConsumablesInInventory())
		{
			log.Information("[QuestRotation] Consumables detected in inventory (no mail) - using them before DC Travel...");
			currentState.Phase = RotationPhase.ProcessingPostMoogle;
			currentState.PhaseStartTime = DateTime.Now;
			if (movementMonitor != null && movementMonitor.IsMonitoring)
			{
				movementMonitor.StopMonitoring();
				log.Information("[QuestRotation] Movement monitor paused for consumables processing");
			}
			_consumablesProcessedThisStopPoint = true;
			postMoogleService.StartConsumablesOnly();
			return;
		}
		Task.Run(async delegate
		{
			_ = 2;
			try
			{
				bool flag3 = true;
				if (configuration.EnableDCTravel && dcTravelService.HasUnlockedDCTravel())
				{
					flag3 = await dcTravelService.PerformDCTravel();
				}
				if (flag3)
				{
					log.Information("[QuestRotation] DC travel phase completed/skipped - waiting 2s before starting quest...");
					await Task.Delay(2000);
					framework.RunOnFrameworkThread(delegate
					{
						if (!CanIssueQuestionableStart("after DC travel (attempt 1)"))
						{
							return;
						}
						try
						{
							TryIssueQuestionableStart("after DC travel (attempt 1)");
						}
						catch (Exception ex3)
						{
							log.Error("[QuestRotation] Failed to send /qst start (Attempt 1): " + ex3.Message);
						}
					});
					await Task.Delay(2000);
					framework.RunOnFrameworkThread(delegate
					{
						if (!CanIssueQuestionableStart("after DC travel (attempt 2)"))
						{
							return;
						}
						try
						{
							TryIssueQuestionableStart("after DC travel (attempt 2)");
						}
						catch (Exception ex3)
						{
							log.Error("[QuestRotation] Failed to send /qst start (Attempt 2): " + ex3.Message);
						}
					});
					currentState.Phase = RotationPhase.WaitingForQuestStart;
					currentState.HasQuestBeenAccepted = false;
					currentState.PhaseStartTime = DateTime.Now;
				}
				else
				{
					log.Error("[QuestRotation] DC travel failed - skipping character");
					SkipToNextCharacter();
				}
			}
			catch (Exception ex2)
			{
				log.Error("[QuestRotation] DC travel error: " + ex2.Message);
				SkipToNextCharacter();
			}
		});
	}

	private unsafe void HandleQuestMonitoring()
	{
		uint questId = currentState.CurrentStopQuestId;
		if (_postMoogleGateWasLocked && configuration.EnablePostMoogleMailCheck && dcTravelService != null && dcTravelService.HasUnlockedDCTravel())
		{
			_postMoogleGateWasLocked = false;
			log.Information("[QuestRotation] â\u02dc… Post Moogle gate just unlocked during questing! Checking for mail...");
			if (postMoogleService != null)
			{
				bool flag = false;
				try
				{
					flag = postMoogleService.IsMailNotificationVisible();
					log.Information($"[QuestRotation] Runtime Post Moogle mail notification check: {flag}");
				}
				catch (Exception ex)
				{
					log.Error("[QuestRotation] Error checking runtime mail notification: " + ex.Message);
				}
				if (!flag)
				{
					log.Information("[QuestRotation] Runtime Post Moogle gate unlocked but no mail notification detected.");
					return;
				}
				if (BeginHomeworldReturnForPostMoogle("Runtime Post Moogle gate"))
				{
					return;
				}
				bool? flag2 = postMoogleService.EnsureInCityOrTeleport();
				if (flag2 == true)
				{
					log.Information("[QuestRotation] Post Moogle gate unlocked. Stopping Questionable and starting NPC mail flow...");
					commandManager.ProcessCommand("/qst stop");
					log.Information("[QuestRotation] âœ“ /qst stop (before runtime Post Moogle)");
					currentState.Phase = RotationPhase.ProcessingPostMoogle;
					currentState.PhaseStartTime = DateTime.Now;
					if (movementMonitor != null && movementMonitor.IsMonitoring)
					{
						movementMonitor.StopMonitoring();
					}
					postMoogleService.StartProcessing();
					return;
				}
				if (flag2 == false)
				{
					log.Information("[QuestRotation] Post Moogle gate unlocked. Moving to supported city...");
					if (movementMonitor != null && movementMonitor.IsMonitoring)
					{
						movementMonitor.StopMonitoring();
					}
					currentState.Phase = RotationPhase.WaitingForSafeLocation;
					currentState.PhaseStartTime = DateTime.Now;
					Task.Delay(5000, _cts.Token).ContinueWith((Task _) => framework.RunOnFrameworkThread(delegate
					{
						currentState.Phase = RotationPhase.CheckingQuestCompletion;
					}), _cts.Token);
					return;
				}
				log.Information("[QuestRotation] Post Moogle gate unlocked but no supported city teleport is available - will retry next cycle");
				_postMoogleGateWasLocked = true;
			}
		}
		bool flag3 = false;
		StopPoint stopPoint = stopPoints.FirstOrDefault((StopPoint sp) => sp.QuestId == questId && sp.IsActive);
		if (stopPoint != null && stopPoint.Sequence.HasValue)
		{
			bool flag4 = false;
			try
			{
				if (questId != 0)
				{
					flag4 = QuestManager.IsQuestComplete((ushort)(questId % 65536));
				}
			}
			catch
			{
			}
			if (flag4)
			{
				flag3 = true;
				log.Debug($"[QuestRotation] Stop sequence reached (Quest {questId} Completed) - skipping submarine check");
			}
			else
			{
				string currentQuestId = questionableIPC.GetCurrentQuestId();
				byte? currentSequence = questionableIPC.GetCurrentSequence();
				if (!string.IsNullOrEmpty(currentQuestId) && currentSequence.HasValue && uint.TryParse(currentQuestId, out var result) && result == questId && currentSequence.Value >= stopPoint.Sequence.Value)
				{
					flag3 = true;
					log.Debug($"[QuestRotation] Stop sequence reached (Quest {questId} Seq {currentSequence.Value} >= {stopPoint.Sequence.Value}) - skipping submarine check");
				}
			}
		}
		bool flag5 = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
		bool flag6 = (combatDutyDetection != null && (combatDutyDetection.IsInDuty || combatDutyDetection.IsInDutyQueue)) || flag5;
		if (!flag3 && !submarineManager.IsSubmarinePaused && !submarineManager.IsSubmarineCooldownActive())
		{
			TimeSpan timeSpan = TimeSpan.FromSeconds(configuration.SubmarineCheckInterval);
			if (DateTime.Now - lastSubmarineCheckTime >= timeSpan)
			{
				lastSubmarineCheckTime = DateTime.Now;
				if (submarineManager.CheckSubmarines())
				{
					if (!flag6)
					{
						log.Information("[QuestRotation] ========================================");
						log.Information("[QuestRotation] === SUBMARINES READY - WAITING FOR QUEST COMPLETION ===");
						log.Information("[QuestRotation] ========================================");
						waitingForQuestAcceptForSubmarines = true;
						log.Information("[QuestRotation] Waiting for current quest to complete, then will pause for submarines...");
						return;
					}
					if (!submarinesReadyDuringDuty)
					{
						log.Information("[QuestRotation] Submarines became ready while in Duty (Main Loop) - will handle after duty");
						submarinesReadyDuringDuty = true;
					}
				}
			}
		}
		if (waitingForQuestAcceptForSubmarines && flag6)
		{
			log.Information("[QuestRotation] Detected Duty while waiting for submarine trigger - aborting immediate trigger and deferring.");
			waitingForQuestAcceptForSubmarines = false;
			submarinesReadyDuringDuty = true;
		}
		if (waitingForQuestAcceptForSubmarines && !flag6)
		{
			if (QuestManager.Instance() == null)
			{
				return;
			}
			byte? currentSequence2 = questionableIPC.GetCurrentSequence();
			if (currentSequence2 == 0 || !currentSequence2.HasValue)
			{
				log.Information("[QuestRotation] ========================================");
				log.Information("[QuestRotation] === READY FOR SUBMARINES (IDLE/SEQ 0)  ===");
				log.Information("[QuestRotation] ========================================");
				log.Information("[QuestRotation] Stopping Questionable...");
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst stop");
				}).Wait();
				log.Information("[QuestRotation] âœ“ /qst stop command sent");
				log.Information("[QuestRotation] Enabling Multi-Mode...");
				submarineManager.EnableMultiMode();
				waitingForQuestAcceptForSubmarines = false;
				if (movementMonitor != null && movementMonitor.IsMonitoring)
				{
					movementMonitor.StopMonitoring();
					log.Information("[QuestRotation] Movement monitor stopped for submarine operations");
				}
				log.Information("[QuestRotation] Multi-Mode enabled - submarines will now run");
			}
		}
		else if (submarineManager.IsSubmarinePaused)
		{
			if (submarineManager.IsExternalPaused)
			{
				return;
			}
			if (movementMonitor != null && movementMonitor.IsMonitoring)
			{
				movementMonitor.StopMonitoring();
				log.Debug("[QuestRotation] Movement monitor stopped during submarine multi-mode");
			}
			log.Debug("[QuestRotation] Submarines running in Multi-Mode...");
			int num = submarineManager.CheckSubmarinesSoon();
			if (num == 0)
			{
				log.Information("[QuestRotation] ========================================");
				log.Information("[QuestRotation] === NO SUBMARINES IN NEXT 2 MINUTES ===");
				log.Information("[QuestRotation] ========================================");
				log.Information("[QuestRotation] Disabling Multi-Mode and returning to character...");
				submarineManager.DisableMultiModeAndReturn();
				string currentCharacter = currentState.CurrentCharacter;
				log.Information("[QuestRotation] Relogging to " + currentCharacter + "...");
				if (IssueCharacterSwitchWithHandoff(currentCharacter))
				{
					log.Information("[QuestRotation] Relog initiated - waiting for character login...");
					ResetCombatJobSetup();
					currentState.Phase = RotationPhase.WaitingForCharacterLogin;
					currentState.PhaseStartTime = DateTime.Now;
					_lastRelogCommandTime = DateTime.Now;
					_relogProcessStarted = false;
					if (dcTravelService != null)
					{
						dcTravelService.ResetDCTravelState();
						log.Information("[QuestRotation] DC Travel state reset after submarine operations");
					}
					log.Information("[QuestRotation] Questionable will resume after login validation and DC Travel checks");
				}
				else
				{
					log.Error("[QuestRotation] Failed to relog to " + currentCharacter + "!");
				}
			}
			else
			{
				log.Debug($"[QuestRotation] Submarine ready in {num}s - waiting...");
			}
		}
		else
		{
			if (submarineManager.IsSubmarineCooldownActive() || waitingForQuestAcceptForSubmarines)
			{
				return;
			}
			if (isRotationActive && !submarineManager.IsSubmarinePaused && !submarineManager.IsSubmarineCooldownActive() && currentState.Phase != RotationPhase.InitializingFirstCharacter && currentState.Phase != RotationPhase.WaitingForCharacterLogin)
			{
				if (combatDutyDetection == null)
				{
					return;
				}
				if (questionableIPC.GetCurrentTask() == null)
				{
					MsqLevelLockData msqLevelLockInfo = questionableIPC.GetMsqLevelLockInfo();
					if (msqLevelLockInfo != null && msqLevelLockInfo.IsLevelLocked)
					{
						bool flag7 = (DateTime.Now - lastLevelingLogTime).TotalSeconds > 60.0;
						if (flag7)
						{
							log.Information($"[QuestRotation] MSQ Level Lock Detected: Needed {msqLevelLockInfo.RequiredLevel}, Current {msqLevelLockInfo.RequiredLevel - msqLevelLockInfo.LevelsNeeded}");
							lastLevelingLogTime = DateTime.Now;
						}
						bool flag8 = submarineManager.CheckSubmarines();
						if (!flag6 && flag8)
						{
							if (isLevelingModeActive)
							{
								log.Information("[QuestRotation] Submarines are ready! Stopping Leveling Mode...");
								questionableIPC.StopLevelingMode();
								isLevelingModeActive = false;
								submarinesReadyDuringDuty = false;
								lastSubmarineCheckTime = DateTime.MinValue;
							}
						}
						else if (flag6 && flag8)
						{
							if (!submarinesReadyDuringDuty && flag7)
							{
								log.Information("[QuestRotation] Submarines ready but in duty - will handle after completion");
								submarinesReadyDuringDuty = true;
							}
						}
						else if (!isLevelingModeActive && !flag6)
						{
							if (flag7)
							{
								log.Information("[QuestRotation] Triggering Leveling Mode...");
							}
							questionableIPC.StartLevelingMode();
							isLevelingModeActive = true;
						}
					}
					else if (isLevelingModeActive)
					{
						isLevelingModeActive = false;
						submarinesReadyDuringDuty = false;
					}
				}
			}
			if ((submarineManager.IsSubmarinePaused || submarineManager.IsSubmarineJustCompleted) && !IsLoggedInAsRotationCharacter())
			{
				string localCharacterKey = GetLocalCharacterKey();
				if (!string.IsNullOrEmpty(localCharacterKey))
				{
					log.Warning($"[QuestRotation] Logged-in character mismatch after submarine relog. Expected '{currentState.CurrentCharacter}', but local player is '{localCharacterKey}'. Waiting for correct character.");
					ResetCombatJobSetup();
					currentState.Phase = RotationPhase.WaitingForCharacterLogin;
					currentState.PhaseStartTime = DateTime.Now;
					_lastRelogCommandTime = DateTime.MinValue;
					_relogProcessStarted = false;
				}
				return;
			}
			questTrackingService.UpdateCurrentCharacterQuests(currentState.CurrentCharacter);
			StopPoint stopPoint2 = stopPoints.FirstOrDefault((StopPoint sp) => sp.QuestId == questId && sp.IsActive);
			bool flag9 = false;
			if (stopPoint2 != null && stopPoint2.Sequence.HasValue)
			{
				bool flag10 = false;
				try
				{
					flag10 = QuestManager.IsQuestComplete((ushort)questId);
				}
				catch
				{
				}
				if (flag10)
				{
					log.Information($"[QuestRotation] âœ“ Quest {questId} already completed by {currentState.CurrentCharacter} (Pre-Check)!");
					flag9 = true;
				}
				string currentQuestId2 = questionableIPC.GetCurrentQuestId();
				byte? currentSequence3 = questionableIPC.GetCurrentSequence();
				uint result2;
				if (string.IsNullOrEmpty(currentQuestId2) && currentState.HasQuestBeenAccepted)
				{
					if (QuestManager.Instance() != null)
					{
						byte questSequence = QuestManager.GetQuestSequence((ushort)questId);
						if (questSequence >= stopPoint2.Sequence.Value)
						{
							log.Information("[QuestRotation] âœ“ Questionable auto-stopped at stop point!");
							log.Information($"[QuestRotation] Quest {questId} Sequence {questSequence} >= {stopPoint2.Sequence.Value}");
							flag9 = true;
						}
						else
						{
							log.Debug($"[QuestRotation] Questionable stopped but not at stop sequence (seq {questSequence} < {stopPoint2.Sequence.Value})");
						}
					}
				}
				else if (!string.IsNullOrEmpty(currentQuestId2) && currentSequence3.HasValue && uint.TryParse(currentQuestId2, out result2))
				{
					if (result2 == questId)
					{
						if (currentSequence3.Value >= stopPoint2.Sequence.Value)
						{
							log.Information($"[QuestRotation] âœ“ Quest {questId} Sequence {stopPoint2.Sequence.Value} reached by {currentState.CurrentCharacter}!");
							log.Information($"[QuestRotation] Current Sequence: {currentSequence3.Value} (reached {stopPoint2.Sequence.Value})");
							flag9 = true;
						}
					}
					else
					{
						bool flag11 = false;
						try
						{
							flag11 = QuestManager.IsQuestComplete((ushort)(questId % 65536));
						}
						catch (Exception ex2)
						{
							log.Error("[QuestRotation] Error checking quest completion: " + ex2.Message);
							return;
						}
						if (flag11)
						{
							log.Information($"[QuestRotation] âœ“ Quest {questId} completed by {currentState.CurrentCharacter}!");
							flag9 = true;
						}
					}
				}
			}
			else
			{
				bool flag12 = false;
				try
				{
					flag12 = QuestManager.IsQuestComplete((ushort)(questId % 65536));
				}
				catch (Exception ex3)
				{
					log.Error("[QuestRotation] Error checking quest completion: " + ex3.Message);
					return;
				}
				if (flag12)
				{
					log.Information($"[QuestRotation] âœ“ Quest {questId} completed by {currentState.CurrentCharacter}!");
					flag9 = true;
				}
			}
			if (flag9)
			{
				log.Information($"[QuestRotation] âœ“ Quest {questId} completed by {currentState.CurrentCharacter}!");
				if (stepsOfFaithHandler != null && questId == 4591)
				{
					stepsOfFaithHandler.Reset();
				}
				try
				{
					commandManager.ProcessCommand("/qst stop");
					log.Information("[QuestRotation] Sent /qst stop command");
				}
				catch (Exception ex4)
				{
					log.Error("[QuestRotation] Failed to send /qst stop: " + ex4.Message);
				}
				log.Information("[QuestRotation] Updating quest completion data for " + currentState.CurrentCharacter + "...");
				string localCharacterKey2 = GetLocalCharacterKey();
				ScanAndSaveAllCompletedQuests(currentState.CurrentCharacter, localCharacterKey2);
				MarkQuestCompleted(questId, currentState.CurrentCharacter, stopPoint2?.Sequence);
				List<string> completedCharacters = currentState.CompletedCharacters;
				List<string> remainingCharacters = currentState.RemainingCharacters;
				log.Debug($"[QuestRotation] DEBUG - Before update: CompletedCharacters count = {completedCharacters.Count}, RemainingCharacters count = {remainingCharacters.Count}");
				if (!completedCharacters.Contains(currentState.CurrentCharacter))
				{
					completedCharacters.Add(currentState.CurrentCharacter);
					log.Information("[QuestRotation] âœ“ Added " + currentState.CurrentCharacter + " to CompletedCharacters list");
				}
				else
				{
					log.Warning("[QuestRotation] " + currentState.CurrentCharacter + " was already in CompletedCharacters list!");
				}
				bool value = remainingCharacters.Remove(currentState.CurrentCharacter);
				log.Information($"[QuestRotation] Removed {currentState.CurrentCharacter} from RemainingCharacters list: {value}");
				currentState.CompletedCharacters = completedCharacters;
				currentState.RemainingCharacters = remainingCharacters;
				log.Debug($"[QuestRotation] DEBUG - After update: CompletedCharacters count = {currentState.CompletedCharacters.Count}, RemainingCharacters count = {currentState.RemainingCharacters.Count}");
				log.Debug("[QuestRotation] DEBUG - CompletedCharacters: " + string.Join(", ", currentState.CompletedCharacters));
				log.Debug("[QuestRotation] DEBUG - RemainingCharacters: " + string.Join(", ", currentState.RemainingCharacters));
				BeginPreCharacterSwitchTasks();
			}
			else
			{
				if (currentState.Phase != RotationPhase.WaitingForQuestStart)
				{
					return;
				}
				bool flag13 = false;
				try
				{
					QuestManager* ptr = QuestManager.Instance();
					if (ptr != null)
					{
						flag13 = ptr->IsQuestAccepted((ushort)questId);
					}
				}
				catch
				{
				}
				if (flag13)
				{
					log.Information($"[QuestRotation] Quest {questId} accepted by {currentState.CurrentCharacter} - now monitoring for completion");
					currentState.Phase = RotationPhase.Questing;
					currentState.PhaseStartTime = DateTime.Now;
					currentState.HasQuestBeenAccepted = true;
					ConfirmRotationHandoffStartupIfObserved();
				}
				else if (ConfirmRotationHandoffStartupIfObserved())
				{
					currentState.Phase = RotationPhase.Questing;
					currentState.PhaseStartTime = DateTime.Now;
					log.Information("[QuestRotation] Questionable startup confirmed through IPC for " + currentState.CurrentCharacter + " - phase changed to Questing");
				}
				else if (!HasLocalQuestionableStartRequest() && (DateTime.Now - currentState.PhaseStartTime).TotalSeconds >= RotationHandoffLogic.QuestStartRetryInterval.TotalSeconds)
				{
					TryIssueQuestionableStart("while waiting for startup confirmation");
				}
			}
		}
	}

	private void HandleCompleted()
	{
		ClearRotationHandoff("normal rotation completion", RotationHandoffLifecycleEvent.NormalCompletion);
		log.Information("[QuestRotation] â•\u0090â•\u0090â•\u0090 ROTATION COMPLETED â•\u0090â•\u0090â•\u0090");
		log.Information($"[QuestRotation] All {currentState.CompletedCharacters.Count} characters completed quest {currentState.CurrentStopQuestId}");
		if (dcTravelService != null && dcTravelService.IsDCTravelCompleted())
		{
			log.Information("[QuestRotation] DC Travel state reset after rotation completion");
			dcTravelService.ResetDCTravelState();
		}
		StopPoint stopPoint = stopPoints.FirstOrDefault((StopPoint sp) => sp.QuestId == currentState.CurrentStopQuestId);
		if (stopPoint != null)
		{
			stopPoint.IsActive = false;
		}
		isRotationActive = false;
		dungeonAutomation?.SetSupportDutyMode();
		combatDutyDetection?.SetRotationActive(active: false);
		combatDutyDetection?.Reset();
		deathHandler?.SetRotationActive(active: false);
		deathHandler?.SetRotationActive(active: false);
		deathHandler?.Reset();
		arrTrialAutomationService?.Reset();
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
			log.Information("[QuestRotation] Movement monitor stopped");
		}
		if (configuration.EnableSubmarineCheck)
		{
			try
			{
				commandManager.ProcessCommand("/ays set MultiModeType 2");
				log.Information("[QuestRotation] Submarine Monitoring enabled - executing '/ays set MultiModeType 2'");
			}
			catch (Exception ex)
			{
				log.Error("[QuestRotation] Failed to execute Submarine MultiMode command: " + ex.Message);
			}
		}
		if (configuration.EnableMultiModeAfterRotation)
		{
			try
			{
				commandManager.ProcessCommand("/ays multi e");
				log.Information("[QuestRotation] AR Multi-Mode enabled after rotation (/ays multi e)");
			}
			catch (Exception ex2)
			{
				log.Error("[QuestRotation] Failed to enable AR Multi-Mode: " + ex2.Message);
			}
		}
		currentState.Phase = RotationPhase.Idle;
	}

	private void BeginPreCharacterSwitchTasks()
	{
		_preSwitchTasksStarted = false;
		StopMovementMonitorForPreSwitchCleanup();
		currentState.Phase = RotationPhase.WaitingForPreCharacterSwitchTasks;
		currentState.PhaseStartTime = DateTime.Now;
		log.Information("[QuestRotation] Preparing optional pre-character-switch cleanup...");
	}

	private void HandleWaitingForPreCharacterSwitchTasks()
	{
		if (_preSwitchTasksStarted)
		{
			return;
		}
		_preSwitchTasksStarted = true;
		StopMovementMonitorForPreSwitchCleanup();
		Task.Run(async delegate
		{
			try
			{
				await ExecutePreCharacterSwitchTasksAsync();
			}
			catch (Exception ex)
			{
				log.Error("[QuestRotation] Pre-switch cleanup failed: " + ex.Message);
			}
			finally
			{
				await framework.RunOnFrameworkThread(delegate
				{
					_preSwitchTasksStarted = false;
					if (configuration.EnableDCTravel)
					{
						log.Information("[QuestRotation] Pre-switch cleanup complete - initiating homeworld return.");
						currentState.Phase = RotationPhase.WaitingForHomeworldReturn;
						currentState.PhaseStartTime = DateTime.Now;
						_homeworldCommandSent = false;
						_homeworldTravelStarted = false;
					}
					else
					{
						log.Information("[QuestRotation] Pre-switch cleanup complete - DC Travel disabled, proceeding to character switch.");
						ProceedToCharacterSwitch();
					}
				});
			}
		});
	}

	private void StopMovementMonitorForPreSwitchCleanup()
	{
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
			log.Information("[QuestRotation] Movement monitor stopped for pre-character-switch cleanup and gearset persistence.");
		}
	}

	private async Task ExecutePreCharacterSwitchTasksAsync()
	{
		CurrentGearsetPersistenceResult currentGearsetPersistenceResult = await jobStoneGearsetReconciliation.PersistCurrentGearsetAsync("quest rotation before character switch", _cts.Token);
		if (!currentGearsetPersistenceResult.Success)
		{
			log.Warning("[QuestRotation] Could not persist the current class/job gearset before switching: " + currentGearsetPersistenceResult.Reason);
		}
		if (configuration.EnableAutoRepair)
		{
			await ExecuteMainCharacterRepairIfNeededAsync();
		}
		if (configuration.EnableAysDiscard)
		{
			await ExecuteAysDiscardAsync();
		}
	}

	private async Task ExecuteMainCharacterRepairIfNeededAsync()
	{
		if (!MainCharacterNeedsRepair())
		{
			log.Information("[QuestRotation] Main character gear condition OK - skipping /ad repair.");
			return;
		}
		log.Information("[QuestRotation] Main character gear below threshold - starting /ad repair.");
		await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand("/ad repair"));
		DateTime timeout = DateTime.Now.AddMinutes(10.0);
		while (DateTime.Now < timeout)
		{
			await Task.Delay(10000);
			if (!MainCharacterNeedsRepair())
			{
				log.Information("[QuestRotation] Main character repair completed.");
				break;
			}
			log.Information("[QuestRotation] Main character still repairing... waiting 10s");
		}
		if (DateTime.Now >= timeout)
		{
			log.Warning("[QuestRotation] Main character repair timed out - proceeding anyway.");
		}
		await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand("/ad stop"));
		await Task.Delay(2000);
	}

	private unsafe bool MainCharacterNeedsRepair()
	{
		try
		{
			InventoryManager* ptr = InventoryManager.Instance();
			if (ptr == null)
			{
				return false;
			}
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(InventoryType.EquippedItems);
			if (inventoryContainer == null)
			{
				return false;
			}
			float num = 30000f;
			for (int i = 0; i < 13; i++)
			{
				InventoryItem inventoryItem = inventoryContainer->Items[i];
				if (inventoryItem.ItemId != 0 && (float)(int)inventoryItem.Condition < num)
				{
					num = (int)inventoryItem.Condition;
				}
			}
			float num2 = num / 300f;
			log.Information($"[QuestRotation] Lowest main character gear condition: {num2:F1}% (Threshold: {configuration.RepairThreshold}%)");
			return num2 <= (float)configuration.RepairThreshold;
		}
		catch (Exception ex)
		{
			log.Error("[QuestRotation] Error checking main character gear condition: " + ex.Message);
			return false;
		}
	}

	private async Task ExecuteAysDiscardAsync()
	{
		log.Information("[QuestRotation] Starting /ays discard.");
		await WaitForAutoRetainerIdleAsync(60);
		await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand("/ays discard"));
		await Task.Delay(2000);
		if (await WaitForAutoRetainerBusyStateAsync(expectedBusy: false, 300))
		{
			log.Information("[QuestRotation] /ays discard completed and AutoRetainer is idle.");
			await Task.Delay(1000);
		}
		else
		{
			log.Warning("[QuestRotation] /ays discard timeout - proceeding anyway.");
		}
	}

	private async Task WaitForAutoRetainerIdleAsync(int timeoutSeconds)
	{
		DateTime timeout = DateTime.Now.AddSeconds(timeoutSeconds);
		while (DateTime.Now < timeout && autoRetainerIpc.GetBusy())
		{
			log.Information("[QuestRotation] AutoRetainer is busy - waiting before cleanup command...");
			await Task.Delay(2000);
		}
	}

	private async Task<bool> WaitForAutoRetainerBusyStateAsync(bool expectedBusy, int timeoutSeconds)
	{
		DateTime timeout = DateTime.Now.AddSeconds(timeoutSeconds);
		while (DateTime.Now < timeout)
		{
			if (autoRetainerIpc.GetBusy() == expectedBusy)
			{
				return true;
			}
			await Task.Delay(1000);
		}
		return false;
	}

	private async Task<bool> WaitForAutoRetainerStableIdleAsync(int timeoutSeconds, int stableSeconds)
	{
		DateTime timeout = DateTime.Now.AddSeconds(timeoutSeconds);
		DateTime? idleSince = null;
		while (DateTime.Now < timeout)
		{
			if (autoRetainerIpc.GetBusy())
			{
				idleSince = null;
				log.Information("[QuestRotation] AutoRetainer became busy again - waiting for stable idle...");
				await Task.Delay(2000);
				continue;
			}
			idleSince.GetValueOrDefault();
			if (!idleSince.HasValue)
			{
				DateTime now = DateTime.Now;
				idleSince = now;
			}
			if ((DateTime.Now - idleSince.Value).TotalSeconds >= (double)stableSeconds)
			{
				return true;
			}
			await Task.Delay(1000);
		}
		return false;
	}

	private void HandleWaitingForHomeworldReturn()
	{
		double totalSeconds = (DateTime.Now - currentState.PhaseStartTime).TotalSeconds;
		if (!_homeworldCommandSent)
		{
			if (totalSeconds >= 1.0)
			{
				string text = "";
				switch (configuration.LifestreamCommand)
				{
				case LifestreamCommandType.Auto:
					text = "/li auto";
					break;
				case LifestreamCommandType.Li:
					text = "/li";
					break;
				case LifestreamCommandType.None:
					text = "";
					break;
				}
				if (string.IsNullOrEmpty(text))
				{
					log.Information("[QuestRotation] Lifestream command configured to NONE - skipping return.");
					CompleteHomeworldReturn();
					return;
				}
				log.Information("[QuestRotation] Sending mandatory " + text + "...");
				commandManager.ProcessCommand(text);
				_homeworldCommandSent = true;
				_homeworldTravelStarted = false;
			}
			return;
		}
		bool flag = false;
		if (dcTravelService != null)
		{
			flag = dcTravelService.IsLifestreamBusy();
		}
		if (!_homeworldTravelStarted)
		{
			if (flag)
			{
				log.Information("[QuestRotation] Lifestream detected busy - Travel started.");
				_homeworldTravelStarted = true;
			}
			else if (totalSeconds > 6.0)
			{
				log.Warning("[QuestRotation] Lifestream did not become busy after /li auto - assuming already at destination or idle.");
				CompleteHomeworldReturn();
			}
		}
		else if (_homeworldTravelStarted && !flag)
		{
			log.Information("[QuestRotation] Lifestream no longer busy - Travel completed.");
			log.Information("[QuestRotation] Waiting 5s for world stability...");
			CompleteHomeworldReturn();
		}
	}

	private void CompleteHomeworldReturn()
	{
		if (_returnHomeworldForPostMoogle)
		{
			_returnHomeworldForPostMoogle = false;
			log.Information("[QuestRotation] Homeworld return complete - rechecking Post Moogle from homeworld.");
			currentState.Phase = RotationPhase.CheckingQuestCompletion;
			currentState.PhaseStartTime = DateTime.Now;
			_moogleCheckStartTime = DateTime.MinValue;
		}
		else
		{
			ProceedToCharacterSwitch();
		}
	}

	private void ProceedToCharacterSwitch()
	{
		log.Information("[QuestRotation] Moving to next character switch phase...");
		currentState.Phase = RotationPhase.WaitingBeforeCharacterSwitch;
		currentState.PhaseStartTime = DateTime.Now;
	}

	private void SkipToNextCharacter(string reason = "")
	{
		try
		{
			log.Information("[QuestRotation] Sending urgent /qst stop before character skip...");
			commandManager.ProcessCommand("/qst stop");
		}
		catch (Exception ex)
		{
			log.Error("[QuestRotation] Failed to send urgent /qst stop: " + ex.Message);
		}
		if (!string.IsNullOrEmpty(reason))
		{
			List<string> skippedCharacters = currentState.SkippedCharacters;
			if (!skippedCharacters.Contains(currentState.CurrentCharacter))
			{
				skippedCharacters.Add(currentState.CurrentCharacter);
				currentState.SkippedCharacters = skippedCharacters;
			}
			List<string> remainingCharacters = currentState.RemainingCharacters;
			remainingCharacters.Remove(currentState.CurrentCharacter);
			currentState.RemainingCharacters = remainingCharacters;
			log.Warning("[QuestRotation] Skipped " + currentState.CurrentCharacter + ": " + reason);
		}
		if (preCheckService != null)
		{
			log.Information("[QuestRotation] Logging completed quests before logout...");
			preCheckService.LogCompletedQuestsBeforeLogout();
		}
		if (dcTravelService != null && dcTravelService.IsDCTravelCompleted())
		{
			log.Information("[QuestRotation] DC Travel state reset for next character");
			dcTravelService.ResetDCTravelState();
		}
		log.Information("[QuestRotation] Initiating pre-character-switch sequence...");
		BeginPreCharacterSwitchTasks();
	}

	private void MarkCharacterCompleted(string characterName, string reason = "")
	{
		List<string> completedCharacters = currentState.CompletedCharacters;
		if (!completedCharacters.Contains(characterName))
		{
			completedCharacters.Add(characterName);
			currentState.CompletedCharacters = completedCharacters;
			log.Debug("[QuestRotation] Added '" + characterName + "' to completed list" + (string.IsNullOrEmpty(reason) ? "" : (" (" + reason + ")")));
		}
		List<string> remainingCharacters = currentState.RemainingCharacters;
		bool num = remainingCharacters.Remove(characterName);
		currentState.RemainingCharacters = remainingCharacters;
		if (num)
		{
			log.Information("[QuestRotation] Removed '" + characterName + "' from remaining list");
			log.Information($"[QuestRotation] Progress: {currentState.CompletedCharacters.Count}/{currentState.SelectedCharacters.Count} completed");
		}
	}

	private void HandleWaitingBeforeCharacterSwitch()
	{
		if (movementMonitor != null && movementMonitor.IsMonitoring)
		{
			movementMonitor.StopMonitoring();
			log.Debug("[QuestRotation] Movement monitor stopped during character switch wait");
		}
		if (condition[ConditionFlag.BetweenAreas])
		{
			log.Debug("[QuestRotation] Character is between areas (Condition 32) - waiting...");
		}
		else if ((DateTime.Now - currentState.PhaseStartTime).TotalSeconds >= 2.0)
		{
			log.Debug("[QuestRotation] 2s wait complete and not between areas, performing character switch...");
			PerformCharacterSwitch();
		}
	}

	private void PerformCharacterSwitch()
	{
		if (Plugin.ObjectTable.LocalPlayer != null && configuration.EnableDCTravel && configuration.LifestreamCommand != LifestreamCommandType.None && Plugin.ObjectTable.LocalPlayer.CurrentWorld.RowId != Plugin.ObjectTable.LocalPlayer.HomeWorld.RowId)
		{
			log.Information($"[QuestRotation] Detected Foreign World (Current: {Plugin.ObjectTable.LocalPlayer.CurrentWorld.Value.Name}, Home: {Plugin.ObjectTable.LocalPlayer.HomeWorld.Value.Name}).");
			log.Information("[QuestRotation] Initiating Homeworld Return sequence before character switch...");
			currentState.Phase = RotationPhase.WaitingForHomeworldReturn;
			currentState.PhaseStartTime = DateTime.Now;
			_homeworldCommandSent = false;
			_homeworldTravelStarted = false;
		}
		else if (currentState.RemainingCharacters.Count == 0)
		{
			int num = Math.Clamp(configuration.SkippedCharacterRetryCount, 0, 99);
			List<string> list = currentState.SkippedCharacters.Where((string character) => !currentState.CompletedCharacters.Contains(character)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
			if (list.Count > 0 && skippedRetryAttempts < num)
			{
				skippedRetryAttempts++;
				currentState.RemainingCharacters = list;
				currentState.SkippedCharacters = new List<string>();
				ResetCombatJobSetup();
				currentState.CurrentCharacter = list[0];
				currentState.NextCharacter = list[0];
				currentStuckCount = 0;
				log.Warning($"[QuestRotation] Retrying skipped characters ({skippedRetryAttempts}/{num}): {string.Join(", ", list)}");
				if (IssueCharacterSwitchWithHandoff(list[0]))
				{
					currentState.Phase = RotationPhase.WaitingForCharacterLogin;
					currentState.PhaseStartTime = DateTime.Now;
					_lastRelogCommandTime = DateTime.Now;
					_relogProcessStarted = false;
				}
				else
				{
					log.Error("[QuestRotation] Failed to switch to skipped retry character " + list[0]);
					currentState.Phase = RotationPhase.Error;
					currentState.ErrorMessage = "Failed to switch to skipped retry character";
				}
				return;
			}
			int num2 = stopPoints.FindIndex((StopPoint sp) => sp.IsActive);
			if (num2 != -1 && num2 < stopPoints.Count - 1)
			{
				StopPoint stopPoint = stopPoints[num2];
				StopPoint stopPoint2 = stopPoints[num2 + 1];
				log.Information("[QuestRotation] ========================================");
				log.Information("[QuestRotation] === CURRENT STOP POINT COMPLETED ===");
				log.Information("[QuestRotation] ========================================");
				log.Information("[QuestRotation] Completed: " + stopPoint.DisplayName);
				log.Information("[QuestRotation] Moving to next stop point: " + stopPoint2.DisplayName);
				stopPoint.IsActive = false;
				stopPoint2.IsActive = true;
				skippedRetryAttempts = 0;
				_consumablesProcessedThisStopPoint = false;
				currentState.CurrentStopQuestId = stopPoint2.QuestId;
				List<string> list2 = new List<string>();
				List<string> list3 = new List<string>();
				foreach (string selectedCharacter in currentState.SelectedCharacters)
				{
					if (HasCharacterCompletedQuest(stopPoint2.QuestId, selectedCharacter, stopPoint2.Sequence))
					{
						list2.Add(selectedCharacter);
						log.Debug($"[QuestRotation] {selectedCharacter} already completed new stop point {stopPoint2.QuestId}");
					}
					else
					{
						list3.Add(selectedCharacter);
					}
				}
				currentState.CompletedCharacters = list2;
				currentState.RemainingCharacters = list3;
				log.Information($"[QuestRotation] New stop point: Remaining={list3.Count}, Completed={list2.Count}");
				if (list3.Count == 0)
				{
					log.Information("[QuestRotation] All characters already completed " + stopPoint2.DisplayName + ", moving to next...");
					return;
				}
				string text = currentState.RemainingCharacters[0];
				ResetCombatJobSetup();
				currentState.CurrentCharacter = text;
				currentState.NextCharacter = text;
				log.Information("[QuestRotation] Starting next stop point with " + text);
				if (IssueCharacterSwitchWithHandoff(text))
				{
					currentState.Phase = RotationPhase.WaitingForCharacterLogin;
					currentState.PhaseStartTime = DateTime.Now;
					_lastRelogCommandTime = DateTime.Now;
					_relogProcessStarted = false;
					currentStuckCount = 0;
				}
				else
				{
					log.Error("[QuestRotation] Failed to switch to " + text);
					currentState.Phase = RotationPhase.Error;
					currentState.ErrorMessage = "Failed to switch to " + text;
				}
			}
			else
			{
				log.Information("[QuestRotation] No more stop points to process");
				currentState.Phase = RotationPhase.Completed;
			}
		}
		else
		{
			if (dcTravelService != null)
			{
				dcTravelService.ResetDCTravelState();
				log.Information("[QuestRotation] DC Travel state reset for next character");
			}
			if (dungeonAutomation != null)
			{
				dungeonAutomation.Reset();
				log.Information("[QuestRotation] Dungeon automation state reset for next character");
			}
			_consumablesProcessedThisStopPoint = false;
			log.Debug($"[QuestRotation] DEBUG - RemainingCharacters count: {currentState.RemainingCharacters.Count}");
			log.Debug("[QuestRotation] DEBUG - RemainingCharacters list: " + string.Join(", ", currentState.RemainingCharacters));
			log.Debug($"[QuestRotation] DEBUG - CompletedCharacters count: {currentState.CompletedCharacters.Count}");
			log.Debug("[QuestRotation] DEBUG - CompletedCharacters list: " + string.Join(", ", currentState.CompletedCharacters));
			string text2 = currentState.RemainingCharacters[0];
			ResetCombatJobSetup();
			currentState.CurrentCharacter = text2;
			currentState.NextCharacter = text2;
			log.Information("[QuestRotation] Switching to next character: " + text2);
			log.Information($"[QuestRotation] Progress: {currentState.CompletedCharacters.Count}/{currentState.SelectedCharacters.Count} completed");
			if (IssueCharacterSwitchWithHandoff(text2))
			{
				currentState.Phase = RotationPhase.WaitingForCharacterLogin;
				currentState.PhaseStartTime = DateTime.Now;
				_lastRelogCommandTime = DateTime.Now;
				_relogProcessStarted = false;
				currentStuckCount = 0;
				log.Information("[QuestRotation] Character switch initiated to " + text2);
			}
			else
			{
				log.Error("[QuestRotation] Failed to switch to " + text2);
				currentState.Phase = RotationPhase.Error;
				currentState.ErrorMessage = "Failed to switch character";
			}
		}
	}

	private void HandlePostMoogleProcessing()
	{
		if (postMoogleService.IsProcessing)
		{
			return;
		}
		log.Information("[QuestRotation] Post Moogle processing completed.");
		if (configuration.EnableDCTravel && dcTravelService != null && dcTravelService.ShouldPerformDCTravel())
		{
			log.Information("[QuestRotation] DC Travel needed after Post Moogle - starting DC Travel...");
			currentState.Phase = RotationPhase.DCTraveling;
			currentState.PhaseStartTime = DateTime.Now;
			PerformDCTravelAndStartQuest();
			return;
		}
		log.Information("[QuestRotation] No DC Travel needed - resuming questing...");
		if (CanIssueQuestionableStart("after Post Moogle"))
		{
			TryIssueQuestionableStart("after Post Moogle");
			currentState.Phase = RotationPhase.WaitingForQuestStart;
			currentState.PhaseStartTime = DateTime.Now;
			if (movementMonitor != null)
			{
				movementMonitor.StartMonitoring();
			}
		}
	}

	public void Dispose()
	{
		framework.Update -= OnFrameworkUpdate;
		MovementMonitorService.OnStuckDetected -= HandleStuckDetected;
		ResetCombatJobSetup();
		_cts.Cancel();
		_cts.Dispose();
		log.Information("[QuestRotation] Service disposed");
	}
}
