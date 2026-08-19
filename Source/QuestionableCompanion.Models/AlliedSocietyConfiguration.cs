using System.Collections.Generic;

namespace QuestionableCompanion.Models;

public class AlliedSocietyConfiguration
{
	public List<AlliedSocietyPriority> Priorities { get; set; } = new List<AlliedSocietyPriority>();

	public AlliedSocietyQuestMode QuestMode { get; set; }

	public void InitializeDefaults()
	{
		Priorities.Clear();
		for (byte b = 1; b <= 20; b++)
		{
			Priorities.Add(new AlliedSocietyPriority
			{
				SocietyId = b,
				Enabled = true,
				Order = b - 1
			});
		}
	}
}
