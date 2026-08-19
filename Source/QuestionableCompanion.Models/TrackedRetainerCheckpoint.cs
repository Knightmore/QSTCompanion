using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class TrackedRetainerCheckpoint
{
	public ulong RetainerId { get; set; }

	public string Name { get; set; } = string.Empty;

	public int CompletedWorkUnits { get; set; }

	public uint ExpectedFirstVentureId { get; set; }

	public void Normalize()
	{
		Name = Name?.Trim() ?? string.Empty;
		CompletedWorkUnits = Math.Clamp(CompletedWorkUnits, 0, 5);
	}
}
