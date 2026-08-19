using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuestionableCompanion.Services;

public class EventQuestExecutionService : IDisposable
{
	private readonly AutoRetainerIPC autoRetainerIpc;

	private readonly QuestionableIPC questionableIPC;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly ICondition condition;

	private readonly Configuration configuration;

	private readonly EventQuestResolver eventQuestResolver;

	private EventQuestState currentState = new EventQuestState();

	private Dictionary<string, List<string>> eventQuestCompletionByCharacter = new Dictionary<string, List<string>>();

	private DateTime lastCheckTime = DateTime.MinValue;

	private const double CheckIntervalMs = 250.0;

	private bool isRotationActive;

	private string? savedQuestionablePriority;

	private Action? onDataChanged;

	public bool IsRotationActive => isRotationActive;

	public EventQuestExecutionService(AutoRetainerIPC autoRetainerIpc, QuestionableIPC questionableIPC, IPluginLog log, IFramework framework, ICommandManager commandManager, ICondition condition, Configuration configuration, IDataManager dataManager, Action? onDataChanged = null)
	{
		this.autoRetainerIpc = autoRetainerIpc;
		this.questionableIPC = questionableIPC;
		this.log = log;
		this.framework = framework;
		this.commandManager = commandManager;
		this.condition = condition;
		this.configuration = configuration;
		this.onDataChanged = onDataChanged;
		eventQuestResolver = new EventQuestResolver(dataManager, log);
		framework.Update += OnFrameworkUpdate;
		log.Information("[EventQuest] Service initialized");
	}

	public bool StartEventQuestRotation(string eventQuestId, List<string> characters)
	{
		if (characters == null || characters.Count == 0)
		{
			log.Error("[EventQuest] Cannot start rotation: No characters selected");
			return false;
		}
		if (string.IsNullOrEmpty(eventQuestId))
		{
			log.Error("[EventQuest] Cannot start rotation: Event Quest ID is empty");
			return false;
		}
		List<string> list = eventQuestResolver.ResolveEventQuestDependencies(eventQuestId);
		List<string> list2 = new List<string>();
		List<string> list3 = new List<string>();
		foreach (string character in characters)
		{
			if (HasCharacterCompletedEventQuest(eventQuestId, character))
			{
				list3.Add(character);
				log.Debug("[EventQuest] " + character + " already completed event quest " + eventQuestId);
			}
			else
			{
				list2.Add(character);
				log.Debug("[EventQuest] " + character + " needs to complete event quest " + eventQuestId);
			}
		}
		if (list2.Count == 0)
		{
			log.Information("[EventQuest] All characters have already completed event quest " + eventQuestId);
			return false;
		}
		if (!questionableIPC.TryExportQuestPriority(out string encodedQuestPriority))
		{
			log.Error("[EventQuest] Cannot start rotation: Questionable priority queue could not be saved");
			return false;
		}
		savedQuestionablePriority = encodedQuestPriority;
		string currentCharacter = autoRetainerIpc.GetCurrentCharacter();
		bool flag = !string.IsNullOrEmpty(currentCharacter) && list2.Contains(currentCharacter);
		currentState = new EventQuestState
		{
			EventQuestId = eventQuestId,
			EventQuestName = eventQuestResolver.GetQuestName(eventQuestId),
			SelectedCharacters = new List<string>(characters),
			RemainingCharacters = list2,
			CompletedCharacters = list3,
			DependencyQuests = list,
			Phase = ((!flag) ? EventQuestPhase.InitializingFirstCharacter : EventQuestPhase.CheckingQuestCompletion),
			CurrentCharacter = (flag ? currentCharacter : ""),
			PhaseStartTime = DateTime.Now,
			RotationStartTime = DateTime.Now
		};
		isRotationActive = true;
		log.Information("[EventQuest] ═══ Starting Event Quest Rotation ═══");
		log.Information($"[EventQuest] Event Quest: {currentState.EventQuestName} ({eventQuestId})");
		log.Information($"[EventQuest] Total Characters: {characters.Count}");
		log.Information($"[EventQuest] Remaining: {list2.Count} | Completed: {list3.Count}");
		log.Information($"[EventQuest] Dependencies to resolve: {list.Count}");
		if (list.Count > 0)
		{
			log.Information("[EventQuest] Prerequisites: " + string.Join(", ", list.Select((string id) => eventQuestResolver.GetQuestName(id))));
		}
		if (flag)
		{
			log.Information("[EventQuest] User already logged in as " + currentCharacter + " - starting immediately");
		}
		return true;
	}

