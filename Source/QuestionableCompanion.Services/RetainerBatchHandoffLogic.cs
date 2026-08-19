using System;
using System.Collections.Generic;
using System.Linq;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal static class RetainerBatchHandoffLogic
{
	public static readonly TimeSpan MaximumInactivity = TimeSpan.FromMinutes(30L);

	public static readonly TimeSpan RelogRetryInterval = TimeSpan.FromSeconds(30L);

	public static readonly TimeSpan ConsequentialActionRetryInterval = TimeSpan.FromSeconds(10L);

	private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(2L);

	public static RetainerBatchHandoffCheckpoint Create(RetainerBatchFrozenSettings frozenSettings, IEnumerable<RetainerBatchTargetCheckpoint> targets, DateTime nowUtc)
	{
		nowUtc = NormalizeUtc(nowUtc);
		List<RetainerBatchTargetCheckpoint> list = (targets ?? Array.Empty<RetainerBatchTargetCheckpoint>()).Select(CloneTarget).ToList();
		return new RetainerBatchHandoffCheckpoint
		{
			SchemaVersion = 1,
			BatchId = Guid.NewGuid().ToString("D"),
			FrozenSettings = CloneSettings(frozenSettings),
			OrderedTargets = list,
			RemainingQueue = list.Select((RetainerBatchTargetCheckpoint target) => new RetainerBatchQueueEntry
			{
				ContentId = target.ContentId
			}).ToList(),
			RecoveryStage = RetainerBatchRecoveryStage.Created,
			CreatedUtc = nowUtc,
			UpdatedUtc = nowUtc
		};
	}

	public static RetainerBatchHandoffValidation Validate(RetainerBatchHandoffCheckpoint? checkpoint, DateTime nowUtc)
	{
		if (checkpoint == null || checkpoint.SchemaVersion != 1 || !Guid.TryParse(checkpoint.BatchId, out var _) || checkpoint.FrozenSettings == null || checkpoint.OrderedTargets == null || checkpoint.CompletedTargetContentIds == null || checkpoint.ProcessedTargetContentIds == null || checkpoint.RemainingQueue == null || checkpoint.SameBatchRequeuedContentIds == null || !Enum.IsDefined(checkpoint.RecoveryStage) || !Enum.IsDefined(checkpoint.PendingAction) || checkpoint.CreatedUtc == DateTime.MinValue || checkpoint.UpdatedUtc == DateTime.MinValue || !SettingsAreValid(checkpoint.FrozenSettings))
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		nowUtc = NormalizeUtc(nowUtc);
		DateTime dateTime = NormalizeUtc(checkpoint.CreatedUtc);
		DateTime dateTime2 = NormalizeUtc(checkpoint.UpdatedUtc);
		if (dateTime > nowUtc + FutureClockTolerance || dateTime2 > nowUtc + FutureClockTolerance || dateTime2 < dateTime)
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		if (nowUtc - dateTime2 > MaximumInactivity)
		{
			return RetainerBatchHandoffValidation.Expired;
		}
		List<RetainerBatchTargetCheckpoint> orderedTargets = checkpoint.OrderedTargets;
		if (orderedTargets.Count == 0 || orderedTargets.Any((RetainerBatchTargetCheckpoint target) => !TargetIsValid(target)) || orderedTargets.Select((RetainerBatchTargetCheckpoint target) => target.ContentId).Distinct().Count() != orderedTargets.Count)
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		HashSet<ulong> targetIds = orderedTargets.Select((RetainerBatchTargetCheckpoint target) => target.ContentId).ToHashSet();
		if (checkpoint.CompletedTargetContentIds.Any((ulong contentId) => !targetIds.Contains(contentId)) || checkpoint.ProcessedTargetContentIds.Any((ulong contentId) => !targetIds.Contains(contentId)) || checkpoint.SameBatchRequeuedContentIds.Any((ulong contentId) => !targetIds.Contains(contentId)) || checkpoint.RemainingQueue.Any((RetainerBatchQueueEntry entry) => entry == null || !targetIds.Contains(entry.ContentId)) || checkpoint.CompletedTargetContentIds.Distinct().Count() != checkpoint.CompletedTargetContentIds.Count || checkpoint.ProcessedTargetContentIds.Distinct().Count() != checkpoint.ProcessedTargetContentIds.Count || checkpoint.SameBatchRequeuedContentIds.Distinct().Count() != checkpoint.SameBatchRequeuedContentIds.Count)
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		HashSet<ulong> completedIds = checkpoint.CompletedTargetContentIds.ToHashSet();
		HashSet<ulong> processedIds = checkpoint.ProcessedTargetContentIds.ToHashSet();
		HashSet<ulong> requeuedIds = checkpoint.SameBatchRequeuedContentIds.ToHashSet();
		if (!completedIds.IsSubsetOf(processedIds) || !requeuedIds.IsSubsetOf(processedIds) || (from entry in checkpoint.RemainingQueue
			group entry by entry.ContentId).Any((IGrouping<ulong, RetainerBatchQueueEntry> group) => group.Count() != 1) || checkpoint.RemainingQueue.Any((RetainerBatchQueueEntry entry) => completedIds.Contains(entry.ContentId) || entry.IsRequeue != (processedIds.Contains(entry.ContentId) && requeuedIds.Contains(entry.ContentId))))
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		if (!TimestampIsValid(checkpoint.RelogCommandIssuedUtc, dateTime, nowUtc) || !TimestampIsValid(checkpoint.QuestStartCommandIssuedUtc, dateTime, nowUtc) || !TimestampIsValid(checkpoint.AutoRetainerStartCommandIssuedUtc, dateTime, nowUtc))
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		if (checkpoint.CurrentTargetContentId == 0L)
		{
			if (!string.IsNullOrWhiteSpace(checkpoint.CurrentTargetCharacterKey) || !string.IsNullOrWhiteSpace(checkpoint.PendingRetainerName))
			{
				return RetainerBatchHandoffValidation.Malformed;
			}
		}
		else
		{
			RetainerBatchTargetCheckpoint retainerBatchTargetCheckpoint = orderedTargets.SingleOrDefault((RetainerBatchTargetCheckpoint target) => target.ContentId == checkpoint.CurrentTargetContentId);
			if (retainerBatchTargetCheckpoint == null || !string.Equals(retainerBatchTargetCheckpoint.CharacterKey, checkpoint.CurrentTargetCharacterKey, StringComparison.OrdinalIgnoreCase) || checkpoint.RemainingQueue.Count == 0 || checkpoint.RemainingQueue[0].ContentId != checkpoint.CurrentTargetContentId)
			{
				return RetainerBatchHandoffValidation.Malformed;
			}
		}
		if (checkpoint.CancellationRequested && checkpoint.RecoveryStage != RetainerBatchRecoveryStage.Cancelling)
		{
			return RetainerBatchHandoffValidation.Malformed;
		}
		return RetainerBatchHandoffValidation.Valid;
	}

	public static RetainerBatchResumeAction DecideResumeAction(RetainerBatchHandoffCheckpoint? checkpoint, DateTime nowUtc, bool dependenciesReady, bool transitionActive, ulong observedContentId, string? observedCharacterKey, ulong mappedTargetContentId, bool externalBusy)
	{
		switch (Validate(checkpoint, nowUtc))
		{
		case RetainerBatchHandoffValidation.Expired:
			return RetainerBatchResumeAction.ClearExpired;
		default:
			if (checkpoint != null)
			{
				if (!dependenciesReady)
				{
					return RetainerBatchResumeAction.WaitForDependencies;
				}
				if (checkpoint.CancellationRequested)
				{
					return RetainerBatchResumeAction.ResumeCancellationCleanup;
				}
				if (checkpoint.CurrentTargetContentId == 0L)
				{
					return RetainerBatchResumeAction.ContinueExactTarget;
				}
				if (mappedTargetContentId != 0L && mappedTargetContentId != checkpoint.CurrentTargetContentId)
				{
					return RetainerBatchResumeAction.ClearIdentityMismatch;
				}
				if (transitionActive || observedContentId == 0L || string.IsNullOrWhiteSpace(observedCharacterKey))
				{
					return RetainerBatchResumeAction.WaitForTransition;
				}
				if (IsExactTarget(checkpoint, observedContentId, observedCharacterKey))
				{
					return RetainerBatchResumeAction.ContinueExactTarget;
				}
				if (string.Equals(observedCharacterKey?.Trim(), checkpoint.CurrentTargetCharacterKey.Trim(), StringComparison.OrdinalIgnoreCase) || checkpoint.RecoveryStage >= RetainerBatchRecoveryStage.ExactLoginConfirmed)
				{
					return RetainerBatchResumeAction.ClearIdentityMismatch;
				}
				if (!ShouldIssueRecoveryRelog(checkpoint, nowUtc, exactTarget: false, transitionActive, externalBusy))
				{
					return RetainerBatchResumeAction.WaitForRelogCooldown;
				}
				return RetainerBatchResumeAction.IssueRelog;
			}
			goto case RetainerBatchHandoffValidation.Malformed;
		case RetainerBatchHandoffValidation.Malformed:
			return RetainerBatchResumeAction.ClearMalformed;
		}
	}

	public static bool IsExactTarget(RetainerBatchHandoffCheckpoint checkpoint, ulong observedContentId, string? observedCharacterKey)
	{
		if (checkpoint.CurrentTargetContentId != 0L && observedContentId == checkpoint.CurrentTargetContentId)
		{
			return string.Equals(observedCharacterKey?.Trim(), checkpoint.CurrentTargetCharacterKey.Trim(), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool ShouldIssueRecoveryRelog(RetainerBatchHandoffCheckpoint checkpoint, DateTime nowUtc, bool exactTarget, bool transitionActive, bool externalBusy)
	{
		if (exactTarget || transitionActive || externalBusy || checkpoint.CancellationRequested || checkpoint.RecoveryStage >= RetainerBatchRecoveryStage.ExactLoginConfirmed)
		{
			return false;
		}
		if (checkpoint.RelogCommandIssuedUtc == DateTime.MinValue)
		{
			return true;
		}
		return NormalizeUtc(nowUtc) - NormalizeUtc(checkpoint.RelogCommandIssuedUtc) >= RelogRetryInterval;
	}

	public static bool ShouldRetryConsequentialAction(DateTime issuedUtc, DateTime nowUtc)
	{
		if (!(issuedUtc == DateTime.MinValue))
		{
			return NormalizeUtc(nowUtc) - NormalizeUtc(issuedUtc) >= ConsequentialActionRetryInterval;
		}
		return true;
	}

	public static bool ShouldClearForLifecycle(RetainerBatchLifecycleEvent lifecycleEvent)
	{
		return lifecycleEvent != RetainerBatchLifecycleEvent.Disposal;
	}

	public static RetainerBatchTargetCheckpoint? FindTarget(RetainerBatchHandoffCheckpoint checkpoint, ulong contentId)
	{
		return checkpoint.OrderedTargets.FirstOrDefault((RetainerBatchTargetCheckpoint target) => target.ContentId == contentId);
	}

	public static bool AdvanceQueueAfterAttempt(RetainerBatchHandoffCheckpoint checkpoint, ulong contentId, bool completedSuccessfully, bool requeue)
	{
		if (checkpoint.RemainingQueue.Count == 0 || checkpoint.RemainingQueue[0].ContentId != contentId)
		{
			return false;
		}
		checkpoint.RemainingQueue.RemoveAt(0);
		if (!checkpoint.ProcessedTargetContentIds.Contains(contentId))
		{
			checkpoint.ProcessedTargetContentIds.Add(contentId);
		}
		if (completedSuccessfully && !checkpoint.CompletedTargetContentIds.Contains(contentId))
		{
			checkpoint.CompletedTargetContentIds.Add(contentId);
		}
		if (requeue)
		{
			checkpoint.RemainingQueue.Add(new RetainerBatchQueueEntry
			{
				ContentId = contentId,
				IsRequeue = true
			});
			if (!checkpoint.SameBatchRequeuedContentIds.Contains(contentId))
			{
				checkpoint.SameBatchRequeuedContentIds.Add(contentId);
			}
		}
		checkpoint.CurrentTargetContentId = 0uL;
		checkpoint.CurrentTargetCharacterKey = string.Empty;
		checkpoint.PendingAction = RetainerBatchPendingAction.None;
		checkpoint.PendingRetainerName = string.Empty;
		checkpoint.RelogCommandIssuedUtc = DateTime.MinValue;
		checkpoint.QuestStartCommandIssuedUtc = DateTime.MinValue;
		checkpoint.AutoRetainerStartCommandIssuedUtc = DateTime.MinValue;
		return true;
	}

	public static bool CanAdoptPendingHire(string pendingHireName, IEnumerable<(ulong RetainerId, string Name)> before, IEnumerable<(ulong RetainerId, string Name)> after)
	{
		(ulong, string)[] array = before.ToArray();
		(ulong, string)[] array2 = after.ToArray();
		if (!string.IsNullOrWhiteSpace(pendingHireName) && array.Length == 1 && array2.Length == 1 && array[0].Item1 != 0L && array[0].Item1 == array2[0].Item1 && string.Equals(array[0].Item2, pendingHireName, StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(array2[0].Item2, pendingHireName, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static bool TargetIsValid(RetainerBatchTargetCheckpoint target)
	{
		if (target.ContentId != 0L && !string.IsNullOrWhiteSpace(target.CharacterKey) && target.Choice != null && string.Equals(target.CharacterKey.Trim(), target.Choice.CharacterKey?.Trim(), StringComparison.OrdinalIgnoreCase) && Enum.IsDefined(target.Choice.Type))
		{
			return RetainerSetupConfiguration.IsStarterCombatClass(target.Choice.CombatStarterClassId);
		}
		return false;
	}

	private static bool SettingsAreValid(RetainerBatchFrozenSettings settings)
	{
		if (Enum.IsDefined(settings.City) && Enum.IsDefined(settings.Appearance) && Enum.IsDefined(settings.Gender) && Enum.IsDefined(settings.Clan) && Enum.IsDefined(settings.Personality) && Enum.IsDefined(settings.StopAfter) && settings.SampleNames != null)
		{
			return settings.UnavailableNames != null;
		}
		return false;
	}

	private static RetainerBatchTargetCheckpoint CloneTarget(RetainerBatchTargetCheckpoint source)
	{
		return new RetainerBatchTargetCheckpoint
		{
			ContentId = source.ContentId,
			CharacterKey = (source.CharacterKey?.Trim() ?? string.Empty),
			Choice = new CharacterRetainerSetupChoice
			{
				CharacterKey = (source.Choice?.CharacterKey?.Trim() ?? string.Empty),
				Type = (source.Choice?.Type ?? RetainerType.Combat),
				CombatStarterClassId = (source.Choice?.CombatStarterClassId ?? 1)
			},
			XadbBaselineUpdatedUtc = source.XadbBaselineUpdatedUtc,
			AllowSameBatchRequeue = source.AllowSameBatchRequeue
		};
	}

	private static RetainerBatchFrozenSettings CloneSettings(RetainerBatchFrozenSettings source)
	{
		return new RetainerBatchFrozenSettings
		{
			City = source.City,
			Appearance = source.Appearance,
			Gender = source.Gender,
			Clan = source.Clan,
			Personality = source.Personality,
			StopAfter = source.StopAfter,
			AttachStarterPlan = source.AttachStarterPlan,
			EnableNewRetainers = source.EnableNewRetainers,
			EnableCharacter = source.EnableCharacter,
			SampleNames = (source.SampleNames?.ToList() ?? new List<string>()),
			UnavailableNames = (source.UnavailableNames?.ToList() ?? new List<string>())
		};
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

	private static bool TimestampIsValid(DateTime value, DateTime createdUtc, DateTime nowUtc)
	{
		if (value == DateTime.MinValue)
		{
			return true;
		}
		DateTime dateTime = NormalizeUtc(value);
		if (dateTime >= createdUtc)
		{
			return dateTime <= nowUtc + FutureClockTolerance;
		}
		return false;
	}
}
