using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuestionableCompanion.Services;

namespace QuestionableCompanion.Windows;

public class ConfigWindow : Window, IDisposable
{
	private readonly Configuration configuration;

	private readonly Plugin plugin;

	private string stopPointCombatStartCommandInput = string.Empty;

	private string stopPointCombatEndCommandInput = string.Empty;

	private string soloDutyCombatStartCommandInput = string.Empty;

	private string soloDutyCombatEndCommandInput = string.Empty;

	public ConfigWindow(Plugin plugin)
		: base("Questionable Companion Settings###QCSettings")
	{
		base.Size = new Vector2(600f, 400f);
		base.SizeCondition = ImGuiCond.FirstUseEver;
		this.plugin = plugin;
		configuration = plugin.Configuration;
	}

	public void Dispose()
	{
	}

	public override void PreDraw()
	{
		if (configuration.IsConfigWindowMovable)
		{
			base.Flags &= ~ImGuiWindowFlags.NoMove;
		}
		else
		{
			base.Flags |= ImGuiWindowFlags.NoMove;
		}
	}

	public override void Draw()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.8f, 0.2f, 1f));
		ImGui.TextWrapped("Configuration Moved!");
		ImGui.PopStyleColor();
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();
		ImGui.TextWrapped("The configuration interface has been moved to the new Main Window for a better user experience.");
		ImGui.Spacing();
		ImGui.TextWrapped("All settings are now available in the Main Window with improved organization and features:");
		ImGui.Spacing();
		ImGui.BulletText("Quest Rotation Management");
		ImGui.BulletText("Event Quest Automation");
		ImGui.BulletText("MSQ Progress Tracking");
		ImGui.BulletText("DC Travel Configuration");
		ImGui.BulletText("Advanced Settings");
		ImGui.BulletText("And much more!");
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();
		if (ImGui.Button(size: new Vector2(ImGui.GetContentRegionAvail().X, 50f), label: "Open Main Window (Settings Tab)"))
		{
			plugin.ToggleMainUi();
			base.IsOpen = false;
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
		ImGui.TextWrapped("This legacy configuration window will be removed in a future update.");
		ImGui.TextWrapped("Please use the Main Window for all configuration needs.");
		ImGui.PopStyleColor();
	}

	private void DrawGeneralTab()
	{
		ImGui.Text("General Settings");
		ImGui.Separator();
		ImGui.Spacing();
		bool v = configuration.AutoStartOnLogin;
		if (ImGui.Checkbox("Auto-start on login", ref v))
		{
			configuration.AutoStartOnLogin = v;
			configuration.Save();
		}
		bool v2 = configuration.EnableDryRun;
		if (ImGui.Checkbox("Enable Dry Run Mode (simulate without executing)", ref v2))
		{
			configuration.EnableDryRun = v2;
			configuration.Save();
		}
		bool v3 = configuration.RestoreStateOnLoad;
		if (ImGui.Checkbox("Restore state on plugin load", ref v3))
		{
			configuration.RestoreStateOnLoad = v3;
			configuration.Save();
		}
		ImGui.Spacing();
		ImGui.Text("Execution Settings");
		ImGui.Separator();
		ImGui.Spacing();
		int v4 = configuration.MaxRetryAttempts;
		if (ImGui.SliderInt("Max retry attempts", ref v4, 1, 10))
		{
			configuration.MaxRetryAttempts = v4;
			configuration.Save();
		}
		int v5 = configuration.CharacterSwitchDelay;
		if (ImGui.SliderInt("Character switch delay (seconds)", ref v5, 3, 15))
		{
			configuration.CharacterSwitchDelay = v5;
			configuration.Save();
		}
		ImGui.Spacing();
		ImGui.Text("Logging Settings");
		ImGui.Separator();
		ImGui.Spacing();
		int v6 = configuration.MaxLogEntries;
		if (ImGui.SliderInt("Max log entries", ref v6, 50, 500))
		{
			configuration.MaxLogEntries = v6;
			configuration.Save();
		}
		bool v7 = configuration.ShowDebugLogs;
		if (ImGui.Checkbox("Show debug logs", ref v7))
		{
			configuration.ShowDebugLogs = v7;
			configuration.Save();
		}
		bool v8 = configuration.LogToFile;
		if (ImGui.Checkbox("Log to file", ref v8))
		{
			configuration.LogToFile = v8;
			configuration.Save();
		}
		ImGui.Spacing();
		ImGui.Text("UI Settings");
		ImGui.Separator();
		ImGui.Spacing();
		bool v9 = configuration.IsConfigWindowMovable;
		if (ImGui.Checkbox("Movable config window", ref v9))
		{
			configuration.IsConfigWindowMovable = v9;
			configuration.Save();
		}
	}

	private void DrawAdvancedFeaturesTab()
	{
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Submarine Management");
		ImGui.Separator();
		ImGui.Spacing();
		bool v = configuration.EnableSubmarineCheck;
		if (ImGui.Checkbox("Enable Submarine Monitoring", ref v))
		{
			configuration.EnableSubmarineCheck = v;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Automatically monitor submarines and pause quest rotation when submarines are ready");
		}
		if (configuration.EnableSubmarineCheck)
		{
			ImGui.Indent();
			int v2 = configuration.SubmarineCheckInterval;
			if (ImGui.SliderInt("Check Interval (seconds)", ref v2, 30, 300))
			{
				configuration.SubmarineCheckInterval = v2;
				configuration.Save();
			}
			int v3 = configuration.SubmarineReloginCooldown;
			if (ImGui.SliderInt("Cooldown after Relog (seconds)", ref v3, 60, 300))
			{
				configuration.SubmarineReloginCooldown = v3;
				configuration.Save();
			}
			int v4 = configuration.SubmarineWaitTime;
			if (ImGui.SliderInt("Wait time before submarine (seconds)", ref v4, 10, 120))
			{
				configuration.SubmarineWaitTime = v4;
				configuration.Save();
			}
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Movement Monitor");
		ImGui.Separator();
		ImGui.Spacing();
		bool v5 = configuration.EnableMovementMonitor;
		if (ImGui.Checkbox("Enable Movement Monitor", ref v5))
		{
			configuration.EnableMovementMonitor = v5;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Restarts Questionable with /qst reload followed by /qst start when the character remains stationary.");
		}
		if (configuration.EnableMovementMonitor)
		{
			ImGui.Indent();
			int v6 = configuration.MovementCheckInterval;
			if (ImGui.SliderInt("Check Interval (seconds)##movement", ref v6, 3, 30))
			{
				configuration.MovementCheckInterval = v6;
				configuration.Save();
			}
			int v7 = configuration.MovementStuckThreshold;
			if (ImGui.SliderInt("Stuck Threshold (seconds)", ref v7, 5, 120))
			{
				configuration.MovementStuckThreshold = v7;
				configuration.Save();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Time without movement before Companion applies its configured final fallback");
			}
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Standard Stop Point Rotation - Combat Handling");
		ImGui.Separator();
		ImGui.Spacing();
		bool v8 = configuration.EnableCombatHandling;
		if (ImGui.Checkbox("Enable Combat Handling", ref v8))
		{
			configuration.EnableCombatHandling = v8;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Only affects overworld combat during an active Standard Stop Point Rotation");
		}
		if (configuration.EnableCombatHandling)
		{
			ImGui.Indent();
			int v9 = configuration.CombatHPThreshold;
			if (ImGui.SliderInt("HP Threshold (%)", ref v9, 1, 99))
			{
				configuration.CombatHPThreshold = v9;
				configuration.Save();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Start the selected Stop Point combat handling when HP reaches this percentage");
			}
			if (ImGui.RadioButton("Use Default Combat Handling", configuration.StopPointCombatHandlingMode == CombatHandlingMode.DefaultBackends))
			{
				configuration.StopPointCombatHandlingMode = CombatHandlingMode.DefaultBackends;
				configuration.Save();
			}
			if (configuration.StopPointCombatHandlingMode == CombatHandlingMode.DefaultBackends)
			{
				ImGui.Indent();
				bool v10 = configuration.EnableStopPointRSR;
				if (ImGui.Checkbox("RSR##LegacyStopPointCombat", ref v10) && (v10 || configuration.EnableStopPointVBM || configuration.EnableStopPointBMRAI))
				{
					configuration.EnableStopPointRSR = v10;
					configuration.Save();
				}
				bool v11 = configuration.EnableStopPointVBM;
				if (ImGui.Checkbox("VBM##LegacyStopPointCombat", ref v11) && (v11 || configuration.EnableStopPointRSR || configuration.EnableStopPointBMRAI))
				{
					configuration.EnableStopPointVBM = v11;
					configuration.Save();
				}
				bool v12 = configuration.EnableStopPointBMRAI;
				if (ImGui.Checkbox("BMR##LegacyStopPointCombat", ref v12) && (v12 || configuration.EnableStopPointRSR || configuration.EnableStopPointVBM))
				{
					configuration.EnableStopPointBMRAI = v12;
					configuration.Save();
				}
				ImGui.Unindent();
			}
			if (ImGui.RadioButton("Use Own Commands", configuration.StopPointCombatHandlingMode == CombatHandlingMode.CustomCommands))
			{
				configuration.StopPointCombatHandlingMode = CombatHandlingMode.CustomCommands;
				configuration.Save();
			}
			if (configuration.StopPointCombatHandlingMode == CombatHandlingMode.CustomCommands)
			{
				ImGui.Indent();
				DrawCommandList("Commands when combat starts", "LegacyStopPointCombatStart", ref stopPointCombatStartCommandInput, configuration.StopPointCombatStartCommands, delegate(string commands)
				{
					configuration.StopPointCombatStartCommands = commands;
					configuration.Save();
				});
				DrawCommandList("Commands after combat is over", "LegacyStopPointCombatEnd", ref stopPointCombatEndCommandInput, configuration.StopPointCombatEndCommands, delegate(string commands)
				{
					configuration.StopPointCombatEndCommands = commands;
					configuration.Save();
				});
				ImGui.Unindent();
			}
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Solo Duty - Combat Handling");
		ImGui.Separator();
		ImGui.Spacing();
		if (ImGui.RadioButton("Use Default Combat Handling##LegacySoloDuty", configuration.SoloDutyCombatHandlingMode == CombatHandlingMode.DefaultBackends))
		{
			configuration.SoloDutyCombatHandlingMode = CombatHandlingMode.DefaultBackends;
			configuration.Save();
		}
		if (configuration.SoloDutyCombatHandlingMode == CombatHandlingMode.DefaultBackends)
		{
			ImGui.Indent();
			bool v13 = configuration.EnableSoloDutyRSR;
			if (ImGui.Checkbox("RSR##LegacySoloDuty", ref v13) && (v13 || configuration.EnableSoloDutyVBM || configuration.EnableSoloDutyBMRAI))
			{
				configuration.EnableSoloDutyRSR = v13;
				configuration.Save();
			}
			bool v14 = configuration.EnableSoloDutyVBM;
			if (ImGui.Checkbox("VBM##LegacySoloDuty", ref v14) && (v14 || configuration.EnableSoloDutyRSR || configuration.EnableSoloDutyBMRAI))
			{
				configuration.EnableSoloDutyVBM = v14;
				configuration.Save();
			}
			bool v15 = configuration.EnableSoloDutyBMRAI;
			if (ImGui.Checkbox("BMR##LegacySoloDuty", ref v15) && (v15 || configuration.EnableSoloDutyRSR || configuration.EnableSoloDutyVBM))
			{
				configuration.EnableSoloDutyBMRAI = v15;
				configuration.Save();
			}
			ImGui.Unindent();
		}
		if (ImGui.RadioButton("Use Own Commands##LegacySoloDuty", configuration.SoloDutyCombatHandlingMode == CombatHandlingMode.CustomCommands))
		{
			configuration.SoloDutyCombatHandlingMode = CombatHandlingMode.CustomCommands;
			configuration.Save();
		}
		if (configuration.SoloDutyCombatHandlingMode == CombatHandlingMode.CustomCommands)
		{
			ImGui.Indent();
			DrawCommandList("Commands when the Solo Duty starts", "LegacySoloDutyCombatStart", ref soloDutyCombatStartCommandInput, configuration.SoloDutyCombatStartCommands, delegate(string commands)
			{
				configuration.SoloDutyCombatStartCommands = commands;
				configuration.Save();
			});
			DrawCommandList("Commands after the Solo Duty is over", "LegacySoloDutyCombatEnd", ref soloDutyCombatEndCommandInput, configuration.SoloDutyCombatEndCommands, delegate(string commands)
			{
				configuration.SoloDutyCombatEndCommands = commands;
				configuration.Save();
			});
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Death Handling");
		ImGui.Separator();
		ImGui.Spacing();
		bool v16 = configuration.EnableDeathHandling;
		if (ImGui.Checkbox("Enable Death Handling", ref v16))
		{
			configuration.EnableDeathHandling = v16;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Automatically respawn when player dies");
		}
		if (configuration.EnableDeathHandling)
		{
			ImGui.Indent();
			int v17 = configuration.DeathRespawnDelay;
			if (ImGui.SliderInt("Teleport Delay (seconds)", ref v17, 1, 30))
			{
				configuration.DeathRespawnDelay = v17;
				configuration.Save();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Time to wait after respawn before teleporting back to death location");
			}
			ImGui.Spacing();
			ImGui.TextWrapped("On Death:");
			ImGui.BulletText("Detect 0% HP");
			ImGui.BulletText("Save position & territory");
			ImGui.BulletText("Auto-click SelectYesNo (respawn)");
			ImU8String text = new ImU8String(13, 1);
			text.AppendLiteral("Wait ");
			text.AppendFormatted(configuration.DeathRespawnDelay);
			text.AppendLiteral(" seconds");
			ImGui.BulletText(text);
			ImGui.BulletText("Teleport back to death location");
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Logging Settings");
		ImGui.Separator();
		ImGui.Spacing();
		bool v18 = configuration.LogToDalamud;
		if (ImGui.Checkbox("Log to Dalamud Log", ref v18))
		{
			configuration.LogToDalamud = v18;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Enable to also log to Dalamud log (can cause spam)");
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Dungeon Automation");
		ImGui.Separator();
		ImGui.Spacing();
		bool v19 = configuration.EnableAutoDutyUnsynced;
		if (ImGui.Checkbox("Enable AutoDuty Unsynced", ref v19))
		{
			configuration.EnableAutoDutyUnsynced = v19;
			configuration.Save();
			plugin.GetDungeonAutomation()?.SetDutyModeBasedOnConfig();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Enabled sets Unsync Party immediately and again at rotation start. Rotation stop/completion resets Questionable to Support.");
		}
		if (configuration.EnableAutoDutyUnsynced)
		{
			ImGui.Indent();
			int v20 = configuration.AutoDutyPartySize;
			if (ImGui.SliderInt("Party Size Check (members)", ref v20, 1, 8))
			{
				configuration.AutoDutyPartySize = v20;
				configuration.Save();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Minimum party size required before starting dungeon");
			}
			int v21 = configuration.AutoDutyMaxWaitForParty;
			if (ImGui.SliderInt("Max Wait for Party (seconds)", ref v21, 10, 120))
			{
				configuration.AutoDutyMaxWaitForParty = v21;
				configuration.Save();
			}
			int v22 = configuration.AutoDutyReInviteInterval;
			if (ImGui.SliderInt("Re-invite Interval (seconds)", ref v22, 5, 60))
			{
				configuration.AutoDutyReInviteInterval = v22;
				configuration.Save();
			}
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Quest Automation");
		ImGui.Separator();
		ImGui.Spacing();
		bool v23 = configuration.EnableStuckRotation;
		if (ImGui.Checkbox("Enable Stuck Rotation", ref v23))
		{
			configuration.EnableStuckRotation = v23;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Skips the current character after repeated stuck detections. The Movement Monitor performs recovery attempts between detections.");
		}
		if (configuration.EnableStuckRotation)
		{
			ImGui.Indent();
			int v24 = configuration.StuckRotationThreshold;
			if (ImGui.SliderInt("Stuck Threshold (Count)", ref v24, 1, 20))
			{
				configuration.StuckRotationThreshold = v24;
				configuration.Save();
			}
			ImGui.Unindent();
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Character Management");
		ImGui.Separator();
		ImGui.Spacing();
		bool v25 = configuration.EnableMultiModeAfterRotation;
		if (ImGui.Checkbox("Enable Multi-Mode after Rotation", ref v25))
		{
			configuration.EnableMultiModeAfterRotation = v25;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Enable AutoRetainer Multi-Mode after completing character rotation");
		}
		bool v26 = configuration.ReturnToHomeworldOnStopQuest;
		if (ImGui.Checkbox("Return to Homeworld on Stop Quest", ref v26))
		{
			configuration.ReturnToHomeworldOnStopQuest = v26;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Automatically return to homeworld when stop quest is completed");
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Safe Wait Settings");
		ImGui.Separator();
		ImGui.Spacing();
		bool v27 = configuration.EnableSafeWaitBeforeCharacterSwitch;
		if (ImGui.Checkbox("Enable Safe Wait Before Character Switch", ref v27))
		{
			configuration.EnableSafeWaitBeforeCharacterSwitch = v27;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Wait for character to stabilize (movement, actions) before switching");
		}
		bool v28 = configuration.EnableSafeWaitAfterCharacterSwitch;
		if (ImGui.Checkbox("Enable Safe Wait After Character Switch", ref v28))
		{
			configuration.EnableSafeWaitAfterCharacterSwitch = v28;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Wait for character to fully load after switching");
		}
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "DC Travel World Selector");
		ImGui.Separator();
		ImGui.Spacing();
		ImGui.TextWrapped("Configure world travel for Data Center travel quests. Requires Lifestream plugin.");
		ImGui.Spacing();
		string[] array = configuration.WorldsByDatacenter.Keys.ToArray();
		int currentItem = Array.IndexOf(array, configuration.SelectedDatacenter);
		if (currentItem < 0)
		{
			currentItem = 0;
		}
		ImGui.Text("Select Datacenter:");
		if (ImGui.Combo("##DCSelector", ref currentItem, array, array.Length))
		{
			configuration.SelectedDatacenter = array[currentItem];
			if (configuration.WorldsByDatacenter.TryGetValue(configuration.SelectedDatacenter, out List<string> value) && value.Count > 0)
			{
				configuration.DCTravelWorld = value[0];
			}
			configuration.Save();
		}
		ImGui.Spacing();
		if (configuration.WorldsByDatacenter.TryGetValue(configuration.SelectedDatacenter, out List<string> value2))
		{
			string[] array2 = value2.ToArray();
			int currentItem2 = Array.IndexOf(array2, configuration.DCTravelWorld);
			if (currentItem2 < 0)
			{
				currentItem2 = 0;
			}
			ImGui.Text("Select Target World:");
			if (ImGui.Combo("##WorldSelector", ref currentItem2, array2, array2.Length))
			{
				configuration.DCTravelWorld = array2[currentItem2];
				configuration.EnableDCTravel = !string.IsNullOrEmpty(configuration.DCTravelWorld);
				configuration.Save();
			}
			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();
			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();
			LifestreamIPC lifestreamIPC = Plugin.Instance?.LifestreamIPC;
			if (lifestreamIPC != null && !lifestreamIPC.IsAvailable)
			{
				lifestreamIPC.ForceCheckAvailability();
			}
			bool flag = lifestreamIPC?.IsAvailable ?? false;
			if (!flag)
			{
				ImGui.BeginDisabled();
			}
			bool v29 = configuration.EnableDCTravel;
			if (ImGui.Checkbox("Enable DC Travel", ref v29))
			{
				configuration.EnableDCTravel = v29;
				configuration.Save();
			}
			ImGui.Spacing();
			LifestreamCommandType lifestreamCommand = configuration.LifestreamCommand;
			ImGui.Text("Lifestream Command (Return/Skip):");
			if (ImGui.BeginCombo("##LifestreamCmd", lifestreamCommand.ToString()))
			{
				foreach (LifestreamCommandType value3 in Enum.GetValues(typeof(LifestreamCommandType)))
				{
					bool flag2 = lifestreamCommand == value3;
					if (ImGui.Selectable(value3.ToString(), flag2))
					{
						configuration.LifestreamCommand = value3;
						configuration.Save();
					}
					if (flag2)
					{
						ImGui.SetItemDefaultFocus();
					}
				}
				ImGui.EndCombo();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Command to execute when returning to homeworld or skipping character.\nAuto: /li auto\nLi: /li\nNone: No command (relies on manual travel or other plugins)");
			}
			if (!flag)
			{
				ImGui.EndDisabled();
			}
			if (ImGui.IsItemHovered())
			{
				if (!flag)
				{
					ImGui.SetTooltip("Lifestream plugin is required for DC Travel!\nPlease install and enable Lifestream to use this feature.");
				}
				else
				{
					ImGui.SetTooltip("Enable automatic DC travel when DC travel quests are detected");
				}
			}
			if (!flag)
			{
				ImGui.Spacing();
				ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "⚠\ufe0f Lifestream plugin not available!");
				ImGui.TextWrapped("DC Travel requires Lifestream to be installed and enabled.");
			}
			ImGui.Spacing();
			if (configuration.EnableDCTravel && !string.IsNullOrEmpty(configuration.DCTravelWorld))
			{
				Vector4 col = new Vector4(0.2f, 1f, 0.2f, 1f);
				ImU8String text2 = new ImU8String(21, 1);
				text2.AppendLiteral("✓ DC Travel ACTIVE → ");
				text2.AppendFormatted(configuration.DCTravelWorld);
				ImGui.TextColored(in col, text2);
				ImU8String text3 = new ImU8String(100, 1);
				text3.AppendLiteral("Character will travel to ");
				text3.AppendFormatted(configuration.DCTravelWorld);
				text3.AppendLiteral(" immediately after login, then return to homeworld before character switch.");
				ImGui.TextWrapped(text3);
			}
			else if (!configuration.EnableDCTravel)
			{
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "○ DC Travel disabled");
			}
			else
			{
				ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "⚠ DC Travel enabled but no world selected!");
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "No worlds available for selected datacenter");
		}
	}

	private static List<string> ParseSavedCommands(string commands)
	{
		return (from command in (commands ?? string.Empty).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			select command.Trim() into command
			where command.Length > 0
			select command).ToList();
	}

	private static void DrawCommandList(string label, string id, ref string input, string savedCommands, Action<string> save)
	{
		ImGui.TextUnformatted(label + ":");
		List<string> list = ParseSavedCommands(savedCommands);
		if (list.Count == 0)
		{
			ImGui.TextDisabled("No commands saved.");
		}
		else
		{
			for (int i = 0; i < list.Count; i++)
			{
				ImU8String strId = new ImU8String(1, 2);
				strId.AppendFormatted(id);
				strId.AppendLiteral("_");
				strId.AppendFormatted(i);
				ImGui.PushID(strId);
				ImGui.TextUnformatted(list[i]);
				ImGui.SameLine();
				if (ImGui.SmallButton("Remove"))
				{
					list.RemoveAt(i);
					save(string.Join(Environment.NewLine, list));
					ImGui.PopID();
					break;
				}
				ImGui.PopID();
			}
		}
		ImGui.SetNextItemWidth(-1f);
		ImU8String label2 = new ImU8String(7, 1);
		label2.AppendLiteral("##");
		label2.AppendFormatted(id);
		label2.AppendLiteral("Input");
		if (ImGui.InputText(label2, ref input, 512, ImGuiInputTextFlags.EnterReturnsTrue))
		{
			string text = input.Trim();
			if (text.Length > 0)
			{
				list.Add(text);
				save(string.Join(Environment.NewLine, list));
				input = string.Empty;
			}
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Type one command and press Enter to save it as a separate command entry.");
		}
	}
}
