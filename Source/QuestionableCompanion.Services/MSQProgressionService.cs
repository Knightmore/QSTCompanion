using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Data;

namespace QuestionableCompanion.Services;

public class MSQProgressionService
{
	private readonly IDataManager dataManager;

	private readonly IPluginLog log;

	private readonly QuestDetectionService questDetectionService;

	private readonly IObjectTable objectTable;

	private readonly IFramework framework;

	private List<Quest>? mainScenarioQuests;

	private Dictionary<uint, string> questNameCache = new Dictionary<uint, string>();

	private Dictionary<string, List<Quest>> questsByExpansion = new Dictionary<string, List<Quest>>();

	private static readonly uint[] MSQ_JOURNAL_GENRE_IDS = new uint[14]
	{
		1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u, 10u,
		11u, 12u, 13u, 14u
	};

	private const uint LAST_ARR_QUEST_ID = 65964u;

	private static readonly Dictionary<uint, MSQExpansionData.Expansion> JournalGenreToExpansion = new Dictionary<uint, MSQExpansionData.Expansion>
	{
		{
			1u,
			MSQExpansionData.Expansion.ARealmReborn
		},
		{
			2u,
			MSQExpansionData.Expansion.ARealmReborn
		},
		{
			3u,
			MSQExpansionData.Expansion.Heavensward
		},
		{
			4u,
			MSQExpansionData.Expansion.Heavensward
		},
		{
			5u,
			MSQExpansionData.Expansion.Heavensward
		},
		{
			6u,
			MSQExpansionData.Expansion.Stormblood
		},
		{
			7u,
			MSQExpansionData.Expansion.Stormblood
		},
		{
			8u,
			MSQExpansionData.Expansion.Shadowbringers
		},
		{
			9u,
			MSQExpansionData.Expansion.Shadowbringers
		},
		{
			10u,
			MSQExpansionData.Expansion.Shadowbringers
		},
		{
			11u,
			MSQExpansionData.Expansion.Endwalker
		},
		{
			12u,
			MSQExpansionData.Expansion.Endwalker
		},
		{
			13u,
			MSQExpansionData.Expansion.Dawntrail
		},
		{
			14u,
			MSQExpansionData.Expansion.Dawntrail
		}
	};

	public MSQProgressionService(IDataManager dataManager, IPluginLog log, QuestDetectionService questDetectionService, IObjectTable objectTable, IFramework framework)
	{
		this.dataManager = dataManager;
		this.log = log;
		this.questDetectionService = questDetectionService;
		this.objectTable = objectTable;
		this.framework = framework;
		InitializeMSQData();
		framework.RunOnTick(delegate
		{
			DebugCurrentCharacterQuest();
		}, default(TimeSpan), 60);
	}

	private void InitializeMSQData()
	{
		try
		{
			ExcelSheet<Quest> excelSheet = dataManager.GetExcelSheet<Quest>();
			if (excelSheet == null)
			{
				log.Warning("[MSQProgression] Failed to load the Quest sheet.");
				return;
			}
			List<Quest> list = new List<Quest>();
			foreach (Quest item in excelSheet)
			{
				try
				{
					if (item.RowId != 0 && MSQ_JOURNAL_GENRE_IDS.Contains(item.JournalGenre.RowId))
					{
						list.Add(item);
					}
				}
				catch (Exception exception)
				{
					log.Warning(exception, $"[MSQProgression] Skipping unreadable Quest row {item.RowId}.");
				}
			}
			mainScenarioQuests = list.OrderBy((Quest q) => q.RowId).ToList();
			if (mainScenarioQuests.Count == 0)
			{
				log.Warning("[MSQProgression] No readable MSQ rows were found.");
				return;
			}
			questNameCache.Clear();
			questsByExpansion.Clear();
			MSQExpansionData.ClearQuests();
			List<Quest> list2 = new List<Quest>(mainScenarioQuests.Count);
			foreach (Quest mainScenarioQuest in mainScenarioQuests)
			{
				try
				{
					string value = mainScenarioQuest.Name.ExtractText();
					if (!string.IsNullOrEmpty(value))
					{
						questNameCache[mainScenarioQuest.RowId] = value;
					}
					uint rowId = mainScenarioQuest.JournalGenre.RowId;
					if (rowId != 2 || mainScenarioQuest.RowId <= 65964)
					{
						MSQExpansionData.Expansion valueOrDefault = JournalGenreToExpansion.GetValueOrDefault(rowId, MSQExpansionData.Expansion.ARealmReborn);
						MSQExpansionData.RegisterQuest(mainScenarioQuest.RowId, valueOrDefault);
						string expansionShortName = MSQExpansionData.GetExpansionShortName(valueOrDefault);
						if (!questsByExpansion.TryGetValue(expansionShortName, out List<Quest> value2))
						{
							value2 = new List<Quest>();
							questsByExpansion[expansionShortName] = value2;
						}
						value2.Add(mainScenarioQuest);
						list2.Add(mainScenarioQuest);
					}
				}
				catch (Exception exception2)
				{
					log.Warning(exception2, $"[MSQProgression] Skipping Quest row {mainScenarioQuest.RowId} while building the MSQ index.");
				}
			}
			mainScenarioQuests = list2;
			log.Information($"[MSQProgression] Indexed {mainScenarioQuests.Count} readable MSQ rows.");
		}
		catch (Exception exception3)
		{
			log.Error(exception3, "[MSQProgression] Failed to initialize MSQ data.");
		}
	}

