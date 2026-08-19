using System;
using System.Globalization;
using System.Text.Json;

namespace QuestionableCompanion.Services;

public static class XadbSnapshotEvidenceParser
{
	public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(30L);

	private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2L);

	private static readonly TimeSpan TimestampConsistencyTolerance = TimeSpan.FromSeconds(5L);

	internal static readonly JsonDocumentOptions JsonOptions = new JsonDocumentOptions
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};

	public static XadbFreshnessEvidence ValidateCollection(string? validationJson, string collectionProperty, string? freshnessJson, DateTime sourceUpdatedUtc, DateTime nowUtc, TimeSpan? maximumAge = null)
	{
		if (!TryReadRequiredTrue(validationJson, collectionProperty, out string failure))
		{
			return XadbFreshnessEvidence.Invalid(failure);
		}
		if (string.IsNullOrWhiteSpace(freshnessJson))
		{
			return XadbFreshnessEvidence.Invalid("freshness_json is unavailable");
		}
		if (sourceUpdatedUtc == DateTime.MinValue)
		{
			return XadbFreshnessEvidence.Invalid("snapshot updated_utc is unavailable");
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(freshnessJson, JsonOptions);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object)
			{
				return XadbFreshnessEvidence.Invalid("freshness_json is not an object");
			}
			if (!TryReadRequiredBoolean(rootElement, "dataCollected", expected: true, out string failure2) || !TryReadRequiredBoolean(rootElement, "isOnHomeworld", expected: true, out failure2) || !TryReadRequiredBoolean(rootElement, "viewingStoredCharacter", expected: false, out failure2))
			{
				return XadbFreshnessEvidence.Invalid(failure2);
			}
			if (!TryReadTimestamp(rootElement, "savedAtUtc", out var timestamp) || !TryReadTimestamp(rootElement, "lastRefreshUtc", out var timestamp2))
			{
				return XadbFreshnessEvidence.Invalid("freshness timestamps are missing or malformed");
			}
			if (!TryGetProperty(rootElement, "trigger", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
			{
				return XadbFreshnessEvidence.Invalid("freshness trigger is missing");
			}
			DateTime dateTime = NormalizeUtc(nowUtc);
			DateTime dateTime2 = NormalizeUtc(sourceUpdatedUtc);
			TimeSpan timeSpan = maximumAge ?? MaximumSnapshotAge;
			if (timestamp > dateTime + ClockTolerance || timestamp2 > dateTime + ClockTolerance || dateTime2 > dateTime + ClockTolerance)
			{
				return XadbFreshnessEvidence.Invalid("freshness timestamps are in the future");
			}
			if (dateTime - timestamp > timeSpan || dateTime - timestamp2 > timeSpan || dateTime - dateTime2 > timeSpan)
			{
				return XadbFreshnessEvidence.Invalid("snapshot is stale");
			}
			if ((timestamp - timestamp2).Duration() > TimestampConsistencyTolerance || (dateTime2 - timestamp).Duration() > TimestampConsistencyTolerance)
			{
				return XadbFreshnessEvidence.Invalid("freshness timestamps are internally inconsistent");
			}
			return new XadbFreshnessEvidence(IsValid: true, timestamp, timestamp2, string.Empty);
		}
		catch (JsonException ex)
		{
			return XadbFreshnessEvidence.Invalid("freshness_json is malformed: " + ex.Message);
		}
	}

	private static bool TryReadRequiredTrue(string? json, string propertyName, out string failure)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			failure = "validation_json is unavailable";
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json, JsonOptions);
			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
			{
				failure = "validation_json is not an object";
				return false;
			}
			return TryReadRequiredBoolean(jsonDocument.RootElement, propertyName, expected: true, out failure);
		}
		catch (JsonException ex)
		{
			failure = "validation_json is malformed: " + ex.Message;
			return false;
		}
	}

	private static bool TryReadRequiredBoolean(JsonElement root, string propertyName, bool expected, out string failure)
	{
		JsonElement value;
		bool flag = !TryGetProperty(root, propertyName, out value);
		if (!flag)
		{
			JsonValueKind valueKind = value.ValueKind;
			bool flag2 = valueKind - 5 <= JsonValueKind.Object;
			flag = !flag2;
		}
		if (flag)
		{
			failure = propertyName + " validation is missing or malformed";
			return false;
		}
		if (value.GetBoolean() != expected)
		{
			failure = propertyName + " validation is " + value.GetBoolean().ToString().ToLowerInvariant();
			return false;
		}
		failure = string.Empty;
		return true;
	}

	private static bool TryReadTimestamp(JsonElement root, string propertyName, out DateTime timestamp)
	{
		timestamp = DateTime.MinValue;
		if (TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
		{
			return DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out timestamp);
		}
		return false;
	}

	private static DateTime NormalizeUtc(DateTime value)
	{
		return value.Kind switch
		{
			DateTimeKind.Utc => value, 
			DateTimeKind.Local => value.ToUniversalTime(), 
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc), 
		};
	}

	internal static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
	{
		if (root.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty item in root.EnumerateObject())
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
}
