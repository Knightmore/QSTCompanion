using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestionableCompanion.Services;

namespace QuestionableCompanion.Windows;

public class DebugWindow : Window, IDisposable
{
	private readonly Plugin plugin;

	private readonly CombatDutyDetectionService? combatDutyDetection;

	private readonly DeathHandlerService? deathHandler;

	private readonly DungeonAutomationService? dungeonAutomation;

	public DebugWindow(Plugin plugin, CombatDutyDetectionService? combatDutyDetection, DeathHandlerService? deathHandler, DungeonAutomationService? dungeonAutomation)
		: base("QST Companion Debug###QSTDebug", ImGuiWindowFlags.NoCollapse)
	{
		this.plugin = plugin;
		this.combatDutyDetection = combatDutyDetection;
		this.deathHandler = deathHandler;
		this.dungeonAutomation = dungeonAutomation;
		base.Size = new Vector2(500f, 400f);
		base.SizeCondition = ImGuiCond.FirstUseEver;
	}

	public void Dispose()
	{
	}

	public override void Draw()
	{
		ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), " DEBUG MENU - FOR TESTING ONLY ");
		ImGui.Separator();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Combat Handling");
		ImGui.Separator();
		if (combatDutyDetection != null)
		{
			ImU8String text = new ImU8String(11, 1);
			text.AppendLiteral("In Combat: ");
			text.AppendFormatted(combatDutyDetection.IsInCombat);
			ImGui.Text(text);
			ImU8String text2 = new ImU8String(9, 1);
			text2.AppendLiteral("In Duty: ");
			text2.AppendFormatted(combatDutyDetection.IsInDuty);
			ImGui.Text(text2);
			ImU8String text3 = new ImU8String(15, 1);
			text3.AppendLiteral("In Duty Queue: ");
			text3.AppendFormatted(combatDutyDetection.IsInDutyQueue);
			ImGui.Text(text3);
			ImU8String text4 = new ImU8String(14, 1);
			text4.AppendLiteral("Should Pause: ");
			text4.AppendFormatted(combatDutyDetection.ShouldPauseAutomation);
			ImGui.Text(text4);
			ImGui.Spacing();
			if (ImGui.Button("Test Combat Detection"))
			{
				combatDutyDetection.Update();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Manually trigger combat detection update");
			}
			ImGui.SameLine();
			if (ImGui.Button("Reset Combat State"))
			{
				combatDutyDetection.Reset();
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "Combat Detection Service not available");
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Death Handling");
		ImGui.Separator();
		if (deathHandler != null)
		{
			ImU8String text5 = new ImU8String(9, 1);
			text5.AppendLiteral("Is Dead: ");
			text5.AppendFormatted(deathHandler.IsDead);
			ImGui.Text(text5);
			ImU8String text6 = new ImU8String(19, 1);
			text6.AppendLiteral("Time Since Death: ");
			text6.AppendFormatted(deathHandler.TimeSinceDeath.TotalSeconds, "F1");
			text6.AppendLiteral("s");
			ImGui.Text(text6);
			ImGui.Spacing();
			if (ImGui.Button("Test Death Detection"))
			{
				deathHandler.Update();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Manually trigger death detection update");
			}
			ImGui.SameLine();
			if (ImGui.Button("Reset Death State"))
			{
				deathHandler.Reset();
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "Death Handler Service not available");
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Dungeon Automation");
		ImGui.Separator();
		if (dungeonAutomation != null)
		{
			ImU8String text7 = new ImU8String(19, 1);
			text7.AppendLiteral("Waiting for Party: ");
			text7.AppendFormatted(dungeonAutomation.IsWaitingForParty);
			ImGui.Text(text7);
			ImU8String text8 = new ImU8String(20, 1);
			text8.AppendLiteral("Current Party Size: ");
			text8.AppendFormatted(dungeonAutomation.CurrentPartySize);
			ImGui.Text(text8);
			ImGui.Spacing();
			if (ImGui.Button("Test Party Invite"))
			{
				dungeonAutomation.StartDungeonAutomation();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Test BTB enable + invite + party wait");
			}
			ImGui.SameLine();
			if (ImGui.Button("Test Party Disband"))
			{
				dungeonAutomation.DisbandParty();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Test party disband with /pcmd breakup");
			}
			ImGui.SameLine();
			if (ImGui.Button("Reset Dungeon State"))
			{
				dungeonAutomation.Reset();
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "Dungeon Automation Service not available");
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Info");
		ImGui.Text("This debug menu allows testing of individual features.");
		ImGui.Text("Use /qstcomp dbg to toggle this window.");
	}
}
