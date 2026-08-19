using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.NativeWrapper;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestionableCompanion.Services;

public class ErrorRecoveryService : IDisposable
{
	private delegate char LobbyErrorHandlerDelegate(long a1, long a2, long a3);

	private readonly IPluginLog log;

	private readonly IGameInteropProvider hookProvider;

	private readonly IClientState clientState;

	private readonly IFramework framework;

	private readonly IGameGui gameGui;

	private readonly AutoRetainerIPC? autoRetainerIPC;

	private Hook<LobbyErrorHandlerDelegate>? lobbyErrorHandlerHook;

	private LobbyErrorHandlerDelegate? originalLobbyErrorHandler;

	private int disposed;

	private string? lastKnownCharacter;

	private bool isRelogging;

	private DateTime lastDialogClickTime = DateTime.MinValue;

	public bool IsErrorDisconnect { get; private set; }

	public string? LastDisconnectedCharacter { get; private set; }

	public ErrorRecoveryService(IPluginLog log, IGameInteropProvider hookProvider, IClientState clientState, IFramework framework, IGameGui gameGui, AutoRetainerIPC? autoRetainerIPC = null)
	{
		this.log = log;
		this.hookProvider = hookProvider;
		this.clientState = clientState;
		this.framework = framework;
		this.gameGui = gameGui;
		this.autoRetainerIPC = autoRetainerIPC;
		framework.Update += OnFrameworkUpdate;
		InitializeHook();
	}

	private void InitializeHook()
	{
		try
		{
			lobbyErrorHandlerHook = hookProvider.HookFromSignature<LobbyErrorHandlerDelegate>("40 53 48 83 EC 30 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 8B D0", LobbyErrorHandlerDetour);
			if (lobbyErrorHandlerHook != null && lobbyErrorHandlerHook.Address != IntPtr.Zero)
			{
				originalLobbyErrorHandler = lobbyErrorHandlerHook.Original;
				lobbyErrorHandlerHook.Enable();
			}
		}
		catch (Exception)
		{
		}
	}

	private char LobbyErrorHandlerDetour(long a1, long a2, long a3)
	{
		try
		{
			nint num = new IntPtr(a3);
			byte b = Marshal.ReadByte(num);
			int num2 = (((b & 0xF) > 0) ? Marshal.ReadInt32(num + 8) : 0);
			_ = 0;
			if (num2 != 0)
			{
				try
				{
					if (autoRetainerIPC != null)
					{
						string currentCharacter = autoRetainerIPC.GetCurrentCharacter();
						if (!string.IsNullOrEmpty(currentCharacter))
						{
							LastDisconnectedCharacter = currentCharacter;
						}
						else if (!string.IsNullOrEmpty(lastKnownCharacter))
						{
							LastDisconnectedCharacter = lastKnownCharacter;
						}
					}
				}
				catch (Exception)
				{
					if (!string.IsNullOrEmpty(lastKnownCharacter))
					{
						LastDisconnectedCharacter = lastKnownCharacter;
					}
				}
				Marshal.WriteInt64(num + 8, 16000L);
				IsErrorDisconnect = true;
				if ((b & 0xF) > 0)
				{
					Marshal.ReadInt32(num + 8);
				}
				else
					_ = 0;
			}
		}
		catch (Exception)
		{
		}
		return originalLobbyErrorHandler?.Invoke(a1, a2, a3) ?? '\0';
	}

