using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal static class XadbQuestSnapshotReader
{
	private const uint QuestRowIdOffset = 65536u;

	private const int DatabaseTimeoutSeconds = 2;

	private static readonly JsonDocumentOptions JsonOptions = new JsonDocumentOptions
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};

	internal static XadbQuestDatabaseReadResult Read(string databasePath, Func<uint, bool> isMsq, Func<uint, string> getQuestName)
	{
		if (string.IsNullOrWhiteSpace(databasePath))
		{
			return Unavailable("XA Database returned an empty database path");
		}
		if (!File.Exists(databasePath))
		{
			return Unavailable("the XA Database file does not exist");
		}
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
			{
				DataSource = databasePath,
				Mode = SqliteOpenMode.ReadOnly,
				Cache = SqliteCacheMode.Shared,
				Pooling = false,
				DefaultTimeout = 2
			}.ToString());
			sqliteConnection.Open();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandTimeout = 2;
			HashSet<string> columns = ReadColumnNames(sqliteConnection);
			sqliteCommand.CommandText = $"SELECT content_id,\n       character_name,\n       world,\n       active_quests_json,\n       msq_milestones_json,\n       updated_utc,\n       {OptionalColumn("retainer_count")} AS retainer_count,\n       {OptionalColumn("retainers_json")} AS retainers_json,\n       {OptionalColumn("validation_json")} AS validation_json,\n       {OptionalColumn("freshness_json")} AS freshness_json,\n       {OptionalColumn("items_json")} AS items_json,\n       {OptionalColumn("armoury_json")} AS armoury_json,\n       {OptionalColumn("equipped_json")} AS equipped_json,\n       {OptionalColumn("jobs_json")} AS jobs_json\n  FROM xa_characters";
			List<XadbQuestDatabaseRow> list = new List<XadbQuestDatabaseRow>();
			DateTime utcNow = DateTime.UtcNow;
			using (SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader())
			{
				while (sqliteDataReader.Read())
				{
					ulong contentId = (sqliteDataReader.IsDBNull(0) ? 0 : ReadContentId(sqliteDataReader.GetValue(0)));
					string text = (sqliteDataReader.IsDBNull(1) ? string.Empty : sqliteDataReader.GetString(1).Trim());
					string text2 = (sqliteDataReader.IsDBNull(2) ? string.Empty : sqliteDataReader.GetString(2).Trim());
					string characterKey = ((string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2)) ? string.Empty : (text + "@" + text2));
					string activeQuestsJson = (sqliteDataReader.IsDBNull(3) ? null : sqliteDataReader.GetString(3));
					string milestonesJson = (sqliteDataReader.IsDBNull(4) ? null : sqliteDataReader.GetString(4));
					DateTime updatedUtc = (sqliteDataReader.IsDBNull(5) ? DateTime.MinValue : ParseTimestamp(sqliteDataReader.GetString(5)));
					object retainerCount = (sqliteDataReader.IsDBNull(6) ? null : sqliteDataReader.GetValue(6));
					string retainersJson = ((sqliteDataReader.IsDBNull(7) || !(sqliteDataReader.GetValue(7) is string text3)) ? null : text3);
					string validationJson = ReadNullableString(sqliteDataReader, 8);
					string freshnessJson = ReadNullableString(sqliteDataReader, 9);
					string itemsJson = ReadNullableString(sqliteDataReader, 10);
					string armouryJson = ReadNullableString(sqliteDataReader, 11);
					string equippedJson = ReadNullableString(sqliteDataReader, 12);
					string jobsJson = ReadNullableString(sqliteDataReader, 13);
					list.Add(ParseRow(contentId, characterKey, activeQuestsJson, milestonesJson, updatedUtc, retainerCount, retainersJson, validationJson, freshnessJson, itemsJson, armouryJson, equippedJson, jobsJson, utcNow, isMsq, getQuestName));
				}
				return new XadbQuestDatabaseReadResult
				{
					IsAvailable = true,
					Rows = list
				};
			}
			string OptionalColumn(string name)
			{
				if (!columns.Contains(name))
				{
					return "NULL";
				}
				return name;
			}
		}
		catch (Exception ex)
		{
			return Unavailable(ex.Message);
		}
	}

	internal static XadbQuestDatabaseRow ParseRow(ulong contentId, string characterKey, string? activeQuestsJson, string? milestonesJson, DateTime updatedUtc, object? retainerCount, string? retainersJson, string? validationJson, string? freshnessJson, string? itemsJson, string? armouryJson, string? equippedJson, string? jobsJson, DateTime nowUtc, Func<uint, bool> isMsq, Func<uint, string> getQuestName)
	{
		int completed = 0;
		int total = 0;
		uint latestCompletedQuestId;
		string latestCompletedQuestName;
		bool flag = TryReadMilestones(milestonesJson, out completed, out total, out latestCompletedQuestId, out latestCompletedQuestName);
		uint questId;
		string questName;
		bool flag2 = TryReadActiveMsq(activeQuestsJson, isMsq, getQuestName, out questId, out questName);
		if (!flag2 && flag && completed > 0)
		{
			questId = latestCompletedQuestId;
			questName = latestCompletedQuestName;
			if (string.IsNullOrWhiteSpace(questName) && questId != 0)
			{
				questName = getQuestName(questId);
			}
			flag2 = questId != 0 || !string.IsNullOrWhiteSpace(questName);
		}
		return new XadbQuestDatabaseRow
		{
			ContentId = contentId,
			CharacterKey = characterKey,
			CompletedMsqCount = completed,
			TotalMsqCount = total,
			HasMsqProgress = flag,
			CurrentMsqId = questId,
			CurrentMsqName = questName,
			HasCurrentMsq = flag2,
			SourceUpdatedUtc = updatedUtc,
			CombatJobLevelsByAbbreviation = ParseCombatJobLevels(jobsJson),
			ItemEvidence = XadbItemEvidenceParser.Parse(validationJson, freshnessJson, updatedUtc, itemsJson, armouryJson, equippedJson, nowUtc),
			RetainerSnapshot = ParseRetainerSnapshot(contentId, retainerCount, retainersJson, validationJson, freshnessJson, updatedUtc, nowUtc)
		};
	}

	private static XadbRetainerSnapshot ParseRetainerSnapshot(ulong contentId, object? retainerCount, string? retainersJson, string? validationJson, string? freshnessJson, DateTime updatedUtc, DateTime nowUtc)
	{
		try
		{
			return XadbRetainerSnapshotParser.Parse(contentId, retainerCount, retainersJson, validationJson, freshnessJson, updatedUtc, nowUtc);
		}
		catch (Exception ex)
		{
			return XadbRetainerSnapshot.Unknown("retainer snapshot parsing failed: " + ex.Message, contentId, updatedUtc);
		}
	}

	private static IReadOnlyDictionary<string, int> ParseCombatJobLevels(string? jobsJson)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(jobsJson))
		{
			return dictionary;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(jobsJson, JsonOptions);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
			{
				return dictionary;
			}
			int result = default(int);
			foreach (JsonElement item in jsonDocument.RootElement.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					string text = ReadString(item, "Abbreviation").Trim();
					bool flag = string.IsNullOrWhiteSpace(text) || !TryReadInt32(item, "Level", out result);
					if (!flag)
					{
						bool flag2 = ((result <= 0 || result > 100) ? true : false);
						flag = flag2;
					}
					if (!flag)
					{
						dictionary[text] = Math.Max(dictionary.GetValueOrDefault(text), result);
					}
				}
			}
		}
		catch (JsonException)
		{
		}
		return dictionary;
	}

	private static string? ReadNullableString(SqliteDataReader reader, int index)
	{
		if (!reader.IsDBNull(index) && reader.GetValue(index) is string result)
		{
			return result;
		}
		return null;
	}

	private static HashSet<string> ReadColumnNames(SqliteConnection connection)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandTimeout = 2;
		sqliteCommand.CommandText = "PRAGMA table_info(xa_characters)";
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			if (!sqliteDataReader.IsDBNull(1))
			{
				hashSet.Add(sqliteDataReader.GetString(1));
			}
		}
		return hashSet;
	}

	private static bool TryReadActiveMsq(string? json, Func<uint, bool> isMsq, Func<uint, string> getQuestName, out uint questId, out string questName)
	{
		questId = 0u;
		questName = string.Empty;
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json, JsonOptions);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
			{
				return false;
			}
			foreach (JsonElement item in jsonDocument.RootElement.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object || !TryReadUInt32(item, "QuestId", out var result) || result == 0)
				{
					continue;
				}
				uint num = ((result < 65536) ? (result + 65536) : result);
				if (isMsq(num))
				{
					questId = num;
					questName = ReadString(item, "Name");
					if (string.IsNullOrWhiteSpace(questName))
					{
						questName = getQuestName(num);
					}
					if (string.IsNullOrWhiteSpace(questName))
					{
						questName = $"Quest {num}";
					}
					return true;
				}
			}
		}
		catch (JsonException)
		{
		}
		return false;
	}

	private static bool TryReadMilestones(string? json, out int completed, out int total, out uint latestCompletedQuestId, out string latestCompletedQuestName)
	{
		completed = 0;
		total = 0;
		latestCompletedQuestId = 0u;
		latestCompletedQuestName = string.Empty;
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json, JsonOptions);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
			{
				return false;
			}
			total = jsonDocument.RootElement.GetArrayLength();
			if (total == 0)
			{
				return false;
			}
			foreach (JsonElement item in jsonDocument.RootElement.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object && ReadBool(item, "IsComplete"))
				{
					completed++;
					if (TryReadUInt32(item, "QuestRowId", out var result))
					{
						latestCompletedQuestId = ((result != 0 && result < 65536) ? (result + 65536) : result);
					}
					else
					{
						latestCompletedQuestId = 0u;
					}
					latestCompletedQuestName = ReadString(item, "Label");
				}
			}
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static ulong ReadContentId(object value)
	{
		try
		{
			return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
		}
		catch (Exception ex) when (((ex is FormatException || ex is InvalidCastException || ex is OverflowException) ? 1 : 0) != 0)
		{
			return 0uL;
		}
	}

	private static DateTime ParseTimestamp(string value)
	{
		if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
		{
			return DateTime.MinValue;
		}
		return result;
	}

	private static bool TryReadUInt32(JsonElement element, string propertyName, out uint result)
	{
		if (!TryGetProperty(element, propertyName, out var value))
		{
			result = 0u;
			return false;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result))
		{
			return true;
		}
		if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
		{
			return true;
		}
		result = 0u;
		return false;
	}

	private static bool TryReadInt32(JsonElement element, string propertyName, out int result)
	{
		result = 0;
		if (!TryGetProperty(element, propertyName, out var value))
		{
			return false;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result))
		{
			return true;
		}
		if (value.ValueKind == JsonValueKind.String)
		{
			return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
		}
		return false;
	}

	private static bool ReadBool(JsonElement element, string propertyName)
	{
		if (!TryGetProperty(element, propertyName, out var value))
		{
			return false;
		}
		JsonValueKind valueKind = value.ValueKind;
		if (valueKind - 5 <= JsonValueKind.Object)
		{
			return value.GetBoolean();
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var value2))
		{
			return value2 != 0;
		}
		bool result = default(bool);
		return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out result) && result;
	}

	private static string ReadString(JsonElement element, string propertyName)
	{
		if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return string.Empty;
		}
		return value.GetString() ?? string.Empty;
	}

	private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty item in element.EnumerateObject())
			{
				if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
				{
					value = item.Value;
					return true;
				}
			}
		}
		value = default(JsonElement);
		return false;
	}

	private static XadbQuestDatabaseReadResult Unavailable(string reason)
	{
		return new XadbQuestDatabaseReadResult
		{
			IsAvailable = false,
			FailureReason = reason
		};
	}
}
