using System;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace QuestionableCompanion.Services;

public class ARRTrialAutomationService : IDisposable
{
	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly IChatGui chatGui;

	private readonly Configuration config;

	private readonly QuestionableIPC questionableIPC;

	private readonly SubmarineManager submarineManager;

	private readonly HelperManager helperManager;

	private readonly IPartyList partyList;

	private readonly ICondition condition;

	private readonly MemoryHelper memoryHelper;

	private readonly IClientState clientState;

	private readonly QuestDetectionService questDetection;

	private RuntimeEventSubscription? loginSubscription;

	private bool isInDuty;

	private static readonly (uint QuestId, uint TrialId, string ADCommand, string Name)[] Trials = new(uint, uint, string, string)[3]
	{
		(1048u, 20004u, "/ad run trial 292 1", "Ifrit HM"),
		(1157u, 20006u, "/ad run trial 294 1", "Garuda HM"),
		(1158u, 20005u, "/ad run trial 293 1", "Titan HM")
	};

	private const uint TRIGGER_QUEST = 89u;

	private const uint TARGET_QUEST = 363u;

	private bool isProcessing;

	private int currentTrialIndex = -1;

	private bool waitingForQuest;

	private bool waitingForParty;

	private bool waitingForTrial;

	private DateTime lastCheckTime = DateTime.MinValue;

	private DateTime lastInviteAttempt = DateTime.MinValue;

	public ARRTrialAutomationService(IPluginLog log, IFramework framework, ICommandManager commandManager, IChatGui chatGui, Configuration config, QuestionableIPC questionableIPC, SubmarineManager submarineManager, HelperManager helperManager, IPartyList partyList, ICondition condition, MemoryHelper memoryHelper, IClientState clientState, QuestDetectionService questDetection)
	{
		this.log = log;
		this.framework = framework;
		this.commandManager = commandManager;
		this.chatGui = chatGui;
		this.config = config;
		this.questionableIPC = questionableIPC;
		this.submarineManager = submarineManager;
		this.helperManager = helperManager;
		this.partyList = partyList;
		this.condition = condition;
		this.memoryHelper = memoryHelper;
		this.clientState = clientState;
		this.questDetection = questDetection;
		framework.Update += OnFrameworkUpdate;
		condition.ConditionChange += OnConditionChanged;
		loginSubscription = RuntimeEventSubscription.Subscribe(clientState, "Login", OnLogin, log, "ARRTrials.Login");
		log.Information("[ARRTrials] Service initialized");
	}

