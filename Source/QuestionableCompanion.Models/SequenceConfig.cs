using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class SequenceConfig
{
	public SequenceType Type { get; set; }

	public string Value { get; set; } = string.Empty;

	public bool WaitForCompletion { get; set; } = true;
}
