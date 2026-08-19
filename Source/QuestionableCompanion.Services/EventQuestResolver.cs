using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace QuestionableCompanion.Services;

public class EventQuestResolver
{
	private readonly IDataManager dataManager;

	private readonly IPluginLog log;

	public EventQuestResolver(IDataManager dataManager, IPluginLog log)
	{
		this.dataManager = dataManager;
		this.log = log;
	}

	public List<string> ResolveEventQuestDependencies(string eventQuestId)
	{
		List<string> list = new List<string>();
		ExcelSheet<Quest> excelSheet = dataManager.GetExcelSheet<Quest>();
		log.Information("[EventQuestResolver] Searching for quest with ID string: '" + eventQuestId + "'");
		Quest? quest = null;
		foreach (Quest item in excelSheet)
		{
			if (item.RowId != 0)
			{
				string text = item.Id.ExtractText();
				if (text == eventQuestId || text.EndsWith("_" + eventQuestId) || text.EndsWith("_" + eventQuestId.PadLeft(5, '0')))
				{
					quest = item;
					log.Information($"[EventQuestResolver] Found quest by ID field: '{text}' (searched for '{eventQuestId}')");
					break;
				}
			}
		}
		if (!quest.HasValue || quest.Value.RowId == 0)
		{
			log.Error("[EventQuestResolver] Quest with ID '" + eventQuestId + "' not found in Lumina");
			return list;
		}
		Quest value = quest.Value;
		string value2 = value.Name.ExtractText();
		log.Information($"[EventQuestResolver] Found quest: RowId={value.RowId}, Name='{value2}', ID='{value.Id.ExtractText()}'");
		try
		{
			foreach (RowRef<Quest> item2 in value.PreviousQuest)
			{
				if (item2.RowId == 0)
				{
					continue;
				}
				if (excelSheet.TryGetRow(item2.RowId, out var row))
				{
					string value3 = row.Name.ExtractText();
					string text2 = row.Id.ExtractText();
					string[] array = text2.Split('_');
					string text3 = ((array.Length > 1) ? array[1].TrimStart('0') : text2);
					if (string.IsNullOrEmpty(text3))
					{
						text3 = "0";
					}
					list.Add(text3);
					log.Information($"[EventQuestResolver] Found previous quest: RowId={item2.RowId}, Name='{value3}', ID='{text2}' -> '{text3}'");
				}
				else
				{
					log.Warning($"[EventQuestResolver] Previous Quest row {item2.RowId} was not found.");
				}
			}
		}
		catch (Exception ex)
		{
			log.Warning("[EventQuestResolver] Error reading PreviousQuest: " + ex.Message);
		}
		try
		{
			foreach (RowRef<Quest> item3 in value.QuestLock)
			{
				if (item3.RowId == 0)
				{
					continue;
				}
				if (excelSheet.TryGetRow(item3.RowId, out var row2))
				{
					string text4 = row2.Id.ExtractText();
					string[] array2 = text4.Split('_');
					string text5 = ((array2.Length > 1) ? array2[1].TrimStart('0') : text4);
					if (string.IsNullOrEmpty(text5))
					{
						text5 = "0";
					}
					list.Add(text5);
					log.Information($"[EventQuestResolver] Found quest lock: RowId={item3.RowId}, ID='{text4}' -> '{text5}'");
				}
				else
				{
					log.Warning($"[EventQuestResolver] Quest lock row {item3.RowId} was not found.");
				}
			}
		}
		catch (Exception ex2)
		{
			log.Warning("[EventQuestResolver] Error reading QuestLock: " + ex2.Message);
		}
		list = list.Distinct().ToList();
		log.Information($"[EventQuestResolver] Found {list.Count} direct prerequisites");
		if (list.Count > 0)
		{
			log.Information("[EventQuestResolver] Event Quest " + eventQuestId + " requires: " + string.Join(", ", list));
		}
		else
		{
			log.Information("[EventQuestResolver] Event Quest " + eventQuestId + " has no prerequisites");
		}
		return list;
	}

	public bool IsValidQuest(string questId, out string classification)
	{
		string item = QuestIdParser.ParseQuestId(questId).rawId;
		if (QuestIdParser.ClassifyQuestId(questId) == QuestIdType.EventQuest)
		{
			classification = "EventQuest";
			log.Debug("[EventQuestResolver] Quest " + questId + " recognized as Event Quest (prefix detected)");
			return true;
		}
		if (!uint.TryParse(item, out var result))
		{
			classification = "Invalid";
			return false;
		}
		try
		{
			if (dataManager.GetExcelSheet<Quest>().TryGetRow(result, out var row))
			{
				classification = "Standard";
				log.Debug($"[EventQuestResolver] Quest {questId} found in Excel Sheet (RowId: {row.RowId})");
				return true;
			}
			classification = "NotFound";
			log.Warning($"[EventQuestResolver] Quest row {result} was not found.");
			return false;
		}
		catch (Exception ex)
		{
			log.Debug("[EventQuestResolver] Error checking quest availability: " + ex.Message);
			classification = "Error";
			return false;
		}
	}

	public bool IsQuestAvailable(string questId)
	{
		string classification;
		return IsValidQuest(questId, out classification);
	}

	public string GetQuestName(string questId)
	{
		string item = QuestIdParser.ParseQuestId(questId).rawId;
		QuestIdType questIdType = QuestIdParser.ClassifyQuestId(questId);
		if (!uint.TryParse(item, out var result))
		{
			if (questIdType == QuestIdType.EventQuest)
			{
				return "Event Quest " + questId;
			}
			return "Unknown Quest (" + questId + ")";
		}
		try
		{
			if (dataManager.GetExcelSheet<Quest>().TryGetRow(result, out var row))
			{
				string text = row.Name.ExtractText();
				if (!string.IsNullOrEmpty(text))
				{
					if (questIdType == QuestIdType.EventQuest)
					{
						return text + " (" + questId + ")";
					}
					return text;
				}
			}
			if (questIdType == QuestIdType.EventQuest)
			{
				return "Event Quest " + questId;
			}
			return "Quest " + questId;
		}
		catch (Exception)
		{
			if (questIdType == QuestIdType.EventQuest)
			{
				return "Event Quest " + questId;
			}
			return "Quest " + questId;
		}
	}

	public List<(string QuestId, string QuestName)> GetAvailableEventQuests()
	{
		List<(string, string)> list = new List<(string, string)>();
		try
		{
			ExcelSheet<Quest> excelSheet = dataManager.GetExcelSheet<Quest>();
			if (excelSheet == null)
			{
				log.Error("[EventQuestResolver] Failed to load Quest sheet");
				return list;
			}
			foreach (Quest item in excelSheet)
			{
				if (item.RowId != 0 && item.JournalGenre.RowId == 9)
				{
					string text = item.Name.ExtractText();
					if (!string.IsNullOrEmpty(text))
					{
						list.Add((item.RowId.ToString(), text));
					}
				}
			}
			log.Information($"[EventQuestResolver] Found {list.Count} event quests");
		}
		catch (Exception ex)
		{
			log.Error("[EventQuestResolver] Error getting event quests: " + ex.Message);
		}
		return list.OrderBy(((string, string) q) => q.Item2).ToList();
	}
}
