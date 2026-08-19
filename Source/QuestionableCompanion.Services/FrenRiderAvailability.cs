namespace QuestionableCompanion.Services;

public sealed record FrenRiderAvailability(FrenRiderAvailabilityKind Kind, string Message)
{
	public bool CanSelect => Kind == FrenRiderAvailabilityKind.Ready;
}