	public EventQuestState GetCurrentState()
	{
		return currentState;
	}

	public void LoadEventQuestCompletionData(Dictionary<string, List<string>> data)
	{
		if (data != null && data.Count > 0)
		{
			eventQuestCompletionByCharacter = new Dictionary<string, List<string>>(data);
			log.Information($"[EventQuest] Loaded completion data for {data.Count} event quests");
		}
	}

	public Dictionary<string, List<string>> GetEventQuestCompletionData()
	{
		return new Dictionary<string, List<string>>(eventQuestCompletionByCharacter);
	}

	public void AbortRotation()
	{
		log.Information("[EventQuest] Aborting Event Quest rotation");
		RestoreQuestionablePriority();
		currentState = new EventQuestState
		{
			Phase = EventQuestPhase.Idle
		};
		isRotationActive = false;
	}

	private void MarkEventQuestCompleted(string eventQuestId, string characterName)
	{
		if (!eventQuestCompletionByCharacter.ContainsKey(eventQuestId))
		{
			eventQuestCompletionByCharacter[eventQuestId] = new List<string>();
		}
		if (!eventQuestCompletionByCharacter[eventQuestId].Contains(characterName))
		{
			eventQuestCompletionByCharacter[eventQuestId].Add(characterName);
			log.Debug("[EventQuest] Marked " + characterName + " as completed event quest " + eventQuestId);
			onDataChanged?.Invoke();
		}
	}

