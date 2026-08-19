using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

[Serializable]
public class HuntLogRunCheckpoint
{
	public bool IsActive { get; set; }

	public HuntLogMode Mode { get; set; } = HuntLogMode.All;

	public List<string> SelectedCharacters { get; set; } = new List<string>();

	public List<string> CompletedCharacters { get; set; } = new List<string>();

	public Dictionary<string, HuntLogCompletionProvenance> CompletionProvenance { get; set; } = new Dictionary<string, HuntLogCompletionProvenance>();

	public List<string> SkippedCharacters { get; set; } = new List<string>();

	public List<string> FailedCharacters { get; set; } = new List<string>();

	public List<HuntLogPendingMark> PendingMarks { get; set; } = new List<HuntLogPendingMark>();

	public string CurrentCharacter { get; set; } = string.Empty;

	public string LastError { get; set; } = string.Empty;

	public DateTime StartedAtUtc { get; set; } = DateTime.MinValue;

	public DateTime UpdatedAtUtc { get; set; } = DateTime.MinValue;

	public DateTime CompletedAtUtc { get; set; } = DateTime.MinValue;
}
