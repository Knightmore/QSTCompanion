using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class CharacterRetainerSetupChoice
{
	public string CharacterKey { get; set; } = string.Empty;

	public RetainerType Type { get; set; }

	public uint CombatStarterClassId { get; set; } = 1u;
}
