using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Services;

internal static class JobStoneGearsetCollectionExtensions
{
	public static bool Exists<T>(this IReadOnlyList<T> values, Func<T, bool> predicate)
	{
		for (int i = 0; i < values.Count; i++)
		{
			if (predicate(values[i]))
			{
				return true;
			}
		}
		return false;
	}
}
