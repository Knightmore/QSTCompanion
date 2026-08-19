using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

public class AlliedSocietyCharacterStatus
{
	public required string CharacterId { get; set; }

	public AlliedSocietyRotationStatus Status { get; set; }

	public DateTime? LastCompletionDate { get; set; }

	public List<string> ImportedQuestIds { get; set; } = new List<string>();
}
