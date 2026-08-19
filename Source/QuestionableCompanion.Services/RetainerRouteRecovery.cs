namespace QuestionableCompanion.Services;

public static class RetainerRouteRecovery
{
	public static RetainerRouteRecoveryDecision Decide(bool transitionActive, uint currentTerritory, uint routeStartTerritory, uint routeTargetTerritory)
	{
		if (transitionActive || currentTerritory == 0)
		{
			return RetainerRouteRecoveryDecision.WaitForTransition;
		}
		if (currentTerritory == routeTargetTerritory)
		{
			return RetainerRouteRecoveryDecision.Arrived;
		}
		if (currentTerritory != routeStartTerritory)
		{
			return RetainerRouteRecoveryDecision.RecalculateFromCurrentTerritory;
		}
		return RetainerRouteRecoveryDecision.ContinueCurrentRoute;
	}
}
