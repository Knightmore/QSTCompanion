using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

public class HuntLogAutomationState
{
	public HuntLogPhase Phase { get; set; }

	public HuntLogMode Mode { get; set; } = HuntLogMode.All;

	public string CurrentCharacter { get; set; } = string.Empty;

	public string CurrentStep { get; set; } = string.Empty;

	public string CurrentMarkName { get; set; } = string.Empty;

	public string DutyBackend { get; set; } = string.Empty;

	public string DutyBlocker { get; set; } = string.Empty;

	public uint CurrentCombatJobId { get; set; }

	public uint SelectedCombatJobId { get; set; }

	public string CurrentCombatJobLabel { get; set; } = string.Empty;

	public string SelectedCombatJobLabel { get; set; } = string.Empty;

	public int CurrentRank { get; set; }

	public List<string> SelectedCharacters { get; set; } = new List<string>();

	public List<string> RemainingCharacters { get; set; } = new List<string>();

	public List<string> CompletedCharacters { get; set; } = new List<string>();

	public Dictionary<string, HuntLogCompletionProvenance> CompletionProvenance { get; set; } = new Dictionary<string, HuntLogCompletionProvenance>();

	public List<string> SkippedCharacters { get; set; } = new List<string>();

	public List<string> FailedCharacters { get; set; } = new List<string>();

	public List<HuntLogPendingMark> PendingMarks { get; set; } = new List<HuntLogPendingMark>();

	public Dictionary<string, string> CharacterStatuses { get; set; } = new Dictionary<string, string>();

	public string ErrorMessage { get; set; } = string.Empty;

	public DateTime StartedAtUtc { get; set; } = DateTime.MinValue;

	public HuntLogAutomationState Clone()
	{
		return new HuntLogAutomationState
		{
			Phase = Phase,
			Mode = Mode,
			CurrentCharacter = CurrentCharacter,
			CurrentStep = CurrentStep,
			CurrentMarkName = CurrentMarkName,
			DutyBackend = DutyBackend,
			DutyBlocker = DutyBlocker,
			CurrentCombatJobId = CurrentCombatJobId,
			SelectedCombatJobId = SelectedCombatJobId,
			CurrentCombatJobLabel = CurrentCombatJobLabel,
			SelectedCombatJobLabel = SelectedCombatJobLabel,
			CurrentRank = CurrentRank,
			SelectedCharacters = new List<string>(SelectedCharacters),
			RemainingCharacters = new List<string>(RemainingCharacters),
			CompletedCharacters = new List<string>(CompletedCharacters),
			CompletionProvenance = new Dictionary<string, HuntLogCompletionProvenance>(CompletionProvenance, StringComparer.OrdinalIgnoreCase),
			SkippedCharacters = new List<string>(SkippedCharacters),
			FailedCharacters = new List<string>(FailedCharacters),
			PendingMarks = PendingMarks.ConvertAll((HuntLogPendingMark x) => x.Clone()),
			CharacterStatuses = new Dictionary<string, string>(CharacterStatuses),
			ErrorMessage = ErrorMessage,
			StartedAtUtc = StartedAtUtc
		};
	}
}
