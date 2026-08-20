using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class BaselineRetainerCheckpoint
{
	public ulong RetainerId { get; set; }

	public string Name { get; set; } = string.Empty;

	public void Normalize()
	{
		Name = Name?.Trim() ?? string.Empty;
	}
}
