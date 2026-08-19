using System.Collections.Generic;

namespace QuestionableCompanion.Helpers;

public static class JobClassification
{
	public static readonly HashSet<byte> CombatJobs = new HashSet<byte>
	{
		1, 3, 19, 21, 32, 37, 2, 4, 20, 22,
		29, 30, 34, 39, 41, 5, 23, 31, 38, 6,
		24, 28, 33, 40, 7, 25, 26, 27, 35, 36,
		42
	};

	public static readonly HashSet<byte> Crafters = new HashSet<byte> { 8, 9, 10, 11, 12, 13, 14, 15 };

	public static readonly HashSet<byte> Gatherers = new HashSet<byte> { 16, 17, 18 };

	public static bool IsCombatJob(byte classJobId)
	{
		return CombatJobs.Contains(classJobId);
	}

	public static bool IsCrafter(byte classJobId)
	{
		return Crafters.Contains(classJobId);
	}

	public static bool IsGatherer(byte classJobId)
	{
		return Gatherers.Contains(classJobId);
	}

	public static string GetJobCategory(byte classJobId)
	{
		if (IsCombatJob(classJobId))
		{
			return "Combat (DoW/DoM)";
		}
		if (IsCrafter(classJobId))
		{
			return "Crafter (DoH)";
		}
		if (IsGatherer(classJobId))
		{
			return "Gatherer (DoL)";
		}
		return "Unknown";
	}
}
