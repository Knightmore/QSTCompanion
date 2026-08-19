using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class AlliedSocietyRotationService : IDisposable
{
	private readonly QuestionableIPC questionableIpc;

	private readonly AlliedSocietyDatabase database;

	private readonly AlliedSocietyQuestSelector questSelector;

	private readonly AutoRetainerIPC autoRetainerIpc;

	private readonly Configuration configuration;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly ICondition condition;

	private readonly IClientState clientState;

	private readonly IPlayerState playerState;

	private bool isRotationActive;

	private AlliedSocietyRotationPhase currentPhase;

	private List<string> rotationCharacters = new List<string>();

	private int currentCharacterIndex = -1;

	private string currentCharacterId = string.Empty;

	private DateTime phaseStartTime = DateTime.MinValue;

	private int consecutiveNoQuestsCount;

	private DateTime lastUpdate = DateTime.MinValue;

	private const double UpdateIntervalMs = 500.0;

	private DateTime characterSwitchStartTime = DateTime.MinValue;

	private string? savedQuestionablePriority;

	private const double CharacterSwitchRetrySeconds = 20.0;

	public bool IsRotationActive => isRotationActive;

	public string CurrentCharacterId => currentCharacterId;

	public AlliedSocietyRotationPhase CurrentPhase => currentPhase;

	public AlliedSocietyRotationService(QuestionableIPC questionableIpc, AlliedSocietyDatabase database, AlliedSocietyQuestSelector questSelector, AutoRetainerIPC autoRetainerIpc, Configuration configuration, IPluginLog log, IFramework framework, ICommandManager commandManager, ICondition condition, IClientState clientState, IPlayerState playerState)
	{
		this.questionableIpc = questionableIpc;
		this.database = database;
		this.questSelector = questSelector;
		this.autoRetainerIpc = autoRetainerIpc;
		this.configuration = configuration;
		this.log = log;
		this.framework = framework;
		this.commandManager = commandManager;
		this.condition = condition;
		this.clientState = clientState;
		this.playerState = playerState;
		framework.Update += OnFrameworkUpdate;
	}

	public void Dispose()
	{
		StopRotation();
		framework.Update -= OnFrameworkUpdate;
	}

	public void StartRotation(List<string> characters)
	{
		if (isRotationActive)
		{
			log.Warning("[AlliedSociety] Rotation already active");
			return;
		}
		if (characters == null || characters.Count == 0)
		{
			log.Warning("[AlliedSociety] Cannot start rotation: No characters selected");
			return;
		}
		if (!questionableIpc.TryExportQuestPriority(out string encodedQuestPriority))
		{
			log.Error("[AlliedSociety] Cannot start rotation: Questionable priority queue could not be saved");
			return;
		}
		savedQuestionablePriority = encodedQuestPriority;
		if (!questionableIpc.ClearQuestPriority())
		{
			savedQuestionablePriority = null;
			log.Error("[AlliedSociety] Cannot start rotation: Questionable priority queue could not be prepared");
			return;
		}
		log.Information($"[AlliedSociety] Starting rotation with {characters.Count} selected characters");
		rotationCharacters = new List<string>(characters);
		isRotationActive = true;
		currentCharacterIndex = -1;
		AdvanceToNextCharacter();
	}

	public void StopRotation()
	{
		if (!isRotationActive)
		{
			RestoreQuestionablePriority();
			return;
		}
		log.Information("[AlliedSociety] Stopping rotation");
		isRotationActive = false;
		currentPhase = AlliedSocietyRotationPhase.Idle;
		currentCharacterId = string.Empty;
		try
		{
			commandManager.ProcessCommand("/qst stop");
		}
		catch
		{
		}
		RestoreQuestionablePriority();
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!isRotationActive || (DateTime.Now - lastUpdate).TotalMilliseconds < 500.0)
		{
			return;
		}
		lastUpdate = DateTime.Now;
		try
		{
			switch (currentPhase)
			{
			case AlliedSocietyRotationPhase.StartingRotation:
				HandleStartingRotation();
				break;
			case AlliedSocietyRotationPhase.ImportingQuests:
				HandleImportingQuests();
				break;
			case AlliedSocietyRotationPhase.WaitingForQuestAccept:
				HandleWaitingForQuestAccept();
				break;
			case AlliedSocietyRotationPhase.MonitoringQuests:
				HandleMonitoringQuests();
				break;
			case AlliedSocietyRotationPhase.CheckingCompletion:
				HandleCheckingCompletion();
				break;
			case AlliedSocietyRotationPhase.WaitingForCharacterSwitch:
				HandleWaitingForCharacterSwitch();
				break;
			}
		}
		catch (Exception ex)
		{
			log.Error("[AlliedSociety] Error in rotation loop: " + ex.Message);
			StopRotation();
		}
	}

	private void HandleStartingRotation()
	{
		if (playerState.ContentId == 0L)
		{
			return;
		}
		string currentCharacter = autoRetainerIpc.GetCurrentCharacter();
		if (string.IsNullOrEmpty(currentCharacter) || currentCharacter != currentCharacterId)
		{
			double totalSeconds = (DateTime.Now - characterSwitchStartTime).TotalSeconds;
			if (!(totalSeconds > 20.0))
			{
				return;
			}
			log.Warning($"[AlliedSociety] Character switch timeout ({totalSeconds:F1}s). Retrying /ar relog for {currentCharacterId}...");
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					commandManager.ProcessCommand("/ays relog " + currentCharacterId);
					log.Information("[AlliedSociety] Retry relog command sent for " + currentCharacterId);
				}
				catch (Exception ex)
				{
					log.Error("[AlliedSociety] Failed to send retry relog: " + ex.Message);
				}
			});
			characterSwitchStartTime = DateTime.Now;
		}
		else if (questionableIpc.TryEnsureAvailableSilent())
		{
			log.Information("[AlliedSociety] ✓ Character logged in (" + currentCharacter + "), Questionable ready");
			SetPhase(AlliedSocietyRotationPhase.ImportingQuests);
		}
	}

	private void HandleImportingQuests()
	{
		int alliedSocietyRemainingAllowances = questionableIpc.GetAlliedSocietyRemainingAllowances();
		log.Information($"[AlliedSociety] Remaining allowances: {alliedSocietyRemainingAllowances}");
		if (alliedSocietyRemainingAllowances <= 0)
		{
			log.Information("[AlliedSociety] No allowances left. Checking completion...");
			SetPhase(AlliedSocietyRotationPhase.CheckingCompletion);
			return;
		}
		List<string> list = questSelector.SelectQuestsForCharacter(currentCharacterId, alliedSocietyRemainingAllowances, configuration.AlliedSociety.RotationConfig.Priorities, configuration.AlliedSociety.RotationConfig.QuestMode);
		if (list.Count == 0)
		{
			consecutiveNoQuestsCount++;
			log.Warning($"[AlliedSociety] No quests selected (attempt {consecutiveNoQuestsCount}/3). Checking completion...");
			if (consecutiveNoQuestsCount >= 3)
			{
				log.Error("[AlliedSociety] Failed to select quests 3 times consecutively. No quests available for this character.");
				log.Information("[AlliedSociety] Marking character as complete and moving to next...");
				consecutiveNoQuestsCount = 0;
				database.SetCharacterComplete(currentCharacterId, DateTime.Now);
				AdvanceToNextCharacter();
			}
			else
			{
				SetPhase(AlliedSocietyRotationPhase.CheckingCompletion);
			}
			return;
		}
		consecutiveNoQuestsCount = 0;
		log.Information($"[AlliedSociety] Importing {list.Count} quests to Questionable...");
		foreach (string item in list)
		{
			log.Debug("[AlliedSociety] Adding quest " + item + " to priority");
			questionableIpc.AddQuestPriority(item);
			database.AddImportedQuest(currentCharacterId, item);
		}
		log.Information("✓ All quests imported to Questionable");
		log.Information("[AlliedSociety] Sending /qst start command...");
		framework.RunOnFrameworkThread(delegate
		{
			try
			{
				commandManager.ProcessCommand("/qst start");
				log.Information("[AlliedSociety] ✓ /qst start command sent successfully");
			}
			catch (Exception ex)
			{
				log.Error("[AlliedSociety] ✗ Failed to send /qst start: " + ex.Message);
			}
		});
		log.Information("[AlliedSociety] Transitioning to WaitingForQuestAccept phase");
		SetPhase(AlliedSocietyRotationPhase.WaitingForQuestAccept);
	}

	private void HandleWaitingForQuestAccept()
	{
		AlliedSocietyCharacterStatus characterStatus = database.GetCharacterStatus(currentCharacterId);
		bool flag = true;
		int num = 0;
		int count = characterStatus.ImportedQuestIds.Count;
		foreach (string importedQuestId in characterStatus.ImportedQuestIds)
		{
			if (questionableIpc.IsQuestAccepted(importedQuestId))
			{
				num++;
			}
			else
			{
				flag = false;
			}
		}
		if (flag)
		{
			log.Information($"[AlliedSociety] ✓ All {count} quests accepted. Monitoring progress...");
			SetPhase(AlliedSocietyRotationPhase.MonitoringQuests);
		}
	}

	private void HandleMonitoringQuests()
	{
		string currentQuestId = questionableIpc.GetCurrentQuestId();
		AlliedSocietyCharacterStatus characterStatus = database.GetCharacterStatus(currentCharacterId);
		log.Debug("[AlliedSociety] Monitoring - Current Quest: " + (currentQuestId ?? "null"));
		if (currentQuestId != null && characterStatus.ImportedQuestIds.Contains(currentQuestId))
		{
			log.Debug("[AlliedSociety] Working on imported quest: " + currentQuestId);
		}
		else if (currentQuestId == null || !characterStatus.ImportedQuestIds.Contains(currentQuestId))
		{
			log.Information("[AlliedSociety] No longer working on imported quests. Checking completion...");
			SetPhase(AlliedSocietyRotationPhase.CheckingCompletion);
		}
	}

	private void HandleCheckingCompletion()
	{
		int alliedSocietyRemainingAllowances = questionableIpc.GetAlliedSocietyRemainingAllowances();
		log.Information($"[AlliedSociety] Checking completion. Allowances: {alliedSocietyRemainingAllowances}");
		if (alliedSocietyRemainingAllowances == 0)
		{
			string currentQuestId = questionableIpc.GetCurrentQuestId();
			AlliedSocietyCharacterStatus characterStatus = database.GetCharacterStatus(currentCharacterId);
			if (currentQuestId != null && characterStatus.ImportedQuestIds.Contains(currentQuestId))
			{
				log.Information("[AlliedSociety] Still working on final quest " + currentQuestId + ". Waiting...");
				return;
			}
			log.Information("[AlliedSociety] Character " + currentCharacterId + " completed all allowances.");
			try
			{
				commandManager.ProcessCommand("/qst stop");
				log.Information("[AlliedSociety] Sent /qst stop command after quest completion");
			}
			catch (Exception ex)
			{
				log.Error("[AlliedSociety] Failed to send /qst stop: " + ex.Message);
			}
			questionableIpc.ClearQuestPriority();
			database.SetCharacterComplete(currentCharacterId, DateTime.Now);
			SetPhase(AlliedSocietyRotationPhase.WaitingForCharacterSwitch);
		}
		else
		{
			log.Information("[AlliedSociety] Allowances remaining. Trying to import more quests...");
			questionableIpc.ClearQuestPriority();
			database.ClearImportedQuests(currentCharacterId);
			SetPhase(AlliedSocietyRotationPhase.ImportingQuests);
		}
	}

	private void HandleWaitingForCharacterSwitch()
	{
		if (!((DateTime.Now - phaseStartTime).TotalSeconds < 2.0))
		{
			AdvanceToNextCharacter();
		}
	}

	private void AdvanceToNextCharacter()
	{
		currentCharacterIndex++;
		if (currentCharacterIndex >= rotationCharacters.Count)
		{
			log.Information("[AlliedSociety] Rotation completed for all characters.");
			StopRotation();
			return;
		}
		string text = rotationCharacters[currentCharacterIndex];
		if (database.GetCharacterStatus(text).Status == AlliedSocietyRotationStatus.Complete)
		{
			log.Information("[AlliedSociety] Skipping " + text + " (Already Complete)");
			AdvanceToNextCharacter();
			return;
		}
		log.Information("[AlliedSociety] Switching to " + text);
		currentCharacterId = text;
		characterSwitchStartTime = DateTime.Now;
		if (autoRetainerIpc.SwitchCharacter(text))
		{
			SetPhase(AlliedSocietyRotationPhase.StartingRotation);
			return;
		}
		log.Error("[AlliedSociety] Failed to switch to " + text);
		StopRotation();
	}

	private void SetPhase(AlliedSocietyRotationPhase phase)
	{
		log.Information($"[AlliedSociety] Phase: {currentPhase} → {phase}");
		currentPhase = phase;
		phaseStartTime = DateTime.Now;
	}

	private void RestoreQuestionablePriority()
	{
		if (savedQuestionablePriority != null)
		{
			if (questionableIpc.RestoreQuestPriority(savedQuestionablePriority))
			{
				savedQuestionablePriority = null;
				log.Information("[AlliedSociety] Restored the user's Questionable priority queue");
			}
			else
			{
				log.Warning("[AlliedSociety] Failed to restore the user's Questionable priority queue");
			}
		}
	}
}
