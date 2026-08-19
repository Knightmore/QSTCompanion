using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Helpers;

namespace QuestionableCompanion.Services;

public sealed class CombatJobResolver
{
	private readonly IPluginLog log;

	private readonly IReadOnlyList<CombatJobDefinition> definitions;

	public IReadOnlyList<CombatJobDefinition> Definitions => definitions;

	public CombatJobResolver(IDataManager dataManager, IPluginLog log)
	{
		this.log = log;
		ExcelSheet<ClassJob> classJobSheet = dataManager.GetExcelSheet<ClassJob>();
		definitions = (from classJobId in ((IEnumerable<byte>)JobClassification.CombatJobs).Select((Func<byte, uint>)((byte classJobId) => classJobId))
			where classJobSheet.TryGetRow(classJobId, out var _)
			select classJobSheet.GetRow(classJobId) into classJob
			select new CombatJobDefinition(classJob.RowId, classJob.ExpArrayIndex, classJob.ItemSoulCrystal.RowId, classJob.Abbreviation.ToString()) into definition
			where definition.ExpArrayIndex >= 0
			orderby definition.ClassJobId
			select definition).ToArray();
	}

	public CombatJobResolution Resolve(IReadOnlyDictionary<uint, int> observedLevels, IEnumerable<uint>? verifiedSoulCrystalItemIds, bool inventoryEvidenceValid)
	{
		return CombatJobResolverLogic.Resolve(definitions, observedLevels.Select((KeyValuePair<uint, int> entry) => new CombatJobObservation(entry.Key, string.Empty, entry.Value)), verifiedSoulCrystalItemIds, inventoryEvidenceValid);
	}

	public CombatJobResolution ResolveAbbreviations(IReadOnlyDictionary<string, int> observedLevels, IEnumerable<uint>? verifiedSoulCrystalItemIds, bool inventoryEvidenceValid)
	{
		return CombatJobResolverLogic.Resolve(definitions, observedLevels.Select<KeyValuePair<string, int>, CombatJobObservation>((KeyValuePair<string, int> entry) => new CombatJobObservation(0u, entry.Key, entry.Value)), verifiedSoulCrystalItemIds, inventoryEvidenceValid);
	}

	public CombatJobResolution ResolveCombined(IReadOnlyDictionary<uint, int> observedLevels, IReadOnlyDictionary<string, int> observedLevelsByAbbreviation, IEnumerable<uint>? verifiedSoulCrystalItemIds, bool inventoryEvidenceValid)
	{
		return CombatJobResolverLogic.Resolve(definitions, observedLevels.Select((KeyValuePair<uint, int> entry) => new CombatJobObservation(entry.Key, string.Empty, entry.Value)).Concat(observedLevelsByAbbreviation.Select<KeyValuePair<string, int>, CombatJobObservation>((KeyValuePair<string, int> entry) => new CombatJobObservation(0u, entry.Key, entry.Value))), verifiedSoulCrystalItemIds, inventoryEvidenceValid);
	}

	public IReadOnlyDictionary<uint, int> MapExplicitObservedLevels(IReadOnlyDictionary<uint, int> observedLevels, IReadOnlyDictionary<string, int> observedLevelsByAbbreviation)
	{
		return CombatJobResolverLogic.MapExplicitObservedLevels(definitions, observedLevels.Select((KeyValuePair<uint, int> entry) => new CombatJobObservation(entry.Key, string.Empty, entry.Value)).Concat(observedLevelsByAbbreviation.Select<KeyValuePair<string, int>, CombatJobObservation>((KeyValuePair<string, int> entry) => new CombatJobObservation(0u, entry.Key, entry.Value))));
	}

	public CombatJobResolution ResolveLevelArray(IReadOnlyList<int> levels, bool useLiveInventoryEvidence = false)
	{
		IEnumerable<CombatJobObservation> observations = from definition in definitions
			where definition.ExpArrayIndex < levels.Count
			select new CombatJobObservation(definition.ClassJobId, definition.Abbreviation, levels[definition.ExpArrayIndex]);
		IReadOnlySet<uint> itemIds = new HashSet<uint>();
		bool inventoryEvidenceValid = useLiveInventoryEvidence && TryReadLiveSoulCrystalItems(out itemIds);
		return CombatJobResolverLogic.Resolve(definitions, observations, itemIds, inventoryEvidenceValid);
	}

