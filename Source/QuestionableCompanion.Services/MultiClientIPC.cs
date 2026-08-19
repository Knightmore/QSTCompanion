using System;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Helpers;

namespace QuestionableCompanion.Services;

public class MultiClientIPC : IDisposable
{
	private readonly IDalamudPluginInterface pluginInterface;

	private readonly IPluginLog log;

	private int disposed;

	private readonly ICallGateProvider<string, ushort, object?> requestHelperProvider;

	private readonly ICallGateProvider<object?> dismissHelperProvider;

	private readonly ICallGateProvider<string, ushort, object?> helperAvailableProvider;

	private readonly ICallGateProvider<string, object?> chatMessageProvider;

	private readonly ICallGateProvider<string, ushort, object?> passengerMountedProvider;

	private readonly ICallGateProvider<string, ushort, string, object?> helperStatusProvider;

	private readonly ICallGateProvider<object?> requestHelperAnnouncementsProvider;

	private readonly ICallGateProvider<string, ushort, string, ushort, object?> requestPartyInviteProvider;

	private readonly ICallGateSubscriber<string, ushort, object?> requestHelperSubscriber;

	private readonly ICallGateSubscriber<object?> dismissHelperSubscriber;

	private readonly ICallGateSubscriber<string, ushort, object?> helperAvailableSubscriber;

	private readonly ICallGateSubscriber<string, object?> chatMessageSubscriber;

	private readonly ICallGateSubscriber<string, ushort, object?> passengerMountedSubscriber;

	private readonly ICallGateSubscriber<string, ushort, string, object?> helperStatusSubscriber;

	private readonly ICallGateSubscriber<object?> requestHelperAnnouncementsSubscriber;

	private readonly ICallGateSubscriber<string, ushort, string, ushort, object?> requestPartyInviteSubscriber;

	public event Action<string, ushort>? OnHelperRequested;

	public event Action? OnHelperDismissed;

	public event Action<string, ushort>? OnHelperAvailable;

	public event Action<string>? OnChatMessageReceived;

	public event Action<string, ushort>? OnPassengerMounted;

	public event Action<string, ushort, string>? OnHelperStatusUpdate;

	public event Action? OnRequestHelperAnnouncements;

	public event Action<string, ushort, string, ushort>? OnPartyInviteRequested;

