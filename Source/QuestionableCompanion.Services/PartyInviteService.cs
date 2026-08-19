using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace QuestionableCompanion.Services;

public class PartyInviteService
{
	private readonly IPluginLog log;

	private readonly IObjectTable objectTable;

	private readonly IClientState clientState;

	public PartyInviteService(IPluginLog log, IObjectTable objectTable, IClientState clientState)
	{
		this.log = log;
		this.objectTable = objectTable;
		this.clientState = clientState;
	}

	public unsafe bool InviteToParty(string characterName, ushort worldId)
	{
		if (string.IsNullOrWhiteSpace(characterName))
		{
			log.Error("[PartyInvite] Character name is null or empty!");
			return false;
		}
		if (worldId == 0)
		{
			log.Error("[PartyInvite] World ID is 0 (invalid)!");
			return false;
		}
		characterName = characterName.Trim();
		try
		{
			InfoModule* ptr = InfoModule.Instance();
			if (ptr == null)
			{
				log.Error("[PartyInvite] InfoModule is null!");
				return false;
			}
			InfoProxyPartyInvite* infoProxyById = (InfoProxyPartyInvite*)ptr->GetInfoProxyById(InfoProxyId.PartyInvite);
			if (infoProxyById == null)
			{
				log.Error("[PartyInvite] InfoProxyPartyInvite is null!");
				return false;
			}
			ulong num = 0uL;
			log.Information($"[PartyInvite] Using name-based invite (ContentId=0, Name={characterName}, World={worldId})");
			log.Information($"[PartyInvite] Sending invite to {characterName}@{worldId} (ContentId: {num})");
			fixed (byte* bytes = Encoding.UTF8.GetBytes(characterName + "\0"))
			{
				bool num2 = infoProxyById->InviteToParty(num, bytes, worldId);
				if (num2)
				{
					log.Information($"[PartyInvite] ✓ Successfully sent invite to {characterName}@{worldId}");
				}
				else
				{
					log.Warning($"[PartyInvite] ✗ Failed to send invite to {characterName}@{worldId}");
				}
				return num2;
			}
		}
		catch (Exception ex)
		{
			log.Error("[PartyInvite] Exception: " + ex.Message);
			log.Error("[PartyInvite] StackTrace: " + ex.StackTrace);
			return false;
		}
	}

	public unsafe bool InviteToPartyByContentId(ulong contentId, ushort worldId)
	{
		try
		{
			InfoModule* ptr = InfoModule.Instance();
			if (ptr == null)
			{
				log.Error("[PartyInvite] InfoModule is null!");
				return false;
			}
			InfoProxyPartyInvite* infoProxyById = (InfoProxyPartyInvite*)ptr->GetInfoProxyById(InfoProxyId.PartyInvite);
			if (infoProxyById == null)
			{
				log.Error("[PartyInvite] InfoProxyPartyInvite is null!");
				return false;
			}
			log.Information($"[PartyInvite] Sending invite to ContentID {contentId}@{worldId}");
			bool num = infoProxyById->InviteToPartyContentId(contentId, worldId);
			if (num)
			{
				log.Information($"[PartyInvite] Successfully sent invite to ContentID {contentId}@{worldId}");
			}
			else
			{
				log.Warning($"[PartyInvite] Failed to send invite to ContentID {contentId}@{worldId}");
			}
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[PartyInvite] Exception: " + ex.Message);
			log.Error("[PartyInvite] StackTrace: " + ex.StackTrace);
			return false;
		}
	}

