namespace QuestionableCompanion.Services;

public sealed record JobStoneGearsetReconciliationResult(JobStoneGearsetReconciliationStatus Status, JobStoneGearsetTarget? Target, JobStoneGearsetDecision? Decision, int StableReads, int MutationAttempts, string Reason)
{
	public bool PersistenceSucceeded
	{
		get
		{
			JobStoneGearsetReconciliationStatus status = Status;
			if ((uint)(status - 4) <= 2u)
			{
				return true;
			}
			return false;
		}
	}

	public bool IsTerminal
	{
		get
		{
			bool flag = PersistenceSucceeded;
			if (!flag)
			{
				JobStoneGearsetReconciliationStatus status = Status;
				bool flag2 = ((status == JobStoneGearsetReconciliationStatus.NotApplicable || (uint)(status - 7) <= 3u) ? true : false);
				flag = flag2;
			}
			return flag;
		}
	}
}
