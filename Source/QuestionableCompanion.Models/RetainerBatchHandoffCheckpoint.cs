using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

[Serializable]
internal sealed class RetainerBatchHandoffCheckpoint
{
	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; set; } = 1;

	public string BatchId { get; set; } = string.Empty;

	public RetainerBatchFrozenSettings FrozenSettings { get; set; } = new RetainerBatchFrozenSettings();

	public List<RetainerBatchTargetCheckpoint> OrderedTargets { get; set; } = new List<RetainerBatchTargetCheckpoint>();

	public List<ulong> CompletedTargetContentIds { get; set; } = new List<ulong>();

	public List<ulong> ProcessedTargetContentIds { get; set; } = new List<ulong>();

	public List<RetainerBatchQueueEntry> RemainingQueue { get; set; } = new List<RetainerBatchQueueEntry>();

	public List<ulong> SameBatchRequeuedContentIds { get; set; } = new List<ulong>();

	public ulong CurrentTargetContentId { get; set; }

	public string CurrentTargetCharacterKey { get; set; } = string.Empty;

	public RetainerBatchRecoveryStage RecoveryStage { get; set; }

	public RetainerBatchPendingAction PendingAction { get; set; }

	public string PendingRetainerName { get; set; } = string.Empty;

	public bool CancellationRequested { get; set; }

	public bool SuspendedByDisposal { get; set; }

	public int RecoveryCount { get; set; }

	public DateTime RelogCommandIssuedUtc { get; set; } = DateTime.MinValue;

	public DateTime QuestStartCommandIssuedUtc { get; set; } = DateTime.MinValue;

	public DateTime AutoRetainerStartCommandIssuedUtc { get; set; } = DateTime.MinValue;

	public bool QuestionablePriorityIsolationActive { get; set; }

	public string QuestionablePrioritySnapshot { get; set; } = string.Empty;

	public bool QuestionableWasRunningBeforePriorityIsolation { get; set; }

	public string QuestionableQuestBeforePriorityIsolation { get; set; } = string.Empty;

	public string QuestionableIsolatedQuestId { get; set; } = string.Empty;

	public DateTime CreatedUtc { get; set; } = DateTime.MinValue;

	public DateTime UpdatedUtc { get; set; } = DateTime.MinValue;

	public string LastFailureReason { get; set; } = string.Empty;
}
