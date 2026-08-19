using System.Collections.Generic;

namespace QuestionableCompanion.Services;

public sealed record CombatJobResolution(IReadOnlyDictionary<uint, int> Levels, uint HighestJobId, int HighestLevel)
{
	public static CombatJobResolution Empty { get; } = new CombatJobResolution(new Dictionary<uint, int>(), 0u, 0);
}
