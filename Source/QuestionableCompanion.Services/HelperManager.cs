using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public class HelperManager : IDisposable
{
	private readonly Configuration configuration;

	private readonly IPluginLog log;

	private readonly ICommandManager commandManager;

	private readonly ICondition condition;

	private readonly IClientState clientState;

	private readonly IFramework framework;

	private readonly PartyInviteService partyInviteService;

	private readonly MultiClientIPC multiClientIPC;

	private readonly CrossProcessIPC crossProcessIPC;

	private readonly PartyInviteAutoAccept partyInviteAutoAccept;

	private readonly MemoryHelper memoryHelper;

	private readonly LANHelperClient? lanHelperClient;

	private LANHelperServer? lanHelperServer;

	private readonly IPartyList partyList;

	private readonly IGameGui gameGui;

	private bool isInDuty;

	private bool helperAutomationOwnedCurrentDuty;

	private int helperAutomationEpoch;

	private List<(string Name, ushort WorldId)> availableHelpers = new List<(string, ushort)>();

	private Dictionary<(string, ushort), bool> helperReadyStatus = new Dictionary<(string, ushort), bool>();

	public bool IsRepairing { get; private set; }

	public HelperManager(Configuration configuration, IPluginLog log, ICommandManager commandManager, ICondition condition, IClientState clientState, IFramework framework, PartyInviteService partyInviteService, MultiClientIPC multiClientIPC, CrossProcessIPC crossProcessIPC, PartyInviteAutoAccept partyInviteAutoAccept, MemoryHelper memoryHelper, LANHelperClient? lanHelperClient, IPartyList partyList, IGameGui gameGui)
	{
		this.configuration = configuration;
		this.log = log;
		this.commandManager = commandManager;
		this.condition = condition;
		this.clientState = clientState;
		this.framework = framework;
		this.partyInviteService = partyInviteService;
		this.multiClientIPC = multiClientIPC;
		this.crossProcessIPC = crossProcessIPC;
		this.memoryHelper = memoryHelper;
		this.lanHelperClient = lanHelperClient;
		this.partyInviteAutoAccept = partyInviteAutoAccept;
		this.partyList = partyList;
		this.gameGui = gameGui;
		condition.ConditionChange += OnConditionChanged;
		multiClientIPC.OnHelperRequested += OnHelperRequested;
		multiClientIPC.OnHelperDismissed += OnHelperDismissed;
		multiClientIPC.OnHelperAvailable += OnHelperAvailable;
		multiClientIPC.OnHelperStatusUpdate += OnHelperStatusUpdate;
		crossProcessIPC.OnHelperRequested += OnHelperRequested;
		crossProcessIPC.OnHelperDismissed += OnHelperDismissed;
		crossProcessIPC.OnHelperAvailable += OnHelperAvailable;
		crossProcessIPC.OnHelperReady += OnHelperReady;
		crossProcessIPC.OnHelperInParty += OnHelperInParty;
		crossProcessIPC.OnHelperInDuty += OnHelperInDuty;
		crossProcessIPC.OnRequestHelperAnnouncements += OnRequestHelperAnnouncements;
		crossProcessIPC.OnPartyInviteRequested += OnPartyInviteRequested;
		crossProcessIPC.OnHelperStatusUpdate += OnHelperStatusUpdate;
		multiClientIPC.OnPartyInviteRequested += OnPartyInviteRequested;
		if (configuration.IsHelperAutomationActive)
		{
			log.Information("[HelperManager] Will announce helper availability on next frame");
		}
		log.Information("[HelperManager] Initialized");
	}

	public void RegisterLANHelperServer(LANHelperServer server)
	{
		if (lanHelperServer != null)
		{
			lanHelperServer.OnPartyInviteRequested -= OnLANPartyInviteRequested;
		}
		lanHelperServer = server;
		lanHelperServer.OnPartyInviteRequested += OnLANPartyInviteRequested;
		log.Information("[HelperManager] Registered LANHelperServer for party invite events");
	}

	private void OnLANPartyInviteRequested(string questerName, ushort questerWorldId)
	{
		framework.RunOnFrameworkThread(delegate
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer != null)
			{
				OnPartyInviteRequested(localPlayer.Name.ToString(), (ushort)localPlayer.HomeWorld.RowId, questerName, questerWorldId);
			}
		});
	}

	public void AnnounceIfHelper()
	{
		if (configuration.IsHelperAutomationActive)
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer == null)
			{
				log.Warning("[HelperManager] LocalPlayer is null, cannot announce helper");
				return;
			}
			string text = localPlayer.Name.ToString();
			ushort num = (ushort)localPlayer.HomeWorld.RowId;
			multiClientIPC.AnnounceHelperAvailable(text, num);
			crossProcessIPC.AnnounceHelper();
			log.Information($"[HelperManager] Announced as helper: {text}@{num} (both IPC systems)");
		}
	}

	public void SetHelperAutomationActive(bool active)
	{
		if (active && !configuration.IsHighLevelHelper)
		{
			log.Warning("[HelperManager] Helper automation cannot be activated because this client is not configured as a High-Level Helper.");
		}
		else
		{
			if (configuration.IsHelperAutomationActive == active)
			{
				return;
			}
			Interlocked.Increment(ref helperAutomationEpoch);
			configuration.HelperAutomationEnabled = active;
			configuration.CurrentHelperStatus = HelperStatus.Available;
			if (!active)
			{
				configuration.AssignedQuester = string.Empty;
				configuration.AssignedQuesterForFollowing = string.Empty;
				helperAutomationOwnedCurrentDuty = false;
			}
			configuration.Save();
			lanHelperServer?.NotifyRoleChanged();
			if (active)
			{
				AnnounceIfHelper();
				Plugin.Instance?.GetChauffeurMode()?.StartHelperStatusBroadcast();
				log.Information("[HelperManager] Helper automation activated.");
				return;
			}
			partyInviteAutoAccept.DisableAutoAccept();
			Plugin.Instance?.GetChauffeurMode()?.OnHelperAutomationDeactivated();
			Plugin.Instance?.GetDungeonAutomation()?.OnHelperAutomationDeactivated();
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer != null)
			{
				BroadcastFormerHelperUnavailable(localPlayer.Name.ToString(), (ushort)localPlayer.HomeWorld.RowId);
			}
			log.Information("[HelperManager] Helper automation deactivated; helper requests and AutoDuty control are disabled.");
		}
	}

	private bool IsHelperAutomationEpochCurrent(int epoch)
	{
		if (configuration.IsHelperAutomationActive)
		{
			return Volatile.Read(in helperAutomationEpoch) == epoch;
		}
		return false;
	}

	public void HandleLocalRoleChanged(bool wasHelper)
	{
		if (wasHelper && !configuration.IsHelperAutomationActive)
		{
			Interlocked.Increment(ref helperAutomationEpoch);
			helperAutomationOwnedCurrentDuty = false;
			partyInviteAutoAccept.DisableAutoAccept();
			Plugin.Instance?.GetChauffeurMode()?.OnHelperAutomationDeactivated();
			Plugin.Instance?.GetDungeonAutomation()?.OnHelperAutomationDeactivated();
		}
		IPlayerCharacter? localPlayer = Plugin.ObjectTable.LocalPlayer;
		string text = localPlayer?.Name.ToString() ?? string.Empty;
		ushort num = (ushort)(localPlayer?.HomeWorld.RowId ?? 0);
		bool flag = wasHelper && !configuration.IsHighLevelHelper && !string.IsNullOrWhiteSpace(text) && num != 0;
		if (flag)
		{
			BroadcastFormerHelperUnavailable(text, num);
			log.Information($"[HelperManager] Removed former helper announcement for {text}@{num}");
		}
		lanHelperServer?.NotifyRoleChanged();
		availableHelpers.Clear();
		helperReadyStatus.Clear();
		if (configuration.IsHelperAutomationActive)
		{
			AnnounceIfHelper();
		}
		else if (configuration.IsQuester)
		{
			if (flag)
			{
				framework.RunOnTick((System.Action)BroadcastRequestHelperAnnouncements, TimeSpan.FromMilliseconds(250L), 0, default(CancellationToken));
			}
			else
			{
				BroadcastRequestHelperAnnouncements();
			}
		}
	}

	private void BroadcastFormerHelperUnavailable(string characterName, ushort worldId)
	{
		SendUnavailable();
		framework.RunOnTick((System.Action)SendUnavailable, TimeSpan.FromMilliseconds(100L), 0, default(CancellationToken));
		framework.RunOnTick((System.Action)SendUnavailable, TimeSpan.FromMilliseconds(500L), 0, default(CancellationToken));
		framework.RunOnTick((System.Action)SendUnavailable, TimeSpan.FromMilliseconds(1500L), 0, default(CancellationToken));
		void SendUnavailable()
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (!configuration.IsHelperAutomationActive && localPlayer != null && string.Equals(localPlayer.Name.ToString(), characterName, StringComparison.OrdinalIgnoreCase) && (ushort)localPlayer.HomeWorld.RowId == worldId)
			{
				multiClientIPC.BroadcastHelperStatus(characterName, worldId, "Unavailable");
				crossProcessIPC.BroadcastHelperStatus(characterName, worldId, "Unavailable");
			}
		}
	}

	public void InviteHelpers()
	{
		if (!configuration.IsQuester)
		{
			log.Debug("[HelperManager] Not a Quester, skipping helper invites");
			return;
		}
		if (configuration.HelperSelection == HelperSelectionMode.ManualInput)
		{
			if (string.IsNullOrEmpty(configuration.ManualHelperName))
			{
				log.Warning("[HelperManager] Manual Input mode selected but no helper name configured!");
				return;
			}
			Task.Run(async delegate
			{
				log.Information("[HelperManager] Manual Input mode: Inviting " + configuration.ManualHelperName);
				string[] array = configuration.ManualHelperName.Split('@');
				if (array.Length != 2)
				{
					log.Error("[HelperManager] Invalid manual helper format: " + configuration.ManualHelperName + " (expected: CharacterName@WorldName)");
				}
				else
				{
					string helperName = array[0].Trim();
					string text = array[1].Trim();
					ushort worldId = 0;
					ExcelSheet<World> excelSheet = Plugin.DataManager.GetExcelSheet<World>();
					if (excelSheet != null)
					{
						foreach (World item in excelSheet)
						{
							if (item.Name.ExtractText().Equals(text, StringComparison.OrdinalIgnoreCase))
							{
								worldId = (ushort)item.RowId;
								break;
							}
						}
					}
					if (worldId == 0)
					{
						log.Error("[HelperManager] Could not find world ID for: " + text);
					}
					else
					{
						log.Information($"[HelperManager] Resolved helper: {helperName}@{worldId} ({text})");
						bool flag = false;
						if (partyList != null)
						{
							foreach (IPartyMember party in partyList)
							{
								if (party.Name.ToString() == helperName && party.World.RowId == worldId)
								{
									flag = true;
									break;
								}
							}
						}
						if (flag)
						{
							log.Information("[HelperManager] helper " + helperName + " is ALREADY in party! Skipping disband/invite.");
						}
						else
						{
							DisbandParty();
							await Task.Delay(500);
							log.Information("[HelperManager] Sending direct invite to " + helperName + " (Manual Input - no IPC wait)");
							if (partyInviteService.InviteToParty(helperName, worldId))
							{
								log.Information("[HelperManager] Successfully invited " + helperName);
							}
							else
							{
								log.Error("[HelperManager] Failed to invite " + helperName);
							}
						}
					}
				}
			});
			return;
		}
		log.Information("[HelperManager] Requesting helper announcements...");
		RequestHelperAnnouncements();
		Task.Run(async delegate
		{
			await Task.Delay(1000);
			List<(string Name, ushort WorldId)> helpersToInvite = new List<(string, ushort)>();
			if (configuration.HelperSelection == HelperSelectionMode.Auto)
			{
				if (availableHelpers.Count == 0)
				{
					log.Warning("[HelperManager] No helpers available via IPC!");
					if (lanHelperClient != null)
					{
						log.Information("[HelperManager] Checking for LAN helpers...");
						LANHelperInfo firstAvailableHelper = lanHelperClient.GetFirstAvailableHelper();
						if (firstAvailableHelper != null)
						{
							log.Information($"[HelperManager] Found LAN helper: {firstAvailableHelper.Name} (World:{firstAvailableHelper.WorldId}) at {firstAvailableHelper.IPAddress}");
							await InviteLANHelper(firstAvailableHelper.IPAddress, firstAvailableHelper.Name, firstAvailableHelper.WorldId);
							return;
						}
					}
					log.Warning("[HelperManager] Make sure helper clients are running with 'I'm a High-Level Helper' enabled");
					return;
				}
				helpersToInvite.AddRange(availableHelpers);
				log.Information($"[HelperManager] Auto mode: Inviting {helpersToInvite.Count} AUTO-DISCOVERED helper(s)...");
			}
			else if (configuration.HelperSelection == HelperSelectionMode.Dropdown)
			{
				if (string.IsNullOrEmpty(configuration.PreferredHelper))
				{
					log.Warning("[HelperManager] Dropdown mode selected but no helper chosen!");
					return;
				}
				string[] array = configuration.PreferredHelper.Split('@');
				if (array.Length != 2)
				{
					log.Error("[HelperManager] Invalid preferred helper format: " + configuration.PreferredHelper);
					return;
				}
				string helperName = array[0].Trim();
				string worldName = array[1].Trim();
				(string, ushort) tuple = availableHelpers.FirstOrDefault<(string, ushort)>(delegate((string Name, ushort WorldId) h)
				{
					ExcelSheet<World> excelSheet = Plugin.DataManager.GetExcelSheet<World>();
					string text4 = "Unknown";
					if (excelSheet != null)
					{
						foreach (World item2 in excelSheet)
						{
							if (item2.RowId == h.WorldId)
							{
								text4 = item2.Name.ExtractText();
								break;
							}
						}
					}
					return h.Name == helperName && text4 == worldName;
				});
				var (text, num) = tuple;
				if (text == null && num == 0)
				{
					log.Warning("[HelperManager] Preferred helper " + configuration.PreferredHelper + " not found in discovered helpers!");
					return;
				}
				helpersToInvite.Add(tuple);
				log.Information("[HelperManager] Dropdown mode: Inviting selected helper " + configuration.PreferredHelper);
			}
			bool flag = false;
			if (partyList != null && partyList.Length > 0 && helpersToInvite.Count > 0)
			{
				int num2 = 0;
				foreach (var (text2, num3) in helpersToInvite)
				{
					foreach (IPartyMember party2 in partyList)
					{
						if (party2.Name.ToString() == text2 && party2.World.RowId == num3)
						{
							num2++;
							break;
						}
					}
				}
				if (num2 >= helpersToInvite.Count)
				{
					flag = true;
				}
			}
			if (flag)
			{
				log.Information("[HelperManager] All desired helpers are ALREADY in party! Skipping disband.");
			}
			else if (partyList != null && partyList.Length > 1)
			{
				bool flag2 = false;
				foreach (var (text3, num4) in helpersToInvite)
				{
					foreach (IPartyMember party3 in partyList)
					{
						if (party3.Name.ToString() == text3 && party3.World.RowId == num4)
						{
							flag2 = true;
							break;
						}
					}
				}
				if (flag2)
				{
					log.Information("[HelperManager] Some helpers already in party - NOT disbanding, simply inviting remaining.");
				}
				else
				{
					DisbandParty();
					await Task.Delay(500);
				}
			}
			else
			{
				DisbandParty();
				await Task.Delay(500);
			}
			foreach (var item3 in helpersToInvite)
			{
				var (name, worldId) = item3;
				if (string.IsNullOrEmpty(name) || worldId == 0)
				{
					log.Warning($"[HelperManager] Invalid helper: {name}@{worldId}");
				}
				else
				{
					log.Information($"[HelperManager] Requesting helper: {name}@{worldId}");
					helperReadyStatus[(name, worldId)] = false;
					multiClientIPC.RequestHelper(name, worldId);
					crossProcessIPC.RequestHelper(name, worldId);
					log.Information("[HelperManager] Waiting for " + name + " to be ready...");
					DateTime timeout = DateTime.Now.AddSeconds(10.0);
					while (!helperReadyStatus.GetValueOrDefault((name, worldId), defaultValue: false) && DateTime.Now < timeout)
					{
						await Task.Delay(100);
					}
					if (!helperReadyStatus.GetValueOrDefault((name, worldId), defaultValue: false))
					{
						log.Warning("[HelperManager] Timeout waiting for " + name + " to be ready!");
					}
					else
					{
						if (configuration.EnableFreeTrialHelperInvite)
						{
							log.Information("[HelperManager] REVERSE INVITE (Free Trial Mode): Requesting " + name + " to invite ME...");
							framework.RunOnFrameworkThread(delegate
							{
								try
								{
									IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
									if (localPlayer != null)
									{
										string text4 = localPlayer.Name.ToString();
										ushort num5 = (ushort)localPlayer.HomeWorld.RowId;
										partyInviteAutoAccept.EnableAutoAccept();
										log.Information("[HelperManager] Quester auto-accept enabled for incoming invite from " + name);
										log.Information($"[HelperManager] Sending REQUEST_PARTY_INVITE: me={text4}@{num5} target={name}@{worldId}");
										multiClientIPC.RequestPartyInvite(name, worldId, text4, num5);
										crossProcessIPC.RequestPartyInvite(name, worldId, text4, num5);
										log.Information("[HelperManager] REQUEST_PARTY_INVITE sent successfully");
									}
									else
									{
										log.Error("[HelperManager] REVERSE INVITE FAILED: LocalPlayer is null!");
									}
								}
								catch (Exception ex)
								{
									log.Error("[HelperManager] REVERSE INVITE EXCEPTION: " + ex.Message);
									log.Error("[HelperManager] Stack: " + ex.StackTrace);
								}
							});
						}
						else
						{
							log.Information("[HelperManager] " + name + " is ready! Sending invite...");
							if (partyInviteService.InviteToParty(name, worldId))
							{
								log.Information("[HelperManager] Successfully invited " + name);
							}
							else
							{
								log.Error("[HelperManager] Failed to invite " + name);
							}
						}
						await Task.Delay(500);
					}
				}
			}
		});
	}

	public void InviteLANHelpers()
	{
		if (!configuration.IsQuester)
		{
			log.Debug("[HelperManager] Not a Quester, skipping LAN helper invites");
			return;
		}
		if (lanHelperClient == null)
		{
			log.Warning("[HelperManager] LAN Helper Client not initialized!");
			return;
		}
		Task.Run(async delegate
		{
			log.Information("[HelperManager] === INVITING ALL LAN HELPERS ===");
			IReadOnlyList<LANHelperInfo> discoveredHelpers = lanHelperClient.DiscoveredHelpers;
			if (discoveredHelpers.Count == 0)
			{
				log.Warning("[HelperManager] No LAN helpers discovered yet.");
			}
			else
			{
				log.Information($"[HelperManager] Found {discoveredHelpers.Count} LAN helpers. Inviting...");
				foreach (LANHelperInfo item in discoveredHelpers)
				{
					log.Information($"[HelperManager] Inviting LAN helper: {item.Name} (World:{item.WorldId})");
					await InviteLANHelper(item.IPAddress, item.Name, item.WorldId);
					await Task.Delay(1000);
				}
			}
		});
	}

	public List<(string Name, ushort WorldId)> GetAvailableHelpers()
	{
		List<(string, ushort)> list = new List<(string, ushort)>(availableHelpers);
		if (lanHelperClient != null)
		{
			foreach (LANHelperInfo lanHelper in lanHelperClient.DiscoveredHelpers)
			{
				LANHelperStatus status = lanHelper.Status;
				bool flag = (uint)(status - 5) <= 1u;
				if (!flag && !list.Any<(string, ushort)>(((string Name, ushort WorldId) h) => h.Name == lanHelper.Name && h.WorldId == lanHelper.WorldId))
				{
					list.Add((lanHelper.Name, lanHelper.WorldId));
				}
			}
		}
		return list;
	}

	private void LeaveParty()
	{
		try
		{
			log.Information("[HelperManager] Leaving party");
			framework.RunOnFrameworkThread(delegate
			{
				memoryHelper.SendChatMessage("/leave");
				log.Information("[HelperManager] /leave command sent via UIModule");
			});
		}
		catch (Exception ex)
		{
			log.Error("[HelperManager] Failed to leave party: " + ex.Message);
		}
	}

	public void DisbandParty()
	{
		try
		{
			log.Information("[HelperManager] Disbanding party");
			framework.RunOnFrameworkThread(delegate
			{
				memoryHelper.SendChatMessage("/leave");
				log.Information("[HelperManager] /leave command sent via UIModule");
			});
			multiClientIPC.DismissHelper();
			crossProcessIPC.DismissHelper();
		}
		catch (Exception ex)
		{
			log.Error("[HelperManager] Failed to disband party: " + ex.Message);
		}
	}

	private void OnConditionChanged(ConditionFlag flag, bool value)
	{
		if (flag == ConditionFlag.BoundByDuty)
		{
			if (value && !isInDuty)
			{
				isInDuty = true;
				OnDutyEnter();
			}
			else if (!value && isInDuty)
			{
				isInDuty = false;
				OnDutyLeave();
			}
		}
	}

	private void OnDutyEnter()
	{
		log.Debug("[HelperManager] Entered duty");
		if (!configuration.IsHelperAutomationActive)
		{
			return;
		}
		int activationEpoch = Volatile.Read(in helperAutomationEpoch);
		helperAutomationOwnedCurrentDuty = true;
		configuration.CurrentHelperStatus = HelperStatus.InDungeon;
		configuration.Save();
		log.Information("[HelperManager] Helper status: InDungeon");
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer != null)
		{
			string helperName = localPlayer.Name.ToString();
			ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
			crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "InDungeon");
		}
		log.Information("[HelperManager] Starting AutoDuty (High-Level Helper)");
		Task.Run(async delegate
		{
			log.Information("[HelperManager] Waiting 5s before starting AutoDuty...");
			await Task.Delay(5000);
			if (!IsHelperAutomationEpochCurrent(activationEpoch) || !helperAutomationOwnedCurrentDuty || !condition[ConditionFlag.BoundByDuty])
			{
				log.Information("[HelperManager] Helper automation is no longer responsible for this duty; skipping delayed AutoDuty start.");
			}
			else
			{
				framework.RunOnFrameworkThread(delegate
				{
					if (!IsHelperAutomationEpochCurrent(activationEpoch) || !helperAutomationOwnedCurrentDuty || !condition[ConditionFlag.BoundByDuty])
					{
						return;
					}
					try
					{
						commandManager.ProcessCommand("/ad start");
						log.Information("[HelperManager] AutoDuty started");
					}
					catch (Exception ex)
					{
						log.Error("[HelperManager] Failed to start AutoDuty: " + ex.Message);
					}
				});
			}
		});
	}

	private unsafe void OnDutyLeave()
	{
		log.Information("[HelperManager] Left duty");
		if (configuration.IsHelperAutomationActive && helperAutomationOwnedCurrentDuty)
		{
			int activationEpoch = Volatile.Read(in helperAutomationEpoch);
			helperAutomationOwnedCurrentDuty = false;
			Task.Run(async delegate
			{
				log.Information("[HelperManager] Leaving party after duty (High-Level Helper)");
				await Task.Delay(2000);
				if (!IsHelperAutomationEpochCurrent(activationEpoch))
				{
					log.Information("[HelperManager] Helper automation was deactivated; skipping duty-exit cleanup and /ad stop.");
				}
				else
				{
					framework.RunOnFrameworkThread(delegate
					{
						if (!IsHelperAutomationEpochCurrent(activationEpoch))
						{
							return;
						}
						try
						{
							commandManager.ProcessCommand("/ad stop");
							log.Information("[HelperManager] AutoDuty stopped");
						}
						catch (Exception ex)
						{
							log.Error("[HelperManager] Failed to stop AutoDuty: " + ex.Message);
						}
					});
					await Task.Delay(1000);
					for (int attempt = 1; attempt <= 3; attempt++)
					{
						bool flag = false;
						GroupManager* ptr = GroupManager.Instance();
						if (ptr != null)
						{
							GroupManager.Group* ptr2 = ptr->GetGroup();
							if (ptr2 != null && ptr2->MemberCount > 1)
							{
								flag = true;
							}
						}
						if (!flag)
						{
							log.Information("[HelperManager] Successfully left party or already solo");
							break;
						}
						log.Information($"[HelperManager] Attempt {attempt}/3: Still in party - sending /leave command");
						LeaveParty();
						if (attempt < 3)
						{
							await Task.Delay(2000);
						}
					}
					StartPartySafetyTimer(activationEpoch);
					await Task.Delay(2000);
					if (CheckGearCondition())
					{
						log.Information("[HelperManager] Gear damaged! Starting Repair Cycle...");
						RunRepairCycle();
					}
					else
					{
						framework.RunOnFrameworkThread(delegate
						{
							if (configuration.CurrentHelperStatus == HelperStatus.InDungeon)
							{
								configuration.CurrentHelperStatus = HelperStatus.Available;
								configuration.Save();
								log.Information("[HelperManager] Helper status: Available");
								IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
								if (localPlayer != null)
								{
									string helperName = localPlayer.Name.ToString();
									ushort helperWorld = (ushort)localPlayer.HomeWorld.RowId;
									crossProcessIPC.BroadcastHelperStatus(helperName, helperWorld, "Available");
								}
							}
						});
					}
				}
			});
		}
		if (configuration.IsQuester)
		{
			log.Information("[HelperManager] Disbanding party after duty (Quester)");
			DisbandParty();
		}
	}

	private unsafe void StartPartySafetyTimer(int activationEpoch)
	{
		Task.Run(async delegate
		{
			await Task.Delay(30000);
			if (IsHelperAutomationEpochCurrent(activationEpoch))
			{
				bool flag = false;
				GroupManager* ptr = GroupManager.Instance();
				if (ptr != null)
				{
					GroupManager.Group* ptr2 = ptr->GetGroup();
					if (ptr2 != null && ptr2->MemberCount > 1)
					{
						flag = true;
					}
				}
				if (flag)
				{
					log.Warning("[HelperManager] SAFETY TIMER: Still in party 30s after dungeon! Force leaving...");
					LeaveParty();
					await Task.Delay(2000);
					GroupManager* ptr3 = GroupManager.Instance();
					if (ptr3 != null)
					{
						GroupManager.Group* ptr4 = ptr3->GetGroup();
						if (ptr4 != null && ptr4->MemberCount > 1)
						{
							log.Error("[HelperManager] SAFETY TIMER: Still in party after force /leave!");
							LeaveParty();
						}
						else
						{
							log.Information("[HelperManager] âœ“ SAFETY TIMER: Successfully left party");
						}
					}
				}
				else
				{
					log.Debug("[HelperManager] Safety timer check: Not in party (OK)");
				}
			}
		});
	}

	private unsafe void OnHelperRequested(string characterName, ushort worldId)
	{
		if (!configuration.IsHelperAutomationActive)
		{
			log.Debug("[HelperManager] Helper automation is inactive, ignoring request");
			return;
		}
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer == null)
		{
			log.Warning("[HelperManager] Local player is null!");
			return;
		}
		string localName = localPlayer.Name.ToString();
		ushort localWorldId = (ushort)localPlayer.HomeWorld.RowId;
		if (!(localName == characterName) || localWorldId != worldId)
		{
			return;
		}
		log.Information("[HelperManager] Helper request is for me! Checking status...");
		int activationEpoch = Volatile.Read(in helperAutomationEpoch);
		Task.Run(async delegate
		{
			if (IsHelperAutomationEpochCurrent(activationEpoch))
			{
				bool flag = false;
				bool flag2 = false;
				GroupManager* ptr = GroupManager.Instance();
				if (ptr != null)
				{
					GroupManager.Group* ptr2 = ptr->GetGroup();
					if (ptr2 != null && ptr2->MemberCount > 0)
					{
						bool flag3 = false;
						if (partyList != null)
						{
							foreach (IPartyMember party in partyList)
							{
								if (party.Name.ToString() == characterName && party.World.RowId == worldId)
								{
									flag3 = true;
									break;
								}
							}
						}
						if (flag3)
						{
							log.Information($"[HelperManager] Request from {characterName}@{worldId} who is ALREADY in my party! Ignoring leave request.");
							flag = false;
						}
						else
						{
							flag = true;
							log.Information("[HelperManager] Currently in party (but not with requester), notifying quester...");
							crossProcessIPC.NotifyHelperInParty(localName, localWorldId);
							if (condition[ConditionFlag.BoundByDuty])
							{
								flag2 = true;
								log.Information("[HelperManager] Currently in duty, notifying quester...");
								crossProcessIPC.NotifyHelperInDuty(localName, localWorldId);
							}
						}
					}
				}
				if (!flag2)
				{
					if (flag)
					{
						if (!IsHelperAutomationEpochCurrent(activationEpoch))
						{
							return;
						}
						LeaveParty();
						await Task.Delay(1000);
					}
					if (IsHelperAutomationEpochCurrent(activationEpoch))
					{
						log.Information("[HelperManager] Ready to accept invite!");
						partyInviteAutoAccept.EnableAutoAccept();
						crossProcessIPC.NotifyHelperReady(localName, localWorldId);
					}
				}
			}
		});
	}

	private void OnHelperDismissed()
	{
		if (configuration.IsHelperAutomationActive)
		{
			log.Information("[HelperManager] Received dismiss signal, leaving party...");
			DisbandParty();
		}
	}

	private void OnHelperAvailable(string characterName, ushort worldId)
	{
		if (!availableHelpers.Any<(string, ushort)>(((string Name, ushort WorldId) h) => h.Name == characterName && h.WorldId == worldId))
		{
			availableHelpers.Add((characterName, worldId));
			log.Information($"[HelperManager] Helper discovered: {characterName}@{worldId} (Total: {availableHelpers.Count})");
		}
	}

	private void OnHelperStatusUpdate(string characterName, ushort worldId, string status)
	{
		if (string.Equals(status, "Unavailable", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Offline", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Quester", StringComparison.OrdinalIgnoreCase))
		{
			int num = availableHelpers.RemoveAll(((string Name, ushort WorldId) helper) => string.Equals(helper.Name, characterName, StringComparison.OrdinalIgnoreCase) && helper.WorldId == worldId);
			helperReadyStatus.Remove((characterName, worldId));
			if (num > 0)
			{
				log.Information($"[HelperManager] Helper removed after role/status change: {characterName}@{worldId}");
			}
		}
		else
		{
			OnHelperAvailable(characterName, worldId);
		}
	}

	private void OnHelperReady(string characterName, ushort worldId)
	{
		if (configuration.IsQuester)
		{
			log.Information($"[HelperManager] Helper {characterName}@{worldId} is ready!");
			helperReadyStatus[(characterName, worldId)] = true;
		}
	}

	private void OnHelperInParty(string characterName, ushort worldId)
	{
		if (configuration.IsQuester)
		{
			log.Information($"[HelperManager] Helper {characterName}@{worldId} is in a party, waiting for them to leave...");
		}
	}

	private void OnHelperInDuty(string characterName, ushort worldId)
	{
		if (configuration.IsQuester)
		{
			log.Warning($"[HelperManager] Helper {characterName}@{worldId} is in a duty! Cannot invite until they leave.");
		}
	}

	private void OnPartyInviteRequested(string targetHelperName, ushort targetHelperWorld, string questerName, ushort questerWorld)
	{
		if (!configuration.IsHelperAutomationActive)
		{
			return;
		}
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		if (localPlayer == null)
		{
			return;
		}
		string text = localPlayer.Name.ToString();
		ushort num = (ushort)localPlayer.HomeWorld.RowId;
		if (!(text == targetHelperName) || num != targetHelperWorld)
		{
			return;
		}
		log.Information($"[HelperManager] Received REVERSE INVITE request from Quester: {questerName}@{questerWorld}. I need to invite THEM.");
		int activationEpoch = Volatile.Read(in helperAutomationEpoch);
		Task.Run(async delegate
		{
			_ = 5;
			try
			{
				if (IsHelperAutomationEpochCurrent(activationEpoch))
				{
					log.Information("[HelperManager] Leaving current party...");
					framework.RunOnFrameworkThread(delegate
					{
						if (IsHelperAutomationEpochCurrent(activationEpoch))
						{
							memoryHelper.SendChatMessage("/leave");
						}
					});
					await Task.Delay(500);
					if (IsHelperAutomationEpochCurrent(activationEpoch))
					{
						bool inviteSuccess = false;
						framework.RunOnFrameworkThread(delegate
						{
							if (IsHelperAutomationEpochCurrent(activationEpoch))
							{
								log.Information($"[HelperManager] Inviting Quester {questerName}@{questerWorld}...");
								inviteSuccess = partyInviteService.InviteToParty(questerName, questerWorld);
								if (!inviteSuccess)
								{
									log.Error("[HelperManager] Failed to invite Quester!");
								}
							}
						});
						await Task.Delay(100);
						if (!inviteSuccess)
						{
							log.Error("[HelperManager] Aborting reverse invite - invite failed");
						}
						else
						{
							DateTime timeout = DateTime.Now.AddSeconds(30.0);
							bool questerJoined = false;
							log.Information("[HelperManager] Waiting for " + questerName + " to join party (30s timeout)...");
							while (DateTime.Now < timeout)
							{
								await Task.Delay(500);
								if (!IsHelperAutomationEpochCurrent(activationEpoch))
								{
									return;
								}
								bool foundInParty = false;
								framework.RunOnFrameworkThread(delegate
								{
									if (partyList != null)
									{
										foreach (IPartyMember party in partyList)
										{
											if (party.Name.ToString() == questerName && party.World.RowId == questerWorld)
											{
												foundInParty = true;
												break;
											}
										}
									}
								});
								await Task.Delay(50);
								if (foundInParty)
								{
									questerJoined = true;
									break;
								}
							}
							if (questerJoined)
							{
								log.Information("[HelperManager] âœ“ Quester " + questerName + " joined! Promoting to Party Leader...");
								await Task.Delay(1000);
								if (IsHelperAutomationEpochCurrent(activationEpoch))
								{
									framework.RunOnFrameworkThread(delegate
									{
										if (IsHelperAutomationEpochCurrent(activationEpoch))
										{
											if (partyInviteService.PromoteToLeader(questerName))
											{
												log.Information("[HelperManager] âœ“ Promote command sent for " + questerName);
											}
											else
											{
												log.Error("[HelperManager] âœ— Failed to send promote command for " + questerName);
											}
										}
									});
									await Task.Delay(1500);
									log.Information("[HelperManager] âœ“ Promote request sent for " + questerName + " (dialog will be auto-accepted)");
								}
							}
							else
							{
								log.Warning("[HelperManager] âœ— Quester " + questerName + " did not join party within timeout.");
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				log.Error("[HelperManager] REVERSE INVITE HANDLER EXCEPTION: " + ex.Message);
				log.Error("[HelperManager] Stack: " + ex.StackTrace);
			}
		});
	}

	private void OnRequestHelperAnnouncements()
	{
		if (configuration.IsHelperAutomationActive)
		{
			log.Information("[HelperManager] Received request for helper announcements, announcing...");
			AnnounceIfHelper();
		}
	}

	public void RequestHelperAnnouncements()
	{
		crossProcessIPC.RequestHelperAnnouncements();
	}

	public void BroadcastRequestHelperAnnouncements()
	{
		multiClientIPC.BroadcastRequestHelperAnnouncements();
		crossProcessIPC.BroadcastRequestHelperAnnouncements();
	}

	public void CheckAndExecuteRepair()
	{
		if (configuration.IsHelperAutomationActive)
		{
			if (CheckGearCondition())
			{
				log.Information("[HelperManager] Gear condition below threshold! Starting Repair Cycle...");
				RunRepairCycle();
			}
			else
			{
				log.Information("[HelperManager] Gear condition OK.");
			}
		}
	}

	private unsafe bool CheckGearCondition()
	{
		try
		{
			InventoryManager* ptr = InventoryManager.Instance();
			if (ptr == null)
			{
				return false;
			}
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(InventoryType.EquippedItems);
			if (inventoryContainer == null)
			{
				return false;
			}
			float num = 30000f;
			for (int i = 0; i < 13; i++)
			{
				InventoryItem inventoryItem = inventoryContainer->Items[i];
				if (inventoryItem.ItemId != 0 && (float)(int)inventoryItem.Condition < num)
				{
					num = (int)inventoryItem.Condition;
				}
			}
			float num2 = num / 300f;
			log.Information($"[HelperManager] Lowest Gear Condition: {num2:F1}% (Threshold: {configuration.RepairThresholdPercent}%)");
			return num2 <= (float)configuration.RepairThresholdPercent;
		}
		catch (Exception ex)
		{
			log.Error("[HelperManager] Error Checking Gear Condition: " + ex.Message);
			return false;
		}
	}

	private void RunRepairCycle()
	{
		if (IsRepairing || !configuration.IsHelperAutomationActive)
		{
			return;
		}
		int activationEpoch = Volatile.Read(in helperAutomationEpoch);
		Task.Run(async delegate
		{
			_ = 3;
			try
			{
				if (IsHelperAutomationEpochCurrent(activationEpoch))
				{
					IsRepairing = true;
					framework.RunOnFrameworkThread(delegate
					{
						if (!IsHelperAutomationEpochCurrent(activationEpoch))
						{
							return;
						}
						configuration.CurrentHelperStatus = HelperStatus.Repairing;
						configuration.Save();
						IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
						if (localPlayer != null)
						{
							string helperName = localPlayer.Name.ToString();
							ushort num = (ushort)localPlayer.HomeWorld.RowId;
							crossProcessIPC.BroadcastHelperStatus(helperName, num, "Repairing");
							multiClientIPC.BroadcastHelperStatus(helperName, num, "Repairing");
						}
						try
						{
							commandManager.ProcessCommand("/ad stop");
						}
						catch
						{
						}
					});
					await Task.Delay(1000);
					if (IsHelperAutomationEpochCurrent(activationEpoch))
					{
						log.Information("[HelperManager] === STARTING REPAIR CYCLE ===");
						framework.RunOnFrameworkThread(delegate
						{
							if (IsHelperAutomationEpochCurrent(activationEpoch))
							{
								commandManager.ProcessCommand("/ad repair");
							}
						});
						log.Information("[HelperManager] Sent /ad repair");
						DateTime timeout = DateTime.Now.AddMinutes(10.0);
						bool repaired = false;
						while (DateTime.Now < timeout)
						{
							await Task.Delay(10000);
							if (!IsHelperAutomationEpochCurrent(activationEpoch))
							{
								return;
							}
							if (!CheckGearCondition())
							{
								repaired = true;
								log.Information("[HelperManager] Gear appears repaired! (Condition > Threshold)");
								break;
							}
							log.Information("[HelperManager] Still repairing... waiting 10s");
						}
						if (!repaired)
						{
							log.Warning("[HelperManager] Repair Cycle Timed Out! Proceeding anyway...");
						}
						framework.RunOnFrameworkThread(delegate
						{
							if (IsHelperAutomationEpochCurrent(activationEpoch))
							{
								commandManager.ProcessCommand("/ad stop");
								log.Information("[HelperManager] Sent /ad stop");
							}
						});
						await Task.Delay(2000);
						if (IsHelperAutomationEpochCurrent(activationEpoch))
						{
							uint startTerritory = 0u;
							framework.RunOnFrameworkThread(() => startTerritory = clientState.TerritoryType);
							for (int i = 0; i < 3; i++)
							{
								if (!IsHelperAutomationEpochCurrent(activationEpoch))
								{
									break;
								}
								framework.RunOnFrameworkThread(delegate
								{
									if (IsHelperAutomationEpochCurrent(activationEpoch))
									{
										commandManager.ProcessCommand("/li inn");
										log.Information($"[HelperManager] Sent /li inn (Attempt {i + 1}/3)");
									}
								});
								await Task.Delay(30000);
								uint currentTerritory = 0u;
								framework.RunOnFrameworkThread(() => currentTerritory = clientState.TerritoryType);
								if (currentTerritory != startTerritory)
								{
									log.Information("[HelperManager] Teleport successful (Territory changed)");
									break;
								}
								if (i < 2)
								{
									log.Warning("[HelperManager] Teleport /li inn seems to have failed (Same territory). Retrying...");
								}
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				log.Error("[HelperManager] Error in Repair Cycle: " + ex.Message);
			}
			finally
			{
				IsRepairing = false;
				framework.RunOnFrameworkThread(delegate
				{
					if (configuration.IsHelperAutomationActive)
					{
						configuration.CurrentHelperStatus = HelperStatus.Available;
						configuration.Save();
						log.Information("[HelperManager] Helper Status: Available");
						IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
						if (localPlayer != null)
						{
							string helperName = localPlayer.Name.ToString();
							ushort num = (ushort)localPlayer.HomeWorld.RowId;
							crossProcessIPC.BroadcastHelperStatus(helperName, num, "Available");
							multiClientIPC.BroadcastHelperStatus(helperName, num, "Available");
						}
					}
				});
			}
		});
	}

	public async Task InviteLANHelper(string ipAddress, string characterName, ushort worldId)
	{
		if (lanHelperClient == null)
		{
			return;
		}
		log.Information("[HelperManager] ========================================");
		log.Information("[HelperManager] === INVITING LAN HELPER ===");
		log.Information("[HelperManager] Helper: " + characterName);
		log.Information("[HelperManager] IP: " + ipAddress);
		log.Information("[HelperManager] ========================================");
		DisbandParty();
		await Task.Delay(500);
		log.Information("[HelperManager] Sending helper request to " + ipAddress + "...");
		try
		{
			Task<bool> requestTask = lanHelperClient.RequestHelperAsync(ipAddress, "LAN Dungeon");
			if (await Task.WhenAny(requestTask, Task.Delay(2000)) == requestTask)
			{
				if (!(await requestTask))
				{
					log.Warning("[HelperManager] Failed to send helper request to " + ipAddress + " (connection failed)");
				}
			}
			else
			{
				log.Warning("[HelperManager] Helper request to " + ipAddress + " timed out (continuing to invite anyway)");
			}
		}
		catch (Exception ex)
		{
			log.Error("[HelperManager] Helper request error: " + ex.Message);
		}
		await Task.Delay(500);
		if (configuration.EnableFreeTrialHelperInvite)
		{
			log.Information("[HelperManager] REVERSE INVITE (Free Trial Mode): Requesting LAN Helper " + characterName + " to invite ME...");
			if (await lanHelperClient.RequestPartyInviteAsync(ipAddress))
			{
				log.Information("[HelperManager] âœ“ LAN Invite Request sent");
			}
			else
			{
				log.Error("[HelperManager] âœ— Failed to send LAN Invite Request");
			}
			return;
		}
		log.Information($"[HelperManager] Sending party invite to {characterName}@{worldId}...");
		if (!partyInviteService.InviteToParty(characterName, worldId))
		{
			log.Error("[HelperManager] Failed to invite " + characterName);
			return;
		}
		await lanHelperClient.NotifyInviteSentAsync(ipAddress, characterName);
		log.Information("[HelperManager] âœ“ LAN helper invite complete");
	}

	public void Dispose()
	{
		condition.ConditionChange -= OnConditionChanged;
		multiClientIPC.OnHelperRequested -= OnHelperRequested;
		multiClientIPC.OnHelperDismissed -= OnHelperDismissed;
		multiClientIPC.OnHelperAvailable -= OnHelperAvailable;
		multiClientIPC.OnHelperStatusUpdate -= OnHelperStatusUpdate;
		crossProcessIPC.OnHelperRequested -= OnHelperRequested;
		crossProcessIPC.OnHelperDismissed -= OnHelperDismissed;
		crossProcessIPC.OnHelperAvailable -= OnHelperAvailable;
		crossProcessIPC.OnHelperReady -= OnHelperReady;
		crossProcessIPC.OnHelperInParty -= OnHelperInParty;
		crossProcessIPC.OnHelperInDuty -= OnHelperInDuty;
		crossProcessIPC.OnHelperInDuty -= OnHelperInDuty;
		crossProcessIPC.OnRequestHelperAnnouncements -= OnRequestHelperAnnouncements;
		crossProcessIPC.OnHelperStatusUpdate -= OnHelperStatusUpdate;
		crossProcessIPC.OnPartyInviteRequested -= OnPartyInviteRequested;
		multiClientIPC.OnPartyInviteRequested -= OnPartyInviteRequested;
		if (lanHelperServer != null)
		{
			lanHelperServer.OnPartyInviteRequested -= OnLANPartyInviteRequested;
		}
	}
}
