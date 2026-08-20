using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public sealed class ClassUnlockRotationService : IDisposable
{
	private readonly record struct InventoryWeaponLocation(InventoryType Container, ushort Slot, uint ItemId, uint ItemLevel);

	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1L);

	private static readonly TimeSpan QuestionableStopPollInterval = TimeSpan.FromMilliseconds(100L);

	private static readonly TimeSpan QuestionableStopTimeout = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan CharacterSwitchTimeout = TimeSpan.FromMinutes(5L);

	private static readonly TimeSpan QuestTimeout = TimeSpan.FromMinutes(60L);

	private readonly object stateLock = new object();

	private readonly Configuration configuration;

	private readonly AutoRetainerIPC autoRetainer;

	private readonly QuestionableIPC questionable;

	private readonly QuestRotationExecutionService questRotation;

	private readonly HuntLogAutomationService huntLogs;

	private readonly RetainerCreationService retainers;

	private readonly PostMoogleService postMoogle;

	private readonly JobStoneGearsetReconciliationService gearsetPersistence;

	private readonly CombatJobResolver combatJobs;

	private readonly IDataManager dataManager;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly ICondition condition;

	private readonly IClientState clientState;

	private readonly IPlayerState playerState;

	private readonly IPluginLog log;

	private CancellationTokenSource? cancellationSource;

	private Task? runner;

	private string ownedPriorityQuest = string.Empty;

	private string activeOwnedQuest = string.Empty;

	private bool startedOwnedQuest;

	private bool ownedQuestStopRequested;

	private bool isStopPointInterruption;

	private ClassUnlockRunState state = IdleState();

	private bool disposed;

	public ClassUnlockRunState State
	{
		get
		{
			lock (stateLock)
			{
				return state with
				{
					Results = state.Results.ToArray()
				};
			}
		}
	}

	public ClassUnlockRotationService(Configuration configuration, AutoRetainerIPC autoRetainer, QuestionableIPC questionable, QuestRotationExecutionService questRotation, HuntLogAutomationService huntLogs, RetainerCreationService retainers, PostMoogleService postMoogle, JobStoneGearsetReconciliationService gearsetPersistence, CombatJobResolver combatJobs, IDataManager dataManager, IFramework framework, ICommandManager commandManager, ICondition condition, IClientState clientState, IPlayerState playerState, IPluginLog log)
	{
		this.configuration = configuration;
		this.autoRetainer = autoRetainer;
		this.questionable = questionable;
		this.questRotation = questRotation;
		this.huntLogs = huntLogs;
		this.retainers = retainers;
		this.postMoogle = postMoogle;
		this.gearsetPersistence = gearsetPersistence;
		this.combatJobs = combatJobs;
		this.dataManager = dataManager;
		this.framework = framework;
		this.commandManager = commandManager;
		this.condition = condition;
		this.clientState = clientState;
		this.playerState = playerState;
		this.log = log;
	}

	public bool TryStart(IEnumerable<string> characters, IEnumerable<uint> classJobIds, out string error)
	{
		string[] array = characters.Where((string value) => !string.IsNullOrWhiteSpace(value)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		uint[] array2 = classJobIds.Distinct().ToArray();
		lock (stateLock)
		{
			if (disposed)
			{
				error = "Class Unlock has been disposed.";
				return false;
			}
			Task task = runner;
			if (task != null && !task.IsCompleted)
			{
				error = "A Class Unlock rotation is already running.";
				return false;
			}
			if (questRotation.IsRotationActive || huntLogs.IsRunning || retainers.Snapshot.IsRunning)
			{
				error = "Stop the active Companion automation before starting Class Unlock.";
				return false;
			}
			if (!questionable.ForceCheckAvailability())
			{
				error = questionable.CompatibilityMessage;
				return false;
			}
			if (array.Length == 0)
			{
				error = "Select at least one character in the Characters tab.";
				return false;
			}
			if (array2.Length == 0)
			{
				error = "Select at least one class or job.";
				return false;
			}
			cancellationSource?.Dispose();
			cancellationSource = new CancellationTokenSource();
			isStopPointInterruption = false;
			state = new ClassUnlockRunState(IsRunning: true, ClassUnlockRunPhase.SwitchingCharacter, string.Empty, 0u, string.Empty, "Starting Class Unlock rotation...", Array.Empty<ClassUnlockTargetResult>());
			runner = RunAsync(array, array2, cancellationSource.Token);
		}
		error = string.Empty;
		return true;
	}

	public void Stop()
	{
		cancellationSource?.Cancel();
	}

	public void OnQuestCompleted(uint questId, string questName)
	{
		if (TryStopCompletedOwnedQuest(questId) || !configuration.ClassUnlocks.UnlockDuringStopPointRotation || !questRotation.IsRotationActive || configuration.ClassUnlocks.SelectedClassJobIds.Count == 0)
		{
			return;
		}
		RotationState currentState = questRotation.GetCurrentState();
		string currentCharacter = autoRetainer.GetCurrentCharacter();
		if (string.IsNullOrWhiteSpace(currentCharacter) || !string.Equals(currentCharacter, currentState.CurrentCharacter, StringComparison.OrdinalIgnoreCase) || questId == currentState.CurrentStopQuestId)
		{
			return;
		}
		lock (stateLock)
		{
			if (disposed)
			{
				return;
			}
			Task task = runner;
			if (task == null || task.IsCompleted)
			{
				IReadOnlyList<int> levels = ReadLiveClassJobLevelsUnsafe();
				IReadOnlyList<ClassUnlockTargetDefinition> readyAutomaticTargets = GetReadyAutomaticTargets(levels, clientState.TerritoryType);
				if (readyAutomaticTargets.Count != 0 && questRotation.TryBeginAutomaticClassUnlockInterruption(questId, currentCharacter))
				{
					cancellationSource?.Dispose();
					cancellationSource = new CancellationTokenSource();
					isStopPointInterruption = true;
					state = new ClassUnlockRunState(IsRunning: true, ClassUnlockRunPhase.PreparingCombatGearset, currentCharacter, 0u, string.Empty, $"Quest {questId} completed; checking {readyAutomaticTargets.Count} newly available Class Unlock target(s)...", Array.Empty<ClassUnlockTargetResult>());
					runner = RunStopPointInterruptionAsync(currentCharacter, questId, questName, cancellationSource.Token);
				}
			}
		}
	}

	public bool RefreshOfflineClassJobSnapshots(IEnumerable<string> characters)
	{
		bool flag = false;
		foreach (string item in characters.Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			if (autoRetainer.TryGetClassJobLevels(item, out IReadOnlyList<int> levels))
			{
				Dictionary<uint, int> dictionary = ClassUnlockCatalog.Targets.Where((ClassUnlockTargetDefinition target) => target.ExpArrayIndex >= 0 && target.ExpArrayIndex < levels.Count).ToDictionary((ClassUnlockTargetDefinition target) => target.ClassJobId, (ClassUnlockTargetDefinition target) => Math.Max(0, levels[target.ExpArrayIndex]));
				configuration.CharacterJobLevels.TryGetValue(item, out CharacterJobLevelSnapshot value);
				if (value == null)
				{
					value = new CharacterJobLevelSnapshot();
				}
				if (!value.HasAllClassJobLevels || !DictionariesEqual(value.AllClassJobLevels, dictionary))
				{
					value.AllClassJobLevels = dictionary;
					value.HasAllClassJobLevels = true;
					value.AllClassJobLevelsUpdatedUtc = DateTime.UtcNow;
					value.LastUpdatedUtc = DateTime.UtcNow;
					configuration.CharacterJobLevels[item] = value;
					flag = true;
				}
			}
		}
		if (flag)
		{
			configuration.Save();
		}
		return flag;
	}

	private IReadOnlyList<ClassUnlockTargetDefinition> GetReadyAutomaticTargets(IReadOnlyList<int> levels, uint territoryId)
	{
		if (levels.Count == 0)
		{
			return Array.Empty<ClassUnlockTargetDefinition>();
		}
		int highestCombatLevel = GetHighestCombatLevel(levels);
		ClassUnlockTargetDefinition[] source = (from target in ClassUnlockCatalog.OrderForRun(configuration.ClassUnlocks.SelectedClassJobIds, territoryId)
			where target.IsAvailable && target.QuestIds.Count > 0 && ClassUnlockCatalog.GetLevel(levels, target) <= 0 && highestCombatLevel >= target.RequiredCombatLevel && target.QuestIds.Any((string questId) => !questionable.IsQuestComplete(questId) && (questionable.IsQuestAccepted(questId) || questionable.IsReadyToAcceptQuest(questId)))
			select target).ToArray();
		uint preferredTarget = ResolveConfiguredSwitchTarget(source.Select((ClassUnlockTargetDefinition target) => target.ClassJobId));
		return source.OrderBy((ClassUnlockTargetDefinition target) => (target.ClassJobId == preferredTarget) ? 1 : 0).ToArray();
	}

	private uint ResolveConfiguredSwitchTarget(IEnumerable<uint> newlyEligibleClassJobIds)
	{
		ClassUnlockTargetDefinition[] array = (from target in newlyEligibleClassJobIds.Select(ClassUnlockCatalog.Find)
			where target?.CanContinueStopPointRotation ?? false
			select target).Cast<ClassUnlockTargetDefinition>().ToArray();
		if (array.Length == 0)
		{
			return 0u;
		}
		int num = array.Max((ClassUnlockTargetDefinition target) => target.RequiredCombatLevel);
		if (!configuration.ClassUnlocks.SwitchToClassJobIdByLevel.TryGetValue(num, out var value))
		{
			return 0u;
		}
		ClassUnlockTargetDefinition classUnlockTargetDefinition = ClassUnlockCatalog.Find(value);
		if ((object)classUnlockTargetDefinition == null || !classUnlockTargetDefinition.CanContinueStopPointRotation || classUnlockTargetDefinition.RequiredCombatLevel > num || !configuration.ClassUnlocks.SelectedClassJobIds.Contains(value))
		{
			return 0u;
		}
		return value;
	}

	private bool ShouldKeepCurrentClassAtConfiguredLevel(IReadOnlyList<ClassUnlockTargetDefinition> newlyEligibleTargets, IReadOnlyList<int> levels, uint currentClassJobId, out int currentClassLevel, out int threshold)
	{
		currentClassLevel = GetClassJobLevel(levels, currentClassJobId);
		threshold = 0;
		int num = (from target in newlyEligibleTargets
			where target.CanContinueStopPointRotation
			select target.RequiredCombatLevel).DefaultIfEmpty(0).Max();
		if (num != 0 && configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.TryGetValue(num, out threshold))
		{
			return currentClassLevel >= threshold;
		}
		return false;
	}

	private async Task RunStopPointInterruptionAsync(string character, uint completedQuestId, string completedQuestName, CancellationToken token)
	{
		List<ClassUnlockTargetResult> results = new List<ClassUnlockTargetResult>();
		string prioritySnapshot = string.Empty;
		bool priorityCaptured = false;
		bool resumeRotation = true;
		try
		{
			if (!questionable.TryExportQuestPriority(out prioritySnapshot))
			{
				throw new InvalidOperationException("Questionable's priority queue could not be backed up.");
			}
			priorityCaptured = true;
			if (!(await WaitForAutomaticUnlockSafeAsync(token)))
			{
				throw new TimeoutException("The character did not become idle after the completed quest.");
			}
			SetState(ClassUnlockRunPhase.PreparingCombatGearset, character, 0u, string.Empty, $"Saving the combat job used after {completedQuestName} ({completedQuestId})...", results);
			ClassUnlockAnchorGearset anchor = await CaptureCombatAnchorAsync(character, token);
			if (anchor == null)
			{
				throw new InvalidOperationException("The current combat gearset could not be saved.");
			}
			IReadOnlyList<int> levels = await ReadLiveClassJobLevelsAsync(token);
			IReadOnlyList<ClassUnlockTargetDefinition> readyAutomaticTargets = GetReadyAutomaticTargets(levels, await framework.RunOnFrameworkThread(() => clientState.TerritoryType));
			Dictionary<uint, int> newlyUnlockedGearsets = new Dictionary<uint, int>();
			uint switchTargetId = ResolveConfiguredSwitchTarget(readyAutomaticTargets.Select((ClassUnlockTargetDefinition classUnlockTargetDefinition2) => classUnlockTargetDefinition2.ClassJobId));
			int currentClassLevel;
			int keepCurrentThreshold;
			bool keepCurrentClass = ShouldKeepCurrentClassAtConfiguredLevel(readyAutomaticTargets, levels, anchor.ClassJobId, out currentClassLevel, out keepCurrentThreshold);
			log.Information($"[ClassUnlock] Continuation decision for {character}: anchorJob={anchor.ClassJobId}, anchorLevel={currentClassLevel}, switchTarget={switchTargetId}, keepThreshold={keepCurrentThreshold}, keepCurrent={keepCurrentClass}.");
			foreach (ClassUnlockTargetDefinition target in readyAutomaticTargets)
			{
				token.ThrowIfCancellationRequested();
				if (!questRotation.IsRotationActive)
				{
					throw new OperationCanceledException("The Stop Point rotation was stopped.", token);
				}
				if (!(await RestoreCombatAnchorAsync(character, anchor, token)))
				{
					AddResult(character, target, ClassUnlockResultKind.Failed, "The saved combat gearset could not be restored before this unlock.", results);
					continue;
				}
				(bool Unlocked, int GearsetId) outcome = await RunAutomaticTargetAsync(character, target, !keepCurrentClass, results, token);
				if (outcome.Unlocked && outcome.GearsetId >= 0)
				{
					newlyUnlockedGearsets[target.ClassJobId] = outcome.GearsetId;
				}
				bool flag = keepCurrentClass;
				if (flag)
				{
					flag = !(await RestoreCombatAnchorForContinuationAsync(character, anchor, token));
				}
				if (flag)
				{
					resumeRotation = false;
					throw new InvalidOperationException("The unlock quest changed the current job and the previous combat job could not be restored; the Stop Point rotation will remain paused.");
				}
				if (keepCurrentClass && outcome.Unlocked)
				{
					await ProcessUnlockRewardItemsAsync(target, token);
				}
			}
			int switchGearsetId = -1;
			if (switchTargetId != 0 && !keepCurrentClass && !newlyUnlockedGearsets.TryGetValue(switchTargetId, out switchGearsetId))
			{
				switchGearsetId = await FindGearsetForClassJobAsync(switchTargetId, token);
			}
			if (switchTargetId != 0 && keepCurrentClass)
			{
				log.Information($"[ClassUnlock] Keeping {character}'s previous combat job {anchor.ClassJobId} at level {currentClassLevel}; the configured switch limit is {keepCurrentThreshold}.");
				if (!(await RestoreCombatAnchorForContinuationAsync(character, anchor, token)))
				{
					resumeRotation = false;
					throw new InvalidOperationException($"The previous level {currentClassLevel} combat job could not be restored; " + "the Stop Point rotation will not resume on the newly unlocked job.");
				}
				configuration.QuestRotationCombatJobByCharacter[character] = anchor.ClassJobId;
				configuration.Save();
			}
			else if (switchTargetId != 0 && switchGearsetId >= 0)
			{
				ClassUnlockTargetDefinition classUnlockTargetDefinition = ClassUnlockCatalog.Find(switchTargetId);
				log.Information($"[ClassUnlock] Manual continuation selection for {character}: {classUnlockTargetDefinition?.Name ?? $"job {switchTargetId}"}.");
				SetState(ClassUnlockRunPhase.RestoringCombatGearset, character, switchTargetId, string.Empty, "Switching to " + (classUnlockTargetDefinition?.Name ?? $"job {switchTargetId}") + "...", results);
				await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand($"/gs change {switchGearsetId + 1}"));
				if (!(await WaitForClassJobAsync(switchTargetId, token)))
				{
					log.Warning($"[ClassUnlock] Could not switch to newly unlocked job {switchTargetId}; restoring combat anchor.");
					if (!(await RestoreCombatAnchorForContinuationAsync(character, anchor, token)))
					{
						resumeRotation = false;
						throw new InvalidOperationException("Neither the selected continuation job nor the previous combat job could be restored; the Stop Point rotation will remain paused.");
					}
				}
				else if ((await gearsetPersistence.PersistCurrentGearsetAsync("automatic Class Unlock continuation job", token)).Success)
				{
					configuration.QuestRotationCombatJobByCharacter[character] = switchTargetId;
					configuration.Save();
				}
			}
			else
			{
				if (switchTargetId != 0)
				{
					log.Warning($"[ClassUnlock] The manually selected continuation job {switchTargetId} has no usable gearset on {character}; restoring the combat anchor.");
				}
				if (!(await RestoreCombatAnchorForContinuationAsync(character, anchor, token)))
				{
					resumeRotation = false;
					throw new InvalidOperationException("The previous combat job could not be restored; the Stop Point rotation will not resume on an unintended job.");
				}
			}
			SetState(ClassUnlockRunPhase.Completed, character, 0u, string.Empty, (results.Count == 0) ? "No selected Class Unlock target remained available." : "Automatic Class Unlock completed; resuming the same Stop Point rotation.", results, isRunning: false);
		}
		catch (OperationCanceledException)
		{
			await StopOwnedQuestAndCleanupAsync();
			SetState(ClassUnlockRunPhase.Stopped, character, State.CurrentClassJobId, string.Empty, questRotation.IsRotationActive ? "Automatic Class Unlock stopped; resuming the Stop Point rotation." : "Automatic Class Unlock stopped with the Stop Point rotation.", results, isRunning: false);
		}
		catch (Exception ex2)
		{
			log.Error(ex2, "[ClassUnlock] Automatic Stop Point interruption failed");
			await StopOwnedQuestAndCleanupAsync();
			SetState(ClassUnlockRunPhase.Failed, character, State.CurrentClassJobId, string.Empty, "Automatic Class Unlock failed: " + ex2.Message, results, isRunning: false);
		}
		finally
		{
			CleanupOwnedPriority();
			bool flag = priorityCaptured;
			if (flag)
			{
				flag = !(await RestorePrioritySnapshotWithRetryAsync(prioritySnapshot));
			}
			if (flag)
			{
				resumeRotation = false;
				log.Error("[ClassUnlock] Could not restore Questionable's priority queue; automatic rotation resume was suppressed.");
				SetState(ClassUnlockRunPhase.Failed, character, State.CurrentClassJobId, string.Empty, "Questionable's priority queue could not be restored; the Stop Point rotation remains paused.", State.Results, isRunning: false);
			}
			isStopPointInterruption = false;
			questRotation.EndAutomaticClassUnlockInterruption(character, resumeRotation);
		}
	}

	private async Task<(bool Unlocked, int GearsetId)> RunAutomaticTargetAsync(string character, ClassUnlockTargetDefinition target, bool createOrUpdateGearset, List<ClassUnlockTargetResult> results, CancellationToken token)
	{
		SetState(ClassUnlockRunPhase.CheckingTarget, character, target.ClassJobId, string.Empty, "Checking " + target.Name + "...", results);
		if (ClassUnlockCatalog.GetLevel(await ReadLiveClassJobLevelsAsync(token), target) > 0)
		{
			AddResult(character, target, ClassUnlockResultKind.AlreadyUnlocked, "Already unlocked before the automatic check; skipped.", results);
			return (Unlocked: false, GearsetId: -1);
		}
		string text = string.Empty;
		foreach (string questId in target.QuestIds)
		{
			if (questionable.IsQuestComplete(questId))
			{
				continue;
			}
			bool flag = questionable.IsQuestAccepted(questId);
			bool flag2 = questionable.IsReadyToAcceptQuest(questId);
			if (!flag && !flag2)
			{
				continue;
			}
			if (!(await RunOwnedQuestAsync(character, target, questId, results, token)))
			{
				text = "Questionable did not complete quest " + questId + ".";
				continue;
			}
			IReadOnlyList<int> levels = await ReadLiveClassJobLevelsAsync(token);
			CacheLevels(character, levels);
			if (ClassUnlockCatalog.GetLevel(levels, target) <= 0)
			{
				text = $"Quest {questId} completed, but the {target.Name} unlock could not be verified.";
				continue;
			}
			if (!createOrUpdateGearset)
			{
				AddResult(character, target, ClassUnlockResultKind.Unlocked, "Unlocked; kept the previous combat job and skipped equipping the new job.", results);
				return (Unlocked: true, GearsetId: -1);
			}
			int num = await EnsureUnlockedTargetGearsetAsync(target, levels, token);
			AddResult(character, target, ClassUnlockResultKind.Unlocked, (num >= 0) ? $"Unlocked; gearset {num + 1} created or updated." : "Unlocked, but the new class/job could not be equipped for gearset creation.", results);
			return (Unlocked: true, GearsetId: num);
		}
		AddResult(character, target, ClassUnlockResultKind.NotUnlocked, string.IsNullOrWhiteSpace(text) ? ("No unlock quest is currently available (" + target.Requirement + ").") : text, results);
		return (Unlocked: false, GearsetId: -1);
	}

	private async Task<int> EnsureUnlockedTargetGearsetAsync(ClassUnlockTargetDefinition target, IReadOnlyList<int> levels, CancellationToken token)
	{
		if (await framework.RunOnFrameworkThread(() => playerState.ClassJob.RowId) != target.ClassJobId)
		{
			int targetLevel = ClassUnlockCatalog.GetLevel(levels, target);
			bool flag = !(await framework.RunOnFrameworkThread(() => TryEquipTargetMainHandUnsafe(target, targetLevel)));
			if (!flag)
			{
				flag = !(await WaitForClassJobAsync(target.ClassJobId, token));
			}
			if (flag)
			{
				return -1;
			}
		}
		await ProcessUnlockRewardItemsAsync(target, token);
		if (!(await EquipRecommendedGearAsync(target, token)))
		{
			log.Warning("[ClassUnlock] Native recommended gear could not be equipped for " + target.Abbreviation + "; continuing with the currently equipped items.");
		}
		await Task.Delay(TimeSpan.FromSeconds(2L), token);
		if (await framework.RunOnFrameworkThread(() => playerState.ClassJob.RowId) != target.ClassJobId)
		{
			log.Warning("[ClassUnlock] Recommended gear changed away from " + target.Abbreviation + "; gearset creation was aborted.");
			return -1;
		}
		CurrentGearsetPersistenceResult currentGearsetPersistenceResult = await gearsetPersistence.PersistCurrentGearsetAsync("automatic " + target.Name + " unlock", token);
		return currentGearsetPersistenceResult.Success ? currentGearsetPersistenceResult.GearsetId : (-1);
	}

	private async Task<bool> EquipRecommendedGearAsync(ClassUnlockTargetDefinition target, CancellationToken token)
	{
		try
		{
			if (!(await framework.RunOnFrameworkThread(() => SetupRecommendedGearUnsafe(target))))
			{
				return false;
			}
			DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10L);
			while (DateTime.UtcNow < deadline)
			{
				token.ThrowIfCancellationRequested();
				bool? flag = await framework.RunOnFrameworkThread((Func<bool?>)GetRecommendedGearUpdateStateUnsafe);
				if (!flag.HasValue)
				{
					log.Warning("[ClassUnlock] RecommendEquipModule became unavailable during setup.");
					return false;
				}
				if (!flag.Value)
				{
					break;
				}
				await Task.Delay(TimeSpan.FromMilliseconds(100L), token);
			}
			if (await framework.RunOnFrameworkThread((Func<bool?>)GetRecommendedGearUpdateStateUnsafe) != false)
			{
				log.Warning("[ClassUnlock] RecommendEquipModule did not finish calculating " + target.Abbreviation + " gear.");
				return false;
			}
			if (!(await framework.RunOnFrameworkThread(() => RequestRecommendedGearEquipUnsafe(target))))
			{
				return false;
			}
			await Task.Delay(TimeSpan.FromSeconds(1L), token);
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			log.Error("[ClassUnlock] Failed to equip native recommended gear for " + target.Abbreviation + ": " + ex2.Message);
			return false;
		}
	}

	private unsafe bool SetupRecommendedGearUnsafe(ClassUnlockTargetDefinition target)
	{
		RecommendEquipModule* ptr = RecommendEquipModule.Instance();
		if (ptr == null)
		{
			log.Warning("[ClassUnlock] RecommendEquipModule is unavailable.");
			return false;
		}
		if (target.ClassJobId > 255)
		{
			log.Warning($"[ClassUnlock] ClassJob {target.ClassJobId} cannot be passed to RecommendEquipModule.");
			return false;
		}
		ptr->SetupForClassJob((byte)target.ClassJobId);
		log.Information("[ClassUnlock] Native recommended gear calculation started for " + target.Abbreviation + ".");
		return true;
	}

	private unsafe static bool? GetRecommendedGearUpdateStateUnsafe()
	{
		RecommendEquipModule* ptr = RecommendEquipModule.Instance();
		if (ptr != null)
		{
			return ptr->IsUpdating;
		}
		return null;
	}

	private unsafe bool RequestRecommendedGearEquipUnsafe(ClassUnlockTargetDefinition target)
	{
		try
		{
			RecommendEquipModule* ptr = RecommendEquipModule.Instance();
			if (ptr == null)
			{
				log.Warning("[ClassUnlock] RecommendEquipModule is unavailable before equipping.");
				return false;
			}
			ptr->EquipRecommendedGear();
			log.Information("[ClassUnlock] Native recommended gear equip requested for " + target.Abbreviation + ".");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[ClassUnlock] Native recommended gear request failed for " + target.Abbreviation + ": " + ex.Message);
			return false;
		}
	}

	private async Task ProcessUnlockRewardItemsAsync(ClassUnlockTargetDefinition target, CancellationToken token)
	{
		DateTime scanDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(4L);
		bool hasRewardItems;
		do
		{
			token.ThrowIfCancellationRequested();
			hasRewardItems = await framework.RunOnFrameworkThread((Func<bool>)postMoogle.HasConsumablesInInventory);
			if (hasRewardItems)
			{
				break;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(250L), token);
		}
		while (DateTime.UtcNow < scanDeadline);
		if (!hasRewardItems)
		{
			log.Information("[ClassUnlock] No usable reward items found before equipping recommended " + target.Abbreviation + " gear.");
			return;
		}
		log.Information("[ClassUnlock] Opening usable inventory reward items before equipping recommended " + target.Abbreviation + " gear.");
		await framework.RunOnFrameworkThread((System.Action)postMoogle.StartConsumablesOnly);
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2L);
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (!(await framework.RunOnFrameworkThread(() => postMoogle.IsProcessing)))
			{
				await Task.Delay(TimeSpan.FromSeconds(1L), token);
				log.Information("[ClassUnlock] Inventory reward-item processing completed for " + target.Abbreviation + ".");
				return;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(250L), token);
		}
		await framework.RunOnFrameworkThread((System.Action)postMoogle.StopProcessing);
		log.Warning("[ClassUnlock] Timed out while opening reward items for " + target.Abbreviation + "; continuing with available gear.");
	}

	private async Task<bool> WaitForAutomaticUnlockSafeAsync(CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2L);
		bool dismountRequested = false;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (!questRotation.IsRotationActive)
			{
				throw new OperationCanceledException("The Stop Point rotation was stopped.", token);
			}
			bool mounted = condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71] || condition[ConditionFlag.InFlight];
			if (mounted && !dismountRequested)
			{
				dismountRequested = true;
				await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand("/mount"));
			}
			bool flag = mounted || condition[ConditionFlag.InCombat] || condition[ConditionFlag.Casting] || condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95] || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut] || condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] || condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78];
			if (!questionable.IsRunning() && !flag && Plugin.ObjectTable.LocalPlayer != null)
			{
				return true;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(250L), token);
		}
		return false;
	}

	private async Task<bool> RestorePrioritySnapshotWithRetryAsync(string snapshot)
	{
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			if (questionable.RestoreQuestPriority(snapshot))
			{
				return true;
			}
			log.Warning($"[ClassUnlock] Priority queue restore attempt {attempt}/3 failed.");
			await Task.Delay(TimeSpan.FromSeconds(1L));
		}
		return false;
	}

	private unsafe bool TryEquipTargetMainHandUnsafe(ClassUnlockTargetDefinition target, int targetLevel)
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return false;
		}
		ExcelSheet<Item> excelSheet = dataManager.GetExcelSheet<Item>(ClientLanguage.English);
		InventoryType[] obj = new InventoryType[5]
		{
			InventoryType.ArmoryMainHand,
			InventoryType.Inventory1,
			InventoryType.Inventory2,
			InventoryType.Inventory3,
			InventoryType.Inventory4
		};
		List<InventoryWeaponLocation> list = new List<InventoryWeaponLocation>();
		InventoryType[] array = obj;
		foreach (InventoryType inventoryType in array)
		{
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(inventoryType);
			if (inventoryContainer == null || !inventoryContainer->IsLoaded)
			{
				continue;
			}
			for (int j = 0; j < inventoryContainer->Size; j++)
			{
				InventoryItem* inventorySlot = ptr->GetInventorySlot(inventoryType, j);
				if (inventorySlot == null || inventorySlot->ItemId == 0)
				{
					continue;
				}
				uint baseItemId = inventorySlot->GetBaseItemId();
				if (excelSheet.TryGetRow(baseItemId, out var row))
				{
					EquipSlotCategory? valueNullable = row.EquipSlotCategory.ValueNullable;
					if (valueNullable.HasValue && valueNullable.GetValueOrDefault().MainHand == 1 && row.LevelEquip <= targetLevel && row.ClassJobCategory.Value.Name.ExtractText().Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains<string>(target.Abbreviation, StringComparer.OrdinalIgnoreCase))
					{
						list.Add(new InventoryWeaponLocation(inventoryType, (ushort)j, baseItemId, row.LevelItem.RowId));
					}
				}
			}
		}
		InventoryWeaponLocation inventoryWeaponLocation = (from candidate in list
			orderby candidate.ItemLevel descending, candidate.ItemId
			select candidate).FirstOrDefault();
		if (inventoryWeaponLocation.ItemId == 0)
		{
			log.Warning("[ClassUnlock] No equippable " + target.Abbreviation + " main-hand item was found after unlock.");
			return false;
		}
		ptr->MoveItemSlot(inventoryWeaponLocation.Container, inventoryWeaponLocation.Slot, InventoryType.EquippedItems, 0, a6: true);
		log.Information($"[ClassUnlock] Equipped {target.Abbreviation} main-hand item {inventoryWeaponLocation.ItemId} before gearset creation.");
		return true;
	}

	private async Task RunAsync(string[] characters, uint[] targetIds, CancellationToken token)
	{
		List<ClassUnlockTargetResult> results = new List<ClassUnlockTargetResult>();
		try
		{
			foreach (string character in characters)
			{
				token.ThrowIfCancellationRequested();
				if (TrySkipFullyUnlockedCharacter(character, targetIds, results))
				{
					SetState(ClassUnlockRunPhase.CheckingTarget, character, 0u, string.Empty, "Skipped " + character + ": every selected target is already unlocked.", results);
					continue;
				}
				SetState(ClassUnlockRunPhase.SwitchingCharacter, character, 0u, string.Empty, "Switching to " + character + "...", results);
				if (!(await EnsureCharacterAsync(character, token)))
				{
					AddCharacterFailures(character, targetIds, "Character login timed out.", results);
					continue;
				}
				await Task.Delay(TimeSpan.FromSeconds(3L), token);
				SetState(ClassUnlockRunPhase.PreparingCombatGearset, character, 0u, string.Empty, "Saving the currently equipped combat job so it can be restored between unlock quests...", results);
				ClassUnlockAnchorGearset anchor = await CaptureCombatAnchorAsync(character, token);
				if (anchor == null)
				{
					AddCharacterFailures(character, targetIds, "The currently equipped combat job could not be saved as a return gearset.", results);
					continue;
				}
				foreach (ClassUnlockTargetDefinition target in ClassUnlockCatalog.OrderForRun(targetIds, await framework.RunOnFrameworkThread(() => clientState.TerritoryType)))
				{
					token.ThrowIfCancellationRequested();
					if (!target.IsAvailable)
					{
						AddResult(character, target, ClassUnlockResultKind.NotUnlocked, target.Requirement, results);
						continue;
					}
					if (!(await RestoreCombatAnchorAsync(character, anchor, token)))
					{
						AddResult(character, target, ClassUnlockResultKind.Failed, "The saved combat gearset could not be restored.", results);
						continue;
					}
					SetState(ClassUnlockRunPhase.CheckingTarget, character, target.ClassJobId, string.Empty, "Checking " + target.Name + "...", results);
					IReadOnlyList<int> levels = await ReadLiveClassJobLevelsAsync(token);
					if (ClassUnlockCatalog.GetLevel(levels, target) > 0)
					{
						AddResult(character, target, ClassUnlockResultKind.AlreadyUnlocked, "Already unlocked; skipped.", results);
						CacheLevels(character, levels);
						continue;
					}
					int highestCombatLevel = GetHighestCombatLevel(levels);
					if (highestCombatLevel < target.RequiredCombatLevel)
					{
						AddResult(character, target, ClassUnlockResultKind.NotUnlocked, $"Not unlocked: {target.Name} requires {target.Requirement} (highest combat level: {highestCombatLevel}).", results);
						continue;
					}
					bool targetSucceeded = false;
					string failure = string.Empty;
					foreach (string questId in target.QuestIds)
					{
						if (questionable.IsQuestComplete(questId))
						{
							continue;
						}
						bool num = questionable.IsQuestAccepted(questId);
						bool flag = questionable.IsReadyToAcceptQuest(questId);
						if (!num && !flag)
						{
							failure = (questionable.IsQuestUnobtainable(questId) ? ("Quest " + questId + " is unobtainable on this character.") : (questionable.IsQuestLocked(questId) ? $"Quest {questId} is locked ({target.Requirement})." : $"Quest {questId} is not ready ({target.Requirement})."));
						}
						else if (!(await RunOwnedQuestAsync(character, target, questId, results, token)))
						{
							failure = "Questionable did not complete quest " + questId + ".";
						}
						else
						{
							if (!(await RestoreCombatAnchorAsync(character, anchor, token)))
							{
								failure = "The original combat gearset could not be restored after the unlock quest.";
								break;
							}
							IReadOnlyList<int> levels2 = await ReadLiveClassJobLevelsAsync(token);
							CacheLevels(character, levels2);
							if (ClassUnlockCatalog.GetLevel(levels2, target) > 0)
							{
								targetSucceeded = true;
								break;
							}
						}
					}
					levels = await ReadLiveClassJobLevelsAsync(token);
					CacheLevels(character, levels);
					if (targetSucceeded && ClassUnlockCatalog.GetLevel(levels, target) > 0)
					{
						AddResult(character, target, ClassUnlockResultKind.Unlocked, "Unlocked.", results);
					}
					else
					{
						AddResult(character, target, ClassUnlockResultKind.NotUnlocked, string.IsNullOrWhiteSpace(failure) ? "Unlock could not be verified." : failure, results);
					}
				}
				await RestoreCombatAnchorAsync(character, anchor, token);
			}
			SetState(ClassUnlockRunPhase.Completed, string.Empty, 0u, string.Empty, "Class Unlock rotation completed.", results, isRunning: false);
		}
		catch (OperationCanceledException)
		{
			await StopOwnedQuestAndCleanupAsync();
			await RestoreConfiguredAnchorBestEffortAsync();
			SetState(ClassUnlockRunPhase.Stopped, State.CurrentCharacter, State.CurrentClassJobId, string.Empty, "Class Unlock rotation stopped.", results, isRunning: false);
		}
		catch (Exception ex2)
		{
			log.Error(ex2, "[ClassUnlock] Rotation failed");
			await StopOwnedQuestAndCleanupAsync();
			await RestoreConfiguredAnchorBestEffortAsync();
			SetState(ClassUnlockRunPhase.Failed, State.CurrentCharacter, State.CurrentClassJobId, string.Empty, "Class Unlock failed: " + ex2.Message, results, isRunning: false);
		}
		finally
		{
			CleanupOwnedPriority();
		}
	}

	private async Task<bool> RunOwnedQuestAsync(string character, ClassUnlockTargetDefinition target, string questId, List<ClassUnlockTargetResult> results, CancellationToken token)
	{
		ownedPriorityQuest = string.Empty;
		activeOwnedQuest = questId;
		startedOwnedQuest = false;
		ownedQuestStopRequested = false;
		if (!questionable.IsQuestInPriority(questId))
		{
			if (!questionable.InsertQuestPriority(0, questId))
			{
				return false;
			}
			ownedPriorityQuest = questId;
		}
		SetState(ClassUnlockRunPhase.RunningQuest, character, target.ClassJobId, questId, "Unlocking " + target.Name + ": quest " + questId, results);
		if (!questionable.StartSingleQuest(questId) && !questionable.IsRunning())
		{
			CleanupOwnedPriority();
			return false;
		}
		startedOwnedQuest = true;
		DateTime deadline = DateTime.UtcNow + QuestTimeout;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (isStopPointInterruption && !questRotation.IsRotationActive)
			{
				throw new OperationCanceledException("The Stop Point rotation was stopped.", token);
			}
			if (questionable.IsQuestComplete(questId))
			{
				await StopCompletedOwnedQuestAndWaitAsync(questId);
				CleanupOwnedPriority();
				return true;
			}
			await Task.Delay(PollInterval, token);
		}
		await StopOwnedQuestAndCleanupAsync();
		return false;
	}

	private async Task<bool> EnsureCharacterAsync(string character, CancellationToken token)
	{
		if (string.Equals(autoRetainer.GetCurrentCharacter(), character, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		DateTime deadline = DateTime.UtcNow + CharacterSwitchTimeout;
		DateTime nextAttempt = DateTime.MinValue;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (string.Equals(autoRetainer.GetCurrentCharacter(), character, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (DateTime.UtcNow >= nextAttempt)
			{
				autoRetainer.SwitchCharacter(character);
				nextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(20L);
			}
			await Task.Delay(PollInterval, token);
		}
		return false;
	}

	private async Task<ClassUnlockAnchorGearset?> CaptureCombatAnchorAsync(string character, CancellationToken token)
	{
		if (!(await SelectHighestCombatGearsetIfNeededAsync(token)))
		{
			return null;
		}
		CurrentGearsetPersistenceResult currentGearsetPersistenceResult = await gearsetPersistence.PersistCurrentGearsetAsync("Class Unlock return job", token);
		ClassUnlockAnchorGearset classUnlockAnchorGearset = (currentGearsetPersistenceResult.Success ? new ClassUnlockAnchorGearset
		{
			GearsetId = currentGearsetPersistenceResult.GearsetId,
			ClassJobId = currentGearsetPersistenceResult.ClassJobId,
			VerifiedUtc = DateTime.UtcNow
		} : null);
		if (classUnlockAnchorGearset == null)
		{
			return null;
		}
		configuration.ClassUnlocks.AnchorGearsets[character] = classUnlockAnchorGearset;
		configuration.Save();
		return classUnlockAnchorGearset;
	}

	private async Task<bool> SelectHighestCombatGearsetIfNeededAsync(CancellationToken token)
	{
		(bool CurrentIsCombat, int GearsetId, uint ClassJobId) selection = await framework.RunOnFrameworkThread(() => ReadCombatGearsetSelectionUnsafe());
		if (selection.CurrentIsCombat)
		{
			return true;
		}
		if (selection.GearsetId < 0)
		{
			return false;
		}
		await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand($"/gs change {selection.GearsetId + 1}"));
		return await WaitForClassJobAsync(selection.ClassJobId, token);
	}

	private unsafe (bool CurrentIsCombat, int GearsetId, uint ClassJobId) ReadCombatGearsetSelectionUnsafe()
	{
		PlayerState* ptr = PlayerState.Instance();
		uint num = (uint)((ptr != null) ? ptr->CurrentClassJobId : 0);
		if (num != 0 && JobClassification.IsCombatJob((byte)num))
		{
			return (CurrentIsCombat: true, GearsetId: -1, ClassJobId: num);
		}
		RaptureGearsetModule* ptr2 = RaptureGearsetModule.Instance();
		if (ptr == null || ptr2 == null || ptr2->IsVirtual || ptr2->CharacterContentId != ptr->ContentId)
		{
			return (CurrentIsCombat: false, GearsetId: -1, ClassJobId: 0u);
		}
		int item = -1;
		uint item2 = 0u;
		int num2 = -1;
		for (int i = 0; i < 100; i++)
		{
			RaptureGearsetModule.GearsetEntry* gearset = ptr2->GetGearset(i);
			if (gearset == null || (gearset->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
			{
				continue;
			}
			uint classJobId = gearset->ClassJob;
			if (classJobId != 0 && JobClassification.IsCombatJob((byte)classJobId))
			{
				CombatJobDefinition combatJobDefinition = combatJobs.Definitions.FirstOrDefault((CombatJobDefinition value) => value.ClassJobId == classJobId);
				int num3 = ((combatJobDefinition != null && combatJobDefinition.ExpArrayIndex >= 0 && combatJobDefinition.ExpArrayIndex < ptr->ClassJobLevels.Length) ? ptr->ClassJobLevels[combatJobDefinition.ExpArrayIndex] : 0);
				if (num3 > num2)
				{
					num2 = num3;
					item = i;
					item2 = classJobId;
				}
			}
		}
		return (CurrentIsCombat: false, GearsetId: item, ClassJobId: item2);
	}

	private async Task<bool> RestoreCombatAnchorAsync(string character, ClassUnlockAnchorGearset anchor, CancellationToken token)
	{
		SetState(ClassUnlockRunPhase.RestoringCombatGearset, character, State.CurrentClassJobId, string.Empty, $"Restoring combat gearset {anchor.GearsetId + 1}...", State.Results);
		if (!(await IsValidAnchorAsync(anchor, token)))
		{
			return false;
		}
		if (await framework.RunOnFrameworkThread(() => playerState.ClassJob.RowId) == anchor.ClassJobId)
		{
			return true;
		}
		await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand($"/gs change {anchor.GearsetId + 1}"));
		return await WaitForClassJobAsync(anchor.ClassJobId, token);
	}

	private async Task<bool> RestoreCombatAnchorForContinuationAsync(string character, ClassUnlockAnchorGearset anchor, CancellationToken token)
	{
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			if (await RestoreCombatAnchorAsync(character, anchor, token))
			{
				log.Information($"[ClassUnlock] Verified continuation job {anchor.ClassJobId} from gearset {anchor.GearsetId + 1} on attempt {attempt}/{3}.");
				return true;
			}
			log.Warning($"[ClassUnlock] Failed to restore continuation gearset {anchor.GearsetId + 1} for job {anchor.ClassJobId} on attempt {attempt}/{3}.");
			if (attempt < 3)
			{
				await Task.Delay(TimeSpan.FromSeconds(1L), token);
			}
		}
		return false;
	}

	private async Task<bool> IsValidAnchorAsync(ClassUnlockAnchorGearset anchor, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		return await framework.RunOnFrameworkThread(() => IsValidAnchorUnsafe(anchor));
	}

	private async Task<int> FindGearsetForClassJobAsync(uint classJobId, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		return await framework.RunOnFrameworkThread(() => FindGearsetForClassJobUnsafe(classJobId));
	}

	private unsafe static int FindGearsetForClassJobUnsafe(uint classJobId)
	{
		PlayerState* ptr = PlayerState.Instance();
		RaptureGearsetModule* ptr2 = RaptureGearsetModule.Instance();
		if (ptr == null || ptr2 == null || ptr2->IsVirtual || ptr2->CharacterContentId != ptr->ContentId)
		{
			return -1;
		}
		for (int i = 0; i < 100; i++)
		{
			RaptureGearsetModule.GearsetEntry* gearset = ptr2->GetGearset(i);
			if (gearset != null && (gearset->Flags & RaptureGearsetModule.GearsetFlag.Exists) != RaptureGearsetModule.GearsetFlag.None && gearset->ClassJob == classJobId)
			{
				return i;
			}
		}
		return -1;
	}

	private unsafe static bool IsValidAnchorUnsafe(ClassUnlockAnchorGearset anchor)
	{
		PlayerState* ptr = PlayerState.Instance();
		RaptureGearsetModule* ptr2 = RaptureGearsetModule.Instance();
		if (ptr == null || ptr2 == null || ptr2->IsVirtual || ptr2->CharacterContentId != ptr->ContentId || anchor.GearsetId < 0)
		{
			return false;
		}
		RaptureGearsetModule.GearsetEntry* gearset = ptr2->GetGearset(anchor.GearsetId);
		if (gearset != null && (gearset->Flags & RaptureGearsetModule.GearsetFlag.Exists) != RaptureGearsetModule.GearsetFlag.None)
		{
			return gearset->ClassJob == anchor.ClassJobId;
		}
		return false;
	}

	private async Task<bool> WaitForClassJobAsync(uint classJobId, CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15L);
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (await framework.RunOnFrameworkThread(() => playerState.ClassJob.RowId) == classJobId)
			{
				return true;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(250L), token);
		}
		return false;
	}

	private async Task<IReadOnlyList<int>> ReadLiveClassJobLevelsAsync(CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		return await framework.RunOnFrameworkThread(() => ReadLiveClassJobLevelsUnsafe());
	}

	private unsafe static IReadOnlyList<int> ReadLiveClassJobLevelsUnsafe()
	{
		PlayerState* ptr = PlayerState.Instance();
		if (ptr == null || !ptr->IsLoaded)
		{
			return Array.Empty<int>();
		}
		int[] array = new int[ptr->ClassJobLevels.Length];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = ptr->ClassJobLevels[i];
		}
		return array;
	}

	private int GetHighestCombatLevel(IReadOnlyList<int> levels)
	{
		int num = 0;
		foreach (CombatJobDefinition definition in combatJobs.Definitions)
		{
			if (definition.ExpArrayIndex >= 0 && definition.ExpArrayIndex < levels.Count)
			{
				num = Math.Max(num, levels[definition.ExpArrayIndex]);
			}
		}
		return num;
	}

	private int GetClassJobLevel(IReadOnlyList<int> levels, uint classJobId)
	{
		CombatJobDefinition combatJobDefinition = combatJobs.Definitions.FirstOrDefault((CombatJobDefinition value) => value.ClassJobId == classJobId);
		if (!(combatJobDefinition != null) || combatJobDefinition.ExpArrayIndex < 0 || combatJobDefinition.ExpArrayIndex >= levels.Count)
		{
			return 0;
		}
		return Math.Max(0, levels[combatJobDefinition.ExpArrayIndex]);
	}

	private void CacheLevels(string character, IReadOnlyList<int> levels)
	{
		if (levels.Count != 0)
		{
			Dictionary<uint, int> allClassJobLevels = ClassUnlockCatalog.Targets.Where((ClassUnlockTargetDefinition target) => target.ExpArrayIndex >= 0 && target.ExpArrayIndex < levels.Count).ToDictionary((ClassUnlockTargetDefinition target) => target.ClassJobId, (ClassUnlockTargetDefinition target) => Math.Max(0, levels[target.ExpArrayIndex]));
			configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot value);
			if (value == null)
			{
				value = new CharacterJobLevelSnapshot();
			}
			value.AllClassJobLevels = allClassJobLevels;
			value.HasAllClassJobLevels = true;
			value.AllClassJobLevelsUpdatedUtc = DateTime.UtcNow;
			value.LastUpdatedUtc = DateTime.UtcNow;
			configuration.CharacterJobLevels[character] = value;
			configuration.Save();
		}
	}

	private async Task StopOwnedQuestAndCleanupAsync()
	{
		if (startedOwnedQuest)
		{
			try
			{
				await RequestOwnedQuestStopAsync();
				await WaitForQuestionableToStopAsync();
			}
			catch (Exception ex)
			{
				log.Debug("[ClassUnlock] Could not stop owned Questionable quest: " + ex.Message);
			}
		}
		CleanupOwnedPriority();
	}

	private void CleanupOwnedPriority()
	{
		if (!string.IsNullOrWhiteSpace(ownedPriorityQuest))
		{
			questionable.RemovePriorityQuest(ownedPriorityQuest);
		}
		ownedPriorityQuest = string.Empty;
		activeOwnedQuest = string.Empty;
		startedOwnedQuest = false;
		ownedQuestStopRequested = false;
	}

	private bool TryStopCompletedOwnedQuest(uint questId)
	{
		if (!startedOwnedQuest || !string.Equals(activeOwnedQuest, questId.ToString(), StringComparison.Ordinal))
		{
			return false;
		}
		if (ownedQuestStopRequested)
		{
			return true;
		}
		ownedQuestStopRequested = true;
		try
		{
			commandManager.ProcessCommand("/qst stop");
			log.Information($"[ClassUnlock] Quest {questId} completed; stopping Questionable before its NextQuestId can run.");
		}
		catch (Exception ex)
		{
			ownedQuestStopRequested = false;
			log.Warning($"[ClassUnlock] Immediate stop for completed quest {questId} failed: {ex.Message}");
		}
		return true;
	}

	private async Task StopCompletedOwnedQuestAndWaitAsync(string questId)
	{
		await RequestOwnedQuestStopAsync();
		if (!(await WaitForQuestionableToStopAsync()))
		{
			throw new TimeoutException("Questionable did not stop after completed Class Unlock quest " + questId + "; no further unlock quest was started.");
		}
	}

	private async Task RequestOwnedQuestStopAsync()
	{
		if (ownedQuestStopRequested)
		{
			return;
		}
		ownedQuestStopRequested = true;
		try
		{
			await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand("/qst stop"));
		}
		catch
		{
			ownedQuestStopRequested = false;
			throw;
		}
	}

	private async Task<bool> WaitForQuestionableToStopAsync()
	{
		DateTime deadline = DateTime.UtcNow + QuestionableStopTimeout;
		while (DateTime.UtcNow < deadline)
		{
			if (!questionable.IsRunning())
			{
				return true;
			}
			await Task.Delay(QuestionableStopPollInterval);
		}
		return !questionable.IsRunning();
	}

	private async Task RestoreConfiguredAnchorBestEffortAsync()
	{
		string currentCharacter = autoRetainer.GetCurrentCharacter();
		if (currentCharacter == null || !configuration.ClassUnlocks.AnchorGearsets.TryGetValue(currentCharacter, out ClassUnlockAnchorGearset value))
		{
			return;
		}
		try
		{
			using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15L));
			await RestoreCombatAnchorAsync(currentCharacter, value, timeout.Token);
		}
		catch
		{
		}
	}

	private bool TrySkipFullyUnlockedCharacter(string character, IEnumerable<uint> targetIds, List<ClassUnlockTargetResult> results)
	{
		if (!configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot snapshot) || !snapshot.HasAllClassJobLevels)
		{
			return false;
		}
		ClassUnlockTargetDefinition[] array = (from classUnlockTargetDefinition in targetIds.Select(ClassUnlockCatalog.Find)
			where classUnlockTargetDefinition != null
			select classUnlockTargetDefinition).Cast<ClassUnlockTargetDefinition>().ToArray();
		if (array.Length == 0 || array.Any((ClassUnlockTargetDefinition classUnlockTargetDefinition) => classUnlockTargetDefinition.IsAvailable && (!snapshot.AllClassJobLevels.TryGetValue(classUnlockTargetDefinition.ClassJobId, out var value) || value <= 0)))
		{
			return false;
		}
		ClassUnlockTargetDefinition[] array2 = array;
		foreach (ClassUnlockTargetDefinition target in array2)
		{
			results.RemoveAll((ClassUnlockTargetResult result) => string.Equals(result.Character, character, StringComparison.OrdinalIgnoreCase) && result.ClassJobId == target.ClassJobId);
			results.Add(new ClassUnlockTargetResult(character, target.ClassJobId, target.IsAvailable ? ClassUnlockResultKind.AlreadyUnlocked : ClassUnlockResultKind.NotUnlocked, target.IsAvailable ? "Already unlocked in the offline snapshot; character login skipped." : target.Requirement));
		}
		return true;
	}

	private static bool DictionariesEqual(Dictionary<uint, int> left, Dictionary<uint, int> right)
	{
		if (left.Count == right.Count)
		{
			return right.All((KeyValuePair<uint, int> entry) => left.TryGetValue(entry.Key, out var value) && value == entry.Value);
		}
		return false;
	}

	private static void AddCharacterFailures(string character, IEnumerable<uint> targetIds, string message, List<ClassUnlockTargetResult> results)
	{
		foreach (ClassUnlockTargetDefinition item in from value in targetIds.Select(ClassUnlockCatalog.Find)
			where value != null
			select value)
		{
			results.Add(new ClassUnlockTargetResult(character, item.ClassJobId, ClassUnlockResultKind.Failed, message));
		}
	}

	private void AddResult(string character, ClassUnlockTargetDefinition target, ClassUnlockResultKind kind, string message, List<ClassUnlockTargetResult> results)
	{
		results.RemoveAll((ClassUnlockTargetResult result) => string.Equals(result.Character, character, StringComparison.OrdinalIgnoreCase) && result.ClassJobId == target.ClassJobId);
		results.Add(new ClassUnlockTargetResult(character, target.ClassJobId, kind, message));
		SetState(State.Phase, character, target.ClassJobId, State.CurrentQuestId, message, results);
	}

	private void SetState(ClassUnlockRunPhase phase, string character, uint classJobId, string questId, string status, IReadOnlyList<ClassUnlockTargetResult> results, bool isRunning = true)
	{
		lock (stateLock)
		{
			state = new ClassUnlockRunState(isRunning, phase, character, classJobId, questId, status, results.ToArray());
		}
	}

	private static ClassUnlockRunState IdleState()
	{
		return new ClassUnlockRunState(IsRunning: false, ClassUnlockRunPhase.Idle, string.Empty, 0u, string.Empty, "Select characters and classes/jobs to unlock.", Array.Empty<ClassUnlockTargetResult>());
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			cancellationSource?.Cancel();
			cancellationSource?.Dispose();
		}
	}
}
