using System.Collections.Generic;

namespace QuestionableCompanion.Services;

internal sealed record AutoRetainerCharacterSnapshot(ulong ContentId, string Name, string HomeWorld, bool Enabled, uint GrandCompanyRank, IReadOnlyList<int> ClassJobLevels, bool StarterPlansConfigured, bool ExactRetainersSelected, IReadOnlyList<AutoRetainerOfflineRetainer> Retainers)
{
	public string CharacterKey => Name + "@" + HomeWorld;
}
