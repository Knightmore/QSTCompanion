using System;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;

namespace QuestionableCompanion.Services;

public class DungeonAutomationService : IDisposable
{
	private readonly ICondition condition;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly IGameGui gameGui;

	private readonly Configuration config;

	private readonly HelperManager helperManager;

	private readonly MemoryHelper memoryHelper;

	private readonly QuestionableIPC questionableIPC;

	private readonly CrossProcessIPC crossProcessIPC;

	private readonly MultiClientIPC multiClientIPC;

	private readonly IDutyState dutyState;

	private RuntimeEventSubscription? dutyCompletedSubscription;

	private bool isWaitingForParty;

	private DateTime partyInviteTime = DateTime.MinValue;

	private int inviteAttempts;

	private bool isInvitingHelpers;

	private DateTime helperInviteTime = DateTime.MinValue;

	private bool isInDuty;

	private bool hasStoppedAD;

	private DateTime dutyEntryTime = DateTime.MinValue;

	private bool pendingAutomationStop;

	private DateTime lastDutyExitTime = DateTime.MinValue;

	private DateTime lastDutyEntryTime = DateTime.MinValue;

	private bool expectingDutyEntry;

	private bool isAutomationActive;

	private Func<bool>? isRotationActiveChecker;

	private bool hasSentAtY;

	public bool IsInPostDungeonWindow => (DateTime.Now - lastDutyExitTime).TotalSeconds < 35.0;

	public bool IsWaitingForParty => isWaitingForParty;

	public int CurrentPartySize { get; private set; } = 1;

	public bool IsInAutoDutyDungeon => isAutomationActive;

	public void SetRotationActiveChecker(Func<bool> checker)
	{
		isRotationActiveChecker = checker;
	}

	private bool CanExecuteAutomation()
	{
		if (config.IsHelperAutomationActive)
		{
			return true;
		}
		if (config.IsQuester)
		{
			Func<bool>? func = isRotationActiveChecker;
			if (func == null || !func())
			{
				return false;
			}
			return true;
		}
		return false;
	}

	public DungeonAutomationService(ICondition condition, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, IGameGui gameGui, Configuration config, HelperManager helperManager, MemoryHelper memoryHelper, QuestionableIPC questionableIPC, CrossProcessIPC crossProcessIPC, MultiClientIPC multiClientIPC, IDutyState dutyState)
	{
		this.condition = condition;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.gameGui = gameGui;
		this.config = config;
		this.helperManager = helperManager;
		this.memoryHelper = memoryHelper;
		this.questionableIPC = questionableIPC;
		this.crossProcessIPC = crossProcessIPC;
		this.multiClientIPC = multiClientIPC;
		this.dutyState = dutyState;
		condition.ConditionChange += OnConditionChanged;
		SubscribeDutyCompleted();
		log.Information("[DungeonAutomation] Service initialized with ConditionChange and DutyCompleted events");
		log.Information($"[DungeonAutomation] Config - Required Party Size: {config.AutoDutyPartySize}");
		log.Information($"[DungeonAutomation] Config - Party Wait Time: {config.AutoDutyMaxWaitForParty}s");
		log.Information($"[DungeonAutomation] Config - Dungeon Automation Enabled: {config.EnableAutoDutyUnsynced}");
	}

	public void StartDungeonAutomation()
	{
		if (!isAutomationActive)
		{
			if (!CanExecuteAutomation())
			{
				log.Information("[DungeonAutomation] Start request ignored - validation failed (Check Role/Rotation)");
				return;
			}
			log.Information("[DungeonAutomation] ========================================");
			log.Information("[DungeonAutomation] === STARTING DUNGEON AUTOMATION ===");
			log.Information("[DungeonAutomation] ========================================");
			isAutomationActive = true;
			expectingDutyEntry = true;
			log.Information("[DungeonAutomation] Inviting helpers via HelperManager...");
			helperManager.InviteHelpers();
			isInvitingHelpers = true;
			helperInviteTime = DateTime.Now;
			inviteAttempts = 0;
		}
	}

	public void SetDutyModeBasedOnConfig()
	{
		if (!questionableIPC.TryEnsureAvailableSilent())
		{
			log.Warning("[DungeonAutomation] Cannot apply Duty Mode setting: Questionable IPC API is unavailable");
			return;
		}
		int num = (config.EnableAutoDutyUnsynced ? 2 : 0);
		string value = (config.EnableAutoDutyUnsynced ? "Unsync Party" : "Support");
		if (questionableIPC.SetDefaultDutyMode(num))
		{
			log.Information($"[DungeonAutomation] Set persistent Questionable Duty Mode to {value} ({num})");
		}
		else
		{
			log.Warning($"[DungeonAutomation] Questionable rejected Duty Mode {value} ({num})");
		}
	}

