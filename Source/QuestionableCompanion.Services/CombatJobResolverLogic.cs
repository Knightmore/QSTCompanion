using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Services;

public static class CombatJobResolverLogic
{
	public const int EvidenceVersion = 1;

	public static IReadOnlyDictionary<uint, int> MapExplicitObservedLevels(IEnumerable<CombatJobDefinition> definitions, IEnumerable<CombatJobObservation> observations)
	{
		CombatJobDefinition[] source = (from definition in definitions
			where definition.ClassJobId != 0 && definition.ExpArrayIndex >= 0
			group definition by definition.ClassJobId into @group
			select @group.First()).ToArray();
		Dictionary<uint, CombatJobDefinition> dictionary = source.ToDictionary((CombatJobDefinition definition) => definition.ClassJobId);
		Dictionary<string, CombatJobDefinition> dictionary2 = source.Where((CombatJobDefinition definition) => !string.IsNullOrWhiteSpace(definition.Abbreviation)).GroupBy<CombatJobDefinition, string>((CombatJobDefinition definition) => definition.Abbreviation, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, CombatJobDefinition>, string, CombatJobDefinition>((IGrouping<string, CombatJobDefinition> group) => group.Key, (IGrouping<string, CombatJobDefinition> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		Dictionary<uint, int> dictionary3 = new Dictionary<uint, int>();
		foreach (CombatJobObservation observation in observations)
		{
			int level = observation.Level;
			if ((level > 0 && level <= 100) || 1 == 0)
			{
				CombatJobDefinition value = null;
				if (observation.ClassJobId != 0)
				{
					dictionary.TryGetValue(observation.ClassJobId, out value);
				}
				if (value == null && !string.IsNullOrWhiteSpace(observation.Abbreviation))
				{
					dictionary2.TryGetValue(observation.Abbreviation, out value);
				}
				if (!(value == null))
				{
					dictionary3[value.ClassJobId] = Math.Max(dictionary3.GetValueOrDefault(value.ClassJobId), observation.Level);
				}
			}
		}
		return dictionary3;
	}

	public static CombatJobResolution MergeTrustedAndObservedLevels(IReadOnlyDictionary<uint, int> trustedLevels, IReadOnlyDictionary<uint, int> observedLevels)
	{
		Dictionary<uint, int> dictionary = new Dictionary<uint, int>();
		foreach (KeyValuePair<uint, int> item in trustedLevels.Concat(observedLevels))
		{
			bool flag = item.Key == 0;
			if (!flag)
			{
				int value = item.Value;
				bool flag2 = ((value <= 0 || value > 100) ? true : false);
				flag = flag2;
			}
			if (!flag)
			{
				dictionary[item.Key] = Math.Max(dictionary.GetValueOrDefault(item.Key), item.Value);
			}
		}
		if (dictionary.Count == 0)
		{
			return CombatJobResolution.Empty;
		}
		KeyValuePair<uint, int> keyValuePair = (from entry in dictionary
			orderby entry.Value descending, entry.Key
			select entry).First();
		return new CombatJobResolution(dictionary, keyValuePair.Key, keyValuePair.Value);
	}

	public static CombatJobResolution Resolve(IEnumerable<CombatJobDefinition> definitions, IEnumerable<CombatJobObservation> observations, IEnumerable<uint>? verifiedSoulCrystalItemIds, bool inventoryEvidenceValid)
	{
		CombatJobDefinition[] array = (from definition in definitions
			where definition.ClassJobId != 0 && definition.ExpArrayIndex >= 0
			group definition by definition.ClassJobId into @group
			select @group.First()).ToArray();
		if (array.Length == 0)
		{
			return CombatJobResolution.Empty;
		}
		Dictionary<uint, CombatJobDefinition> dictionary = array.ToDictionary((CombatJobDefinition definition) => definition.ClassJobId);
		Dictionary<string, CombatJobDefinition> dictionary2 = array.Where((CombatJobDefinition definition) => !string.IsNullOrWhiteSpace(definition.Abbreviation)).GroupBy<CombatJobDefinition, string>((CombatJobDefinition definition) => definition.Abbreviation, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, CombatJobDefinition>, string, CombatJobDefinition>((IGrouping<string, CombatJobDefinition> group) => group.Key, (IGrouping<string, CombatJobDefinition> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		Dictionary<int, int> dictionary3 = new Dictionary<int, int>();
		int key;
		foreach (CombatJobObservation observation in observations)
		{
			key = observation.Level;
			if ((key > 0 && key <= 100) || 1 == 0)
			{
				CombatJobDefinition value = null;
				if (observation.ClassJobId != 0)
				{
					dictionary.TryGetValue(observation.ClassJobId, out value);
				}
				if (value == null && !string.IsNullOrWhiteSpace(observation.Abbreviation))
				{
					dictionary2.TryGetValue(observation.Abbreviation, out value);
				}
				if (!(value == null))
				{
					dictionary3[value.ExpArrayIndex] = Math.Max(dictionary3.GetValueOrDefault(value.ExpArrayIndex), observation.Level);
				}
			}
		}
		if (dictionary3.Count == 0)
		{
			return CombatJobResolution.Empty;
		}
		HashSet<uint> stones = (inventoryEvidenceValid ? new HashSet<uint>((verifiedSoulCrystalItemIds ?? Array.Empty<uint>()).Where((uint itemId) => itemId != 0)) : new HashSet<uint>());
		Dictionary<uint, int> dictionary4 = new Dictionary<uint, int>();
		foreach (KeyValuePair<int, int> item in dictionary3)
		{
			item.Deconstruct(out key, out var value2);
			int expArrayIndex = key;
			int value3 = value2;
			CombatJobDefinition[] source = (from definition in array
				where definition.ExpArrayIndex == expArrayIndex
				orderby definition.ClassJobId
				select definition).ToArray();
			CombatJobDefinition[] array2 = source.Where((CombatJobDefinition definition) => definition.SoulCrystalItemId != 0 && stones.Contains(definition.SoulCrystalItemId)).ToArray();
			if (array2.Length != 0)
			{
				CombatJobDefinition[] array3 = array2;
				for (value2 = 0; value2 < array3.Length; value2++)
				{
					CombatJobDefinition combatJobDefinition = array3[value2];
					dictionary4[combatJobDefinition.ClassJobId] = value3;
				}
			}
			else
			{
				CombatJobDefinition combatJobDefinition2 = source.FirstOrDefault((CombatJobDefinition definition) => definition.SoulCrystalItemId == 0);
				if (combatJobDefinition2 != null)
				{
					dictionary4[combatJobDefinition2.ClassJobId] = value3;
				}
			}
		}
		if (dictionary4.Count == 0)
		{
			return CombatJobResolution.Empty;
		}
		KeyValuePair<uint, int> keyValuePair = (from entry in dictionary4
			orderby entry.Value descending, entry.Key
			select entry).First();
		return new CombatJobResolution(dictionary4, keyValuePair.Key, keyValuePair.Value);
	}
}
