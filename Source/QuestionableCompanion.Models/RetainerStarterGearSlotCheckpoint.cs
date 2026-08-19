using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class RetainerStarterGearSlotCheckpoint
{
	public int ContainerType { get; set; }

	public int Slot { get; set; }

	public uint ItemId { get; set; }
}
