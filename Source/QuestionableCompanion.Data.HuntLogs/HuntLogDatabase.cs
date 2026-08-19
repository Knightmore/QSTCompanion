using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace QuestionableCompanion.Data.HuntLogs;

public sealed class HuntLogDatabase
{
	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IDataManager dataManager;

	private readonly IPluginLog log;

	private bool initialized;

	public List<HuntMark> HuntMarks { get; private set; } = new List<HuntMark>();

	public Dictionary<uint, HuntLog> ClassHuntRanks { get; private set; } = new Dictionary<uint, HuntLog>();

	public Dictionary<uint, HuntLog> GrandCompanyHuntRanks { get; private set; } = new Dictionary<uint, HuntLog>();

	public HuntLogDatabase(IDalamudPluginInterface pluginInterface, IDataManager dataManager, IPluginLog log)
	{
		this.pluginInterface = pluginInterface;
		this.dataManager = dataManager;
		this.log = log;
	}

	public bool EnsureInitialized()
	{
		if (initialized)
		{
			return true;
		}
		try
		{
			HuntMarks = new List<HuntMark>();
			ClassHuntRanks = new Dictionary<uint, HuntLog>();
			GrandCompanyHuntRanks = new Dictionary<uint, HuntLog>();
			ProcessHuntMarkJson("ARRHunt.json", HuntMarks);
			PopulateClassHuntLogs();
			PopulateGrandCompanyHuntLogs();
			initialized = true;
			log.Information($"[HuntLogs] Loaded {HuntMarks.Count} ARR mark locations, {ClassHuntRanks.Count} class logs, {GrandCompanyHuntRanks.Count} GC logs");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[HuntLogs] Failed to initialize hunt-log database: " + ex.Message);
			initialized = false;
			return false;
		}
	}

	public void ResetCurrentTarget()
	{
		foreach (HuntMark huntMark2 in HuntMarks)
		{
			huntMark2.IsCurrentTarget = false;
		}
		foreach (HuntLog item in ClassHuntRanks.Values.Concat(GrandCompanyHuntRanks.Values))
		{
			for (int i = 0; i < item.HuntMarks.GetLength(0); i++)
			{
				for (int j = 0; j < item.HuntMarks.GetLength(1); j++)
				{
					HuntMark huntMark = item.HuntMarks[i, j];
					if (huntMark != null)
					{
						huntMark.IsCurrentTarget = false;
					}
				}
			}
		}
	}

	public List<HuntMark> GetClassRankMarks(uint monsterNoteId, int rankIndex, int playerLevel)
	{
		if (!EnsureInitialized() || !ClassHuntRanks.TryGetValue(monsterNoteId, out HuntLog value))
		{
			return new List<HuntMark>();
		}
		return GetRankMarks(value, rankIndex, playerLevel, preferOverworldNonFate: true);
	}

	public List<HuntMark> GetGrandCompanyRankMarks(uint grandCompanyId, int rankIndex, int playerLevel)
	{
		if (!EnsureInitialized() || !GrandCompanyHuntRanks.TryGetValue(grandCompanyId, out HuntLog value))
		{
			return new List<HuntMark>();
		}
		return GetRankMarks(value, rankIndex, playerLevel, preferOverworldNonFate: true);
	}

	public string GetMarkName(HuntMark mark)
	{
		try
		{
			BNpcName row;
			return dataManager.GetExcelSheet<BNpcName>().TryGetRow(mark.BNpcNameRowId, out row) ? row.Singular.ExtractText() : $"BNpcName {mark.BNpcNameRowId}";
		}
		catch
		{
			return $"BNpcName {mark.BNpcNameRowId}";
		}
	}

