using System.Collections.Generic;
using System.Numerics;

namespace QuestionableCompanion.Data.HuntLogs;

public sealed class HuntMark
{
	private bool isCurrentTarget;

	public HuntMark? TargetStateSource { get; set; }

	public uint BNpcNameRowId { get; }

	public uint FateId { get; }

	public uint TerritoryId { get; }

	public byte? Level { get; set; }

	public List<Vector3> Positions { get; private set; } = new List<Vector3>();

	public int NeededKills { get; set; }

	public int MonsterNoteId { get; set; }

	public int MonsterNoteSubRank { get; set; }

	public int MonsterNoteCount { get; set; }

	public bool IsCurrentTarget
	{
		get
		{
			return TargetStateSource?.IsCurrentTarget ?? isCurrentTarget;
		}
		set
		{
			if (TargetStateSource != null)
			{
				TargetStateSource.IsCurrentTarget = value;
			}
			else
			{
				isCurrentTarget = value;
			}
		}
	}

	public HuntMark(uint bnpcNameRowId, float x, float y, float z, uint territoryId, uint fateId, byte? level = null)
	{
		BNpcNameRowId = bnpcNameRowId;
		Positions.Add(new Vector3(x, y, z));
		TerritoryId = territoryId;
		FateId = fateId;
		Level = level;
	}

	public HuntMark(HuntMark original)
	{
		BNpcNameRowId = original.BNpcNameRowId;
		Positions = new List<Vector3>(original.Positions);
		TerritoryId = original.TerritoryId;
		FateId = original.FateId;
		Level = original.Level;
		NeededKills = original.NeededKills;
		MonsterNoteId = original.MonsterNoteId;
		MonsterNoteSubRank = original.MonsterNoteSubRank;
		MonsterNoteCount = original.MonsterNoteCount;
		TargetStateSource = original.TargetStateSource;
	}
}
