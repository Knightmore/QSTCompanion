using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

[Serializable]
public class HuntLogSettings
{
	public const int MaxLegalGrandCompanyRank = 11;

	private string? selectedMount;

	public bool AutoGrandCompanyRankUp { get; set; } = true;

	public bool AutoSyncFateTargets { get; set; } = true;

	public bool TargetOnlyCombat { get; set; } = true;

	public bool ResumeIncompleteRuns { get; set; } = true;

	public int StopAfterClassRank { get; set; } = 5;

	public int StopAfterGrandCompanyRank { get; set; } = 9;

	public bool SkipDutyMarks { get; set; }

	public bool SoloUnsyncedLogDuty { get; set; } = true;

	public bool ReturnOnceDone { get; set; }

	public HuntLogReturnDestination ReturnDestination { get; set; } = HuntLogReturnDestination.Auto;

	public bool UseMountBetweenMarks { get; set; } = true;

	public string SelectedMount
	{
		get
		{
			return selectedMount ?? "Mount Roulette";
		}
		set
		{
			selectedMount = value;
		}
	}

	[Obsolete("Use SelectedMount instead.")]
	public uint MountId { get; set; }

	public float MountDistance { get; set; } = 40f;

	public float GroundApproachDistance { get; set; } = 35f;

	public bool SummonChocobo { get; set; }

	public string CompanionStance { get; set; } = "Free Stance";

	public HuntLogCombatJobMode CombatJobMode { get; set; }

	public uint PreferredCombatJobId { get; set; }

	public HuntLogCombatMode CombatMode { get; set; }

	public bool EnableRotationSolverReborn { get; set; } = true;

	public bool EnableVBMAI { get; set; }

	public bool EnableBMRAI { get; set; } = true;

	public int MaxMarkRetries { get; set; } = 7;

	public int MovementTimeoutSeconds { get; set; } = 120;

	public int KillTimeoutSeconds { get; set; } = 90;

	public HuntLogRunCheckpoint CurrentCheckpoint { get; set; } = new HuntLogRunCheckpoint();

	public Dictionary<string, HuntLogCharacterSnapshot> CharacterSnapshots { get; set; } = new Dictionary<string, HuntLogCharacterSnapshot>();

	public bool EnsureSelectedMount(Func<uint, string?>? resolveLegacyMountName = null)
	{
		if (!string.IsNullOrWhiteSpace(selectedMount))
		{
			if (MountId == 0)
			{
				return false;
			}
			MountId = 0u;
			return true;
		}
		if (MountId != 0)
		{
			selectedMount = resolveLegacyMountName?.Invoke(MountId);
		}
		if (string.IsNullOrWhiteSpace(selectedMount))
		{
			selectedMount = "Mount Roulette";
		}
		MountId = 0u;
		return true;
	}
}