	public MultiClientIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
	{
		this.pluginInterface = pluginInterface;
		this.log = log;
		requestHelperProvider = pluginInterface.GetIpcProvider<string, ushort, object>("QSTCompanion.RequestHelper");
		dismissHelperProvider = pluginInterface.GetIpcProvider<object>("QSTCompanion.DismissHelper");
		helperAvailableProvider = pluginInterface.GetIpcProvider<string, ushort, object>("QSTCompanion.HelperAvailable");
		chatMessageProvider = pluginInterface.GetIpcProvider<string, object>("QSTCompanion.ChatMessage");
		passengerMountedProvider = pluginInterface.GetIpcProvider<string, ushort, object>("QSTCompanion.PassengerMounted");
		helperStatusProvider = pluginInterface.GetIpcProvider<string, ushort, string, object>("QSTCompanion.HelperStatus");
		requestHelperAnnouncementsProvider = pluginInterface.GetIpcProvider<object>("QSTCompanion.RequestHelperAnnouncements");
		requestPartyInviteProvider = pluginInterface.GetIpcProvider<string, ushort, string, ushort, object>("QSTCompanion.RequestPartyInvite");
		requestHelperSubscriber = pluginInterface.GetIpcSubscriber<string, ushort, object>("QSTCompanion.RequestHelper");
		dismissHelperSubscriber = pluginInterface.GetIpcSubscriber<object>("QSTCompanion.DismissHelper");
		helperAvailableSubscriber = pluginInterface.GetIpcSubscriber<string, ushort, object>("QSTCompanion.HelperAvailable");
		chatMessageSubscriber = pluginInterface.GetIpcSubscriber<string, object>("QSTCompanion.ChatMessage");
		passengerMountedSubscriber = pluginInterface.GetIpcSubscriber<string, ushort, object>("QSTCompanion.PassengerMounted");
		helperStatusSubscriber = pluginInterface.GetIpcSubscriber<string, ushort, string, object>("QSTCompanion.HelperStatus");
		requestHelperAnnouncementsSubscriber = pluginInterface.GetIpcSubscriber<object>("QSTCompanion.RequestHelperAnnouncements");
		requestPartyInviteSubscriber = pluginInterface.GetIpcSubscriber<string, ushort, string, ushort, object>("QSTCompanion.RequestPartyInvite");
		requestHelperProvider.RegisterFunc(delegate(string name, ushort worldId)
		{
			OnRequestHelperReceived(name, worldId);
			return (object?)null;
		});
		dismissHelperProvider.RegisterFunc(delegate
		{
			OnDismissHelperReceived();
			return (object?)null;
		});
		helperAvailableProvider.RegisterFunc(delegate(string name, ushort worldId)
		{
			OnHelperAvailableReceived(name, worldId);
			return (object?)null;
		});
		chatMessageProvider.RegisterFunc(delegate(string message)
		{
			OnChatMessageReceivedInternal(message);
			return (object?)null;
		});
		passengerMountedProvider.RegisterFunc(delegate(string questerName, ushort questerWorld)
		{
			OnPassengerMountedReceived(questerName, questerWorld);
			return (object?)null;
		});
		helperStatusProvider.RegisterFunc(delegate(string helperName, ushort helperWorld, string status)
		{
			OnHelperStatusReceived(helperName, helperWorld, status);
			return (object?)null;
		});
		requestHelperAnnouncementsProvider.RegisterFunc(delegate
		{
			OnRequestHelperAnnouncementsReceived();
			return (object?)null;
		});
		requestPartyInviteProvider.RegisterFunc(delegate(string targetName, ushort targetWorld, string questerName, ushort questerWorld)
		{
			OnPartyInviteRequestedReceived(targetName, targetWorld, questerName, questerWorld);
			return (object?)null;
		});
		log.Information("[MultiClientIPC] ✅ IPC initialized successfully");
	}

	public void RequestHelper(string characterName, ushort worldId)
	{
		try
		{
			log.Information($"[MultiClientIPC] Broadcasting helper request: {characterName}@{worldId}");
			requestHelperSubscriber.InvokeFunc(characterName, worldId);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to send helper request: " + ex.Message);
		}
	}

	public void DismissHelper()
	{
		try
		{
			log.Information("[MultiClientIPC] Broadcasting helper dismiss");
			dismissHelperSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to send helper dismiss: " + ex.Message);
		}
	}

	private void OnRequestHelperReceived(string characterName, ushort worldId)
	{
		try
		{
			log.Information($"[MultiClientIPC] Received helper request: {characterName}@{worldId}");
			this.OnHelperRequested?.Invoke(characterName, worldId);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling helper request: " + ex.Message);
		}
	}

	private void OnDismissHelperReceived()
	{
		try
		{
			log.Information("[MultiClientIPC] Received helper dismiss");
			this.OnHelperDismissed?.Invoke();
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling helper dismiss: " + ex.Message);
		}
	}

	public void AnnounceHelperAvailable(string characterName, ushort worldId)
	{
		try
		{
			log.Information($"[MultiClientIPC] Broadcasting helper availability: {characterName}@{worldId}");
			helperAvailableSubscriber.InvokeFunc(characterName, worldId);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to announce helper: " + ex.Message);
		}
	}

	private void OnHelperAvailableReceived(string characterName, ushort worldId)
	{
		try
		{
			log.Information($"[MultiClientIPC] Received helper available: {characterName}@{worldId}");
			this.OnHelperAvailable?.Invoke(characterName, worldId);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling helper available: " + ex.Message);
		}
	}

	public void SendChatMessage(string message)
	{
		try
		{
			log.Information("[MultiClientIPC] Broadcasting chat message: " + message);
			chatMessageSubscriber.InvokeFunc(message);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to send chat message: " + ex.Message);
		}
	}

	private void OnChatMessageReceivedInternal(string message)
	{
		try
		{
			log.Information("[MultiClientIPC] Received chat message: " + message);
			this.OnChatMessageReceived?.Invoke(message);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling chat message: " + ex.Message);
		}
	}

