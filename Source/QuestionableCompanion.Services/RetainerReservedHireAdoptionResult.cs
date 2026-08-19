namespace QuestionableCompanion.Services;

internal sealed record RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision Decision, RetainerRosterIdentity? Retainer, string Error);
