namespace QuestionableCompanion.Services;

public sealed class RetainerNativeRosterGate
{
	private RetainerNativeRosterObservation? lastObservation;

	private int stableReads;

	public RetainerNativeRosterSnapshot? Observe(RetainerNativeRosterObservation observation)
	{
		if (!observation.IsAvailable)
		{
			Reset();
			return null;
		}
		if (lastObservation == null || lastObservation != observation)
		{
			lastObservation = observation;
			stableReads = 1;
		}
		else
		{
			stableReads++;
		}
		if (stableReads < 4)
		{
			return null;
		}
		return observation.ToSnapshot();
	}

	public void Reset()
	{
		lastObservation = null;
		stableReads = 0;
	}
}
