using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuestionableCompanion.Services;

public class QuestTrackingService : IDisposable
{
	private readonly IPluginLog log;

	private readonly Dictionary<string, HashSet<uint>> characterQuestCache = new Dictionary<string, HashSet<uint>>();

	public QuestTrackingService(IPluginLog log)
	{
		this.log = log;
	}

	public HashSet<uint> GetCharacterCompletedQuests(string characterName)
	{
		if (string.IsNullOrEmpty(characterName))
		{
			return new HashSet<uint>();
		}
		if (characterQuestCache.TryGetValue(characterName, out HashSet<uint> value))
		{
			return value;
		}
		return new HashSet<uint>();
	}

	public void UpdateCurrentCharacterQuests(string characterName)
	{
		if (string.IsNullOrEmpty(characterName))
		{
			return;
		}
		try
		{
			HashSet<uint> hashSet = new HashSet<uint>();
			for (uint num = 66000u; num < 72000; num++)
			{
				try
				{
					if (QuestManager.IsQuestComplete(num))
					{
						hashSet.Add(num);
					}
				}
				catch
				{
				}
			}
			characterQuestCache[characterName] = hashSet;
		}
		catch (Exception ex)
		{
			log.Error("[QuestTracking] Failed to update quests for " + characterName + ": " + ex.Message);
		}
	}

	public bool IsQuestCompleted(string characterName, uint questId)
	{
		return GetCharacterCompletedQuests(characterName).Contains(questId);
	}

	public void ClearCharacterCache(string characterName)
	{
		if (characterQuestCache.ContainsKey(characterName))
		{
			characterQuestCache.Remove(characterName);
			log.Debug("[QuestTracking] Cleared cache for " + characterName);
		}
	}

	public void ClearAllCache()
	{
		characterQuestCache.Clear();
		log.Information("[QuestTracking] Cleared all cached quest data");
	}

	public void Dispose()
	{
		characterQuestCache.Clear();
		log.Information("[QuestTracking] Service disposed");
	}
}
