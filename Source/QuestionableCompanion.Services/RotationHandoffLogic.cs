using System;
using System.Collections.Generic;
using System.Linq;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class RotationHandoffLogic
{
	public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30L);

	public static readonly TimeSpan RelogRetryInterval = TimeSpan.FromSeconds(30L);

	public static readonly TimeSpan QuestStartRetryInterval = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(2L);

	public static RotationHandoffCheckpoint Create(RotationRunMode runMode, ulong expectedContentId, string expectedCharacterKey, bool combatJobPreparationRequired, uint preferredCombatJobId, IEnumerable<string> selectedCharacters, IEnumerable<string> completedCharacters, IEnumerable<string> remainingCharacters, uint stopQuestId, DateTime nowUtc)
	{
		nowUtc = NormalizeUtc(nowUtc);
		return new RotationHandoffCheckpoint
		{
			SchemaVersion = 1,
			RunMode = runMode,
			ExpectedContentId = expectedContentId,
			ExpectedCharacterKey = (expectedCharacterKey?.Trim() ?? string.Empty),
			CombatJobPreparationRequired = combatJobPreparationRequired,
			PreferredCombatJobId = preferredCombatJobId,
			SelectedCharacters = NormalizeCharacters(selectedCharacters),
			CompletedCharacters = NormalizeCharacters(completedCharacters),
			RemainingCharacters = NormalizeCharacters(remainingCharacters),
			StopQuestId = stopQuestId,
			RecoveryStage = RotationHandoffRecoveryStage.RelogPending,
			CreatedUtc = nowUtc,
			UpdatedUtc = nowUtc
		};
	}

	public static RotationHandoffValidation Validate(RotationHandoffCheckpoint? checkpoint, DateTime nowUtc)
	{
		if (checkpoint == null || checkpoint.SchemaVersion != 1 || !Enum.IsDefined(checkpoint.RunMode) || !Enum.IsDefined(checkpoint.RecoveryStage) || checkpoint.ExpectedContentId == 0L || string.IsNullOrWhiteSpace(checkpoint.ExpectedCharacterKey) || checkpoint.PreferredCombatJobId > 255 || checkpoint.CreatedUtc == DateTime.MinValue || checkpoint.UpdatedUtc == DateTime.MinValue || checkpoint.SelectedCharacters == null || checkpoint.CompletedCharacters == null || checkpoint.RemainingCharacters == null)
		{
			return RotationHandoffValidation.Malformed;
		}
		if (!checkpoint.CombatJobPreparationRequired && checkpoint.PreferredCombatJobId != 0)
		{
			return RotationHandoffValidation.Malformed;
		}
		nowUtc = NormalizeUtc(nowUtc);
		DateTime dateTime = NormalizeUtc(checkpoint.CreatedUtc);
		DateTime dateTime2 = NormalizeUtc(checkpoint.UpdatedUtc);
		if (dateTime > nowUtc + FutureClockTolerance || dateTime2 > nowUtc + FutureClockTolerance || dateTime2 < dateTime)
		{
			return RotationHandoffValidation.Malformed;
		}
		if (nowUtc - dateTime > MaximumAge || nowUtc - dateTime2 > MaximumAge)
		{
			return RotationHandoffValidation.Expired;
		}
		List<string> selected = NormalizeCharacters(checkpoint.SelectedCharacters);
		List<string> list = NormalizeCharacters(checkpoint.CompletedCharacters);
		List<string> list2 = NormalizeCharacters(checkpoint.RemainingCharacters);
		if (selected.Count == 0 || list2.Count == 0 || !selected.Contains<string>(checkpoint.ExpectedCharacterKey, StringComparer.OrdinalIgnoreCase) || !list2.Contains<string>(checkpoint.ExpectedCharacterKey, StringComparer.OrdinalIgnoreCase) || list.Any((string character) => !selected.Contains<string>(character, StringComparer.OrdinalIgnoreCase)) || list2.Any((string character) => !selected.Contains<string>(character, StringComparer.OrdinalIgnoreCase)) || list.Intersect<string>(list2, StringComparer.OrdinalIgnoreCase).Any())
		{
			return RotationHandoffValidation.Malformed;
		}
		if (checkpoint.RunMode == RotationRunMode.Quest != (checkpoint.StopQuestId != 0))
		{
			return RotationHandoffValidation.Malformed;
		}
		return RotationHandoffValidation.Valid;
	}

	public static RotationHandoffResumeAction DecideResumeAction(RotationHandoffCheckpoint? checkpoint, DateTime nowUtc, bool dependenciesReady, bool transitionActive, bool stableWorld, ulong observedContentId, string? observedCharacterKey, bool questStartupObserved)
	{
		switch (Validate(checkpoint, nowUtc))
		{
		case RotationHandoffValidation.Expired:
			return RotationHandoffResumeAction.ClearExpired;
		case RotationHandoffValidation.Malformed:
			return RotationHandoffResumeAction.ClearMalformed;
		default:
			if (!dependenciesReady)
			{
				return RotationHandoffResumeAction.WaitForDependencies;
			}
			if (transitionActive || observedContentId == 0L || string.IsNullOrWhiteSpace(observedCharacterKey))
			{
				return RotationHandoffResumeAction.WaitForDestination;
			}
			if (!IsExactDestination(checkpoint, observedContentId, observedCharacterKey))
			{
				bool num = string.Equals(observedCharacterKey?.Trim(), checkpoint.ExpectedCharacterKey.Trim(), StringComparison.OrdinalIgnoreCase);
				bool flag = observedContentId == checkpoint.ExpectedContentId;
				if (!(num || flag) && checkpoint.RecoveryStage < RotationHandoffRecoveryStage.ExactLoginConfirmed)
				{
					return RotationHandoffResumeAction.WaitForDestination;
				}
				return RotationHandoffResumeAction.ClearIdentityMismatch;
			}
			if (!stableWorld)
			{
				return RotationHandoffResumeAction.WaitForStableWorld;
			}
			if (questStartupObserved)
			{
				return RotationHandoffResumeAction.ClearStartupConfirmed;
			}
			switch (checkpoint.RecoveryStage)
			{
			case RotationHandoffRecoveryStage.RelogPending:
			case RotationHandoffRecoveryStage.WaitingForExactLogin:
			case RotationHandoffRecoveryStage.ExactLoginConfirmed:
				return RotationHandoffResumeAction.ReconstructAtLogin;
			case RotationHandoffRecoveryStage.PreparingCombatJob:
			case RotationHandoffRecoveryStage.CombatJobPrepared:
				return RotationHandoffResumeAction.ReconstructAtJobPreparation;
			case RotationHandoffRecoveryStage.QuestStartRequested:
				return RotationHandoffResumeAction.ReconstructAtQuestStartup;
			default:
				return RotationHandoffResumeAction.ClearMalformed;
			}
		}
	}

	public static bool IsExactDestination(RotationHandoffCheckpoint checkpoint, ulong observedContentId, string? observedCharacterKey)
	{
		if (observedContentId == checkpoint.ExpectedContentId)
		{
			return string.Equals(observedCharacterKey?.Trim(), checkpoint.ExpectedCharacterKey.Trim(), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool ShouldIssueRecoveryRelog(RotationHandoffCheckpoint checkpoint, DateTime nowUtc, bool exactDestination, bool transitionActive, bool externalBusy)
	{
		if (exactDestination || transitionActive || externalBusy || checkpoint.RecoveryStage >= RotationHandoffRecoveryStage.ExactLoginConfirmed)
		{
			return false;
		}
		nowUtc = NormalizeUtc(nowUtc);
		DateTime dateTime = NormalizeUtc(checkpoint.RelogCommandIssuedUtc);
		if (!(checkpoint.RelogCommandIssuedUtc == DateTime.MinValue))
		{
			return nowUtc - dateTime >= RelogRetryInterval;
		}
		return nowUtc - NormalizeUtc(checkpoint.CreatedUtc) >= TimeSpan.FromSeconds(2L);
	}

	public static bool ShouldIssueQuestStart(RotationHandoffCheckpoint? checkpoint, DateTime nowUtc, bool exactDestination, bool combatJobPrepared, bool questStartupObserved)
	{
		if (!exactDestination || !combatJobPrepared || questStartupObserved)
		{
			return false;
		}
		if (checkpoint == null || checkpoint.QuestStartCommandIssuedUtc == DateTime.MinValue)
		{
			return true;
		}
		return NormalizeUtc(nowUtc) - NormalizeUtc(checkpoint.QuestStartCommandIssuedUtc) >= QuestStartRetryInterval;
	}

	public static bool ShouldClearForLifecycle(RotationHandoffLifecycleEvent lifecycleEvent)
	{
		return lifecycleEvent != RotationHandoffLifecycleEvent.Disposal;
	}

	private static List<string> NormalizeCharacters(IEnumerable<string>? characters)
	{
		return (from character in characters ?? Array.Empty<string>()
			where !string.IsNullOrWhiteSpace(character)
			select character.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static DateTime NormalizeUtc(DateTime value)
	{
		return value.Kind switch
		{
			DateTimeKind.Utc => value, 
			DateTimeKind.Local => value.ToUniversalTime(), 
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc), 
		};
	}
}
