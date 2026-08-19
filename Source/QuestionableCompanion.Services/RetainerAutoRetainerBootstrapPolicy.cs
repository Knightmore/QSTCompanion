using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal static class RetainerAutoRetainerBootstrapPolicy
{
	public static bool ShouldAttachStarterPlan(RetainerStopAfter stopAfter, bool configured)
	{
		if (!configured)
		{
			return stopAfter == RetainerStopAfter.AutoRetainerBootstrapped;
		}
		return true;
	}
}
