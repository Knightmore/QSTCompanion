using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class XadbRetainerSnapshotParser
{
	private static readonly JsonDocumentOptions JsonOptions = new JsonDocumentOptions
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};

	public static XadbRetainerSnapshot Parse(ulong ownerContentId, object? rawCount, string? retainersJson, string? validationJson, string? freshnessJson, DateTime sourceUpdatedUtc, DateTime nowUtc, TimeSpan? maximumAge = null)
	{
		if (ownerContentId == 0L)
		{
			return XadbRetainerSnapshot.Unknown("snapshot owner ContentId is missing", 0uL);
		}
		XadbFreshnessEvidence xadbFreshnessEvidence = XadbSnapshotEvidenceParser.ValidateCollection(validationJson, "RetainersCollected", freshnessJson, sourceUpdatedUtc, nowUtc, maximumAge);
		if (!TryReadCount(rawCount, out var count) || count < 0)
		{
			return XadbRetainerSnapshot.Unknown("retainer_count is unavailable or invalid", ownerContentId, sourceUpdatedUtc);
		}
		if (string.IsNullOrWhiteSpace(retainersJson))
		{
			return XadbRetainerSnapshot.Unknown("retainers_json is unavailable", ownerContentId, sourceUpdatedUtc);
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(retainersJson, JsonOptions);
			JsonElement element = jsonDocument.RootElement;
			if (element.ValueKind == JsonValueKind.Object && TryGetProperty(element, out var value, "retainers", "Retainers"))
			{
				element = value;
			}
			if (element.ValueKind != JsonValueKind.Array)
			{
				return XadbRetainerSnapshot.Unknown("retainers_json is not an array", ownerContentId, sourceUpdatedUtc);
			}
			if (element.GetArrayLength() != count)
			{
				return XadbRetainerSnapshot.Unknown("retainer_count does not match retainers_json", ownerContentId, sourceUpdatedUtc);
			}
			if (count == 0)
			{
				return xadbFreshnessEvidence.IsValid ? XadbRetainerSnapshot.ConfirmedZero(ownerContentId, sourceUpdatedUtc, xadbFreshnessEvidence.SavedAtUtc) : XadbRetainerSnapshot.Unknown(xadbFreshnessEvidence.FailureReason, ownerContentId, sourceUpdatedUtc);
			}
			List<XadbRetainerEntry> list = new List<XadbRetainerEntry>(count);
			HashSet<ulong> hashSet = new HashSet<ulong>();
			foreach (JsonElement item in element.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
				{
					return XadbRetainerSnapshot.Unknown("a retainer entry is not an object", ownerContentId, sourceUpdatedUtc);
				}
				if (!TryReadUInt64(item, out var result, "retainerId", "RetainerId", "id", "Id") || result == 0L || !hashSet.Add(result))
				{
					return XadbRetainerSnapshot.Unknown("a retainer ID is missing, invalid, or duplicated", ownerContentId, sourceUpdatedUtc);
				}
				if (!TryReadUInt64(item, out var result2, "ownerContentId", "OwnerContentId", "contentId", "ContentId") || result2 == 0L)
				{
					return XadbRetainerSnapshot.Unknown("a retainer owner ContentId is missing or invalid", ownerContentId, sourceUpdatedUtc);
				}
				if (result2 != ownerContentId)
				{
					return XadbRetainerSnapshot.Unknown("a retainer owner ContentId does not match the snapshot row", ownerContentId, sourceUpdatedUtc, hasDefinitiveOwnershipConflict: true);
				}
				if (!TryReadString(item, out string result3, "name", "Name") || string.IsNullOrWhiteSpace(result3))
				{
					return XadbRetainerSnapshot.Unknown("a retainer name is missing", ownerContentId, sourceUpdatedUtc);
				}
				int result4;
				bool flag = !TryReadInt32(item, out result4, "level", "Level");
				if (!flag)
				{
					bool flag2 = ((result4 < 0 || result4 > 100) ? true : false);
					flag = flag2;
				}
				if (flag)
				{
					return XadbRetainerSnapshot.Unknown("a retainer level is missing or invalid", ownerContentId, sourceUpdatedUtc);
				}
				if (!TryReadUInt32(item, out var result5, "classJobId", "ClassJobId", "classJob", "ClassJob", "classId", "ClassId", "job", "Job") || result5 > 43)
				{
					return XadbRetainerSnapshot.Unknown("a retainer class/job is missing or invalid", ownerContentId, sourceUpdatedUtc);
				}
				if (!TryReadUInt32(item, out var result6, "ventureId", "VentureId", "venture", "Venture"))
				{
					return XadbRetainerSnapshot.Unknown("a retainer venture field is missing or invalid", ownerContentId, sourceUpdatedUtc);
				}
				if (!TryReadInt64(item, out var result7, "ventureCompleteUnixSeconds", "VentureCompleteUnixSeconds", "ventureCompleteUnix", "VentureCompleteUnix", "ventureComplete", "VentureComplete", "ventureCompleteAt", "VentureCompleteAt", "ventureEndsAt", "VentureEndsAt") || result7 < 0)
				{
					return XadbRetainerSnapshot.Unknown("a retainer venture completion field is missing or invalid", ownerContentId, sourceUpdatedUtc);
				}
				if (result6 == 0 != (result7 == 0))
				{
					return XadbRetainerSnapshot.Unknown("a retainer venture ID and completion time are inconsistent", ownerContentId, sourceUpdatedUtc);
				}
				list.Add(new XadbRetainerEntry(result, result2, result3.Trim(), result4, result5, result6, result7));
			}
			return new XadbRetainerSnapshot(ownerContentId, XadbRetainerRosterStatus.Populated, count, list, xadbFreshnessEvidence.IsValid ? string.Empty : xadbFreshnessEvidence.FailureReason, sourceUpdatedUtc, xadbFreshnessEvidence.SavedAtUtc, xadbFreshnessEvidence.IsValid);
		}
		catch (JsonException ex)
		{
			return XadbRetainerSnapshot.Unknown("retainers_json is malformed: " + ex.Message, ownerContentId, sourceUpdatedUtc);
		}
	}

	private static bool TryReadCount(object? value, out int count)
	{
		if (value != null && !(value is DBNull))
		{
			if (!(value is int num))
			{
				if (value is long num2 && num2 >= int.MinValue && num2 <= int.MaxValue)
				{
					count = (int)num2;
					return true;
				}
				return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
			}
			count = num;
			return true;
		}
		count = 0;
		return false;
	}

	private static bool TryReadUInt64(JsonElement element, out ulong result, params string[] names)
	{
		result = 0uL;
		if (TryGetProperty(element, out var value, names))
		{
			return TryConvertUInt64(value, out result);
		}
		return false;
	}

	private static bool TryReadUInt32(JsonElement element, out uint result, params string[] names)
	{
		if (TryReadUInt64(element, out var result2, names) && result2 <= uint.MaxValue)
		{
			result = (uint)result2;
			return true;
		}
		result = 0u;
		return false;
	}

	private static bool TryReadInt32(JsonElement element, out int result, params string[] names)
	{
		if (TryGetProperty(element, out var value, names))
		{
			if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result))
			{
				return true;
			}
			if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
			{
				return true;
			}
		}
		result = 0;
		return false;
	}

	private static bool TryReadInt64(JsonElement element, out long result, params string[] names)
	{
		if (TryGetProperty(element, out var value, names))
		{
			if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
			{
				return true;
			}
			if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
			{
				return true;
			}
		}
		result = 0L;
		return false;
	}

	private static bool TryReadString(JsonElement element, out string result, params string[] names)
	{
		if (TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String)
		{
			result = value.GetString() ?? string.Empty;
			return true;
		}
		result = string.Empty;
		return false;
	}

	private static bool TryConvertUInt64(JsonElement value, out ulong result)
	{
		if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out result))
		{
			return true;
		}
		if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
		{
			return true;
		}
		result = 0uL;
		return false;
	}

	private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (names.Any((string name) => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
				{
					value = property.Value;
					return true;
				}
			}
		}
		value = default(JsonElement);
		return false;
	}
}
