namespace QuestionableCompanion.Services;

public sealed record LiveRetainerInfo(ulong RetainerId, string Name, int Level, uint ClassJobId, uint VentureId, long VentureCompleteUnixSeconds);
