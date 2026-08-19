using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuestionableCompanion.Services;

internal sealed record RetainerVentureQuestRuntime(Func<CancellationToken, Task> VerifyIdentityAsync, Func<CancellationToken, Task<byte>> ReadNativeStartTownAsync, Func<uint, CancellationToken, Task<bool>> IsQuestCompleteAsync, Func<uint, CancellationToken, Task<bool>> IsQuestAcceptedAsync, Func<bool> IsQuestionableAvailable, Func<bool> IsQuestionableRunning, Func<string?> GetCurrentQuestionableQuestId, Func<CancellationToken, Task<bool>> PrepareCombatJobAsync, Func<string, CancellationToken, Task> PrepareQuestionablePriorityAsync, Func<CancellationToken, Task> RestoreQuestionablePriorityAsync, Func<string, bool> StartSingleQuest, Func<TimeSpan, CancellationToken, Task> DelayAsync, Func<DateTime> UtcNow);
