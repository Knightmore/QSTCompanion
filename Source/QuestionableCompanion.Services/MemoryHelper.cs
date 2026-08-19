using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace QuestionableCompanion.Services;

public class MemoryHelper : IDisposable
{
	public unsafe delegate void RidePillionDelegate(BattleChara* target, int seatIndex);

	private readonly IPluginLog log;

	private Hook<RidePillionDelegate>? ridePillionHook;

	private int disposed;

	private const string RidePillionSignature = "48 85 C9 0F 84 ?? ?? ?? ?? 48 89 6C 24 ?? 56 48 83 EC";

	public RidePillionDelegate? RidePillion { get; private set; }

	public unsafe MemoryHelper(IPluginLog log, IGameInteropProvider gameInterop)
	{
		this.log = log;
		try
		{
			ridePillionHook = gameInterop.HookFromSignature<RidePillionDelegate>("48 85 C9 0F 84 ?? ?? ?? ?? 48 89 6C 24 ?? 56 48 83 EC", RidePillionDetour);
			if (ridePillionHook != null && ridePillionHook.Address != IntPtr.Zero)
			{
				log.Information($"[MemoryHelper] RidePillion function found at 0x{ridePillionHook.Address:X}");
				RidePillion = ridePillionHook.Original;
				log.Information("[MemoryHelper] RidePillion function initialized successfully");
			}
			else
			{
				log.Warning("[MemoryHelper] RidePillion function not found - will fall back to commands");
			}
		}
		catch (Exception ex)
		{
			log.Error("[MemoryHelper] Error initializing RidePillion: " + ex.Message);
		}
	}

	private unsafe void RidePillionDetour(BattleChara* target, int seatIndex)
	{
		ridePillionHook?.Original(target, seatIndex);
	}

	public unsafe bool ExecuteRidePillion(BattleChara* target, int seatIndex = 10)
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return false;
		}
		if (RidePillion == null)
		{
			log.Warning("[MemoryHelper] RidePillion function not available");
			return false;
		}
		if (target == null)
		{
			log.Error("[MemoryHelper] RidePillion target is null");
			return false;
		}
		try
		{
			log.Information($"[MemoryHelper] Executing RidePillion on target (seat {seatIndex})");
			RidePillion(target, seatIndex);
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[MemoryHelper] RidePillion execution error: " + ex.Message);
			return false;
		}
	}

	public unsafe bool SendChatMessage(string message)
	{
		if (Volatile.Read(in disposed) != 0)
		{
			return false;
		}
		try
		{
			UIModule* ptr = UIModule.Instance();
			if (ptr == null)
			{
				log.Error("[MemoryHelper] UIModule is null!");
				return false;
			}
			byte[] bytes = Encoding.UTF8.GetBytes(message);
			nint num = Marshal.AllocHGlobal(400);
			nint num2 = Marshal.AllocHGlobal(bytes.Length + 30);
			try
			{
				Marshal.Copy(bytes, 0, num2, bytes.Length);
				Marshal.WriteByte(num2 + bytes.Length, 0);
				Marshal.WriteInt64(num, ((IntPtr)num2).ToInt64());
				Marshal.WriteInt64(num + 8, 64L);
				Marshal.WriteInt64(num + 8 + 8, bytes.Length + 1);
				Marshal.WriteInt64(num + 8 + 8 + 8, 0L);
				ptr->ProcessChatBoxEntry((Utf8String*)num);
				log.Information("[MemoryHelper] Chat message sent: " + message);
				return true;
			}
			finally
			{
				Marshal.FreeHGlobal(num);
				Marshal.FreeHGlobal(num2);
			}
		}
		catch (Exception ex)
		{
			log.Error("[MemoryHelper] SendChatMessage error: " + ex.Message);
			return false;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		RidePillion = null;
		Hook<RidePillionDelegate> hook = ridePillionHook;
		ridePillionHook = null;
		if (hook == null)
		{
			return;
		}
		try
		{
			hook.Disable();
		}
		finally
		{
			hook.Dispose();
		}
	}
}
