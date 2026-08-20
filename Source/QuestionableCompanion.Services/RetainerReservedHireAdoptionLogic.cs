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
		RetainerRosterIdentity[] array2 = array.Where((RetainerRosterIdentity retainer) => reservedNames.Contains<string>(retainer.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
		if (array2.Length != 0)
		{
			return new RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision.Adopt, array2.OrderBy<RetainerRosterIdentity, string>((RetainerRosterIdentity retainer) => retainer.Name, StringComparer.OrdinalIgnoreCase).ThenBy((RetainerRosterIdentity retainer) => retainer.RetainerId).First(), string.Empty);
		}
		return new RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision.None, null, string.Empty);
	}

	private static RetainerReservedHireAdoptionResult Conflict(string error)
	{
		return new RetainerReservedHireAdoptionResult(RetainerReservedHireAdoptionDecision.Conflict, null, error);
	}
}
