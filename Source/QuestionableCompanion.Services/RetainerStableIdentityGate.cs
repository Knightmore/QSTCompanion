using System;

namespace QuestionableCompanion.Services;

public sealed class RetainerStableIdentityGate
{
	private string lastKey = string.Empty;

	private RetainerIdentityObservationKind lastKind;

	private int stableReads;

	public RetainerIdentityObservationKind Observe(RetainerIdentityObservation observation)
	{
		if (observation.Kind == RetainerIdentityObservationKind.Unavailable)
		{
			Reset();
			return RetainerIdentityObservationKind.Unavailable;
		}
		if (observation.Kind != lastKind || !string.Equals(observation.StableKey, lastKey, StringComparison.OrdinalIgnoreCase))
		{
			lastKind = observation.Kind;
			lastKey = observation.StableKey;
			stableReads = 1;
		}
		else
		{
			stableReads++;
		}
		if (stableReads < 4)
		{
			return RetainerIdentityObservationKind.Unavailable;
		}
		return observation.Kind;
	}

	public void Reset()
	{
		lastKey = string.Empty;
		lastKind = RetainerIdentityObservationKind.Unavailable;
		stableReads = 0;
	}
}
