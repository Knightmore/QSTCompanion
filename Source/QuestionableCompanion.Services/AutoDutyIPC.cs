using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public sealed class AutoDutyIPC : IDisposable
{
	private readonly IPluginLog log;

	private readonly ICallGateSubscriber<uint, bool> contentHasPathSubscriber;

	private readonly ICallGateSubscriber<bool> isStoppedSubscriber;

	private readonly ICallGateSubscriber<string, object, object> setConfigSubscriber;

	private readonly ICallGateSubscriber<uint, int, bool, object> runSubscriber;

	public bool IsAvailable
	{
		get
		{
			try
			{
				isStoppedSubscriber.InvokeFunc();
				return true;
			}
			catch
			{
				return false;
			}
		}
	}

	public AutoDutyIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
	{
		this.log = log;
		contentHasPathSubscriber = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
		isStoppedSubscriber = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
		setConfigSubscriber = pluginInterface.GetIpcSubscriber<string, object, object>("AutoDuty.SetConfig");
		runSubscriber = pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
	}

	public bool IsStopped()
	{
		try
		{
			return isStoppedSubscriber.InvokeFunc();
		}
		catch
		{
			return true;
		}
	}

	public bool ContentHasPath(uint territoryType)
	{
		try
		{
			return contentHasPathSubscriber.InvokeFunc(territoryType);
		}
		catch (Exception ex)
		{
			log.Warning($"[AutoDutyIPC] ContentHasPath({territoryType}) failed: {ex.Message}");
			return false;
		}
	}

	public bool RunDuty(uint territoryType, bool unsynced, bool bareMode = true)
	{
		try
		{
			if (unsynced)
			{
				setConfigSubscriber.InvokeAction("dutyModeEnum", "Regular");
				setConfigSubscriber.InvokeAction("Unsynced", "true");
			}
			else
			{
				setConfigSubscriber.InvokeAction("dutyModeEnum", "Support");
			}
			runSubscriber.InvokeAction(territoryType, 1, bareMode);
			log.Information($"[AutoDutyIPC] Started duty {territoryType} (unsynced={unsynced})");
			return true;
		}
		catch (Exception ex)
		{
			log.Error($"[AutoDutyIPC] RunDuty({territoryType}) failed: {ex.Message}");
			return false;
		}
	}

	public void Dispose()
	{
	}
}
