namespace QuestionableCompanion.Services;

public readonly record struct HuntDutyStartResult(bool Started, HuntDutyBackend Backend, string Blocker);
