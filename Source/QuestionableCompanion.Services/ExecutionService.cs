using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class ExecutionService : IDisposable
{
	private readonly IPluginLog log;

	private readonly Configuration config;

	private readonly QuestionableIPC questionableIPC;

	private readonly AutoRetainerIPC autoRetainerIPC;

	private readonly QuestDetectionService questDetection;

	private readonly IClientState clientState;

	private readonly IGameGui gameGui;

	private readonly IFramework framework;

	private CharacterSafeWaitService? safeWaitService;

	private QuestPreCheckService? preCheckService;

	private DCTravelService? dcTravelService;

	private int currentCharacterIndex;

	private readonly HashSet<string> completedCharacters = new HashSet<string>();

	private readonly HashSet<string> failedCharacters = new HashSet<string>();

	private bool waitingForRelog;

	private string targetRelogCharacter = string.Empty;

	private DateTime relogStartTime = DateTime.MinValue;

	private bool wasLoggedOutDuringRelog;

	public ExecutionState CurrentState { get; private set; } = new ExecutionState();

	public bool IsRunning { get; private set; }

	public bool IsPaused { get; private set; }

	public List<LogEntry> Logs { get; } = new List<LogEntry>();

	public event Action<LogEntry>? LogAdded;

	public event Action<ExecutionState>? StateChanged;

	public ExecutionService(IPluginLog log, Configuration config, QuestionableIPC questionableIPC, AutoRetainerIPC autoRetainerIPC, QuestDetectionService questDetection, IClientState clientState, IGameGui gameGui, IFramework framework)
	{
		this.log = log;
		this.config = config;
		this.questionableIPC = questionableIPC;
		this.autoRetainerIPC = autoRetainerIPC;
		this.questDetection = questDetection;
		this.clientState = clientState;
		this.gameGui = gameGui;
		this.framework = framework;
		questDetection.QuestAccepted += OnQuestAccepted;
		questDetection.QuestCompleted += OnQuestCompleted;
		framework.Update += OnFrameworkUpdate;
		AddLog(LogLevel.Info, "Execution service initialized");
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!waitingForRelog)
		{
			return;
		}
		try
		{
			if ((DateTime.Now - relogStartTime).TotalSeconds > 60.0)
			{
				AddLog(LogLevel.Warning, "Relog timeout - moving to next character");
				waitingForRelog = false;
				failedCharacters.Add(targetRelogCharacter);
				SwitchToNextCharacter();
			}
			else if (!clientState.IsLoggedIn)
			{
				if (!wasLoggedOutDuringRelog)
				{
					AddLog(LogLevel.Info, "[ExecutionService] Character logged out, waiting for relog...");
					wasLoggedOutDuringRelog = true;
				}
			}
			else
			{
				if (!clientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null || !IsNamePlateReady())
				{
					return;
				}
				string currentCharacter = autoRetainerIPC.GetCurrentCharacter();
				if (string.IsNullOrEmpty(currentCharacter) || !(currentCharacter == targetRelogCharacter))
				{
					return;
				}
				AddLog(LogLevel.Success, "[ExecutionService] Relog confirmed: " + currentCharacter);
				AddLog(LogLevel.Info, "[ExecutionService] NamePlate ready, character fully loaded");
				if (config.EnableSafeWaitAfterCharacterSwitch && safeWaitService != null)
				{
					AddLog(LogLevel.Info, "[SafeWait] Stabilizing after character switch...");
					safeWaitService.PerformQuickSafeWait();
					AddLog(LogLevel.Info, "[SafeWait] Post-switch stabilization complete");
				}
				if (config.EnableQuestPreCheck && preCheckService != null)
				{
					AddLog(LogLevel.Info, "[PreCheck] Scanning quest status for current character...");
					preCheckService.ScanCurrentCharacterQuestStatus();
				}
				waitingForRelog = false;
				CurrentState.CurrentCharacter = currentCharacter;
				questDetection.ResetTracking();
				AddLog(LogLevel.Info, "[ExecutionService] Refreshing quest cache...");
				questDetection.RefreshQuestCache();
				NotifyStateChanged();
				if (dcTravelService != null && dcTravelService.ShouldPerformDCTravel())
				{
					AddLog(LogLevel.Warning, "[DCTravel] DC Travel required for this character!");
					AddLog(LogLevel.Info, "[DCTravel] Initiating DC travel before quest execution...");
					Task.Run(async delegate
					{
						try
						{
							if (await dcTravelService.PerformDCTravel())
							{
								AddLog(LogLevel.Success, "[DCTravel] DC travel completed - starting quests");
								ExecuteQuestsForCurrentCharacter();
							}
							else
							{
								AddLog(LogLevel.Error, "[DCTravel] DC travel failed - switching character");
								SwitchToNextCharacter();
							}
						}
						catch (Exception ex2)
						{
							AddLog(LogLevel.Error, "[DCTravel] Error: " + ex2.Message);
							SwitchToNextCharacter();
						}
					});
				}
				else
				{
					ExecuteQuestsForCurrentCharacter();
				}
			}
		}
		catch (Exception ex)
		{
			log.Error("[ExecutionService] Error in relog monitoring: " + ex.Message);
		}
	}

	public bool Start()
	{
		QuestProfile activeProfile = config.GetActiveProfile();
		if (activeProfile == null)
		{
			AddLog(LogLevel.Error, "No active profile selected");
			return false;
		}
		if (activeProfile.Characters.Count == 0)
		{
			AddLog(LogLevel.Error, "No characters configured in profile");
			return false;
		}
		IsRunning = true;
		IsPaused = false;
		CurrentState.ActiveProfile = activeProfile.Name;
		CurrentState.Status = ExecutionStatus.Running;
		AddLog(LogLevel.Success, "Started profile: " + activeProfile.Name);
		AddLog(LogLevel.Info, $"Characters in rotation: {activeProfile.Characters.Count}");
		currentCharacterIndex = 0;
		SwitchToCharacter(activeProfile.Characters[0]);
		NotifyStateChanged();
		return true;
	}

	public void Stop()
	{
		IsRunning = false;
		IsPaused = false;
		waitingForRelog = false;
		if (questionableIPC.IsRunning())
		{
			questionableIPC.Stop();
		}
		CurrentState.Status = ExecutionStatus.Idle;
		CurrentState.CurrentQuestId = 0u;
		CurrentState.CurrentQuestName = string.Empty;
		CurrentState.CurrentSequence = string.Empty;
		AddLog(LogLevel.Info, "Execution stopped");
		NotifyStateChanged();
	}

	public void Pause()
	{
		IsPaused = true;
		questionableIPC.Stop();
		CurrentState.Status = ExecutionStatus.Waiting;
		AddLog(LogLevel.Warning, "Execution paused");
		NotifyStateChanged();
	}

	public void Resume()
	{
		IsPaused = false;
		CurrentState.Status = ExecutionStatus.Running;
		AddLog(LogLevel.Info, "Execution resumed");
		NotifyStateChanged();
	}

	private void OnQuestAccepted(uint questId, string questName)
	{
		if (!IsRunning || IsPaused)
		{
			AddLog(LogLevel.Debug, $"Quest {questId} accepted but execution not running");
			return;
		}
		QuestProfile activeProfile = config.GetActiveProfile();
		if (activeProfile == null)
		{
			AddLog(LogLevel.Warning, "No active profile found");
			return;
		}
		QuestConfig questConfig = activeProfile.Quests.FirstOrDefault((QuestConfig q) => q.QuestId == questId && q.TriggerType == TriggerType.OnAccept);
		if (questConfig != null)
		{
			AddLog(LogLevel.Success, $"Quest accepted trigger matched: {questName} (ID: {questId})");
			ExecuteSequence(questConfig);
		}
		else
		{
			AddLog(LogLevel.Info, $"Quest {questId} ({questName}) accepted but no OnAccept trigger configured");
			AddLog(LogLevel.Info, "Add this quest to your profile with TriggerType=OnAccept to auto-execute");
		}
	}

	private void OnQuestCompleted(uint questId, string questName)
	{
		if (!IsRunning || IsPaused)
		{
			return;
		}
		QuestProfile activeProfile = config.GetActiveProfile();
		if (activeProfile != null)
		{
			QuestConfig questConfig = activeProfile.Quests.FirstOrDefault((QuestConfig q) => q.QuestId == questId && q.TriggerType == TriggerType.OnComplete);
			if (questConfig != null)
			{
				AddLog(LogLevel.Success, "Quest completed trigger matched: " + questName);
				ExecuteSequence(questConfig);
			}
		}
	}

	private async void ExecuteSequence(QuestConfig questConfig)
	{
		CurrentState.CurrentQuestId = questConfig.QuestId;
		CurrentState.CurrentQuestName = questConfig.QuestName;
		CurrentState.CurrentSequence = questConfig.SequenceAfterQuest.Value;
		CurrentState.Status = ExecutionStatus.Running;
		NotifyStateChanged();
		if (config.EnableDryRun)
		{
			AddLog(LogLevel.Debug, "[DRY RUN] Would execute sequence: " + questConfig.SequenceAfterQuest.Value);
			await Task.Delay(2000);
			OnSequenceComplete(questConfig);
			return;
		}
		switch (questConfig.SequenceAfterQuest.Type)
		{
		case SequenceType.QuestionableProfile:
			await ExecuteQuestionableProfile(questConfig);
			break;
		case SequenceType.InternalAction:
			await ExecuteInternalAction(questConfig);
			break;
		}
	}

	private async Task ExecuteQuestionableProfile(QuestConfig questConfig)
	{
		AddLog(LogLevel.Info, $"Monitoring Quest {questConfig.QuestId} for completion...");
		AddLog(LogLevel.Info, "Questionable will handle quest progression automatically");
		CurrentState.CurrentSequence = $"Monitoring Quest {questConfig.QuestId}";
		NotifyStateChanged();
		await WaitForQuestCompletion(questConfig.QuestId);
		OnSequenceComplete(questConfig);
	}

	private async Task WaitForQuestAcceptanceThenNext(QuestConfig questConfig)
	{
		int maxWaitSeconds = 3600;
		int waited = 0;
		AddLog(LogLevel.Info, $"Waiting for quest {questConfig.QuestId} to be accepted...");
		while (waited < maxWaitSeconds && IsRunning)
		{
			await Task.Delay(5000);
			waited += 5;
			if (questDetection.IsQuestAccepted(questConfig.QuestId))
			{
				AddLog(LogLevel.Success, $"Quest {questConfig.QuestId} accepted!");
				OnSequenceComplete(questConfig);
				return;
			}
			if (questDetection.IsQuestCompletedDirect(questConfig.QuestId))
			{
				AddLog(LogLevel.Success, $"Quest {questConfig.QuestId} already completed!");
				OnSequenceComplete(questConfig);
				return;
			}
			if (waited % 60 == 0)
			{
				AddLog(LogLevel.Debug, $"Still waiting for quest {questConfig.QuestId} acceptance... ({waited}s)");
			}
		}
		if (!IsRunning)
		{
			AddLog(LogLevel.Info, "Monitoring stopped - execution not running");
			return;
		}
		AddLog(LogLevel.Warning, $"Quest {questConfig.QuestId} acceptance timeout after {maxWaitSeconds}s");
		OnSequenceFailed(questConfig);
	}

	private async Task WaitForQuestCompletion(uint questId)
	{
		int maxWaitSeconds = 600;
		int waited = 0;
		while (waited < maxWaitSeconds && IsRunning)
		{
			await Task.Delay(5000);
			waited += 5;
			if (questDetection.IsQuestCompletedDirect(questId))
			{
				AddLog(LogLevel.Success, $"Quest {questId} completed!");
				return;
			}
			if (waited % 30 == 0)
			{
				AddLog(LogLevel.Debug, $"Still waiting for quest {questId}... ({waited}s)");
			}
		}
		if (!IsRunning)
		{
			AddLog(LogLevel.Info, "Monitoring stopped - execution not running");
			return;
		}
		AddLog(LogLevel.Warning, $"Quest {questId} completion timeout after {maxWaitSeconds}s");
	}

	private async Task WaitForQuestionableCompletion()
	{
		AddLog(LogLevel.Warning, "Waiting for Questionable to complete...");
		while (questionableIPC.IsRunning())
		{
			await Task.Delay(1000);
		}
		AddLog(LogLevel.Success, "Questionable completed");
	}

	private async Task ExecuteInternalAction(QuestConfig questConfig)
	{
		AddLog(LogLevel.Warning, "Internal action not yet implemented: " + questConfig.SequenceAfterQuest.Value);
		await Task.Delay(1000);
		OnSequenceComplete(questConfig);
	}

	private void OnSequenceComplete(QuestConfig questConfig)
	{
		AddLog(LogLevel.Success, "Sequence completed: " + questConfig.SequenceAfterQuest.Value);
		CurrentState.Status = ExecutionStatus.Complete;
		CurrentState.Progress = 100;
		NotifyStateChanged();
		if (questConfig.NextCharacter == "auto_next")
		{
			SwitchToNextCharacter();
		}
		else if (!string.IsNullOrEmpty(questConfig.NextCharacter))
		{
			SwitchToCharacter(questConfig.NextCharacter);
		}
	}

	private void OnSequenceFailed(QuestConfig questConfig)
	{
		AddLog(LogLevel.Error, "Sequence failed: " + questConfig.SequenceAfterQuest.Value);
		CurrentState.Status = ExecutionStatus.Failed;
		NotifyStateChanged();
		if (!string.IsNullOrEmpty(CurrentState.CurrentCharacter))
		{
			failedCharacters.Add(CurrentState.CurrentCharacter);
		}
		SwitchToNextCharacter();
	}

	private async Task SwitchToCharacter(string characterName)
	{
		if (config.EnableDryRun)
		{
			AddLog(LogLevel.Debug, "[DRY RUN] Would switch to character: " + characterName);
			CurrentState.CurrentCharacter = characterName;
			NotifyStateChanged();
			return;
		}
		AddLog(LogLevel.Info, "Switching to character: " + characterName);
		if (questionableIPC.IsRunning())
		{
			questionableIPC.Stop();
		}
		string currentCharacter = autoRetainerIPC.GetCurrentCharacter();
		AddLog(LogLevel.Debug, "Current character before switch: " + (currentCharacter ?? "null"));
		if (!string.IsNullOrEmpty(currentCharacter) && currentCharacter == characterName)
		{
			AddLog(LogLevel.Info, "Already on character: " + characterName);
			AddLog(LogLevel.Info, "Refreshing quest cache...");
			questDetection.RefreshQuestCache();
			CurrentState.CurrentCharacter = characterName;
			NotifyStateChanged();
			await Task.Delay(2000);
			ExecuteQuestsForCurrentCharacter();
			return;
		}
		if (string.IsNullOrEmpty(currentCharacter))
		{
			AddLog(LogLevel.Warning, "Could not get current character - might be in transition");
		}
		AddLog(LogLevel.Info, "[AutoRetainerIPC] Relog request sent for: " + characterName);
		if (autoRetainerIPC.SwitchCharacter(characterName))
		{
			waitingForRelog = true;
			targetRelogCharacter = characterName;
			relogStartTime = DateTime.Now;
			wasLoggedOutDuringRelog = false;
			AddLog(LogLevel.Info, "Relog command sent, monitoring via Framework.Update...");
		}
		else
		{
			AddLog(LogLevel.Error, "Failed to send relog request for character: " + characterName);
			failedCharacters.Add(characterName);
			SwitchToNextCharacter();
		}
	}

	private void ExecuteQuestsForCurrentCharacter()
	{
		AddLog(LogLevel.Info, "=== ExecuteQuestsForCurrentCharacter called ===");
		QuestProfile activeProfile = config.GetActiveProfile();
		if (activeProfile == null)
		{
			AddLog(LogLevel.Error, "No active profile");
			SwitchToNextCharacter();
			return;
		}
		string currentChar = CurrentState.CurrentCharacter;
		if (string.IsNullOrEmpty(currentChar))
		{
			AddLog(LogLevel.Warning, "No current character set");
			SwitchToNextCharacter();
			return;
		}
		AddLog(LogLevel.Info, "Current character: " + currentChar);
		AddLog(LogLevel.Info, $"Total quests in profile: {activeProfile.Quests.Count}");
		List<QuestConfig> list = (from q in activeProfile.Quests
			where string.IsNullOrEmpty(q.AssignedCharacter) || q.AssignedCharacter == currentChar
			orderby q.QuestId
			select q).ToList();
		if (list.Count == 0)
		{
			AddLog(LogLevel.Warning, "No quests configured for " + currentChar);
			AddLog(LogLevel.Info, "Please add quests to your profile using /qstcomp");
			completedCharacters.Add(currentChar);
			SwitchToNextCharacter();
			return;
		}
		AddLog(LogLevel.Info, $"Found {list.Count} quests for {currentChar}");
		foreach (QuestConfig item in list)
		{
			AddLog(LogLevel.Debug, $"Checking quest {item.QuestId}: {item.QuestName}");
			bool flag = questDetection.IsQuestCompletedDirect(item.QuestId);
			AddLog(LogLevel.Debug, $"Quest {item.QuestId} completed status: {flag}");
			if (!flag)
			{
				AddLog(LogLevel.Success, $"Starting quest {item.QuestId}: {item.QuestName}");
				CurrentState.CurrentQuestId = item.QuestId;
				CurrentState.CurrentQuestName = item.QuestName;
				CurrentState.CurrentSequence = $"Quest {item.QuestId}";
				NotifyStateChanged();
				if (item.TriggerType == TriggerType.OnAccept)
				{
					AddLog(LogLevel.Info, $"Quest {item.QuestId} has OnAccept trigger - waiting for acceptance");
					WaitForQuestAcceptanceThenNext(item);
				}
				else
				{
					AddLog(LogLevel.Info, $"Quest {item.QuestId} has OnComplete trigger - monitoring for completion");
					ExecuteSequence(item);
				}
				return;
			}
			AddLog(LogLevel.Info, $"Quest {item.QuestId} already completed, skipping");
		}
		AddLog(LogLevel.Success, "All quests completed for " + currentChar + "!");
		completedCharacters.Add(currentChar);
		SwitchToNextCharacter();
	}

	public void SetSafeWaitService(CharacterSafeWaitService service)
	{
		safeWaitService = service;
	}

	public void SetPreCheckService(QuestPreCheckService service)
	{
		preCheckService = service;
	}

	public void SetDCTravelService(DCTravelService service)
	{
		dcTravelService = service;
	}

	private void SwitchToNextCharacter()
	{
		QuestProfile activeProfile = config.GetActiveProfile();
		if (activeProfile == null || activeProfile.Characters.Count == 0)
		{
			AddLog(LogLevel.Error, "No profile or characters available");
			Stop();
			return;
		}
		if (config.EnableQuestPreCheck && preCheckService != null)
		{
			AddLog(LogLevel.Info, "Logging completed quests before logout...");
			preCheckService.LogCompletedQuestsBeforeLogout();
		}
		if (dcTravelService != null && dcTravelService.IsDCTravelCompleted())
		{
			if (config.ReturnToHomeworldOnStopQuest)
			{
				AddLog(LogLevel.Info, "[DCTravel] Returning to homeworld before character switch...");
				dcTravelService.ReturnToHomeworld();
				Thread.Sleep(2000);
				AddLog(LogLevel.Info, "[DCTravel] Returned to homeworld");
			}
			else
			{
				AddLog(LogLevel.Info, "[DCTravel] Skipping return to homeworld (setting disabled)");
			}
		}
		if (config.EnableSafeWaitBeforeCharacterSwitch && safeWaitService != null)
		{
			AddLog(LogLevel.Info, "[SafeWait] Stabilizing character before switch...");
			safeWaitService.PerformSafeWait();
			AddLog(LogLevel.Info, "[SafeWait] Stabilization complete");
		}
		if (!string.IsNullOrEmpty(CurrentState.CurrentCharacter))
		{
			completedCharacters.Add(CurrentState.CurrentCharacter);
		}
		int num = 0;
		while (num < activeProfile.Characters.Count)
		{
			currentCharacterIndex = (currentCharacterIndex + 1) % activeProfile.Characters.Count;
			string text = activeProfile.Characters[currentCharacterIndex];
			if (config.EnableQuestPreCheck && preCheckService != null)
			{
				List<StopPoint> stopPoints = config.StopPoints;
				if (stopPoints != null && stopPoints.Count > 0)
				{
					uint questId = stopPoints[0].QuestId;
					if (preCheckService.ShouldSkipCharacter(text, questId))
					{
						AddLog(LogLevel.Info, "[PreCheck] Skipping " + text + " - quest already completed");
						completedCharacters.Add(text);
						num++;
						continue;
					}
				}
			}
			if (!completedCharacters.Contains(text) && !failedCharacters.Contains(text))
			{
				SwitchToCharacter(text);
				return;
			}
			num++;
		}
		AddLog(LogLevel.Success, "All characters completed!");
		Stop();
	}

	private void AddLog(LogLevel level, string message)
	{
		LogEntry logEntry = new LogEntry
		{
			Level = level,
			Message = message,
			Timestamp = DateTime.Now
		};
		Logs.Add(logEntry);
		while (Logs.Count > config.MaxLogEntries)
		{
			Logs.RemoveAt(0);
		}
		log.Information("[ExecutionService] " + message);
		this.LogAdded?.Invoke(logEntry);
	}

	private void NotifyStateChanged()
	{
		CurrentState.LastUpdate = DateTime.Now;
		this.StateChanged?.Invoke(CurrentState);
	}

	private unsafe bool IsNamePlateReady()
	{
		try
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName("NamePlate");
			if (addonByName == IntPtr.Zero)
			{
				return false;
			}
			AddonNamePlate* address = (AddonNamePlate*)addonByName.Address;
			if (address != null && address->AtkUnitBase.IsVisible && address->AtkUnitBase.IsReady)
			{
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public void Dispose()
	{
		questDetection.QuestAccepted -= OnQuestAccepted;
		questDetection.QuestCompleted -= OnQuestCompleted;
		framework.Update -= OnFrameworkUpdate;
		Stop();
		AddLog(LogLevel.Info, "Execution service disposed");
	}
}
