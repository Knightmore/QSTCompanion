using System;
using System.Numerics;
using System.Threading;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class MovementMonitorService : IDisposable
{
	public sealed class ScopedMonitoringSession : IDisposable
	{
		private readonly MovementMonitorService service;

		private readonly string context;

		private readonly bool ownsMonitoring;

		private int recoveryRequested;

		private int recoveryAttempts;

		private bool disposed;

		public bool Enabled { get; }

		internal ScopedMonitoringSession(MovementMonitorService service, string context)
		{
			this.service = service;
			this.context = context;
			Enabled = false;
			if (Enabled)
			{
				ownsMonitoring = !service.IsMonitoring;
				service.ScopedStuckDetected += HandleStuckDetected;
				if (ownsMonitoring)
				{
					service.StartMonitoring();
				}
				service.ResetMovementTimer();
				service.log.Information("[MovementMonitor] Scoped monitoring started for " + context + ".");
			}
		}

		public bool ConsumeRecoveryRequest()
		{
			return Interlocked.Exchange(ref recoveryRequested, 0) != 0;
		}

		public int RegisterRecoveryAttempt()
		{
			return Interlocked.Increment(ref recoveryAttempts);
		}

		public void ResetMovementTimer()
		{
			if (Enabled)
			{
				service.ResetMovementTimer();
			}
		}

		private void HandleStuckDetected(object? sender, StuckDetectedEventArgs args)
		{
			args.Handled = true;
			Interlocked.Exchange(ref recoveryRequested, 1);
			service.log.Warning("[MovementMonitor] Scoped recovery requested for " + context + "; the owning workflow will reload and restart Questionable.");
		}

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}
			disposed = true;
			if (Enabled)
			{
				service.ScopedStuckDetected -= HandleStuckDetected;
				if (ownsMonitoring)
				{
					service.StopMonitoring();
				}
				service.log.Information("[MovementMonitor] Scoped monitoring stopped for " + context + ".");
			}
		}
	}

	private readonly IClientState clientState;

	private readonly IPluginLog log;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly Configuration config;

	private ChauffeurModeService? chauffeurMode;

	private Vector3 lastPosition = Vector3.Zero;

	private DateTime lastMovementTime = DateTime.Now;

	private DateTime lastCheckTime = DateTime.MinValue;

	private bool isMonitoring;

	private const float MovementThreshold = 0.1f;

	public bool IsMonitoring => isMonitoring;

	public static event EventHandler<StuckDetectedEventArgs>? OnStuckDetected;

	private event EventHandler<StuckDetectedEventArgs>? ScopedStuckDetected;

	public MovementMonitorService(IClientState clientState, IPluginLog log, ICommandManager commandManager, IFramework framework, Configuration config)
	{
		this.clientState = clientState;
		this.log = log;
		this.commandManager = commandManager;
		this.framework = framework;
		this.config = config;
		log.Information("[MovementMonitor] Service initialized");
	}

	public void SetChauffeurMode(ChauffeurModeService service)
	{
		chauffeurMode = service;
		log.Information("[MovementMonitor] ChauffeurMode service linked for failsafe");
	}

	public void StartMonitoring()
	{
		if (!isMonitoring)
		{
			isMonitoring = true;
			lastMovementTime = DateTime.Now;
			lastCheckTime = DateTime.Now;
			lastPosition = Vector3.Zero;
			framework.Update += OnFrameworkUpdate;
			log.Information($"[MovementMonitor] Movement monitoring started (check every {config.MovementCheckInterval}s, recover after {config.MovementStuckThreshold}s without movement)");
		}
	}

	public void StopMonitoring()
	{
		if (isMonitoring)
		{
			isMonitoring = false;
			framework.Update -= OnFrameworkUpdate;
			log.Information("[MovementMonitor] Movement monitoring stopped");
		}
	}

	public void ResetMovementTimer()
	{
		lastMovementTime = DateTime.Now;
		if (Plugin.ObjectTable.LocalPlayer != null)
		{
			lastPosition = Plugin.ObjectTable.LocalPlayer.Position;
		}
	}

	public ScopedMonitoringSession BeginScopedMonitoring(string context)
	{
		return new ScopedMonitoringSession(this, context);
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!isMonitoring)
		{
			return;
		}
		DateTime now = DateTime.Now;
		if ((now - lastCheckTime).TotalSeconds < (double)config.MovementCheckInterval)
		{
			return;
		}
		lastCheckTime = now;
		if (Plugin.ObjectTable.LocalPlayer == null || !clientState.IsLoggedIn)
		{
			return;
		}
		try
		{
			Vector3 position = Plugin.ObjectTable.LocalPlayer.Position;
			if (lastPosition == Vector3.Zero)
			{
				lastPosition = position;
				lastMovementTime = now;
				return;
			}
			if (Vector3.Distance(lastPosition, position) > 0.1f)
			{
				lastMovementTime = now;
				lastPosition = position;
				return;
			}
			double totalSeconds = (now - lastMovementTime).TotalSeconds;
			if (!(totalSeconds >= (double)config.MovementStuckThreshold))
			{
				return;
			}
			log.Warning("[MovementMonitor] ========================================");
			log.Warning("[MovementMonitor] === PLAYER STUCK DETECTED ===");
			log.Warning("[MovementMonitor] ========================================");
			log.Warning($"[MovementMonitor] No movement for {totalSeconds:F1} seconds");
			log.Warning($"[MovementMonitor] Position: {position}");
			if (chauffeurMode != null && (chauffeurMode.IsWaitingForHelper || chauffeurMode.IsTransportingQuester))
			{
				log.Warning("[MovementMonitor] FAILSAFE: Resetting Chauffeur Mode due to stuck detection!");
				chauffeurMode.ResetChauffeurState();
			}
			StuckDetectedEventArgs e = new StuckDetectedEventArgs();
			this.ScopedStuckDetected?.Invoke(this, e);
			if (!e.Handled)
			{
				MovementMonitorService.OnStuckDetected?.Invoke(this, e);
			}
			if (e.Handled)
			{
				lastMovementTime = now;
				lastPosition = position;
				log.Information("[MovementMonitor] Automatic recovery suppressed by the owning workflow.");
				return;
			}
			log.Warning("[MovementMonitor] Sending /qst reload followed by /qst start...");
			framework.RunOnTick(delegate
			{
				try
				{
					commandManager.ProcessCommand("/qst reload");
					log.Information("[MovementMonitor] /qst reload command sent");
				}
				catch (Exception ex2)
				{
					log.Error("[MovementMonitor] Failed to send /qst reload: " + ex2.Message);
				}
			}, TimeSpan.FromMilliseconds(100L));
			framework.RunOnTick(delegate
			{
				try
				{
					commandManager.ProcessCommand("/qst start");
					log.Information("[MovementMonitor] /qst start command sent");
				}
				catch (Exception ex2)
				{
					log.Error("[MovementMonitor] Failed to send /qst start: " + ex2.Message);
				}
			}, TimeSpan.FromSeconds(1L));
			lastMovementTime = now;
			lastPosition = position;
			log.Information("[MovementMonitor] Movement timer reset - monitoring continues...");
		}
		catch (Exception ex)
		{
			log.Error("[MovementMonitor] Error checking movement: " + ex.Message);
		}
	}

	public void Dispose()
	{
		StopMonitoring();
		log.Information("[MovementMonitor] Service disposed");
	}
}