	public (uint questId, string questName) GetLastCompletedMSQ(string characterName)
	{
		if (mainScenarioQuests == null || mainScenarioQuests.Count == 0)
		{
			return (questId: 0u, questName: "—");
		}
		try
		{
			List<uint> completedQuests = questDetectionService.GetAllCompletedQuestIds();
			Quest quest = (from q in mainScenarioQuests
				where completedQuests.Contains(q.RowId)
				orderby q.RowId descending
				select q).FirstOrDefault();
			if (quest.RowId != 0)
			{
				string valueOrDefault = questNameCache.GetValueOrDefault(quest.RowId, "Unknown Quest");
				return (questId: quest.RowId, questName: valueOrDefault);
			}
		}
		catch (Exception)
		{
		}
		return (questId: 0u, questName: "—");
	}

	public float GetMSQCompletionPercentage()
	{
		if (mainScenarioQuests == null || mainScenarioQuests.Count == 0)
		{
			return 0f;
		}
		try
		{
			List<uint> completedQuests = questDetectionService.GetAllCompletedQuestIds();
			return (float)mainScenarioQuests.Count((Quest q) => completedQuests.Contains(q.RowId)) / (float)mainScenarioQuests.Count * 100f;
		}
		catch (Exception)
		{
			return 0f;
		}
	}

	public int GetTotalMSQCount()
	{
		return mainScenarioQuests?.Count ?? 0;
	}

	public int GetCompletedMSQCount()
	{
		if (mainScenarioQuests == null || mainScenarioQuests.Count == 0)
		{
			return 0;
		}
		try
		{
			List<uint> completedQuests = questDetectionService.GetAllCompletedQuestIds();
			return mainScenarioQuests.Count((Quest q) => completedQuests.Contains(q.RowId));
		}
		catch (Exception)
		{
			return 0;
		}
	}

	public string GetQuestName(uint questId)
	{
		return questNameCache.GetValueOrDefault(questId, "Unknown Quest");
	}

	public bool IsMSQ(uint questId)
	{
		return mainScenarioQuests?.Any((Quest q) => q.RowId == questId) ?? false;
	}

	public ExpansionInfo? GetExpansionForQuest(uint questId)
	{
		MSQExpansionData.Expansion expansionForQuest = MSQExpansionData.GetExpansionForQuest(questId);
		return new ExpansionInfo
		{
			Name = MSQExpansionData.GetExpansionName(expansionForQuest),
			ShortName = MSQExpansionData.GetExpansionShortName(expansionForQuest),
			MinQuestId = 0u,
			MaxQuestId = 0u,
			ExpectedQuestCount = MSQExpansionData.GetExpectedQuestCount(expansionForQuest)
		};
	}

