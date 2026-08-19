using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace QuestionableCompanion.Services;

public class DeathHandlerService : IDisposable
{
	private readonly ICondition condition;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly Configuration config;

	private readonly IGameGui gameGui;

	private readonly IDataManager dataManager;

	private readonly IObjectTable objectTable;

	private bool wasDead;

	private Vector3 deathPosition = Vector3.Zero;

	private uint deathTerritoryId;

	private DateTime deathTime = DateTime.MinValue;

	private bool hasClickedYesNo;

	private bool needsTeleportBack;

	private bool isRotationActive;

	private bool suppressedByHuntLogs;

	public bool IsDead { get; private set; }

	public TimeSpan TimeSinceDeath
	{
		get
		{
			if (!IsDead || !(deathTime != DateTime.MinValue))
			{
				return TimeSpan.Zero;
			}
			return DateTime.Now - deathTime;
		}
	}

	public DeathHandlerService(ICondition condition, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, Configuration config, IGameGui gameGui, IDataManager dataManager, IObjectTable objectTable)
	{
		this.condition = condition;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.config = config;
		this.gameGui = gameGui;
		this.dataManager = dataManager;
		this.objectTable = objectTable;
		log.Information("[DeathHandler] Service initialized");
	}

	public void SetRotationActive(bool active)
	{
		isRotationActive = active;
	}

	public void SetSuppressedByHuntLogs(bool suppressed)
	{
		if (suppressedByHuntLogs != suppressed)
		{
			suppressedByHuntLogs = suppressed;
			if (suppressed)
			{
				ClearState();
				log.Information("[DeathHandler] Suppressed while hunt-log automation is running");
			}
			else
			{
				log.Information("[DeathHandler] Hunt-log suppression cleared");
			}
		}
	}

	public unsafe void Update()
	{
		if (objectTable.LocalPlayer == null || !clientState.IsLoggedIn || !config.EnableDeathHandling || isRotationActive || suppressedByHuntLogs)
		{
			return;
		}
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		uint currentHp = localPlayer.CurrentHp;
		if (currentHp == 0 && !wasDead)
		{
			IsDead = true;
			wasDead = true;
			deathTime = DateTime.Now;
			deathPosition = localPlayer.Position;
			deathTerritoryId = clientState.TerritoryType;
			hasClickedYesNo = false;
			needsTeleportBack = false;
			string territoryName = GetTerritoryName(deathTerritoryId);
			log.Warning("[DeathHandler] ========================================");
			log.Warning("[DeathHandler] === PLAYER DIED (0% HP) ===");
			log.Warning("[DeathHandler] ========================================");
			log.Information($"[DeathHandler] Death Position: {deathPosition}");
			log.Information($"[DeathHandler] Death Territory: {territoryName} ({deathTerritoryId})");
		}
		if (IsDead && !hasClickedYesNo)
		{
			try
			{
				AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectYesno");
				if (addonByName != IntPtr.Zero)
				{
					log.Information("[DeathHandler] SelectYesNo addon detected - clicking YES");
					AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
					if (ptr != null && ptr->IsVisible)
					{
						AtkValue* ptr2 = stackalloc AtkValue[1];
						*ptr2 = default(AtkValue);
						ptr2->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
						ptr2->Int = 0;
						ptr->FireCallback(1u, ptr2);
						hasClickedYesNo = true;
						log.Information("[DeathHandler] ✓ SelectYesNo callback fired (YES)");
						log.Information("[DeathHandler] Waiting for respawn...");
					}
				}
			}
			catch (Exception ex)
			{
				log.Error("[DeathHandler] Error clicking SelectYesNo: " + ex.Message);
			}
		}
		if (wasDead && currentHp != 0 && !localPlayer.IsDead)
		{
			log.Information("[DeathHandler] ========================================");
			log.Information("[DeathHandler] === PLAYER RESPAWNED ===");
			log.Information("[DeathHandler] ========================================");
			IsDead = false;
			wasDead = false;
			needsTeleportBack = true;
			log.Information("[DeathHandler] Preparing to teleport back to death location...");
		}
		if (needsTeleportBack && !IsDead && currentHp != 0 && (DateTime.Now - deathTime).TotalSeconds >= (double)config.DeathRespawnDelay)
		{
			TeleportBackToDeathLocation();
			needsTeleportBack = false;
		}
	}

	private string GetTerritoryName(uint territoryId)
	{
		try
		{
			ExcelSheet<TerritoryType> excelSheet = dataManager.GetExcelSheet<TerritoryType>();
			if (excelSheet == null)
			{
				return territoryId.ToString();
			}
			if (!excelSheet.HasRow(territoryId))
			{
				return territoryId.ToString();
			}
			RowRef<PlaceName> placeName = excelSheet.GetRow(territoryId).PlaceName;
			if (placeName.RowId == 0)
			{
				return territoryId.ToString();
			}
			PlaceName? valueNullable = placeName.ValueNullable;
			if (!valueNullable.HasValue)
			{
				return territoryId.ToString();
			}
			string text = valueNullable.Value.Name.ExtractText();
			if (!string.IsNullOrEmpty(text))
			{
				return text;
			}
		}
		catch (Exception ex)
		{
			log.Error("[DeathHandler] Error getting territory name: " + ex.Message);
		}
		return territoryId.ToString();
	}

	private void TeleportBackToDeathLocation()
	{
		try
		{
			string territoryName = GetTerritoryName(deathTerritoryId);
			log.Information("[DeathHandler] ========================================");
			log.Information("[DeathHandler] === TELEPORTING BACK TO DEATH LOCATION ===");
			log.Information("[DeathHandler] ========================================");
			log.Information($"[DeathHandler] Target Territory: {territoryName} ({deathTerritoryId})");
			log.Information($"[DeathHandler] Target Position: {deathPosition}");
			if (clientState.TerritoryType == deathTerritoryId)
			{
				log.Information("[DeathHandler] Already in death territory - using /li to teleport to position");
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						string text = $"/li {deathPosition.X:F2}, {deathPosition.Y:F2}, {deathPosition.Z:F2}";
						commandManager.ProcessCommand(text);
						log.Information("[DeathHandler] ✓ Teleport to position: " + text);
					}
					catch (Exception ex2)
					{
						log.Error("[DeathHandler] Failed to send teleport command: " + ex2.Message);
					}
				}).Wait();
			}
			else
			{
				log.Information("[DeathHandler] Different territory - teleporting to death territory");
				framework.RunOnFrameworkThread(delegate
				{
					try
					{
						string text = "/li " + territoryName;
						commandManager.ProcessCommand(text);
						log.Information("[DeathHandler] ✓ Teleport to territory: " + text);
					}
					catch (Exception ex2)
					{
						log.Error("[DeathHandler] Failed to send teleport command: " + ex2.Message);
					}
				}).Wait();
			}
			deathPosition = Vector3.Zero;
			deathTerritoryId = 0u;
			deathTime = DateTime.MinValue;
			log.Information("[DeathHandler] Death handling complete");
		}
		catch (Exception ex)
		{
			log.Error("[DeathHandler] Error during teleport: " + ex.Message);
		}
	}

	public void Reset()
	{
		ClearState();
		log.Information("[DeathHandler] State reset");
	}

	private void ClearState()
	{
		IsDead = false;
		wasDead = false;
		needsTeleportBack = false;
		hasClickedYesNo = false;
		deathPosition = Vector3.Zero;
		deathTerritoryId = 0u;
		deathTime = DateTime.MinValue;
	}

	public void Dispose()
	{
	}
}
