using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace QuestionableCompanion.Services;

public class CharacterSafeWaitService
{
	private readonly IClientState clientState;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICondition condition;

	private readonly IGameGui gameGui;

	public CharacterSafeWaitService(IClientState clientState, IPluginLog log, IFramework framework, ICondition condition, IGameGui gameGui)
	{
		this.clientState = clientState;
		this.log = log;
		this.framework = framework;
		this.condition = condition;
		this.gameGui = gameGui;
	}

	public unsafe Task<bool> WaitForCharacterFullyLoadedAsync(int timeoutSeconds = 5, int checkIntervalMs = 200)
	{
		return Task.Run(delegate
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			log.Information("[CharLoad] Waiting for character to fully load...");
			TimeSpan timeSpan = TimeSpan.FromSeconds(timeoutSeconds);
			while (stopwatch.Elapsed < timeSpan)
			{
				try
				{
					if (Control.GetLocalPlayer() == null || !clientState.IsLoggedIn)
					{
						Thread.Sleep(checkIntervalMs);
					}
					else
					{
						if (!condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && !condition[ConditionFlag.OccupiedInCutSceneEvent])
						{
							stopwatch.Stop();
							log.Information($"[CharLoad] Character fully loaded in {stopwatch.ElapsedMilliseconds}ms!");
							return true;
						}
						Thread.Sleep(checkIntervalMs);
					}
				}
				catch (Exception ex)
				{
					log.Error("[CharLoad] Error during load check: " + ex.Message);
					Thread.Sleep(checkIntervalMs);
				}
			}
			stopwatch.Stop();
			log.Warning($"[CharLoad] Character load timeout after {stopwatch.ElapsedMilliseconds}ms");
			return false;
		});
	}

	public bool WaitForCharacterFullyLoaded(int timeoutSeconds = 5)
	{
		return WaitForCharacterFullyLoadedAsync(timeoutSeconds).GetAwaiter().GetResult();
	}

	public async Task PerformSafeWaitAsync()
	{
		Stopwatch sw = Stopwatch.StartNew();
		log.Information("[SafeWait] Starting character stabilization...");
		try
		{
			if (!(await WaitForCharacterFullyLoadedAsync(120)))
			{
				log.Warning("[SafeWait] Character not fully loaded, proceeding anyway");
			}
			await Task.Delay(250);
			await WaitForMovementEndAsync();
			await Task.Delay(250);
			await WaitForActionsCompleteAsync();
			await Task.Delay(500);
			sw.Stop();
			log.Information($"[SafeWait] Character stabilization complete in {sw.ElapsedMilliseconds}ms");
		}
		catch (Exception ex)
		{
			sw.Stop();
			log.Error($"[SafeWait] Error during safe wait after {sw.ElapsedMilliseconds}ms: {ex.Message}");
		}
	}

	public void PerformSafeWait()
	{
		PerformSafeWaitAsync().GetAwaiter().GetResult();
	}

	private async Task WaitForMovementEndAsync(int maxWaitMs = 5000, int checkIntervalMs = 100)
	{
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < maxWaitMs)
		{
			try
			{
				if (!IsPlayerMoving())
				{
					log.Debug($"[SafeWait] Movement ended after {sw.ElapsedMilliseconds}ms");
					return;
				}
				await Task.Delay(checkIntervalMs);
			}
			catch (Exception ex)
			{
				log.Error("[SafeWait] Error checking movement: " + ex.Message);
				await Task.Delay(checkIntervalMs);
			}
		}
		log.Warning($"[SafeWait] Movement wait timeout after {sw.ElapsedMilliseconds}ms");
	}

	private void WaitForMovementEnd()
	{
		WaitForMovementEndAsync().GetAwaiter().GetResult();
	}

	private async Task WaitForActionsCompleteAsync(int maxWaitMs = 10000, int checkIntervalMs = 100)
	{
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < maxWaitMs)
		{
			try
			{
				if (!IsPlayerInAction())
				{
					log.Debug($"[SafeWait] Actions completed after {sw.ElapsedMilliseconds}ms");
					return;
				}
				await Task.Delay(checkIntervalMs);
			}
			catch (Exception ex)
			{
				log.Error("[SafeWait] Error checking actions: " + ex.Message);
				await Task.Delay(checkIntervalMs);
			}
		}
		log.Warning($"[SafeWait] Action wait timeout after {sw.ElapsedMilliseconds}ms");
	}

	private void WaitForActionsComplete()
	{
		WaitForActionsCompleteAsync().GetAwaiter().GetResult();
	}

	private unsafe bool IsPlayerMoving()
	{
		try
		{
			BattleChara* localPlayer = Control.GetLocalPlayer();
			if (localPlayer == null)
			{
				return false;
			}
			FFXIVClientStructs.FFXIV.Common.Math.Vector3 position = localPlayer->Character.GameObject.Position;
			Thread.Sleep(50);
			localPlayer = Control.GetLocalPlayer();
			if (localPlayer == null)
			{
				return false;
			}
			FFXIVClientStructs.FFXIV.Common.Math.Vector3 position2 = localPlayer->Character.GameObject.Position;
			return System.Numerics.Vector3.Distance(position, position2) > 0.01f;
		}
		catch
		{
			return false;
		}
	}

	private unsafe bool IsPlayerInAction()
	{
		try
		{
			BattleChara* localPlayer = Control.GetLocalPlayer();
			if (localPlayer == null)
			{
				return false;
			}
			if (localPlayer->Character.IsCasting)
			{
				log.Debug("[SafeWait] Player is casting");
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public async Task PerformQuickSafeWaitAsync()
	{
		Stopwatch sw = Stopwatch.StartNew();
		log.Debug("[SafeWait] Performing quick safe wait...");
		try
		{
			await WaitForMovementEndAsync(3000);
			await Task.Delay(1000);
			sw.Stop();
			log.Debug($"[SafeWait] Quick safe wait complete in {sw.ElapsedMilliseconds}ms");
		}
		catch (Exception ex)
		{
			sw.Stop();
			log.Error("[SafeWait] Error during quick safe wait: " + ex.Message);
		}
	}

	public void PerformQuickSafeWait()
	{
		PerformQuickSafeWaitAsync().GetAwaiter().GetResult();
	}
}
