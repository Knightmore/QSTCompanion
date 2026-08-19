using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public sealed class HuntDutyRunner : IDisposable
{
	private enum PreparationResult
	{
		Ready,
		FallbackAllowed,
		Blocked
	}

	private readonly object sessionLock = new object();

	private readonly IPluginLog log;

	private readonly ICallGateSubscriber<uint, bool> dadContentHasPath;

	private readonly ICallGateSubscriber<string, string, object> dadSetConfig;

	private readonly ICallGateSubscriber<uint, int, bool, object> dadRun;

	private readonly ICallGateSubscriber<bool> dadIsStopped;

	private readonly ICallGateSubscriber<object> dadStop;

	private readonly ICallGateSubscriber<uint, bool> autoDutyContentHasPath;

	private readonly ICallGateSubscriber<string, object, object> autoDutySetConfig;

	private readonly ICallGateSubscriber<uint, int, bool, object> autoDutyRun;

	private readonly ICallGateSubscriber<bool> autoDutyIsStopped;

	private readonly ICallGateSubscriber<object> autoDutyStop;

	private HuntDutyBackend activeBackend;

	private bool ownsActiveSession;

	public HuntDutyRunner(IDalamudPluginInterface pluginInterface, IPluginLog log)
	{
		this.log = log;
		dadContentHasPath = pluginInterface.GetIpcSubscriber<uint, bool>("dad.Duty.ContentHasPath");
		dadSetConfig = pluginInterface.GetIpcSubscriber<string, string, object>("dad.Duty.SetConfig");
		dadRun = pluginInterface.GetIpcSubscriber<uint, int, bool, object>("dad.Duty.Run");
		dadIsStopped = pluginInterface.GetIpcSubscriber<bool>("dad.Duty.IsStopped");
		dadStop = pluginInterface.GetIpcSubscriber<object>("dad.Duty.Stop");
		autoDutyContentHasPath = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
		autoDutySetConfig = pluginInterface.GetIpcSubscriber<string, object, object>("AutoDuty.SetConfig");
		autoDutyRun = pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
		autoDutyIsStopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
		autoDutyStop = pluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");
	}

	public HuntDutyStartResult StartDuty(uint territoryType, bool unsynced, bool bareMode = true)
	{
		lock (sessionLock)
		{
			if (ownsActiveSession)
			{
				return new HuntDutyStartResult(Started: false, activeBackend, GetBackendName(activeBackend) + " already has an active hunt-owned duty session.");
			}
			string blocker;
			switch (PrepareAutoDuty(territoryType, unsynced, out blocker))
			{
			case PreparationResult.Ready:
				return AttemptRun(HuntDutyBackend.AutoDuty, territoryType, unsynced, bareMode);
			case PreparationResult.Blocked:
				return new HuntDutyStartResult(Started: false, HuntDutyBackend.AutoDuty, blocker);
			default:
			{
				log.Information($"[HuntDutyRunner] AutoDuty unavailable before Run; DAD fallback allowed for territory {territoryType}: {blocker}");
				string blocker2;
				switch (PrepareDad(territoryType, unsynced, out blocker2))
				{
				case PreparationResult.Ready:
				{
					log.Information($"[HuntDutyRunner] Using DAD fallback for territory {territoryType}");
					HuntDutyStartResult huntDutyStartResult = AttemptRun(HuntDutyBackend.Dad, territoryType, unsynced, bareMode);
					return huntDutyStartResult.Started ? huntDutyStartResult with
					{
						Blocker = "AutoDuty fallback: " + blocker
					} : huntDutyStartResult with
					{
						Blocker = "AutoDuty: " + blocker + " DAD: " + huntDutyStartResult.Blocker
					};
				}
				case PreparationResult.Blocked:
					return new HuntDutyStartResult(Started: false, HuntDutyBackend.Dad, "AutoDuty: " + blocker + " DAD: " + blocker2);
				default:
				{
					string blocker3 = "AutoDuty: " + blocker + " DAD: " + blocker2;
					return new HuntDutyStartResult(Started: false, HuntDutyBackend.None, blocker3);
				}
				}
			}
			}
		}
	}

	public HuntDutyPollResult PollOwnedSession()
	{
		lock (sessionLock)
		{
			if (!ownsActiveSession)
			{
				return new HuntDutyPollResult(Succeeded: false, IsStopped: true, HuntDutyBackend.None, "No hunt-owned duty session is active.");
			}
			HuntDutyBackend backend = activeBackend;
			try
			{
				bool isStopped = InvokeIsStopped(backend);
				return new HuntDutyPollResult(Succeeded: true, isStopped, backend, string.Empty);
			}
			catch (Exception ex)
			{
				string text = GetBackendName(backend) + " IsStopped failed: " + ex.Message;
				log.Warning("[HuntDutyRunner] " + text);
				return new HuntDutyPollResult(Succeeded: false, IsStopped: false, backend, text);
			}
		}
	}

	public bool StopOwnedSession(string reason)
	{
		lock (sessionLock)
		{
			if (!ownsActiveSession)
			{
				return false;
			}
			HuntDutyBackend backend = activeBackend;
			try
			{
				InvokeStop(backend);
				if (InvokeIsStopped(backend))
				{
					ReleaseOwnership();
					log.Information("[HuntDutyRunner] Stopped and verified hunt-owned " + GetBackendName(backend) + " duty session: " + reason);
				}
				else
				{
					log.Information($"[HuntDutyRunner] Stop requested for hunt-owned {GetBackendName(backend)} duty session; terminal verification is still pending: {reason}");
				}
			}
			catch (Exception ex)
			{
				log.Warning($"[HuntDutyRunner] {GetBackendName(backend)} Stop failed during {reason}: {ex.Message}");
			}
			return true;
		}
	}

	public static string GetBackendName(HuntDutyBackend backend)
	{
		return backend switch
		{
			HuntDutyBackend.Dad => "DAD", 
			HuntDutyBackend.AutoDuty => "AutoDuty", 
			_ => "None", 
		};
	}

	private PreparationResult PrepareDad(uint territoryType, bool unsynced, out string blocker)
	{
		try
		{
			if (!dadIsStopped.InvokeFunc())
			{
				blocker = "Dad is already running a duty session that is not owned by hunt logs.";
				return PreparationResult.Blocked;
			}
		}
		catch (Exception ex)
		{
			blocker = "unavailable or incompatible IsStopped IPC (" + ex.Message + ").";
			return PreparationResult.FallbackAllowed;
		}
		try
		{
			if (!dadContentHasPath.InvokeFunc(territoryType))
			{
				blocker = $"no compatible path for territory {territoryType}.";
				return PreparationResult.FallbackAllowed;
			}
		}
		catch (Exception ex2)
		{
			blocker = "incompatible ContentHasPath IPC (" + ex2.Message + ").";
			return PreparationResult.FallbackAllowed;
		}
		try
		{
			ConfigureDad(unsynced);
			blocker = string.Empty;
			return PreparationResult.Ready;
		}
		catch (Exception ex3)
		{
			blocker = "incompatible SetConfig IPC (" + ex3.Message + ").";
			return PreparationResult.FallbackAllowed;
		}
	}

	private PreparationResult PrepareAutoDuty(uint territoryType, bool unsynced, out string blocker)
	{
		try
		{
			if (!autoDutyIsStopped.InvokeFunc())
			{
				blocker = "already running a duty session that is not owned by hunt logs.";
				return PreparationResult.Blocked;
			}
		}
		catch (Exception ex)
		{
			blocker = "unavailable or incompatible IsStopped IPC (" + ex.Message + ").";
			return PreparationResult.FallbackAllowed;
		}
		try
		{
			if (!autoDutyContentHasPath.InvokeFunc(territoryType))
			{
				blocker = $"no path for territory {territoryType}.";
				return PreparationResult.FallbackAllowed;
			}
		}
		catch (Exception ex2)
		{
			blocker = "incompatible ContentHasPath IPC (" + ex2.Message + ").";
			return PreparationResult.FallbackAllowed;
		}
		try
		{
			ConfigureAutoDuty(unsynced);
			blocker = string.Empty;
			return PreparationResult.Ready;
		}
		catch (Exception ex3)
		{
			blocker = "incompatible SetConfig IPC (" + ex3.Message + ").";
			return PreparationResult.FallbackAllowed;
		}
	}

	private HuntDutyStartResult AttemptRun(HuntDutyBackend backend, uint territoryType, bool unsynced, bool bareMode)
	{
		activeBackend = backend;
		ownsActiveSession = true;
		try
		{
			InvokeRun(backend, territoryType, bareMode);
		}
		catch (Exception ex)
		{
			string blocker = GetBackendName(backend) + " Run IPC failed: " + ex.Message;
			StopAfterFailedStart(backend, blocker);
			return new HuntDutyStartResult(Started: false, backend, blocker);
		}
		try
		{
			if (InvokeIsStopped(backend))
			{
				string text = GetBackendName(backend) + " stopped immediately after Run; startup was rejected or ended before it could be observed.";
				log.Warning("[HuntDutyRunner] " + text);
				return new HuntDutyStartResult(Started: false, backend, text);
			}
		}
		catch (Exception ex2)
		{
			string blocker2 = GetBackendName(backend) + " startup IsStopped failed after Run: " + ex2.Message;
			StopAfterFailedStart(backend, blocker2);
			return new HuntDutyStartResult(Started: false, backend, blocker2);
		}
		log.Information($"[HuntDutyRunner] Started territory {territoryType} with {GetBackendName(backend)} (unsynced={unsynced}, bareMode={bareMode})");
		return new HuntDutyStartResult(Started: true, backend, string.Empty);
	}

	private void ConfigureDad(bool unsynced)
	{
		dadSetConfig.InvokeAction("Unsynced", unsynced ? "true" : "false");
		dadSetConfig.InvokeAction("dutyModeEnum", unsynced ? "Regular" : "Support");
	}

	private void ConfigureAutoDuty(bool unsynced)
	{
		autoDutySetConfig.InvokeAction("Unsynced", unsynced ? "true" : "false");
		autoDutySetConfig.InvokeAction("dutyModeEnum", unsynced ? "Regular" : "Support");
	}

	private void InvokeRun(HuntDutyBackend backend, uint territoryType, bool bareMode)
	{
		if (backend == HuntDutyBackend.Dad)
		{
			dadRun.InvokeAction(territoryType, 1, bareMode);
		}
		else
		{
			autoDutyRun.InvokeAction(territoryType, 1, bareMode);
		}
	}

	private bool InvokeIsStopped(HuntDutyBackend backend)
	{
		if (backend != HuntDutyBackend.Dad)
		{
			return autoDutyIsStopped.InvokeFunc();
		}
		return dadIsStopped.InvokeFunc();
	}

	private void InvokeStop(HuntDutyBackend backend)
	{
		if (backend == HuntDutyBackend.Dad)
		{
			dadStop.InvokeAction();
		}
		else
		{
			autoDutyStop.InvokeAction();
		}
	}

	private void StopAfterFailedStart(HuntDutyBackend backend, string blocker)
	{
		try
		{
			InvokeStop(backend);
		}
		catch (Exception ex)
		{
			log.Warning("[HuntDutyRunner] " + GetBackendName(backend) + " Stop also failed after startup failure: " + ex.Message);
		}
		log.Warning("[HuntDutyRunner] " + blocker);
	}

	private void ReleaseOwnership()
	{
		ownsActiveSession = false;
		activeBackend = HuntDutyBackend.None;
	}

	public void Dispose()
	{
		StopOwnedSession("hunt duty runner disposed");
	}
}
