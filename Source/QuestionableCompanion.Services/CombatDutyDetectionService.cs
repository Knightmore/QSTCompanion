using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class CombatDutyDetectionService : IDisposable
{
	private enum MultiClientRole
	{
		None,
		Quester,
		Helper
	}

	private readonly ICondition condition;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly Configuration config;

	private readonly IObjectTable objectTable;

	private bool wasInCombat;

	private bool wasInDuty;

	private DateTime dutyExitTime = DateTime.MinValue;

	private DateTime dutyEntryTime = DateTime.MinValue;

	private DateTime lastStateChange = DateTime.MinValue;

	private bool combatCommandsActive;

	private bool rsrCommandActive;

	private bool vbmaiCommandActive;

	private bool bmraiCommandActive;

	private bool customCombatCommandsActive;

	private bool customCombatCommandsAreSoloDuty;

	private bool hasCombatCommandsForDuty;

	private bool isInAutoDutyDungeon;

	private uint currentQuestId;

	private bool isRotationActive;

	private readonly YesAlreadyIPC yesAlreadyIPC;

	public bool JustEnteredDuty { get; private set; }

	public bool JustExitedDuty { get; private set; }

	public DateTime DutyExitTime => dutyExitTime;

	public bool IsInCombat { get; private set; }

	public bool IsInDuty { get; private set; }

	public bool IsInDutyQueue { get; private set; }

	public bool ShouldPauseAutomation
	{
		get
		{
			if (!IsInCombat && !IsInDuty)
			{
				return IsInDutyQueue;
			}
			return true;
		}
	}

	public void AcknowledgeDutyEntry()
	{
		JustEnteredDuty = false;
	}

	public void AcknowledgeDutyExit()
	{
		JustExitedDuty = false;
	}

	public CombatDutyDetectionService(ICondition condition, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, Configuration config, IObjectTable objectTable, YesAlreadyIPC yesAlreadyIPC)
	{
		this.condition = condition;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.config = config;
		this.objectTable = objectTable;
		this.yesAlreadyIPC = yesAlreadyIPC;
		this.framework.Update += OnFrameworkUpdate;
		log.Information("[CombatDuty] Service initialized");
	}

	private void OnFrameworkUpdate(IFramework _)
	{
		Update();
	}

	public void SetRotationActive(bool active)
	{
		if (!active && combatCommandsActive)
		{
			DisableCombatCommands();
		}
		isRotationActive = active;
	}

	public void SetAutoDutyDungeon(bool isAutoDuty)
	{
		isInAutoDutyDungeon = isAutoDuty;
	}

	public void SetCurrentQuestId(uint questId)
	{
		currentQuestId = questId;
	}

	public void Update()
	{
		if (objectTable.LocalPlayer == null || !clientState.IsLoggedIn)
		{
			return;
		}
		if (isRotationActive)
		{
			bool flag = condition[ConditionFlag.InCombat];
			if (flag != wasInCombat)
			{
				IsInCombat = flag;
				wasInCombat = flag;
				lastStateChange = DateTime.Now;
				if (flag)
				{
					log.Information("[CombatDuty] Combat started - pausing automation");
					if (currentQuestId == 811)
					{
						log.Information("[CombatHandling] Quest 811 RSR Auto override remains active; non-RSR Solo Duty handling is still allowed");
					}
				}
				else
				{
					log.Information("[CombatDuty] Combat ended - resuming automation");
					if (combatCommandsActive && !IsInDuty)
					{
						log.Information("[CombatDuty] Not in duty - disabling combat commands");
						DisableCombatCommands();
					}
					else if (combatCommandsActive && IsInDuty)
					{
						log.Information("[CombatDuty] In duty - keeping combat commands active");
					}
				}
			}
		}
		if (isRotationActive && config.EnableCombatHandling && !IsInSoloDuty() && IsInCombat && !combatCommandsActive && currentQuestId != 811)
		{
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer != null)
			{
				float num = (float)localPlayer.CurrentHp / (float)localPlayer.MaxHp * 100f;
				if (num <= (float)config.CombatHPThreshold && CanExecuteCombatAutomation())
				{
					log.Warning($"[CombatDuty] HP at {num:F1}% (threshold: {config.CombatHPThreshold}%) - enabling combat commands");
					EnableCombatCommands();
				}
			}
		}
		bool flag2 = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];
		if (flag2 != wasInDuty)
		{
			IsInDuty = flag2;
			wasInDuty = flag2;
			lastStateChange = DateTime.Now;
			if (flag2)
			{
				log.Information("[CombatDuty] Duty started - pausing automation");
				JustEnteredDuty = true;
				JustExitedDuty = false;
				dutyEntryTime = DateTime.Now;
				hasCombatCommandsForDuty = false;
			}
			else
			{
				log.Information("[CombatDuty] Duty completed - resuming automation");
				JustEnteredDuty = false;
				JustExitedDuty = true;
				dutyExitTime = DateTime.Now;
				dutyEntryTime = DateTime.MinValue;
				hasCombatCommandsForDuty = false;
				if (combatCommandsActive)
				{
					log.Information("[CombatDuty] Duty ended - disabling combat commands");
					DisableCombatCommands();
				}
				if (yesAlreadyIPC != null)
				{
					log.Information("[CombatDuty] Duty exited - Enforcing YesAlready Enable");
					yesAlreadyIPC.EnablePlugin();
				}
			}
		}
		if (isRotationActive && IsInDuty && !isInAutoDutyDungeon && !hasCombatCommandsForDuty && dutyEntryTime != DateTime.MinValue)
		{
			if (currentQuestId == 4591)
			{
				log.Information("[CombatDuty] Quest 4591 (Steps of Faith) - skipping combat commands (handler does it)");
				hasCombatCommandsForDuty = true;
				return;
			}
			if ((DateTime.Now - dutyEntryTime).TotalSeconds >= 8.0)
			{
				if (CanExecuteCombatAutomation())
				{
					log.Information("[CombatDuty] 8 seconds in Solo Duty - enabling combat commands");
					EnableCombatCommands();
				}
				else
				{
					log.Debug("[CombatDuty] Solo Duty combat automation is not applicable to role 'None'");
				}
				hasCombatCommandsForDuty = true;
			}
		}
		bool flag3 = condition[ConditionFlag.WaitingForDuty] || condition[ConditionFlag.WaitingForDutyFinder];
		if (flag3 != IsInDutyQueue)
		{
			IsInDutyQueue = flag3;
			lastStateChange = DateTime.Now;
			if (flag3)
			{
				log.Information("[CombatDuty] Duty queue active - pausing automation");
			}
			else
			{
				log.Information("[CombatDuty] Duty queue ended - resuming automation");
			}
		}
	}

	public TimeSpan TimeSinceLastStateChange()
	{
		if (lastStateChange == DateTime.MinValue)
		{
			return TimeSpan.Zero;
		}
		return DateTime.Now - lastStateChange;
	}

	private void EnableCombatCommands()
	{
		if (combatCommandsActive)
		{
			return;
		}
		if (!CanExecuteCombatAutomation())
		{
			switch (GetCurrentMultiClientRole())
			{
			case MultiClientRole.None:
				log.Debug("[CombatDuty] Combat blocked: Role is 'None' (Config not Helper/Quester)");
				break;
			case MultiClientRole.Quester:
				log.Debug("[CombatDuty] Combat blocked: Quester outside Solo Duty (Let D.Automation handle invalid content)");
				break;
			}
			return;
		}
		try
		{
			log.Information("[CombatDuty] ========================================");
			log.Information("[CombatDuty] === ENABLING COMBAT AUTOMATION ===");
			log.Information("[CombatDuty] ========================================");
			bool flag = IsInSoloDuty();
			if (flag ? (config.SoloDutyCombatHandlingMode == CombatHandlingMode.CustomCommands) : (config.StopPointCombatHandlingMode == CombatHandlingMode.CustomCommands))
			{
				string value = (flag ? "Solo duty" : "Standard Stop Point rotation");
				string[] array = ParseCommands(flag ? config.SoloDutyCombatStartCommands : config.StopPointCombatStartCommands);
				log.Information($"[CombatHandling] {value} executing {array.Length} custom combat-start command(s)");
				ExecuteCommands(array, "custom combat-start");
				customCombatCommandsActive = true;
				customCombatCommandsAreSoloDuty = flag;
				combatCommandsActive = true;
				return;
			}
			bool flag2 = (flag ? config.EnableSoloDutyRSR : config.EnableStopPointRSR);
			bool flag3 = (flag ? config.EnableSoloDutyVBM : config.EnableStopPointVBM);
			bool flag4 = (flag ? config.EnableSoloDutyBMRAI : config.EnableStopPointBMRAI);
			if (flag && currentQuestId == 811 && flag2)
			{
				flag2 = false;
				log.Information("[CombatHandling] Applied quest-specific RSR Auto override; other configured Solo Duty handling remains enabled");
			}
			if (!flag && !flag2 && !flag3 && !flag4)
			{
				flag2 = true;
				log.Warning("[CombatHandling] Standard Stop Point rotation had no backend selected; falling back to RSR");
			}
			if (!flag2 && !flag3 && !flag4)
			{
				log.Information("[CombatDuty] Solo Duty combat automation skipped - no commands enabled in config");
				hasCombatCommandsForDuty = true;
				return;
			}
			string text = (flag ? "Solo duty" : "Standard Stop Point rotation");
			string text2 = string.Join(", ", new string[3]
			{
				flag2 ? "RSR" : null,
				flag3 ? "VBM" : null,
				flag4 ? "BMR" : null
			}.Where((string x) => x != null));
			log.Information("[CombatHandling] " + text + " using default backend(s): " + text2);
			if (flag2)
			{
				framework.RunOnTick(delegate
				{
					try
					{
						string text3 = (IsInDuty ? "auto" : "manual");
						commandManager.ProcessCommand("/rsr " + text3);
						rsrCommandActive = true;
						log.Information("[CombatDuty] /rsr " + text3 + " sent");
					}
					catch (Exception ex2)
					{
						log.Error("[CombatDuty] Failed to send /rsr command: " + ex2.Message);
					}
				}, TimeSpan.Zero);
			}
			if (flag3)
			{
				framework.RunOnTick(delegate
				{
					try
					{
						commandManager.ProcessCommand("/vbmai on");
						vbmaiCommandActive = true;
						log.Information("[CombatDuty] /vbmai on sent");
					}
					catch (Exception ex2)
					{
						log.Error("[CombatDuty] Failed to send /vbmai on: " + ex2.Message);
					}
				}, TimeSpan.FromMilliseconds(100L));
			}
			if (flag4)
			{
				framework.RunOnTick(delegate
				{
					try
					{
						commandManager.ProcessCommand("/bmrai on");
						bmraiCommandActive = true;
						log.Information("[CombatDuty] /bmrai on sent");
					}
					catch (Exception ex2)
					{
						log.Error("[CombatDuty] Failed to send /bmrai on: " + ex2.Message);
					}
				}, TimeSpan.FromMilliseconds(200L));
			}
			combatCommandsActive = true;
			log.Information("[CombatDuty] Combat automation enabled");
		}
		catch (Exception ex)
		{
			log.Error("[CombatDuty] Error enabling combat commands: " + ex.Message);
		}
	}

	private void DisableCombatCommands()
	{
		if (!combatCommandsActive)
		{
			return;
		}
		try
		{
			log.Information("[CombatDuty] ========================================");
			if (customCombatCommandsActive)
			{
				string value = (customCombatCommandsAreSoloDuty ? "Solo duty" : "Standard Stop Point rotation");
				string[] array = ParseCommands(customCombatCommandsAreSoloDuty ? config.SoloDutyCombatEndCommands : config.StopPointCombatEndCommands);
				log.Information($"[CombatHandling] {value} executing {array.Length} custom post-combat command(s)");
				ExecuteCommands(array, "custom post-combat");
			}
			log.Information("[CombatDuty] === DISABLING COMBAT AUTOMATION ===");
			log.Information("[CombatDuty] ========================================");
			if (rsrCommandActive)
			{
				framework.RunOnTick(delegate
				{
					try
					{
						commandManager.ProcessCommand("/rsr off");
						log.Information("[CombatDuty] /rsr off sent");
					}
					catch (Exception ex2)
					{
						log.Error("[CombatDuty] Failed to send /rsr off: " + ex2.Message);
					}
				}, TimeSpan.Zero);
			}
			if (vbmaiCommandActive)
			{
				framework.RunOnTick(delegate
				{
					try
					{
						commandManager.ProcessCommand("/vbmai off");
						log.Information("[CombatDuty] /vbmai off sent");
					}
					catch (Exception ex2)
					{
						log.Error("[CombatDuty] Failed to send /vbmai off: " + ex2.Message);
					}
				}, TimeSpan.FromMilliseconds(100L));
			}
			if (bmraiCommandActive)
			{
				framework.RunOnTick(delegate
				{
					try
					{
						commandManager.ProcessCommand("/bmrai off");
						log.Information("[CombatDuty] /bmrai off sent");
					}
					catch (Exception ex2)
					{
						log.Error("[CombatDuty] Failed to send /bmrai off: " + ex2.Message);
					}
				}, TimeSpan.FromMilliseconds(200L));
			}
			combatCommandsActive = false;
			rsrCommandActive = false;
			vbmaiCommandActive = false;
			bmraiCommandActive = false;
			customCombatCommandsActive = false;
			customCombatCommandsAreSoloDuty = false;
			log.Information("[CombatDuty] Combat automation disabled");
		}
		catch (Exception ex)
		{
			log.Error("[CombatDuty] Error disabling combat commands: " + ex.Message);
		}
	}

	private static string[] ParseCommands(string commands)
	{
		return (from command in (commands ?? string.Empty).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			select command.Trim() into command
			where command.Length > 0
			select command).ToArray();
	}

	private void ExecuteCommands(string[] commands, string description)
	{
		for (int i = 0; i < commands.Length; i++)
		{
			string command = commands[i];
			framework.RunOnTick(delegate
			{
				try
				{
					commandManager.ProcessCommand(command);
				}
				catch (Exception ex)
				{
					log.Error("[CombatHandling] Failed to execute " + description + " command: " + ex.Message);
				}
			}, TimeSpan.FromMilliseconds(i * 100));
		}
	}

	public void ClearDutyExitFlag()
	{
		JustExitedDuty = false;
	}

	public void Reset()
	{
		IsInCombat = false;
		IsInDuty = false;
		IsInDutyQueue = false;
		wasInCombat = false;
		wasInDuty = false;
		combatCommandsActive = false;
		rsrCommandActive = false;
		vbmaiCommandActive = false;
		bmraiCommandActive = false;
		customCombatCommandsActive = false;
		customCombatCommandsAreSoloDuty = false;
		hasCombatCommandsForDuty = false;
		JustEnteredDuty = false;
		JustExitedDuty = false;
		dutyExitTime = DateTime.MinValue;
		dutyEntryTime = DateTime.MinValue;
		log.Information("[CombatDuty] State reset");
	}

	private MultiClientRole GetCurrentMultiClientRole()
	{
		if (config.IsHelperAutomationActive)
		{
			return MultiClientRole.Helper;
		}
		if (config.IsQuester)
		{
			return MultiClientRole.Quester;
		}
		return MultiClientRole.None;
	}

	private bool IsInSoloDuty()
	{
		if (condition[ConditionFlag.BoundByDuty95])
		{
			return true;
		}
		if (IsInDuty && !isInAutoDutyDungeon)
		{
			return true;
		}
		return false;
	}

	private bool CanExecuteCombatAutomation()
	{
		switch (GetCurrentMultiClientRole())
		{
		case MultiClientRole.None:
			return !IsInDuty;
		case MultiClientRole.Helper:
			return true;
		case MultiClientRole.Quester:
			if (!IsInDuty)
			{
				return true;
			}
			if (IsInSoloDuty())
			{
				return true;
			}
			if (currentQuestId == 811)
			{
				return true;
			}
			if (currentQuestId == 4591)
			{
				return true;
			}
			return false;
		default:
			return false;
		}
	}

	public void Dispose()
	{
		framework.Update -= OnFrameworkUpdate;
		if (combatCommandsActive)
		{
			DisableCombatCommands();
		}
	}
}
