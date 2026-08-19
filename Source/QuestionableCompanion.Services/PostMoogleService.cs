using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Utils;

namespace QuestionableCompanion.Services;

public class PostMoogleService : IDisposable
{
	private enum ProcessState
	{
		Idle,
		NavigatingToMoogle,
		InteractingWithMoogle,
		OpeningLetterList,
		ClickingMail,
		WaitingForViewer,
		TakingItems,
		Deleting,
		ClosingViewer,
		WaitingBeforeNextMail,
		UsingConsumables,
		Completed
	}

	private readonly ICondition condition;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly ICommandManager commandManager;

	private readonly IFramework framework;

	private readonly IGameGui gameGui;

	private readonly ITargetManager targetManager;

	private readonly IObjectTable objectTable;

	private readonly IDataManager dataManager;

	private readonly VNavmeshIPC vnavmesh;

	private readonly LifestreamIPC lifestreamIPC;

	private ProcessState currentState;

	private long stateStartTime;

	private bool hasExecutedAction;

	private bool hasTeleportedToSupportedCity;

	private bool moogleDestinationCommandIssued;

	private long supportedCityArrivalTime;

	private int currentMailIndex;

	private int totalMails;

	private int processedMails;

	private long frameCounter;

	private int navPathfindAttempts;

	private const string LETTER_LIST_ADDON = "LetterList";

	private const string LETTER_VIEWER_ADDON = "LetterViewer";

	private const int MaxOperationFailures = 30;

	private int mailOperationFailures;

	private long lastInteractionTime;

	private long lastConsumableActionTime;

	private Dictionary<string, int> failedConsumables = new Dictionary<string, int>();

	private long letterListVisibleStartTime;

	public bool IsProcessing
	{
		get
		{
			if (currentState != ProcessState.Idle)
			{
				return currentState != ProcessState.Completed;
			}
			return false;
		}
	}

	public PostMoogleService(ICondition condition, IPluginLog log, IClientState clientState, ICommandManager commandManager, IFramework framework, IGameGui gameGui, ITargetManager targetManager, IObjectTable objectTable, IDataManager dataManager, IChatGui chatGui, VNavmeshIPC vnavmesh, LifestreamIPC lifestreamIPC)
	{
		this.condition = condition;
		this.log = log;
		this.clientState = clientState;
		this.commandManager = commandManager;
		this.framework = framework;
		this.gameGui = gameGui;
		this.targetManager = targetManager;
		this.objectTable = objectTable;
		this.dataManager = dataManager;
		this.vnavmesh = vnavmesh;
		this.lifestreamIPC = lifestreamIPC;
		framework.Update += OnFrameworkUpdate;
	}

	public void StartProcessing()
	{
		if (!IsProcessing)
		{
			log.Information("[PostMoogle] Starting Mail Processing Sequence...");
			currentMailIndex = 0;
			totalMails = 0;
			processedMails = 0;
			mailOperationFailures = 0;
			hasTeleportedToSupportedCity = false;
			supportedCityArrivalTime = 0L;
			TransitionToState(ProcessState.NavigatingToMoogle);
		}
	}

	public void StopProcessing()
	{
		if (IsProcessing)
		{
			log.Debug("[PostMoogle] Stopping processing.");
			TransitionToState(ProcessState.Idle);
		}
	}

	public void StartConsumablesOnly()
	{
		if (!IsProcessing)
		{
			log.Information("[PostMoogle] Starting Consumables-Only Processing (no mail check)...");
			failedConsumables.Clear();
			lastConsumableActionTime = Environment.TickCount64;
			TransitionToState(ProcessState.UsingConsumables);
		}
	}

