namespace QuestionableCompanion.Services;

public static class RetainerSafeCallbackPolicy
{
	public static bool CanInvoke(RetainerIdentityObservationKind identity, bool safeStateAvailable)
	{
		return identity == RetainerIdentityObservationKind.Exact && safeStateAvailable;
	}
}
