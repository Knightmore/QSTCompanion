namespace QuestionableCompanion.Services;

public sealed class RetainerVocateUiActionGate
{
	private long frameworkTick;

	private long lastActionTick = -1L;

	public void Reset()
	{
		frameworkTick = 0L;
		lastActionTick = -1L;
	}

	public void AdvanceFrameworkTick()
	{
		frameworkTick++;
	}

	public bool TryBeginAction()
	{
		if (lastActionTick == frameworkTick)
		{
			return false;
		}
		lastActionTick = frameworkTick;
		return true;
	}
}