	public List<ExpansionInfo> GetExpansions()
	{
		return (from exp in MSQExpansionData.GetAllExpansions()
			select new ExpansionInfo
			{
				Name = MSQExpansionData.GetExpansionName(exp),
				ShortName = MSQExpansionData.GetExpansionShortName(exp),
				MinQuestId = 0u,
				MaxQuestId = 0u,
				ExpectedQuestCount = MSQExpansionData.GetExpectedQuestCount(exp)
			}).ToList();
	}

	public (int completed, int total) GetExpansionProgress(string expansionShortName)
	{
		List<uint> completedQuests = questDetectionService.GetAllCompletedQuestIds();
		List<Quest>? obj = questsByExpansion.GetValueOrDefault(expansionShortName) ?? new List<Quest>();
		int item = obj.Count((Quest q) => completedQuests.Contains(q.RowId));
		int count = obj.Count;
		return (completed: item, total: count);
	}

	public ExpansionInfo? GetCurrentExpansion()
	{
		try
		{
			List<uint> allCompletedQuestIds = questDetectionService.GetAllCompletedQuestIds();
			(MSQExpansionData.Expansion expansion, string debugInfo) currentExpansionFromGameWithDebug = MSQExpansionData.GetCurrentExpansionFromGameWithDebug();
			MSQExpansionData.Expansion item = currentExpansionFromGameWithDebug.expansion;
			string[] array = currentExpansionFromGameWithDebug.debugInfo.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string.IsNullOrWhiteSpace(array[i]);
			}
			MSQExpansionData.Expansion currentExpansion = MSQExpansionData.GetCurrentExpansion(allCompletedQuestIds);
			array = MSQExpansionData.GetExpansionDetectionDebugInfo(allCompletedQuestIds).Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string.IsNullOrWhiteSpace(array[i]);
			}
			MSQExpansionData.Expansion expansion = item;
			if (item == MSQExpansionData.Expansion.ARealmReborn && currentExpansion != MSQExpansionData.Expansion.ARealmReborn)
			{
				expansion = currentExpansion;
			}
			return new ExpansionInfo
			{
				Name = MSQExpansionData.GetExpansionName(expansion),
				ShortName = MSQExpansionData.GetExpansionShortName(expansion),
				MinQuestId = 0u,
				MaxQuestId = 0u,
				ExpectedQuestCount = MSQExpansionData.GetExpectedQuestCount(expansion)
			};
		}
		catch (Exception)
		{
			return GetExpansions().FirstOrDefault();
		}
	}

	public Dictionary<string, (int completed, int total)> GetExpansionProgressForCharacter(List<uint> completedQuestIds)
	{
		Dictionary<string, (int, int)> dictionary = new Dictionary<string, (int, int)>();
		foreach (ExpansionInfo expansion in GetExpansions())
		{
			List<Quest> list = questsByExpansion.GetValueOrDefault(expansion.ShortName) ?? new List<Quest>();
			int item = list.Count((Quest q) => completedQuestIds.Contains(q.RowId));
			dictionary[expansion.ShortName] = (item, list.Count);
		}
		return dictionary;
	}

	public List<Quest> GetAllMSQQuests()
	{
		return mainScenarioQuests ?? new List<Quest>();
	}

	public Dictionary<string, ExpansionProgressInfo> GetExpansionProgress()
	{
		Dictionary<string, ExpansionProgressInfo> dictionary = new Dictionary<string, ExpansionProgressInfo>();
		List<uint> allCompletedQuestIds = questDetectionService.GetAllCompletedQuestIds();
		foreach (MSQExpansionData.Expansion allExpansion in MSQExpansionData.GetAllExpansions())
		{
			ExpansionProgress expansionProgress = MSQExpansionData.GetExpansionProgress(allCompletedQuestIds, allExpansion);
			dictionary[expansionProgress.ExpansionName] = new ExpansionProgressInfo
			{
				ExpansionName = expansionProgress.ExpansionName,
				ShortName = expansionProgress.ExpansionShortName,
				TotalQuests = expansionProgress.ExpectedCount,
				CompletedQuests = expansionProgress.CompletedCount,
				Percentage = expansionProgress.Percentage
			};
		}
		return dictionary;
	}

	public Dictionary<string, ExpansionProgressInfo> GetExpansionProgressForCharacter(List<string> completedQuestIds)
	{
		Dictionary<string, ExpansionProgressInfo> dictionary = new Dictionary<string, ExpansionProgressInfo>();
		List<uint> completedQuestIds2 = (from id in completedQuestIds
			select uint.TryParse(id, out var result) ? result : 0u into id
			where id != 0
			select id).ToList();
		foreach (MSQExpansionData.Expansion allExpansion in MSQExpansionData.GetAllExpansions())
		{
			ExpansionProgress expansionProgress = MSQExpansionData.GetExpansionProgress(completedQuestIds2, allExpansion);
			dictionary[expansionProgress.ExpansionName] = new ExpansionProgressInfo
			{
				ExpansionName = expansionProgress.ExpansionName,
				ShortName = expansionProgress.ExpansionShortName,
				TotalQuests = expansionProgress.ExpectedCount,
				CompletedQuests = expansionProgress.CompletedCount,
				Percentage = expansionProgress.Percentage
			};
		}
		return dictionary;
	}

	public ExpansionInfo? GetCurrentExpansion(uint lastCompletedQuestId)
	{
		MSQExpansionData.Expansion expansionForQuest = MSQExpansionData.GetExpansionForQuest(lastCompletedQuestId);
		return new ExpansionInfo
		{
			Name = MSQExpansionData.GetExpansionName(expansionForQuest),
			ShortName = MSQExpansionData.GetExpansionShortName(expansionForQuest),
			MinQuestId = 0u,
			MaxQuestId = 0u,
			ExpectedQuestCount = MSQExpansionData.GetExpectedQuestCount(expansionForQuest)
		};
	}

	private MSQExpansionData.Expansion ConvertLuminaExpansionToOurs(uint luminaExpansionId)
	{
		return luminaExpansionId switch
		{
			0u => MSQExpansionData.Expansion.ARealmReborn, 
			1u => MSQExpansionData.Expansion.Heavensward, 
			2u => MSQExpansionData.Expansion.Stormblood, 
			3u => MSQExpansionData.Expansion.Shadowbringers, 
			4u => MSQExpansionData.Expansion.Endwalker, 
			5u => MSQExpansionData.Expansion.Dawntrail, 
			_ => MSQExpansionData.Expansion.ARealmReborn, 
		};
	}

	public void DebugCurrentCharacterQuest()
	{
		try
		{
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			if (localPlayer == null)
			{
				framework.RunOnTick(delegate
				{
					DebugCurrentCharacterQuest();
				}, default(TimeSpan), 60);
				return;
			}
			_ = localPlayer.Name.TextValue;
			localPlayer.HomeWorld.Value.Name.ToString();
			ExcelSheet<Quest> excelSheet = dataManager.GetExcelSheet<Quest>();
			if (excelSheet == null)
			{
				return;
			}
			List<Quest> list = new List<Quest>();
			foreach (Quest item in excelSheet)
			{
				if (MSQ_JOURNAL_GENRE_IDS.Contains(item.JournalGenre.RowId) && QuestManager.IsQuestComplete((ushort)item.RowId))
				{
					list.Add(item);
				}
			}
			if (list.Count == 0)
			{
				return;
			}
			Quest quest = list.OrderByDescending((Quest q) => q.RowId).First();
			try
			{
				quest.JournalGenre.Value.Name.ToString();
			}
			catch
			{
			}
			try
			{
				quest.Expansion.Value.Name.ToString();
			}
			catch
			{
			}
			foreach (Quest item2 in list.OrderByDescending((Quest q) => q.RowId).Take(10).ToList())
			{
				_ = item2;
			}
			foreach (IGrouping<MSQExpansionData.Expansion, Quest> item3 in from q in list
				group q by JournalGenreToExpansion.GetValueOrDefault(q.JournalGenre.RowId, MSQExpansionData.Expansion.ARealmReborn) into g
				orderby g.Key
				select g)
			{
				_ = item3;
			}
		}
		catch (Exception)
		{
		}
	}
}
