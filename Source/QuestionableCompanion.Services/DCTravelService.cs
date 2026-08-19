using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuestionableCompanion.Services;

public class DCTravelService : IDisposable
{
	private readonly IPluginLog log;

	private readonly Configuration config;

	private readonly IClientState clientState;

	private readonly LifestreamIPC lifestreamIPC;

	private readonly QuestionableIPC questionableIPC;

	private readonly CharacterSafeWaitService safeWaitService;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly IObjectTable objectTable;

	private readonly IPlayerState playerState;

	private bool dcTravelCompleted;

	private bool dcTravelInProgress;

	public static readonly List<uint> RequiredQuests = new List<uint> { 108u, 109u, 85u, 123u, 124u, 568u, 569u, 570u };

	public unsafe bool HasUnlockedDCTravel()
	{
		if (QuestManager.Instance() == null)
		{
			return false;
		}
		foreach (uint requiredQuest in RequiredQuests)
		{
			if (QuestManager.IsQuestComplete(requiredQuest))
			{
				return true;
			}
		}
		return false;
	}

	public DCTravelService(IPluginLog log, Configuration config, LifestreamIPC lifestreamIPC, QuestionableIPC questionableIPC, CharacterSafeWaitService safeWaitService, IClientState clientState, ICommandManager commandManager, IFramework framework, IObjectTable objectTable, IPlayerState playerState)
	{
		this.log = log;
		this.config = config;
		this.lifestreamIPC = lifestreamIPC;
		this.questionableIPC = questionableIPC;
		this.safeWaitService = safeWaitService;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.objectTable = objectTable;
		this.playerState = playerState;
	}

	public bool ShouldPerformDCTravel()
	{
		log.Information("[DCTravel] ========================================");
		log.Information("[DCTravel] === DC TRAVEL CHECK START ===");
		log.Information("[DCTravel] ========================================");
		log.Information($"[DCTravel] Config.EnableDCTravel: {config.EnableDCTravel}");
		log.Information("[DCTravel] Config.DCTravelWorld: '" + config.DCTravelWorld + "'");
		log.Information($"[DCTravel] State.dcTravelCompleted: {dcTravelCompleted}");
		log.Information($"[DCTravel] State.dcTravelInProgress: {dcTravelInProgress}");
		if (!config.EnableDCTravel)
		{
			log.Warning("[DCTravel] SKIP: DC Travel is DISABLED in config");
			return false;
		}
		if (string.IsNullOrEmpty(config.DCTravelWorld))
		{
			log.Warning("[DCTravel] SKIP: No target world configured");
			return false;
		}
		if (!HasUnlockedDCTravel())
		{
			log.Warning("[DCTravel] SKIP: Required quests for DC Travel not completed.");
			return false;
		}
		if (objectTable.LocalPlayer == null)
		{
			log.Error("[DCTravel] SKIP: LocalPlayer is NULL");
			return false;
		}
		string text = playerState.CurrentWorld.Value.Name.ToString();
		log.Information("[DCTravel] Current World: '" + text + "'");
		log.Information("[DCTravel] Target World: '" + config.DCTravelWorld + "'");
		if (text.Equals(config.DCTravelWorld, StringComparison.OrdinalIgnoreCase))
		{
			if (!dcTravelCompleted)
			{
				log.Information("[DCTravel] Character is already on target world - marking as completed");
				dcTravelCompleted = true;
			}
			log.Warning("[DCTravel] SKIP: Already on target world '" + config.DCTravelWorld + "'");
			return false;
		}
		if (dcTravelCompleted)
		{
			log.Warning("[DCTravel] State says completed but character is NOT on target world!");
			log.Warning("[DCTravel] Resetting state - will perform DC Travel");
			dcTravelCompleted = false;
		}
		if (dcTravelInProgress)
		{
			log.Warning("[DCTravel] SKIP: Travel already in progress");
			return false;
		}
		log.Information("[DCTravel] ========================================");
		log.Information("[DCTravel] DC TRAVEL WILL BE PERFORMED!");
		log.Information("[DCTravel] ========================================");
		return true;
	}

	public bool IsLifestreamBusy()
	{
		return lifestreamIPC.IsBusy();
	}

