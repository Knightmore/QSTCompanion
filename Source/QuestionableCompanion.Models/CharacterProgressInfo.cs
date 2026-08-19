using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class CharacterProgressInfo
{
	public string World { get; set; } = "Unknown";

	public uint LastQuestId { get; set; }

	public string LastQuestName { get; set; } = "—";

	public int CompletedQuestCount { get; set; }

	public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;

	public uint LastCompletedMSQId { get; set; }

	public string LastCompletedMSQName { get; set; } = "—";

	public int CompletedMSQCount { get; set; }

	public int TotalMSQCount { get; set; }

	public float MSQCompletionPercentage { get; set; }

	public bool HasMSQProgressData { get; set; }

	public bool HasCurrentMSQData { get; set; }

	public bool UsesXadbSummary { get; set; }

	public MsqProgressBasis MSQProgressBasis { get; set; }

	public int HighestCombatJobLevel { get; set; }

	public uint HighestCombatJobId { get; set; }

	public uint GrandCompanyId { get; set; }

	public int GrandCompanyRank { get; set; }

	public XadbRetainerRosterStatus RetainerRosterStatus { get; set; }

	public bool RetainerEvidenceValidated { get; set; }

	public int? RetainerCount { get; set; }

	public int HighestRetainerLevel { get; set; }

	public int? RetainerSetupPercent { get; set; }

	public string RetainerSetupStatus { get; set; } = "Unknown";
}