	public void SendPassengerMounted(string questerName, ushort questerWorld)
	{
		try
		{
			log.Information("[MultiClientIPC] Broadcasting passenger mounted: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
			passengerMountedSubscriber.InvokeFunc(questerName, questerWorld);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to send passenger mounted: " + ex.Message);
		}
	}

	private void OnPassengerMountedReceived(string questerName, ushort questerWorld)
	{
		try
		{
			log.Information("[MultiClientIPC] Received passenger mounted: " + questerName + "@" + WorldNameHelper.GetWorldName(questerWorld));
			this.OnPassengerMounted?.Invoke(questerName, questerWorld);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling passenger mounted: " + ex.Message);
		}
	}

	public void BroadcastHelperStatus(string helperName, ushort worldId, string status)
	{
		try
		{
			log.Debug($"[MultiClientIPC] Broadcasting helper status: {helperName}@{worldId} = {status}");
			helperStatusSubscriber.InvokeFunc(helperName, worldId, status);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to broadcast helper status: " + ex.Message);
		}
	}

	private void OnHelperStatusReceived(string helperName, ushort helperWorld, string status)
	{
		try
		{
			log.Debug($"[MultiClientIPC] Received helper status: {helperName}@{helperWorld} = {status}");
			this.OnHelperStatusUpdate?.Invoke(helperName, helperWorld, status);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling helper status: " + ex.Message);
		}
	}

	public void BroadcastRequestHelperAnnouncements()
	{
		try
		{
			log.Information("[MultiClientIPC] Broadcasting request for helper announcements");
			requestHelperAnnouncementsSubscriber.InvokeFunc();
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to broadcast request for helper announcements: " + ex.Message);
		}
	}

	private void OnRequestHelperAnnouncementsReceived()
	{
		try
		{
			log.Information("[MultiClientIPC] Received request for helper announcements");
			this.OnRequestHelperAnnouncements?.Invoke();
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling request for helper announcements: " + ex.Message);
		}
	}

	public void RequestPartyInvite(string targetHelperName, ushort targetHelperWorld, string questerName, ushort questerWorld)
	{
		try
		{
			log.Information($"[MultiClientIPC] Broadcasting Party Invite Request -> Target: {targetHelperName}@{targetHelperWorld} from {questerName}@{questerWorld}");
			requestPartyInviteSubscriber.InvokeFunc(targetHelperName, targetHelperWorld, questerName, questerWorld);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Failed to send Party Invite Request: " + ex.Message);
		}
	}

	private void OnPartyInviteRequestedReceived(string targetHelperName, ushort targetHelperWorld, string questerName, ushort questerWorld)
	{
		try
		{
			log.Information($"[MultiClientIPC] Received Party Invite Request -> Target: {targetHelperName}@{targetHelperWorld} from {questerName}@{questerWorld}");
			this.OnPartyInviteRequested?.Invoke(targetHelperName, targetHelperWorld, questerName, questerWorld);
		}
		catch (Exception ex)
		{
			log.Error("[MultiClientIPC] Error handling Party Invite Request: " + ex.Message);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) == 0)
		{
			Unregister(requestHelperProvider.UnregisterFunc, "RequestHelper");
			Unregister(dismissHelperProvider.UnregisterFunc, "DismissHelper");
			Unregister(helperAvailableProvider.UnregisterFunc, "HelperAvailable");
			Unregister(chatMessageProvider.UnregisterFunc, "ChatMessage");
			Unregister(passengerMountedProvider.UnregisterFunc, "PassengerMounted");
			Unregister(helperStatusProvider.UnregisterFunc, "HelperStatus");
			Unregister(requestHelperAnnouncementsProvider.UnregisterFunc, "RequestHelperAnnouncements");
			Unregister(requestPartyInviteProvider.UnregisterFunc, "RequestPartyInvite");
		}
	}

	private void Unregister(Action unregister, string channel)
	{
		try
		{
			unregister();
		}
		catch (Exception exception)
		{
			log.Error(exception, "[MultiClientIPC] Failed to unregister " + channel + ".");
		}
	}
}
