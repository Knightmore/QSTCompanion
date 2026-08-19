using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuestionableCompanion.Services;

public class AutoEquipHeadgearService : IDisposable
{
	private const uint TargetItemId = 8567u;

	private const int MaxLevel = 25;

	private const int HeadSlotIndex = 2;

	private static readonly ushort[] ExcludedQuestIds = new ushort[5] { 463, 201, 3852, 448, 449 };

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly IFramework framework;

	private DateTime _lastCheckTime = DateTime.MinValue;

	private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3L);

	public Func<bool>? IsRotationActive { get; set; }

	public Func<bool>? IsEnabled { get; set; }

	public AutoEquipHeadgearService(IPluginLog log, IClientState clientState, IFramework framework)
	{
		this.log = log;
		this.clientState = clientState;
		this.framework = framework;
		framework.Update += OnFrameworkUpdate;
		log.Information("[AutoEquipHeadgear] Service initialized (Item: {0}, MaxLevel: {1})", 8567u, 25);
	}

	private void OnFrameworkUpdate(IFramework fwk)
	{
		if (DateTime.Now - _lastCheckTime < CheckInterval)
		{
			return;
		}
		_lastCheckTime = DateTime.Now;
		try
		{
			CheckAndEquip();
		}
		catch (Exception ex)
		{
			log.Error("[AutoEquipHeadgear] Error: " + ex.Message);
		}
	}

	private unsafe void CheckAndEquip()
	{
		Func<bool>? isEnabled = IsEnabled;
		if (isEnabled == null || !isEnabled() || IsRotationActive == null || !IsRotationActive())
		{
			return;
		}
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer == null || localPlayer.Level > 25)
		{
			return;
		}
		QuestManager* ptr = QuestManager.Instance();
		if (ptr != null)
		{
			ushort[] excludedQuestIds = ExcludedQuestIds;
			foreach (ushort questId in excludedQuestIds)
			{
				if (ptr->IsQuestAccepted(questId))
				{
					return;
				}
			}
		}
		InventoryManager* ptr2 = InventoryManager.Instance();
		if (ptr2 == null)
		{
			return;
		}
		InventoryContainer* inventoryContainer = ptr2->GetInventoryContainer(InventoryType.EquippedItems);
		if (inventoryContainer == null)
		{
			return;
		}
		InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(2);
		if (inventorySlot == null || inventorySlot->ItemId == 8567)
		{
			return;
		}
		InventoryType[] array = new InventoryType[5]
		{
			InventoryType.Inventory1,
			InventoryType.Inventory2,
			InventoryType.Inventory3,
			InventoryType.Inventory4,
			InventoryType.ArmoryHead
		};
		foreach (InventoryType inventoryType in array)
		{
			InventoryContainer* inventoryContainer2 = ptr2->GetInventoryContainer(inventoryType);
			if (inventoryContainer2 == null)
			{
				continue;
			}
			for (int j = 0; j < inventoryContainer2->Size; j++)
			{
				InventoryItem* inventorySlot2 = inventoryContainer2->GetInventorySlot(j);
				if (inventorySlot2 != null && inventorySlot2->ItemId != 0 && inventorySlot2->ItemId == 8567)
				{
					log.Information($"[AutoEquipHeadgear] Equipping item {8567u} from {inventoryType} slot {j} â†’ Head slot (Level: {localPlayer.Level})");
					ptr2->MoveItemSlot(inventoryType, (ushort)j, InventoryType.EquippedItems, 2, a6: true);
					return;
				}
			}
		}
	}

	public void Dispose()
	{
		framework.Update -= OnFrameworkUpdate;
		log.Information("[AutoEquipHeadgear] Service disposed");
	}
}