	public unsafe bool InviteToPartyInInstanceByContentId(ulong contentId)
	{
		try
		{
			InfoModule* ptr = InfoModule.Instance();
			if (ptr == null)
			{
				log.Error("[PartyInvite] InfoModule is null!");
				return false;
			}
			InfoProxyPartyInvite* infoProxyById = (InfoProxyPartyInvite*)ptr->GetInfoProxyById(InfoProxyId.PartyInvite);
			if (infoProxyById == null)
			{
				log.Error("[PartyInvite] InfoProxyPartyInvite is null!");
				return false;
			}
			log.Information($"[PartyInvite] Sending instance invite to ContentID {contentId}");
			bool num = infoProxyById->InviteToPartyInInstanceByContentId(contentId);
			if (num)
			{
				log.Information($"[PartyInvite] Successfully sent instance invite to ContentID {contentId}");
			}
			else
			{
				log.Warning($"[PartyInvite] Failed to send instance invite to ContentID {contentId}");
			}
			return num;
		}
		catch (Exception ex)
		{
			log.Error("[PartyInvite] Exception: " + ex.Message);
			log.Error("[PartyInvite] StackTrace: " + ex.StackTrace);
			return false;
		}
	}

	public unsafe bool LeaveParty()
	{
		try
		{
			GroupManager* ptr = GroupManager.Instance();
			if (ptr == null)
			{
				log.Error("[PartyInvite] GroupManager is null!");
				return false;
			}
			GroupManager.Group* ptr2 = ptr->GetGroup();
			if (ptr2 == null || ptr2->MemberCount == 0)
			{
				log.Debug("[PartyInvite] Not in a party");
				return true;
			}
			log.Information($"[PartyInvite] Leaving party (Members: {ptr2->MemberCount})");
			RaptureShellModule* ptr3 = RaptureShellModule.Instance();
			if (ptr3 == null)
			{
				log.Error("[PartyInvite] RaptureShellModule is null!");
				return false;
			}
			UIModule* ptr4 = UIModule.Instance();
			if (ptr4 == null)
			{
				log.Error("[PartyInvite] UIModule is null!");
				return false;
			}
			Utf8String* ptr5 = Utf8String.FromString("/leave");
			ptr3->ExecuteCommandInner(ptr5, ptr4);
			ptr5->Dtor();
			log.Information("[PartyInvite] Leave command executed successfully");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[PartyInvite] Exception: " + ex.Message);
			log.Error("[PartyInvite] StackTrace: " + ex.StackTrace);
			return false;
		}
	}

	public unsafe bool PromoteToLeader(string characterName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(characterName))
			{
				return false;
			}
			log.Information("[PartyInvite] Promoting " + characterName + " to leader using AgentPartyMember.Promote");
			GroupManager* ptr = GroupManager.Instance();
			if (ptr == null)
			{
				log.Error("[PartyInvite] GroupManager is null");
				return false;
			}
			GroupManager.Group* ptr2 = ptr->GetGroup();
			if (ptr2 == null)
			{
				log.Error("[PartyInvite] Not in a party");
				return false;
			}
			ulong num = 0uL;
			for (int i = 0; i < ptr2->MemberCount; i++)
			{
				PartyMember* partyMemberByIndex = ptr2->GetPartyMemberByIndex(i);
				if (partyMemberByIndex != null && partyMemberByIndex->NameString == characterName)
				{
					num = partyMemberByIndex->ContentId;
					log.Information($"[PartyInvite] Found {characterName} with ContentId: {num}");
					break;
				}
			}
			if (num == 0L)
			{
				log.Error("[PartyInvite] Could not find " + characterName + " in party list - cannot promote");
				return false;
			}
			AgentPartyMember* ptr3 = AgentPartyMember.Instance();
			if (ptr3 == null)
			{
				log.Error("[PartyInvite] AgentPartyMember is null");
				return false;
			}
			Encoding uTF = Encoding.UTF8;
			byte[] array = new byte[uTF.GetByteCount(characterName) + 1];
			uTF.GetBytes(characterName, 0, characterName.Length, array, 0);
			array[^1] = 0;
			fixed (byte* ptr4 = array)
			{
				ptr3->Promote(ptr4, 0, num);
			}
			log.Information("[PartyInvite] ✓ Promoted " + characterName + " to Party Leader via AgentPartyMember");
			return true;
		}
		catch (Exception ex)
		{
			log.Error("[PartyInvite] Failed to promote: " + ex.Message);
			log.Error("[PartyInvite] Stack: " + ex.StackTrace);
			return false;
		}
	}
}
