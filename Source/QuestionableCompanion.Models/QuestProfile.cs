using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

[Serializable]
public class QuestProfile
{
	public string Name { get; set; } = "New Profile";

	public List<string> Characters { get; set; } = new List<string>();

	public List<QuestConfig> Quests { get; set; } = new List<QuestConfig>();

	public bool IsActive { get; set; }
}
