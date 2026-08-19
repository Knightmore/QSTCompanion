using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Services;

public class EventQuestState
{
	public EventQuestPhase Phase { get; set; }

	public string EventQuestId { get; set; } = string.Empty;

	public string EventQuestName { get; set; } = string.Empty;

	public List<string> SelectedCharacters { get; set; } = new List<string>();

	public List<string> RemainingCharacters { get; set; } = new List<string>();

	public List<string> CompletedCharacters { get; set; } = new List<string>();

	public string CurrentCharacter { get; set; } = string.Empty;

	public string NextCharacter { get; set; } = string.Empty;

	public List<string> DependencyQuests { get; set; } = new List<string>();

	public string CurrentExecutingQuest { get; set; } = string.Empty;

	public int DependencyIndex { get; set; }

	public DateTime PhaseStartTime { get; set; } = DateTime.Now;

	public DateTime RotationStartTime { get; set; } = DateTime.Now;

	public string ErrorMessage { get; set; } = string.Empty;

	public bool HasEventQuestBeenAccepted { get; set; }
}
