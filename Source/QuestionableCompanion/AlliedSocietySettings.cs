using System;
using System.Collections.Generic;
using QuestionableCompanion.Models;

namespace QuestionableCompanion;

[Serializable]
public class AlliedSocietySettings
{
	public AlliedSocietyConfiguration RotationConfig { get; set; } = new AlliedSocietyConfiguration();

	public Dictionary<string, AlliedSocietyCharacterStatus> CharacterStatuses { get; set; } = new Dictionary<string, AlliedSocietyCharacterStatus>();

	public Dictionary<string, List<AlliedSocietyProgress>> CharacterProgress { get; set; } = new Dictionary<string, List<AlliedSocietyProgress>>();

	public DateTime LastResetDate { get; set; } = DateTime.MinValue;
}
