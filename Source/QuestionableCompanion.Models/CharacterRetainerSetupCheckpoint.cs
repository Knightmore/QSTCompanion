using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class CharacterRetainerSetupCheckpoint
{
	public ulong ContentId { get; set; }

	public string CharacterKey { get; set; } = string.Empty;

	public int IntendedRetainerCount { get; set; }

	public CharacterRetainerSetupChoice LockedChoice { get; set; } = new CharacterRetainerSetupChoice();

	public List<BaselineRetainerCheckpoint> BaselineRetainers { get; set; } = new List<BaselineRetainerCheckpoint>();

	public bool BaselineRosterCaptured { get; set; }

	public List<TrackedRetainerCheckpoint> Retainers { get; set; } = new List<TrackedRetainerCheckpoint>();

	public List<string> ReservedNames { get; set; } = new List<string>();

	public RetainerStopAfter LastVerifiedCheckpoint { get; set; }

	public RetainerStopAfter? PendingCheckpoint { get; set; }

	public RetainerStarterCity ResolvedCity { get; set; }

	public uint StarterItemId { get; set; }

	public int StarterGearAcquiredCount { get; set; }

	public List<RetainerStarterGearSlotCheckpoint> StarterGearSlots { get; set; } = new List<RetainerStarterGearSlotCheckpoint>();

	public RetainerCheckpointState State { get; set; }

	public RetainerCheckpointDisposition Disposition { get; set; }

	public string LastError { get; set; } = string.Empty;

	public bool CleanupVerified { get; set; } = true;

	public bool DisallowAutomaticRequeue { get; set; }

	public bool AutoRetainerResetIssued { get; set; }

	public DateTime UpdatedUtc { get; set; } = DateTime.MinValue;

	public int CompletedWorkUnits => Retainers.Sum((TrackedRetainerCheckpoint x) => Math.Clamp(x.CompletedWorkUnits, 0, 5));

	public int TotalWorkUnits => Math.Max(0, IntendedRetainerCount) * 5;

	public int ProgressPercent
	{
		get
		{
			if (TotalWorkUnits != 0)
			{
				return Math.Clamp((int)Math.Floor((double)CompletedWorkUnits * 100.0 / (double)TotalWorkUnits), 0, 100);
			}
			return 0;
		}
	}

	public bool IsComplete
	{
		get
		{
			if (TotalWorkUnits > 0)
			{
				return CompletedWorkUnits >= TotalWorkUnits;
			}
			return false;
		}
	}

	public bool IsIncomplete
	{
		get
		{
			if (!IsComplete)
			{
				if (!Retainers.Any((TrackedRetainerCheckpoint retainer) => retainer.RetainerId != 0))
				{
					return IntendedRetainerCount > 0;
				}
				return true;
			}
			return false;
		}
	}

	public bool IsRetryablePreSideEffectFailure
	{
		get
		{
			if (Disposition == RetainerCheckpointDisposition.RetryablePreSideEffectFailure && State == RetainerCheckpointState.Failed && IntendedRetainerCount == 0 && Retainers.Count == 0)
			{
				return CleanupVerified;
			}
			return false;
		}
	}

	public bool IsResumablePartial
	{
		get
		{
			if (Disposition == RetainerCheckpointDisposition.ResumablePartial)
			{
				return IsIncomplete;
			}
			return false;
		}
	}

	public bool IsInterruptedBeforeSideEffects
	{
		get
		{
			if (Disposition == RetainerCheckpointDisposition.InterruptedBeforeSideEffects && State == RetainerCheckpointState.Failed && IntendedRetainerCount == 0 && Retainers.Count == 0)
			{
				return !CleanupVerified;
			}
			return false;
		}
	}

	public void Normalize(ulong key)
	{
		ContentId = key;
		CharacterKey = CharacterKey?.Trim() ?? string.Empty;
		IntendedRetainerCount = Math.Max(0, IntendedRetainerCount);
		if (LockedChoice == null)
		{
			CharacterRetainerSetupChoice characterRetainerSetupChoice = (LockedChoice = new CharacterRetainerSetupChoice());
		}
		LockedChoice.Type = (Enum.IsDefined(LockedChoice.Type) ? LockedChoice.Type : RetainerType.Combat);
		LockedChoice.CombatStarterClassId = ((!RetainerSetupConfiguration.IsStarterCombatClass(LockedChoice.CombatStarterClassId)) ? 1u : LockedChoice.CombatStarterClassId);
		LockedChoice.CharacterKey = (string.IsNullOrWhiteSpace(LockedChoice.CharacterKey) ? CharacterKey : LockedChoice.CharacterKey.Trim());
		Retainers = (Retainers ?? new List<TrackedRetainerCheckpoint>()).Where((TrackedRetainerCheckpoint retainer) => retainer != null).ToList();
		foreach (TrackedRetainerCheckpoint retainer in Retainers)
		{
			retainer.Normalize();
		}
		BaselineRetainers = (BaselineRetainers ?? new List<BaselineRetainerCheckpoint>()).Where((BaselineRetainerCheckpoint retainer) => retainer != null).ToList();
		foreach (BaselineRetainerCheckpoint baselineRetainer in BaselineRetainers)
		{
			baselineRetainer.Normalize();
		}
		StarterGearSlots = (from slot in StarterGearSlots ?? new List<RetainerStarterGearSlotCheckpoint>()
			where slot != null && slot.ItemId != 0 && slot.Slot >= 0
			group slot by (ContainerType: slot.ContainerType, Slot: slot.Slot) into @group
			select @group.First()).ToList();
		Retainers = (from x in Retainers
			where x.RetainerId != 0L && !string.IsNullOrWhiteSpace(x.Name)
			group x by x.RetainerId into x
			select x.OrderByDescending((TrackedRetainerCheckpoint y) => y.CompletedWorkUnits).First()).ToList();
		BaselineRetainers = (from x in BaselineRetainers
			where x.RetainerId != 0L && !string.IsNullOrWhiteSpace(x.Name)
			group x by x.RetainerId into x
			select x.First()).ToList();
		if (BaselineRetainers.Count > 0)
		{
			BaselineRosterCaptured = true;
		}
		ReservedNames = (from x in ReservedNames ?? new List<string>()
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (LastError == null)
		{
			string text = (LastError = string.Empty);
		}
		State = (Enum.IsDefined(State) ? State : RetainerCheckpointState.NotStarted);
		Disposition = (Enum.IsDefined(Disposition) ? Disposition : RetainerCheckpointDisposition.Unclassified);
		LastVerifiedCheckpoint = (RetainerStopAfter)Math.Clamp((int)LastVerifiedCheckpoint, 0, 5);
		ResolvedCity = (Enum.IsDefined(ResolvedCity) ? ResolvedCity : RetainerStarterCity.Automatic);
		StarterGearAcquiredCount = Math.Max(0, StarterGearAcquiredCount);
		if (PendingCheckpoint.HasValue)
		{
			PendingCheckpoint = (RetainerStopAfter)Math.Clamp((int)PendingCheckpoint.Value, 0, 5);
		}
		if (IsComplete)
		{
			State = RetainerCheckpointState.Complete;
			Disposition = RetainerCheckpointDisposition.Complete;
			LastVerifiedCheckpoint = RetainerStopAfter.AutoRetainerBootstrapped;
			PendingCheckpoint = null;
		}
		else if (Disposition == RetainerCheckpointDisposition.Unclassified)
		{
			bool flag = LastError.Contains("cancel", StringComparison.OrdinalIgnoreCase);
			RetainerCheckpointDisposition disposition;
			switch (State)
			{
			case RetainerCheckpointState.DeliberatelyStopped:
				if (IsIncomplete)
				{
					disposition = RetainerCheckpointDisposition.ResumablePartial;
					break;
				}
				goto default;
			case RetainerCheckpointState.Failed:
				if (IsIncomplete && CleanupVerified)
				{
					disposition = RetainerCheckpointDisposition.ResumablePartial;
					break;
				}
				if (!IsIncomplete && CleanupVerified && !flag)
				{
					disposition = RetainerCheckpointDisposition.RetryablePreSideEffectFailure;
					break;
				}
				goto case RetainerCheckpointState.Running;
			case RetainerCheckpointState.Running:
				disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
				break;
			default:
				disposition = RetainerCheckpointDisposition.Unclassified;
				break;
			}
			Disposition = disposition;
		}
		if (Disposition == RetainerCheckpointDisposition.RetryablePreSideEffectFailure && (!CleanupVerified || IsIncomplete || LastError.Contains("cancel", StringComparison.OrdinalIgnoreCase)))
		{
			Disposition = ((IsIncomplete && CleanupVerified) ? RetainerCheckpointDisposition.ResumablePartial : RetainerCheckpointDisposition.UnsafeOrTerminal);
		}
		bool flag2 = !CleanupVerified;
		if (flag2)
		{
			RetainerCheckpointState state = State;
			bool flag3 = ((state == RetainerCheckpointState.Running || state == RetainerCheckpointState.Failed) ? true : false);
			flag2 = flag3;
		}
		if (flag2)
		{
			Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
		}
	}

	public void ReconcileVerifiedUnits(IReadOnlyDictionary<ulong, int> verifiedUnits)
	{
		foreach (TrackedRetainerCheckpoint retainer in Retainers)
		{
			if (verifiedUnits.TryGetValue(retainer.RetainerId, out var value))
			{
				retainer.CompletedWorkUnits = Math.Max(retainer.CompletedWorkUnits, Math.Clamp(value, 0, 5));
			}
		}
		PendingCheckpoint = null;
		LastVerifiedCheckpoint = ((Retainers.Count != 0) ? ((RetainerStopAfter)Retainers.Min((TrackedRetainerCheckpoint x) => Math.Clamp(x.CompletedWorkUnits, 0, 5))) : RetainerStopAfter.ArrivedAtVocate);
		State = (IsComplete ? RetainerCheckpointState.Complete : RetainerCheckpointState.Failed);
		UpdatedUtc = DateTime.UtcNow;
	}
}
