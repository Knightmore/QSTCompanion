using System;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace QuestionableCompanion.Helpers;

public static class WorldNameHelper
{
	public static string GetWorldName(ushort worldId)
	{
		try
		{
			if (Plugin.DataManager == null)
			{
				return worldId.ToString();
			}
			ExcelSheet<World> excelSheet = Plugin.DataManager.GetExcelSheet<World>();
			if (excelSheet == null)
			{
				return worldId.ToString();
			}
			if (excelSheet.TryGetRow(worldId, out var row))
			{
				return row.Name.ExtractText();
			}
			return worldId.ToString();
		}
		catch (Exception)
		{
			return worldId.ToString();
		}
	}

	public static string FormatCharacterWithWorld(string characterName, ushort worldId)
	{
		return characterName + "@" + GetWorldName(worldId);
	}
}
