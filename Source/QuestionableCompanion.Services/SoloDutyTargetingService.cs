using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuestionableCompanion.Services;

public class SoloDutyTargetingService : IDisposable
{
	private class TargetConfig
	{
		public uint Sequence { get; set; }

		public string TargetName { get; set; } = string.Empty;

		public int IntervalSeconds { get; set; } = 10;

		public int ActiveDurationSeconds { get; set; }

		public uint TerritoryId { get; set; }
	}

	private readonly ICondition condition;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly QuestionableIPC questionableIPC;

	private readonly ITargetManager targetManager;

	private readonly IObjectTable objectTable;

	private bool isRunningMessageLoop;

	private DateTime lastTargetTime = DateTime.MinValue;

	private string currentTargetWindowKey = string.Empty;

	private DateTime currentTargetWindowStart = DateTime.MinValue;

	private Func<bool>? isRotationActiveChecker;

	private readonly Dictionary<uint, List<TargetConfig>> configuredTargets = new Dictionary<uint, List<TargetConfig>>();

	public SoloDutyTargetingService(ICondition condition, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, QuestionableIPC questionableIPC, ITargetManager targetManager, IObjectTable objectTable)
	{
		this.condition = condition;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.questionableIPC = questionableIPC;
		this.targetManager = targetManager;
		this.objectTable = objectTable;
		InitializeConfigs();
		framework.Update += OnUpdate;
		log.Information("[SoloDutyTargeting] Service initialized");
	}

	public void SetRotationActiveChecker(Func<bool> checker)
	{
		isRotationActiveChecker = checker;
	}

	private void InitializeConfigs()
	{
		AddConfig(4521u, 3u, "Rhitahtyn sas Arvina");
		AddConfig(2239u, 2u, "Flame General Aldynn", 1, 10);
	}

	private void AddConfig(uint questId, uint sequence, string targetName, int interval = 10, int activeDurationSeconds = 0)
	{
		if (!configuredTargets.ContainsKey(questId))
		{
			configuredTargets[questId] = new List<TargetConfig>();
		}
		configuredTargets[questId].Add(new TargetConfig
		{
			Sequence = sequence,
			TargetName = targetName,
			IntervalSeconds = interval,
			ActiveDurationSeconds = activeDurationSeconds
		});
	}

	private void OnUpdate(IFramework _)
	{
		if (!clientState.IsLoggedIn)
		{
			return;
		}
		if (isRotationActiveChecker != null && !isRotationActiveChecker())
		{
			double totalSecond = (DateTime.Now - lastTargetTime).TotalSeconds;
			double num = 60.0;
			return;
		}
		uint currentQuestId = GetCurrentQuestId();
		if (currentQuestId == 0 || !configuredTargets.TryGetValue(currentQuestId, out List<TargetConfig> value))
		{
			return;
		}
		uint currentQuestSequence = GetCurrentQuestSequence(currentQuestId);
		double totalSecond2 = (DateTime.Now - lastTargetTime).TotalSeconds;
		double num2 = 30.0;
		foreach (TargetConfig item in value)
		{
			if (item.Sequence == currentQuestSequence && IsInsideTargetWindow(currentQuestId, currentQuestSequence, item))
			{
				ExecuteTargetLogic(item);
				break;
			}
		}
	}

	private bool IsInsideTargetWindow(uint questId, uint sequence, TargetConfig config)
	{
		if (config.ActiveDurationSeconds <= 0)
		{
			return true;
		}
		string b = $"{questId}:{sequence}:{config.TargetName}";
		if (!string.Equals(currentTargetWindowKey, b, StringComparison.Ordinal))
		{
			currentTargetWindowKey = b;
			currentTargetWindowStart = DateTime.Now;
			lastTargetTime = DateTime.MinValue;
		}
		return (DateTime.Now - currentTargetWindowStart).TotalSeconds <= (double)config.ActiveDurationSeconds;
	}

	private void ExecuteTargetLogic(TargetConfig config)
	{
		DateTime now = DateTime.Now;
		if (!((now - lastTargetTime).TotalSeconds >= (double)config.IntervalSeconds))
		{
			return;
		}
		try
		{
			framework.RunOnFrameworkThread(delegate
			{
				Vector3 playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
				IGameObject gameObject = (from a in objectTable
					where a.Name.ToString().Equals(config.TargetName, StringComparison.OrdinalIgnoreCase)
					where a.IsTargetable
					orderby Vector3.Distance(a.Position, playerPos)
					select a).FirstOrDefault();
				if (gameObject != null)
				{
					targetManager.Target = gameObject;
					log.Information($"[SoloDutyTargeting] Set Target to Actor: {config.TargetName} (ID: {gameObject.GameObjectId}, Dist: {Vector3.Distance(gameObject.Position, playerPos):F1})");
				}
				else
				{
					log.Warning("[SoloDutyTargeting] Actor '" + config.TargetName + "' found but none are Targetable?");
				}
			});
			lastTargetTime = now;
		}
		catch (Exception ex)
		{
			log.Error("[SoloDutyTargeting] Error executing target: " + ex.Message);
		}
	}

	private uint GetCurrentQuestId()
	{
		return GetActiveQuestIdUnsafe();
	}

	private unsafe uint GetActiveQuestIdUnsafe()
	{
		QuestManager* ptr = QuestManager.Instance();
		if (ptr == null)
		{
			return 0u;
		}
		foreach (KeyValuePair<uint, List<TargetConfig>> configuredTarget in configuredTargets)
		{
			uint key = configuredTarget.Key;
			if (ptr->IsQuestAccepted(key))
			{
				return key;
			}
		}
		return 0u;
	}

	private unsafe uint GetCurrentQuestSequence(uint questId)
	{
		if (QuestManager.Instance() == null)
		{
			return 0u;
		}
		return QuestManager.GetQuestSequence(questId);
	}

	public void Dispose()
	{
		framework.Update -= OnUpdate;
	}
}
