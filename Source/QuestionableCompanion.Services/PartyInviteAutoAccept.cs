using System;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using QuestionableCompanion.Utils;

namespace QuestionableCompanion.Services;

public class PartyInviteAutoAccept : IDisposable
{
	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly IGameGui gameGui;

	private readonly IPartyList partyList;

	private readonly Configuration configuration;

	private bool shouldAutoAccept;

	private DateTime autoAcceptUntil = DateTime.MinValue;

	private bool hasLoggedAlwaysAccept;

	public PartyInviteAutoAccept(IPluginLog log, IFramework framework, IGameGui gameGui, IPartyList partyList, Configuration configuration)
	{
		this.log = log;
		this.framework = framework;
		this.gameGui = gameGui;
		this.partyList = partyList;
		this.configuration = configuration;
		framework.Update += OnFrameworkUpdate;
		log.Information("[PartyInviteAutoAccept] Initialized");
	}

	public void EnableAutoAccept()
	{
		if (!configuration.IsHelperAutomationActive && !configuration.IsQuester)
		{
			log.Debug("[PartyInviteAutoAccept] Not a helper or quester, ignoring auto-accept request");
			return;
		}
		shouldAutoAccept = true;
		autoAcceptUntil = DateTime.Now.AddSeconds(30.0);
		string text = (configuration.IsHelperAutomationActive ? "Helper" : "Quester");
		log.Information("[PartyInviteAutoAccept] Auto-accept enabled for 30 seconds (" + text + ")");
		log.Information($"[PartyInviteAutoAccept] Will accept until: {autoAcceptUntil:HH:mm:ss}");
		log.Information("[PartyInviteAutoAccept] Will accept ALL party invites during this time!");
	}

	public void EnableForQuester(string questerName)
	{
		if (!configuration.IsHelperAutomationActive)
		{
			log.Debug("[PartyInviteAutoAccept] Helper automation is inactive, ignoring LAN auto-accept request");
			return;
		}
		shouldAutoAccept = true;
		autoAcceptUntil = DateTime.Now.AddSeconds(60.0);
		log.Information("[PartyInviteAutoAccept] Auto-accept enabled for quester: " + questerName);
		log.Information("[PartyInviteAutoAccept] Will accept invites for 60 seconds");
	}

	public void DisableAutoAccept()
	{
		shouldAutoAccept = false;
		autoAcceptUntil = DateTime.MinValue;
		hasLoggedAlwaysAccept = false;
		log.Information("[PartyInviteAutoAccept] Helper auto-accept disabled");
	}

	private unsafe void OnFrameworkUpdate(IFramework framework)
	{
		bool flag = false;
		if (configuration.IsHighLevelHelper && !configuration.IsHelperAutomationActive)
		{
			shouldAutoAccept = false;
			hasLoggedAlwaysAccept = false;
			return;
		}
		if (configuration.IsHelperAutomationActive && configuration.CurrentHelperStatus == HelperStatus.Repairing)
		{
			if (shouldAutoAccept)
			{
				shouldAutoAccept = false;
			}
			return;
		}
		if (configuration.IsHelperAutomationActive && configuration.AlwaysAutoAcceptInvites)
		{
			if (!hasLoggedAlwaysAccept)
			{
				log.Information("[PartyInviteAutoAccept] === ALWAYS AUTO-ACCEPT ENABLED ===");
				log.Information("[PartyInviteAutoAccept] Helper will continuously accept ALL party invites");
				log.Information("[PartyInviteAutoAccept] This mode is ALWAYS ON (no timeout)");
				hasLoggedAlwaysAccept = true;
			}
			flag = true;
		}
		else if (shouldAutoAccept)
		{
			if (hasLoggedAlwaysAccept)
			{
				log.Information("[PartyInviteAutoAccept] Always auto-accept disabled");
				hasLoggedAlwaysAccept = false;
			}
			if (DateTime.Now > autoAcceptUntil)
			{
				shouldAutoAccept = false;
				log.Information("[PartyInviteAutoAccept] Auto-accept window expired");
				return;
			}
			flag = true;
		}
		else if (hasLoggedAlwaysAccept)
		{
			log.Information("[PartyInviteAutoAccept] Always auto-accept disabled");
			hasLoggedAlwaysAccept = false;
		}
		if (!flag)
		{
			return;
		}
		try
		{
			AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectYesno");
			if (addonByName == IntPtr.Zero)
			{
				return;
			}
			AtkUnitBase* ptr = (AtkUnitBase*)(nint)addonByName;
			if (ptr == null)
			{
				log.Warning("[PartyInviteAutoAccept] Addon pointer is null!");
			}
			else
			{
				if (!ptr->IsVisible)
				{
					return;
				}
				string dialogText = SelectYesnoTextHandler.GetDialogText(ptr);
				if (string.IsNullOrEmpty(dialogText))
				{
					log.Debug("[PartyInviteAutoAccept] Could not extract dialog text");
				}
				else if (IsPartyInviteDialog(dialogText))
				{
					log.Information("[PartyInviteAutoAccept] ✓ Party invite detected: " + dialogText);
					if (SelectYesnoTextHandler.ClickYesButton(ptr))
					{
						log.Information("[PartyInviteAutoAccept] ✓ Party invite accepted!");
					}
					else
					{
						log.Warning("[PartyInviteAutoAccept] Failed to click Yes button");
					}
				}
			}
		}
		catch (Exception ex)
		{
			log.Error("[PartyInviteAutoAccept] Error: " + ex.Message);
			log.Error("[PartyInviteAutoAccept] Stack: " + ex.StackTrace);
		}
	}

	private bool IsPartyInviteDialog(string text)
	{
		string[] array = new string[9] { "Der Gruppe von .* beitreten\\?", "Join .* party\\?", ".*のパーティに参加します。よろしいですか？", "Rejoindre l'équipe de .*\\?", "zum Gruppenanführer", "Designate .* as party leader", "Promote .* to party leader", ".*をパーティリーダーにします", "Désigner .* comme chef d'équipe" };
		foreach (string pattern in array)
		{
			if (SelectYesnoTextHandler.MatchesPattern(text, pattern, isRegex: true))
			{
				return true;
			}
		}
		return false;
	}

	public void Dispose()
	{
		framework.Update -= OnFrameworkUpdate;
	}
}
