using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json;

namespace QuestionableCompanion.Services;

public class QuestPreCheckService : IDisposable
{
	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly Configuration config;

	private readonly AutoRetainerIPC autoRetainerIPC;

	private readonly IDalamudPluginInterface pluginInterface;

	private RuntimeEventSubscription? loginSubscription;

	private Dictionary<string, bool> preCheckResults = new Dictionary<string, bool>();

	private Dictionary<string, Dictionary<uint, bool>> questDatabase = new Dictionary<string, Dictionary<uint, bool>>();

	private Dictionary<string, Dictionary<uint, byte>> questSequenceDatabase = new Dictionary<string, Dictionary<uint, byte>>();

	private Dictionary<string, DateTime> lastRefreshByCharacter = new Dictionary<string, DateTime>();

	private readonly TimeSpan refreshInterval = TimeSpan.FromMinutes(30L);

	private string QuestDatabasePath => Path.Combine(pluginInterface.GetPluginConfigDirectory(), "QuestDatabase.json");

	private string QuestSequenceDatabasePath => Path.Combine(pluginInterface.GetPluginConfigDirectory(), "QuestSequenceDatabase.json");

	public QuestPreCheckService(IPluginLog log, IClientState clientState, Configuration config, AutoRetainerIPC autoRetainerIPC, IDalamudPluginInterface pluginInterface)
	{
		this.log = log;
		this.clientState = clientState;
		this.config = config;
		this.autoRetainerIPC = autoRetainerIPC;
		this.pluginInterface = pluginInterface;
		LoadQuestDatabase();
		LoadQuestSequenceDatabase();
		loginSubscription = RuntimeEventSubscription.Subscribe(this.clientState, "Login", OnLogin, log, "QuestPreCheck.Login");
	}

	private void OnLogin()
	{
		log.Information("[QuestPreCheck] Login detected - Triggering quest scan...");
		try
		{
			ScanCurrentCharacterQuestStatus(verbose: true);
		}
		catch (Exception ex)
		{
			log.Error("[QuestPreCheck] Error on login scan: " + ex.Message);
		}
	}

	private void LoadQuestDatabase()
	{
		try
		{
			EnsureQuestDatabasePath();
			log.Information("[QuestPreCheck] Quest database path: " + QuestDatabasePath);
			if (!File.Exists(QuestDatabasePath))
			{
				log.Information("[QuestPreCheck] Creating new quest database...");
				questDatabase = new Dictionary<string, Dictionary<uint, bool>>();
			}
			else
			{
				string value = File.ReadAllText(QuestDatabasePath);
				if (string.IsNullOrEmpty(value))
				{
					questDatabase = new Dictionary<string, Dictionary<uint, bool>>();
				}
				else
				{
					questDatabase = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<uint, bool>>>(value) ?? new Dictionary<string, Dictionary<uint, bool>>();
				}
			}
			log.Information($"[QuestPreCheck] Loaded quest database for {questDatabase.Count} characters");
		}
		catch (Exception ex)
		{
			log.Error("[QuestPreCheck] Error loading quest database: " + ex.Message);
			questDatabase = new Dictionary<string, Dictionary<uint, bool>>();
		}
	}

	private void LoadQuestSequenceDatabase()
	{
		try
		{
			log.Information("[QuestPreCheck] Quest sequence database path: " + QuestSequenceDatabasePath);
			if (!File.Exists(QuestSequenceDatabasePath))
			{
				questSequenceDatabase = new Dictionary<string, Dictionary<uint, byte>>();
			}
			else
			{
				string value = File.ReadAllText(QuestSequenceDatabasePath);
				questSequenceDatabase = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<uint, byte>>>(value) ?? new Dictionary<string, Dictionary<uint, byte>>();
			}
			log.Information($"[QuestPreCheck] Loaded sequence database for {questSequenceDatabase.Count} characters");
		}
		catch (Exception ex)
		{
			log.Error("[QuestPreCheck] Error loading sequence database: " + ex.Message);
			questSequenceDatabase = new Dictionary<string, Dictionary<uint, byte>>();
		}
	}

	private void SaveQuestDatabase()
	{
		try
		{
			EnsureQuestDatabasePath();
			string contents = JsonConvert.SerializeObject(questDatabase, Formatting.Indented);
			File.WriteAllText(QuestDatabasePath, contents);
			string contents2 = JsonConvert.SerializeObject(questSequenceDatabase, Formatting.Indented);
			File.WriteAllText(QuestSequenceDatabasePath, contents2);
			log.Information($"[QuestPreCheck] Quest databases saved ({questDatabase.Count} characters, {questSequenceDatabase.Count} sequences)");
		}
		catch (Exception ex)
		{
			log.Error("[QuestPreCheck] Error saving quest database: " + ex.Message);
		}
	}