	public void SetSupportDutyMode()
	{
		if (!questionableIPC.TryEnsureAvailableSilent())
		{
			log.Warning("[DungeonAutomation] Cannot reset Duty Mode to Support: Questionable IPC API is unavailable");
		}
		else if (questionableIPC.SetDefaultDutyMode(0))
		{
			log.Information("[DungeonAutomation] Reset Questionable Duty Mode to Support (0)");
		}
		else
		{
			log.Warning("[DungeonAutomation] Questionable rejected Support Duty Mode (0)");
		}
	}

	public void StopDungeonAutomation()
	{
		if (isAutomationActive)
		{
			log.Information("[DungeonAutomation] ========================================");
			log.Information("[DungeonAutomation] === STOPPING DUNGEON AUTOMATION ===");
			log.Information("[DungeonAutomation] ========================================");
			isAutomationActive = false;
			Reset();
		}
	}

	public void OnHelperAutomationDeactivated()
	{
		expectingDutyEntry = false;
		dutyEntryTime = DateTime.MinValue;
		pendingAutomationStop = false;
		hasStoppedAD = true;
		Reset();
		log.Information("[DungeonAutomation] Helper automation deactivated; pending helper dungeon actions were cleared without controlling AutoDuty.");
	}

	private void UpdateHelperInvite()
	{
		double totalSeconds = (DateTime.Now - helperInviteTime).TotalSeconds;
		try
		{
			if (totalSeconds >= 2.0)
			{
				isInvitingHelpers = false;
				isWaitingForParty = true;
				partyInviteTime = DateTime.Now;
				log.Information("[DungeonAutomation] Helper invites sent, waiting for party...");
			}
		}
		catch (Exception ex)
		{
			log.Error("[DungeonAutomation] Error in helper invite: " + ex.Message);
			isInvitingHelpers = false;
		}
	}

	public void Update()
	{
		if ((config.IsHighLevelHelper && !config.IsHelperAutomationActive) || (!CanExecuteAutomation() && !isAutomationActive))
		{
			return;
		}
		if (config.EnableAutoDutyUnsynced && !isAutomationActive)
		{
			CheckWaitForPartyTask();
		}
		if (!hasStoppedAD && dutyEntryTime != DateTime.MinValue && (DateTime.Now - dutyEntryTime).TotalSeconds >= 1.0)
		{
			try
			{
				commandManager.ProcessCommand("/ad stop");
				hasStoppedAD = true;
				dutyEntryTime = DateTime.MinValue;
			}
			catch (Exception ex)
			{
				log.Error("[DungeonAutomation] Failed to stop AD: " + ex.Message);
			}
		}
		if (isInvitingHelpers)
		{
			UpdateHelperInvite();
		}
		else if (pendingAutomationStop && (DateTime.Now - dutyEntryTime).TotalSeconds >= 5.0)
		{
			log.Information("[DungeonAutomation] 5s delay complete - stopping automation now");
			StopDungeonAutomation();
			pendingAutomationStop = false;
		}
		else if (isWaitingForParty)
		{
			UpdatePartySize();
			if (CurrentPartySize >= config.AutoDutyPartySize)
			{
				log.Information("[DungeonAutomation] ========================================");
				log.Information("[DungeonAutomation] === PARTY FULL ===");
				log.Information("[DungeonAutomation] ========================================");
				log.Information($"[DungeonAutomation] Party Size: {CurrentPartySize}/{config.AutoDutyPartySize}");
				isWaitingForParty = false;
				partyInviteTime = DateTime.MinValue;
				inviteAttempts = 0;
				log.Information("[DungeonAutomation] Party full - ready for dungeon!");
			}
			else if ((DateTime.Now - partyInviteTime).TotalSeconds >= (double)config.AutoDutyMaxWaitForParty)
			{
				log.Warning($"[DungeonAutomation] Party not full after {config.AutoDutyMaxWaitForParty}s - retrying invite (Attempt #{inviteAttempts + 1})");
				log.Information($"[DungeonAutomation] Current Party Size: {CurrentPartySize}/{config.AutoDutyPartySize}");
				log.Information("[DungeonAutomation] Retrying helper invites...");
				helperManager.InviteHelpers();
				partyInviteTime = DateTime.Now;
			}
		}
	}

