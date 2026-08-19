using System;

namespace QuestionableCompanion.Services;

internal static class RetainerInitialAssignmentLogic
{
	public const uint VentureTokenCost = 2u;

	public static RetainerInitialAssignmentDecision Decide(int assignedRetainers, int totalRetainers, uint? ventureTokens)
	{
		if (totalRetainers < 0 || assignedRetainers < 0 || assignedRetainers > totalRetainers)
		{
			throw new ArgumentOutOfRangeException("assignedRetainers");
		}
		if (assignedRetainers == totalRetainers)
		{
			return RetainerInitialAssignmentDecision.AllExpectedVenturesAssigned;
		}
		if (ventureTokens.HasValue && ventureTokens.Value < 2)
		{
			return RetainerInitialAssignmentDecision.InsufficientVentureTokens;
		}
		return RetainerInitialAssignmentDecision.ContinueWaiting;
	}
}
