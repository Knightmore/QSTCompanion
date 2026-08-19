using System;
using System.Collections.Generic;
using System.Linq;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class ClassUnlockCatalog
{
	public static readonly IReadOnlyList<ClassUnlockTargetDefinition> Targets = new global::_003C_003Ez__ReadOnlyArray<ClassUnlockTargetDefinition>(new ClassUnlockTargetDefinition[33]
	{
		T(3u, 2, "MRD", "Marauder", ClassUnlockCategory.ArrCombat, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "179", "310" })),
		T(29u, 19, "ROG", "Rogue", ClassUnlockCategory.ArrCombat, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "101", "102" }), 10, "Armoury System and a level 10 starting-class quest"),
		T(26u, 18, "ACN", "Arcanist", ClassUnlockCategory.ArrCombat, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "451", "452" })),
		T(9u, 8, "BSM", "Blacksmith", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "185", "291" })),
		T(10u, 9, "ARM", "Armorer", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "186", "273" })),
		T(15u, 14, "CUL", "Culinarian", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "191", "271" })),
		T(18u, 17, "FSH", "Fisher", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "1134", "1107" })),
		T(36u, 25, "BLU", "Blue Mage", ClassUnlockCategory.Special, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlySingleElementList<string>("3192"), 50, "Level 50 combat job and The Ultimate Weapon"),
		T(38u, 27, "DNC", "Dancer", ClassUnlockCategory.Expansion, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlySingleElementList<string>("3249"), 60, "Level 60 combat job"),
		T(40u, 29, "SGE", "Sage", ClassUnlockCategory.Expansion, ClassUnlockHub.LimsaLominsa, new global::_003C_003Ez__ReadOnlySingleElementList<string>("4067"), 70, "Level 70 combat job"),
		T(5u, 3, "ARC", "Archer", ClassUnlockCategory.ArrCombat, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "181", "131" })),
		T(4u, 4, "LNC", "Lancer", ClassUnlockCategory.ArrCombat, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "180", "132" })),
		T(6u, 6, "CNJ", "Conjurer", ClassUnlockCategory.ArrCombat, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "182", "133" })),
		T(8u, 7, "CRP", "Carpenter", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "184", "138" })),
		T(12u, 11, "LTW", "Leatherworker", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "188", "105" })),
		T(17u, 16, "BTN", "Botanist", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "193", "3" })),
		T(37u, 26, "GNB", "Gunbreaker", ClassUnlockCategory.Expansion, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlySingleElementList<string>("3261"), 60, "Level 60 combat job"),
		T(42u, 31, "PCT", "Pictomancer", ClassUnlockCategory.Expansion, ClassUnlockHub.Gridania, new global::_003C_003Ez__ReadOnlySingleElementList<string>("4854"), 80, "Level 80 combat job"),
		T(1u, 1, "GLA", "Gladiator", ClassUnlockCategory.ArrCombat, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "177", "285" })),
		T(2u, 0, "PGL", "Pugilist", ClassUnlockCategory.ArrCombat, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "178", "532" })),
		T(7u, 5, "THM", "Thaumaturge", ClassUnlockCategory.ArrCombat, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "183", "344" })),
		T(11u, 10, "GSM", "Goldsmith", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "187", "608" })),
		T(13u, 12, "WVR", "Weaver", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "189", "534" })),
		T(14u, 13, "ALC", "Alchemist", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "190", "575" })),
		T(16u, 15, "MIN", "Miner", ClassUnlockCategory.CraftingGathering, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "192", "597" })),
		T(34u, 23, "SAM", "Samurai", ClassUnlockCategory.Expansion, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlySingleElementList<string>("2559"), 50, "Level 50 combat job"),
		T(35u, 24, "RDM", "Red Mage", ClassUnlockCategory.Expansion, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlySingleElementList<string>("2576"), 50, "Level 50 combat job"),
		T(39u, 28, "RPR", "Reaper", ClassUnlockCategory.Expansion, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlySingleElementList<string>("4073"), 70, "Level 70 combat job"),
		T(41u, 30, "VPR", "Viper", ClassUnlockCategory.Expansion, ClassUnlockHub.Uldah, new global::_003C_003Ez__ReadOnlySingleElementList<string>("4848"), 80, "Level 80 combat job"),
		T(31u, 20, "MCH", "Machinist", ClassUnlockCategory.Expansion, ClassUnlockHub.Ishgard, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "2109", "1696" }), 50, "Level 50 combat job and access to Ishgard"),
		T(32u, 21, "DRK", "Dark Knight", ClassUnlockCategory.Expansion, ClassUnlockHub.Ishgard, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "2110", "2053" }), 50, "Level 50 combat job and access to Ishgard"),
		T(33u, 22, "AST", "Astrologian", ClassUnlockCategory.Expansion, ClassUnlockHub.Ishgard, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "2123", "2012" }), 50, "Level 50 combat job and access to Ishgard"),
		T(43u, 32, "BST", "Beastmaster", ClassUnlockCategory.Special, ClassUnlockHub.LimsaLominsa, Array.Empty<string>(), 100, "Patch 7.56 quest data is not available yet", isAvailable: false)
	});

	public static ClassUnlockTargetDefinition? Find(uint classJobId)
	{
		return Targets.FirstOrDefault((ClassUnlockTargetDefinition target) => target.ClassJobId == classJobId);
	}

	public static IReadOnlyList<ClassUnlockTargetDefinition> OrderForRun(IEnumerable<uint> selectedClassJobIds, uint currentTerritoryId)
	{
		HashSet<uint> selected = selectedClassJobIds.ToHashSet();
		List<ClassUnlockHub> list = new ClassUnlockHub[4]
		{
			ClassUnlockHub.LimsaLominsa,
			ClassUnlockHub.Gridania,
			ClassUnlockHub.Uldah,
			ClassUnlockHub.Ishgard
		}.ToList();
		ClassUnlockHub? classUnlockHub = TerritoryToHub(currentTerritoryId);
		if (classUnlockHub.HasValue)
		{
			list.Remove(classUnlockHub.Value);
			list.Insert(0, classUnlockHub.Value);
		}
		return list.SelectMany((ClassUnlockHub hub) => Targets.Where((ClassUnlockTargetDefinition target) => target.Hub == hub && selected.Contains(target.ClassJobId))).ToArray();
	}

	public static int GetLevel(IReadOnlyList<int> levels, ClassUnlockTargetDefinition target)
	{
		if (target.ExpArrayIndex < 0 || target.ExpArrayIndex >= levels.Count)
		{
			return 0;
		}
		return Math.Max(0, levels[target.ExpArrayIndex]);
	}

	private static ClassUnlockHub? TerritoryToHub(uint territoryId)
	{
		switch (territoryId)
		{
		case 128u:
		case 129u:
			return ClassUnlockHub.LimsaLominsa;
		case 132u:
		case 133u:
			return ClassUnlockHub.Gridania;
		case 130u:
		case 131u:
			return ClassUnlockHub.Uldah;
		case 418u:
		case 419u:
			return ClassUnlockHub.Ishgard;
		default:
			return null;
		}
	}

	private static ClassUnlockTargetDefinition T(uint classJobId, int expArrayIndex, string abbreviation, string name, ClassUnlockCategory category, ClassUnlockHub hub, IReadOnlyList<string> questIds, int requiredCombatLevel = 1, string requirement = "Armoury System unlocked", bool isAvailable = true)
	{
		return new ClassUnlockTargetDefinition(classJobId, expArrayIndex, abbreviation, name, category, hub, questIds, requiredCombatLevel, requirement, isAvailable);
	}
}
