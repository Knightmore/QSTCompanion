namespace QuestionableCompanion.Services;

public sealed class RetainerVocateClosureGate
{
	private int stableClosedReads;

	public bool Observe(bool completelyClosed)
	{
		stableClosedReads = (completelyClosed ? (stableClosedReads + 1) : 0);
		return stableClosedReads >= 4;
	}
}
