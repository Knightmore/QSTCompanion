using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace QuestionableCompanion.Services;

public class DataCenterService
{
	private readonly IDataManager dataManager;

	private readonly IPluginLog log;

	private readonly Dictionary<string, string> worldToDCCache = new Dictionary<string, string>();

	private readonly Dictionary<string, string> dataCenterToRegion = new Dictionary<string, string>
	{
		{ "Chaos", "EU" },
		{ "Light", "EU" },
		{ "Shadow", "EU" },
		{ "Aether", "NA" },
		{ "Primal", "NA" },
		{ "Crystal", "NA" },
		{ "Dynamis", "NA" },
		{ "Elemental", "JP" },
		{ "Gaia", "JP" },
		{ "Mana", "JP" },
		{ "Meteor", "JP" },
		{ "Materia", "OCE" },
		{ "陆行鸟", "Others" },
		{ "莫古力", "Others" },
		{ "猫小胖", "Others" },
		{ "豆豆柴", "Others" }
	};

	public DataCenterService(IDataManager dataManager, IPluginLog log)
	{
		this.dataManager = dataManager;
		this.log = log;
	}

	public void InitializeWorldMapping()
	{
		try
		{
			ExcelSheet<World> excelSheet = dataManager.GetExcelSheet<World>();
			if (excelSheet == null)
			{
				return;
			}
			int num = 0;
			int num2 = 0;
			foreach (World item in excelSheet)
			{
				if (item.RowId == 0)
				{
					continue;
				}
				string text = item.Name.ExtractText();
				if (string.IsNullOrEmpty(text))
				{
					num2++;
					continue;
				}
				WorldDCGroupType? valueNullable = item.DataCenter.ValueNullable;
				if (!valueNullable.HasValue)
				{
					num2++;
					continue;
				}
				string text2 = valueNullable.Value.Name.ExtractText();
				if (string.IsNullOrEmpty(text2))
				{
					num2++;
					continue;
				}
				if (!item.IsPublic)
				{
					num2++;
					continue;
				}
				string regionForDataCenter = GetRegionForDataCenter(text2);
				worldToDCCache[text.ToLower()] = regionForDataCenter;
				num++;
				if (num > 10)
				{
					_ = regionForDataCenter != "Others";
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private string GetRegionForDataCenter(string dataCenterName)
	{
		if (dataCenterToRegion.TryGetValue(dataCenterName, out string value))
		{
			return value;
		}
		return "Others";
	}

	public string GetDataCenterForWorld(string worldName)
	{
		if (string.IsNullOrEmpty(worldName))
		{
			return "Unknown";
		}
		string key = worldName.ToLower();
		if (worldToDCCache.TryGetValue(key, out string value))
		{
			return value;
		}
		return "Unknown";
	}

	public Dictionary<string, List<string>> GroupCharactersByDataCenter(List<string> characters)
	{
		Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>
		{
			{
				"EU",
				new List<string>()
			},
			{
				"NA",
				new List<string>()
			},
			{
				"JP",
				new List<string>()
			},
			{
				"OCE",
				new List<string>()
			},
			{
				"Others",
				new List<string>()
			},
			{
				"Unknown",
				new List<string>()
			}
		};
		foreach (string character in characters)
		{
			try
			{
				string[] array = character.Split('@');
				if (array.Length != 2)
				{
					dictionary["Unknown"].Add(character);
					continue;
				}
				string worldName = array[1];
				string dataCenterForWorld = GetDataCenterForWorld(worldName);
				if (!dictionary.ContainsKey(dataCenterForWorld))
				{
					dictionary[dataCenterForWorld] = new List<string>();
				}
				dictionary[dataCenterForWorld].Add(character);
			}
			catch (Exception)
			{
				dictionary["Unknown"].Add(character);
			}
		}
		foreach (KeyValuePair<string, List<string>> item in dictionary.Where((KeyValuePair<string, List<string>> g) => g.Value.Count > 0))
		{
			_ = item;
		}
		return dictionary;
	}

	public List<string> GetAvailableDataCenters(Dictionary<string, List<string>> charactersByDataCenter)
	{
		List<string> list = new List<string> { "All" };
		string[] array = new string[6] { "EU", "NA", "JP", "OCE", "Others", "Unknown" };
		foreach (string text in array)
		{
			if (charactersByDataCenter.TryGetValue(text, out List<string> value) && value.Count > 0)
			{
				list.Add(text);
			}
		}
		return list;
	}

	public List<string> GetCharactersForDataCenter(List<string> allCharacters, string dataCenterName, Dictionary<string, List<string>> charactersByDataCenter)
	{
		if (dataCenterName == "All")
		{
			return allCharacters;
		}
		if (charactersByDataCenter.TryGetValue(dataCenterName, out List<string> value))
		{
			return value;
		}
		return new List<string>();
	}
}
