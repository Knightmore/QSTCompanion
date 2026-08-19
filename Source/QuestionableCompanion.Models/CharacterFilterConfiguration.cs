using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class CharacterFilterConfiguration
{
	public const int CurrentMigrationVersion = 2;

	public int MigrationVersion { get; set; }

	public string DataCenter { get; set; } = "All";

	public string World { get; set; } = "All";

	public bool BelowGrandCompanyRank9 { get; set; }

	public bool AboveLevelEnabled { get; set; }

	public int AboveLevel { get; set; } = 3;

	public bool BelowLevelEnabled { get; set; }

	public int BelowLevel { get; set; } = 100;

	public bool MissingRetainers { get; set; }

	public uint ClassJobId { get; set; }

	public ClassUnlockFilterStatus ClassUnlockStatus { get; set; }

	public bool HasActiveLevelRange
	{
		get
		{
			if (!AboveLevelEnabled)
			{
				return BelowLevelEnabled;
			}
			return true;
		}
	}

	public void Normalize()
	{
		DataCenter = (string.IsNullOrWhiteSpace(DataCenter) ? "All" : DataCenter.Trim());
		World = (string.IsNullOrWhiteSpace(World) ? "All" : World.Trim());
		AboveLevel = Math.Clamp(AboveLevel, 0, 100);
		BelowLevel = Math.Clamp(BelowLevel, 1, 100);
		if (!Enum.IsDefined(ClassUnlockStatus))
		{
			ClassUnlockStatus = ClassUnlockFilterStatus.All;
		}
		MigrationVersion = Math.Max(0, MigrationVersion);
	}

	public void Reset()
	{
		DataCenter = "All";
		World = "All";
		BelowGrandCompanyRank9 = false;
		AboveLevelEnabled = false;
		AboveLevel = 3;
		BelowLevelEnabled = false;
		BelowLevel = 100;
		MissingRetainers = false;
		ClassJobId = 0u;
		ClassUnlockStatus = ClassUnlockFilterStatus.All;
		MigrationVersion = 2;
	}
}
