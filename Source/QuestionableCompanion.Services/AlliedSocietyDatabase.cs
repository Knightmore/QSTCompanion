using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class AlliedSocietyDatabase
{
	private readonly Configuration configuration;

	private readonly IPluginLog log;

	public AlliedSocietyDatabase(Configuration configuration, IPluginLog log)
	{
		this.configuration = configuration;
		this.log = log;
		if (configuration.AlliedSociety.RotationConfig.Priorities.Count == 0)
		{
			configuration.AlliedSociety.RotationConfig.InitializeDefaults();
			SaveToConfig();
		}
	}

	public void SaveToConfig()
	{
		configuration.Save();
	}

	public void UpdateCharacterProgress(string characterId, byte societyId, int rank, bool isMaxRank)
	{
		if (!configuration.AlliedSociety.CharacterProgress.ContainsKey(characterId))
		{
			configuration.AlliedSociety.CharacterProgress[characterId] = new List<AlliedSocietyProgress>();
		}
		List<AlliedSocietyProgress> list = configuration.AlliedSociety.CharacterProgress[characterId];
		AlliedSocietyProgress alliedSocietyProgress = list.FirstOrDefault((AlliedSocietyProgress p) => p.SocietyId == societyId);
		if (alliedSocietyProgress != null)
		{
			alliedSocietyProgress.CurrentRank = rank;
			alliedSocietyProgress.IsMaxRank = isMaxRank;
		}
		else
		{
			list.Add(new AlliedSocietyProgress
			{
				CharacterId = characterId,
				SocietyId = societyId,
				CurrentRank = rank,
				IsMaxRank = isMaxRank
			});
		}
		SaveToConfig();
	}

	public AlliedSocietyProgress? GetProgress(string characterId, byte societyId)
	{
		if (configuration.AlliedSociety.CharacterProgress.TryGetValue(characterId, out List<AlliedSocietyProgress> value))
		{
			return value.FirstOrDefault((AlliedSocietyProgress p) => p.SocietyId == societyId);
		}
		return null;
	}

	public AlliedSocietyCharacterStatus GetCharacterStatus(string characterId)
	{
		if (!configuration.AlliedSociety.CharacterStatuses.ContainsKey(characterId))
		{
			configuration.AlliedSociety.CharacterStatuses[characterId] = new AlliedSocietyCharacterStatus
			{
				CharacterId = characterId,
				Status = AlliedSocietyRotationStatus.Ready
			};
			SaveToConfig();
		}
		return configuration.AlliedSociety.CharacterStatuses[characterId];
	}

	public void UpdateCharacterStatus(string characterId, AlliedSocietyRotationStatus status)
	{
		GetCharacterStatus(characterId).Status = status;
		SaveToConfig();
	}

	public void SetCharacterComplete(string characterId, DateTime completionDate)
	{
		AlliedSocietyCharacterStatus characterStatus = GetCharacterStatus(characterId);
		characterStatus.Status = AlliedSocietyRotationStatus.Complete;
		characterStatus.LastCompletionDate = completionDate;
		characterStatus.ImportedQuestIds.Clear();
		SaveToConfig();
	}

	public void CheckAndResetExpired(DateTime nextResetDate)
	{
		List<string> charactersNeedingReset = GetCharactersNeedingReset(nextResetDate);
		foreach (string item in charactersNeedingReset)
		{
			log.Information("[AlliedSociety] Resetting status for character " + item);
			AlliedSocietyCharacterStatus characterStatus = GetCharacterStatus(item);
			characterStatus.Status = AlliedSocietyRotationStatus.Ready;
			characterStatus.ImportedQuestIds.Clear();
		}
		if (charactersNeedingReset.Count > 0)
		{
			SaveToConfig();
		}
	}

	public List<string> GetCharactersNeedingReset(DateTime nextResetDate)
	{
		List<string> list = new List<string>();
		DateTime dateTime = nextResetDate.AddDays(-1.0);
		foreach (KeyValuePair<string, AlliedSocietyCharacterStatus> characterStatus in configuration.AlliedSociety.CharacterStatuses)
		{
			AlliedSocietyCharacterStatus value = characterStatus.Value;
			if (value.Status == AlliedSocietyRotationStatus.Ready)
			{
				continue;
			}
			if (value.LastCompletionDate.HasValue)
			{
				if (value.LastCompletionDate.Value < dateTime)
				{
					list.Add(characterStatus.Key);
				}
			}
			else
			{
				list.Add(characterStatus.Key);
			}
		}
		return list;
	}

	public void ClearAllStatuses()
	{
		foreach (KeyValuePair<string, AlliedSocietyCharacterStatus> characterStatus in configuration.AlliedSociety.CharacterStatuses)
		{
			characterStatus.Value.Status = AlliedSocietyRotationStatus.Ready;
			characterStatus.Value.ImportedQuestIds.Clear();
		}
		SaveToConfig();
	}

	public void AddImportedQuest(string characterId, string questId)
	{
		AlliedSocietyCharacterStatus characterStatus = GetCharacterStatus(characterId);
		if (!characterStatus.ImportedQuestIds.Contains(questId))
		{
			characterStatus.ImportedQuestIds.Add(questId);
			SaveToConfig();
		}
	}

	public void ClearImportedQuests(string characterId)
	{
		GetCharacterStatus(characterId).ImportedQuestIds.Clear();
		SaveToConfig();
	}
}
