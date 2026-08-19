namespace QuestionableCompanion.Services;

public sealed record JobStoneTargetResolution(JobStoneTargetResolutionKind Kind, JobStoneGearsetTarget? Target, string Reason);