	private void EnsureQuestDatabasePath()
	{
		string directoryName = Path.GetDirectoryName(QuestDatabasePath);
		if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
	}

	public unsafe void ScanCurrentCharacterQuestStatus(bool verbose = false)
	{
		if (Plugin.ObjectTable.LocalPlayer == null)
		{
			log.Warning("[QuestPreCheck] No local player found");
			return;
		}
		string value = Plugin.ObjectTable.LocalPlayer.HomeWorld.Value.Name.ToString();
		string text = $"{Plugin.ObjectTable.LocalPlayer.Name}@{value}";
		if (verbose)
		{
			log.Information("[QuestPreCheck] Scanning quest status for: " + text);
		}
		if (!questDatabase.ContainsKey(text))
		{
			questDatabase[text] = new Dictionary<uint, bool>();
		}
		QuestManager* ptr = QuestManager.Instance();
		if (ptr == null)
		{
			log.Error("[QuestPreCheck] QuestManager not available");
			return;
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		List<uint> list = new List<uint>();
		List<uint> list2 = config.QuestPreCheckRange ?? new List<uint>();
		if (list2.Count == 0)
		{
			for (uint num4 = 1u; num4 < 65535; num4++)
			{
				list2.Add(num4);
			}
		}
		foreach (uint item in list2)
		{
			try
			{
				bool num5 = QuestManager.IsQuestComplete((ushort)(item % 65536));
				num++;
				if (num5)
				{
					num2++;
					if (!questDatabase[text].GetValueOrDefault(item, defaultValue: false))
					{
						questDatabase[text][item] = true;
						num3++;
						list.Add(item);
						if (questSequenceDatabase.ContainsKey(text) && questSequenceDatabase[text].ContainsKey(item))
						{
							questSequenceDatabase[text].Remove(item);
						}
						if (verbose)
						{
							log.Debug($"[QuestPreCheck] {text} - Quest {item}: âœ“ NEWLY COMPLETED");
						}
					}
					else
					{
						questDatabase[text][item] = true;
					}
				}
				else
				{
					QuestWork* questById = ptr->GetQuestById((ushort)(item % 65536));
					if (questById != null)
					{
						byte sequence = questById->Sequence;
						if (sequence > 0)
						{
							if (!questSequenceDatabase.ContainsKey(text))
							{
								questSequenceDatabase[text] = new Dictionary<uint, byte>();
							}
							if (questSequenceDatabase[text].GetValueOrDefault<uint, byte>(item, 0) != sequence)
							{
								questSequenceDatabase[text][item] = sequence;
								if (verbose)
								{
									log.Debug($"[QuestPreCheck] {text} - Quest {item}: Active Sequence {sequence}");
								}
							}
						}
					}
				}
				if (verbose)
				{
					_ = item % 500;
				}
			}
			catch (Exception ex)
			{
				log.Error($"[QuestPreCheck] Error checking quest {item}: {ex.Message}");
			}
		}
		if (verbose)
		{
			log.Information($"[QuestPreCheck] Scan complete: {num} checked, {num2} completed, {num3} changed");
			if (list.Count > 0)
			{
				log.Information("[QuestPreCheck] NEWLY COMPLETED: " + string.Join(", ", list));
			}
		}
		lastRefreshByCharacter[text] = DateTime.Now;
		SaveQuestDatabase();
	}

	public bool IsLiveQuestCompleted(uint questId)
	{
		try
		{
			return QuestManager.IsQuestComplete((ushort)(questId % 65536));
		}
		catch
		{
			return false;
		}
	}

	public unsafe byte? GetCurrentQuestSequence(uint questId)
	{
		try
		{
			QuestManager* ptr = QuestManager.Instance();
			if (ptr == null)
			{
				return null;
			}
			QuestWork* questById = ptr->GetQuestById((ushort)(questId % 65536));
			if (questById == null)
			{
				return null;
			}
			return questById->Sequence;
		}
		catch
		{
			return null;
		}
	}

	public void RefreshQuestDatabasePeriodic()
	{
		if (Plugin.ObjectTable.LocalPlayer != null && clientState.IsLoggedIn)
		{
			string value = Plugin.ObjectTable.LocalPlayer.HomeWorld.Value.Name.ToString();
			string text = $"{Plugin.ObjectTable.LocalPlayer.Name}@{value}";
			if (!lastRefreshByCharacter.TryGetValue(text, out var value2) || DateTime.Now - value2 >= refreshInterval)
			{
				log.Information("[QuestDB] === 30-MINUTE REFRESH TRIGGERED ===");
				log.Information("[QuestDB] Updating quest status for: " + text);
				ScanCurrentCharacterQuestStatus(verbose: true);
				log.Information("[QuestDB] === 30-MINUTE REFRESH COMPLETE ===");
			}
		}
	}

	public void LogCompletedQuestsBeforeLogout()
	{
		if (Plugin.ObjectTable.LocalPlayer != null)
		{
			string value = Plugin.ObjectTable.LocalPlayer.HomeWorld.Value.Name.ToString();
			string text = $"{Plugin.ObjectTable.LocalPlayer.Name}@{value}";
			log.Information("[QuestDB] Logging final quest status before logout: " + text);
			ScanCurrentCharacterQuestStatus();
			log.Information("[QuestDB] Final quest state saved for: " + text);
		}
	}

	public Dictionary<string, bool> PerformPreRotationCheck(uint stopQuestId, List<string> characters)
	{
		log.Information("[QuestPreCheck] === STARTING PRE-ROTATION QUEST VERIFICATION ===");
		log.Information($"[QuestPreCheck] Checking {characters.Count} characters for quest {stopQuestId}...");
		preCheckResults.Clear();
		foreach (string character in characters)
		{
			try
			{
				if (questDatabase.ContainsKey(character) && questDatabase[character].ContainsKey(stopQuestId))
				{
					bool flag = questDatabase[character][stopQuestId];
					preCheckResults[character] = flag;
					string value = (flag ? "âœ“ COMPLETED" : "â—‹ PENDING");
					log.Information($"[QuestPreCheck] {character}: {value} (from database)");
				}
				else
				{
					log.Debug("[QuestPreCheck] " + character + ": Not in database, will check during rotation");
					preCheckResults[character] = false;
				}
			}
			catch (Exception ex)
			{
				log.Error("[QuestPreCheck] Error checking " + character + ": " + ex.Message);
				preCheckResults[character] = false;
			}
		}
		log.Information("[QuestPreCheck] === PRE-ROTATION CHECK COMPLETE ===");
		return preCheckResults;
	}

	public bool ShouldSkipCharacter(string characterName, uint questId)
	{
		if (preCheckResults.TryGetValue(characterName, out var value) && value)
		{
			log.Information($"[QuestPreCheck] Character {characterName} already completed quest {questId} - SKIPPING");
			return true;
		}
		bool value3 = default(bool);
		if (questDatabase.TryGetValue(characterName, out Dictionary<uint, bool> value2) && value2.TryGetValue(questId, out value3) && value3)
		{
			log.Information($"[QuestPreCheck] Character {characterName} already completed quest {questId} (from DB) - SKIPPING");
			return true;
		}
		return false;
	}

	public bool? GetQuestStatus(string characterName, uint questId)
	{
		if (questDatabase.TryGetValue(characterName, out Dictionary<uint, bool> value) && value.TryGetValue(questId, out var value2))
		{
			return value2;
		}
		return null;
	}

	public byte GetQuestSequence(string characterName, uint questId)
	{
		if (questSequenceDatabase.TryGetValue(characterName, out Dictionary<uint, byte> value) && value.TryGetValue(questId, out var value2))
		{
			return value2;
		}
		return 0;
	}

	public List<uint> GetCompletedQuests(string characterName)
	{
		if (!questDatabase.TryGetValue(characterName, out Dictionary<uint, bool> value))
		{
			return new List<uint>();
		}
		return (from kvp in value
			where kvp.Value
			select kvp.Key).ToList();
	}

	public void MarkQuestCompleted(string characterName, uint questId)
	{
		if (!questDatabase.ContainsKey(characterName))
		{
			questDatabase[characterName] = new Dictionary<uint, bool>();
		}
		questDatabase[characterName][questId] = true;
		SaveQuestDatabase();
		log.Information($"[QuestPreCheck] Marked quest {questId} as completed for {characterName}");
	}

	public void ClearPreCheckResults()
	{
		preCheckResults.Clear();
		log.Information("[QuestPreCheck] Pre-check results cleared");
	}

	public void ClearCharacterData(string characterName)
	{
		if (questDatabase.ContainsKey(characterName))
		{
			int count = questDatabase[characterName].Count;
			questDatabase.Remove(characterName);
			SaveQuestDatabase();
			log.Information($"[QuestPreCheck] Cleared {count} quests for {characterName}");
		}
		else
		{
			log.Information("[QuestPreCheck] No quest data found for " + characterName);
		}
		lastRefreshByCharacter.Remove(characterName);
	}

	public void Dispose()
	{
		loginSubscription?.Dispose();
		SaveQuestDatabase();
		log.Information("[QuestPreCheck] Service disposed");
	}
}
