using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Newtonsoft.Json;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion;

[Serializable]
public class Configuration : IPluginConfiguration
{
	private static readonly string[] DefaultChauffeurBlacklist = new string[60]
	{
		"317:7", "757:1:0", "774:3", "245:2", "245:3", "509:1", "510:255:0", "510:255:4", "710:1", "725:255",
		"3860:1", "3860:4:3", "3860:255:0", "737:255", "738:255:1", "3862:255", "514:1", "514:255", "744:1", "782:1",
		"782:2", "787:3", "787:1", "786:1", "799:1", "3865:3", "3865:255", "822:1", "845:1", "850:1",
		"856:1:0", "517:255", "3867:2", "887:1", "890:1", "897:1", "3868:3", "927:1", "3863:2", "3869:1",
		"3869:255", "941:1", "952:2", "1054:255", "955:255", "956:3", "3870:2", "978:255", "982:2", "984:1:2",
		"1005:5", "1005:6", "1005:7", "521:1", "4521:1", "4521:3", "1037:255", "516:255", "3860:255", "402:255:0"
	};

	public int Version { get; set; } = 1;

	public int CompletedInitialSetupVersion { get; set; }

	public int DismissedInitialSetupVersion { get; set; }

	public bool IsConfigWindowMovable { get; set; } = true;

	public bool ShowDebugLogs { get; set; }

	public List<QuestProfile> Profiles { get; set; } = new List<QuestProfile>();

	public string ActiveProfileName { get; set; } = string.Empty;

	public AlliedSocietySettings AlliedSociety { get; set; } = new AlliedSocietySettings();

	public HuntLogSettings HuntLogs { get; set; } = new HuntLogSettings();

	public ClassUnlockSettings ClassUnlocks { get; set; } = new ClassUnlockSettings();

	public bool AutoStartOnLogin { get; set; }

	public bool EnableDryRun { get; set; }

	public int MaxRetryAttempts { get; set; } = 3;

	public int CharacterSwitchDelay { get; set; } = 5;

	public int MaxLogEntries { get; set; } = 100;

	public bool LogToFile { get; set; }

	public ExecutionState LastExecutionState { get; set; } = new ExecutionState();

	public bool RestoreStateOnLoad { get; set; }

	public List<StopPoint> StopPoints { get; set; } = new List<StopPoint>();

	public RotationState LastRotationState { get; set; } = new RotationState();

	public RotationHandoffCheckpoint? RotationHandoff { get; set; }

	[JsonProperty]
	internal RetainerBatchHandoffCheckpoint? RetainerBatchHandoff { get; set; }

	public List<string> SelectedCharactersForRotation { get; set; } = new List<string>();

	public List<string> SelectedCharactersForUI { get; set; } = new List<string>();

	public Dictionary<uint, List<string>> QuestCompletionByCharacter { get; set; } = new Dictionary<uint, List<string>>();

	public Dictionary<string, CharacterJobLevelSnapshot> CharacterJobLevels { get; set; } = new Dictionary<string, CharacterJobLevelSnapshot>();

	public Dictionary<string, uint> QuestRotationCombatJobByCharacter { get; set; } = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, XadbMsqProgressSnapshot> XadbMsqProgressByCharacter { get; set; } = new Dictionary<string, XadbMsqProgressSnapshot>(StringComparer.OrdinalIgnoreCase);

	public RetainerSetupConfiguration RetainerSetup { get; set; } = new RetainerSetupConfiguration();

	public CharacterFilterConfiguration CharacterFilters { get; set; } = new CharacterFilterConfiguration();

	public Dictionary<string, List<string>> EventQuestCompletionByCharacter { get; set; } = new Dictionary<string, List<string>>();

	public string CurrentEventQuestId { get; set; } = string.Empty;

	public List<string> SelectedCharactersForEventQuest { get; set; } = new List<string>();

	public bool RunEventQuestsOnARPostProcess { get; set; }

	public List<string> EventQuestsToRunOnPostProcess { get; set; } = new List<string>();

