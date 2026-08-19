using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public sealed class XADatabaseIPC : IDisposable
{
	public const string RepositoryUrl = "https://aethertek.io/x.json";

	private const string InternalName = "XADatabase";

	private const string IsReadyEndpoint = "XA.Database.IsReady";

	private const string GetAccountCharacterListJsonEndpoint = "XA.Database.GetAccountCharacterListJson";

	private const string GetDbPathEndpoint = "XA.Database.GetDbPath";

	private const string SaveEndpoint = "XA.Database.Save";

	private static readonly TimeSpan SavePollTimeout = TimeSpan.FromSeconds(12L);

	private static readonly TimeSpan SavePollInterval = TimeSpan.FromMilliseconds(500L);

	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private readonly MSQProgressionService msqProgressionService;

	private readonly CombatJobResolver combatJobResolver;

	private readonly IFramework framework;

	private readonly ICallGateSubscriber<bool> isReadySubscriber;

	private readonly ICallGateSubscriber<string> getAccountCharacterListJsonSubscriber;

	private readonly ICallGateSubscriber<string> getDbPathSubscriber;

	private readonly ICallGateSubscriber<object> saveSubscriber;

	public bool IsInstalled => pluginInterface.InstalledPlugins.Any((IExposedPlugin plugin) => string.Equals(plugin.InternalName, "XADatabase", StringComparison.Ordinal));

	public XADatabaseIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, MSQProgressionService msqProgressionService, CombatJobResolver combatJobResolver, IFramework framework)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		this.msqProgressionService = msqProgressionService;
		this.combatJobResolver = combatJobResolver;
		this.framework = framework;
		isReadySubscriber = pluginInterface.GetIpcSubscriber<bool>("XA.Database.IsReady");
		getAccountCharacterListJsonSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetAccountCharacterListJson");
		getDbPathSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetDbPath");
		saveSubscriber = pluginInterface.GetIpcSubscriber<object>("XA.Database.Save");
	}

	public bool TryGetCharacterProgress(out IReadOnlyDictionary<string, XadbCharacterProgress> characters, out string status)
	{
		XadbProgressReadSummary summary;
		return TryGetCharacterProgress(out characters, out summary, out status);
	}

	public bool TryGetCharacterProgress(out IReadOnlyDictionary<string, XadbCharacterProgress> characters, out XadbProgressReadSummary summary, out string status)
	{
		characters = new Dictionary<string, XadbCharacterProgress>(StringComparer.OrdinalIgnoreCase);
		summary = new XadbProgressReadSummary();
		IExposedPlugin exposedPlugin = pluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin plugin) => string.Equals(plugin.InternalName, "XADatabase", StringComparison.Ordinal));
		if (exposedPlugin == null)
		{
			status = "XA Database is not installed.";
			return false;
		}
		if (!exposedPlugin.IsLoaded)
		{
			status = "XA Database is installed but disabled.";
			return false;
		}
		IReadOnlyDictionary<string, XadbCharacterProgress> readOnlyDictionary;
		try
		{
			if (!isReadySubscriber.InvokeFunc())
			{
				status = "XA Database is not ready.";
				return false;
			}
			string text = getAccountCharacterListJsonSubscriber.InvokeFunc();
			if (string.IsNullOrWhiteSpace(text))
			{
				status = "XA Database returned an empty character roster.";
				return false;
			}
			readOnlyDictionary = (characters = ParseCharacterProgress(text));
			if (readOnlyDictionary.Count == 0)
			{
				status = "XA Database returned no usable character snapshots.";
				return false;
			}
		}
		catch (Exception ex)
		{
			status = "XA Database roster IPC failed: " + ex.Message;
			log.Warning("[XADatabaseIPC] " + status);
			return false;
		}
		XadbQuestDatabaseReadResult xadbQuestDatabaseReadResult;
		try
		{
			xadbQuestDatabaseReadResult = XadbQuestSnapshotReader.Read(getDbPathSubscriber.InvokeFunc(), msqProgressionService.IsMSQ, msqProgressionService.GetQuestName);
		}
		catch (Exception ex2)
		{
			xadbQuestDatabaseReadResult = new XadbQuestDatabaseReadResult
			{
				IsAvailable = false,
				FailureReason = "database-path IPC failed: " + ex2.Message
			};
		}
		summary = MergeSnapshotProgress(readOnlyDictionary, xadbQuestDatabaseReadResult);
		status = (xadbQuestDatabaseReadResult.IsAvailable ? "XA Database roster and quest snapshot were available." : ("XA Database quest snapshot was unavailable (" + xadbQuestDatabaseReadResult.FailureReason + "); roster job data remains available."));
		return true;
	}

	internal IReadOnlyDictionary<string, XadbCharacterProgress> ParseCharacterProgress(string json)
	{
		Dictionary<string, XadbCharacterProgress> dictionary = new Dictionary<string, XadbCharacterProgress>(StringComparer.OrdinalIgnoreCase);
		using JsonDocument jsonDocument = JsonDocument.Parse(json, new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip
		});
		if (!TryGetCharacterArray(jsonDocument.RootElement, out var characters))
		{
			return dictionary;
		}
		foreach (JsonElement item in characters.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			string text = ReadCharacterKey(item);
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			Dictionary<uint, int> dictionary2 = ReadCombatJobLevels(item);
			if (dictionary2.Count == 0)
			{
				int num = ReadInt(item, "highestCombatJobLevel", "level");
				uint num2 = (uint)Math.Max(0, ReadInt(item, "highestCombatJobId", "jobId"));
				if (num > 0 && num2 != 0)
				{
					dictionary2[num2] = num;
				}
			}
			CombatJobResolution combatJobResolution = combatJobResolver.Resolve(dictionary2, Array.Empty<uint>(), inventoryEvidenceValid: false);
			IReadOnlyDictionary<uint, int> readOnlyDictionary = combatJobResolver.MapExplicitObservedLevels(dictionary2, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
			dictionary[text] = new XadbCharacterProgress
			{
				ContentId = ReadUInt64(item, "contentId"),
				CharacterKey = text,
				ObservedCombatJobLevels = readOnlyDictionary.ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value),
				CombatJobLevels = combatJobResolution.Levels.ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value),
				HighestCombatJobId = combatJobResolution.HighestJobId,
				HighestCombatJobLevel = combatJobResolution.HighestLevel,
				HasLevel = (readOnlyDictionary.Count > 0),
				SourceUpdatedUtc = ReadTimestamp(item)
			};
		}
		return dictionary;
	}

	public void Dispose()
	{
	}

	public async Task<XadbRetainerSnapshot> RequestFreshRetainerSnapshotAsync(ulong expectedContentId, string expectedCharacterKey, DateTime baselineUpdatedUtc, CancellationToken token)
	{
		if (expectedContentId == 0L || string.IsNullOrWhiteSpace(expectedCharacterKey))
		{
			return XadbRetainerSnapshot.Unknown("fresh-save target identity is incomplete", expectedContentId);
		}
		string lastFailure = "XA Database did not publish a fresh exact-owner snapshot";
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			token.ThrowIfCancellationRequested();
			DateTime requestedUtc = DateTime.UtcNow;
			try
			{
				await framework.RunOnFrameworkThread((Action)saveSubscriber.InvokeAction);
			}
			catch (Exception ex)
			{
				lastFailure = $"XA.Database.Save attempt {attempt} failed: {ex.Message}";
				log.Warning("[XADatabaseIPC] " + lastFailure);
				continue;
			}
			DateTime deadline = DateTime.UtcNow + SavePollTimeout;
			while (DateTime.UtcNow < deadline)
			{
				token.ThrowIfCancellationRequested();
				if (TryGetCharacterProgress(out IReadOnlyDictionary<string, XadbCharacterProgress> characters, out XadbProgressReadSummary _, out string status))
				{
					XadbCharacterProgress xadbCharacterProgress = characters.Values.FirstOrDefault((XadbCharacterProgress character) => character.ContentId == expectedContentId && string.Equals(character.CharacterKey, expectedCharacterKey, StringComparison.OrdinalIgnoreCase));
					XadbCharacterProgress xadbCharacterProgress2 = characters.Values.FirstOrDefault((XadbCharacterProgress character) => string.Equals(character.CharacterKey, expectedCharacterKey, StringComparison.OrdinalIgnoreCase));
					if (xadbCharacterProgress == null && xadbCharacterProgress2 != null && xadbCharacterProgress2.ContentId != 0L && xadbCharacterProgress2.ContentId != expectedContentId)
					{
						return XadbRetainerSnapshot.Unknown($"fresh save mapped {expectedCharacterKey} to ContentId {xadbCharacterProgress2.ContentId}, not {expectedContentId}", xadbCharacterProgress2.ContentId, xadbCharacterProgress2.SourceUpdatedUtc, hasDefinitiveOwnershipConflict: true);
					}
					if (xadbCharacterProgress != null && XadbFreshSaveLogic.IsNewerExactOwnerSnapshot(xadbCharacterProgress.RetainerSnapshot, expectedContentId, baselineUpdatedUtc, requestedUtc))
					{
						return xadbCharacterProgress.RetainerSnapshot;
					}
					lastFailure = ((xadbCharacterProgress == null) ? "fresh save did not contain the exact target owner" : xadbCharacterProgress.RetainerSnapshot.FailureReason);
				}
				else
				{
					lastFailure = status;
				}
				await Task.Delay(SavePollInterval, token);
			}
			log.Warning($"[XADatabaseIPC] Fresh collection attempt {attempt}/{3} did not validate for {expectedCharacterKey}: {lastFailure}");
		}
		return XadbRetainerSnapshot.Unknown(lastFailure, expectedContentId);
	}

	private XadbProgressReadSummary MergeSnapshotProgress(IReadOnlyDictionary<string, XadbCharacterProgress> characters, XadbQuestDatabaseReadResult questRead)
	{
		if (!questRead.IsAvailable)
		{
			return new XadbProgressReadSummary
			{
				RosterRows = characters.Count,
				QuestDatabaseAvailable = false,
				RetainerUnknownCharacters = characters.Count
			};
		}
		Dictionary<ulong, XadbCharacterProgress> dictionary = (from character in characters.Values
			where character.ContentId != 0
			group character by character.ContentId).ToDictionary((IGrouping<ulong, XadbCharacterProgress> group) => group.Key, (IGrouping<ulong, XadbCharacterProgress> group) => group.First());
		HashSet<XadbCharacterProgress> hashSet = new HashSet<XadbCharacterProgress>();
		int num = 0;
		foreach (XadbQuestDatabaseRow row in questRead.Rows)
		{
			XadbCharacterProgress value = null;
			if (row.ContentId != 0L)
			{
				dictionary.TryGetValue(row.ContentId, out value);
			}
			if (value == null && !string.IsNullOrWhiteSpace(row.CharacterKey) && characters.TryGetValue(row.CharacterKey, out value))
			{
				num++;
			}
			if (value != null)
			{
				hashSet.Add(value);
				value.HasQuestSnapshotRow = true;
				bool flag = row.ContentId != 0L && row.ContentId == value.ContentId;
				value.RetainerSnapshot = (flag ? row.RetainerSnapshot : XadbRetainerSnapshot.Unknown("retainer snapshot row ContentId did not exactly match the roster owner", row.ContentId, row.SourceUpdatedUtc, hasDefinitiveOwnershipConflict: true));
				value.ObservedCombatJobLevelsByAbbreviation = row.CombatJobLevelsByAbbreviation;
				value.ObservedCombatJobLevels = combatJobResolver.MapExplicitObservedLevels(value.ObservedCombatJobLevels, value.ObservedCombatJobLevelsByAbbreviation).ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value);
				value.InventoryEvidenceValid = flag && row.ItemEvidence.IsValid;
				XadbCharacterProgress xadbCharacterProgress = value;
				IReadOnlySet<uint> verifiedSoulCrystalItemIds;
				if (!value.InventoryEvidenceValid)
				{
					IReadOnlySet<uint> readOnlySet = new HashSet<uint>();
					verifiedSoulCrystalItemIds = readOnlySet;
				}
				else
				{
					verifiedSoulCrystalItemIds = row.ItemEvidence.ItemIds;
				}
				xadbCharacterProgress.VerifiedSoulCrystalItemIds = verifiedSoulCrystalItemIds;
				value.JobEvidenceSource = (value.InventoryEvidenceValid ? "XADBValidatedInventory" : "XADBConservativeFallback");
				CombatJobResolution combatJobResolution = combatJobResolver.ResolveCombined(value.ObservedCombatJobLevels, value.ObservedCombatJobLevelsByAbbreviation, value.VerifiedSoulCrystalItemIds, value.InventoryEvidenceValid);
				value.CombatJobLevels = combatJobResolution.Levels.ToDictionary((KeyValuePair<uint, int> entry) => entry.Key, (KeyValuePair<uint, int> entry) => entry.Value);
				value.HighestCombatJobId = combatJobResolution.HighestJobId;
				value.HighestCombatJobLevel = combatJobResolution.HighestLevel;
				value.HasLevel = value.ObservedCombatJobLevels.Count > 0;
				if (row.HasMsqProgress)
				{
					value.CompletedMsqCount = row.CompletedMsqCount;
					value.TotalMsqCount = row.TotalMsqCount;
					value.HasMsqProgress = true;
				}
				if (row.HasCurrentMsq)
				{
					value.CurrentMsqId = row.CurrentMsqId;
					value.CurrentMsqName = row.CurrentMsqName;
					value.HasCurrentMsq = true;
				}
				if (row.SourceUpdatedUtc > value.SourceUpdatedUtc)
				{
					value.SourceUpdatedUtc = row.SourceUpdatedUtc;
				}
			}
		}
		return new XadbProgressReadSummary
		{
			RosterRows = characters.Count,
			QuestRows = questRead.Rows.Count,
			QuestMatchedCharacters = hashSet.Count,
			NameFallbackMatches = num,
			QuestDatabaseAvailable = true,
			RetainerKnownCharacters = hashSet.Count((XadbCharacterProgress character) => character.RetainerSnapshot.Status != XadbRetainerRosterStatus.Unknown),
			RetainerUnknownCharacters = characters.Values.Count((XadbCharacterProgress character) => character.RetainerSnapshot.Status == XadbRetainerRosterStatus.Unknown)
		};
	}

	private static bool TryGetCharacterArray(JsonElement root, out JsonElement characters)
	{
		if (root.ValueKind == JsonValueKind.Array)
		{
			characters = root;
			return true;
		}
		if (TryGetProperty(root, "characters", out characters) && characters.ValueKind == JsonValueKind.Array)
		{
			return true;
		}
		if (TryGetProperty(root, "data", out var value) && TryGetProperty(value, "characters", out characters) && characters.ValueKind == JsonValueKind.Array)
		{
			return true;
		}
		characters = default(JsonElement);
		return false;
	}

	private static string ReadCharacterKey(JsonElement character)
	{
		string text = ReadString(character, "characterKey");
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}
		string text2 = ReadString(character, "characterName", "name");
		string text3 = ReadString(character, "worldName", "world");
		if (TryGetProperty(character, "character", out var value))
		{
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = ReadString(value, "characterName", "name");
			}
			if (string.IsNullOrWhiteSpace(text3))
			{
				text3 = ReadString(value, "worldName", "world");
			}
		}
		if (!string.IsNullOrWhiteSpace(text2) && !string.IsNullOrWhiteSpace(text3))
		{
			return text2.Trim() + "@" + text3.Trim();
		}
		return string.Empty;
	}

	private static Dictionary<uint, int> ReadCombatJobLevels(JsonElement character)
	{
		Dictionary<uint, int> dictionary = new Dictionary<uint, int>();
		if (TryGetNestedProperty(character, out var value, "jobs") && value.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					uint num = (uint)Math.Max(0, ReadInt(item, "jobId", "classJobId", "id"));
					int num2 = Math.Max(0, ReadInt(item, "level"));
					string category = ReadString(item, "category");
					if (num != 0 && num2 > 0 && IsCombatJob(num, category))
					{
						dictionary[num] = Math.Max(dictionary.GetValueOrDefault(num), num2);
					}
				}
			}
		}
		if (dictionary.Count == 0 && TryGetNestedProperty(character, out var value2, "jobLevels") && value2.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty item2 in value2.EnumerateObject())
			{
				if (uint.TryParse(item2.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && TryReadInt(item2.Value, out var result2) && result2 > 0 && IsCombatJob(result, string.Empty))
				{
					dictionary[result] = Math.Max(dictionary.GetValueOrDefault(result), result2);
				}
			}
		}
		return dictionary;
	}

	private static bool IsCombatJob(uint jobId, string category)
	{
		if (jobId == 43)
		{
			return true;
		}
		if (jobId <= 255 && JobClassification.IsCombatJob((byte)jobId))
		{
			return true;
		}
		if (!category.Contains("tank", StringComparison.OrdinalIgnoreCase) && !category.Contains("healer", StringComparison.OrdinalIgnoreCase) && !category.Contains("dps", StringComparison.OrdinalIgnoreCase) && !category.Contains("combat", StringComparison.OrdinalIgnoreCase))
		{
			return category.Contains("limited", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static DateTime ReadTimestamp(JsonElement character)
	{
		if (!DateTime.TryParse(ReadString(character, "lastSnapshotUtc", "updatedUtc", "lastUpdatedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
		{
			return DateTime.MinValue;
		}
		return result;
	}

	private static bool TryGetNestedProperty(JsonElement element, out JsonElement value, params string[] propertyNames)
	{
		string[] array = propertyNames;
		foreach (string propertyName in array)
		{
			if (TryGetProperty(element, propertyName, out value))
			{
				return true;
			}
		}
		array = new string[3] { "snapshot", "progress", "data" };
		foreach (string propertyName2 in array)
		{
			if (!TryGetProperty(element, propertyName2, out var value2) || value2.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			foreach (string propertyName3 in propertyNames)
			{
				if (TryGetProperty(value2, propertyName3, out value))
				{
					return true;
				}
			}
		}
		value = default(JsonElement);
		return false;
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

	private static string ReadString(JsonElement element, params string[] propertyNames)
	{
		foreach (string propertyName in propertyNames)
		{
			if (TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
			{
				return value.GetString() ?? string.Empty;
			}
		}
		return string.Empty;
	}

	private static int ReadInt(JsonElement element, params string[] propertyNames)
	{
		foreach (string propertyName in propertyNames)
		{
			if (TryGetProperty(element, propertyName, out var value) && TryReadInt(value, out var result))
			{
				return result;
			}
		}
		return 0;
	}

	private static ulong ReadUInt64(JsonElement element, params string[] propertyNames)
	{
		foreach (string propertyName in propertyNames)
		{
			if (TryGetProperty(element, propertyName, out var value))
			{
				if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var value2))
				{
					return value2;
				}
				if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value2))
				{
					return value2;
				}
			}
		}
		return 0uL;
	}

	private static bool TryReadInt(JsonElement value, out int result)
	{
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result))
		{
			return true;
		}
		if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
		{
			return true;
		}
		result = 0;
		return false;
	}
}
