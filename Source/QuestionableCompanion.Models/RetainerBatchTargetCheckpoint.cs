using System;

namespace QuestionableCompanion.Models;

[Serializable]
internal sealed class RetainerBatchTargetCheckpoint
{
	public ulong ContentId { get; set; }

	public string CharacterKey { get; set; } = string.Empty;

	public CharacterRetainerSetupChoice Choice { get; set; } = new CharacterRetainerSetupChoice();

	public DateTime XadbBaselineUpdatedUtc { get; set; } = DateTime.MinValue;

	public bool AllowSameBatchRequeue { get; set; } = true;
}
