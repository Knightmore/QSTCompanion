using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class ExecutionState
{
	public string ActiveProfile { get; set; } = string.Empty;

	public string CurrentCharacter { get; set; } = string.Empty;

	public uint CurrentQuestId { get; set; }

	public string CurrentQuestName { get; set; } = string.Empty;

	public string CurrentSequence { get; set; } = string.Empty;

	public ExecutionStatus Status { get; set; }

	public int Progress { get; set; }

	public DateTime LastUpdate { get; set; } = DateTime.Now;
}
