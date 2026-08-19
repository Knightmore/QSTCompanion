using System;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class AutoRetainerStarterPlans
{
	public static (uint First, uint Second) Get(RetainerType type)
	{
		return type switch
		{
			RetainerType.Combat => (First: 343u, Second: 344u), 
			RetainerType.Mining => (First: 356u, Second: 357u), 
			RetainerType.Botany => (First: 369u, Second: 370u), 
			RetainerType.Fishing => (First: 382u, Second: 383u), 
			_ => throw new ArgumentOutOfRangeException("type", type, null), 
		};
	}
}
