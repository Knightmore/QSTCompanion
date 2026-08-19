using System.Numerics;

namespace QuestionableCompanion.Services;

public class StepData
{
	public required string QuestId { get; init; }

	public required byte Sequence { get; init; }

	public required int Step { get; init; }

	public required string InteractionType { get; init; }

	public Vector3? Position { get; init; }

	public ushort TerritoryId { get; init; }
}
