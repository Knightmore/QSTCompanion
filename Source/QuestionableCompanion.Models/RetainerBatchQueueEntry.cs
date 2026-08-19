using System;

namespace QuestionableCompanion.Models;

[Serializable]
internal sealed class RetainerBatchQueueEntry
{
	public ulong ContentId { get; set; }

	public bool IsRequeue { get; set; }
}
