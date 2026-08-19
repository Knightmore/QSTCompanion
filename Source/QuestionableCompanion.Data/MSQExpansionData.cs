using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace QuestionableCompanion.Data;

public static class MSQExpansionData
{
	public enum Expansion
	{
		ARealmReborn,
		Heavensward,
		Stormblood,
		Shadowbringers,
		Endwalker,
		Dawntrail
	}

	private static readonly Dictionary<Expansion, HashSet<uint>> ExpansionQuests = new Dictionary<Expansion, HashSet<uint>>
	{
		{
			Expansion.ARealmReborn,
			new HashSet<uint>()
		},
		{
			Expansion.Heavensward,
			new HashSet<uint>()
		},
		{
			Expansion.Stormblood,
			new HashSet<uint>()
		},
		{
			Expansion.Shadowbringers,
			new HashSet<uint>()
		},
		{
			Expansion.Endwalker,
			new HashSet<uint>()
		},
		{
			Expansion.Dawntrail,
			new HashSet<uint>()
		}
	};

	private static readonly Dictionary<Expansion, int> ExpectedQuestCounts = new Dictionary<Expansion, int>
	{
		{
			Expansion.ARealmReborn,
			200
		},
		{
			Expansion.Heavensward,
			100
		},
		{
			Expansion.Stormblood,
			100
		},
		{
			Expansion.Shadowbringers,
			100
		},
		{
			Expansion.Endwalker,
			100
		},
		{
			Expansion.Dawntrail,
			100
		}
	};

	private static readonly Dictionary<Expansion, string> ExpansionNames = new Dictionary<Expansion, string>
	{
		{
			Expansion.ARealmReborn,
			"A Realm Reborn"
		},
		{
			Expansion.Heavensward,
			"Heavensward"
		},
		{
			Expansion.Stormblood,
			"Stormblood"
		},
		{
			Expansion.Shadowbringers,
			"Shadowbringers"
		},
		{
			Expansion.Endwalker,
			"Endwalker"
		},
		{
			Expansion.Dawntrail,
			"Dawntrail"
		}
	};

	private static readonly Dictionary<Expansion, string> ExpansionShortNames = new Dictionary<Expansion, string>
	{
		{
			Expansion.ARealmReborn,
			"ARR"
		},
		{
			Expansion.Heavensward,
			"HW"
		},
		{
			Expansion.Stormblood,
			"SB"
		},
		{
			Expansion.Shadowbringers,
			"ShB"
		},
		{
			Expansion.Endwalker,
			"EW"
		},
		{
			Expansion.Dawntrail,
			"DT"
		}
	};

	public static void RegisterQuest(uint questId, Expansion expansion)
	{
		if (ExpansionQuests.TryGetValue(expansion, out HashSet<uint> value))
		{
			value.Add(questId);
		}
	}

	public static void ClearQuests()
	{
		foreach (HashSet<uint> value in ExpansionQuests.Values)
		{
			value.Clear();
		}
	}

	public static Expansion GetExpansionForQuest(uint questId)
	{
		foreach (var (result, hashSet2) in ExpansionQuests)
		{
			if (hashSet2.Contains(questId))
			{
				return result;
			}
		}
		return Expansion.ARealmReborn;
	}

	public static IReadOnlySet<uint> GetQuestsForExpansion(Expansion expansion)
	{
		if (!ExpansionQuests.TryGetValue(expansion, out HashSet<uint> value))
		{
			return new HashSet<uint>();
		}
		return value;
	}

	public static int GetExpectedQuestCount(Expansion expansion)
	{
		if (!ExpectedQuestCounts.TryGetValue(expansion, out var value))
		{
			return 0;
		}
		return value;
	}

	public static string GetExpansionName(Expansion expansion)
	{
		if (!ExpansionNames.TryGetValue(expansion, out string value))
		{
			return "Unknown";
		}
		return value;
	}

	public static string GetExpansionShortName(Expansion expansion)
	{
		if (!ExpansionShortNames.TryGetValue(expansion, out string value))
		{
			return "???";
		}
		return value;
	}

	public static IEnumerable<Expansion> GetAllExpansions()
	{
		return from e in Enum.GetValues<Expansion>()
			orderby (int)e
			select e;
	}

	public static int GetCompletedQuestCountForExpansion(IEnumerable<uint> completedQuestIds, Expansion expansion)
	{
		IReadOnlySet<uint> expansionQuests = GetQuestsForExpansion(expansion);
		return completedQuestIds.Count((uint qId) => expansionQuests.Contains(qId));
	}