	private void CheckWaitForPartyTask()
	{
		if (string.Equals(questionableIPC.GetCurrentTask()?.TaskName, "WaitForParty", StringComparison.Ordinal))
		{
			StartDungeonAutomation();
		}
	}

	private unsafe void UpdatePartySize()
	{
		try
		{
			int num = 0;
			GroupManager* ptr = GroupManager.Instance();
			if (ptr != null)
			{
				GroupManager.Group* ptr2 = ptr->GetGroup();
				if (ptr2 != null)
				{
					num = ptr2->MemberCount;
				}
			}
			if (num == 0)
			{
				num = 1;
			}
			if (num != CurrentPartySize)
			{
				CurrentPartySize = num;
				log.Information($"[DungeonAutomation] Party Size updated: {CurrentPartySize}/{config.AutoDutyPartySize}");
			}
		}
		catch (Exception ex)
		{
			log.Error("[DungeonAutomation] Error updating party size: " + ex.Message);
		}
	}

	private void OnConditionChanged(ConditionFlag flag, bool value)
	{
		if (flag == ConditionFlag.BoundByDuty)
		{
			if (value && !isInDuty)
			{
				isInDuty = true;
				OnDutyEntered();
			}
			else if (!value && isInDuty)
			{
				isInDuty = false;
				OnDutyExited();
			}
		}
	}

	public void OnDutyEntered()
	{
		if ((DateTime.Now - lastDutyEntryTime).TotalSeconds < 5.0)
		{
			return;
		}
		lastDutyEntryTime = DateTime.Now;
		log.Debug("[DungeonAutomation] Entered duty");
		if (!CanExecuteAutomation())
		{
			log.Debug("[DungeonAutomation] OnDutyEntered ignored - validation failed");
		}
		else if (expectingDutyEntry)
		{
			log.Information("[DungeonAutomation] Duty started by DungeonAutomation - enabling automation commands");
			expectingDutyEntry = false;
			hasStoppedAD = false;
			dutyEntryTime = DateTime.Now;
			if (!hasSentAtY)
			{
				commandManager.ProcessCommand("/at y");
				log.Information("[DungeonAutomation] Sent /at y (duty entered)");
				hasSentAtY = true;
			}
		}
		else
		{
			log.Information("[DungeonAutomation] Duty NOT started by DungeonAutomation (Solo Duty/Quest Battle) - skipping automation commands");
		}
	}

	public void OnDutyExited()
	{
		if ((DateTime.Now - lastDutyExitTime).TotalSeconds < 2.0)
		{
			log.Debug("[DungeonAutomation] OnDutyExited called too soon - ignoring spam");
			return;
		}
		lastDutyExitTime = DateTime.Now;
		log.Information("[DungeonAutomation] Exited duty");
		if (!CanExecuteAutomation() && !isAutomationActive)
		{
			log.Information("[DungeonAutomation] OnDutyExited ignored - validation failed");
			return;
		}
		if (config.IsQuester)
		{
			StartPartySafetyTimer();
		}
		if (isAutomationActive)
		{
			commandManager.ProcessCommand("/at n");
			log.Information("[DungeonAutomation] Sent /at n (duty exited)");
			hasSentAtY = false;
			log.Information("[DungeonAutomation] Waiting 8s, then disband + restart quest");
			Task.Run(async delegate
			{
				await EnsureSoloPartyAsync();
			});
			StopDungeonAutomation();
		}
		else
		{
			log.Information("[DungeonAutomation] Exited non-automated duty - no cleanup needed");
		}
	}

	private void SubscribeDutyCompleted()
	{
		dutyCompletedSubscription = RuntimeEventSubscription.Subscribe(dutyState, "DutyCompleted", OnDutyCompleted, log, "DungeonAutomation.DutyCompleted");
	}

