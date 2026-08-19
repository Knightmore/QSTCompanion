using System;
using System.Collections.Generic;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public sealed class XadbCharacterProgress
{
	public ulong ContentId { get; init; }

	public string CharacterKey { get; init; } = string.Empty;

	public Dictionary<uint, int> CombatJobLevels { get; set; } = new Dictionary<uint, int>();

	public uint HighestCombatJobId { get; set; }

	public int HighestCombatJobLevel { get; set; }

	public bool HasLevel { get; set; }

	internal Dictionary<uint, int> ObservedCombatJobLevels { get; set; } = new Dictionary<uint, int>();

	internal IReadOnlyDictionary<string, int> ObservedCombatJobLevelsByAbbreviation { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public bool InventoryEvidenceValid { get; set; }

	public IReadOnlySet<uint> VerifiedSoulCrystalItemIds { get; set; } = new HashSet<uint>();

	public string JobEvidenceSource { get; set; } = "XADBWithoutInventoryEvidence";

	public int CompletedMsqCount { get; set; }

	public int TotalMsqCount { get; set; }

	public bool HasMsqProgress { get; set; }

	public uint CurrentMsqId { get; set; }

	public string CurrentMsqName { get; set; } = string.Empty;

	public bool HasCurrentMsq { get; set; }

	public bool HasQuestSnapshotRow { get; set; }

	public DateTime SourceUpdatedUtc { get; set; }

	public XadbRetainerSnapshot RetainerSnapshot { get; set; } = XadbRetainerSnapshot.Unknown("no matching XADB snapshot row", 0uL);
}
