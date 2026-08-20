using System;
using System.Linq;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class RetainerSetupLogic
{
	public static bool IsConfirmedEmptyOwner(ulong contentId, XadbRetainerSnapshot snapshot)
	{
		if (contentId != 0L && snapshot.OwnerContentId == contentId && snapshot.Status == XadbRetainerRosterStatus.ConfirmedZero)
		{
			return snapshot.EvidenceValidated;
		}
		return false;
	}

	public static bool IsEligibleForExplicitRun(CharacterRetainerSetupCheckpoint? checkpoint)
	{
		if (checkpoint != null)
		{
			return checkpoint.State != RetainerCheckpointState.Complete;
		}
		return false;
	}

	public static bool IsEligibleForExplicitTarget(ulong contentId, XadbRetainerSnapshot snapshot, CharacterRetainerSetupCheckpoint? checkpoint)
	{
		if (checkpoint != null)
		{
			return IsEligibleForExplicitRun(checkpoint);
		}
		if (contentId != 0L)
		{
			if (snapshot.Status != XadbRetainerRosterStatus.Unknown)
			{
				if (snapshot.Status == XadbRetainerRosterStatus.ConfirmedZero || snapshot.Status == XadbRetainerRosterStatus.Populated)
				{
					return snapshot.OwnerContentId == contentId;
				}
				return false;
			}
			return true;
		}
		return false;
	}

	public static bool HasRecordedProgress(CharacterRetainerSetupCheckpoint checkpoint)
	{
		if (!checkpoint.Retainers.Any((TrackedRetainerCheckpoint retainer) => retainer.RetainerId != 0L || retainer.CompletedWorkUnits > 0) && !checkpoint.ReservedNames.Any((string name) => !string.IsNullOrWhiteSpace(name)) && checkpoint.StarterItemId == 0)
		{
			return checkpoint.StarterGearAcquiredCount > 0;
		}
		return true;
	}

	internal static bool HasLockedChoice(CharacterRetainerSetupCheckpoint checkpoint)
	{
		return checkpoint.Retainers.Any((TrackedRetainerCheckpoint retainer) => retainer.RetainerId != 0);
	}

	internal static void LockChoiceForFirstExactRetainer(CharacterRetainerSetupCheckpoint checkpoint, CharacterRetainerSetupChoice frozenChoice)
	{
		if (!HasLockedChoice(checkpoint))
		{
			checkpoint.LockedChoice = new CharacterRetainerSetupChoice
			{
				CharacterKey = (string.IsNullOrWhiteSpace(frozenChoice.CharacterKey) ? checkpoint.CharacterKey : frozenChoice.CharacterKey),
				Type = frozenChoice.Type,
				CombatStarterClassId = frozenChoice.CombatStarterClassId
			};
		}
	}

	public static bool RestartStructurallyZeroProgressCheckpoint(CharacterRetainerSetupCheckpoint checkpoint, ulong contentId, string characterKey, CharacterRetainerSetupChoice choice)
	{
		if (!IsEligibleForExplicitRun(checkpoint) || HasRecordedProgress(checkpoint))
		{
			return false;
		}
		checkpoint.ContentId = contentId;
		checkpoint.CharacterKey = characterKey;
		checkpoint.IntendedRetainerCount = 0;
		checkpoint.LockedChoice = new CharacterRetainerSetupChoice
		{
			CharacterKey = (string.IsNullOrWhiteSpace(choice.CharacterKey) ? characterKey : choice.CharacterKey)
		};
		checkpoint.Retainers.Clear();
		checkpoint.BaselineRetainers.Clear();
		checkpoint.BaselineRosterCaptured = false;
		checkpoint.ReservedNames.Clear();
		checkpoint.LastVerifiedCheckpoint = RetainerStopAfter.ArrivedAtVocate;
		checkpoint.PendingCheckpoint = null;
		checkpoint.ResolvedCity = RetainerStarterCity.Automatic;
		checkpoint.StarterItemId = 0u;
		checkpoint.StarterGearAcquiredCount = 0;
		checkpoint.State = RetainerCheckpointState.NotStarted;
		checkpoint.Disposition = RetainerCheckpointDisposition.Unclassified;
		checkpoint.LastError = string.Empty;
		checkpoint.CleanupVerified = true;
		checkpoint.AutoRetainerResetIssued = false;
		checkpoint.UpdatedUtc = DateTime.UtcNow;
		return true;
	}

	public static RetainerCheckpointDisposition ClassifyFailure(CharacterRetainerSetupCheckpoint checkpoint, bool terminalFailure, bool cancellationRequested, bool cleanupVerified)
	{
		if (!cleanupVerified || cancellationRequested || terminalFailure)
		{
			return RetainerCheckpointDisposition.UnsafeOrTerminal;
		}
		if (checkpoint.IsIncomplete)
		{
			return RetainerCheckpointDisposition.ResumablePartial;
		}
		return RetainerCheckpointDisposition.RetryablePreSideEffectFailure;
	}

	public static bool PromptMatches(string actual, string expected, string? dynamicName)
	{
		actual = NormalizeText(actual);
		expected = NormalizeText(expected);
		if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(dynamicName) && !actual.Contains(NormalizeText(dynamicName), StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string[] array = (from word in expected.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			select new string(word.Where(delegate(char character)
			{
				bool flag = char.IsLetterOrDigit(character);
				if (!flag)
				{
					bool flag2 = ((character == '\'' || character == '-') ? true : false);
					flag = flag2;
				}
				return flag;
			}).ToArray()) into word
			where word.Length >= 3
			select word).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		int num = ((array.Length <= 1) ? array.Length : Math.Max(2, (int)Math.Ceiling((double)array.Length * 0.75)));
		if (num > 0)
		{
			return array.Count((string word) => actual.Contains(word, StringComparison.OrdinalIgnoreCase)) >= num;
		}
		return false;
	}

	public static RetainerStarterCity ResolveStarterCity(uint townId)
	{
		return townId switch
		{
			1u => RetainerStarterCity.LimsaLominsa, 
			2u => RetainerStarterCity.Gridania, 
			3u => RetainerStarterCity.Uldah, 
			_ => throw new InvalidOperationException($"Unsupported starter-town row {townId}."), 
		};
	}

	private static string NormalizeText(string value)
	{
		return string.Join(' ', (value ?? string.Empty).Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
	}
}
