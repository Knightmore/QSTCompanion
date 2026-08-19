namespace QuestionableCompanion.Services;

public readonly record struct HuntDutyPollResult(bool Succeeded, bool IsStopped, HuntDutyBackend Backend, string Blocker);
