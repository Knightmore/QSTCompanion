using System;
using System.Collections.Generic;

namespace QuestionableCompanion;

[Serializable]
public class CharacterJobLevelSnapshot
{
	public int JobEvidenceVersion { get; set; }

	public int HighestCombatJobLevel { get; set; }

	public uint HighestCombatJobId { get; set; }

	public uint GrandCompanyId { get; set; }

	public int GrandCompanyRank { get; set; }

	public Dictionary<uint, int> CombatJobLevels { get; set; } = new Dictionary<uint, int>();

	public Dictionary<uint, int> XadbObservedCombatJobLevels { get; set; } = new Dictionary<uint, int>();

	public Dictionary<uint, int> AllClassJobLevels { get; set; } = new Dictionary<uint, int>();

	public bool HasAllClassJobLevels { get; set; }

	public DateTime AllClassJobLevelsUpdatedUtc { get; set; } = DateTime.MinValue;

	public DateTime XadbObservedCombatJobLevelsUpdatedUtc { get; set; } = DateTime.MinValue;

	public bool InventoryEvidenceValid { get; set; }

	public List<uint> VerifiedSoulCrystalItemIds { get; set; } = new List<uint>();

	public string JobEvidenceSource { get; set; } = string.Empty;

	public DateTime JobEvidenceUpdatedUtc { get; set; } = DateTime.MinValue;

	public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;
}
