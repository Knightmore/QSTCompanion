namespace QuestionableCompanion.Services;

internal sealed record RetainerNativeCapacityPlan(bool IsValid, int IntendedCount, int RemainingHires, int OpenSlots, string Error);
