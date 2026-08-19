using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class HuntLogPendingMark
{
	public string CharacterName { get; set; } = string.Empty;

	public bool IsGrandCompanyLog { get; set; }

	public int Rank { get; set; }

	public uint BNpcNameRowId { get; set; }

	public uint TerritoryId { get; set; }

	public int MonsterNoteId { get; set; }

	public int MonsterNoteSubRank { get; set; }

	public int MonsterNoteCount { get; set; }

	public int RemainingKills { get; set; }

	public int ConsecutiveNoProgressScans { get; set; }

	public bool Deferred { get; set; }

	public HuntLogPendingMark Clone()
	{
		return (HuntLogPendingMark)MemberwiseClone();
	}
}