	public string GetTerritoryName(uint territoryId)
	{
		try
		{
			TerritoryType row;
			return (!dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out row)) ? territoryId.ToString() : (row.PlaceName.ValueNullable?.Name.ExtractText() ?? territoryId.ToString());
		}
		catch
		{
			return territoryId.ToString();
		}
	}

	public bool IsDutyTerritory(uint territoryId)
	{
		try
		{
			TerritoryType row;
			return dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out row) && row.ExclusiveType == 2;
		}
		catch
		{
			return false;
		}
	}

	public HuntMark ResolveBestLevelVariant(HuntMark source, int playerLevel, bool useLowest = true, bool preferOverworldNonFate = false)
	{
		uint sourceExpansion = GetExpansion(source.TerritoryId);
		List<HuntMark> list = HuntMarks.Where((HuntMark x) => x.BNpcNameRowId == source.BNpcNameRowId && GetExpansion(x.TerritoryId) == sourceExpansion).ToList();
		return new HuntMark(SelectBestLevelVariant(preferOverworldNonFate ? list.Where((HuntMark x) => !IsDutyTerritory(x.TerritoryId) && x.FateId == 0).ToList() : list.Where((HuntMark x) => IsDutyTerritory(x.TerritoryId) == IsDutyTerritory(source.TerritoryId) && ((source.FateId == 0) ? (x.FateId == 0) : (x.FateId != 0))).ToList(), playerLevel, useLowest) ?? SelectBestLevelVariant(list, playerLevel, useLowest) ?? list.FirstOrDefault() ?? source)
		{
			TargetStateSource = source,
			NeededKills = source.NeededKills,
			MonsterNoteId = source.MonsterNoteId,
			MonsterNoteSubRank = source.MonsterNoteSubRank,
			MonsterNoteCount = source.MonsterNoteCount,
			IsCurrentTarget = source.IsCurrentTarget
		};
	}

	private List<HuntMark> GetRankMarks(HuntLog huntLog, int rankIndex, int playerLevel, bool preferOverworldNonFate)
	{
		if (rankIndex < 0 || rankIndex >= huntLog.HuntMarks.GetLength(0))
		{
			return new List<HuntMark>();
		}
		List<HuntMark> list = new List<HuntMark>();
		for (int i = 0; i < huntLog.HuntMarks.GetLength(1); i++)
		{
			HuntMark huntMark = huntLog.HuntMarks[rankIndex, i];
			if (huntMark != null)
			{
				list.Add(ResolveBestLevelVariant(huntMark, playerLevel, useLowest: true, preferOverworldNonFate));
			}
		}
		return list;
	}

	private void ProcessHuntMarkJson(string fileName, List<HuntMark> marks)
	{
		string text = Path.Combine(Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName) ?? string.Empty, "Data", "HuntLogs", fileName);
		if (!File.Exists(text))
		{
			text = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "Data", "HuntLogs", fileName);
		}
		if (!File.Exists(text))
		{
			throw new FileNotFoundException("Hunt mark data file was not found: " + fileName, text);
		}
		foreach (JsonHuntMark jsonMark in JsonSerializer.Deserialize<List<JsonHuntMark>>(File.ReadAllText(text)) ?? new List<JsonHuntMark>())
		{
			IEnumerable<HuntMark> source = marks.Where((HuntMark x) => x.BNpcNameRowId == jsonMark.BnpcName && x.TerritoryId == jsonMark.TerritoryId && x.FateId == jsonMark.FateId);
			HuntMark huntMark = ((!jsonMark.Level.HasValue) ? source.FirstOrDefault() : (source.FirstOrDefault((HuntMark x) => x.Level == jsonMark.Level) ?? source.FirstOrDefault((HuntMark x) => !x.Level.HasValue)));
			if (huntMark == null)
			{
				marks.Add(new HuntMark(jsonMark.BnpcName, jsonMark.X, jsonMark.Y, jsonMark.Z, jsonMark.TerritoryId, jsonMark.FateId, jsonMark.Level));
				continue;
			}
			if (!huntMark.Level.HasValue)
			{
				huntMark.Level = jsonMark.Level;
			}
			Vector3 item = new Vector3(jsonMark.X, jsonMark.Y, jsonMark.Z);
			if (!huntMark.Positions.Contains(item))
			{
				huntMark.Positions.Add(item);
			}
		}
	}

	private void PopulateClassHuntLogs()
	{
		ExcelSheet<ClassJob> excelSheet = dataManager.GetExcelSheet<ClassJob>();
		ExcelSheet<MonsterNote> excelSheet2 = dataManager.GetExcelSheet<MonsterNote>();
		foreach (ClassJob item in from x in excelSheet.DistinctBy((ClassJob x) => x.MonsterNote.RowId)
			where x.MonsterNote.RowId != 127 && x.MonsterNote.RowId < 12
			select x)
		{
			HuntLog huntLog = new HuntLog();
			int num = (int)(item.RowId * 10000);
			for (int num2 = 0; num2 < 5; num2++)
			{
				int num3 = num + num2 * 10 + 1;
				int num4 = 0;
				int num5 = 0;
				for (int num6 = num3; num6 <= num3 + 9; num6++)
				{
					if (!excelSheet2.TryGetRow((uint)num6, out var row))
					{
						continue;
					}
					for (int num7 = 0; num7 < 4; num7++)
					{
						uint monsterNoteTargetBNpcName = GetMonsterNoteTargetBNpcName(row, num7);
						if (monsterNoteTargetBNpcName != 0)
						{
							HuntMark huntMarkForExpansion = GetHuntMarkForExpansion(monsterNoteTargetBNpcName, 0u);
							if (huntMarkForExpansion != null)
							{
								huntLog.HuntMarks[num2, num4] = new HuntMark(huntMarkForExpansion)
								{
									NeededKills = row.Count[num7],
									MonsterNoteId = (int)item.MonsterNote.RowId,
									MonsterNoteSubRank = num5,
									MonsterNoteCount = num7
								};
							}
						}
						num4++;
					}
					num5++;
				}
			}
			ClassHuntRanks[item.MonsterNote.RowId] = huntLog;
		}
	}

	private void PopulateGrandCompanyHuntLogs()
	{
		ExcelSheet<GrandCompany> excelSheet = dataManager.GetExcelSheet<GrandCompany>();
		ExcelSheet<MonsterNote> excelSheet2 = dataManager.GetExcelSheet<MonsterNote>();
		foreach (GrandCompany item in excelSheet.Where((GrandCompany x) => x.RowId != 0))
		{
			HuntLog huntLog = new HuntLog();
			uint num = item.RowId * 1000000;
			for (uint num2 = 0u; num2 < 5; num2++)
			{
				uint num3 = num + num2 * 10 + 1;
				int num4 = 0;
				int num5 = 0;
				for (uint num6 = num3; num6 <= num3 + 9; num6++)
				{
					if (!excelSheet2.TryGetRow(num6, out var row))
					{
						continue;
					}
					for (int num7 = 0; num7 < 4; num7++)
					{
						uint monsterNoteTargetBNpcName = GetMonsterNoteTargetBNpcName(row, num7);
						if (monsterNoteTargetBNpcName != 0)
						{
							HuntMark huntMarkForExpansion = GetHuntMarkForExpansion(monsterNoteTargetBNpcName, 0u);
							if (huntMarkForExpansion != null)
							{
								huntLog.HuntMarks[num2, num4] = new HuntMark(huntMarkForExpansion)
								{
									NeededKills = row.Count[num7],
									MonsterNoteId = (int)item.MonsterNote.RowId,
									MonsterNoteSubRank = num5,
									MonsterNoteCount = num7
								};
							}
						}
						num4++;
					}
					num5++;
				}
			}
			GrandCompanyHuntRanks[item.RowId] = huntLog;
		}
	}

	private HuntMark? GetHuntMarkForExpansion(uint bnpcNameRowId, uint exVersionRowId)
	{
		return HuntMarks.Where((HuntMark x) => x.BNpcNameRowId == bnpcNameRowId).FirstOrDefault((HuntMark x) => GetExpansion(x.TerritoryId) == exVersionRowId);
	}

	private uint GetExpansion(uint territoryId)
	{
		try
		{
			TerritoryType row;
			return dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out row) ? row.ExVersion.RowId : 0u;
		}
		catch
		{
			return 0u;
		}
	}

	private static HuntMark? SelectBestLevelVariant(List<HuntMark> candidates, int playerLevel, bool useLowest)
	{
		List<HuntMark> source = candidates.Where((HuntMark x) => x.Level.HasValue).ToList();
		return (useLowest ? source.OrderBy((HuntMark x) => x.Level).FirstOrDefault() : ((from x in source
			where x.Level <= playerLevel
			orderby x.Level descending
			select x).FirstOrDefault() ?? source.OrderBy((HuntMark x) => Math.Abs(x.Level.Value - playerLevel)).FirstOrDefault())) ?? candidates.FirstOrDefault();
	}

	private static uint GetMonsterNoteTargetBNpcName(MonsterNote rankEntryRow, int index)
	{
		try
		{
			RowRef<MonsterNoteTarget> rowRef = rankEntryRow.MonsterNoteTarget[index];
			if (rowRef.RowId == 0)
			{
				return 0u;
			}
			return rowRef.Value.BNpcName.RowId;
		}
		catch
		{
			return 0u;
		}
	}
}
