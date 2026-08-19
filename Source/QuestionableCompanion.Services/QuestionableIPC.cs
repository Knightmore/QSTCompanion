using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class QuestionableIPC : IDisposable
{
	public enum EStopConditionMode
	{
		Off,
		Pause,
		Stop
	}

	private const string RequiredQuestionableMessage = "WigglyMuffin's version of Questionable is required.";

	private const string WigglyRepositoryName = "DalamudPlugins";

	private const string WigglyPluginMasterFile = "pluginmaster.json";

	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private readonly QuestionableReflection questionableReflection;

	private ICallGateSubscriber<string, bool>? importQuestPrioritySubscriber;

	private ICallGateSubscriber<string>? exportQuestPrioritySubscriber;

	private ICallGateSubscriber<List<string>>? getPriorityQuestsSubscriber;

	private ICallGateSubscriber<string, bool>? isQuestInPrioritySubscriber;

	private ICallGateSubscriber<string?>? getCurrentQuestIdSubscriber;

	private ICallGateSubscriber<StepData?>? getCurrentStepDataSubscriber;

	private ICallGateSubscriber<bool>? isRunningSubscriber;

	private ICallGateSubscriber<TaskData?>? getCurrentTaskSubscriber;

	private ICallGateSubscriber<string, bool>? startQuestSubscriber;

	private ICallGateSubscriber<string, bool>? startSingleQuestSubscriber;

	private ICallGateSubscriber<string, bool>? isQuestCompleteSubscriber;

	private ICallGateSubscriber<string, bool>? isReadyToAcceptQuestSubscriber;

	private ICallGateSubscriber<string, bool>? isQuestAcceptedSubscriber;

	private ICallGateSubscriber<string, bool>? addQuestPrioritySubscriber;

	private ICallGateSubscriber<int, string, bool>? insertQuestPrioritySubscriber;

	private ICallGateSubscriber<string, bool>? removePriorityQuestSubscriber;

	private ICallGateSubscriber<string, bool>? isQuestLockedSubscriber;

	private ICallGateSubscriber<string, bool>? isQuestUnobtainableSubscriber;

	private ICallGateSubscriber<bool>? clearQuestPrioritySubscriber;

	private ICallGateSubscriber<List<string>>? getCurrentlyActiveEventQuestsSubscriber;

	private ICallGateSubscriber<int>? getAlliedSocietyRemainingAllowancesSubscriber;

	private ICallGateSubscriber<byte, List<string>>? getAlliedSocietyAvailableQuestIdsSubscriber;

	private ICallGateSubscriber<Dictionary<byte, int>>? getAlliedSocietyAllAvailableQuestCountsSubscriber;

	private ICallGateSubscriber<byte, bool>? getAlliedSocietyIsMaxRankSubscriber;

	private ICallGateSubscriber<byte, int>? getAlliedSocietyCurrentRankSubscriber;

	private ICallGateSubscriber<List<byte>>? getAlliedSocietiesWithAvailableQuestsSubscriber;

	private ICallGateSubscriber<byte, int>? addAlliedSocietyOptimalQuestsSubscriber;

	private ICallGateSubscriber<byte, List<string>>? getAlliedSocietyOptimalQuestsSubscriber;

	private ICallGateSubscriber<long>? getAlliedSocietyTimeUntilResetSubscriber;

	private ICallGateSubscriber<bool>? getStopConditionsEnabledSubscriber;

	private ICallGateSubscriber<bool, bool>? setStopConditionsEnabledSubscriber;

	private ICallGateSubscriber<List<string>>? getStopQuestListSubscriber;

	private ICallGateSubscriber<string, bool>? addStopQuestSubscriber;

	private ICallGateSubscriber<StopConditionData>? getLevelStopConditionSubscriber;

	private ICallGateSubscriber<StopConditionData>? getSequenceStopConditionSubscriber;

	private ICallGateSubscriber<string, int>? getQuestSequenceStopConditionSubscriber;

	private ICallGateSubscriber<string, int, bool>? setQuestSequenceStopConditionSubscriber;

	private ICallGateSubscriber<Dictionary<string, int>>? getAllQuestSequenceStopConditionsSubscriber;

	private ICallGateSubscriber<int>? getLevelStopModeSubscriber;

	private ICallGateSubscriber<int>? getSequenceStopModeSubscriber;

	private ICallGateSubscriber<string, int>? getQuestStopModeSubscriber;

	private ICallGateSubscriber<string, int, bool>? setQuestStopModeSubscriber;

	private ICallGateSubscriber<int>? getDefaultDutyModeSubscriber;

	private ICallGateSubscriber<int, bool>? setDefaultDutyModeSubscriber;

	private ICallGateSubscriber<bool>? isLevelingModeEnabledSubscriber;

	private ICallGateSubscriber<bool, bool>? setLevelingModeEnabledSubscriber;

	private ICallGateSubscriber<MsqLevelLockData?>? getMsqLevelLockInfoSubscriber;

	private ICallGateSubscriber<bool>? startLevelingModeSubscriber;

	private ICallGateSubscriber<bool>? stopLevelingModeSubscriber;

	private bool subscribersInitialized;

	private DateTime lastAvailabilityCheck = DateTime.MinValue;

	private const int AvailabilityCheckCooldownSeconds = 5;

	public bool IsAvailable { get; private set; }

	public bool IsWigglyRepository { get; private set; }

	public bool IsWigglyManifest { get; private set; }

	public bool IsTrustedWigglyBuild
	{
		get
		{
			if (!IsWigglyRepository)
			{
				return IsWigglyManifest;
			}
			return true;
		}
	}

	public string? DetectedSourceRepository { get; private set; }

	public string? DetectedManifestAuthor { get; private set; }

	public string CompatibilityMessage { get; private set; } = "WigglyMuffin's version of Questionable is required.";

	public QuestionableIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		questionableReflection = new QuestionableReflection(pluginInterface, log);
		pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
		InitializeIPC();
	}

	private void InitializeIPC()
	{
		try
		{
			getCurrentQuestIdSubscriber = pluginInterface.GetIpcSubscriber<string>("Questionable.GetCurrentQuestId");
			getCurrentStepDataSubscriber = pluginInterface.GetIpcSubscriber<StepData>("Questionable.GetCurrentStepData");
			isRunningSubscriber = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
			importQuestPrioritySubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.ImportQuestPriority");
			exportQuestPrioritySubscriber = pluginInterface.GetIpcSubscriber<string>("Questionable.ExportQuestPriority");
			getCurrentTaskSubscriber = pluginInterface.GetIpcSubscriber<TaskData>("Questionable.GetCurrentTask");
			startQuestSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartQuest");
			startSingleQuestSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartSingleQuest");
			isQuestCompleteSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestComplete");
			isReadyToAcceptQuestSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsReadyToAcceptQuest");
			isQuestAcceptedSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestAccepted");
			getPriorityQuestsSubscriber = pluginInterface.GetIpcSubscriber<List<string>>("Questionable.GetPriorityQuests");
			isQuestInPrioritySubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestInPriority");
			addQuestPrioritySubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.AddQuestPriority");
			insertQuestPrioritySubscriber = pluginInterface.GetIpcSubscriber<int, string, bool>("Questionable.InsertQuestPriority");
			removePriorityQuestSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.RemovePriorityQuest");
			isQuestLockedSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestLocked");
			isQuestUnobtainableSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestUnobtainable");
			clearQuestPrioritySubscriber = pluginInterface.GetIpcSubscriber<bool>("Questionable.ClearQuestPriority");
			getCurrentlyActiveEventQuestsSubscriber = pluginInterface.GetIpcSubscriber<List<string>>("Questionable.GetCurrentlyActiveEventQuests");
			getAlliedSocietyRemainingAllowancesSubscriber = pluginInterface.GetIpcSubscriber<int>("Questionable.AlliedSociety.GetRemainingAllowances");
			getAlliedSocietyAvailableQuestIdsSubscriber = pluginInterface.GetIpcSubscriber<byte, List<string>>("Questionable.AlliedSociety.GetAvailableQuestIds");
			getAlliedSocietyAllAvailableQuestCountsSubscriber = pluginInterface.GetIpcSubscriber<Dictionary<byte, int>>("Questionable.AlliedSociety.GetAllAvailableQuestCounts");
			getAlliedSocietyIsMaxRankSubscriber = pluginInterface.GetIpcSubscriber<byte, bool>("Questionable.AlliedSociety.IsMaxRank");
			getAlliedSocietyCurrentRankSubscriber = pluginInterface.GetIpcSubscriber<byte, int>("Questionable.AlliedSociety.GetCurrentRank");
			getAlliedSocietiesWithAvailableQuestsSubscriber = pluginInterface.GetIpcSubscriber<List<byte>>("Questionable.AlliedSociety.GetSocietiesWithAvailableQuests");
			addAlliedSocietyOptimalQuestsSubscriber = pluginInterface.GetIpcSubscriber<byte, int>("Questionable.AlliedSociety.AddOptimalQuests");
			getAlliedSocietyOptimalQuestsSubscriber = pluginInterface.GetIpcSubscriber<byte, List<string>>("Questionable.AlliedSociety.GetOptimalQuests");
			getAlliedSocietyTimeUntilResetSubscriber = pluginInterface.GetIpcSubscriber<long>("Questionable.AlliedSociety.GetTimeUntilReset");
			getStopConditionsEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("Questionable.GetStopConditionsEnabled");
			setStopConditionsEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool, bool>("Questionable.SetStopConditionsEnabled");
			getStopQuestListSubscriber = pluginInterface.GetIpcSubscriber<List<string>>("Questionable.GetStopQuestList");
			addStopQuestSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.AddStopQuest");
			getLevelStopConditionSubscriber = pluginInterface.GetIpcSubscriber<StopConditionData>("Questionable.GetLevelStopCondition");
			getSequenceStopConditionSubscriber = pluginInterface.GetIpcSubscriber<StopConditionData>("Questionable.GetSequenceStopCondition");
			getQuestSequenceStopConditionSubscriber = pluginInterface.GetIpcSubscriber<string, int>("Questionable.GetQuestSequenceStopCondition");
			setQuestSequenceStopConditionSubscriber = pluginInterface.GetIpcSubscriber<string, int, bool>("Questionable.SetQuestSequenceStopCondition");
			getAllQuestSequenceStopConditionsSubscriber = pluginInterface.GetIpcSubscriber<Dictionary<string, int>>("Questionable.GetAllQuestSequenceStopConditions");
			getLevelStopModeSubscriber = pluginInterface.GetIpcSubscriber<int>("Questionable.GetLevelStopMode");
			getSequenceStopModeSubscriber = pluginInterface.GetIpcSubscriber<int>("Questionable.GetSequenceStopMode");
			getQuestStopModeSubscriber = pluginInterface.GetIpcSubscriber<string, int>("Questionable.GetQuestStopMode");
			setQuestStopModeSubscriber = pluginInterface.GetIpcSubscriber<string, int, bool>("Questionable.SetQuestStopMode");
			getDefaultDutyModeSubscriber = pluginInterface.GetIpcSubscriber<int>("Questionable.GetDefaultDutyMode");
			setDefaultDutyModeSubscriber = pluginInterface.GetIpcSubscriber<int, bool>("Questionable.SetDefaultDutyMode");
			isLevelingModeEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsLevelingModeEnabled");
			setLevelingModeEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool, bool>("Questionable.SetLevelingModeEnabled");
			getMsqLevelLockInfoSubscriber = pluginInterface.GetIpcSubscriber<MsqLevelLockData>("Questionable.GetMsqLevelLockInfo");
			startLevelingModeSubscriber = pluginInterface.GetIpcSubscriber<bool>("Questionable.StartLevelingMode");
			stopLevelingModeSubscriber = pluginInterface.GetIpcSubscriber<bool>("Questionable.StopLevelingMode");
			subscribersInitialized = true;
			log.Debug("[QuestionableIPC] IPC subscribers initialized (lazy-loading enabled)");
		}
		catch (Exception ex)
		{
			IsAvailable = false;
			subscribersInitialized = false;
			log.Error("[QuestionableIPC] Failed to initialize subscribers: " + ex.Message);
		}
	}

	private bool TryEnsureAvailable()
	{
		if (IsAvailable)
		{
			return true;
		}
		if (!subscribersInitialized)
		{
			InitializeIPC();
			if (!subscribersInitialized)
			{
				log.Warning("[QuestionableIPC] Subscribers not initialized yet");
				return false;
			}
		}
		DateTime now = DateTime.Now;
		if ((now - lastAvailabilityCheck).TotalSeconds < 5.0)
		{
			return false;
		}
		lastAvailabilityCheck = now;
		try
		{
			return ValidateQuestionableInstallation(logFailure: false);
		}
		catch (Exception ex)
		{
			log.Debug("[QuestionableIPC] Questionable not yet available: " + ex.GetType().Name);
			IsAvailable = false;
			return false;
		}
	}

	public bool ForceCheckAvailability()
	{
		try
		{
			if (!subscribersInitialized)
			{
				InitializeIPC();
				if (!subscribersInitialized)
				{
					log.Error("[QuestionableIPC] Subscribers not initialized yet");
					return false;
				}
			}
			log.Information("[QuestionableIPC] Force checking Questionable availability...");
			lastAvailabilityCheck = DateTime.Now;
			return ValidateQuestionableInstallation(logFailure: true);
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] Failed to connect to Questionable:");
			log.Error("[QuestionableIPC]   Exception Type: " + ex.GetType().Name);
			log.Error("[QuestionableIPC]   Message: " + ex.Message);
			log.Error("[QuestionableIPC]   Stack Trace: " + ex.StackTrace);
			IsAvailable = false;
			return false;
		}
	}

	public bool TryEnsureAvailableSilent()
	{
		if (IsAvailable)
		{
			return true;
		}
		if (!subscribersInitialized)
		{
			InitializeIPC();
			if (!subscribersInitialized)
			{
				return false;
			}
		}
		lastAvailabilityCheck = DateTime.MinValue;
		try
		{
			return ValidateQuestionableInstallation(logFailure: false);
		}
		catch
		{
			IsAvailable = false;
			return false;
		}
	}

	private bool ValidateQuestionableInstallation(bool logFailure)
	{
		try
		{
			DetectedSourceRepository = null;
			DetectedManifestAuthor = null;
			IsWigglyRepository = false;
			IsWigglyManifest = false;
			string sourceRepository;
			string failureReason;
			bool num = questionableReflection.TryGetSourceRepository(out sourceRepository, out failureReason);
			string manifestAuthor;
			string failureReason2;
			bool flag = questionableReflection.TryGetManifestAuthor(out manifestAuthor, out failureReason2);
			if (num)
			{
				DetectedSourceRepository = sourceRepository;
				IsWigglyRepository = IsWigglyPluginMasterRepository(sourceRepository);
			}
			if (flag)
			{
				DetectedManifestAuthor = manifestAuthor;
				IsWigglyManifest = IsWigglyManifestAuthor(manifestAuthor);
			}
			if (!IsTrustedWigglyBuild)
			{
				List<string> list = new List<string>();
				if (!string.IsNullOrWhiteSpace(sourceRepository))
				{
					list.Add("Detected source: " + sourceRepository);
				}
				if (!string.IsNullOrWhiteSpace(manifestAuthor))
				{
					list.Add("Detected author: " + manifestAuthor);
				}
				if (list.Count == 0 && !string.IsNullOrWhiteSpace(failureReason))
				{
					list.Add(failureReason);
				}
				if (list.Count == 0 && !string.IsNullOrWhiteSpace(failureReason2))
				{
					list.Add(failureReason2);
				}
				CompatibilityMessage = ((list.Count == 0) ? "WigglyMuffin's version of Questionable is required." : ("WigglyMuffin's version of Questionable is required. " + string.Join(" ", list)));
				IsAvailable = false;
				if (logFailure)
				{
					log.Error("[QuestionableIPC] " + CompatibilityMessage);
				}
				return false;
			}
			if (isRunningSubscriber == null)
			{
				CompatibilityMessage = "WigglyMuffin's Questionable was detected, but its IPC endpoints are not ready yet.";
				IsAvailable = false;
				return false;
			}
			isRunningSubscriber.InvokeFunc();
			CompatibilityMessage = string.Empty;
			IsAvailable = true;
			return true;
		}
		catch (Exception ex)
		{
			CompatibilityMessage = (IsTrustedWigglyBuild ? "WigglyMuffin's Questionable was detected, but its IPC endpoints are not ready yet." : "WigglyMuffin's version of Questionable is required.");
			IsAvailable = false;
			if (logFailure)
			{
				log.Error($"[QuestionableIPC] {CompatibilityMessage} ({ex.GetType().Name}: {ex.Message})");
			}
			return false;
		}
	}

	private static bool IsWigglyPluginMasterRepository(string sourceRepository)
	{
		if (!Uri.TryCreate(sourceRepository, UriKind.Absolute, out Uri result) || !string.Equals(result.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] array = result.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (string.Equals(result.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
		{
			if (array.Length == 4 && IsWigglyRepositoryOwner(array[0]) && string.Equals(array[1], "DalamudPlugins", StringComparison.OrdinalIgnoreCase) && string.Equals(array[2], "main", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(array[3], "pluginmaster.json", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		if (string.Equals(result.Host, "github.com", StringComparison.OrdinalIgnoreCase))
		{
			if (array.Length == 5 && IsWigglyRepositoryOwner(array[0]) && string.Equals(array[1], "DalamudPlugins", StringComparison.OrdinalIgnoreCase) && string.Equals(array[2], "raw", StringComparison.OrdinalIgnoreCase) && string.Equals(array[3], "main", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(array[4], "pluginmaster.json", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		return false;
	}

	private static bool IsWigglyRepositoryOwner(string owner)
	{
		if (!string.Equals(owner, "WigglyMuffin", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(owner, "WigglyCorp", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsWigglyManifestAuthor(string manifestAuthor)
	{
		string[] source = manifestAuthor.Split(new char[3] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (source.Any((string author) => string.Equals(author, "WigglyMuffin", StringComparison.OrdinalIgnoreCase)))
		{
			return source.Any((string author) => string.Equals(author, "CryoTechnic", StringComparison.OrdinalIgnoreCase));
		}
		return false;
	}

	private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
	{
		if (args.AffectedInternalNames.Any((string name) => string.Equals(name, "Questionable", StringComparison.Ordinal)))
		{
			IsAvailable = false;
			IsWigglyRepository = false;
			IsWigglyManifest = false;
			DetectedSourceRepository = null;
			DetectedManifestAuthor = null;
			CompatibilityMessage = "WigglyMuffin's version of Questionable is required.";
			lastAvailabilityCheck = DateTime.MinValue;
			log.Debug("[QuestionableIPC] Questionable plugin state changed; repository and IPC availability will be checked again");
		}
	}

	public string? GetCurrentQuestId()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getCurrentQuestIdSubscriber == null)
		{
			return null;
		}
		try
		{
			return getCurrentQuestIdSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Debug("[QuestionableIPC] GetCurrentQuestId failed: " + ex.Message);
			return null;
		}
	}

	public StepData? GetCurrentStepData()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getCurrentStepDataSubscriber == null)
		{
			return null;
		}
		try
		{
			return getCurrentStepDataSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Debug("[QuestionableIPC] GetCurrentStepData failed: " + ex.Message);
			return null;
		}
	}

	public bool SetQuestStopMode(string questId, EStopConditionMode mode)
	{
		TryEnsureAvailable();
		if (!IsAvailable || setQuestStopModeSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = setQuestStopModeSubscriber.InvokeFunc(questId, (int)mode);
			log.Debug($"[QuestionableIPC] SetQuestStopMode({questId}, {mode}) -> {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error($"[QuestionableIPC] SetQuestStopMode({questId}, {mode}) failed: {ex.Message}");
			return false;
		}
	}

	public byte? GetCurrentSequence()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getCurrentStepDataSubscriber == null)
		{
			return null;
		}
		try
		{
			return getCurrentStepDataSubscriber.InvokeFunc()?.Sequence;
		}
		catch (Exception ex)
		{
			log.Debug("[QuestionableIPC] GetCurrentSequence failed: " + ex.Message);
			return null;
		}
	}

	public bool IsRunning()
	{
		TryEnsureAvailable();
		if (!IsAvailable || isRunningSubscriber == null)
		{
			return false;
		}
		try
		{
			return isRunningSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			if (IsAvailable)
			{
				IsAvailable = false;
				log.Warning("[QuestionableIPC] Questionable is no longer available: " + ex.Message);
			}
			log.Debug("[QuestionableIPC] IsRunning failed: " + ex.Message);
			return false;
		}
	}

	public TaskData? GetCurrentTask()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getCurrentTaskSubscriber == null)
		{
			return null;
		}
		try
		{
			return getCurrentTaskSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			if (IsAvailable)
			{
				IsAvailable = false;
				log.Warning("[QuestionableIPC] Questionable is no longer available: " + ex.Message);
			}
			log.Debug("[QuestionableIPC] GetCurrentTask failed: " + ex.Message);
			return null;
		}
	}

	public bool Start()
	{
		log.Warning("[QuestionableIPC] Start() called - NOT AVAILABLE VIA IPC!");
		log.Warning("[QuestionableIPC] Use /qst start command instead");
		return false;
	}

	public bool StartQuest(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || startQuestSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = startQuestSubscriber.InvokeFunc(questId);
			log.Information($"[QuestionableIPC] StartQuest({questId}) -> {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Debug("[QuestionableIPC] StartQuest(" + questId + ") failed: " + ex.Message);
			return false;
		}
	}

	internal bool StartSingleQuest(string canonicalQuestId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || startSingleQuestSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = startSingleQuestSubscriber.InvokeFunc(canonicalQuestId);
			log.Information($"[QuestionableIPC] StartSingleQuest({canonicalQuestId}) -> {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Debug("[QuestionableIPC] StartSingleQuest(" + canonicalQuestId + ") failed: " + ex.Message);
			return false;
		}
	}

	public bool Stop()
	{
		log.Warning("[QuestionableIPC] Stop() called - NOT AVAILABLE VIA IPC!");
		log.Warning("[QuestionableIPC] Use /qst stop command instead");
		return false;
	}

	public bool ImportQuestPriority(string base64QuestData)
	{
		TryEnsureAvailable();
		if (!IsAvailable || importQuestPrioritySubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = importQuestPrioritySubscriber.InvokeFunc(base64QuestData);
			log.Information($"[QuestionableIPC] Imported priority quest: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] ImportQuestPriority failed: " + ex.Message);
			return false;
		}
	}

	public bool TryExportQuestPriority(out string encodedQuestPriority)
	{
		encodedQuestPriority = string.Empty;
		TryEnsureAvailable();
		if (!IsAvailable || exportQuestPrioritySubscriber == null)
		{
			return false;
		}
		try
		{
			encodedQuestPriority = exportQuestPrioritySubscriber.InvokeFunc() ?? string.Empty;
			log.Debug("[QuestionableIPC] Exported the current quest-priority snapshot");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] ExportQuestPriority failed: " + ex.Message);
			return false;
		}
	}

	public List<string> GetPriorityQuests()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getPriorityQuestsSubscriber == null)
		{
			return new List<string>();
		}
		try
		{
			return getPriorityQuestsSubscriber.InvokeFunc() ?? new List<string>();
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] GetPriorityQuests failed: " + ex.Message);
			return new List<string>();
		}
	}

	public bool IsQuestInPriority(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || isQuestInPrioritySubscriber == null)
		{
			return false;
		}
		try
		{
			return isQuestInPrioritySubscriber.InvokeFunc(questId);
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsQuestInPriority(" + questId + ") failed: " + ex.Message);
			return false;
		}
	}

	public bool AddQuestPriority(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || addQuestPrioritySubscriber == null)
		{
			return false;
		}
		try
		{
			ICallGateSubscriber<string, bool>? callGateSubscriber = isQuestInPrioritySubscriber;
			if (callGateSubscriber != null && callGateSubscriber.InvokeFunc(questId))
			{
				log.Debug("[QuestionableIPC] Quest " + questId + " is already in priority");
				return true;
			}
			bool flag = addQuestPrioritySubscriber.InvokeFunc(questId);
			log.Debug($"[QuestionableIPC] Added quest {questId} to priority: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] AddQuestPriority failed: " + ex.Message);
			return false;
		}
	}

	public bool InsertQuestPriority(int index, string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || insertQuestPrioritySubscriber == null)
		{
			return false;
		}
		try
		{
			return insertQuestPrioritySubscriber.InvokeFunc(Math.Max(0, index), questId);
		}
		catch (Exception ex)
		{
			log.Error($"[QuestionableIPC] InsertQuestPriority({index}, {questId}) failed: {ex.Message}");
			return false;
		}
	}

	public bool RemovePriorityQuest(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || removePriorityQuestSubscriber == null)
		{
			return false;
		}
		try
		{
			return removePriorityQuestSubscriber.InvokeFunc(questId);
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] RemovePriorityQuest(" + questId + ") failed: " + ex.Message);
			return false;
		}
	}

	public bool ClearQuestPriority()
	{
		TryEnsureAvailable();
		if (!IsAvailable || clearQuestPrioritySubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = clearQuestPrioritySubscriber.InvokeFunc();
			log.Debug($"[QuestionableIPC] Cleared quest priority: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] ClearQuestPriority failed: " + ex.Message);
			return false;
		}
	}

	public bool RestoreQuestPriority(string encodedQuestPriority)
	{
		if (!ClearQuestPriority())
		{
			return false;
		}
		if (string.IsNullOrEmpty(encodedQuestPriority))
		{
			return true;
		}
		return ImportQuestPriority(encodedQuestPriority);
	}

	public bool IsQuestComplete(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || isQuestCompleteSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = isQuestCompleteSubscriber.InvokeFunc(questId);
			log.Debug($"[QuestionableIPC] Quest {questId} complete: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsQuestComplete failed: " + ex.Message);
			return false;
		}
	}

	public bool IsReadyToAcceptQuest(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || isReadyToAcceptQuestSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = isReadyToAcceptQuestSubscriber.InvokeFunc(questId);
			log.Debug($"[QuestionableIPC] Quest {questId} ready to accept: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsReadyToAcceptQuest failed: " + ex.Message);
			return false;
		}
	}

	public bool IsQuestAccepted(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || isQuestAcceptedSubscriber == null)
		{
			return false;
		}
		try
		{
			return isQuestAcceptedSubscriber.InvokeFunc(questId);
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsQuestAccepted failed: " + ex.Message);
			return false;
		}
	}

	public bool IsQuestLocked(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || isQuestLockedSubscriber == null)
		{
			return false;
		}
		try
		{
			return isQuestLockedSubscriber.InvokeFunc(questId);
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsQuestLocked(" + questId + ") failed: " + ex.Message);
			return false;
		}
	}

	public bool IsQuestUnobtainable(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || isQuestUnobtainableSubscriber == null)
		{
			return false;
		}
		try
		{
			return isQuestUnobtainableSubscriber.InvokeFunc(questId);
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsQuestUnobtainable(" + questId + ") failed: " + ex.Message);
			return false;
		}
	}

	public bool AddQuestsToQueue(List<string> questIds)
	{
		TryEnsureAvailable();
		if (!IsAvailable)
		{
			log.Warning("[QuestionableIPC] Cannot add quests to queue - Questionable not available");
			return false;
		}
		if (questIds == null || questIds.Count == 0)
		{
			return true;
		}
		try
		{
			log.Information($"[QuestionableIPC] Adding {questIds.Count} quests to priority queue");
			foreach (string questId in questIds)
			{
				if (!string.IsNullOrEmpty(questId))
				{
					try
					{
						bool? value = addQuestPrioritySubscriber?.InvokeFunc(questId);
						log.Debug($"[QuestionableIPC] Added quest {questId} to queue: {value}");
					}
					catch (Exception ex)
					{
						log.Warning("[QuestionableIPC] Failed to add quest " + questId + " to queue: " + ex.Message);
					}
				}
			}
			log.Information("[QuestionableIPC] All quests added to priority queue");
			return true;
		}
		catch (Exception ex2)
		{
			log.Error("[QuestionableIPC] Error adding quests to queue: " + ex2.Message);
			return false;
		}
	}

	public List<string> GetCurrentlyActiveEventQuests()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getCurrentlyActiveEventQuestsSubscriber == null)
		{
			log.Warning("[QuestionableIPC] Cannot get active event quests - Questionable not available");
			return new List<string>();
		}
		try
		{
			List<string> list = getCurrentlyActiveEventQuestsSubscriber.InvokeFunc();
			log.Debug($"[QuestionableIPC] Retrieved {list?.Count ?? 0} active event quests");
			return list ?? new List<string>();
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] Error getting active event quests: " + ex.Message);
			return new List<string>();
		}
	}

	public int GetAlliedSocietyRemainingAllowances()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyRemainingAllowancesSubscriber == null)
		{
			log.Debug("[AlliedSociety] Cannot get remaining allowances - Questionable not available");
			return 12;
		}
		try
		{
			int num = getAlliedSocietyRemainingAllowancesSubscriber.InvokeFunc();
			log.Debug($"[AlliedSociety] Remaining allowances: {num}");
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[AlliedSociety] Error getting remaining allowances: " + ex.Message);
			return 12;
		}
	}

	public List<string> GetAlliedSocietyAvailableQuestIds(byte societyId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyAvailableQuestIdsSubscriber == null)
		{
			log.Debug($"[AlliedSociety] Cannot get quest IDs for society {societyId} - Questionable not available");
			return new List<string>();
		}
		try
		{
			List<string> list = getAlliedSocietyAvailableQuestIdsSubscriber.InvokeFunc(societyId);
			log.Debug($"[AlliedSociety] Society {societyId} has {list?.Count ?? 0} available quests");
			return list ?? new List<string>();
		}
		catch (Exception ex)
		{
			log.Error($"[AlliedSociety] Error getting quest IDs for society {societyId}: {ex.Message}");
			return new List<string>();
		}
	}

	public Dictionary<byte, int> GetAlliedSocietyAllAvailableQuestCounts()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyAllAvailableQuestCountsSubscriber == null)
		{
			log.Debug("[AlliedSociety] Cannot get quest counts - Questionable not available");
			return new Dictionary<byte, int>();
		}
		try
		{
			Dictionary<byte, int> dictionary = getAlliedSocietyAllAvailableQuestCountsSubscriber.InvokeFunc();
			log.Debug($"[AlliedSociety] Found {dictionary?.Count ?? 0} societies with available quests");
			return dictionary ?? new Dictionary<byte, int>();
		}
		catch (Exception ex)
		{
			log.Error("[AlliedSociety] Error getting quest counts: " + ex.Message);
			return new Dictionary<byte, int>();
		}
	}

	public bool GetAlliedSocietyIsMaxRank(byte societyId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyIsMaxRankSubscriber == null)
		{
			log.Debug($"[AlliedSociety] Cannot check max rank for society {societyId} - Questionable not available");
			return false;
		}
		try
		{
			bool flag = getAlliedSocietyIsMaxRankSubscriber.InvokeFunc(societyId);
			log.Debug($"[AlliedSociety] Society {societyId} max rank: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error($"[AlliedSociety] Error checking max rank for society {societyId}: {ex.Message}");
			return false;
		}
	}

	public int GetAlliedSocietyCurrentRank(byte societyId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyCurrentRankSubscriber == null)
		{
			log.Debug($"[AlliedSociety] Cannot get rank for society {societyId} - Questionable not available");
			return -1;
		}
		try
		{
			int num = getAlliedSocietyCurrentRankSubscriber.InvokeFunc(societyId);
			log.Debug($"[AlliedSociety] Society {societyId} current rank: {num}");
			return num;
		}
		catch (Exception ex)
		{
			log.Error($"[AlliedSociety] Error getting rank for society {societyId}: {ex.Message}");
			return -1;
		}
	}

	public List<byte> GetAlliedSocietiesWithAvailableQuests()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietiesWithAvailableQuestsSubscriber == null)
		{
			log.Debug("[AlliedSociety] Cannot get societies with quests - Questionable not available");
			return new List<byte>();
		}
		try
		{
			List<byte> list = getAlliedSocietiesWithAvailableQuestsSubscriber.InvokeFunc();
			log.Debug($"[AlliedSociety] Found {list?.Count ?? 0} societies with available quests");
			return list ?? new List<byte>();
		}
		catch (Exception ex)
		{
			log.Error("[AlliedSociety] Error getting societies with quests: " + ex.Message);
			return new List<byte>();
		}
	}

	public int AddAlliedSocietyOptimalQuests(byte societyId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || addAlliedSocietyOptimalQuestsSubscriber == null)
		{
			log.Debug($"[AlliedSociety] Cannot add optimal quests for society {societyId} - Questionable not available");
			return 0;
		}
		try
		{
			int num = addAlliedSocietyOptimalQuestsSubscriber.InvokeFunc(societyId);
			log.Information($"[AlliedSociety] Added {num} optimal quests for society {societyId}");
			return num;
		}
		catch (Exception ex)
		{
			log.Error($"[AlliedSociety] Error adding optimal quests for society {societyId}: {ex.Message}");
			return 0;
		}
	}

	public List<string> GetAlliedSocietyOptimalQuests(byte societyId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyOptimalQuestsSubscriber == null)
		{
			log.Debug($"[AlliedSociety] Cannot get optimal quests for society {societyId} - Questionable not available");
			return new List<string>();
		}
		try
		{
			List<string> list = getAlliedSocietyOptimalQuestsSubscriber.InvokeFunc(societyId);
			log.Debug($"[AlliedSociety] Found {list?.Count ?? 0} optimal quests for society {societyId}");
			return list ?? new List<string>();
		}
		catch (Exception ex)
		{
			log.Error($"[AlliedSociety] Error getting optimal quests for society {societyId}: {ex.Message}");
			return new List<string>();
		}
	}

	public TimeSpan GetAlliedSocietyTimeUntilReset()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAlliedSocietyTimeUntilResetSubscriber == null)
		{
			log.Debug("[AlliedSociety] Cannot get time until reset - Questionable not available");
			return TimeSpan.Zero;
		}
		try
		{
			TimeSpan timeSpan = TimeSpan.FromTicks(getAlliedSocietyTimeUntilResetSubscriber.InvokeFunc());
			log.Debug($"[AlliedSociety] Time until reset: {timeSpan}");
			return timeSpan;
		}
		catch (Exception ex)
		{
			log.Error("[AlliedSociety] Error getting time until reset: " + ex.Message);
			return TimeSpan.Zero;
		}
	}

	public bool GetStopConditionsEnabled()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getStopConditionsEnabledSubscriber == null)
		{
			log.Debug("[StopCondition] Cannot get stop conditions enabled - Questionable not available");
			return false;
		}
		try
		{
			bool flag = getStopConditionsEnabledSubscriber.InvokeFunc();
			log.Debug($"[StopCondition] Stop conditions enabled: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error getting stop conditions enabled: " + ex.Message);
			return false;
		}
	}

	public bool SetStopConditionsEnabled(bool enabled)
	{
		TryEnsureAvailable();
		if (!IsAvailable || setStopConditionsEnabledSubscriber == null)
		{
			log.Warning("[StopCondition] Cannot set stop conditions enabled - Questionable not available");
			return false;
		}
		try
		{
			bool flag = setStopConditionsEnabledSubscriber.InvokeFunc(enabled);
			log.Information($"[StopCondition] Set stop conditions enabled to {enabled}: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error setting stop conditions enabled: " + ex.Message);
			return false;
		}
	}

	public List<string> GetStopQuestList()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getStopQuestListSubscriber == null)
		{
			return new List<string>();
		}
		try
		{
			List<string> list = getStopQuestListSubscriber.InvokeFunc();
			log.Debug($"[StopCondition] Found {list?.Count ?? 0} stop quests");
			return list ?? new List<string>();
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error getting stop quest list: " + ex.Message);
			return new List<string>();
		}
	}

	public bool AddStopQuest(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || addStopQuestSubscriber == null)
		{
			log.Warning("[StopCondition] Cannot add stop quest - Questionable not available");
			return false;
		}
		try
		{
			bool flag = addStopQuestSubscriber.InvokeFunc(questId);
			log.Information($"[StopCondition] Add stop quest {questId}: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error adding stop quest " + questId + ": " + ex.Message);
			return false;
		}
	}

	public StopConditionData? GetLevelStopCondition()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getLevelStopConditionSubscriber == null)
		{
			log.Debug("[StopCondition] Cannot get level stop condition - Questionable not available");
			return null;
		}
		try
		{
			return getLevelStopConditionSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error getting level stop condition: " + ex.Message);
			return null;
		}
	}

	public StopConditionData? GetSequenceStopCondition()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getSequenceStopConditionSubscriber == null)
		{
			log.Debug("[StopCondition] Cannot get sequence stop condition - Questionable not available");
			return null;
		}
		try
		{
			StopConditionData stopConditionData = getSequenceStopConditionSubscriber.InvokeFunc();
			log.Debug($"[StopCondition] Sequence stop condition - Enabled: {stopConditionData?.Enabled}, Target: {stopConditionData?.TargetValue}");
			return stopConditionData;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error getting sequence stop condition: " + ex.Message);
			return null;
		}
	}

	public int GetQuestSequenceStopCondition(string questId)
	{
		TryEnsureAvailable();
		if (!IsAvailable || getQuestSequenceStopConditionSubscriber == null)
		{
			log.Warning("[StopCondition] Cannot get quest sequence stop condition - Questionable not available");
			return -1;
		}
		try
		{
			int num = getQuestSequenceStopConditionSubscriber.InvokeFunc(questId);
			log.Debug($"[StopCondition] Quest sequence stop condition for {questId}: {num}");
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error getting quest sequence stop condition: " + ex.Message);
			return -1;
		}
	}

	public bool SetQuestSequenceStopCondition(string questId, byte sequence)
	{
		TryEnsureAvailable();
		if (!IsAvailable || setQuestSequenceStopConditionSubscriber == null)
		{
			log.Warning("[StopCondition] Cannot set quest sequence stop condition - Questionable not available");
			return false;
		}
		try
		{
			bool flag = setQuestSequenceStopConditionSubscriber.InvokeFunc(questId, sequence);
			log.Information($"[StopCondition] Set quest {questId} sequence stop to {sequence}: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error setting quest " + questId + " sequence stop: " + ex.Message);
			return false;
		}
	}

	public Dictionary<string, int> GetAllQuestSequenceStopConditions()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getAllQuestSequenceStopConditionsSubscriber == null)
		{
			return new Dictionary<string, int>();
		}
		try
		{
			Dictionary<string, int> dictionary = getAllQuestSequenceStopConditionsSubscriber.InvokeFunc();
			if (dictionary == null || dictionary.Count == 0)
			{
				log.Information("[StopCondition] No quest sequence stop conditions configured (empty or null result)");
				return new Dictionary<string, int>();
			}
			log.Information($"[StopCondition] Found {dictionary.Count} quest sequence stop condition(s)");
			return dictionary;
		}
		catch (Exception ex)
		{
			log.Error("[StopCondition] Error getting all quest sequence stop conditions: " + ex.Message);
			return new Dictionary<string, int>();
		}
	}

	public int GetDefaultDutyMode()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getDefaultDutyModeSubscriber == null)
		{
			log.Debug("[QuestionableIPC] Cannot get default duty mode - Questionable not available");
			return 0;
		}
		try
		{
			int num = getDefaultDutyModeSubscriber.InvokeFunc();
			log.Debug($"[QuestionableIPC] Default Duty Mode: {num}");
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] GetDefaultDutyMode failed: " + ex.Message);
			return 0;
		}
	}

	public bool SetDefaultDutyMode(int dutyMode)
	{
		TryEnsureAvailable();
		if (!IsAvailable || setDefaultDutyModeSubscriber == null)
		{
			log.Debug("[QuestionableIPC] Cannot set default duty mode - Questionable not available");
			return false;
		}
		try
		{
			bool flag = setDefaultDutyModeSubscriber.InvokeFunc(dutyMode);
			log.Information($"[QuestionableIPC] Set Default Duty Mode to {dutyMode}: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] SetDefaultDutyMode failed: " + ex.Message);
			return false;
		}
	}

	public bool ValidateFeatureCompatibility()
	{
		if (TryEnsureAvailableSilent())
		{
			return IsTrustedWigglyBuild;
		}
		return false;
	}

	public bool IsLevelingModeEnabled()
	{
		TryEnsureAvailable();
		if (!IsAvailable || isLevelingModeEnabledSubscriber == null)
		{
			return false;
		}
		try
		{
			return isLevelingModeEnabledSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] IsLevelingModeEnabled failed: " + ex.Message);
			return false;
		}
	}

	public bool SetLevelingModeEnabled(bool enabled)
	{
		TryEnsureAvailable();
		if (!IsAvailable || setLevelingModeEnabledSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = setLevelingModeEnabledSubscriber.InvokeFunc(enabled);
			log.Information($"[QuestionableIPC] SetLevelingModeEnabled({enabled}) -> {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] SetLevelingModeEnabled failed: " + ex.Message);
			return false;
		}
	}

	public MsqLevelLockData? GetMsqLevelLockInfo()
	{
		TryEnsureAvailableSilent();
		if (!IsAvailable || getMsqLevelLockInfoSubscriber == null)
		{
			return null;
		}
		try
		{
			return getMsqLevelLockInfoSubscriber.InvokeFunc();
		}
		catch
		{
			return null;
		}
	}

	public bool StartLevelingMode()
	{
		TryEnsureAvailable();
		if (!IsAvailable || startLevelingModeSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = startLevelingModeSubscriber.InvokeFunc();
			log.Information($"[QuestionableIPC] StartLevelingMode() -> {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] StartLevelingMode failed: " + ex.Message);
			return false;
		}
	}

	public bool StopLevelingMode()
	{
		TryEnsureAvailable();
		if (!IsAvailable || stopLevelingModeSubscriber == null)
		{
			return false;
		}
		try
		{
			bool flag = stopLevelingModeSubscriber.InvokeFunc();
			log.Information($"[QuestionableIPC] StopLevelingMode() -> {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error("[QuestionableIPC] StopLevelingMode failed: " + ex.Message);
			return false;
		}
	}

	public void Dispose()
	{
		pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
		IsAvailable = false;
	}
}
