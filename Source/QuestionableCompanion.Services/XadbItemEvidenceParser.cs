using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace QuestionableCompanion.Services;

public static class XadbItemEvidenceParser
{
	public static XadbItemEvidence Parse(string? validationJson, string? freshnessJson, DateTime sourceUpdatedUtc, string? itemsJson, string? armouryJson, string? equippedJson, DateTime nowUtc, TimeSpan? maximumAge = null)
	{
		XadbFreshnessEvidence xadbFreshnessEvidence = XadbSnapshotEvidenceParser.ValidateCollection(validationJson, "InventoryCollected", freshnessJson, sourceUpdatedUtc, nowUtc, maximumAge);
		if (!xadbFreshnessEvidence.IsValid)
		{
			return XadbItemEvidence.Invalid(xadbFreshnessEvidence.FailureReason);
		}
		HashSet<uint> hashSet = new HashSet<uint>();
		(string, string)[] array = new(string, string)[3]
		{
			("items_json", itemsJson),
			("armoury_json", armouryJson),
			("equipped_json", equippedJson)
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, string) tuple = array[i];
			var (text, _) = tuple;
			if (!TryReadItemArray(tuple.Item2, hashSet, out string failure))
			{
				return XadbItemEvidence.Invalid(text + " " + failure);
			}
		}
		return new XadbItemEvidence(IsValid: true, hashSet, string.Empty);
	}

	private static bool TryReadItemArray(string? json, HashSet<uint> ids, out string failure)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			failure = "is unavailable";
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json, XadbSnapshotEvidenceParser.JsonOptions);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Array)
			{
				failure = "is not an array";
				return false;
			}
			foreach (JsonElement item in jsonDocument.RootElement.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object || !XadbSnapshotEvidenceParser.TryGetProperty(item, "ItemId", out var value) || !TryReadUInt32(value, out var result) || result == 0)
				{
					failure = "contains a malformed item entry";
					return false;
				}
				if (XadbSnapshotEvidenceParser.TryGetProperty(item, "Quantity", out var value2) && (!TryReadInt32(value2, out var result2) || result2 <= 0))
				{
					failure = "contains an invalid quantity";
					return false;
				}
				ids.Add(result);
			}
			failure = string.Empty;
			return true;
		}
		catch (JsonException ex)
		{
			failure = "is malformed: " + ex.Message;
			return false;
		}
	}

	private static bool TryReadUInt32(JsonElement value, out uint result)
	{
		result = 0u;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result))
		{
			return true;
		}
		if (value.ValueKind == JsonValueKind.String)
		{
			return uint.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
		}
		return false;
	}

	private static bool TryReadInt32(JsonElement value, out int result)
	{
		result = 0;
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
}
