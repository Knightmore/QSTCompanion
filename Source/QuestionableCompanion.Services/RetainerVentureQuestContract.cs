using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Services;

internal static class RetainerVentureQuestContract
{
	public static readonly IReadOnlyList<RetainerVentureQuest> All = new global::_003C_003Ez__ReadOnlyArray<RetainerVentureQuest>(new RetainerVentureQuest[3]
	{
		new RetainerVentureQuest(66969u, "1433"),
		new RetainerVentureQuest(66968u, "1432"),
		new RetainerVentureQuest(66970u, "1434")
	});

	public static RetainerVentureQuest Resolve(byte nativeStartTown)
	{
		return nativeStartTown switch
		{
			1 => All[0], 
			2 => All[1], 
			3 => All[2], 
			_ => throw new InvalidOperationException($"Native starting town {nativeStartTown} is not supported for the venture-unlock quest."), 
		};
	}
}
