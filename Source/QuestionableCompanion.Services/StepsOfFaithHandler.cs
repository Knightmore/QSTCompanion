using System;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class StepsOfFaithHandler : IDisposable
{
	private readonly ICondition condition;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly Configuration config;

	private bool isActive;

	private bool handledCurrentInstance;

	private const uint StepsOfFaithQuestId = 4591u;

	public bool IsActive => isActive;

	public bool IsStepsOfFaithQuest(uint questId)
	{
		return questId == 4591;
	}

	public StepsOfFaithHandler(ICondition condition, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, Configuration config)
	{
		this.condition = condition;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.config = config;
		log.Information("[StepsOfFaith] Handler initialized");
	}

	public bool ShouldActivate(uint questId, bool isInSoloDuty)
	{
		if (isActive)
		{
			return false;
		}
		if (questId != 4591)
		{
			return false;
		}
		if (!isInSoloDuty)
		{
			return false;
		}
		if (handledCurrentInstance)
		{
			return false;
		}
		string currentCharacterName = GetCurrentCharacterName();
		if (string.IsNullOrEmpty(currentCharacterName))
		{
			return false;
		}
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer != null && Vector3.Distance(value2: new Vector3(2.88f, 0f, 293.36f), value1: localPlayer.Position) < 10f)
		{
			return false;
		}
		log.Information("[StepsOfFaith] Handler will activate for " + currentCharacterName);
		return true;
	}

	public void Execute(string characterName)
	{
		isActive = true;
		handledCurrentInstance = true;
		if (!string.IsNullOrEmpty(characterName))
		{
			log.Information("[StepsOfFaith] Activating for " + characterName);
		}
		log.Information("[StepsOfFaith] ========================================");
		log.Information("[StepsOfFaith] === STEPS OF FAITH HANDLER ACTIVATED ===");
		log.Information("[StepsOfFaith] ========================================");
		try
		{
			log.Information("[StepsOfFaith] Waiting for conditions to clear...");
			DateTime now = DateTime.Now;
			TimeSpan timeSpan = TimeSpan.FromSeconds(6000L);
			while (DateTime.Now - now < timeSpan)
			{
				bool flag = condition[ConditionFlag.Occupied];
				bool flag2 = condition[ConditionFlag.SufferingStatusAffliction63];
				if (!flag && !flag2)
				{
					log.Information("[StepsOfFaith] Conditions cleared!");
					break;
				}
				if ((DateTime.Now - now).TotalSeconds % 5.0 < 0.1)
				{
					log.Information($"[StepsOfFaith] Waiting... (29: {flag}, 63: {flag2})");
				}
				Thread.Sleep(200);
			}
			log.Information("[StepsOfFaith] Waiting 25s for stabilization...");
			Thread.Sleep(25000);
			log.Information("[StepsOfFaith] Moving to target position...");
			framework.RunOnFrameworkThread(delegate
			{
				commandManager.ProcessCommand("/vnav moveto 2.8788917064667 0.0 293.36273193359");
			});
			log.Information("[StepsOfFaith] Enabling combat commands...");
			framework.RunOnFrameworkThread(delegate
			{
				commandManager.ProcessCommand("/rsr auto");
				Thread.Sleep(100);
				commandManager.ProcessCommand("/vbmai on");
				Thread.Sleep(100);
				commandManager.ProcessCommand("/bmrai on");
			});
			log.Information("[StepsOfFaith] === HANDLER COMPLETE ===");
		}
		catch (Exception ex)
		{
			log.Error("[StepsOfFaith] Error: " + ex.Message);
		}
		finally
		{
			isActive = false;
		}
	}

	public void Reset()
	{
		isActive = false;
		log.Information("[StepsOfFaith] Active state reset");
	}

	public void PrepareForNewDuty()
	{
		handledCurrentInstance = false;
		isActive = false;
		log.Information("[StepsOfFaith] Prepared for new duty instance (Reset handled flag)");
	}

	private string GetCurrentCharacterName()
	{
		try
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer != null)
			{
				return $"{localPlayer.Name}@{localPlayer.HomeWorld.Value.Name}";
			}
		}
		catch (Exception ex)
		{
			log.Error("[StepsOfFaith] Failed to get character name: " + ex.Message);
		}
		return string.Empty;
	}

	public void Dispose()
	{
	}
}