	private unsafe void OnFrameworkUpdate(IFramework framework)
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return;
		}
		if (autoRetainerIPC != null && Plugin.ObjectTable.LocalPlayer != null)
		{
			try
			{
				string currentCharacter = autoRetainerIPC.GetCurrentCharacter();
				if (!string.IsNullOrEmpty(currentCharacter))
				{
					lastKnownCharacter = currentCharacter;
				}
			}
			catch
			{
			}
		}
		try
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName("Dialogue");
			if (addonByName == IntPtr.Zero)
			{
				return;
			}
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr == null || !ptr->IsVisible || (DateTime.Now - lastDialogClickTime).TotalMilliseconds < 1000.0)
			{
				return;
			}
			AtkTextNode* textNodeById = ptr->GetTextNodeById(3u);
			if (textNodeById == null)
			{
				return;
			}
			string text = textNodeById->NodeText.ToString();
			if (string.IsNullOrEmpty(text) || (!text.Contains("server", StringComparison.OrdinalIgnoreCase) && !text.Contains("connection", StringComparison.OrdinalIgnoreCase) && !text.Contains("error", StringComparison.OrdinalIgnoreCase) && !text.Contains("lost", StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}
			IsErrorDisconnect = true;
			try
			{
				if (autoRetainerIPC != null)
				{
					string currentCharacter2 = autoRetainerIPC.GetCurrentCharacter();
					if (!string.IsNullOrEmpty(currentCharacter2))
					{
						LastDisconnectedCharacter = currentCharacter2;
					}
					else if (!string.IsNullOrEmpty(lastKnownCharacter))
					{
						LastDisconnectedCharacter = lastKnownCharacter;
					}
				}
			}
			catch
			{
				if (!string.IsNullOrEmpty(lastKnownCharacter))
				{
					LastDisconnectedCharacter = lastKnownCharacter;
				}
			}
			try
			{
				AtkComponentButton* componentButtonById = ptr->GetComponentButtonById(4u);
				if (componentButtonById != null)
				{
					AtkResNode atkResNode = componentButtonById->AtkComponentBase.OwnerNode->AtkResNode;
					AtkEvent* ptr2 = atkResNode.AtkEventManager.Event;
					ptr->ReceiveEvent(ptr2->State.EventType, (int)ptr2->Param, atkResNode.AtkEventManager.Event, null);
					lastDialogClickTime = DateTime.Now;
				}
			}
			catch (Exception)
			{
			}
		}
		catch (Exception)
		{
		}
	}

	public void Reset()
	{
		IsErrorDisconnect = false;
		LastDisconnectedCharacter = null;
	}

	public void RequestRelog()
	{
		if (isRelogging)
		{
			return;
		}
		isRelogging = true;
		Task.Run(async delegate
		{
			try
			{
				int num = 0;
				bool flag = false;
				while (num < 12 && !flag)
				{
					num++;
					if (autoRetainerIPC == null || !autoRetainerIPC.IsAvailable)
					{
						log.Warning($"[ErrorRecovery] AutoRetainer IPC not available (Attempt {num})");
					}
					else if (string.IsNullOrEmpty(LastDisconnectedCharacter))
					{
						log.Warning($"[ErrorRecovery] No character to relog to (Attempt {num})");
						if (!string.IsNullOrEmpty(lastKnownCharacter))
						{
							LastDisconnectedCharacter = lastKnownCharacter;
							log.Information("[ErrorRecovery] Recovered character from cache: " + LastDisconnectedCharacter);
						}
					}
					else
					{
						log.Information($"[ErrorRecovery] Requesting AutoRetainer relog to: {LastDisconnectedCharacter} (Attempt {num})");
						if (autoRetainerIPC.SwitchCharacter(LastDisconnectedCharacter))
						{
							flag = true;
							log.Information("[ErrorRecovery] Relog request accepted");
							break;
						}
						log.Error("[ErrorRecovery] AutoRetainer rejected relog request");
					}
					if (!flag)
					{
						log.Information("[ErrorRecovery] Relog failed, waiting 5s before retry...");
						Thread.Sleep(5000);
					}
				}
				if (!flag)
				{
					log.Error("[ErrorRecovery] Failed to relog after retries");
				}
			}
			finally
			{
				isRelogging = false;
			}
		});
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		framework.Update -= OnFrameworkUpdate;
		Hook<LobbyErrorHandlerDelegate> hook = lobbyErrorHandlerHook;
		lobbyErrorHandlerHook = null;
		if (hook == null)
		{
			originalLobbyErrorHandler = null;
			return;
		}
		try
		{
			hook.Disable();
		}
		finally
		{
			hook.Dispose();
			originalLobbyErrorHandler = null;
		}
	}
}
