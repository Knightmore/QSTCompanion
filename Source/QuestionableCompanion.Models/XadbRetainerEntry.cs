namespace QuestionableCompanion.Models;

public sealed record XadbRetainerEntry(ulong RetainerId, ulong OwnerContentId, string Name, int Level, uint ClassJobId, uint VentureId, long VentureCompleteUnixSeconds);
