using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class AlliedSocietyQuestSelector
{
	private readonly QuestionableIPC questionableIpc;

	private readonly IPluginLog log;

	public AlliedSocietyQuestSelector(QuestionableIPC questionableIpc, IPluginLog log)
	{
		this.questionableIpc = questionableIpc;
		this.log = log;
	}

	public List<string> SelectQuestsForCharacter(string characterId, int remainingAllowances, List<AlliedSocietyPriority> priorities, AlliedSocietyQuestMode mode)
	{
		List<string> list = new List<string>();
		int num = remainingAllowances;
		List<AlliedSocietyPriority> list2 = (from p in priorities
			where p.Enabled
			orderby p.Order
			select p).ToList();
		log.Debug($"[AlliedSociety] Selecting quests for {characterId}. Allowances: {remainingAllowances}, Mode: {mode}");
		foreach (AlliedSocietyPriority item in list2)
		{
			if (num <= 0)
			{
				log.Debug("[AlliedSociety] No allowances left, stopping selection");
				break;
			}
			byte societyId = item.SocietyId;
			List<string> alliedSocietyOptimalQuests = questionableIpc.GetAlliedSocietyOptimalQuests(societyId);
			if (alliedSocietyOptimalQuests.Count == 0)
			{
				continue;
			}
			List<string> list3 = new List<string>();
			foreach (string item2 in alliedSocietyOptimalQuests)
			{
				if (questionableIpc.IsReadyToAcceptQuest(item2))
				{
					list3.Add(item2);
				}
			}
			if (list3.Count == 0)
			{
				continue;
			}
			if (mode == AlliedSocietyQuestMode.OnlyThreePerSociety)
			{
				foreach (string item3 in list3.Take(3).ToList())
				{
					if (num > 0)
					{
						list.Add(item3);
						num--;
						continue;
					}
					break;
				}
				continue;
			}
			foreach (string item4 in list3)
			{
				if (num > 0)
				{
					list.Add(item4);
					num--;
					continue;
				}
				break;
			}
		}
		log.Information($"[AlliedSociety] Selected {list.Count} quests for {characterId}");
		return list;
	}
}