	public unsafe bool TryReadLiveSoulCrystalItems(out IReadOnlySet<uint> itemIds)
	{
		HashSet<uint> hashSet = new HashSet<uint>();
		try
		{
			InventoryManager* ptr = InventoryManager.Instance();
			if (ptr == null)
			{
				itemIds = hashSet;
				return false;
			}
			foreach (uint item in (from definition in definitions
				select definition.SoulCrystalItemId into itemId
				where itemId != 0
				select itemId).Distinct())
			{
				if (ptr->GetInventoryItemCount(item, isHq: false, checkEquipped: true, checkArmory: true, 0) > 0)
				{
					hashSet.Add(item);
				}
			}
			itemIds = hashSet;
			return true;
		}
		catch (Exception ex)
		{
			log.Debug("[CombatJobResolver] Live soul-crystal evidence was unavailable: " + ex.Message);
			itemIds = hashSet;
			return false;
		}
	}

	public bool MigrateSavedSnapshots(Configuration configuration)
	{
		bool result = false;
		string key;
		foreach (KeyValuePair<string, CharacterJobLevelSnapshot> characterJobLevel in configuration.CharacterJobLevels)
		{
			characterJobLevel.Deconstruct(out key, out var value);
			string text = key;
			CharacterJobLevelSnapshot characterJobLevelSnapshot = value;
			value = characterJobLevelSnapshot;
			if (value.XadbObservedCombatJobLevels == null)
			{
				Dictionary<uint, int> dictionary = (value.XadbObservedCombatJobLevels = new Dictionary<uint, int>());
			}
			bool flag = characterJobLevelSnapshot.JobEvidenceVersion < 1;
			bool flag2 = !flag && characterJobLevelSnapshot.InventoryEvidenceValid;
			Dictionary<uint, int> combatJobLevels = characterJobLevelSnapshot.CombatJobLevels;
			IEnumerable<uint> verifiedSoulCrystalItemIds;
			if (!flag2)
			{
				IEnumerable<uint> enumerable = Array.Empty<uint>();
				verifiedSoulCrystalItemIds = enumerable;
			}
			else
			{
				IEnumerable<uint> enumerable = characterJobLevelSnapshot.VerifiedSoulCrystalItemIds;
				verifiedSoulCrystalItemIds = enumerable;
			}
			CombatJobResolution combatJobResolution = Resolve(combatJobLevels, verifiedSoulCrystalItemIds, flag2);
			if (!DictionaryEqual(characterJobLevelSnapshot.CombatJobLevels, combatJobResolution.Levels) || characterJobLevelSnapshot.HighestCombatJobId != combatJobResolution.HighestJobId || characterJobLevelSnapshot.HighestCombatJobLevel != combatJobResolution.HighestLevel || flag)
			{
				characterJobLevelSnapshot.CombatJobLevels = combatJobResolution.Levels.ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value);
				characterJobLevelSnapshot.HighestCombatJobId = combatJobResolution.HighestJobId;
				characterJobLevelSnapshot.HighestCombatJobLevel = combatJobResolution.HighestLevel;
				characterJobLevelSnapshot.JobEvidenceVersion = 1;
				if (flag)
				{
					characterJobLevelSnapshot.InventoryEvidenceValid = false;
					characterJobLevelSnapshot.VerifiedSoulCrystalItemIds.Clear();
					characterJobLevelSnapshot.JobEvidenceSource = "LegacyConservativeMigration";
					characterJobLevelSnapshot.JobEvidenceUpdatedUtc = DateTime.UtcNow;
				}
				result = true;
			}
			if (configuration.QuestRotationCombatJobByCharacter.TryGetValue(text, out var value2) && value2 != 0 && !combatJobResolution.Levels.ContainsKey(value2) && !characterJobLevelSnapshot.XadbObservedCombatJobLevels.ContainsKey(value2))
			{
				configuration.QuestRotationCombatJobByCharacter.Remove(text);
				log.Warning($"[CombatJobResolver] Cleared uncorroborated saved combat-job selection {value2} for {text}.");
				result = true;
			}
		}
		KeyValuePair<string, uint>[] array = configuration.QuestRotationCombatJobByCharacter.ToArray();
		foreach (KeyValuePair<string, uint> keyValuePair in array)
		{
			keyValuePair.Deconstruct(out key, out var value3);
			string text2 = key;
			uint num2 = value3;
			if (num2 != 0 && (!configuration.CharacterJobLevels.TryGetValue(text2, out CharacterJobLevelSnapshot value4) || (!value4.CombatJobLevels.ContainsKey(num2) && !value4.XadbObservedCombatJobLevels.ContainsKey(num2))))
			{
				configuration.QuestRotationCombatJobByCharacter.Remove(text2);
				log.Warning($"[CombatJobResolver] Cleared saved combat-job selection {num2} for {text2} " + "because no corroborated job snapshot exists.");
				result = true;
			}
		}
		return result;
	}

	private static bool DictionaryEqual(IReadOnlyDictionary<uint, int> left, IReadOnlyDictionary<uint, int> right)
	{
		if (left.Count == right.Count)
		{
			return left.All((KeyValuePair<uint, int> entry) => right.TryGetValue(entry.Key, out var value) && value == entry.Value);
		}
		return false;
	}
}
