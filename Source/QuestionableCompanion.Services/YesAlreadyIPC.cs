using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class YesAlreadyIPC : IDisposable
{
	private const string StopRequestsKey = "YesAlready.StopRequests";

	private const string RetainerLockName = "QSTCompanion.RetainerSetup";

	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private bool retainerLockHeld;

	private readonly ICallGateSubscriber<bool, object?>? setPluginEnabledSubscriber;

	private readonly ICallGateSubscriber<bool>? isPluginEnabledSubscriber;

	public bool IsAvailable => isPluginEnabledSubscriber != null;

	public YesAlreadyIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		try
		{
			setPluginEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("YesAlready.SetPluginEnabled");
			isPluginEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("YesAlready.IsPluginEnabled");
		}
		catch (Exception ex)
		{
			log.Error("[YesAlreadyIPC] Failed to initialize subscribers: " + ex.Message);
		}
	}

	public bool PauseForRetainerFlow()
	{
		if (retainerLockHeld)
		{
			return true;
		}
		try
		{
			HashSet<string> orCreateData = pluginInterface.GetOrCreateData("YesAlready.StopRequests", () => new HashSet<string>());
			orCreateData.Add("QSTCompanion.RetainerSetup");
			retainerLockHeld = orCreateData.Contains("QSTCompanion.RetainerSetup");
			if (retainerLockHeld)
			{
				log.Information("[YesAlreadyIPC] Paused YesAlready for the QST-owned retainer flow.");
			}
			return retainerLockHeld;
		}
		catch (Exception ex)
		{
			log.Error("[YesAlreadyIPC] Failed to acquire the retainer-flow stop request: " + ex.Message);
			return false;
		}
	}

	public void ResumeAfterRetainerFlow()
	{
		if (!retainerLockHeld)
		{
			return;
		}
		try
		{
			pluginInterface.GetOrCreateData("YesAlready.StopRequests", () => new HashSet<string>()).Remove("QSTCompanion.RetainerSetup");
			retainerLockHeld = false;
			log.Information("[YesAlreadyIPC] Released the QST-owned retainer-flow stop request.");
		}
		catch (Exception ex)
		{
			log.Error("[YesAlreadyIPC] Failed to release the retainer-flow stop request: " + ex.Message);
		}
	}

	public void EnablePlugin()
	{
		SetPluginEnabled(enabled: true);
	}

	public void DisablePlugin()
	{
		SetPluginEnabled(enabled: false);
	}

	public void SetPluginEnabled(bool enabled)
	{
		if (setPluginEnabledSubscriber == null)
		{
			return;
		}
		try
		{
			setPluginEnabledSubscriber.InvokeAction(enabled);
			log.Information($"[YesAlreadyIPC] SetPluginEnabled via IPC -> {enabled}");
		}
		catch (Exception ex)
		{
			log.Error($"[YesAlreadyIPC] SetPluginEnabled({enabled}) IPC failed: {ex.Message}");
		}
	}

	public bool GetPluginEnabled()
	{
		if (isPluginEnabledSubscriber == null)
		{
			return false;
		}
		try
		{
			return isPluginEnabledSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Error("[YesAlreadyIPC] IsPluginEnabled IPC failed: " + ex.Message);
			return false;
		}
	}

	public void Dispose()
	{
		ResumeAfterRetainerFlow();
	}
}