	private void OnLogin()
	{
		log.Information("[ARRTrials] Login detected - Resetting state");
		Reset();
		questDetection.ResetTracking();
		log.Information("[ARRTrials] Quest detection tracking reset for new character");
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!isProcessing)
		{
			return;
		}
		if (waitingForParty)
		{
			if (partyList != null && partyList.Length > 1)
			{
				if (!((DateTime.Now - lastCheckTime).TotalSeconds < 1.0))
				{
					lastCheckTime = DateTime.Now;
					log.Information($"[ARRTrials] Party join detected (Size: {partyList.Length}) - Triggering trial...");
					waitingForParty = false;
					TriggerCurrentTrial();
				}
				return;
			}
			if ((DateTime.Now - lastInviteAttempt).TotalSeconds > 10.0)
			{
				lastInviteAttempt = DateTime.Now;
				log.Information("[ARRTrials] Party not yet formed (Size < 2). Retrying helper invite...");
				helperManager.InviteHelpers();
			}
		}
		if (waitingForQuest && currentTrialIndex >= 0 && currentTrialIndex < Trials.Length && !((DateTime.Now - lastCheckTime).TotalSeconds < 2.0))
		{
			lastCheckTime = DateTime.Now;
			(uint QuestId, uint TrialId, string ADCommand, string Name) tuple = Trials[currentTrialIndex];
			uint item = tuple.TrialId;
			string item2 = tuple.Name;
			bool flag = IsTrialUnlocked(item);
			log.Debug($"[ARRTrials] Polling {item2} ({item}) Unlocked: {flag}");
			if (flag)
			{
				log.Information("[ARRTrials] Polling detected " + item2 + " unlocked - Proceeding...");
				waitingForQuest = false;
				lastInviteAttempt = DateTime.Now;
				helperManager.InviteHelpers();
				waitingForParty = true;
			}
		}
	}

	public bool IsTrialComplete(uint instanceId)
	{
		return UIState.IsInstanceContentCompleted(instanceId);
	}

	public bool IsTrialUnlocked(uint instanceId)
	{
		return UIState.IsInstanceContentUnlocked(instanceId);
	}

	public bool IsTargetQuestAvailableOrComplete()
	{
		if (QuestManager.IsQuestComplete(363u))
		{
			return true;
		}
		if (questionableIPC.IsReadyToAcceptQuest(363u.ToString()))
		{
			return true;
		}
		return false;
	}

	public void OnTriggerQuestComplete()
	{
		if (!config.EnableARRPrimalCheck)
		{
			log.Debug("[ARRTrials] Feature disabled, skipping check");
			return;
		}
		log.Information("[ARRTrials] Quest 89 complete, starting ARR Primal check...");
		StartTrialChain();
	}

	public void StartTrialChain()
	{
		if (isProcessing)
		{
			log.Debug("[ARRTrials] Already processing trial chain");
			return;
		}
		isProcessing = true;
		submarineManager.SetExternalPause(paused: true);
		int num = -1;
		for (int num2 = Trials.Length - 1; num2 >= 0; num2--)
		{
			if (!IsTrialComplete(Trials[num2].TrialId))
			{
				for (int i = 0; i <= num2; i++)
				{
					if (!IsTrialComplete(Trials[i].TrialId))
					{
						num = i;
						break;
					}
				}
				break;
			}
		}
		if (num == -1)
		{
			log.Information("[ARRTrials] All trials already complete!");
			isProcessing = false;
			submarineManager.SetExternalPause(paused: false);
			return;
		}
		currentTrialIndex = num;
		log.Information($"[ARRTrials] Starting from trial index {num}: {Trials[num].Name}");
		ProcessCurrentTrial();
	}

	private void ProcessCurrentTrial()
	{
		if (currentTrialIndex < 0 || currentTrialIndex >= Trials.Length)
		{
			log.Information("[ARRTrials] Trial chain complete!");
			isProcessing = false;
			submarineManager.SetExternalPause(paused: false);
			return;
		}
		var (num, instanceId, _, text) = Trials[currentTrialIndex];
		if (IsTrialComplete(instanceId))
		{
			log.Information("[ARRTrials] " + text + " already complete, moving to next");
			currentTrialIndex++;
			ProcessCurrentTrial();
		}
		else if (!QuestManager.IsQuestComplete(num))
		{
			log.Information($"[ARRTrials] Queueing unlock quest {num} for {text}");
			questionableIPC.AddQuestPriority(num.ToString());
			framework.RunOnFrameworkThread(delegate
			{
				commandManager.ProcessCommand("/qst start");
			});
			waitingForQuest = true;
		}
		else
		{
			log.Information("[ARRTrials] " + text + " unlocked, inviting helper and triggering trial...");
			lastInviteAttempt = DateTime.Now;
			helperManager.InviteHelpers();
			waitingForParty = true;
		}
	}

	public void OnQuestComplete(uint questId)
	{
		if (!isProcessing || !waitingForQuest)
		{
			return;
		}
		for (int i = currentTrialIndex; i < Trials.Length; i++)
		{
			if (Trials[i].QuestId == questId)
			{
				log.Information($"[ARRTrials] Unlock quest {questId} completed, triggering trial");
				waitingForQuest = false;
				lastInviteAttempt = DateTime.Now;
				helperManager.InviteHelpers();
				waitingForParty = true;
				break;
			}
		}
	}

	public void OnPartyReady()
	{
		if (isProcessing && waitingForParty)
		{
			waitingForParty = false;
			TriggerCurrentTrial();
		}
	}

	private void TriggerCurrentTrial()
	{
		if (currentTrialIndex >= 0 && currentTrialIndex < Trials.Length)
		{
			(uint, uint, string, string) tuple = Trials[currentTrialIndex];
			string adCommand = tuple.Item3;
			string name = tuple.Item4;
			log.Information("[ARRTrials] Triggering " + name + " via AD command");
			framework.RunOnFrameworkThread(delegate
			{
				chatGui.Print(new XivChatEntry
				{
					Message = "[QSTCompanion] Triggering " + name + "...",
					Type = XivChatType.Echo
				});
				commandManager.ProcessCommand("/ad cfg Unsynced true");
				commandManager.ProcessCommand(adCommand);
			});
			waitingForTrial = true;
		}
	}

	public void OnDutyComplete()
	{
		if (!isProcessing || !waitingForTrial)
		{
			return;
		}
		(uint QuestId, uint TrialId, string ADCommand, string Name) tuple = Trials[currentTrialIndex];
		uint item = tuple.TrialId;
		string item2 = tuple.Name;
		if (IsTrialComplete(item))
		{
			log.Information("[ARRTrials] " + item2 + " completed successfully!");
			waitingForTrial = false;
			currentTrialIndex++;
			framework.RunOnFrameworkThread(delegate
			{
				ProcessCurrentTrial();
			});
		}
		else
		{
			log.Warning("[ARRTrials] " + item2 + " NOT complete after verification. Retrying current step...");
			waitingForTrial = false;
			framework.RunOnFrameworkThread(delegate
			{
				ProcessCurrentTrial();
			});
		}
	}

	public string GetStatus()
	{
		if (!isProcessing)
		{
			return "Idle";
		}
		if (currentTrialIndex >= 0 && currentTrialIndex < Trials.Length)
		{
			string item = Trials[currentTrialIndex].Name;
			if (waitingForQuest)
			{
				return "Waiting for " + item + " unlock quest";
			}
			if (waitingForParty)
			{
				return "Waiting for party (" + item + ")";
			}
			if (waitingForTrial)
			{
				return "In " + item;
			}
			return "Processing " + item;
		}
		return "Processing...";
	}

	public void Reset()
	{
		isProcessing = false;
		isInDuty = false;
		currentTrialIndex = -1;
		waitingForQuest = false;
		waitingForParty = false;
		waitingForTrial = false;
		submarineManager.SetExternalPause(paused: false);
	}

	public void Dispose()
	{
		Reset();
		framework.Update -= OnFrameworkUpdate;
		condition.ConditionChange -= OnConditionChanged;
		loginSubscription?.Dispose();
		log.Information("[ARRTrials] Service disposed");
	}

	private void OnConditionChanged(ConditionFlag flag, bool value)
	{
		if (flag == ConditionFlag.BoundByDuty)
		{
			if (value && !isInDuty)
			{
				isInDuty = true;
				log.Debug("[ARRTrials] Entered duty");
			}
			else if (!value && isInDuty)
			{
				isInDuty = false;
				OnDutyExited();
			}
		}
	}

	private void OnDutyExited()
	{
		if (!isProcessing || !waitingForTrial)
		{
			return;
		}
		log.Information("[ARRTrials] Exited duty - stopping AD and disbanding...");
		framework.RunOnFrameworkThread(delegate
		{
			commandManager.ProcessCommand("/ad stop");
		});
		Task.Run(async delegate
		{
			await Task.Delay(2000);
			framework.RunOnFrameworkThread(delegate
			{
				memoryHelper.SendChatMessage("/leave");
				commandManager.ProcessCommand("/ad stop");
				log.Information("[ARRTrials] /leave and safety /ad stop sent");
			});
			log.Information("[ARRTrials] Waiting for completion state check...");
			await Task.Delay(1000);
			(uint, uint, string, string) tuple = Trials[currentTrialIndex];
			uint trialId = tuple.Item2;
			for (int i = 0; i < 10; i++)
			{
				if (IsTrialComplete(trialId))
				{
					log.Information($"[ARRTrials] Completion verified on attempt {i + 1}");
					break;
				}
				await Task.Delay(1000);
			}
			OnDutyComplete();
		});
	}
}
