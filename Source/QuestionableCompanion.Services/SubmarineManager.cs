using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace QuestionableCompanion.Services;

public class SubmarineManager : IDisposable
{
	private readonly IPluginLog log;

	private readonly AutoRetainerIPC autoRetainerIPC;

	private readonly Configuration config;

	private readonly ICommandManager? commandManager;

	private readonly IFramework? framework;

	private readonly IDalamudPluginInterface pluginInterface;

	private DateTime lastSubmarineCheck = DateTime.MinValue;

	private DateTime submarineReloginCooldownEnd = DateTime.MinValue;

	private DateTime submarineNoAvailableWaitEnd = DateTime.MinValue;

	private bool submarinesPaused;

	private bool externalPause;

	private bool submarinesWaitingForSeq0;

	private bool submarineReloginInProgress;

	private bool submarineJustCompleted;

	private string? originalCharacterForSubmarines;

	private DateTime lastWatchdogCheck = DateTime.MinValue;

	private const int WatchdogIntervalSeconds = 60;

	public bool IsSubmarinePaused
	{
		get
		{
			if (!submarinesPaused)
			{
				return externalPause;
			}
			return true;
		}
	}

	public bool IsExternalPaused => externalPause;

	public bool IsWaitingForSequence0 => submarinesWaitingForSeq0;

	public bool IsReloginInProgress => submarineReloginInProgress;

	public bool IsSubmarineJustCompleted => submarineJustCompleted;

	public void SetExternalPause(bool paused)
	{
		externalPause = paused;
		log.Information($"[SubmarineManager] External pause set to: {paused}");
	}

	public SubmarineManager(IPluginLog log, AutoRetainerIPC autoRetainerIPC, Configuration config, ICommandManager? commandManager = null, IFramework? framework = null, IDalamudPluginInterface? pluginInterface = null)
	{
		this.log = log;
		this.autoRetainerIPC = autoRetainerIPC;
		this.config = config;
		this.commandManager = commandManager;
		this.framework = framework;
		this.pluginInterface = pluginInterface ?? Plugin.PluginInterface;
		if (this.framework != null)
		{
			this.framework.Update += OnFrameworkUpdate;
		}
		log.Information("[SubmarineManager] Service initialized");
	}

	private string? GetConfigPath()
	{
		try
		{
			string pluginConfigDirectory = pluginInterface.GetPluginConfigDirectory();
			if (string.IsNullOrWhiteSpace(pluginConfigDirectory))
			{
				log.Warning("[SubmarineManager] Could not resolve plugin config directory");
				return null;
			}
			string text = Directory.GetParent(pluginConfigDirectory)?.FullName;
			if (string.IsNullOrWhiteSpace(text))
			{
				log.Warning("[SubmarineManager] Could not resolve pluginConfigs parent from: " + pluginConfigDirectory);
				return null;
			}
			return Path.Combine(text, "AutoRetainer", "DefaultConfig.json");
		}
		catch (Exception ex)
		{
			log.Error("[SubmarineManager] Error resolving config path: " + ex.Message);
			return null;
		}
	}

	public bool CheckSubmarines()
	{
		if (!config.EnableSubmarineCheck || externalPause)
		{
			return false;
		}
		string configPath = GetConfigPath();
		if (string.IsNullOrEmpty(configPath))
		{
			log.Warning("[SubmarineManager] Could not resolve config path");
			return false;
		}
		if (!File.Exists(configPath))
		{
			log.Debug("[SubmarineManager] Config file not found: " + configPath);
			return false;
		}
		try
		{
			string text = File.ReadAllText(configPath);
			if (string.IsNullOrEmpty(text))
			{
				log.Warning("[SubmarineManager] Config file is empty");
				return false;
			}
			List<long> list = ParseReturnTimes(text);
			if (list.Count == 0)
			{
				return false;
			}
			long num = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			int num2 = 0;
			long? num3 = null;
			foreach (long item in list)
			{
				long num4 = item - num;
				if (num4 <= 0)
				{
					num2++;
				}
				else if (!num3.HasValue || num4 < num3.Value)
				{
					num3 = num4;
				}
			}
			if (num2 > 0)
			{
				string value = ((num2 == 1) ? "Sub" : "Subs");
				log.Debug($"[SubmarineManager] {num2} {value} available - pausing quest rotation!");
				return true;
			}
			if (num3.HasValue && num3.Value > 0)
			{
				int num5 = Math.Max(0, (int)Math.Ceiling((double)num3.Value / 60.0));
				string value2 = ((num5 == 1) ? "minute" : "minutes");
				log.Debug($"[SubmarineManager] Next submarine in {num5} {value2}");
			}
			return false;
		}
		catch (Exception ex)
		{
			log.Error("[SubmarineManager] Error checking submarines: " + ex.Message);
			return false;
		}
	}

