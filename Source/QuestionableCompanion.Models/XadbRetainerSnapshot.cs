using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Models;

public sealed record XadbRetainerSnapshot(ulong OwnerContentId, XadbRetainerRosterStatus Status, int? DeclaredCount, IReadOnlyList<XadbRetainerEntry> Retainers, string FailureReason, DateTime SourceUpdatedUtc = default(DateTime), DateTime CollectedUtc = default(DateTime), bool EvidenceValidated = false, bool HasDefinitiveOwnershipConflict = false)
{
	public int HighestLevel
	{
		get
		{
			if (Retainers.Count != 0)
			{
				return Retainers.Max((XadbRetainerEntry x) => x.Level);
			}
			return 0;
		}
	}

	public static XadbRetainerSnapshot Unknown(string reason, ulong ownerContentId = 0uL, DateTime sourceUpdatedUtc = default(DateTime), bool hasDefinitiveOwnershipConflict = false)
	{
		XadbRetainerEntry[] retainers = Array.Empty<XadbRetainerEntry>();
		bool hasDefinitiveOwnershipConflict2 = hasDefinitiveOwnershipConflict;
		return new XadbRetainerSnapshot(ownerContentId, XadbRetainerRosterStatus.Unknown, null, retainers, reason, sourceUpdatedUtc, default(DateTime), EvidenceValidated: false, hasDefinitiveOwnershipConflict2);
	}

	public static XadbRetainerSnapshot ConfirmedZero(ulong ownerContentId, DateTime sourceUpdatedUtc, DateTime collectedUtc)
	{
		return new XadbRetainerSnapshot(ownerContentId, XadbRetainerRosterStatus.ConfirmedZero, 0, Array.Empty<XadbRetainerEntry>(), string.Empty, sourceUpdatedUtc, collectedUtc, EvidenceValidated: true);
	}
}
