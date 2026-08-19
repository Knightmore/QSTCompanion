using System;

namespace QuestionableCompanion.Services;

public sealed record XadbFreshnessEvidence(bool IsValid, DateTime SavedAtUtc, DateTime LastRefreshUtc, string FailureReason)
{
	public static XadbFreshnessEvidence Invalid(string reason)
	{
		return new XadbFreshnessEvidence(IsValid: false, DateTime.MinValue, DateTime.MinValue, reason);
	}
}