	private void OnDutyCompleted()
	{
		log.Debug("[DungeonAutomation] Duty Completed event fired.");
		if (!isAutomationActive)
		{
			log.Debug("[DungeonAutomation] Auto Leave skipped - Dungeon Automation not active.");
			return;
		}
		Func<bool>? func = isRotationActiveChecker;
		if (func == null || !func())
		{
			log.Debug("[DungeonAutomation] Auto Leave skipped - Rotation NOT active.");
			return;
		}
		log.Information($"[DungeonAutomation] Duty Completed during Active Rotation. Initiating Auto Leave in {config.AutoLeaveDelaySeconds} seconds...");
		Task.Run(async delegate
		{
			await Task.Delay(config.AutoLeaveDelaySeconds * 1000);
			if (!(isRotationActiveChecker?.Invoke() ?? false))
			{
				log.Warning("[DungeonAutomation] Auto Leave aborted - Rotation stopped during wait.");
			}
			else
			{
				log.Information("[DungeonAutomation] Executing Auto Leave (EventFramework.LeaveCurrentContent)...");
				await framework.RunOnFrameworkThread(delegate
				{
					EventFramework.LeaveCurrentContent(forced: true);
				});
			}
		});
	}

	private async Task EnsureSoloPartyAsync()
	{
		TimeSpan timeout = TimeSpan.FromSeconds(60L);
		DateTime start = DateTime.Now;
		while (CurrentPartySize > 1 && DateTime.Now - start < timeout)
		{
			if (!CanExecuteAutomation())
			{
				return;
			}
			await framework.RunOnFrameworkThread(delegate
			{
				if (CanExecuteAutomation())
				{
					commandManager.ProcessCommand("/leave");
				}
			});
			log.Information("[DungeonAutomation] Forced /leave sent, rechecking party size...");
			await Task.Delay(1500);
			UpdatePartySize();
		}
		if (CurrentPartySize > 1)
		{
			log.Warning("[DungeonAutomation] Still not solo after leave spam!");
		}
		else
		{
			log.Information("[DungeonAutomation] Party reduced to solo after duty exit.");
		}
		if (CanExecuteAutomation())
		{
			helperManager.CheckAndExecuteRepair();
		}
	}

	public void DisbandParty()
	{
		try
		{
			if (!CanExecuteAutomation())
			{
				log.Information("[DungeonAutomation] DisbandParty ignored - validation failed");
				return;
			}
			log.Information("[DungeonAutomation] Disbanding party");
			framework.RunOnFrameworkThread(delegate
			{
				memoryHelper.SendChatMessage("/leave");
				log.Information("[DungeonAutomation] /leave command sent via UIModule");
			});
		}
		catch (Exception ex)
		{
			log.Error("[DungeonAutomation] Failed to disband party: " + ex.Message);
		}
	}

	public void Reset()
	{
		isWaitingForParty = false;
		partyInviteTime = DateTime.MinValue;
		inviteAttempts = 0;
		CurrentPartySize = 1;
		isInvitingHelpers = false;
		helperInviteTime = DateTime.MinValue;
		isAutomationActive = false;
		log.Information("[DungeonAutomation] State reset");
	}

	private unsafe void StartPartySafetyTimer()
	{
		Task.Run(async delegate
		{
			await Task.Delay(30000);
			if (CanExecuteAutomation())
			{
				if (IsInPostDungeonWindow)
				{
					log.Information("[DungeonAutomation] Safety timer: Still in post-dungeon window - skipping (new party forming)");
				}
				else
				{
					bool flag = false;
					GroupManager* ptr = GroupManager.Instance();
					if (ptr != null)
					{
						GroupManager.Group* ptr2 = ptr->GetGroup();
						if (ptr2 != null && ptr2->MemberCount > 1)
						{
							flag = true;
						}
					}
					if (flag)
					{
						log.Warning("[DungeonAutomation] SAFETY TIMER: Still in party 30s after dungeon! Force leaving...");
						await framework.RunOnFrameworkThread(delegate
						{
							commandManager.ProcessCommand("/leave");
							log.Information("[DungeonAutomation] Safety timer sent /leave");
						});
						await Task.Delay(2000);
						bool flag2 = false;
						GroupManager* ptr3 = GroupManager.Instance();
						if (ptr3 != null)
						{
							GroupManager.Group* ptr4 = ptr3->GetGroup();
							if (ptr4 != null && ptr4->MemberCount > 1)
							{
								flag2 = true;
							}
						}
						if (flag2)
						{
							log.Error("[DungeonAutomation] SAFETY TIMER: Still in party after force /leave!");
							await framework.RunOnFrameworkThread(delegate
							{
								commandManager.ProcessCommand("/leave");
							});
						}
						else
						{
							log.Information("[DungeonAutomation] ✓ SAFETY TIMER: Successfully left party");
						}
					}
				}
			}
		});
	}

	public void Dispose()
	{
		Reset();
		condition.ConditionChange -= OnConditionChanged;
		dutyCompletedSubscription?.Dispose();
	}
}
