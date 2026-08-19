using System.Collections.Generic;

namespace QuestionableCompanion.Models;

public sealed record ClassUnlockRunState(bool IsRunning, ClassUnlockRunPhase Phase, string CurrentCharacter, uint CurrentClassJobId, string CurrentQuestId, string Status, IReadOnlyList<ClassUnlockTargetResult> Results);