	public unsafe static (Expansion expansion, string debugInfo) GetCurrentExpansionFromGameWithDebug()
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("=== AGENT SCENARIO TREE DEBUG ===");
		try
		{
			AgentScenarioTree* ptr = AgentScenarioTree.Instance();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
			handler.AppendLiteral("AgentScenarioTree.Instance(): ");
			handler.AppendFormatted((ptr != null) ? "OK" : "NULL");
			stringBuilder3.AppendLine(ref handler);
			if (ptr == null)
			{
				stringBuilder.AppendLine("ERROR: AgentScenarioTree is NULL!");
				return (expansion: Expansion.ARealmReborn, debugInfo: stringBuilder.ToString());
			}
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder2);
			handler.AppendLiteral("AgentScenarioTree->Data: ");
			handler.AppendFormatted((ptr->Data != null) ? "OK" : "NULL");
			stringBuilder4.AppendLine(ref handler);
			if (ptr->Data == null)
			{
				stringBuilder.AppendLine("ERROR: AgentScenarioTree->Data is NULL!");
				return (expansion: Expansion.ARealmReborn, debugInfo: stringBuilder.ToString());
			}
			ushort num = ptr->Data->MainScenarioQuestIds[0];
			ushort num2 = ptr->Data->MainScenarioQuestIds[3];
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(44, 2, stringBuilder2);
			handler.AppendLiteral("MainScenarioQuestIds[0] current (raw): ");
			handler.AppendFormatted(num);
			handler.AppendLiteral(" (0x");
			handler.AppendFormatted(num, "X4");
			handler.AppendLiteral(")");
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(46, 2, stringBuilder2);
			handler.AppendLiteral("MainScenarioQuestIds[3] completed (raw): ");
			handler.AppendFormatted(num2);
			handler.AppendLiteral(" (0x");
			handler.AppendFormatted(num2, "X4");
			handler.AppendLiteral(")");
			stringBuilder6.AppendLine(ref handler);
			ushort num3 = ((num != 0) ? num : num2);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("Quest to check: ");
			handler.AppendFormatted(num3);
			handler.AppendLiteral(" (using ");
			handler.AppendFormatted((num != 0) ? "Current" : "Completed");
			handler.AppendLiteral(")");
			stringBuilder7.AppendLine(ref handler);
			if (num3 == 0)
			{
				stringBuilder.AppendLine("WARNING: Both current and completed main scenario quest IDs are 0!");
				return (expansion: Expansion.ARealmReborn, debugInfo: stringBuilder.ToString());
			}
			uint num4 = (uint)(num3 | 0x10000);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder2);
			handler.AppendLiteral("Converted Quest ID: ");
			handler.AppendFormatted(num4);
			handler.AppendLiteral(" (0x");
			handler.AppendFormatted(num4, "X8");
			handler.AppendLiteral(")");
			stringBuilder8.AppendLine(ref handler);
			Expansion expansion = GetExpansionForQuest(num4);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(22, 2, stringBuilder2);
			handler.AppendLiteral("Expansion for Quest ");
			handler.AppendFormatted(num4);
			handler.AppendLiteral(": ");
			handler.AppendFormatted(GetExpansionName(expansion));
			stringBuilder9.AppendLine(ref handler);
			IReadOnlySet<uint> questsForExpansion = GetQuestsForExpansion(expansion);
			bool flag = questsForExpansion.Contains(num4);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(23, 3, stringBuilder2);
			handler.AppendLiteral("Quest ");
			handler.AppendFormatted(num4);
			handler.AppendLiteral(" registered in ");
			handler.AppendFormatted(expansion);
			handler.AppendLiteral(": ");
			handler.AppendFormatted(flag);
			stringBuilder10.AppendLine(ref handler);
			if (!flag)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder11 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
				handler.AppendLiteral("WARNING: Quest ");
				handler.AppendFormatted(num4);
				handler.AppendLiteral(" is NOT in our registered quests!");
				stringBuilder11.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder12 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(30, 2, stringBuilder2);
				handler.AppendLiteral("Total registered quests for ");
				handler.AppendFormatted(expansion);
				handler.AppendLiteral(": ");
				handler.AppendFormatted(questsForExpansion.Count);
				stringBuilder12.AppendLine(ref handler);
				foreach (Expansion allExpansion in GetAllExpansions())
				{
					if (GetQuestsForExpansion(allExpansion).Contains(num4))
					{
						stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder13 = stringBuilder2;
						handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder2);
						handler.AppendLiteral("FOUND in ");
						handler.AppendFormatted(allExpansion);
						handler.AppendLiteral("!");
						stringBuilder13.AppendLine(ref handler);
						expansion = allExpansion;
						break;
					}
				}
			}
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder14 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder2);
			handler.AppendLiteral(">>> FINAL EXPANSION: ");
			handler.AppendFormatted(GetExpansionName(expansion));
			handler.AppendLiteral(" <<<");
			stringBuilder14.AppendLine(ref handler);
			return (expansion: expansion, debugInfo: stringBuilder.ToString());
		}
		catch (Exception ex)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder15 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
			handler.AppendLiteral("EXCEPTION: ");
			handler.AppendFormatted(ex.Message);
			stringBuilder15.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder16 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
			handler.AppendLiteral("Stack: ");
			handler.AppendFormatted(ex.StackTrace);
			stringBuilder16.AppendLine(ref handler);
			return (expansion: Expansion.ARealmReborn, debugInfo: stringBuilder.ToString());
		}
	}

	public static Expansion GetCurrentExpansionFromGame()
	{
		return GetCurrentExpansionFromGameWithDebug().expansion;
	}

	public static Expansion GetCurrentExpansion(IEnumerable<uint> completedQuestIds)
	{
		List<uint> list = completedQuestIds.ToList();
		if (list.Count == 0)
		{
			return Expansion.ARealmReborn;
		}
		foreach (Expansion item in GetAllExpansions().Reverse().ToList())
		{
			IReadOnlySet<uint> expansionQuests = GetQuestsForExpansion(item);
			if (list.Where((uint qId) => expansionQuests.Contains(qId)).ToList().Count > 0)
			{
				return item;
			}
		}
		return Expansion.ARealmReborn;
	}

	public static string GetExpansionDetectionDebugInfo(IEnumerable<uint> completedQuestIds)
	{
		List<uint> list = completedQuestIds.ToList();
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("=== EXPANSION DETECTION DEBUG ===");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(24, 1, stringBuilder2);
		handler.AppendLiteral("Total completed quests: ");
		handler.AppendFormatted(list.Count);
		stringBuilder3.AppendLine(ref handler);
		stringBuilder.AppendLine("");
		stringBuilder.AppendLine("Checking expansions from highest to lowest:");
		stringBuilder.AppendLine("");
		foreach (Expansion item in GetAllExpansions().Reverse())
		{
			IReadOnlySet<uint> expansionQuests = GetQuestsForExpansion(item);
			List<uint> list2 = list.Where((uint qId) => expansionQuests.Contains(qId)).ToList();
			float value = ((expansionQuests.Count > 0) ? ((float)list2.Count / (float)expansionQuests.Count * 100f) : 0f);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 2, stringBuilder2);
			handler.AppendFormatted(GetExpansionName(item));
			handler.AppendLiteral(" (");
			handler.AppendFormatted(GetExpansionShortName(item));
			handler.AppendLiteral("):");
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
			handler.AppendLiteral("  - Total MSQ in expansion: ");
			handler.AppendFormatted(expansionQuests.Count);
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(32, 2, stringBuilder2);
			handler.AppendLiteral("  - Completed by character: ");
			handler.AppendFormatted(list2.Count);
			handler.AppendLiteral(" (");
			handler.AppendFormatted(value, "F1");
			handler.AppendLiteral("%)");
			stringBuilder6.AppendLine(ref handler);
			if (list2.Count > 0)
			{
				string value2 = string.Join(", ", list2.OrderByDescending((uint x) => x).Take(5));
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
				handler.AppendLiteral("  - Sample Quest IDs: ");
				handler.AppendFormatted(value2);
				stringBuilder7.AppendLine(ref handler);
				stringBuilder.AppendLine("  >>> HAS COMPLETED QUESTS - WOULD SELECT THIS EXPANSION <<<");
			}
			else
			{
				stringBuilder.AppendLine("  - No quests completed in this expansion");
			}
			stringBuilder.AppendLine("");
		}
		Expansion currentExpansion = GetCurrentExpansion(list);
		stringBuilder.AppendLine("===========================================");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral(">>> FINAL DETECTED EXPANSION: ");
		handler.AppendFormatted(GetExpansionName(currentExpansion));
		handler.AppendLiteral(" <<<");
		stringBuilder8.AppendLine(ref handler);
		stringBuilder.AppendLine("===========================================");
		return stringBuilder.ToString();
	}

	public static ExpansionProgress GetExpansionProgress(IEnumerable<uint> completedQuestIds, Expansion expansion)
	{
		int completedQuestCountForExpansion = GetCompletedQuestCountForExpansion(completedQuestIds, expansion);
		int expectedQuestCount = GetExpectedQuestCount(expansion);
		return new ExpansionProgress
		{
			Expansion = expansion,
			CompletedCount = completedQuestCountForExpansion,
			ExpectedCount = expectedQuestCount,
			Percentage = ((expectedQuestCount > 0) ? ((float)completedQuestCountForExpansion / (float)expectedQuestCount * 100f) : 0f),
			IsComplete = (completedQuestCountForExpansion >= expectedQuestCount)
		};
	}

	public static List<ExpansionProgress> GetAllExpansionProgress(IEnumerable<uint> completedQuestIds)
	{
		return (from exp in GetAllExpansions()
			select GetExpansionProgress(completedQuestIds, exp)).ToList();
	}
}
