using System;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class XadbMsqProgressSnapshot
{
	public int CompletedMsqCount { get; set; }

	public int TotalMsqCount { get; set; }

	public uint CurrentMsqId { get; set; }

	public string CurrentMsqName { get; set; } = string.Empty;

	public bool HasMsqProgress { get; set; }

	public bool HasCurrentMsq { get; set; }

	public MsqProgressBasis ProgressBasis { get; set; } = MsqProgressBasis.XadbMilestones;

	public DateTime SourceUpdatedUtc { get; set; } = DateTime.MinValue;
}
