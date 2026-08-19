using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class QuestConfig
{
	public uint QuestId { get; set; }

	public string QuestName { get; set; } = string.Empty;

	public TriggerType TriggerType { get; set; } = TriggerType.OnComplete;

	public SequenceConfig SequenceAfterQuest { get; set; } = new SequenceConfig();

	public string NextCharacter { get; set; } = "auto_next";

	public string AssignedCharacter { get; set; } = string.Empty;
}
