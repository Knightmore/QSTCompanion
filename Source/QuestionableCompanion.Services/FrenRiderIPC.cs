using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public sealed class FrenRiderIPC : IDisposable
{
	public const string RepositoryUrl = "https://aethertek.io/x.json";

	private const string InternalName = "FrenRider";

	private const string IsReadyEndpoint = "FrenRider.CombatOnly.IsReady";

	private const string ClearFrenNameEndpoint = "FrenRider.CombatOnly.ClearFrenName";

	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private readonly ICallGateSubscriber<bool> isReadySubscriber;

	private readonly ICallGateSubscriber<bool> clearFrenNameSubscriber;

	private FrenRiderAvailability? cachedAvailability;

	private DateTime cacheExpiresUtc = DateTime.MinValue;

	public string LastFailure { get; private set; } = string.Empty;

	public FrenRiderIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		isReadySubscriber = pluginInterface.GetIpcSubscriber<bool>("FrenRider.CombatOnly.IsReady");
		clearFrenNameSubscriber = pluginInterface.GetIpcSubscriber<bool>("FrenRider.CombatOnly.ClearFrenName");
	}

	public FrenRiderAvailability GetAvailability(bool forceRefresh = false)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!forceRefresh && cachedAvailability != null && utcNow < cacheExpiresUtc)
		{
			return cachedAvailability;
		}
		cachedAvailability = ProbeAvailability();
		cacheExpiresUtc = utcNow.AddSeconds(1.0);
		return cachedAvailability;
	}

	public bool TryPrepareCombat()
	{
		LastFailure = string.Empty;
		FrenRiderAvailability availability = GetAvailability(forceRefresh: true);
		if (!availability.CanSelect)
		{
			LastFailure = availability.Message;
			return false;
		}
		try
		{
			if (clearFrenNameSubscriber.InvokeFunc())
			{
				return true;
			}
			LastFailure = "FrenRider did not clear and save FrenName for the active character.";
		}
		catch (Exception ex)
		{
			LastFailure = "FrenRider combat preparation IPC failed: " + ex.Message;
			log.Debug("[FrenRiderIPC] " + LastFailure);
		}
		cachedAvailability = null;
		return false;
	}

	public void Dispose()
	{
	}

	private FrenRiderAvailability ProbeAvailability()
	{
		IExposedPlugin exposedPlugin = pluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin x) => string.Equals(x.InternalName, "FrenRider", StringComparison.Ordinal));
		if (exposedPlugin == null)
		{
			return new FrenRiderAvailability(FrenRiderAvailabilityKind.Missing, "FrenRider is not installed. Add its custom repository to install it.");
		}
		if (exposedPlugin.IsOutdated || exposedPlugin.IsBanned || exposedPlugin.IsDecommissioned)
		{
			return new FrenRiderAvailability(FrenRiderAvailabilityKind.Incompatible, "The installed FrenRider is incompatible or outdated. Update it from the custom repository.");
		}
		if (!exposedPlugin.IsLoaded)
		{
			return new FrenRiderAvailability(FrenRiderAvailabilityKind.Disabled, "FrenRider is installed but disabled. Enable it in the Dalamud plugin installer.");
		}
		try
		{
			if (isReadySubscriber.InvokeFunc())
			{
				return new FrenRiderAvailability(FrenRiderAvailabilityKind.Ready, $"FrenRider {exposedPlugin.Version} is loaded and ready.");
			}
			return new FrenRiderAvailability(FrenRiderAvailabilityKind.Incompatible, "FrenRider is loaded but its QST combat-only IPC is not ready. Update FrenRider.");
		}
		catch
		{
			return new FrenRiderAvailability(FrenRiderAvailabilityKind.Incompatible, "FrenRider is loaded but does not provide the required QST combat-only IPC. Update FrenRider.");
		}
	}
}
