using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class LifestreamIPC : IDisposable
{
	private readonly IPluginLog log;

	private readonly IDalamudPluginInterface pluginInterface;

	private ICallGateSubscriber<bool>? isBusySubscriber;

	private ICallGateSubscriber<string, bool>? changeWorldSubscriber;

	private ICallGateSubscriber<uint, bool>? changeWorldByIdSubscriber;

	private ICallGateSubscriber<object>? abortSubscriber;

	private ICallGateSubscriber<uint, byte, bool>? teleportSubscriber;

	private ICallGateSubscriber<string, object>? executeCommandSubscriber;

	private ICallGateSubscriber<bool>? teleportToHomeSubscriber;

	private ICallGateSubscriber<bool>? teleportToFcSubscriber;

	private ICallGateSubscriber<bool>? teleportToApartmentSubscriber;

	private bool _isAvailable;

	private bool _ipcInitialized;

	private DateTime lastAvailabilityCheck = DateTime.MinValue;

	private const int AvailabilityCheckCooldownSeconds = 5;

	private bool hasPerformedInitialCheck;

	private readonly ICommandManager commandManager;

	public bool IsAvailable
	{
		get
		{
			return _isAvailable;
		}
		private set
		{
			_isAvailable = value;
		}
	}

	public LifestreamIPC(IPluginLog log, IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
	{
		this.log = log;
		this.pluginInterface = pluginInterface;
		this.commandManager = commandManager;
	}

	private void InitializeIPC()
	{
		if (_ipcInitialized)
		{
			return;
		}
		try
		{
			isBusySubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
			changeWorldSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
			changeWorldByIdSubscriber = pluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById");
			abortSubscriber = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
			teleportSubscriber = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
			executeCommandSubscriber = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
			teleportToHomeSubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToHome");
			teleportToFcSubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToFC");
			teleportToApartmentSubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToApartment");
			_ipcInitialized = true;
			log.Debug("[LifestreamIPC] IPC subscribers initialized (lazy-loading enabled)");
		}
		catch (Exception ex)
		{
			log.Error("[LifestreamIPC] Failed to initialize subscribers: " + ex.Message);
			_isAvailable = false;
			_ipcInitialized = false;
		}
	}

	private bool TryEnsureAvailable(bool forceCheck = false)
	{
		if (_isAvailable)
		{
			return true;
		}
		if (!_ipcInitialized)
		{
			InitializeIPC();
		}
		if (!_ipcInitialized)
		{
			return false;
		}
		DateTime now = DateTime.Now;
		if (!forceCheck && hasPerformedInitialCheck && (now - lastAvailabilityCheck).TotalSeconds < 5.0)
		{
			log.Debug($"[LifestreamIPC] Cooldown active - skipping check (last check: {(now - lastAvailabilityCheck).TotalSeconds:F1}s ago)");
			return false;
		}
		if (forceCheck)
		{
			log.Information("[LifestreamIPC] FORCED availability check requested");
		}
		lastAvailabilityCheck = now;
		hasPerformedInitialCheck = true;
		try
		{
			if (isBusySubscriber == null)
			{
				log.Debug("[LifestreamIPC] isBusySubscriber is NULL - cannot check availability");
				_isAvailable = false;
				return false;
			}
			log.Debug("[LifestreamIPC] Attempting to invoke Lifestream.IsBusy()...");
			bool value = isBusySubscriber.InvokeFunc();
			if (!_isAvailable)
			{
				_isAvailable = true;
				log.Information($"[LifestreamIPC] Lifestream is now available (Busy: {value})");
			}
			else
			{
				log.Debug($"[LifestreamIPC] Lifestream still available (Busy: {value})");
			}
			return true;
		}
		catch (Exception ex)
		{
			if (!hasPerformedInitialCheck)
			{
				log.Warning("[LifestreamIPC] First availability check FAILED: " + ex.GetType().Name + ": " + ex.Message);
			}
			else
			{
				log.Debug("[LifestreamIPC] Lifestream not yet available: " + ex.Message);
			}
			_isAvailable = false;
			return false;
		}
	}

	public bool IsBusy()
	{
		TryEnsureAvailable();
		if (!_isAvailable || isBusySubscriber == null)
		{
			return false;
		}
		try
		{
			return isBusySubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Error("[LifestreamIPC] Error checking busy status: " + ex.Message);
			return false;
		}
	}

	public bool TryGetBusy(out bool busy)
	{
		busy = false;
		if (!TryEnsureAvailable() || isBusySubscriber == null)
		{
			return false;
		}
		try
		{
			busy = isBusySubscriber.InvokeFunc();
			return true;
		}
		catch (Exception ex)
		{
			log.Debug("[LifestreamIPC] Busy state unavailable: " + ex.Message);
			_isAvailable = false;
			return false;
		}
	}

	public bool ForceCheckAvailability()
	{
		log.Information("[LifestreamIPC] ========================================");
		log.Information("[LifestreamIPC] === FORCING AVAILABILITY CHECK ===");
		log.Information("[LifestreamIPC] ========================================");
		bool flag = TryEnsureAvailable(forceCheck: true);
		log.Information($"[LifestreamIPC] Force check result: {flag}");
		return flag;
	}

	public bool ChangeWorld(string worldName)
	{
		TryEnsureAvailable();
		log.Information("[LifestreamIPC] ========================================");
		log.Information("[LifestreamIPC] === CHANGE WORLD REQUEST ===");
		log.Information("[LifestreamIPC] ========================================");
		log.Information("[LifestreamIPC] Target World: '" + worldName + "'");
		log.Information($"[LifestreamIPC] IsAvailable: {_isAvailable}");
		log.Information($"[LifestreamIPC] changeWorldSubscriber != null: {changeWorldSubscriber != null}");
		if (!_isAvailable || changeWorldSubscriber == null)
		{
			log.Error("[LifestreamIPC] CANNOT CHANGE WORLD - Lifestream not available!");
			log.Error("[LifestreamIPC] Make sure Lifestream plugin is installed and enabled!");
			return false;
		}
		try
		{
			log.Information("[LifestreamIPC] Invoking Lifestream.ChangeWorld('" + worldName + "')...");
			bool num = changeWorldSubscriber.InvokeFunc(worldName);
			if (num)
			{
				log.Information("[LifestreamIPC] ========================================");
				log.Information("[LifestreamIPC] WORLD CHANGE ACCEPTED: " + worldName);
				log.Information("[LifestreamIPC] ========================================");
			}
			else
			{
				log.Warning("[LifestreamIPC] ========================================");
				log.Warning("[LifestreamIPC] WORLD CHANGE REJECTED: " + worldName);
				log.Warning("[LifestreamIPC] ========================================");
				log.Warning("[LifestreamIPC] Possible reasons:");
				log.Warning("[LifestreamIPC] - Lifestream is busy");
				log.Warning("[LifestreamIPC] - World name is invalid");
				log.Warning("[LifestreamIPC] - Cannot visit this world");
			}
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[LifestreamIPC] ========================================");
			log.Error("[LifestreamIPC] ERROR REQUESTING WORLD CHANGE!");
			log.Error("[LifestreamIPC] ========================================");
			log.Error("[LifestreamIPC] Error: " + ex.Message);
			log.Error("[LifestreamIPC] Stack: " + ex.StackTrace);
			return false;
		}
	}

	public bool ChangeWorldById(uint worldId)
	{
		TryEnsureAvailable();
		if (!_isAvailable || changeWorldByIdSubscriber == null)
		{
			log.Warning("[LifestreamIPC] Lifestream not available for world change");
			return false;
		}
		try
		{
			log.Information($"[LifestreamIPC] Requesting world change to ID: {worldId}");
			bool num = changeWorldByIdSubscriber.InvokeFunc(worldId);
			if (num)
			{
				log.Information($"[LifestreamIPC] World change request accepted for ID: {worldId}");
			}
			else
			{
				log.Warning($"[LifestreamIPC] World change request rejected for ID: {worldId}");
			}
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[LifestreamIPC] Error requesting world change by ID: " + ex.Message);
			return false;
		}
	}

	public void Abort()
	{
		TryEnsureAvailable();
		if (!_isAvailable || abortSubscriber == null)
		{
			return;
		}
		try
		{
			abortSubscriber.InvokeAction();
			log.Information("[LifestreamIPC] Abort request sent to Lifestream");
		}
		catch (Exception ex)
		{
			log.Error("[LifestreamIPC] Error aborting Lifestream: " + ex.Message);
		}
	}

	public bool ExecuteCommand(string command)
	{
		TryEnsureAvailable();
		try
		{
			if (_isAvailable && executeCommandSubscriber != null)
			{
				executeCommandSubscriber.InvokeAction(command);
				log.Information("[LifestreamIPC] ExecuteCommand sent via IPC: " + command);
				return true;
			}
		}
		catch (Exception ex)
		{
			log.Warning("[LifestreamIPC] ExecuteCommand IPC failed for '" + command + "': " + ex.Message);
		}
		try
		{
			string text = (command.StartsWith("/", StringComparison.Ordinal) ? command : ("/li " + command));
			commandManager.ProcessCommand(text);
			log.Information("[LifestreamIPC] ExecuteCommand fallback sent: " + text);
			return true;
		}
		catch (Exception ex2)
		{
			log.Error("[LifestreamIPC] ExecuteCommand fallback failed for '" + command + "': " + ex2.Message);
			return false;
		}
	}

	public bool Teleport(uint aetheryteId, byte subIndex, string aetheryteName)
	{
		TryEnsureAvailable();
		if (_isAvailable && teleportSubscriber != null)
		{
			try
			{
				bool num = teleportSubscriber.InvokeFunc(aetheryteId, subIndex);
				if (num)
				{
					log.Information($"[LifestreamIPC] Typed teleport accepted: aetheryteId={aetheryteId}, subIndex={subIndex}, name=\"{aetheryteName}\"");
				}
				else
				{
					log.Warning($"[LifestreamIPC] Typed teleport rejected: aetheryteId={aetheryteId}, subIndex={subIndex}, name=\"{aetheryteName}\"");
				}
				return num;
			}
			catch (Exception ex)
			{
				log.Warning($"[LifestreamIPC] Typed teleport IPC failed for aetheryteId={aetheryteId}, subIndex={subIndex}: {ex.Message}");
			}
		}
		else
		{
			log.Warning($"[LifestreamIPC] Typed teleport IPC unavailable for aetheryteId={aetheryteId}, subIndex={subIndex}; using chat fallback.");
		}
		try
		{
			string text = "/li tp " + aetheryteName;
			commandManager.ProcessCommand(text);
			log.Information("[LifestreamIPC] Teleport fallback sent: " + text);
			return true;
		}
		catch (Exception ex2)
		{
			log.Error("[LifestreamIPC] Teleport fallback failed for '" + aetheryteName + "': " + ex2.Message);
			return false;
		}
	}

	public bool ReturnTo(HuntLogReturnDestination destination)
	{
		TryEnsureAvailable();
		try
		{
			if (_isAvailable && destination switch
			{
				HuntLogReturnDestination.Home => teleportToHomeSubscriber?.InvokeFunc(), 
				HuntLogReturnDestination.FreeCompany => teleportToFcSubscriber?.InvokeFunc(), 
				HuntLogReturnDestination.Apartment => teleportToApartmentSubscriber?.InvokeFunc(), 
				HuntLogReturnDestination.Inn => ExecuteCommand("inn"), 
				HuntLogReturnDestination.Auto => ExecuteCommand("auto"), 
				_ => false, 
			} == true)
			{
				return true;
			}
		}
		catch (Exception ex)
		{
			log.Warning($"[LifestreamIPC] ReturnTo({destination}) IPC failed: {ex.Message}");
		}
		return destination switch
		{
			HuntLogReturnDestination.Home => ExecuteCommand("home"), 
			HuntLogReturnDestination.FreeCompany => ExecuteCommand("fc"), 
			HuntLogReturnDestination.Apartment => ExecuteCommand("apt"), 
			HuntLogReturnDestination.Inn => ExecuteCommand("inn"), 
			HuntLogReturnDestination.Auto => ExecuteCommand("auto"), 
			_ => false, 
		};
	}

	public void Dispose()
	{
		log.Information("[LifestreamIPC] Service disposed");
	}

	public void Teleport(string aetheryteName)
	{
		TryEnsureAvailable();
		if (!_isAvailable)
		{
			log.Warning("[LifestreamIPC] Lifestream not available - falling back to vanilla /tp for " + aetheryteName);
		}
		string text = "/li " + aetheryteName;
		log.Information("[LifestreamIPC] Executing command: " + text);
		try
		{
			commandManager.ProcessCommand(text);
		}
		catch (Exception ex)
		{
			log.Error("[LifestreamIPC] Error executing teleport command: " + ex.Message);
		}
	}
}
