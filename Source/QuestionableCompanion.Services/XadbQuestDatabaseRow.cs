using System;
using System.Collections.Generic;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal sealed class XadbQuestDatabaseRow
{
	public ulong ContentId { get; init; }

	public string CharacterKey { get; init; } = string.Empty;

	public int CompletedMsqCount { get; init; }

	public int TotalMsqCount { get; init; }

	public bool HasMsqProgress { get; init; }

	public uint CurrentMsqId { get; init; }

	public string CurrentMsqName { get; init; } = string.Empty;

	public bool HasCurrentMsq { get; init; }

	public DateTime SourceUpdatedUtc { get; init; }

	public IReadOnlyDictionary<string, int> CombatJobLevelsByAbbreviation { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public XadbItemEvidence ItemEvidence { get; init; } = XadbItemEvidence.Invalid("inventory columns were not read");

	public XadbRetainerSnapshot RetainerSnapshot { get; init; } = XadbRetainerSnapshot.Unknown("retainer columns were not read", 0uL);
}
