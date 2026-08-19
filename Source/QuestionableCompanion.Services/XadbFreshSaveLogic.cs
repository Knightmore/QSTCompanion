using System;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class XadbFreshSaveLogic
{
	private static readonly TimeSpan RequestClockTolerance = TimeSpan.FromSeconds(2L);

	public static bool IsNewerExactOwnerSnapshot(XadbRetainerSnapshot snapshot, ulong expectedOwnerContentId, DateTime baselineUpdatedUtc, DateTime requestedUtc)
	{
		if (snapshot.OwnerContentId != expectedOwnerContentId || snapshot.Status == XadbRetainerRosterStatus.Unknown || !snapshot.EvidenceValidated)
		{
			return false;
		}
		DateTime dateTime = NormalizeUtc(baselineUpdatedUtc);
		DateTime dateTime2 = NormalizeUtc(requestedUtc);
		if (snapshot.SourceUpdatedUtc > dateTime)
		{
			return snapshot.CollectedUtc >= dateTime2 - RequestClockTolerance;
		}
		return false;
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
