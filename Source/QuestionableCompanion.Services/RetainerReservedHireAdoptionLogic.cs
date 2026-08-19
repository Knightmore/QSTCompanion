using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Services;

internal static class RetainerReservedHireAdoptionLogic
{
	public static RetainerReservedHireAdoptionResult Decide(IReadOnlyList<RetainerRosterIdentity> tracked, IReadOnlyCollection<string> reservedNames, IReadOnlyList<RetainerRosterIdentity> live)
	{
		IGrouping<ulong, RetainerRosterIdentity> grouping = (from retainer in tracked
			group retainer by retainer.RetainerId).FirstOrDefault((IGrouping<ulong, RetainerRosterIdentity> group) => group.Count() > 1);
		if (grouping != null)
		{
			return Conflict($"Checkpoint contains duplicate retainer ID {grouping.Key}.");
		}
		IGrouping<string, RetainerRosterIdentity> grouping2 = tracked.GroupBy<RetainerRosterIdentity, string>((RetainerRosterIdentity retainer) => retainer.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault((IGrouping<string, RetainerRosterIdentity> group) => group.Count() > 1);
		if (grouping2 != null)
		{
			return Conflict("Checkpoint contains duplicate retainer name " + grouping2.Key + ".");
		}
		IGrouping<ulong, RetainerRosterIdentity> grouping3 = (from retainer in live
			group retainer by retainer.RetainerId).FirstOrDefault((IGrouping<ulong, RetainerRosterIdentity> group) => group.Count() > 1);
		if (grouping3 != null)
		{
			return Conflict($"Native roster contains duplicate retainer ID {grouping3.Key}.");
		}
		IGrouping<string, RetainerRosterIdentity> grouping4 = live.GroupBy<RetainerRosterIdentity, string>((RetainerRosterIdentity retainer) => retainer.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault((IGrouping<string, RetainerRosterIdentity> group) => group.Count() > 1);
		if (grouping4 != null)
		{
			return Conflict("Native roster contains duplicate retainer name " + grouping4.Key + ".");
		}
		foreach (RetainerRosterIdentity expected in tracked)
		{
			RetainerRosterIdentity retainerRosterIdentity = live.FirstOrDefault((RetainerRosterIdentity actual) => actual.RetainerId == expected.RetainerId);
			RetainerRosterIdentity retainerRosterIdentity2 = live.FirstOrDefault((RetainerRosterIdentity actual) => string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
			if (retainerRosterIdentity == null || retainerRosterIdentity2 == null || retainerRosterIdentity.RetainerId != retainerRosterIdentity2.RetainerId || !string.Equals(retainerRosterIdentity.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
			{
				return Conflict($"Tracked retainer {expected.Name} ({expected.RetainerId}) is missing or conflicts with the native roster.");
			}
		}
		HashSet<ulong> trackedIds = tracked.Select((RetainerRosterIdentity retainer) => retainer.RetainerId).ToHashSet();
		RetainerRosterIdentity[] array = live.Where((RetainerRosterIdentity retainer) => !trackedIds.Contains(retainer.RetainerId)).ToArray();
		if (array.Length == 0)
		{
			return new RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision.None, null, string.Empty);
		}
		RetainerRosterIdentity[] reservedMatches = array.Where((RetainerRosterIdentity retainer) => reservedNames.Contains<string>(retainer.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
		if (reservedMatches.Length > 1)
		{
			return Conflict("Multiple untracked native retainers match persisted QST reservations; ownership is ambiguous.");
		}
		if (reservedMatches.Length == 1 && array.Length == 1)
		{
			return new RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision.Adopt, reservedMatches[0], string.Empty);
		}
		RetainerRosterIdentity retainerRosterIdentity3 = array.First((RetainerRosterIdentity retainer) => !reservedMatches.Contains(retainer));
		return Conflict($"Live retainer {retainerRosterIdentity3.Name} ({retainerRosterIdentity3.RetainerId}) is not owned by this Companion checkpoint.");
	}

	private static RetainerReservedHireAdoptionResult Conflict(string error)
	{
		return new RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision.Conflict, null, error);
	}
}
