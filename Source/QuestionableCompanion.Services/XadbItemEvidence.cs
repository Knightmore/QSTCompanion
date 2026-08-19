using System.Collections.Generic;

namespace QuestionableCompanion.Services;

public sealed record XadbItemEvidence(bool IsValid, IReadOnlySet<uint> ItemIds, string FailureReason)
{
	public static XadbItemEvidence Invalid(string reason)
	{
		return new XadbItemEvidence(IsValid: false, new HashSet<uint>(), reason);
	}
}
