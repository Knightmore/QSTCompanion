using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class HuntLogCharacterSnapshot
{
	public string CharacterName { get; set; } = string.Empty;

	public uint ClassJobId { get; set; }

	public uint SelectedCombatJobId { get; set; }

	public int SelectedCombatGearsetId { get; set; } = -1;

	public int Level { get; set; }

	public int ClassLogRank { get; set; }

	public uint GrandCompanyId { get; set; }

	public int GrandCompanyRank { get; set; } = -1;

	public HuntLogCompletionProvenance GrandCompanyRankProvenance { get; set; }

	public int GrandCompanyLogRank { get; set; }

	public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;
}
