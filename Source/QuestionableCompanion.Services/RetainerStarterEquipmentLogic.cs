using System;
using System.Collections.Generic;
using System.Linq;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal static class RetainerStarterEquipmentLogic
{
	public static uint ResolveClassJob(CharacterRetainerSetupChoice choice)
	{
		switch (choice.Type)
		{
		case RetainerType.Combat:
			if (RetainerSetupConfiguration.IsStarterCombatClass(choice.CombatStarterClassId))
			{
				return choice.CombatStarterClassId;
			}
			break;
		case RetainerType.Mining:
			return 16u;
		case RetainerType.Botany:
			return 17u;
		case RetainerType.Fishing:
			return 18u;
		}
		throw new InvalidOperationException("A valid combat starter class is required.");
	}

	public static uint ResolveWeatheredMainHand(string classAbbreviation, IEnumerable<RetainerStarterItemCandidate> candidates)
	{
		return (candidates ?? Array.Empty<RetainerStarterItemCandidate>()).FirstOrDefault((RetainerStarterItemCandidate item) => item.ClassJobCategory.Contains(classAbbreviation, StringComparison.OrdinalIgnoreCase) && item.Name.Contains("Weathered", StringComparison.OrdinalIgnoreCase) && new string[3] { "Arm", "Grimoire", "Primary Tool" }.Any((string category) => item.UiCategory.Contains(category, StringComparison.OrdinalIgnoreCase)))?.ItemId ?? 0;
	}

	public static int RequiredPurchaseCount(int inventoryBefore, int desiredMinimumInventoryCount)
	{
		return Math.Max(0, desiredMinimumInventoryCount - Math.Max(0, inventoryBefore));
	}

	public static bool InventoryProofSatisfied(int actualCount, int expectedMinimumCount)
	{
		return actualCount >= expectedMinimumCount;
	}

	public static IReadOnlyList<TrackedRetainerCheckpoint> SelectPendingExactRetainers(CharacterRetainerSetupCheckpoint checkpoint)
	{
		return checkpoint.Retainers.Where((TrackedRetainerCheckpoint retainer) => retainer.CompletedWorkUnits < 4).ToArray();
	}
}
