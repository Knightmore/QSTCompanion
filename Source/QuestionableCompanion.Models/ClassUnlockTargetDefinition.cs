using System.Collections.Generic;

namespace QuestionableCompanion.Models;

public sealed record ClassUnlockTargetDefinition(uint ClassJobId, int ExpArrayIndex, string Abbreviation, string Name, ClassUnlockCategory Category, ClassUnlockHub Hub, IReadOnlyList<string> QuestIds, int RequiredCombatLevel, string Requirement, bool IsAvailable = true)
{
	public bool CanContinueStopPointRotation
	{
		get
		{
			if (IsAvailable)
			{
				return Category == ClassUnlockCategory.Expansion;
			}
			return false;
		}
	}
}