	public async Task<bool> PerformDCTravel()
	{
		if (dcTravelInProgress)
		{
			log.Warning("[DCTravel] DC Travel already in progress");
			return false;
		}
		if (string.IsNullOrEmpty(config.DCTravelWorld))
		{
			log.Error("[DCTravel] No target world configured");
			return false;
		}
		log.Information("[DCTravel] ========================================");
		log.Information("[DCTravel] === CHECKING LIFESTREAM AVAILABILITY ===");
		log.Information("[DCTravel] ========================================");
		log.Information($"[DCTravel] lifestreamIPC.IsAvailable (cached): {lifestreamIPC.IsAvailable}");
		bool flag = lifestreamIPC.ForceCheckAvailability();
		log.Information($"[DCTravel] lifestreamIPC.ForceCheckAvailability() result: {flag}");
		if (!flag)
		{
			log.Error("[DCTravel] ========================================");
			log.Error("[DCTravel] ======= LIFESTREAM NOT AVAILABLE! ======");
			log.Error("[DCTravel] ========================================");
			log.Error("[DCTravel] Possible reasons:");
			log.Error("[DCTravel] 1. Lifestream plugin is not installed");
			log.Error("[DCTravel] 2. Lifestream plugin is not enabled");
			log.Error("[DCTravel] 3. Lifestream plugin failed to load");
			log.Error("[DCTravel] 4. IPC communication error");
			log.Error("[DCTravel] ========================================");
			return false;
		}
		log.Information("[DCTravel] Lifestream is available!");
		dcTravelInProgress = true;
		try
		{
			log.Information("[DCTravel] === INITIATING DATA CENTER TRAVEL ===");
			log.Information("[DCTravel] Target World: " + config.DCTravelWorld);
			log.Information("[DCTravel] ========================================");
			log.Information("[DCTravel] === STOPPING QUESTIONABLE ===");
			log.Information("[DCTravel] ========================================");
			log.Information($"[DCTravel] Questionable IsRunning: {questionableIPC.IsRunning()}");
			try
			{
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst stop");
					Thread.Sleep(1000);
				});
				log.Information("[DCTravel] /qst stop command sent on Framework Thread");
				log.Information($"[DCTravel] Questionable IsRunning after stop: {questionableIPC.IsRunning()}");
			}
			catch (Exception ex)
			{
				log.Error("[DCTravel] Error stopping Questionable: " + ex.Message);
			}
			log.Information("[DCTravel] Initiating travel to " + config.DCTravelWorld + "...");
			log.Information("[DCTravel] Checking Lifestream status before travel...");
			log.Information($"[DCTravel] Lifestream.IsAvailable: {lifestreamIPC.IsAvailable}");
			log.Information($"[DCTravel] Lifestream.IsBusy(): {lifestreamIPC.IsBusy()}");
			if (lifestreamIPC.IsBusy())
			{
				log.Error("[DCTravel] Lifestream is BUSY! Cannot start travel!");
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst start");
				}).Wait();
				log.Information("[DCTravel] /qst start command sent on Framework Thread (recovery)");
				dcTravelInProgress = false;
				return false;
			}
			log.Information("[DCTravel] Sending /li " + config.DCTravelWorld + " command on Framework Thread...");
			bool commandSuccess = false;
			try
			{
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						commandManager.ProcessCommand("/li " + config.DCTravelWorld);
						commandSuccess = true;
						log.Information("[DCTravel] /li " + config.DCTravelWorld + " command executed on Framework Thread");
					}
					catch (Exception ex5)
					{
						log.Error("[DCTravel] Failed to execute /li command: " + ex5.Message);
						commandSuccess = false;
					}
				}).Wait();
				if (!commandSuccess)
				{
					log.Error("[DCTravel] Failed to send /li command");
					framework.RunOnFrameworkThread(delegate
					{
						commandManager.ProcessCommand("/qst start");
					}).Wait();
					log.Information("[DCTravel] /qst start command sent (recovery)");
					dcTravelInProgress = false;
					return false;
				}
				Thread.Sleep(1000);
				bool flag2 = lifestreamIPC.IsBusy();
				log.Information($"[DCTravel] Lifestream.IsBusy() after command: {flag2}");
				if (!flag2)
				{
					log.Warning("[DCTravel] Lifestream did not become busy after command!");
					log.Warning("[DCTravel] Travel may not have started - check Lifestream manually");
				}
			}
			catch (Exception ex2)
			{
				log.Error("[DCTravel] Error sending /li command: " + ex2.Message);
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst start");
				}).Wait();
				log.Information("[DCTravel] /qst start command sent (recovery)");
				dcTravelInProgress = false;
				return false;
			}
			log.Information("[DCTravel] Waiting for travel completion...");
			if (!(await WaitForTravelCompletion(120)))
			{
				log.Warning("[DCTravel] Travel timeout - proceeding anyway");
			}
			log.Information("[DCTravel] ========================================");
			log.Information("[DCTravel] === RESUMING QUESTIONABLE ===");
			log.Information("[DCTravel] ========================================");
			try
			{
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst start");
				}).Wait();
				log.Information("[DCTravel] /qst start command sent on Framework Thread");
				Thread.Sleep(1000);
				log.Information($"[DCTravel] Questionable IsRunning after start: {questionableIPC.IsRunning()}");
			}
			catch (Exception ex3)
			{
				log.Error("[DCTravel] Error starting Questionable: " + ex3.Message);
			}
			dcTravelCompleted = true;
			dcTravelInProgress = false;
			log.Information("[DCTravel] === DATA CENTER TRAVEL COMPLETE ===");
			return true;
		}
		catch (Exception ex4)
		{
			log.Error("[DCTravel] Error during DC travel: " + ex4.Message);
			try
			{
				framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst start");
				}).Wait();
				log.Information("[DCTravel] /qst start command sent on Framework Thread (error recovery)");
			}
			catch
			{
			}
			dcTravelInProgress = false;
			return false;
		}
	}

	private async Task<bool> WaitForTravelCompletion(int timeoutSeconds)
	{
		DateTime startTime = DateTime.Now;
		TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);
		log.Information("[DCTravel] ========================================");
		log.Information("[DCTravel] === WAITING FOR TRAVEL TO START ===");
		log.Information("[DCTravel] ========================================");
		DateTime phase1Start = DateTime.Now;
		TimeSpan phase1Timeout = TimeSpan.FromSeconds(30L);
		bool travelStarted = false;
		log.Information("[DCTravel] Waiting 5 seconds for Lifestream to queue travel tasks...");
		await Task.Delay(5000);
		while (DateTime.Now - phase1Start < phase1Timeout)
		{
			try
			{
				if (lifestreamIPC.IsBusy())
				{
					log.Information("[DCTravel] Lifestream travel has STARTED!");
					travelStarted = true;
					break;
				}
				double totalSeconds = (DateTime.Now - phase1Start).TotalSeconds;
				if (totalSeconds % 5.0 < 0.5)
				{
					log.Information($"[DCTravel] Waiting for travel to start... ({totalSeconds:F1}s)");
				}
			}
			catch (Exception ex)
			{
				log.Debug("[DCTravel] Error checking Lifestream status: " + ex.Message);
			}
			await Task.Delay(1000);
		}
		if (!travelStarted)
		{
			log.Error("[DCTravel] ❌ Travel did not start within 30 seconds!");
			return false;
		}
		log.Information("[DCTravel] ========================================");
		log.Information("[DCTravel] === WAITING FOR TRAVEL TO COMPLETE ===");
		log.Information("[DCTravel] ========================================");
		while (DateTime.Now - startTime < timeout)
		{
			try
			{
				if (!lifestreamIPC.IsBusy())
				{
					log.Information("[DCTravel] Lifestream is no longer busy!");
					log.Information("[DCTravel] Waiting 5 seconds for character to stabilize...");
					await Task.Delay(5000);
					log.Information("[DCTravel] Travel complete!");
					return true;
				}
				double totalSeconds2 = (DateTime.Now - startTime).TotalSeconds;
				if (totalSeconds2 % 10.0 < 0.5)
				{
					log.Information($"[DCTravel] Lifestream is busy (traveling)... ({totalSeconds2:F0}s)");
				}
			}
			catch (Exception ex2)
			{
				log.Debug("[DCTravel] Error checking Lifestream status: " + ex2.Message);
			}
			await Task.Delay(1000);
		}
		log.Warning($"[DCTravel] Travel timeout after {timeoutSeconds}s");
		return false;
	}

	public async Task<bool> ReturnToHomeworld()
	{
		if (!lifestreamIPC.IsAvailable)
		{
			log.Warning("[DCTravel] Lifestream not available for homeworld return");
			return false;
		}
		if (objectTable.LocalPlayer == null)
		{
			return false;
		}
		string text = playerState.HomeWorld.Value.Name.ToString();
		string value = playerState.CurrentWorld.Value.Name.ToString();
		if (text.Equals(value, StringComparison.OrdinalIgnoreCase))
		{
			log.Information("[DCTravel] Already on homeworld");
			return true;
		}
		log.Information("[DCTravel] Returning to homeworld: " + text);
		bool success = lifestreamIPC.ChangeWorld(text);
		if (success)
		{
			await Task.Delay(2000);
			log.Information("[DCTravel] Homeworld return initiated");
		}
		return success;
	}

	public void ResetDCTravelState()
	{
		dcTravelCompleted = false;
		dcTravelInProgress = false;
		log.Information("[DCTravel] DC Travel state reset");
	}

	public bool IsDCTravelCompleted()
	{
		return dcTravelCompleted;
	}

	public bool IsDCTravelInProgress()
	{
		return dcTravelInProgress;
	}

	public void Dispose()
	{
		log.Information("[DCTravel] Service disposed");
	}
}
