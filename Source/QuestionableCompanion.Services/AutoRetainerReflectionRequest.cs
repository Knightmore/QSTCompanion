using System.Collections.Generic;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal sealed record AutoRetainerReflectionRequest(ulong ContentId, string CharacterKey, RetainerType Type, IReadOnlyList<AutoRetainerExpectedRetainer> Retainers, IReadOnlyList<AutoRetainerExpectedRetainer> BaselineRetainers, bool AttachStarterPlan, bool EnableCharacter, bool EnableRetainers);