	public unsafe bool HasConsumablesInInventory()
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return false;
		}
		ExcelSheet<Item> excelSheet = dataManager.GetExcelSheet<Item>();
		if (excelSheet == null)
		{
			return false;
		}
		InventoryType[] array = new InventoryType[4]
		{
			InventoryType.Inventory1,
			InventoryType.Inventory2,
			InventoryType.Inventory3,
			InventoryType.Inventory4
		};
		foreach (InventoryType inventoryType in array)
		{
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(inventoryType);
			if (inventoryContainer == null)
			{
				continue;
			}
			for (int j = 0; j < inventoryContainer->Size; j++)
			{
				InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(j);
				if (inventorySlot != null && inventorySlot->ItemId != 0)
				{
					uint rowId = inventorySlot->ItemId % 1000000;
					if (excelSheet.TryGetRow(rowId, out var row) && (row.ItemUICategory.RowId == 81 || row.ItemUICategory.RowId == 61 || row.ItemUICategory.RowId == 94 || row.ItemUICategory.RowId == 63 || (row.Name.ToString().Contains("Coffer", StringComparison.OrdinalIgnoreCase) && !row.Name.ToString().Contains("Weapon", StringComparison.OrdinalIgnoreCase))))
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private unsafe bool IsAetheryteUnlocked(uint aetheryteId)
	{
		try
		{
			Telepo* ptr = Telepo.Instance();
			if (ptr == null)
			{
				log.Error("[PostMoogle] Telepo instance is NULL");
				return false;
			}
			TeleportInfo* first = ptr->TeleportList.First;
			TeleportInfo* last = ptr->TeleportList.Last;
			for (TeleportInfo* ptr2 = first; ptr2 < last; ptr2++)
			{
				if (ptr2->AetheryteId == aetheryteId)
				{
					log.Information($"[PostMoogle] Aetheryte {aetheryteId} is UNLOCKED.");
					return true;
				}
			}
			log.Information($"[PostMoogle] Aetheryte {aetheryteId} is LOCKED/Not Found.");
		}
		catch (Exception ex)
		{
			log.Error("[PostMoogle] Error checking aetheryte lock status: " + ex.Message);
		}
		return false;
	}

	public unsafe bool IsMailNotificationVisible()
	{
		nint num = 0;
		try
		{
			num = (nint)gameGui.GetAddonByName("_DTR");
		}
		catch
		{
			return false;
		}
		if (num == 0)
		{
			return false;
		}
		AtkUnitBase* ptr = (AtkUnitBase*)num;
		if (ptr == null)
		{
			return false;
		}
		if (!ptr->IsVisible)
		{
			return false;
		}
		AtkResNode* nodeById = GetNodeById(ptr, 12u);
		if (nodeById != null && nodeById->IsVisible())
		{
			if (nodeById->Type == NodeType.Text)
			{
				AtkTextNode* ptr2 = (AtkTextNode*)nodeById;
				string text = ptr2->NodeText.ToString();
				if (!string.IsNullOrEmpty(text))
				{
					log.Information("[PostMoogle] Mail Notification Visible. Count: " + text);
					return true;
				}
			}
			return true;
		}
		return false;
	}

	public bool? EnsureInCityOrTeleport()
	{
		try
		{
			log.Information("[PostMoogle] DEBUG: EnsureInCityOrTeleport ENTRY");
			uint territoryType = clientState.TerritoryType;
			log.Information($"[PostMoogle] DEBUG: Current Territory: {territoryType}");
			if (IsSupportedMoogleTerritory(territoryType))
			{
				log.Information("[PostMoogle] DEBUG: In supported starter city zone.");
				return true;
			}
			log.Information("[PostMoogle] Not in a main city - attempting to teleport to Post Moogle location...");
			log.Information("[PostMoogle] DEBUG: Checking Aetherytes...");
			if (moogleDestinationCommandIssued)
			{
				log.Debug("[PostMoogle] Direct Moogle destination command is already in progress; not sending another /li command.");
				return false;
			}
			if (IsAetheryteUnlocked(2u))
			{
				return IssueMoogleDestinationCommand("Mih Khetto") ? new bool?(false) : ((bool?)null);
			}
			if (IsAetheryteUnlocked(9u))
			{
				return IssueMoogleDestinationCommand("Ul'dah Aetheryte") ? new bool?(false) : ((bool?)null);
			}
			if (IsAetheryteUnlocked(8u))
			{
				return IssueMoogleDestinationCommand("Aftcastle") ? new bool?(false) : ((bool?)null);
			}
			log.Information("[PostMoogle] No main city aetherytes unlocked - skipping mail processing for this character.");
			return null;
		}
		catch (Exception ex)
		{
			log.Error("[PostMoogle] CRITICAL ERROR in EnsureInCityOrTeleport: " + ex.Message + " \n " + ex.StackTrace);
			return false;
		}
	}

	private bool IssueMoogleDestinationCommand(string aetheryteName)
	{
		if (moogleDestinationCommandIssued)
		{
			return true;
		}
		string text = "/li " + aetheryteName;
		try
		{
			if (!commandManager.ProcessCommand(text))
			{
				log.Warning("[PostMoogle] Lifestream rejected direct destination command: " + text);
				return false;
			}
			moogleDestinationCommandIssued = true;
			log.Information("[PostMoogle] Direct destination command sent: " + text);
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[PostMoogle] Failed to send direct destination command '" + text + "': " + ex.Message);
			return false;
		}
	}

	private bool IsSupportedMoogleTerritory(uint territory)
	{
		if (territory != 128 && territory != 129 && territory != 130 && territory != 131 && territory != 132)
		{
			return territory == 133;
		}
		return true;
	}

	private bool IsDeliveryMoogleNearby()
	{
		try
		{
			if (objectTable == null)
			{
				log.Error("[PostMoogle] ObjectTable is null in IsDeliveryMoogleNearby");
				return false;
			}
			foreach (IGameObject item in objectTable)
			{
				if (item != null && item.IsValid() && (item.BaseId == 2000214 || item.BaseId == 1001985))
				{
					float num = Vector3.Distance(objectTable.LocalPlayer?.Position ?? Vector3.Zero, item.Position);
					if (num < 20f)
					{
						log.Information($"[PostMoogle] Found Moogle (ID: {item.BaseId}) at distance {num:F1}");
						return true;
					}
				}
			}
		}
		catch (Exception ex)
		{
			log.Error("[PostMoogle] Error in IsDeliveryMoogleNearby: " + ex.Message);
		}
		return false;
	}

	public unsafe void DebugInspect(string addonName)
	{
		log.Information("[PostMoogle] Inspecting Addon: " + addonName);
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName(addonName);
		if (addonByName == IntPtr.Zero)
		{
			log.Error("[PostMoogle] Addon " + addonName + " not found or not visible.");
			return;
		}
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
		AtkUldManager uldManager = ptr->UldManager;
		log.Information($"[PostMoogle] Node Count: {uldManager.NodeListCount}");
		for (int i = 0; i < uldManager.NodeListCount; i++)
		{
			AtkResNode* ptr2 = uldManager.NodeList[i];
			if (ptr2 == null)
			{
				continue;
			}
			string value = "";
			if (ptr2->Type == NodeType.Text || ptr2->Type == NodeType.NineGrid)
			{
				AtkTextNode* ptr3 = (AtkTextNode*)ptr2;
				try
				{
					Utf8String nodeText = ptr3->NodeText;
					byte* ptr4 = nodeText.StringPtr;
					int num = (int)nodeText.BufUsed;
					if (ptr4 != null && (ulong)ptr4 > 65536uL && num > 0 && num < 2000)
					{
						if (num > 256)
						{
							num = 256;
						}
						string text = Encoding.UTF8.GetString(ptr4, num);
						value = "Text: '" + text + "'";
					}
					else
					{
						value = $"Text: (Empty/Unsafe - Ptr: {(ulong)ptr4:X} Len: {num})";
					}
				}
				catch
				{
					value = "Text: (Error)";
				}
			}
			else if (ptr2->Type == (NodeType)6)
			{
				AtkComponentNode* ptr5 = (AtkComponentNode*)ptr2;
				AtkComponentBase* component = ptr5->Component;
				if (component != null)
				{
					AtkUldManager uldManager2 = component->UldManager;
					value = $"Component (Child Nodes: {uldManager2.NodeListCount})";
				}
			}
			log.Information($"[PostMoogle] Node ID: {ptr2->NodeId}, Type: {ptr2->Type}, Visible: {ptr2->IsVisible}, {value}");
		}
	}

	private void OnFrameworkUpdate(IFramework _)
	{
		frameCounter++;
		if (currentState == ProcessState.Idle || currentState == ProcessState.Completed)
		{
			return;
		}
		try
		{
			ProcessCurrentState();
		}
		catch (Exception exception)
		{
			log.Error(exception, "[PostMoogle] Error in state machine");
			TransitionToState(ProcessState.Idle);
		}
	}

	private unsafe void ProcessCurrentState()
	{
		long num = Environment.TickCount64 - stateStartTime;
		switch (currentState)
		{
		case ProcessState.NavigatingToMoogle:
		{
			uint territoryType = clientState.TerritoryType;
			Vector3 zero = Vector3.Zero;
			string text = "";
			switch (territoryType)
			{
			case 128u:
			case 129u:
				text = "/li Aftcastle";
				zero = new Vector3(18.509094f, 44.499996f, 158.98376f);
				break;
			case 130u:
			case 131u:
				text = "/li Ul'dah Aetheryte";
				zero = new Vector3(-24.1f, 10f, -61.57f);
				break;
			case 132u:
			case 133u:
				text = "/li Mih Khetto";
				zero = new Vector3(-54.89f, 6.69f, -148.74f);
				break;
			default:
				if (!hasTeleportedToSupportedCity)
				{
					if (!EnsureInCityOrTeleport().HasValue)
					{
						log.Warning($"[PostMoogle] Cannot reach a supported Post Moogle zone from territory {territoryType}. Stopping mail processing.");
						TransitionToState(ProcessState.Idle);
					}
					else
					{
						hasTeleportedToSupportedCity = true;
						stateStartTime = Environment.TickCount64;
					}
				}
				else if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || lifestreamIPC.IsBusy())
				{
					stateStartTime = Environment.TickCount64;
				}
				else if (num > 30000)
				{
					log.Warning($"[PostMoogle] Still not in a supported Post Moogle zone after teleport attempt (territory {territoryType}). Stopping mail processing.");
					TransitionToState(ProcessState.Idle);
				}
				return;
			}
			if (hasTeleportedToSupportedCity && !hasExecutedAction)
			{
				if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || lifestreamIPC.IsBusy() || objectTable.LocalPlayer == null)
				{
					supportedCityArrivalTime = 0L;
					stateStartTime = Environment.TickCount64;
					break;
				}
				if (supportedCityArrivalTime == 0L)
				{
					supportedCityArrivalTime = Environment.TickCount64;
					log.Information($"[PostMoogle] Arrived in supported city territory {territoryType}. Waiting for zone stability before {text}...");
					break;
				}
				if (Environment.TickCount64 - supportedCityArrivalTime < 5000)
				{
					break;
				}
			}
			if (Vector3.Distance(objectTable.LocalPlayer?.Position ?? Vector3.Zero, zero) < 5f)
			{
				log.Information("[PostMoogle] Arrived at Navigation Target.");
				vnavmesh.StopPathfinding();
				TransitionToState(ProcessState.InteractingWithMoogle);
			}
			else if (num > 30000)
			{
				log.Warning("[PostMoogle] Navigation timed out before reaching the Post Moogle. Stopping mail processing.");
				vnavmesh.StopPathfinding();
				TransitionToState(ProcessState.Idle);
			}
			else if (!hasExecutedAction)
			{
				string text2 = text.Substring(4);
				if (moogleDestinationCommandIssued)
				{
					log.Information("[PostMoogle] Destination command already sent during pre-travel: " + text);
				}
				else if (!IssueMoogleDestinationCommand(text2))
				{
					log.Warning("[PostMoogle] Unable to start direct travel to " + text2 + ". Stopping mail processing.");
					TransitionToState(ProcessState.Idle);
					break;
				}
				hasExecutedAction = true;
				stateStartTime = Environment.TickCount64;
			}
			else if (frameCounter % 30 == 0L && (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || lifestreamIPC.IsBusy()))
			{
				stateStartTime = Environment.TickCount64;
			}
			else
			{
				if (num < 3000 || num <= 3000 || frameCounter % 30 != 0L || navPathfindAttempts >= 2)
				{
					break;
				}
				log.Information($"[PostMoogle] Navigation Attempt {navPathfindAttempts + 1}/2");
				try
				{
					if (vnavmesh != null)
					{
						vnavmesh.PathfindAndMoveTo(zero, fly: false);
						navPathfindAttempts++;
					}
					else
					{
						log.Error("[PostMoogle] vNavmeshIPC is null!");
					}
					break;
				}
				catch (Exception ex)
				{
					log.Error("[PostMoogle] Pathfind failed: " + ex.Message);
					break;
				}
			}
			break;
		}
		case ProcessState.InteractingWithMoogle:
			if (num >= 500)
			{
				if (IsAddonVisible("LetterList"))
				{
					TransitionToState(ProcessState.OpeningLetterList);
				}
				else if (InteractWithMoogle() && num > 5000)
				{
					log.Error("[PostMoogle] Interaction timeout.");
					TransitionToState(ProcessState.Idle);
				}
			}
			break;
		case ProcessState.OpeningLetterList:
			if (!IsAddonVisible("LetterList"))
			{
				if (!IsDeliveryMoogleNearby() && num > 10000)
				{
					log.Error("[PostMoogle] LetterList did not open and no Delivery Moogle is nearby.");
					TransitionToState(ProcessState.Idle);
				}
			}
			else
			{
				if (!IsAddonVisible("LetterList"))
				{
					break;
				}
				AtkUnitBase* ptr2 = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LetterList");
				if (ptr2 == null)
				{
					break;
				}
				if (IsAddonVisible("SelectOk"))
				{
					CloseAddon("SelectOk");
				}
				if (IsAddonVisible("SelectYesno"))
				{
					CloseAddon("SelectYesno");
				}
				if (letterListVisibleStartTime == 0L)
				{
					letterListVisibleStartTime = Environment.TickCount64;
				}
				if (Environment.TickCount64 - letterListVisibleStartTime < 500)
				{
					break;
				}
				totalMails = DetectMailCount(ptr2);
				if (totalMails > 0)
				{
					log.Information($"[PostMoogle] Found {totalMails} mails (LetterList UI). Refreshing List.");
					SendLetterListEvent(0uL, 0, 0, 0, 0);
					currentMailIndex = 0;
					mailOperationFailures = 0;
					TransitionToState(ProcessState.ClickingMail);
				}
				else
				{
					long num2 = 10000L;
					if (Environment.TickCount64 - letterListVisibleStartTime > num2)
					{
						log.Information($"[PostMoogle] No mails found (Count={totalMails}) after {num2}ms.");
						TransitionToState(ProcessState.UsingConsumables);
					}
				}
			}
			break;
		case ProcessState.ClickingMail:
		{
			if (num < 1000)
			{
				break;
			}
			if (IsAddonVisible("LetterEditor"))
			{
				log.Information("[PostMoogle] Found blocking 'LetterEditor' (Reply). Closing...");
				CloseAddon("LetterEditor");
				break;
			}
			if (processedMails >= totalMails || processedMails > 100)
			{
				TransitionToState(ProcessState.UsingConsumables);
				break;
			}
			AtkUnitBasePtr addonByName2 = gameGui.GetAddonByName("LetterList");
			if (addonByName2 == IntPtr.Zero)
			{
				TransitionToState(ProcessState.Idle);
				break;
			}
			AtkUnitBase* letterList = (AtkUnitBase*)(nint)addonByName2;
			if (!ClickMailOneByOne(letterList, 0))
			{
				RetryMailOperationOrSkip("selecting the next letter failed");
			}
			else
			{
				TransitionToState(ProcessState.WaitingForViewer);
			}
			break;
		}
		case ProcessState.WaitingForViewer:
			if (IsAddonVisible("LetterViewer"))
			{
				mailOperationFailures = 0;
				TransitionToState(ProcessState.TakingItems);
			}
			else if (!hasExecutedAction && num > 200)
			{
				if (!SendOpenLetterEvent())
				{
					RetryMailOperationOrSkip("opening the selected letter failed");
				}
				else
				{
					hasExecutedAction = true;
				}
			}
			else if (num > 5000)
			{
				log.Warning("[PostMoogle] Mail viewer open timeout. Retry/Skip.");
				processedMails++;
				TransitionToState(ProcessState.ClickingMail);
			}
			break;
		case ProcessState.TakingItems:
			if (num < 500)
			{
				break;
			}
			if (IsAddonVisible("SelectYesno"))
			{
				FireCallback("SelectYesno", 0);
			}
			else if (!hasExecutedAction)
			{
				if (!SendLetterViewEvent(0uL, 1))
				{
					RetryMailOperationOrSkip("claiming letter attachments failed");
				}
				else
				{
					hasExecutedAction = true;
				}
			}
			else if (!IsLetterTransferBusy() && num > 500)
			{
				mailOperationFailures = 0;
				TransitionToState(ProcessState.Deleting);
			}
			break;
		case ProcessState.Deleting:
		{
			if (num < 500)
			{
				break;
			}
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectYesno");
			if (addonByName != IntPtr.Zero)
			{
				AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
				if (ptr != null && ptr->IsVisible)
				{
					string dialogText = SelectYesnoTextHandler.GetDialogText(ptr);
					if (!string.IsNullOrEmpty(dialogText) && IsDeleteLetterDialog(dialogText))
					{
						if (Environment.TickCount64 % 500 < 50)
						{
							FireCallback("SelectYesno", 0);
							SelectYesnoTextHandler.ClickYesButton(ptr);
						}
						if (num > 5000)
						{
							log.Warning("[PostMoogle] SelectYesno stuck. Force closing.");
							CloseAddon("SelectYesno");
						}
						break;
					}
				}
			}
			if (IsAddonVisible("SelectOk"))
			{
				FireCallback("SelectOk", 0);
			}
			else if (!hasExecutedAction && num > 1000)
			{
				if (!SendLetterViewEvent(0uL, 2))
				{
					RetryMailOperationOrSkip("deleting the current letter failed");
				}
				else
				{
					hasExecutedAction = true;
				}
			}
			else if (num > 4000 && !IsAddonVisible("SelectYesno"))
			{
				TransitionToState(ProcessState.ClosingViewer);
			}
			break;
		}
		case ProcessState.ClosingViewer:
			if (num < 500)
			{
				break;
			}
			if (IsAddonVisible("LetterEditor"))
			{
				CloseAddon("LetterEditor");
			}
			if (IsAddonVisible("LetterViewer"))
			{
				FireCallback("LetterViewer", -1);
				if (num > 2000)
				{
					CloseAddon("LetterViewer");
				}
			}
			else
			{
				processedMails++;
				TransitionToState(ProcessState.WaitingBeforeNextMail);
			}
			break;
		case ProcessState.WaitingBeforeNextMail:
			if (num >= 500)
			{
				if (IsAddonVisible("SelectOk"))
				{
					FireCallback("SelectOk", 0);
				}
				TransitionToState(ProcessState.ClickingMail);
			}
			break;
		case ProcessState.UsingConsumables:
			if (num < 1000)
			{
				break;
			}
			if (IsAddonVisible("LetterList"))
			{
				failedConsumables.Clear();
				CloseAddon("LetterList");
				lastConsumableActionTime = Environment.TickCount64;
			}
			else if (IsAddonVisible("SelectOk"))
			{
				FireCallback("SelectOk", 0);
			}
			else if (Environment.TickCount64 - lastConsumableActionTime >= 1500)
			{
				if (ScanAndUseConsumables())
				{
					lastConsumableActionTime = Environment.TickCount64;
					break;
				}
				log.Information("[PostMoogle] No more safe consumables found. Mail check complete.");
				TransitionToState(ProcessState.Completed);
			}
			break;
		case ProcessState.Completed:
			break;
		}
	}

	private unsafe int DetectMailCount(AtkUnitBase* addon)
	{
		if (addon == null)
		{
			return -1;
		}
		AtkUldManager uldManager = addon->UldManager;
		if (uldManager.NodeList == null)
		{
			return -1;
		}
		if (uldManager.NodeListCount > 23)
		{
			int num = TryReadMailCount(uldManager.NodeList[23], 23);
			if (num != -1)
			{
				return num;
			}
		}
		int[] array = new int[3] { 3, 5, 8 };
		foreach (int num2 in array)
		{
			if (num2 != 23 && num2 < uldManager.NodeListCount)
			{
				int num3 = TryReadMailCount(uldManager.NodeList[num2], num2);
				if (num3 != -1)
				{
					return num3;
				}
			}
		}
		return -1;
	}

	private unsafe int TryReadMailCount(AtkResNode* node, int index)
	{
		if (node == null)
		{
			return -1;
		}
		int type = (int)node->Type;
		if (type != 3 && type != 4)
		{
			return -1;
		}
		try
		{
			Utf8String nodeText = ((AtkTextNode*)node)->NodeText;
			byte* ptr = nodeText.StringPtr;
			int num = (int)nodeText.BufUsed;
			if (ptr == null || (ulong)ptr <= 65536uL || num <= 0 || num > 1000)
			{
				return -1;
			}
			string text = Encoding.UTF8.GetString(ptr, num).Replace("\0", "").Trim();
			if (!string.IsNullOrEmpty(text))
			{
				Match match = Regex.Match(text, "(\\d+)\\s*/\\s*(\\d+)");
				if (match.Success && int.TryParse(match.Groups[1].Value, out var result) && int.TryParse(match.Groups[2].Value, out var result2))
				{
					if (result2 >= 100)
					{
						return -1;
					}
					return result;
				}
			}
		}
		catch (Exception ex)
		{
			log.Debug($"[PostMoogle] Error reading Node {index}: {ex.Message}");
		}
		return -1;
	}

	private unsafe bool ScanAndUseConsumables()
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return false;
		}
		ExcelSheet<Item> excelSheet = dataManager.GetExcelSheet<Item>();
		if (excelSheet == null)
		{
			return false;
		}
		InventoryType[] array = new InventoryType[4]
		{
			InventoryType.Inventory1,
			InventoryType.Inventory2,
			InventoryType.Inventory3,
			InventoryType.Inventory4
		};
		foreach (InventoryType inventoryType in array)
		{
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(inventoryType);
			if (inventoryContainer == null)
			{
				continue;
			}
			for (int j = 0; j < inventoryContainer->Size; j++)
			{
				InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(j);
				if (inventorySlot == null || inventorySlot->ItemId == 0)
				{
					continue;
				}
				uint num = inventorySlot->ItemId % 1000000;
				if (!excelSheet.TryGetRow(num, out var row))
				{
					continue;
				}
				string key = $"{(int)inventoryType}_{j}";
				if (failedConsumables.TryGetValue(key, out var value) && value >= 5)
				{
					continue;
				}
				bool flag = false;
				if (row.ItemUICategory.RowId == 81 || row.ItemUICategory.RowId == 61 || row.ItemUICategory.RowId == 94 || row.ItemUICategory.RowId == 63 || (row.Name.ToString().Contains("Coffer", StringComparison.OrdinalIgnoreCase) && !row.Name.ToString().Contains("Weapon", StringComparison.OrdinalIgnoreCase)))
				{
					flag = true;
				}
				if (num == 30362)
				{
					flag = false;
				}
				if (!(row.ItemAction.RowId != 0 && flag))
				{
					continue;
				}
				ActionManager* ptr2 = ActionManager.Instance();
				if (ptr2 != null)
				{
					if (ptr2->GetActionStatus(ActionType.Item, num, 3758096384uL, checkRecastActive: true, checkCastingActive: true, null) != 0)
					{
						int value2 = value + 1;
						failedConsumables[key] = value2;
						return true;
					}
					if (ptr2->UseAction(ActionType.Item, num, 3758096384uL, 65535u, ActionManager.UseActionMode.None, 0u, null))
					{
						return true;
					}
					int value3 = value + 1;
					failedConsumables[key] = value3;
					return true;
				}
			}
		}
		return false;
	}

	private unsafe bool InteractWithMoogle()
	{
		if (IsAddonVisible("LetterList"))
		{
			return false;
		}
		if (IsAddonVisible("SelectOk"))
		{
			log.Debug("[PostMoogle] Found SelectOk. Force closing...");
			CloseAddon("SelectOk");
			return true;
		}
		if (IsAddonVisible("SelectYesno"))
		{
			log.Debug("[PostMoogle] Found SelectYesno. Force closing...");
			CloseAddon("SelectYesno");
			return true;
		}
		if (IsAddonVisible("Talk") || IsAddonVisible("SelectString") || IsAddonVisible("SelectIconString"))
		{
			return true;
		}
		if (!IsSupportedMoogleTerritory(clientState.TerritoryType))
		{
			log.Warning($"[PostMoogle] Refusing to interact outside supported Post Moogle territories. Current territory: {clientState.TerritoryType}");
			return false;
		}
		IGameObject gameObject = objectTable.FirstOrDefault((IGameObject o) => o.Name.ToString().Contains("Delivery Moogle", StringComparison.OrdinalIgnoreCase) && o.IsTargetable);
		if (gameObject != null)
		{
			if (targetManager.Target?.Address != gameObject.Address)
			{
				targetManager.Target = gameObject;
			}
			if (Environment.TickCount64 - lastInteractionTime > 2000)
			{
				GameObject* address = (GameObject*)gameObject.Address;
				TargetSystem.Instance()->InteractWithObject(address, checkLineOfSight: false);
				lastInteractionTime = Environment.TickCount64;
				commandManager.ProcessCommand("/at y");
			}
			return true;
		}
		return false;
	}

	private unsafe bool IsAddonVisible(string name)
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName(name);
		if (addonByName == IntPtr.Zero)
		{
			return false;
		}
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
		if (ptr != null)
		{
			return ptr->IsVisible;
		}
		return false;
	}

	private unsafe bool ClickMailOneByOne(AtkUnitBase* letterList, int index)
	{
		return SendLetterListEvent(0uL, 0, index, 0, 1);
	}

	private unsafe AtkResNode* GetNodeById(AtkUnitBase* unitBase, uint id)
	{
		if (unitBase == null)
		{
			return null;
		}
		AtkUldManager uldManager = unitBase->UldManager;
		for (int i = 0; i < uldManager.NodeListCount; i++)
		{
			AtkResNode* ptr = uldManager.NodeList[i];
			if (ptr != null && ptr->NodeId == id)
			{
				return ptr;
			}
		}
		return null;
	}

	private unsafe void FireCallback(string addonName, params object[] args)
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName(addonName);
		if (addonByName == IntPtr.Zero)
		{
			return;
		}
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
		if (ptr == null || !ptr->IsVisible)
		{
			return;
		}
		AtkValue* ptr2 = stackalloc AtkValue[args.Length];
		for (int i = 0; i < args.Length; i++)
		{
			AtkValue* ptr3 = ptr2 + i;
			*ptr3 = default(AtkValue);
			object obj = args[i];
			if (!(obj is int num))
			{
				if (!(obj is bool flag))
				{
					if (!(obj is uint uInt))
					{
						if (obj != null)
						{
							ptr3->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
							ptr3->Int = 0;
						}
					}
					else
					{
						ptr3->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt;
						ptr3->UInt = uInt;
					}
				}
				else
				{
					ptr3->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool;
					ptr3->Byte = (flag ? ((byte)1) : ((byte)0));
				}
			}
			else
			{
				ptr3->Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
				ptr3->Int = num;
			}
		}
		ptr->FireCallback((uint)args.Length, ptr2);
	}

	private bool SendOpenLetterEvent()
	{
		return SendLetterListEvent(1uL, 0, 0, 0u, 0, 0);
	}

	private bool SendLetterListEvent(ulong eventKind, params object[] eventParams)
	{
		return SendMailAgentEvent(AgentId.Letter, eventKind, eventParams);
	}

	private bool SendLetterViewEvent(ulong eventKind, params object[] eventParams)
	{
		return SendMailAgentEvent(AgentId.LetterView, eventKind, eventParams);
	}

	private unsafe bool SendMailAgentEvent(AgentId agentId, ulong eventKind, params object[] eventParams)
	{
		try
		{
			AgentModule* ptr = AgentModule.Instance();
			if (ptr == null)
			{
				return false;
			}
			AgentInterface* agentByInternalId = ptr->GetAgentByInternalId(agentId);
			if (agentByInternalId == null)
			{
				return false;
			}
			AtkValue* ptr2 = stackalloc AtkValue[1];
			*ptr2 = default(AtkValue);
			AtkValue* ptr3 = null;
			if (eventParams.Length != 0)
			{
				ptr3 = stackalloc AtkValue[eventParams.Length];
				for (int i = 0; i < eventParams.Length; i++)
				{
					ptr3[i] = default(AtkValue);
					object obj = eventParams[i];
					if (!(obj is uint uInt))
					{
						if (!(obj is bool flag))
						{
							if (!(obj is int num))
							{
								if (obj == null)
								{
									ptr3[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
									ptr3[i].Int = 0;
								}
								else
								{
									ptr3[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
									ptr3[i].Int = Convert.ToInt32(eventParams[i]);
								}
							}
							else
							{
								ptr3[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
								ptr3[i].Int = num;
							}
						}
						else
						{
							ptr3[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool;
							ptr3[i].Byte = (flag ? ((byte)1) : ((byte)0));
						}
					}
					else
					{
						ptr3[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt;
						ptr3[i].UInt = uInt;
					}
				}
			}
			agentByInternalId->ReceiveEvent(ptr2, ptr3, (uint)eventParams.Length, eventKind);
			return true;
		}
		catch (Exception exception)
		{
			log.Warning(exception, "[PostMoogle] Failed to send mail agent event {EventKind} to {AgentId}.", eventKind, agentId);
			return false;
		}
	}

	private unsafe static bool IsLetterTransferBusy()
	{
		AtkStage* ptr = AtkStage.Instance();
		if (ptr == null)
		{
			return false;
		}
		NumberArrayData* numberArrayData = ptr->GetNumberArrayData(NumberArrayType.Letter);
		if (numberArrayData != null)
		{
			return numberArrayData->IntArray[136] != 0;
		}
		return false;
	}

	private void RetryMailOperationOrSkip(string reason)
	{
		mailOperationFailures++;
		if (mailOperationFailures < 30)
		{
			log.Debug($"[PostMoogle] Mail operation retry {mailOperationFailures}/{30}: {reason}");
		}
		else
		{
			log.Warning("[PostMoogle] Skipping current letter after repeated failures: " + reason);
			mailOperationFailures = 0;
			processedMails++;
			TransitionToState(ProcessState.ClickingMail);
		}
	}

	private unsafe void CloseAddon(string name)
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName(name);
		if (addonByName != IntPtr.Zero)
		{
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr != null)
			{
				ptr->Close(fireCallback: true);
			}
		}
	}

	private bool IsDeleteLetterDialog(string text)
	{
		string[] array = new string[4] { "Delete .* letter\\?", "Brief .* löschen\\?", "supprimer .* courrier\\?", ".*手紙を削除しますか.*" };
		foreach (string pattern in array)
		{
			if (SelectYesnoTextHandler.MatchesPattern(text, pattern, isRegex: true))
			{
				return true;
			}
		}
		return false;
	}

	private void TransitionToState(ProcessState newState)
	{
		currentState = newState;
		stateStartTime = Environment.TickCount64;
		hasExecutedAction = false;
		navPathfindAttempts = 0;
		letterListVisibleStartTime = 0L;
		if (newState != ProcessState.NavigatingToMoogle)
		{
			hasTeleportedToSupportedCity = false;
			moogleDestinationCommandIssued = false;
			supportedCityArrivalTime = 0L;
		}
	}

	public void Dispose()
	{
		framework.Update -= OnFrameworkUpdate;
	}
}