	public int CheckSubmarinesSoon()
	{
		if (!config.EnableSubmarineCheck || externalPause)
		{
			return 0;
		}
		string configPath = GetConfigPath();
		if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
		{
			return 0;
		}
		try
		{
			string text = File.ReadAllText(configPath);
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}
			List<long> list = ParseReturnTimes(text);
			if (list.Count == 0)
			{
				return 0;
			}
			long num = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			long? num2 = null;
			int num3 = 0;
			foreach (long item in list)
			{
				long num4 = item - num;
				if (num4 <= 0)
				{
					num3++;
				}
				else if (num4 <= 120 && (!num2.HasValue || num4 < num2.Value))
				{
					num2 = num4;
				}
			}
			if (num3 > 0)
			{
				log.Debug($"[SubmarineManager] {num3} submarines ready NOW - continue Multi-Mode");
				return 999;
			}
			if (num2.HasValue)
			{
				int value = (int)Math.Ceiling((double)num2.Value / 60.0);
				log.Debug($"[SubmarineManager] Submarine will be ready in {num2.Value} seconds ({value} min) - waiting before character switch");
				return (int)num2.Value;
			}
			if (submarineNoAvailableWaitEnd == DateTime.MinValue)
			{
				submarineNoAvailableWaitEnd = DateTime.Now.AddSeconds(60.0);
				log.Information("[SubmarineManager] No submarines available - waiting 60 seconds before relog");
				return 60;
			}
			if (DateTime.Now < submarineNoAvailableWaitEnd)
			{
				int num5 = (int)(submarineNoAvailableWaitEnd - DateTime.Now).TotalSeconds;
				log.Debug($"[SubmarineManager] Waiting {num5}s before relog...");
				return num5;
			}
			submarineNoAvailableWaitEnd = DateTime.MinValue;
			return 0;
		}
		catch (Exception ex)
		{
			log.Error("[SubmarineManager] Error checking submarines soon: " + ex.Message);
			return 0;
		}
	}

	private List<long> ParseReturnTimes(string jsonContent)
	{
		List<long> list = new List<long>();
		try
		{
			if (JObject.Parse(jsonContent)["OfflineData"] is JArray jArray)
			{
				foreach (JToken item in jArray)
				{
					if (!(item is JObject jObject))
					{
						continue;
					}
					jObject.Value<string>("Name");
					JToken jToken = jObject["EnabledSubs"];
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					bool flag = jToken != null;
					if (flag && jToken is JArray jArray2)
					{
						foreach (JToken item2 in jArray2)
						{
							string text = item2.Value<string>();
							if (!string.IsNullOrEmpty(text))
							{
								hashSet.Add(text);
							}
						}
					}
					if (!(jObject["OfflineSubmarineData"] is JArray jArray3))
					{
						continue;
					}
					foreach (JToken item3 in jArray3)
					{
						string text2 = item3.Value<string>("Name");
						long num = item3.Value<long>("ReturnTime");
						if (!string.IsNullOrEmpty(text2) && (!flag || hashSet.Contains(text2)) && num > 0)
						{
							list.Add(num);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			log.Error("[SubmarineManager] Error parsing submarine config: " + ex.Message);
			return new List<long>();
		}
		return list;
	}

	public void StartSubmarineWait(string currentCharacter)
	{
		submarinesWaitingForSeq0 = true;
		originalCharacterForSubmarines = currentCharacter;
		log.Information("[SubmarineManager] Waiting for Sequence 0 completion before enabling Multi-Mode");
	}

	public void EnableMultiMode()
	{
		if (!autoRetainerIPC.IsAvailable)
		{
			log.Warning("[SubmarineManager] AutoRetainer not available - cannot enable Multi-Mode");
			return;
		}
		try
		{
			if (autoRetainerIPC.GetMultiModeEnabled())
			{
				log.Information("[SubmarineManager] Multi-Mode is already enabled - skipping activation");
				submarinesPaused = true;
				submarinesWaitingForSeq0 = false;
				return;
			}
			if (commandManager != null && framework != null)
			{
				log.Information("[SubmarineManager] Sending /ays set MultiModeType 1 command...");
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/ays set MultiModeType 1");
				}).Wait();
				log.Information("[SubmarineManager] Sending /ays multi e command...");
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/ays multi e");
				}).Wait();
				log.Information("[SubmarineManager] ✓ /ays multi e command sent");
			}
			autoRetainerIPC.SetMultiModeEnabled(enabled: true);
			submarinesPaused = true;
			submarinesWaitingForSeq0 = false;
			log.Information("[SubmarineManager] Multi-Mode enabled - quest automation paused");
		}
		catch (Exception ex)
		{
			log.Error("[SubmarineManager] Failed to enable Multi-Mode: " + ex.Message);
		}
	}

	public void DisableMultiModeAndReturn()
	{
		if (!autoRetainerIPC.IsAvailable)
		{
			log.Warning("[SubmarineManager] AutoRetainer not available");
			return;
		}
		try
		{
			if (commandManager != null && framework != null)
			{
				log.Information("[SubmarineManager] Sending /ays multi d command...");
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/ays multi d");
				}).Wait();
				log.Information("[SubmarineManager] ✓ /ays multi d command sent");
			}
			autoRetainerIPC.SetMultiModeEnabled(enabled: false);
			log.Information("[SubmarineManager] Multi-Mode disabled - starting return to original character");
			submarineNoAvailableWaitEnd = DateTime.MinValue;
			if (!string.IsNullOrEmpty(originalCharacterForSubmarines))
			{
				submarineReloginInProgress = true;
				log.Information("[SubmarineManager] Returning to original character: " + originalCharacterForSubmarines);
			}
		}
		catch (Exception ex)
		{
			log.Error("[SubmarineManager] Failed to disable Multi-Mode: " + ex.Message);
		}
	}

	public void CompleteSubmarineRelog()
	{
		submarineReloginInProgress = false;
		submarinesPaused = false;
		submarineJustCompleted = true;
		submarineReloginCooldownEnd = DateTime.Now.AddSeconds(config.SubmarineReloginCooldown);
		log.Information($"[SubmarineManager] Submarine rotation complete - cooldown active for {config.SubmarineReloginCooldown} seconds");
	}

	public bool IsSubmarineCooldownActive()
	{
		return DateTime.Now < submarineReloginCooldownEnd;
	}

	public void ClearSubmarineJustCompleted()
	{
		submarineJustCompleted = false;
		log.Information("[SubmarineManager] Cooldown expired - submarine checks re-enabled");
	}

	public void Reset()
	{
		submarinesPaused = false;
		submarinesWaitingForSeq0 = false;
		submarineReloginInProgress = false;
		submarineJustCompleted = false;
		originalCharacterForSubmarines = null;
		submarineReloginCooldownEnd = DateTime.MinValue;
		log.Information("[SubmarineManager] State reset");
	}

	public void Dispose()
	{
		if (framework != null)
		{
			framework.Update -= OnFrameworkUpdate;
		}
		Reset();
		log.Information("[SubmarineManager] Service disposed");
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!config.EnableSubmarineCheck || !submarinesPaused || (DateTime.Now - lastWatchdogCheck).TotalSeconds < 60.0)
		{
			return;
		}
		lastWatchdogCheck = DateTime.Now;
		Task.Run(delegate
		{
			try
			{
				log.Information("[SubmarineManager] [Watchdog] Checking AutoRetainer status...");
				if (!autoRetainerIPC.IsAvailable)
				{
					log.Warning("[SubmarineManager] [Watchdog] AutoRetainer IPC unavailable!");
				}
				else
				{
					bool multiModeEnabled = autoRetainerIPC.GetMultiModeEnabled();
					log.Information($"[SubmarineManager] [Watchdog] Multi-Mode Enabled: {multiModeEnabled}");
					if (!multiModeEnabled)
					{
						log.Warning("[SubmarineManager] [Watchdog] Multi-Mode unexpectedly DISABLED. Restarting...");
						EnableMultiMode();
					}
				}
			}
			catch (Exception ex)
			{
				log.Error("[SubmarineManager] [Watchdog] Error: " + ex.Message);
			}
		});
	}
}
