namespace QuestionableCompanion.Services;

internal static class RetainerMovementPolicy
{
	public static RetainerMovementRequestKind SelectRequest(bool zoneTransition)
	{
		if (!zoneTransition)
		{
			return RetainerMovementRequestKind.CloseTo;
		}
		return RetainerMovementRequestKind.Exact;
	}

	public static RetainerMovementProgress Observe(RetainerMovementRequestKind request, bool crossingInitiated, bool betweenAreas, bool withinTolerance, uint currentTerritory, uint sourceTerritory, uint targetTerritory)
	{
		if (request == RetainerMovementRequestKind.CloseTo)
		{
			return new RetainerMovementProgress(crossingInitiated, (currentTerritory == targetTerritory && withinTolerance) ? RetainerMovementProgressDecision.Complete : RetainerMovementProgressDecision.Continue);
		}
		crossingInitiated = crossingInitiated || betweenAreas || currentTerritory != sourceTerritory;
		if (!crossingInitiated || betweenAreas || currentTerritory == 0 || currentTerritory == sourceTerritory)
		{
			return new RetainerMovementProgress(crossingInitiated, RetainerMovementProgressDecision.Continue);
		}
		return new RetainerMovementProgress(crossingInitiated, (currentTerritory == targetTerritory) ? RetainerMovementProgressDecision.Complete : RetainerMovementProgressDecision.WrongTerritory);
	}
}
