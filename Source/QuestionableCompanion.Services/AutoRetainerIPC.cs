using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons.Reflection;

namespace QuestionableCompanion.Services;

public class AutoRetainerIPC : IDisposable
{
	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly IObjectTable objectTable;

	private readonly IPlayerState playerState;

	private readonly CombatJobResolver combatJobResolver;

	private readonly AutoRetainerReflectionBridge reflectionBridge;

	private ICallGateSubscriber<List<ulong>>? getRegisteredCIDsSubscriber;

	private ICallGateSubscriber<object>? abortAllTasksSubscriber;

	private ICallGateSubscriber<object>? disableAllFunctionsSubscriber;

	private ICallGateSubscriber<bool>? getMultiModeEnabledSubscriber;

	private ICallGateSubscriber<bool, object>? setMultiModeEnabledSubscriber;

	private Dictionary<ulong, string> characterCache = new Dictionary<ulong, string>();

	private Dictionary<string, int> grandCompanyRankCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, IReadOnlyList<int>> classJobLevelCache = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

	private HashSet<ulong> unknownCIDs = new HashSet<ulong>();

	private bool subscribersInitialized;

	private DateTime lastAvailabilityCheck = DateTime.MinValue;

	private const int AvailabilityCheckCooldownSeconds = 5;

	public bool IsAvailable { get; private set; }

