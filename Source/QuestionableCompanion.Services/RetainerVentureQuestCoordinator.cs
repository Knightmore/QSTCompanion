using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuestionableCompanion.Services;

internal sealed class RetainerVentureQuestCoordinator
{
	public async Task CompleteAsync(RetainerVentureQuestRuntime runtime, TimeSpan timeout, CancellationToken token)
	{
		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException("timeout");
		}
		await runtime.VerifyIdentityAsync(token);
		if (await IsAnyVentureQuestCompleteAsync(runtime, token))
		{
			await runtime.RestoreQuestionablePriorityAsync(CancellationToken.None);
			return;
		}
		RetainerVentureQuest quest = RetainerVentureQuestContract.Resolve(await runtime.ReadNativeStartTownAsync(token));
		if (!runtime.IsQuestionableAvailable())
		{
			throw new InvalidOperationException("Questionable is unavailable for the venture-unlock quest.");
		}
		if (!(await runtime.PrepareCombatJobAsync(token)))
		{
			throw new InvalidOperationException("No compatible live combat gearset was available for the venture quest.");
		}
		try
		{
			await runtime.PrepareQuestionablePriorityAsync(quest.CanonicalId, token);
			bool flag = await runtime.IsQuestAcceptedAsync(quest.RawId, token);
			bool flag2 = runtime.IsQuestionableRunning() && string.Equals(runtime.GetCurrentQuestionableQuestId(), quest.CanonicalId, StringComparison.OrdinalIgnoreCase);
			if (!flag && !flag2 && !runtime.StartSingleQuest(quest.CanonicalId))
			{
				throw new InvalidOperationException($"Questionable rejected venture-unlock quest {quest.CanonicalId} (native {quest.RawId}).");
			}
			bool startVerified = flag || flag2;
			DateTime startVerificationDeadline = runtime.UtcNow() + TimeSpan.FromSeconds(10L);
			while (!startVerified && runtime.UtcNow() < startVerificationDeadline)
			{
				token.ThrowIfCancellationRequested();
				if (await runtime.IsQuestAcceptedAsync(quest.RawId, token) || (runtime.IsQuestionableRunning() && string.Equals(runtime.GetCurrentQuestionableQuestId(), quest.CanonicalId, StringComparison.OrdinalIgnoreCase)))
				{
					startVerified = true;
					break;
				}
				await runtime.DelayAsync(TimeSpan.FromMilliseconds(250L), token);
			}
			if (!startVerified)
			{
				throw new InvalidOperationException($"Questionable did not confirm venture-unlock quest {quest.CanonicalId} (native {quest.RawId}) within 10 seconds of the isolated start request.");
			}
			DateTime deadline = runtime.UtcNow() + timeout;
			while (true)
			{
				if (runtime.UtcNow() < deadline)
				{
					token.ThrowIfCancellationRequested();
					await runtime.VerifyIdentityAsync(token);
					if (!(await IsAnyVentureQuestCompleteAsync(runtime, token)))
					{
						if (!runtime.IsQuestionableAvailable())
						{
							throw new InvalidOperationException("Questionable became unavailable while completing the venture-unlock quest.");
						}
						string text = runtime.GetCurrentQuestionableQuestId();
						if (runtime.IsQuestionableRunning() && !string.Equals(text, quest.CanonicalId, StringComparison.OrdinalIgnoreCase))
						{
							throw new InvalidOperationException($"Questionable selected unrelated quest {text ?? "(none)"} while venture quest {quest.CanonicalId} owned the isolated priority handoff.");
						}
						await runtime.DelayAsync(TimeSpan.FromMilliseconds(500L), token);
						continue;
					}
					break;
				}
				throw new TimeoutException($"Questionable did not complete native venture-unlock quest {quest.RawId} within {timeout.TotalMinutes:0.#} minutes.");
			}
		}
		finally
		{
			await runtime.RestoreQuestionablePriorityAsync(CancellationToken.None);
		}
	}

	private static async Task<bool> IsAnyVentureQuestCompleteAsync(RetainerVentureQuestRuntime runtime, CancellationToken token)
	{
		foreach (RetainerVentureQuest item in RetainerVentureQuestContract.All)
		{
			if (await runtime.IsQuestCompleteAsync(item.RawId, token))
			{
				return true;
			}
		}
		return false;
	}
}