	public int EventQuestPostProcessTimeoutMinutes { get; set; } = 30;

	public bool EnablePostMoogleMailCheck { get; set; } = true;

	public LifestreamCommandType LifestreamCommand { get; set; }

	public bool EnableSubmarineCheck { get; set; }

	public int SubmarineCheckInterval { get; set; } = 360;

	public int SubmarineReloginCooldown { get; set; } = 120;

	public int SubmarineWaitTime { get; set; } = 30;

	public bool EnableAutoDutyUnsynced { get; set; }

	public int AutoDutyPartySize { get; set; } = 2;

	public float WindowOpacity { get; set; } = 1f;

	public int AutoDutyMaxWaitForParty { get; set; } = 30;

	public int AutoDutyReInviteInterval { get; set; } = 30;

	public int AutoLeaveDelaySeconds { get; set; } = 5;

	public bool EnableStuckRotation { get; set; }

	public int StuckRotationThreshold { get; set; } = 10;

	public int SkippedCharacterRetryCount { get; set; }

	public bool EnableDCTravel { get; set; }

	public string DCTravelWorld { get; set; } = "";

	public bool EnableAutoRepair { get; set; }

	public int RepairThreshold { get; set; } = 50;

	public bool EnableAysDiscard { get; set; }

	public bool EnableMovementMonitor { get; set; }

	public bool EnableFriendshipCirclet { get; set; }

	public int MovementCheckInterval { get; set; } = 5;

	public int MovementStuckThreshold { get; set; } = 120;

	public bool EnableCombatHandling { get; set; }

	public int CombatHPThreshold { get; set; } = 50;

	public CombatHandlingMode StopPointCombatHandlingMode { get; set; }

	public bool EnableStopPointRSR { get; set; } = true;

	public bool EnableStopPointVBM { get; set; }

	public bool EnableStopPointBMRAI { get; set; }

	public string StopPointCombatStartCommands { get; set; } = string.Empty;

	public string StopPointCombatEndCommands { get; set; } = string.Empty;

	public CombatHandlingMode SoloDutyCombatHandlingMode { get; set; }

	public bool EnableSoloDutyRSR { get; set; } = true;

	public bool EnableSoloDutyVBM { get; set; }

	public bool EnableSoloDutyBMRAI { get; set; }

	public string SoloDutyCombatStartCommands { get; set; } = string.Empty;

	public string SoloDutyCombatEndCommands { get; set; } = string.Empty;

	public bool EnableDeathHandling { get; set; }

	public int DeathRespawnDelay { get; set; } = 5;

	public bool LogToDalamud { get; set; }

	public MSQDisplayMode MSQDisplayMode { get; set; } = MSQDisplayMode.Overall;

	public bool ShowPatchVersion { get; set; }

	public string DCTravelDataCenter { get; set; } = "";

	public string DCTravelTargetWorld { get; set; } = "";

	public bool EnableDCTravelFeature { get; set; }

	public bool EnableMultiModeAfterRotation { get; set; }

	public bool ReturnToHomeworldOnStopQuest { get; set; }

	public bool IsHighLevelHelper { get; set; }

	public bool HelperAutomationEnabled { get; set; }

	[JsonIgnore]
	public bool IsHelperAutomationActive
	{
		get
		{
			if (IsHighLevelHelper)
			{
				return HelperAutomationEnabled;
			}
			return false;
		}
	}

	public bool IsQuester { get; set; }

	public List<HighLevelHelperConfig> HighLevelHelpers { get; set; } = new List<HighLevelHelperConfig>();

	public bool ChauffeurModeEnabled { get; set; }

	public float ChauffeurDistanceThreshold { get; set; } = 105f;

	public float ChauffeurStopDistance { get; set; } = 5f;

	public uint ChauffeurMountId { get; set; }

	public string PreferredHelper { get; set; } = "";

	public string AssignedQuester { get; set; } = "";

	public HelperStatus CurrentHelperStatus { get; set; }