	public AutoRetainerIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, IObjectTable objectTable, IPlayerState playerState, CombatJobResolver combatJobResolver)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.objectTable = objectTable;
		this.playerState = playerState;
		this.combatJobResolver = combatJobResolver;
		reflectionBridge = new AutoRetainerReflectionBridge(ResolveReflectionTarget);
		InitializeIPC();
	}

	public void ClearCache()
	{
		characterCache.Clear();
		grandCompanyRankCache.Clear();
		classJobLevelCache.Clear();
		unknownCIDs.Clear();
		log.Information("[AutoRetainerIPC] Cache cleared");
	}

	private void InitializeIPC()
	{
		try
		{
			getRegisteredCIDsSubscriber = null;
			abortAllTasksSubscriber = null;
			disableAllFunctionsSubscriber = null;
			getMultiModeEnabledSubscriber = null;
			setMultiModeEnabledSubscriber = null;
			IsAvailable = false;
			getRegisteredCIDsSubscriber = pluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs");
			abortAllTasksSubscriber = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.AbortAllTasks");
			disableAllFunctionsSubscriber = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.DisableAllFunctions");
			try
			{
				getMultiModeEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled");
				setMultiModeEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled");
				log.Debug("[AutoRetainerIPC] Multi-Mode IPC initialized");
			}
			catch (Exception ex)
			{
				log.Debug("[AutoRetainerIPC] Failed to initialize Multi-Mode IPC: " + ex.Message);
			}
			subscribersInitialized = true;
			log.Debug("[AutoRetainerIPC] IPC subscribers initialized (lazy-loading enabled)");
		}
		catch (Exception ex2)
		{
			IsAvailable = false;
			subscribersInitialized = false;
			log.Error("[AutoRetainerIPC] Failed to initialize subscribers: " + ex2.Message);
		}
	}

	private bool TryEnsureAvailable()
	{
		if (IsAvailable)
		{
			return true;
		}
		DateTime now = DateTime.Now;
		if ((now - lastAvailabilityCheck).TotalSeconds < 5.0)
		{
			return false;
		}
		lastAvailabilityCheck = now;
		if (!subscribersInitialized)
		{
			InitializeIPC();
			if (!subscribersInitialized)
			{
				return false;
			}
		}
		try
		{
			if (getRegisteredCIDsSubscriber == null)
			{
				return false;
			}
			List<ulong> list = getRegisteredCIDsSubscriber.InvokeFunc();
			if (!IsAvailable)
			{
				IsAvailable = true;
				log.Information($"[AutoRetainerIPC] ✅ AutoRetainer is now available ({list?.Count ?? 0} characters)");
			}
			return true;
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] AutoRetainer not yet available: " + ex.Message);
			IsAvailable = false;
			return false;
		}
	}

	public bool TryReinitialize()
	{
		log.Information("[AutoRetainerIPC] Manual IPC reinitialization requested");
		InitializeIPC();
		lastAvailabilityCheck = DateTime.MinValue;
		bool num = TryEnsureAvailable();
		if (num)
		{
			log.Information("[AutoRetainerIPC] IPC reinitialization successful");
			return num;
		}
		log.Warning("[AutoRetainerIPC] IPC still unavailable after reinitialization attempt");
		return num;
	}

	public List<string> GetRegisteredCharacters()
	{
		log.Debug("[AutoRetainerIPC] GetRegisteredCharacters called");
		TryEnsureAvailable();
		if (!IsAvailable || getRegisteredCIDsSubscriber == null)
		{
			log.Warning("[AutoRetainerIPC] Cannot get characters - IPC not available");
			log.Warning($"[AutoRetainerIPC] IsAvailable: {IsAvailable}, Subscriber: {getRegisteredCIDsSubscriber != null}");
			return new List<string>();
		}
		try
		{
			List<ulong> list = getRegisteredCIDsSubscriber.InvokeFunc();
			if (list == null || list.Count == 0)
			{
				log.Warning("[AutoRetainerIPC] No CIDs returned from AutoRetainer");
				return new List<string>();
			}
			List<string> list2 = new List<string>();
			foreach (ulong item in list)
			{
				string characterNameFromCID = GetCharacterNameFromCID(item);
				if (!string.IsNullOrEmpty(characterNameFromCID))
				{
					list2.Add(characterNameFromCID);
					continue;
				}
				log.Debug($"[AutoRetainerIPC] Could not resolve name for CID: {item}");
			}
			if (list2.Count == 0)
			{
				log.Warning("[AutoRetainerIPC] No character names could be resolved from CIDs");
			}
			return list2;
		}
		catch (Exception ex)
		{
			log.Error("[AutoRetainerIPC] GetRegisteredCharacters failed: " + ex.Message);
			log.Error("[AutoRetainerIPC] Stack trace: " + ex.StackTrace);
			return new List<string>();
		}
	}

	public IReadOnlyDictionary<ulong, string> GetRegisteredCharacterMap()
	{
		TryEnsureAvailable();
		if (!IsAvailable || getRegisteredCIDsSubscriber == null)
		{
			return new Dictionary<ulong, string>();
		}
		try
		{
			return (from cid in getRegisteredCIDsSubscriber.InvokeFunc()
				select (ContentId: cid, CharacterKey: GetCharacterNameFromCID(cid)) into entry
				where !string.IsNullOrWhiteSpace(entry.CharacterKey) && entry.CharacterKey.Contains('@') && !entry.CharacterKey.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
				group entry by entry.ContentId).ToDictionary((IGrouping<ulong, (ulong ContentId, string CharacterKey)> group) => group.Key, (IGrouping<ulong, (ulong ContentId, string CharacterKey)> group) => group.First().CharacterKey);
		}
		catch (Exception ex)
		{
			log.Warning("[AutoRetainerIPC] Failed to build registered character map: " + ex.Message);
			return new Dictionary<ulong, string>();
		}
	}

	public bool TryGetContentId(string characterKey, out ulong contentId)
	{
		contentId = GetRegisteredCharacterMap().FirstOrDefault<KeyValuePair<ulong, string>>((KeyValuePair<ulong, string> entry) => string.Equals(entry.Value, characterKey, StringComparison.OrdinalIgnoreCase)).Key;
		return contentId != 0;
	}

	internal async Task<AutoRetainerReflectionApplyResult> ConfigureRetainerBootstrapAsync(AutoRetainerReflectionRequest request)
	{
		if (!TryEnsureAvailable())
		{
			return AutoRetainerReflectionApplyResult.Fail("AutoRetainer IPC availability could not be proven.");
		}
		return await framework.RunOnFrameworkThread(() => reflectionBridge.Apply(request));
	}

	internal async Task<AutoRetainerReflectionReadResult> ReadRetainerSnapshotAsync(AutoRetainerReflectionRequest request)
	{
		if (!TryEnsureAvailable())
		{
			return AutoRetainerReflectionReadResult.Fail("AutoRetainer IPC availability could not be proven.");
		}
		return await framework.RunOnFrameworkThread(() => reflectionBridge.ReadExact(request));
	}

	private AutoRetainerReflectionTarget? ResolveReflectionTarget()
	{
		if (!DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out var instance, out var context, suppressErrors: true, ignoreCache: true) || instance == null || context == null)
		{
			return null;
		}
		Assembly[] array = context.Assemblies.Where((Assembly assembly) => string.Equals(assembly.GetName().Name, "ECommons", StringComparison.Ordinal)).ToArray();
		if (array.Length != 1)
		{
			return null;
		}
		Type type = array[0].GetType("ECommons.Configuration.EzConfig", throwOnError: false, ignoreCase: false);
		PropertyInfo config = type?.GetProperty("Config", BindingFlags.Static | BindingFlags.Public);
		MethodInfo save = type?.GetMethod("Save", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
		PropertyInfo propertyInfo = config;
		if ((object)propertyInfo == null || !propertyInfo.CanRead || save == null)
		{
			return null;
		}
		return new AutoRetainerReflectionTarget(instance, context, () => config.GetValue(null), delegate
		{
			save.Invoke(null, null);
		});
	}

	private AutoRetainerCharacterSnapshot? TryReadCharacterSnapshot(ulong contentId)
	{
		try
		{
			AutoRetainerReflectionReadResult result = framework.RunOnFrameworkThread(() => reflectionBridge.ReadCharacter(contentId)).GetAwaiter().GetResult();
			if (!result.Success)
			{
				log.Debug("[AutoRetainerIPC] Reflected character snapshot failed: " + result.Error);
			}
			if (result.Snapshot != null && !string.IsNullOrWhiteSpace(result.Snapshot.CharacterKey))
			{
				classJobLevelCache[result.Snapshot.CharacterKey] = result.Snapshot.ClassJobLevels.ToArray();
			}
			return result.Snapshot;
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] Reflected character snapshot failed: " + ex.Message);
			return null;
		}
	}

	public async Task<bool> AbortAllTasksAsync()
	{
		if (!TryEnsureAvailable() || abortAllTasksSubscriber == null)
		{
			return false;
		}
		try
		{
			await framework.RunOnFrameworkThread(delegate
			{
				abortAllTasksSubscriber.InvokeAction();
			});
			return true;
		}
		catch (Exception ex)
		{
			log.Warning("[AutoRetainerIPC] AbortAllTasks failed: " + ex.Message);
			return false;
		}
	}

	public async Task<bool> DisableAllFunctionsAsync()
	{
		if (!TryEnsureAvailable() || disableAllFunctionsSubscriber == null)
		{
			return false;
		}
		try
		{
			await framework.RunOnFrameworkThread(delegate
			{
				disableAllFunctionsSubscriber.InvokeAction();
			});
			return true;
		}
		catch (Exception ex)
		{
			log.Warning("[AutoRetainerIPC] DisableAllFunctions failed: " + ex.Message);
			return false;
		}
	}

	public async Task SendCommandAsync(string command)
	{
		await framework.RunOnFrameworkThread(() => commandManager.ProcessCommand(command));
	}

	public int GetHighestCombatJobLevel(string characterNameWithWorld)
	{
		return GetHighestCombatJobLevelAndId(characterNameWithWorld).Level;
	}

	public int GetGrandCompanyRank(string characterNameWithWorld)
	{
		if (string.IsNullOrEmpty(characterNameWithWorld))
		{
			return 0;
		}
		if (grandCompanyRankCache.TryGetValue(characterNameWithWorld, out var value))
		{
			return value;
		}
		return GetGrandCompanyInfo(characterNameWithWorld).Rank;
	}

	public (uint CompanyId, int Rank) GetGrandCompanyInfo(string characterNameWithWorld)
	{
		if (string.IsNullOrEmpty(characterNameWithWorld))
		{
			return (CompanyId: 0u, Rank: 0);
		}
		grandCompanyRankCache.TryGetValue(characterNameWithWorld, out var value);
		TryEnsureAvailable();
		if (!IsAvailable || getRegisteredCIDsSubscriber == null)
		{
			return (CompanyId: GetLiveGrandCompanyId(characterNameWithWorld), Rank: value);
		}
		try
		{
			List<ulong> list = getRegisteredCIDsSubscriber.InvokeFunc();
			if (list == null)
			{
				return (CompanyId: GetLiveGrandCompanyId(characterNameWithWorld), Rank: value);
			}
			foreach (ulong item in list)
			{
				AutoRetainerCharacterSnapshot autoRetainerCharacterSnapshot = TryReadCharacterSnapshot(item);
				if (!(autoRetainerCharacterSnapshot == null) && string.Equals(autoRetainerCharacterSnapshot.CharacterKey, characterNameWithWorld, StringComparison.OrdinalIgnoreCase))
				{
					int num = Math.Max((int)autoRetainerCharacterSnapshot.GrandCompanyRank, 0);
					uint liveGrandCompanyId = GetLiveGrandCompanyId(characterNameWithWorld);
					grandCompanyRankCache[characterNameWithWorld] = num;
					return (CompanyId: liveGrandCompanyId, Rank: num);
				}
			}
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] Failed to read Grand Company info for " + characterNameWithWorld + ": " + ex.Message);
		}
		return (CompanyId: GetLiveGrandCompanyId(characterNameWithWorld), Rank: value);
	}

	public (int Level, uint JobId) GetHighestCombatJobLevelAndId(string characterNameWithWorld)
	{
		if (string.IsNullOrEmpty(characterNameWithWorld))
		{
			return (Level: 0, JobId: 0u);
		}
		TryEnsureAvailable();
		if (!IsAvailable || getRegisteredCIDsSubscriber == null)
		{
			return (Level: 0, JobId: 0u);
		}
		try
		{
			List<ulong> list = getRegisteredCIDsSubscriber.InvokeFunc();
			if (list == null)
			{
				return (Level: 0, JobId: 0u);
			}
			foreach (ulong item in list)
			{
				AutoRetainerCharacterSnapshot autoRetainerCharacterSnapshot = TryReadCharacterSnapshot(item);
				if (!(autoRetainerCharacterSnapshot == null) && string.Equals(autoRetainerCharacterSnapshot.CharacterKey, characterNameWithWorld, StringComparison.OrdinalIgnoreCase))
				{
					CombatJobResolution combatJobResolution = combatJobResolver.ResolveLevelArray(autoRetainerCharacterSnapshot.ClassJobLevels);
					return (Level: combatJobResolution.HighestLevel, JobId: combatJobResolution.HighestJobId);
				}
			}
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] Failed to read highest combat level and ID for " + characterNameWithWorld + ": " + ex.Message);
		}
		return (Level: 0, JobId: 0u);
	}

	public bool TryGetClassJobLevels(string characterNameWithWorld, out IReadOnlyList<int> levels)
	{
		levels = Array.Empty<int>();
		if (string.IsNullOrWhiteSpace(characterNameWithWorld))
		{
			return false;
		}
		if (classJobLevelCache.TryGetValue(characterNameWithWorld, out IReadOnlyList<int> value))
		{
			levels = value;
			return levels.Count > 0;
		}
		TryEnsureAvailable();
		if (!IsAvailable || getRegisteredCIDsSubscriber == null)
		{
			return false;
		}
		try
		{
			List<ulong> list = getRegisteredCIDsSubscriber.InvokeFunc();
			if (list == null)
			{
				return false;
			}
			foreach (ulong item in list)
			{
				AutoRetainerCharacterSnapshot autoRetainerCharacterSnapshot = TryReadCharacterSnapshot(item);
				if (!(autoRetainerCharacterSnapshot == null) && string.Equals(autoRetainerCharacterSnapshot.CharacterKey, characterNameWithWorld, StringComparison.OrdinalIgnoreCase))
				{
					levels = autoRetainerCharacterSnapshot.ClassJobLevels.ToArray();
					return levels.Count > 0;
				}
			}
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] Failed to read class/job levels for " + characterNameWithWorld + ": " + ex.Message);
		}
		return false;
	}

	private uint GetLiveGrandCompanyId(string characterNameWithWorld)
	{
		try
		{
			if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
			{
				return 0u;
			}
			if (!string.Equals($"{objectTable.LocalPlayer.Name}@{playerState.HomeWorld.Value.Name}", characterNameWithWorld, StringComparison.OrdinalIgnoreCase))
			{
				return 0u;
			}
			return playerState.GrandCompany.RowId;
		}
		catch
		{
			return 0u;
		}
	}

	private string GetCharacterNameFromCID(ulong cid)
	{
		if (characterCache.TryGetValue(cid, out string value))
		{
			if (value.Contains("@"))
			{
				return value;
			}
			characterCache.Remove(cid);
		}
		if (unknownCIDs.Contains(cid))
		{
			return $"Unknown (CID: {cid})";
		}
		AutoRetainerCharacterSnapshot autoRetainerCharacterSnapshot = TryReadCharacterSnapshot(cid);
		if (autoRetainerCharacterSnapshot != null && !string.IsNullOrWhiteSpace(autoRetainerCharacterSnapshot.Name) && !string.IsNullOrWhiteSpace(autoRetainerCharacterSnapshot.HomeWorld) && !string.Equals(autoRetainerCharacterSnapshot.Name, "Unknown", StringComparison.OrdinalIgnoreCase))
		{
			characterCache[cid] = autoRetainerCharacterSnapshot.CharacterKey;
			grandCompanyRankCache[autoRetainerCharacterSnapshot.CharacterKey] = Math.Max((int)autoRetainerCharacterSnapshot.GrandCompanyRank, 0);
			return autoRetainerCharacterSnapshot.CharacterKey;
		}
		unknownCIDs.Add(cid);
		return $"Unknown (CID: {cid})";
	}

	public string? GetCurrentCharacter()
	{
		try
		{
			string result = null;
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					if (!clientState.IsLoggedIn)
					{
						result = null;
					}
					else if (objectTable.LocalPlayer == null)
					{
						result = null;
					}
					else
					{
						string text = objectTable.LocalPlayer.Name.ToString();
						string text2 = playerState.HomeWorld.Value.Name.ToString();
						if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text2))
						{
							result = null;
						}
						else
						{
							result = text + "@" + text2;
						}
					}
				}
				catch (Exception ex2)
				{
					log.Debug("[AutoRetainerIPC] GetCurrentCharacter inner failed: " + ex2.Message);
					result = null;
				}
			}).Wait();
			return result;
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] GetCurrentCharacter failed: " + ex.Message);
			return null;
		}
	}

	public bool SwitchCharacter(string characterNameWithWorld)
	{
		if (string.IsNullOrEmpty(characterNameWithWorld))
		{
			log.Warning("[AutoRetainerIPC] Character name is null or empty");
			return false;
		}
		TryEnsureAvailable();
		if (!IsAvailable)
		{
			log.Warning("[AutoRetainerIPC] AutoRetainer not available");
			return false;
		}
		try
		{
			log.Information("[AutoRetainerIPC] Requesting relog to: " + characterNameWithWorld);
			string command = "/ays relog " + characterNameWithWorld;
			bool success = false;
			framework.RunOnFrameworkThread(delegate
			{
				try
				{
					success = commandManager.ProcessCommand(command);
					if (success)
					{
						log.Information("[AutoRetainerIPC] Relog command accepted: " + command);
					}
					else
					{
						log.Warning("[AutoRetainerIPC] Relog command was rejected and will be retried: " + command);
					}
				}
				catch (Exception ex2)
				{
					log.Error("[AutoRetainerIPC] Failed to execute relog command: " + ex2.Message);
					success = false;
				}
			}).Wait();
			return success;
		}
		catch (Exception ex)
		{
			log.Error("[AutoRetainerIPC] Failed to switch character: " + ex.Message);
			return false;
		}
	}

	public bool GetMultiModeEnabled()
	{
		bool enabled;
		return TryGetMultiModeEnabled(out enabled) && enabled;
	}

	public bool TryGetMultiModeEnabled(out bool enabled)
	{
		enabled = false;
		TryEnsureAvailable();
		if (!IsAvailable || getMultiModeEnabledSubscriber == null)
		{
			log.Debug("[AutoRetainerIPC] Multi-Mode IPC not available");
			return false;
		}
		try
		{
			enabled = getMultiModeEnabledSubscriber.InvokeFunc();
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[AutoRetainerIPC] GetMultiModeEnabled failed: " + ex.Message);
			return false;
		}
	}

	public bool SetMultiModeEnabled(bool enabled)
	{
		TryEnsureAvailable();
		if (!IsAvailable || setMultiModeEnabledSubscriber == null)
		{
			log.Warning("[AutoRetainerIPC] Multi-Mode IPC not available");
			return false;
		}
		try
		{
			setMultiModeEnabledSubscriber.InvokeAction(enabled);
			log.Information($"[AutoRetainerIPC] Multi-Mode set to: {enabled}");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[AutoRetainerIPC] SetMultiModeEnabled failed: " + ex.Message);
			return false;
		}
	}

	public bool GetBusy()
	{
		bool busy;
		return TryGetBusy(out busy) && busy;
	}

	public bool TryGetBusy(out bool busy)
	{
		busy = false;
		TryEnsureAvailable();
		if (!IsAvailable)
		{
			return false;
		}
		try
		{
			ICallGateSubscriber<bool> ipcSubscriber = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
			busy = ipcSubscriber.InvokeFunc();
			return true;
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] PluginState.IsBusy failed: " + ex.Message);
			return false;
		}
	}

	public bool GetSuppressed()
	{
		TryEnsureAvailable();
		if (!IsAvailable)
		{
			return false;
		}
		try
		{
			return pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Debug("[AutoRetainerIPC] GetSuppressed failed: " + ex.Message);
			return false;
		}
	}

	public void Dispose()
	{
		IsAvailable = false;
		characterCache.Clear();
		grandCompanyRankCache.Clear();
		unknownCIDs.Clear();
		log.Information("[AutoRetainerIPC] Service disposed");
	}
}
