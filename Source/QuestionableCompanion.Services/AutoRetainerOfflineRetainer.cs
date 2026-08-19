namespace QuestionableCompanion.Services;

public sealed record AutoRetainerOfflineRetainer(ulong RetainerId, string Name, bool HasVenture, uint VentureId, long VentureEndsAt, int Level, uint Job);