	private bool HasCharacterCompletedEventQuest(string eventQuestId, string characterName)
	{
		if (eventQuestCompletionByCharacter.TryGetValue(eventQuestId, out List<string> value))
		{
			return value.Contains(characterName);
		}
		return false;
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!isRotationActive)
		{
			return;
		}
		DateTime now = DateTime.Now;
		if (!((now - lastCheckTime).TotalMilliseconds < 250.0))
		{
			lastCheckTime = now;
			switch (currentState.Phase)
			{
			case EventQuestPhase.InitializingFirstCharacter:
				HandleInitializingFirstCharacter();
				break;
			case EventQuestPhase.WaitingForCharacterLogin:
				HandleWaitingForCharacterLogin();
				break;
			case EventQuestPhase.CheckingQuestCompletion:
				HandleCheckingQuestCompletion();
				break;
			case EventQuestPhase.ResolvingDependencies:
				HandleResolvingDependencies();
				break;
			case EventQuestPhase.ExecutingDependencies:
				HandleExecutingDependencies();
				break;
			case EventQuestPhase.WaitingForQuestStart:
			case EventQuestPhase.QuestActive:
				HandleQuestMonitoring();
				break;
			case EventQuestPhase.WaitingBeforeCharacterSwitch:
				HandleWaitingBeforeCharacterSwitch();
				break;
			case EventQuestPhase.Completed:
				HandleCompleted();
				break;
			}
		}
	}

	private void HandleInitializingFirstCharacter()
	{
		if (currentState.RemainingCharacters.Count == 0)
		{
			log.Information("[EventQuest] No remaining characters - rotation complete");
			currentState.Phase = EventQuestPhase.Completed;
			isRotationActive = false;
			return;
		}
		string text = currentState.RemainingCharacters[0];
		currentState.CurrentCharacter = text;
		log.Information("[EventQuest] >>> Initializing first character: " + text);
		if (autoRetainerIpc.SwitchCharacter(text))
		{
			currentState.Phase = EventQuestPhase.WaitingForCharacterLogin;
			currentState.PhaseStartTime = DateTime.Now;
			log.Information("[EventQuest] Character switch initiated to " + text);
		}
		else
		{
			log.Error("[EventQuest] Failed to switch to " + text);
			currentState.Phase = EventQuestPhase.Error;
			currentState.ErrorMessage = "Failed to switch to " + text;
		}
	}

	private void HandleWaitingForCharacterLogin()
	{
		if ((DateTime.Now - currentState.PhaseStartTime).TotalSeconds > 60.0)
		{
			log.Error("[EventQuest] Login timeout for " + currentState.CurrentCharacter);
			SkipToNextCharacter();
			return;
		}
		string currentCharacter = autoRetainerIpc.GetCurrentCharacter();
		if (!string.IsNullOrEmpty(currentCharacter) && currentCharacter == currentState.CurrentCharacter && !((DateTime.Now - currentState.PhaseStartTime).TotalSeconds < 5.0))
		{
			log.Information("[EventQuest] Successfully logged in as " + currentCharacter);
			currentState.Phase = EventQuestPhase.CheckingQuestCompletion;
			currentState.PhaseStartTime = DateTime.Now;
		}
	}

	private void HandleCheckingQuestCompletion()
	{
		string eventQuestId = currentState.EventQuestId;
		string item = QuestIdParser.ParseQuestId(eventQuestId).rawId;
		QuestIdType value = QuestIdParser.ClassifyQuestId(eventQuestId);
		log.Debug($"[EventQuest] Checking completion for {eventQuestId} (Type: {value}, RawId: {item})");
		if (!uint.TryParse(item, out var result))
		{
			log.Error($"[EventQuest] Invalid quest ID: {eventQuestId} (cannot parse numeric part: {item})");
			SkipToNextCharacter();
			return;
		}
		bool flag = false;
		try
		{
			flag = QuestManager.IsQuestComplete(result);
		}
		catch (Exception ex)
		{
			log.Error("[EventQuest] Error checking quest completion: " + ex.Message);
		}
		if (flag)
		{
			log.Information("[EventQuest] " + currentState.CurrentCharacter + " already completed event quest " + eventQuestId);
			List<string> completedCharacters = currentState.CompletedCharacters;
			if (!completedCharacters.Contains(currentState.CurrentCharacter))
			{
				completedCharacters.Add(currentState.CurrentCharacter);
				currentState.CompletedCharacters = completedCharacters;
			}
			MarkEventQuestCompleted(eventQuestId, currentState.CurrentCharacter);
			SkipToNextCharacter();
		}
		else
		{
			log.Information("[EventQuest] " + currentState.CurrentCharacter + " needs to complete event quest " + eventQuestId);
			log.Information($"[EventQuest] >>> Starting event quest with {currentState.DependencyQuests.Count} prerequisites");
			StartEventQuest();
		}
	}

	private void HandleResolvingDependencies()
	{
		log.Information("[EventQuest] All prerequisites completed - starting event quest");
		StartEventQuest();
	}

	private void HandleExecutingDependencies()
	{
		string currentExecutingQuest = currentState.CurrentExecutingQuest;
		if (!uint.TryParse(currentExecutingQuest, out var result))
		{
			log.Error("[EventQuest] Invalid dependency quest ID: " + currentExecutingQuest);
			currentState.DependencyIndex++;
			currentState.Phase = EventQuestPhase.ResolvingDependencies;
			return;
		}
		bool flag = false;
		try
		{
			flag = QuestManager.IsQuestComplete(result);
		}
		catch
		{
		}
		if (flag)
		{
			log.Information("[EventQuest] Dependency " + eventQuestResolver.GetQuestName(currentExecutingQuest) + " already completed");
			currentState.DependencyIndex++;
			currentState.Phase = EventQuestPhase.ResolvingDependencies;
			return;
		}
		try
		{
			commandManager.ProcessCommand("/qst start");
			log.Information("[EventQuest] Started dependency quest: " + eventQuestResolver.GetQuestName(currentExecutingQuest));
		}
		catch (Exception ex)
		{
			log.Error("[EventQuest] Failed to start dependency: " + ex.Message);
		}
		currentState.Phase = EventQuestPhase.QuestActive;
		currentState.HasEventQuestBeenAccepted = false;
		currentState.PhaseStartTime = DateTime.Now;
	}

	private void HandleQuestMonitoring()
	{
		string eventQuestId = currentState.EventQuestId;
		try
		{
			if (questionableIPC.IsQuestComplete(eventQuestId))
			{
				log.Information("[EventQuest] Event quest " + eventQuestId + " completed by " + currentState.CurrentCharacter);
				MarkEventQuestCompleted(currentState.EventQuestId, currentState.CurrentCharacter);
				List<string> completedCharacters = currentState.CompletedCharacters;
				if (!completedCharacters.Contains(currentState.CurrentCharacter))
				{
					completedCharacters.Add(currentState.CurrentCharacter);
					currentState.CompletedCharacters = completedCharacters;
				}
				try
				{
					commandManager.ProcessCommand("/qst stop");
					log.Information("[EventQuest] Sent /qst stop");
				}
				catch
				{
				}
				currentState.Phase = EventQuestPhase.WaitingBeforeCharacterSwitch;
				currentState.PhaseStartTime = DateTime.Now;
			}
		}
		catch (Exception ex)
		{
			log.Error("[EventQuest] Error checking quest completion via IPC: " + ex.Message);
		}
	}

	private void HandleWaitingBeforeCharacterSwitch()
	{
		if (!condition[ConditionFlag.BetweenAreas] && (DateTime.Now - currentState.PhaseStartTime).TotalSeconds >= 2.0)
		{
			PerformCharacterSwitch();
		}
	}

	private void HandleCompleted()
	{
		log.Information("[EventQuest] ═══ EVENT QUEST ROTATION COMPLETED ═══");
		log.Information($"[EventQuest] All {currentState.CompletedCharacters.Count} characters completed the event quest");
		RestoreQuestionablePriority();
		isRotationActive = false;
		currentState.Phase = EventQuestPhase.Idle;
	}

	private void StartEventQuest()
	{
		List<string> list = new List<string>();
		if (currentState.DependencyQuests.Count > 0)
		{
			foreach (string dependencyQuest in currentState.DependencyQuests)
			{
				list.Add(dependencyQuest);
				QuestIdType value = QuestIdParser.ClassifyQuestId(dependencyQuest);
				log.Information($"[EventQuest] Adding dependency: {dependencyQuest} (Type: {value})");
			}
		}
		string eventQuestId = currentState.EventQuestId;
		list.Add(eventQuestId);
		QuestIdType value2 = QuestIdParser.ClassifyQuestId(eventQuestId);
		log.Information($"[EventQuest] Adding main event quest: {eventQuestId} (Type: {value2})");
		log.Information($"[EventQuest] Setting {list.Count} quests as Questionable priority");
		if (questionableIPC.IsAvailable)
		{
			try
			{
				questionableIPC.ClearQuestPriority();
				log.Information("[EventQuest] Cleared existing quest priority queue");
			}
			catch (Exception ex)
			{
				log.Warning("[EventQuest] Failed to clear quest priority: " + ex.Message);
			}
			foreach (string item in list)
			{
				try
				{
					bool value3 = questionableIPC.AddQuestPriority(item);
					log.Information($"[EventQuest] Added quest {item} to priority: {value3}");
				}
				catch (Exception ex2)
				{
					log.Warning("[EventQuest] Failed to add quest " + item + " to priority: " + ex2.Message);
				}
			}
		}
		else
		{
			log.Warning("[EventQuest] Questionable IPC not available - cannot set priority");
		}
		if (condition[ConditionFlag.BetweenAreas])
		{
			log.Debug("[EventQuest] Character is between areas - waiting before starting quest");
			return;
		}
		if (questionableIPC.IsAvailable && questionableIPC.IsRunning())
		{
			log.Debug("[EventQuest] Questionable is busy - waiting before starting quest");
			return;
		}
		try
		{
			commandManager.ProcessCommand("/qst start");
			log.Information("[EventQuest] Sent /qst start for event quest");
			currentState.Phase = EventQuestPhase.QuestActive;
			currentState.CurrentExecutingQuest = currentState.EventQuestId;
			currentState.HasEventQuestBeenAccepted = false;
			currentState.PhaseStartTime = DateTime.Now;
		}
		catch (Exception ex3)
		{
			log.Error("[EventQuest] Failed to start quest: " + ex3.Message);
		}
	}

	private void SkipToNextCharacter()
	{
		try
		{
			commandManager.ProcessCommand("/qst stop");
			log.Information("[EventQuest] Sent /qst stop before character switch");
		}
		catch
		{
		}
		List<string> remainingCharacters = currentState.RemainingCharacters;
		List<string> completedCharacters = currentState.CompletedCharacters;
		if (remainingCharacters.Contains(currentState.CurrentCharacter))
		{
			remainingCharacters.Remove(currentState.CurrentCharacter);
			currentState.RemainingCharacters = remainingCharacters;
		}
		if (!completedCharacters.Contains(currentState.CurrentCharacter))
		{
			completedCharacters.Add(currentState.CurrentCharacter);
			currentState.CompletedCharacters = completedCharacters;
			log.Information("[EventQuest] Character " + currentState.CurrentCharacter + " marked as completed (skipped)");
		}
		currentState.Phase = EventQuestPhase.WaitingBeforeCharacterSwitch;
		currentState.PhaseStartTime = DateTime.Now;
	}

	private void PerformCharacterSwitch()
	{
		List<string> remainingCharacters = currentState.RemainingCharacters;
		if (remainingCharacters.Contains(currentState.CurrentCharacter))
		{
			remainingCharacters.Remove(currentState.CurrentCharacter);
			currentState.RemainingCharacters = remainingCharacters;
		}
		if (currentState.RemainingCharacters.Count == 0)
		{
			currentState.Phase = EventQuestPhase.Completed;
			return;
		}
		string text = currentState.RemainingCharacters[0];
		currentState.CurrentCharacter = text;
		currentState.NextCharacter = text;
		log.Information("[EventQuest] Switching to next character: " + text);
		log.Information($"[EventQuest] Progress: {currentState.CompletedCharacters.Count}/{currentState.SelectedCharacters.Count} completed");
		if (autoRetainerIpc.SwitchCharacter(text))
		{
			currentState.Phase = EventQuestPhase.WaitingForCharacterLogin;
			currentState.PhaseStartTime = DateTime.Now;
		}
		else
		{
			log.Error("[EventQuest] Failed to switch to " + text);
			currentState.Phase = EventQuestPhase.Error;
			currentState.ErrorMessage = "Failed to switch character";
		}
	}

	private void RestoreQuestionablePriority()
	{
		if (savedQuestionablePriority != null)
		{
			if (questionableIPC.RestoreQuestPriority(savedQuestionablePriority))
			{
				savedQuestionablePriority = null;
				log.Information("[EventQuest] Restored the user's Questionable priority queue");
			}
			else
			{
				log.Warning("[EventQuest] Failed to restore the user's Questionable priority queue");
			}
		}
	}

	public void Dispose()
	{
		RestoreQuestionablePriority();
		framework.Update -= OnFrameworkUpdate;
		log.Information("[EventQuest] Service disposed");
	}
}
