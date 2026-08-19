namespace QuestionableCompanion.Services;

internal sealed record RetainerRecoveryRuntimeObservation(RetainerIdentityObservation Identity, ulong ObservedContentId, string ObservedCharacterKey, bool TransitionActive, bool StableWorldAvailable);
