using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class CompanionIPC : IDisposable
{
	private sealed class HuntLogSettingsPatch
	{
		public bool? AutoGrandCompanyRankUp { get; set; }

		public bool? ResumeIncompleteRuns { get; set; }

		public int? StopAfterClassRank { get; set; }

		public int? StopAfterGrandCompanyRank { get; set; }

		public bool? SkipDutyMarks { get; set; }

		public bool? SoloUnsyncedLogDuty { get; set; }

		public bool? ReturnOnceDone { get; set; }

		public string? ReturnDestination { get; set; }

		public string? CombatJobMode { get; set; }

		public uint? PreferredCombatJobId { get; set; }

		public float? GroundApproachDistance { get; set; }

		public bool? SummonChocobo { get; set; }

		public string? CompanionStance { get; set; }

		public bool? AutoSyncFateTargets { get; set; }

		public string? CombatMode { get; set; }

		public bool? EnableRotationSolverReborn { get; set; }

		public bool? EnableVbmAi { get; set; }

		public bool? EnableBmrAi { get; set; }
	}

	private sealed class HuntLogIpcSettings
	{
		public bool AutoGrandCompanyRankUp { get; set; }

		public bool ResumeIncompleteRuns { get; set; }

		public int StopAfterClassRank { get; set; }

		public int StopAfterGrandCompanyRank { get; set; }

		public bool SkipDutyMarks { get; set; }

		public bool SoloUnsyncedLogDuty { get; set; }

		public bool ReturnOnceDone { get; set; }

		public HuntLogReturnDestination ReturnDestination { get; set; }

		public HuntLogCombatJobMode CombatJobMode { get; set; }

		public uint PreferredCombatJobId { get; set; }

		public float GroundApproachDistance { get; set; }

		public bool SummonChocobo { get; set; }

		public string CompanionStance { get; set; } = "Free Stance";

		public bool AutoSyncFateTargets { get; set; }

		public HuntLogCombatMode CombatMode { get; set; }

		public bool EnableRotationSolverReborn { get; set; }

		public bool EnableVbmAi { get; set; }

		public bool EnableBmrAi { get; set; }

		public static HuntLogIpcSettings From(HuntLogSettings settings)
		{
			return new HuntLogIpcSettings
			{
				AutoGrandCompanyRankUp = settings.AutoGrandCompanyRankUp,
				ResumeIncompleteRuns = settings.ResumeIncompleteRuns,
				StopAfterClassRank = settings.StopAfterClassRank,
				StopAfterGrandCompanyRank = settings.StopAfterGrandCompanyRank,
				SkipDutyMarks = settings.SkipDutyMarks,
				SoloUnsyncedLogDuty = settings.SoloUnsyncedLogDuty,
				ReturnOnceDone = settings.ReturnOnceDone,
				ReturnDestination = settings.ReturnDestination,
				CombatJobMode = settings.CombatJobMode,
				PreferredCombatJobId = settings.PreferredCombatJobId,
				GroundApproachDistance = settings.GroundApproachDistance,
				SummonChocobo = settings.SummonChocobo,
				CompanionStance = settings.CompanionStance,
				AutoSyncFateTargets = settings.AutoSyncFateTargets,
				CombatMode = settings.CombatMode,
				EnableRotationSolverReborn = settings.EnableRotationSolverReborn,
				EnableVbmAi = settings.EnableVBMAI,
				EnableBmrAi = settings.EnableBMRAI
			};
		}

		public void ApplyTo(HuntLogSettings settings)
		{
			settings.AutoGrandCompanyRankUp = AutoGrandCompanyRankUp;
			settings.ResumeIncompleteRuns = ResumeIncompleteRuns;
			settings.StopAfterClassRank = StopAfterClassRank;
			settings.StopAfterGrandCompanyRank = StopAfterGrandCompanyRank;
			settings.SkipDutyMarks = SkipDutyMarks;
			settings.SoloUnsyncedLogDuty = SoloUnsyncedLogDuty;
			settings.ReturnOnceDone = ReturnOnceDone;
			settings.ReturnDestination = ReturnDestination;
			settings.CombatJobMode = CombatJobMode;
			settings.PreferredCombatJobId = PreferredCombatJobId;
			settings.GroundApproachDistance = GroundApproachDistance;
			settings.SummonChocobo = SummonChocobo;
			settings.CompanionStance = CompanionStance;
			settings.AutoSyncFateTargets = AutoSyncFateTargets;
			settings.CombatMode = CombatMode;
			settings.EnableRotationSolverReborn = EnableRotationSolverReborn;
			settings.EnableVBMAI = EnableVbmAi;
			settings.EnableBMRAI = EnableBmrAi;
		}
	}

	private const int IpcApiVersion = 1;

	private static readonly JsonSerializerSettings IpcJsonSettings = new JsonSerializerSettings
	{
		MissingMemberHandling = MissingMemberHandling.Error,
		NullValueHandling = NullValueHandling.Ignore
	};

	private static readonly string[] CompanionStances = new string[5] { "Free Stance", "Defender Stance", "Attacker Stance", "Healer Stance", "Follow" };

	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly QuestRotationExecutionService questRotationService;

	private readonly HuntLogAutomationService huntLogAutomationService;

	private readonly Configuration configuration;

	private readonly IClientState clientState;

	private readonly ICallGateProvider<(int completed, int total)> getProgressProvider;

	private readonly ICallGateProvider<(string? characterName, string? worldName)> getCurrentCharacterProvider;

	private readonly ICallGateProvider<string?> getCurrentStopPointProvider;

	private readonly ICallGateProvider<int> getApiVersionProvider;

	private readonly ICallGateProvider<string> startRotationProvider;

	private readonly ICallGateProvider<string> stopRotationProvider;

	private readonly ICallGateProvider<string> getRotationStateProvider;

	private readonly ICallGateProvider<string> getHuntLogCapabilitiesProvider;

	private readonly ICallGateProvider<string> getHuntLogSettingsProvider;

	private readonly ICallGateProvider<string, string> updateHuntLogSettingsProvider;

	private readonly ICallGateProvider<string, string> startHuntLogsProvider;

	private readonly ICallGateProvider<string> stopHuntLogsProvider;

	private readonly ICallGateProvider<string> getHuntLogStateProvider;

	private string StartRotation()
	{
		try
		{
			if (questRotationService.IsRotationActive)
			{
				return Result(success: true, "already-running", "Quest rotation is already running.", "rotation");
			}
			if (IsHuntLogBusy())
			{
				return Result(success: false, "feature-conflict", "Hunt Logs are currently running or stopping.", "rotation");
			}
			if (configuration.SelectedCharactersForUI.Count == 0)
			{
				return Result(success: false, "no-characters", "No characters are selected in Questionable Companion.", "rotation");
			}
			if (questRotationService.GetAllStopPoints().Count == 0)
			{
				return Result(success: false, "no-stop-points", "No rotation stop points are configured.", "rotation");
			}
			questRotationService.StartNextAvailableRotation();
			return questRotationService.IsRotationActive ? Result(success: true, "started", "Quest rotation started.", "rotation") : Result(success: false, "nothing-to-run", "No configured stop point can currently be started.", "rotation");
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] Rotation.Start failed");
			return Result(success: false, "internal-error", ex.Message, "rotation");
		}
	}

	private string StopRotation()
	{
		try
		{
			if (!questRotationService.IsRotationActive)
			{
				return Result(success: true, "already-stopped", "Quest rotation is already stopped.", "rotation");
			}
			questRotationService.AbortRotation();
			return Result(success: true, "stopped", "Quest rotation stop requested.", "rotation");
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] Rotation.Stop failed");
			return Result(success: false, "internal-error", ex.Message, "rotation");
		}
	}

	private string GetRotationState()
	{
		try
		{
			RotationState currentState = questRotationService.GetCurrentState();
			return Serialize(new
			{
				apiVersion = 1,
				feature = "rotation",
				running = questRotationService.IsRotationActive,
				phase = ToKebabCase(currentState.Phase.ToString()),
				currentStopQuestId = currentState.CurrentStopQuestId,
				currentCharacter = EmptyToNull(currentState.CurrentCharacter),
				nextCharacter = EmptyToNull(currentState.NextCharacter),
				selectedCharacters = currentState.SelectedCharacters,
				remainingCharacters = currentState.RemainingCharacters,
				completedCharacters = currentState.CompletedCharacters,
				skippedCharacters = currentState.SkippedCharacters,
				error = EmptyToNull(currentState.ErrorMessage)
			});
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] Rotation.GetState failed");
			return Result(success: false, "internal-error", ex.Message, "rotation");
		}
	}

	private string GetHuntLogCapabilities()
	{
		try
		{
			FrenRiderAvailability frenRiderAvailability = huntLogAutomationService.GetFrenRiderAvailability();
			return Serialize(new
			{
				apiVersion = 1,
				feature = "hunt-logs",
				modes = new string[3] { "class", "grand-company", "all" },
				classRank = new
				{
					min = 1,
					max = 5
				},
				grandCompanyRank = new
				{
					min = 1,
					max = 11
				},
				returnDestinations = new string[5] { "home", "free-company", "apartment", "inn", "auto" },
				combatJobModes = new string[3] { "highest", "current", "specific" },
				combatModes = new string[2] { "standard", "fren-rider" },
				companionStances = new string[5] { "free", "defender", "attacker", "healer", "follow" },
				frenRider = new
				{
					available = frenRiderAvailability.CanSelect,
					message = frenRiderAvailability.Message
				},
				mountConfiguration = "plugin-only"
			});
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] HuntLogs.GetCapabilities failed");
			return Result(success: false, "internal-error", ex.Message, "hunt-logs");
		}
	}

	private string GetHuntLogSettings()
	{
		try
		{
			return Serialize(new
			{
				apiVersion = 1,
				feature = "hunt-logs",
				settings = CreateHuntLogSettingsView(configuration.HuntLogs)
			});
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] HuntLogs.GetSettings failed");
			return Result(success: false, "internal-error", ex.Message, "hunt-logs");
		}
	}

	private string UpdateHuntLogSettings(string jsonPatch)
	{
		try
		{
			if (IsHuntLogBusy())
			{
				return Result(success: false, "feature-running", "Hunt Log settings cannot be changed while Hunt Logs are running or stopping.", "hunt-logs");
			}
			if (string.IsNullOrWhiteSpace(jsonPatch))
			{
				return Result(success: false, "invalid-json", "The settings patch must be a JSON object.", "hunt-logs");
			}
			if (JToken.Parse(jsonPatch).Type != JTokenType.Object)
			{
				return Result(success: false, "invalid-json", "The settings patch must be a JSON object.", "hunt-logs");
			}
			HuntLogSettingsPatch huntLogSettingsPatch = JsonConvert.DeserializeObject<HuntLogSettingsPatch>(jsonPatch, IpcJsonSettings) ?? throw new JsonException("The settings patch was empty.");
			HuntLogIpcSettings huntLogIpcSettings = HuntLogIpcSettings.From(configuration.HuntLogs);
			if (huntLogSettingsPatch.AutoGrandCompanyRankUp.HasValue)
			{
				huntLogIpcSettings.AutoGrandCompanyRankUp = huntLogSettingsPatch.AutoGrandCompanyRankUp.Value;
			}
			if (huntLogSettingsPatch.AutoSyncFateTargets.HasValue)
			{
				huntLogIpcSettings.AutoSyncFateTargets = huntLogSettingsPatch.AutoSyncFateTargets.Value;
			}
			if (huntLogSettingsPatch.ResumeIncompleteRuns.HasValue)
			{
				huntLogIpcSettings.ResumeIncompleteRuns = huntLogSettingsPatch.ResumeIncompleteRuns.Value;
			}
			if (huntLogSettingsPatch.StopAfterClassRank.HasValue)
			{
				huntLogIpcSettings.StopAfterClassRank = huntLogSettingsPatch.StopAfterClassRank.Value;
			}
			if (huntLogSettingsPatch.StopAfterGrandCompanyRank.HasValue)
			{
				huntLogIpcSettings.StopAfterGrandCompanyRank = huntLogSettingsPatch.StopAfterGrandCompanyRank.Value;
			}
			if (huntLogSettingsPatch.SkipDutyMarks.HasValue)
			{
				huntLogIpcSettings.SkipDutyMarks = huntLogSettingsPatch.SkipDutyMarks.Value;
			}
			if (huntLogSettingsPatch.SoloUnsyncedLogDuty.HasValue)
			{
				huntLogIpcSettings.SoloUnsyncedLogDuty = huntLogSettingsPatch.SoloUnsyncedLogDuty.Value;
			}
			if (huntLogSettingsPatch.ReturnOnceDone.HasValue)
			{
				huntLogIpcSettings.ReturnOnceDone = huntLogSettingsPatch.ReturnOnceDone.Value;
			}
			if (huntLogSettingsPatch.GroundApproachDistance.HasValue)
			{
				huntLogIpcSettings.GroundApproachDistance = huntLogSettingsPatch.GroundApproachDistance.Value;
			}
			if (huntLogSettingsPatch.SummonChocobo.HasValue)
			{
				huntLogIpcSettings.SummonChocobo = huntLogSettingsPatch.SummonChocobo.Value;
			}
			if (huntLogSettingsPatch.EnableRotationSolverReborn.HasValue)
			{
				huntLogIpcSettings.EnableRotationSolverReborn = huntLogSettingsPatch.EnableRotationSolverReborn.Value;
			}
			if (huntLogSettingsPatch.EnableVbmAi.HasValue)
			{
				huntLogIpcSettings.EnableVbmAi = huntLogSettingsPatch.EnableVbmAi.Value;
			}
			if (huntLogSettingsPatch.EnableBmrAi.HasValue)
			{
				huntLogIpcSettings.EnableBmrAi = huntLogSettingsPatch.EnableBmrAi.Value;
			}
			if (huntLogSettingsPatch.ReturnDestination != null)
			{
				if (!TryParseReturnDestination(huntLogSettingsPatch.ReturnDestination, out var destination))
				{
					return Result(success: false, "invalid-return-destination", "Unknown returnDestination. Call GetCapabilities for valid values.", "hunt-logs");
				}
				huntLogIpcSettings.ReturnDestination = destination;
			}
			if (huntLogSettingsPatch.CombatJobMode != null)
			{
				if (!TryParseCombatJobMode(huntLogSettingsPatch.CombatJobMode, out var mode))
				{
					return Result(success: false, "invalid-combat-job-mode", "Unknown combatJobMode. Call GetCapabilities for valid values.", "hunt-logs");
				}
				huntLogIpcSettings.CombatJobMode = mode;
			}
			if (huntLogSettingsPatch.PreferredCombatJobId.HasValue)
			{
				huntLogIpcSettings.PreferredCombatJobId = huntLogSettingsPatch.PreferredCombatJobId.Value;
			}
			if (huntLogSettingsPatch.CombatMode != null)
			{
				if (!TryParseCombatMode(huntLogSettingsPatch.CombatMode, out var mode2))
				{
					return Result(success: false, "invalid-combat-mode", "Unknown combatMode. Call GetCapabilities for valid values.", "hunt-logs");
				}
				huntLogIpcSettings.CombatMode = mode2;
			}
			if (huntLogSettingsPatch.CompanionStance != null)
			{
				if (!TryParseCompanionStance(huntLogSettingsPatch.CompanionStance, out string stance))
				{
					return Result(success: false, "invalid-companion-stance", "Unknown companionStance. Call GetCapabilities for valid values.", "hunt-logs");
				}
				huntLogIpcSettings.CompanionStance = stance;
			}
			(string, string)? tuple = ValidateHuntLogSettings(huntLogIpcSettings);
			if (tuple.HasValue)
			{
				return Result(success: false, tuple.Value.Item1, tuple.Value.Item2, "hunt-logs");
			}
			huntLogIpcSettings.ApplyTo(configuration.HuntLogs);
			configuration.Save();
			JObject jObject = JObject.Parse(Result(success: true, "settings-updated", "Hunt Log settings were updated.", "hunt-logs"));
			jObject["settings"] = JToken.FromObject(CreateHuntLogSettingsView(configuration.HuntLogs));
			return jObject.ToString(Formatting.None);
		}
		catch (JsonException ex)
		{
			return Result(success: false, "invalid-json", ex.Message, "hunt-logs");
		}
		catch (Exception ex2)
		{
			log.Error(ex2, "[CompanionIPC] HuntLogs.UpdateSettings failed");
			return Result(success: false, "internal-error", ex2.Message, "hunt-logs");
		}
	}

	private string StartHuntLogs(string modeValue)
	{
		try
		{
			if (!TryParseHuntLogMode(modeValue, out var mode))
			{
				return Result(success: false, "invalid-mode", "Unknown Hunt Log mode. Call GetCapabilities for valid values.", "hunt-logs");
			}
			if (IsHuntLogBusy())
			{
				return (huntLogAutomationService.GetCurrentState().Phase == HuntLogPhase.Stopping) ? Result(success: false, "feature-stopping", "Hunt Logs are still stopping.", "hunt-logs") : Result(success: true, "already-running", "Hunt Logs are already running.", "hunt-logs");
			}
			if (questRotationService.IsRotationActive)
			{
				return Result(success: false, "feature-conflict", "Quest rotation is currently running.", "hunt-logs");
			}
			List<string> list = configuration.SelectedCharactersForUI.Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
			if (list.Count == 0)
			{
				return Result(success: false, "no-characters", "No characters are selected in Questionable Companion.", "hunt-logs");
			}
			if (!huntLogAutomationService.Start(mode, list))
			{
				string errorMessage = huntLogAutomationService.GetCurrentState().ErrorMessage;
				return Result(success: false, "start-rejected", string.IsNullOrWhiteSpace(errorMessage) ? "Hunt Log start was rejected." : errorMessage, "hunt-logs");
			}
			return Result(success: true, "started", "Hunt Logs started in " + FormatHuntLogMode(mode) + " mode.", "hunt-logs");
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] HuntLogs.Start failed");
			return Result(success: false, "internal-error", ex.Message, "hunt-logs");
		}
	}

	private string StopHuntLogs()
	{
		try
		{
			if (huntLogAutomationService.GetCurrentState().Phase == HuntLogPhase.Stopping)
			{
				return Result(success: true, "already-stopping", "Hunt Logs are already stopping.", "hunt-logs");
			}
			if (!huntLogAutomationService.IsRunning)
			{
				return Result(success: true, "already-stopped", "Hunt Logs are already stopped.", "hunt-logs");
			}
			huntLogAutomationService.Stop();
			return Result(success: true, "stopped", "Hunt Log stop requested.", "hunt-logs");
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] HuntLogs.Stop failed");
			return Result(success: false, "internal-error", ex.Message, "hunt-logs");
		}
	}

	private string GetHuntLogState()
	{
		try
		{
			HuntLogAutomationState currentState = huntLogAutomationService.GetCurrentState();
			return Serialize(new
			{
				apiVersion = 1,
				feature = "hunt-logs",
				running = huntLogAutomationService.IsRunning,
				busy = IsHuntLogBusy(currentState),
				phase = ToKebabCase(currentState.Phase.ToString()),
				mode = FormatHuntLogMode(currentState.Mode),
				currentCharacter = EmptyToNull(currentState.CurrentCharacter),
				currentStep = EmptyToNull(currentState.CurrentStep),
				currentMarkName = EmptyToNull(currentState.CurrentMarkName),
				currentRank = currentState.CurrentRank,
				currentCombatJobId = currentState.CurrentCombatJobId,
				selectedCombatJobId = currentState.SelectedCombatJobId,
				selectedCharacters = currentState.SelectedCharacters,
				remainingCharacters = currentState.RemainingCharacters,
				completedCharacters = currentState.CompletedCharacters,
				skippedCharacters = currentState.SkippedCharacters,
				failedCharacters = currentState.FailedCharacters,
				characterStatuses = currentState.CharacterStatuses,
				pendingMarks = currentState.PendingMarks,
				error = EmptyToNull(currentState.ErrorMessage),
				startedAtUtc = ((currentState.StartedAtUtc == DateTime.MinValue) ? ((DateTime?)null) : new DateTime?(currentState.StartedAtUtc))
			});
		}
		catch (Exception ex)
		{
			log.Error(ex, "[CompanionIPC] HuntLogs.GetState failed");
			return Result(success: false, "internal-error", ex.Message, "hunt-logs");
		}
	}

	private bool IsHuntLogBusy()
	{
		return IsHuntLogBusy(huntLogAutomationService.GetCurrentState());
	}

	private static bool IsHuntLogBusy(HuntLogAutomationState state)
	{
		HuntLogPhase phase = state.Phase;
		bool flag = ((phase == HuntLogPhase.Idle || phase == HuntLogPhase.Completed || phase == HuntLogPhase.Error) ? true : false);
		return !flag;
	}

	private (string Code, string Message)? ValidateHuntLogSettings(HuntLogIpcSettings settings)
	{
		int stopAfterClassRank = settings.StopAfterClassRank;
		if ((stopAfterClassRank < 1 || stopAfterClassRank > 5) ? true : false)
		{
			return ("invalid-class-rank", "stopAfterClassRank must be between 1 and 5.");
		}
		stopAfterClassRank = settings.StopAfterGrandCompanyRank;
		if ((stopAfterClassRank < 1 || stopAfterClassRank > 11) ? true : false)
		{
			return ("invalid-gc-rank", $"stopAfterGrandCompanyRank must be between 1 and {11}.");
		}
		float groundApproachDistance = settings.GroundApproachDistance;
		if ((groundApproachDistance < 5f || groundApproachDistance > 100f) ? true : false)
		{
			return ("invalid-ground-distance", "groundApproachDistance must be between 5 and 100 yalms.");
		}
		if (settings.PreferredCombatJobId > 255 || (settings.PreferredCombatJobId != 0 && !JobClassification.IsCombatJob((byte)settings.PreferredCombatJobId)))
		{
			return ("invalid-combat-job", "preferredCombatJobId must be a valid combat ClassJob ID.");
		}
		if (settings.CombatJobMode == HuntLogCombatJobMode.SpecificJob && settings.PreferredCombatJobId == 0)
		{
			return ("missing-combat-job", "preferredCombatJobId is required when combatJobMode is specific.");
		}
		if (settings.CombatMode == HuntLogCombatMode.FrenRider && !huntLogAutomationService.GetFrenRiderAvailability().CanSelect)
		{
			return ("fren-rider-unavailable", "FrenRider is not currently available or compatible.");
		}
		return null;
	}

	private static object CreateHuntLogSettingsView(HuntLogSettings settings)
	{
		return new
		{
			autoGrandCompanyRankUp = settings.AutoGrandCompanyRankUp,
			resumeIncompleteRuns = settings.ResumeIncompleteRuns,
			stopAfterClassRank = settings.StopAfterClassRank,
			stopAfterGrandCompanyRank = settings.StopAfterGrandCompanyRank,
			skipDutyMarks = settings.SkipDutyMarks,
			soloUnsyncedLogDuty = settings.SoloUnsyncedLogDuty,
			returnOnceDone = settings.ReturnOnceDone,
			returnDestination = FormatReturnDestination(settings.ReturnDestination),
			combatJobMode = FormatCombatJobMode(settings.CombatJobMode),
			preferredCombatJobId = settings.PreferredCombatJobId,
			groundApproachDistance = settings.GroundApproachDistance,
			summonChocobo = settings.SummonChocobo,
			companionStance = FormatCompanionStance(settings.CompanionStance),
			autoSyncFateTargets = settings.AutoSyncFateTargets,
			combatMode = FormatCombatMode(settings.CombatMode),
			enableRotationSolverReborn = settings.EnableRotationSolverReborn,
			enableVbmAi = settings.EnableVBMAI,
			enableBmrAi = settings.EnableBMRAI,
			mountConfiguration = "plugin-only"
		};
	}

	private static string Result(bool success, string code, string message, string feature)
	{
		return Serialize(new
		{
			apiVersion = 1,
			success = success,
			code = code,
			message = message,
			feature = feature
		});
	}

	private static string Serialize(object value)
	{
		return JsonConvert.SerializeObject(value, Formatting.None);
	}

	private static string? EmptyToNull(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		return null;
	}

	private static string ToKebabCase(string value)
	{
		StringBuilder stringBuilder = new StringBuilder(value.Length + 8);
		for (int i = 0; i < value.Length; i++)
		{
			char c = value[i];
			if (char.IsUpper(c) && i > 0)
			{
				stringBuilder.Append('-');
			}
			stringBuilder.Append(char.ToLowerInvariant(c));
		}
		return stringBuilder.ToString();
	}

	private static string Normalize(string value)
	{
		return value.Trim().Replace('_', '-').Replace(' ', '-')
			.ToLowerInvariant();
	}

	private static bool TryParseHuntLogMode(string? value, out HuntLogMode mode)
	{
		HuntLogMode huntLogMode;
		switch (Normalize(value ?? string.Empty))
		{
		case "class":
			huntLogMode = HuntLogMode.Class;
			break;
		case "gc":
		case "grand-company":
		case "grandcompany":
			huntLogMode = HuntLogMode.GrandCompany;
			break;
		case "all":
			huntLogMode = HuntLogMode.All;
			break;
		default:
			huntLogMode = (HuntLogMode)(-1);
			break;
		}
		mode = huntLogMode;
		return Enum.IsDefined(mode);
	}

	private static string FormatHuntLogMode(HuntLogMode mode)
	{
		return mode switch
		{
			HuntLogMode.Class => "class", 
			HuntLogMode.GrandCompany => "grand-company", 
			_ => "all", 
		};
	}

	private static bool TryParseReturnDestination(string value, out HuntLogReturnDestination destination)
	{
		HuntLogReturnDestination huntLogReturnDestination;
		switch (Normalize(value))
		{
		case "home":
			huntLogReturnDestination = HuntLogReturnDestination.Home;
			break;
		case "free-company":
		case "freecompany":
		case "fc":
			huntLogReturnDestination = HuntLogReturnDestination.FreeCompany;
			break;
		case "apt":
		case "apartment":
			huntLogReturnDestination = HuntLogReturnDestination.Apartment;
			break;
		case "inn":
			huntLogReturnDestination = HuntLogReturnDestination.Inn;
			break;
		case "auto":
			huntLogReturnDestination = HuntLogReturnDestination.Auto;
			break;
		default:
			huntLogReturnDestination = (HuntLogReturnDestination)(-1);
			break;
		}
		destination = huntLogReturnDestination;
		return Enum.IsDefined(destination);
	}

	private static string FormatReturnDestination(HuntLogReturnDestination destination)
	{
		return destination switch
		{
			HuntLogReturnDestination.Home => "home", 
			HuntLogReturnDestination.FreeCompany => "free-company", 
			HuntLogReturnDestination.Apartment => "apartment", 
			HuntLogReturnDestination.Inn => "inn", 
			_ => "auto", 
		};
	}

	private static bool TryParseCombatJobMode(string value, out HuntLogCombatJobMode mode)
	{
		HuntLogCombatJobMode huntLogCombatJobMode;
		switch (Normalize(value))
		{
		case "highest":
		case "highest-combat-job":
			huntLogCombatJobMode = HuntLogCombatJobMode.HighestCombatJob;
			break;
		case "current":
		case "current-combat-job":
			huntLogCombatJobMode = HuntLogCombatJobMode.CurrentCombatJob;
			break;
		case "specific":
		case "specific-job":
			huntLogCombatJobMode = HuntLogCombatJobMode.SpecificJob;
			break;
		default:
			huntLogCombatJobMode = (HuntLogCombatJobMode)(-1);
			break;
		}
		mode = huntLogCombatJobMode;
		return Enum.IsDefined(mode);
	}

	private static string FormatCombatJobMode(HuntLogCombatJobMode mode)
	{
		return mode switch
		{
			HuntLogCombatJobMode.CurrentCombatJob => "current", 
			HuntLogCombatJobMode.SpecificJob => "specific", 
			_ => "highest", 
		};
	}

	private static bool TryParseCombatMode(string value, out HuntLogCombatMode mode)
	{
		HuntLogCombatMode huntLogCombatMode;
		switch (Normalize(value))
		{
		case "standard":
			huntLogCombatMode = HuntLogCombatMode.Standard;
			break;
		case "fren-rider":
		case "frenrider":
			huntLogCombatMode = HuntLogCombatMode.FrenRider;
			break;
		default:
			huntLogCombatMode = (HuntLogCombatMode)(-1);
			break;
		}
		mode = huntLogCombatMode;
		return Enum.IsDefined(mode);
	}

	private static string FormatCombatMode(HuntLogCombatMode mode)
	{
		if (mode != HuntLogCombatMode.FrenRider)
		{
			return "standard";
		}
		return "fren-rider";
	}

	private static bool TryParseCompanionStance(string value, out string stance)
	{
		stance = Normalize(value).Replace("-stance", string.Empty) switch
		{
			"free" => "Free Stance", 
			"defender" => "Defender Stance", 
			"attacker" => "Attacker Stance", 
			"healer" => "Healer Stance", 
			"follow" => "Follow", 
			_ => string.Empty, 
		};
		return CompanionStances.Contains<string>(stance, StringComparer.Ordinal);
	}

	private static string FormatCompanionStance(string stance)
	{
		TryParseCompanionStance(stance, out string stance2);
		return stance2 switch
		{
			"Defender Stance" => "defender", 
			"Attacker Stance" => "attacker", 
			"Healer Stance" => "healer", 
			"Follow" => "follow", 
			_ => "free", 
		};
	}

	public CompanionIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, IFramework framework, QuestRotationExecutionService questRotationService, HuntLogAutomationService huntLogAutomationService, Configuration configuration, IClientState clientState)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		this.framework = framework;
		this.questRotationService = questRotationService;
		this.huntLogAutomationService = huntLogAutomationService;
		this.configuration = configuration;
		this.clientState = clientState;
		getProgressProvider = pluginInterface.GetIpcProvider<(int, int)>("QSTCompanion.GetProgress");
		getCurrentCharacterProvider = pluginInterface.GetIpcProvider<(string, string)>("QSTCompanion.GetCurrentCharacter");
		getCurrentStopPointProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.GetCurrentStopPoint");
		getApiVersionProvider = pluginInterface.GetIpcProvider<int>("QSTCompanion.GetApiVersion");
		startRotationProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.Rotation.Start");
		stopRotationProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.Rotation.Stop");
		getRotationStateProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.Rotation.GetState");
		getHuntLogCapabilitiesProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.HuntLogs.GetCapabilities");
		getHuntLogSettingsProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.HuntLogs.GetSettings");
		updateHuntLogSettingsProvider = pluginInterface.GetIpcProvider<string, string>("QSTCompanion.HuntLogs.UpdateSettings");
		startHuntLogsProvider = pluginInterface.GetIpcProvider<string, string>("QSTCompanion.HuntLogs.Start");
		stopHuntLogsProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.HuntLogs.Stop");
		getHuntLogStateProvider = pluginInterface.GetIpcProvider<string>("QSTCompanion.HuntLogs.GetState");
		getProgressProvider.RegisterFunc(GetProgress);
		getCurrentCharacterProvider.RegisterFunc(GetCurrentCharacter);
		getCurrentStopPointProvider.RegisterFunc(GetCurrentStopPoint);
		getApiVersionProvider.RegisterFunc(() => 1);
		startRotationProvider.RegisterFunc(() => InvokeOnFrameworkThread(StartRotation));
		stopRotationProvider.RegisterFunc(() => InvokeOnFrameworkThread(StopRotation));
		getRotationStateProvider.RegisterFunc(() => InvokeOnFrameworkThread(GetRotationState));
		getHuntLogCapabilitiesProvider.RegisterFunc(() => InvokeOnFrameworkThread(GetHuntLogCapabilities));
		getHuntLogSettingsProvider.RegisterFunc(() => InvokeOnFrameworkThread(GetHuntLogSettings));
		updateHuntLogSettingsProvider.RegisterFunc((string json) => InvokeOnFrameworkThread(() => UpdateHuntLogSettings(json)));
		startHuntLogsProvider.RegisterFunc((string mode) => InvokeOnFrameworkThread(() => StartHuntLogs(mode)));
		stopHuntLogsProvider.RegisterFunc(() => InvokeOnFrameworkThread(StopHuntLogs));
		getHuntLogStateProvider.RegisterFunc(() => InvokeOnFrameworkThread(GetHuntLogState));
		log.Information("[CompanionIPC] IPC Providers registered");
	}

	private T InvokeOnFrameworkThread<T>(Func<T> action)
	{
		if (framework.IsInFrameworkUpdateThread)
		{
			return action();
		}
		return framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
	}

	private (int completed, int total) GetProgress()
	{
		try
		{
			StopPoint currentStopPoint = questRotationService.GetCurrentStopPoint();
			if (currentStopPoint == null)
			{
				return (completed: 0, total: 0);
			}
			List<string> rotationCharacters = questRotationService.GetRotationCharacters();
			if (rotationCharacters == null || rotationCharacters.Count == 0)
			{
				return (completed: 0, total: 0);
			}
			var (item, item2) = questRotationService.GetRotationProgress(currentStopPoint, rotationCharacters);
			return (completed: item, total: item2);
		}
		catch (Exception ex)
		{
			log.Error("[CompanionIPC] GetProgress failed: " + ex.Message);
			return (completed: 0, total: 0);
		}
	}

	private (string? characterName, string? worldName) GetCurrentCharacter()
	{
		try
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer == null)
			{
				return (characterName: null, worldName: null);
			}
			string item = localPlayer.Name.ToString();
			string item2 = localPlayer.HomeWorld.Value.Name.ExtractText();
			return (characterName: item, worldName: item2);
		}
		catch (Exception ex)
		{
			log.Error("[CompanionIPC] GetCurrentCharacter failed: " + ex.Message);
			return (characterName: null, worldName: null);
		}
	}

	private string? GetCurrentStopPoint()
	{
		try
		{
			return questRotationService.GetCurrentStopPoint()?.DisplayName;
		}
		catch (Exception ex)
		{
			log.Error("[CompanionIPC] GetCurrentStopPoint failed: " + ex.Message);
			return null;
		}
	}

	public void Dispose()
	{
		try
		{
			getProgressProvider.UnregisterFunc();
			getCurrentCharacterProvider.UnregisterFunc();
			getCurrentStopPointProvider.UnregisterFunc();
			getApiVersionProvider.UnregisterFunc();
			startRotationProvider.UnregisterFunc();
			stopRotationProvider.UnregisterFunc();
			getRotationStateProvider.UnregisterFunc();
			getHuntLogCapabilitiesProvider.UnregisterFunc();
			getHuntLogSettingsProvider.UnregisterFunc();
			updateHuntLogSettingsProvider.UnregisterFunc();
			startHuntLogsProvider.UnregisterFunc();
			stopHuntLogsProvider.UnregisterFunc();
			getHuntLogStateProvider.UnregisterFunc();
			log.Information("[CompanionIPC] IPC Providers unregistered");
		}
		catch (Exception ex)
		{
			log.Error("[CompanionIPC] Dispose failed: " + ex.Message);
		}
	}
}
