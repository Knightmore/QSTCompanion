using System;

namespace QuestionableCompanion.Services;

public static class RetainerIdentityLogic
{
	public const int RequiredStableReads = 4;

	public static RetainerIdentityObservation Classify(bool isLoggedIn, bool transitionActive, ulong observedContentId, uint observedHomeWorldId, string? observedCharacterKey, ulong expectedContentId, string expectedCharacterKey)
	{
		if (!isLoggedIn || transitionActive || observedContentId == 0L || observedHomeWorldId == 0 || string.IsNullOrWhiteSpace(observedCharacterKey))
		{
			return new RetainerIdentityObservation(RetainerIdentityObservationKind.Unavailable, string.Empty, "identity fields are temporarily unavailable");
		}
		string stableKey = $"{observedContentId}:{observedHomeWorldId}:{observedCharacterKey.Trim()}";
		if (observedContentId == expectedContentId && string.Equals(observedCharacterKey.Trim(), expectedCharacterKey.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return new RetainerIdentityObservation(RetainerIdentityObservationKind.Exact, stableKey, string.Empty);
		}
		return new RetainerIdentityObservation(RetainerIdentityObservationKind.DefinitiveMismatch, stableKey, $"observed {observedCharacterKey} ({observedContentId})");
	}
}
