namespace QuestionableCompanion.Services;

public sealed record CurrentGearsetPersistenceResult(bool Success, int GearsetId, uint ClassJobId, bool Created, string Reason);
