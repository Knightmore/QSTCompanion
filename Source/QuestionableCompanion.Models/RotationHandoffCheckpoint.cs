using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class RotationHandoffCheckpoint
{
	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; set; } = 1;

	public RotationRunMode RunMode { get; set; }

	public ulong ExpectedContentId { get; set; }

	public string ExpectedCharacterKey { get; set; } = string.Empty;

	public bool CombatJobPreparationRequired { get; set; }

	public uint PreferredCombatJobId { get; set; }

	public List<string> SelectedCharacters { get; set; } = new List<string>();

	public List<string> CompletedCharacters { get; set; } = new List<string>();

	public List<string> RemainingCharacters { get; set; } = new List<string>();

	public uint StopQuestId { get; set; }

	public RotationHandoffRecoveryStage RecoveryStage { get; set; }

	public DateTime CreatedUtc { get; set; }

	public DateTime UpdatedUtc { get; set; }

	public DateTime RelogCommandIssuedUtc { get; set; }

	public DateTime QuestStartCommandIssuedUtc { get; set; }
}
