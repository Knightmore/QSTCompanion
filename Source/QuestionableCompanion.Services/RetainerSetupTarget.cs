using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public sealed record RetainerSetupTarget(ulong ContentId, string CharacterKey, XadbRetainerSnapshot XadbSnapshot, CharacterRetainerSetupChoice Choice);
