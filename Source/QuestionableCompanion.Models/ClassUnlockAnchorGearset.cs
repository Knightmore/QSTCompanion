using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class ClassUnlockAnchorGearset
{
	public int GearsetId { get; set; } = -1;

	public uint ClassJobId { get; set; }

	public DateTime VerifiedUtc { get; set; } = DateTime.MinValue;
}