	public HelperSelectionMode HelperSelection { get; set; }

	public string ManualHelperName { get; set; } = "";

	public bool AlwaysAutoAcceptInvites { get; set; }

	public bool EnableHelperFollowing { get; set; }

	public float HelperFollowDistance { get; set; } = 100f;

	public int HelperFollowCheckInterval { get; set; } = 5;

	public string AssignedQuesterForFollowing { get; set; } = "";

	public string AssignedHelperForFollowing { get; set; } = "";

	public bool EnableARRPrimalCheck { get; set; }

	[JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
	public List<string> ChauffeurBlacklist { get; set; } = new List<string>(DefaultChauffeurBlacklist);

	public bool QuesterInvitesHelper { get; set; }

	public bool EnableFreeTrialHelperInvite { get; set; }

	public bool EnableSafeWaitBeforeCharacterSwitch { get; set; }

	public bool EnableSafeWaitAfterCharacterSwitch { get; set; }

	public bool EnableQuestPreCheck { get; set; }

	public List<uint>? QuestPreCheckRange { get; set; }

	public string SelectedDatacenter { get; set; } = "NA";

	public Dictionary<string, List<string>> WorldsByDatacenter { get; set; } = new Dictionary<string, List<string>>
	{
		{
			"NA",
			new List<string>
			{
				"Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren", "Behemoth", "Excalibur",
				"Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros", "Balmung", "Brynhildr", "Coeurl", "Diabolos",
				"Goblin", "Malboro", "Mateus", "Zalera", "Halicarnassus", "Maduin", "Marilith", "Seraph"
			}
		},
		{
			"EU",
			new List<string>
			{
				"Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan", "Alpha", "Lich",
				"Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark"
			}
		},
		{
			"JP",
			new List<string>
			{
				"Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon", "Alexander", "Bahamut",
				"Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima", "Anima", "Asura", "Chocobo", "Hades",
				"Ixion", "Masamune", "Pandaemonium", "Titan", "Gaia", "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn",
				"Valefor", "Yojimbo", "Zeromus"
			}
		},
		{
			"OCE",
			new List<string> { "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan" }
		}
	};

	public bool EnableLANHelpers { get; set; }

	public int LANServerPort { get; set; } = 47788;

	public List<string> LANHelperIPs { get; set; } = new List<string>();

	public bool StartLANServer { get; set; }

	public int RepairThresholdPercent { get; set; } = 50;

	public void Save()
	{
		Plugin.PluginInterface.SavePluginConfig(this);
	}

	public bool NormalizeChauffeurBlacklist()
	{
		if (ChauffeurBlacklist == null)
		{
			ChauffeurBlacklist = new List<string>(DefaultChauffeurBlacklist);
			return true;
		}
		List<string> list = (from x in ChauffeurBlacklist
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == ChauffeurBlacklist.Count && list.SequenceEqual<string>(ChauffeurBlacklist, StringComparer.Ordinal))
		{
			return false;
		}
		ChauffeurBlacklist = list;
		return true;
	}

	public QuestProfile? GetActiveProfile()
	{
		return Profiles.Find((QuestProfile p) => p.Name == ActiveProfileName);
	}

	public void EnsureDefaultProfile()
	{
		CharacterJobLevels = ((CharacterJobLevels == null) ? new Dictionary<string, CharacterJobLevelSnapshot>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, CharacterJobLevelSnapshot>(CharacterJobLevels, StringComparer.OrdinalIgnoreCase));
		string[] array = CharacterJobLevels.Keys.ToArray();
		foreach (string key in array)
		{
			CharacterJobLevelSnapshot characterJobLevelSnapshot = CharacterJobLevels[key] ?? new CharacterJobLevelSnapshot();
			CharacterJobLevels[key] = characterJobLevelSnapshot;
			CharacterJobLevelSnapshot characterJobLevelSnapshot2 = characterJobLevelSnapshot;
			if (characterJobLevelSnapshot2.CombatJobLevels == null)
			{
				Dictionary<uint, int> dictionary = (characterJobLevelSnapshot2.CombatJobLevels = new Dictionary<uint, int>());
			}
			characterJobLevelSnapshot2 = characterJobLevelSnapshot;
			if (characterJobLevelSnapshot2.XadbObservedCombatJobLevels == null)
			{
				Dictionary<uint, int> dictionary = (characterJobLevelSnapshot2.XadbObservedCombatJobLevels = new Dictionary<uint, int>());
			}
			characterJobLevelSnapshot2 = characterJobLevelSnapshot;
			if (characterJobLevelSnapshot2.AllClassJobLevels == null)
			{
				Dictionary<uint, int> dictionary = (characterJobLevelSnapshot2.AllClassJobLevels = new Dictionary<uint, int>());
			}
			characterJobLevelSnapshot.VerifiedSoulCrystalItemIds = (characterJobLevelSnapshot.VerifiedSoulCrystalItemIds ?? new List<uint>()).Where((uint itemId) => itemId != 0).Distinct().ToList();
			characterJobLevelSnapshot2 = characterJobLevelSnapshot;
			if (characterJobLevelSnapshot2.JobEvidenceSource == null)
			{
				string text = (characterJobLevelSnapshot2.JobEvidenceSource = string.Empty);
			}
		}
		QuestRotationCombatJobByCharacter = ((QuestRotationCombatJobByCharacter == null) ? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, uint>(QuestRotationCombatJobByCharacter, StringComparer.OrdinalIgnoreCase));
		XadbMsqProgressByCharacter = ((XadbMsqProgressByCharacter == null) ? new Dictionary<string, XadbMsqProgressSnapshot>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, XadbMsqProgressSnapshot>(XadbMsqProgressByCharacter, StringComparer.OrdinalIgnoreCase));
		if (RetainerSetup == null)
		{
			RetainerSetupConfiguration retainerSetupConfiguration = (RetainerSetup = new RetainerSetupConfiguration());
		}
		RetainerSetup.Normalize();
		if (CharacterFilters == null)
		{
			CharacterFilterConfiguration characterFilterConfiguration = (CharacterFilters = new CharacterFilterConfiguration());
		}
		if (CharacterFilters.MigrationVersion < 2)
		{
			CharacterFilters.BelowLevelEnabled = RetainerSetup.FilterBelowLevelEnabled;
			CharacterFilters.BelowLevel = RetainerSetup.FilterBelowLevel;
			CharacterFilters.MissingRetainers = RetainerSetup.FilterIncompleteSetup;
			CharacterFilters.MigrationVersion = 2;
		}
		CharacterFilters.Normalize();
		if (ClassUnlocks == null)
		{
			ClassUnlockSettings classUnlockSettings = (ClassUnlocks = new ClassUnlockSettings());
		}
		ClassUnlocks.Normalize();
		if (AlliedSociety == null)
		{
			AlliedSocietySettings alliedSocietySettings = (AlliedSociety = new AlliedSocietySettings());
		}
		HuntLogSettings huntLogSettings;
		if (HuntLogs == null)
		{
			huntLogSettings = (HuntLogs = new HuntLogSettings());
		}
		huntLogSettings = HuntLogs;
		HuntLogRunCheckpoint huntLogRunCheckpoint;
		if (huntLogSettings.CurrentCheckpoint == null)
		{
			huntLogRunCheckpoint = (huntLogSettings.CurrentCheckpoint = new HuntLogRunCheckpoint());
		}
		huntLogRunCheckpoint = HuntLogs.CurrentCheckpoint;
		if (huntLogRunCheckpoint.SelectedCharacters == null)
		{
			List<string> list = (huntLogRunCheckpoint.SelectedCharacters = new List<string>());
		}
		huntLogRunCheckpoint = HuntLogs.CurrentCheckpoint;
		if (huntLogRunCheckpoint.CompletedCharacters == null)
		{
			List<string> list = (huntLogRunCheckpoint.CompletedCharacters = new List<string>());
		}
		huntLogRunCheckpoint = HuntLogs.CurrentCheckpoint;
		if (huntLogRunCheckpoint.CompletionProvenance == null)
		{
			Dictionary<string, HuntLogCompletionProvenance> dictionary5 = (huntLogRunCheckpoint.CompletionProvenance = new Dictionary<string, HuntLogCompletionProvenance>());
		}
		huntLogRunCheckpoint = HuntLogs.CurrentCheckpoint;
		if (huntLogRunCheckpoint.SkippedCharacters == null)
		{
			List<string> list = (huntLogRunCheckpoint.SkippedCharacters = new List<string>());
		}
		huntLogRunCheckpoint = HuntLogs.CurrentCheckpoint;
		if (huntLogRunCheckpoint.FailedCharacters == null)
		{
			List<string> list = (huntLogRunCheckpoint.FailedCharacters = new List<string>());
		}
		huntLogRunCheckpoint = HuntLogs.CurrentCheckpoint;
		if (huntLogRunCheckpoint.PendingMarks == null)
		{
			List<HuntLogPendingMark> list6 = (huntLogRunCheckpoint.PendingMarks = new List<HuntLogPendingMark>());
		}
		huntLogSettings = HuntLogs;
		if (huntLogSettings.CharacterSnapshots == null)
		{
			Dictionary<string, HuntLogCharacterSnapshot> dictionary7 = (huntLogSettings.CharacterSnapshots = new Dictionary<string, HuntLogCharacterSnapshot>());
		}
		HuntLogs.StopAfterGrandCompanyRank = Math.Clamp(HuntLogs.StopAfterGrandCompanyRank, 1, 11);
		foreach (HuntLogCharacterSnapshot value2 in HuntLogs.CharacterSnapshots.Values)
		{
			if (value2.GrandCompanyRankProvenance == HuntLogCompletionProvenance.Unknown && value2.LastUpdatedUtc != DateTime.MinValue)
			{
				int i = value2.GrandCompanyRank;
				if (i > 0 && i <= 11)
				{
					value2.GrandCompanyRankProvenance = HuntLogCompletionProvenance.LiveInspection;
				}
			}
		}
		foreach (string completedCharacter in HuntLogs.CurrentCheckpoint.CompletedCharacters)
		{
			HuntLogCharacterSnapshot value;
			bool flag = HuntLogs.CharacterSnapshots.TryGetValue(completedCharacter, out value);
			if (flag)
			{
				HuntLogCompletionProvenance grandCompanyRankProvenance = value.GrandCompanyRankProvenance;
				bool flag2 = (uint)(grandCompanyRankProvenance - 2) <= 1u;
				flag = flag2;
			}
			if (flag && value.GrandCompanyRank >= HuntLogs.StopAfterGrandCompanyRank)
			{
				HuntLogs.CurrentCheckpoint.CompletionProvenance[completedCharacter] = value.GrandCompanyRankProvenance;
			}
		}
		HuntLogs.GroundApproachDistance = Math.Clamp(HuntLogs.GroundApproachDistance, 5f, 100f);
		if (HuntLogs.MaxMarkRetries == 3)
		{
			HuntLogs.MaxMarkRetries = 7;
		}
		if (!Enum.IsDefined(typeof(HuntLogCombatJobMode), HuntLogs.CombatJobMode))
		{
			HuntLogs.CombatJobMode = HuntLogCombatJobMode.HighestCombatJob;
		}
		if (HuntLogs.PreferredCombatJobId != 0 && (HuntLogs.PreferredCombatJobId > 255 || !JobClassification.IsCombatJob((byte)HuntLogs.PreferredCombatJobId)))
		{
			HuntLogs.PreferredCombatJobId = 0u;
		}
		if (Profiles.Count == 0)
		{
			QuestProfile questProfile = new QuestProfile
			{
				Name = "Default Profile",
				IsActive = true
			};
			Profiles.Add(questProfile);
			ActiveProfileName = questProfile.Name;
		}
	}
}
