using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.Reflection;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Data;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;
using QuestionableCompanion.Services;

namespace QuestionableCompanion.Windows;

public class NewMainWindow : Window, IDisposable
{
	private class Particle
	{
		public Vector2 Position;

		public Vector2 Velocity;

		public float Size;

		public float Alpha;

		public Vector4 Color;
	}

	private sealed record DependencyEntry(string Feature, string Name, string InternalName, string RepositoryUrl, bool IsStub = false);

	private sealed record HuntLogSnapshotDisplayCacheEntry(DateTime LastUpdatedUtc, uint ClassJobId, uint SelectedCombatJobId, int Level, int ClassLogRank, int GrandCompanyRank, int GrandCompanyLogRank, string Text);

	private sealed record XadbImportResult(XadbProgressReadSummary ReadSummary, int MatchedCharacters, string Status);

	private string classUnlockUiMessage = string.Empty;

	private const string KoFiUrl = "https://ko-fi.com/macarondream";

	private static readonly string DisplayVersion;

	private readonly Plugin plugin;

	private readonly AutoRetainerIPC autoRetainerIpc;

	private readonly QuestTrackingService questTrackingService;

	private readonly QuestRotationExecutionService questRotationService;

	private readonly EventQuestExecutionService eventQuestService;

	private readonly AlliedSocietyRotationService alliedSocietyRotationService;

	private readonly AlliedSocietyPriorityWindow alliedSocietyPriorityWindow;

	private readonly DataCenterService dataCenterService;

	private readonly MSQProgressionService msqProgressionService;

	private readonly HuntLogAutomationService huntLogAutomationService;

	private readonly XADatabaseIPC xadbIpc;

	private readonly RetainerCreationService retainerCreationService;

	private readonly ClassUnlockRotationService classUnlockRotationService;

	private readonly Configuration configuration;

	private readonly IPluginLog log;

	private readonly IUiBuilder uiBuilder;

	private readonly IDataManager dataManager;

	private readonly IClientState clientState;

	private readonly IObjectTable objectTable;

	private readonly Vector4 colorPrimary = new Vector4(0.478f, 0.686f, 0.878f, 1f);

	private readonly Vector4 colorSecondary = new Vector4(0.949f, 0.769f, 0.388f, 1f);

	private readonly Vector4 colorAccent = new Vector4(0.729f, 0.294f, 0.184f, 1f);

	private readonly Vector4 colorSuccess = new Vector4(0.298f, 0.788f, 0.659f, 1f);

	private readonly Vector4 colorDarkButtonText = new Vector4(0.06f, 0.05f, 0.03f, 1f);

	private readonly Vector4 colorDarkBg = new Vector4(0.12f, 0.12f, 0.12f, 1f);

	private readonly Vector4 colorSidebarBg = new Vector4(0.08f, 0.08f, 0.08f, 1f);

	private readonly Vector4 colorHover = new Vector4(0.2f, 0.2f, 0.2f, 1f);

	private float animTime;

	private float glowPulse;

	private float particleTime;

	private List<Particle> particles = new List<Particle>();

	private int selectedTab;

	private int selectedDCFilter;

	private bool charactersExpanded = true;

	private bool menuExpanded = true;

	private bool rotationsMenuExpanded = true;

	private bool toolsMenuExpanded;

	private bool pluginMenuExpanded;

	private bool isMinimized;

	private static readonly Vector2 NormalMinWindowSize;

	private static readonly Vector2 MinimizedWindowSize;

	private Vector2 lastNormalWindowSize = NormalMinWindowSize;

	private List<string> registeredCharacters = new List<string>();

	private Dictionary<string, bool> characterSelection = new Dictionary<string, bool>();

	private Dictionary<string, CharacterProgressInfo> characterProgressCache = new Dictionary<string, CharacterProgressInfo>();

	private Dictionary<string, int> characterGrandCompanyRankFilterCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, XadbRetainerSnapshot> xadbRetainerSnapshots = new Dictionary<string, XadbRetainerSnapshot>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, ulong> characterContentIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

	private DateTime lastCharacterRefresh = DateTime.MinValue;

	private bool initialCharacterLoadComplete;

	private DateTime initialLoadStartTime = DateTime.MinValue;

	private int characterLoadAttempts;

	private const int MaxCharacterLoadAttempts = 5;

	private readonly int[] retryDelaysSeconds = new int[5] { 1, 2, 4, 8, 15 };

	private string stopPointTransferStatus = string.Empty;

	private bool stopPointTransferSucceeded;

	private int? draggedStopPointIndex;

	private bool warningMenuRetryCycleComplete;

	private DateTime warningMenuRetryStartTime = DateTime.MinValue;

	private int warningMenuRetryAttempts;

	private readonly int[] warningMenuRetryDelaysSeconds = new int[4] { 1, 2, 4, 8 };

	private Dictionary<string, List<string>> charactersByDataCenter = new Dictionary<string, List<string>>();

	private List<string> availableDataCenters = new List<string> { "All", "EU", "NA", "JP", "OCE" };

	private bool showSelectWorldDialog;

	private bool showDeselectWorldDialog;

	private string selectedWorldForBulkAction = "";

	private List<string> availableWorlds = new List<string>();

	private string selectedEventQuestId = "";

	private List<(string QuestId, string QuestName)> availableEventQuests = new List<(string, string)>();

	private List<string> resolvedPrerequisites = new List<string>();

	private int eventQuestViewMode;

	private DateTime lastEventQuestRefresh = DateTime.MinValue;

	private string? newLANHelperIP;

	private readonly Dictionary<string, List<string>> dataCenterWorlds = new Dictionary<string, List<string>>
	{
		{
			"Aether",
			new List<string> { "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren" }
		},
		{
			"Primal",
			new List<string> { "Behemoth", "Excalibur", "Exodus", "Hyperion", "Lamia", "Leviathan", "Ultros" }
		},
		{
			"Crystal",
			new List<string> { "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera" }
		},
		{
			"Dynamis",
			new List<string> { "Halicarnassus", "Maduin", "Marilith", "Seraph" }
		},
		{
			"Chaos",
			new List<string> { "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan" }
		},
		{
			"Light",
			new List<string> { "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark" }
		},
		{
			"Materia",
			new List<string> { "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan" }
		},
		{
			"Elemental",
			new List<string> { "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon" }
		},
		{
			"Gaia",
			new List<string> { "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima" }
		},
		{
			"Mana",
			new List<string> { "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan" }
		},
		{
			"Meteor",
			new List<string> { "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus" }
		}
	};

	private string selectedDataCenter = "";

	private string selectedWorld = "";

	private static readonly IReadOnlyList<DependencyEntry> DependencyEntries;

	private readonly Dictionary<string, HuntLogSnapshotDisplayCacheEntry> huntLogSnapshotDisplayCache = new Dictionary<string, HuntLogSnapshotDisplayCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private HuntLogMode selectedHuntLogMode = HuntLogMode.All;

	private string huntLogMountSearch = string.Empty;

	private string[]? huntLogMountNames;

	private (uint Id, string Label)[]? huntLogCombatJobOptions;

	private static readonly string[] HuntLogCompanionStances;

	private const int CurrentInitialSetupVersion = 1;

	private static readonly string[] RequiredSetupDependencies;

	private readonly ConcurrentDictionary<string, byte> setupInstallingDependencies = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

	private int initialSetupStep;

	private string initialSetupStatus = string.Empty;

	private bool initialSetupStatusSucceeded;

	private DateTime initialSetupLastDependencyCheck = DateTime.MinValue;

	private bool initialSetupQuestionableReady;

	private bool returnToInitialSetupFromMultiboxing;

	private string retainerUiMessage = string.Empty;

	private bool retainerSamplesInitialized;

	private uint retainerBulkJobId = 1u;

	private string stopPointCombatStartCommandInput = string.Empty;

	private string stopPointCombatEndCommandInput = string.Empty;

	private string soloDutyCombatStartCommandInput = string.Empty;

	private string soloDutyCombatEndCommandInput = string.Empty;

	public bool IsMinimized => isMinimized;

	public Vector2 MinimumWindowSize
	{
		get
		{
			if (!isMinimized)
			{
				return NormalMinWindowSize;
			}
			return MinimizedWindowSize;
		}
	}

	public Vector2? ForcedWindowSize
	{
		get
		{
			if (!isMinimized)
			{
				return null;
			}
			return MinimizedWindowSize;
		}
	}

	private void DrawAlliedSocietyTab()
	{
		ImGui.TextColored(in colorSecondary, "Allied Society Rotation");
		ImGui.TextWrapped("Quest selection, allowance limits, and overcap-safe ordering are provided by Questionable. Companion only controls character and society priority.");
		ImGui.Separator();
		if (ImGui.Button("Configure Priorities"))
		{
			alliedSocietyPriorityWindow.IsOpen = true;
		}
		ImGui.SameLine();
		ImGui.TextDisabled("(Use Up/Down buttons to reorder)");
		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextColored(in colorSecondary, "Quest Selection Mode");
		AlliedSocietyConfiguration rotationConfig = plugin.Configuration.AlliedSociety.RotationConfig;
		bool flag = false;
		if (ImGui.RadioButton("Only 3 Quests per Society", rotationConfig.QuestMode == AlliedSocietyQuestMode.OnlyThreePerSociety))
		{
			rotationConfig.QuestMode = AlliedSocietyQuestMode.OnlyThreePerSociety;
			flag = true;
		}
		if (ImGui.RadioButton("All Available Quests (until 0 allowances)", rotationConfig.QuestMode == AlliedSocietyQuestMode.AllAvailableQuests))
		{
			rotationConfig.QuestMode = AlliedSocietyQuestMode.AllAvailableQuests;
			flag = true;
		}
		if (flag)
		{
			plugin.Configuration.Save();
		}
		ImGui.Spacing();
		ImGui.Separator();
		List<string> list = (from kvp in characterSelection
			where kvp.Value
			select kvp.Key).ToList();
		ImGui.TextColored(in colorSecondary, "Rotation Control");
		if (alliedSocietyRotationService.IsRotationActive)
		{
			if (ImGui.Button("Stop Rotation", new Vector2(150f, 30f)))
			{
				alliedSocietyRotationService.StopRotation();
			}
			ImGui.SameLine();
			Vector4 col = ImGuiColors.DalamudYellow;
			ImU8String text = new ImU8String(18, 1);
			text.AppendLiteral("Running... Phase: ");
			text.AppendFormatted(alliedSocietyRotationService.CurrentPhase);
			ImGui.TextColored(in col, text);
			ImU8String text2 = new ImU8String(19, 1);
			text2.AppendLiteral("Current Character: ");
			text2.AppendFormatted(alliedSocietyRotationService.CurrentCharacterId);
			ImGui.Text(text2);
		}
		else
		{
			if (ImGui.Button("Start Rotation", new Vector2(150f, 30f)))
			{
				if (list.Count == 0)
				{
					ImGui.OpenPopup("NoCharactersSelected");
				}
				else
				{
					alliedSocietyRotationService.StartRotation(list);
				}
			}
			if (ImGui.BeginPopup("NoCharactersSelected"))
			{
				ImGui.Text("Please select at least one character from the Characters tab.");
				if (ImGui.Button("OK", new Vector2(120f, 0f)))
				{
					ImGui.CloseCurrentPopup();
				}
				ImGui.EndPopup();
			}
		}
		ImGui.Spacing();
		ImGui.Separator();
		ref readonly Vector4 col2 = ref colorSecondary;
		ImU8String text3 = new ImU8String(28, 1);
		text3.AppendLiteral("Character Status (");
		text3.AppendFormatted(list.Count);
		text3.AppendLiteral(" selected)");
		ImGui.TextColored(in col2, text3);
		if (list.Count > 0 && ImGui.BeginTable("AlliedSocietyStatusTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
		{
			ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 150f);
			ImGui.TableSetupColumn("Allowances", ImGuiTableColumnFlags.WidthFixed, 100f);
			ImGui.TableHeadersRow();
			foreach (string item in list)
			{
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				if (item == alliedSocietyRotationService.CurrentCharacterId)
				{
					ImGui.TextColored(ImGuiColors.DalamudYellow, item);
				}
				else
				{
					ImGui.Text(item);
				}
				ImGui.TableNextColumn();
				AlliedSocietyCharacterStatus alliedSocietyCharacterStatus = (plugin.Configuration.AlliedSociety.CharacterStatuses.ContainsKey(item) ? plugin.Configuration.AlliedSociety.CharacterStatuses[item] : new AlliedSocietyCharacterStatus
				{
					CharacterId = item
				});
				ImGui.TextColored((alliedSocietyCharacterStatus.Status == AlliedSocietyRotationStatus.Complete) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite, alliedSocietyCharacterStatus.Status.ToString());
				ImGui.TableNextColumn();
				ImGui.Text("-");
			}
			ImGui.EndTable();
		}
		else if (list.Count == 0)
		{
			ImGui.TextColored(ImGuiColors.DalamudGrey, "No characters selected. Please select characters in the Characters tab.");
		}
	}

	private void DrawClassUnlocksTab()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Classes Unlock");
		ImGui.PopStyleColor();
		ImGui.TextWrapped("Unlocks the selected level-one classes/jobs for every character selected in the Characters tab. It never levels them or runs later class/job quests. Targets are grouped by city to avoid unnecessary travel.");
		ImGui.Spacing();
		ClassUnlockRunState state = classUnlockRotationService.State;
		ImGui.BeginDisabled(state.IsRunning);
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Targets");
		ImGui.PopStyleColor();
		float num = ImGui.CalcTextSize("Clear selection").X + ImGui.GetStyle().FramePadding.X * 2f;
		ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - num));
		if (ImGui.SmallButton("Clear selection"))
		{
			configuration.ClassUnlocks.SelectedClassJobIds.Clear();
			configuration.ClassUnlocks.SwitchToClassJobIdByLevel.Clear();
			configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.Clear();
			configuration.ClassUnlocks.SwitchToClassJobId = 0u;
			configuration.Save();
		}
		ImGui.Spacing();
		bool v = configuration.ClassUnlocks.UnlockDuringStopPointRotation;
		int num2 = (v ? new int[4] { 50, 60, 70, 80 }.Count((int num6) => ClassUnlockCatalog.Targets.Any((ClassUnlockTargetDefinition target) => target.CanContinueStopPointRotation && target.RequiredCombatLevel == num6 && configuration.ClassUnlocks.SelectedClassJobIds.Contains(target.ClassJobId))) : 0);
		float num3 = ((state.Results.Count == 0) ? 125f : 310f) + (float)num2 * 28f;
		float y = Math.Clamp(ImGui.GetContentRegionAvail().Y - num3, 245f, 390f);
		using (ImRaii.ImChild imChild = ImRaii.Child("ClassUnlockTargets", new Vector2(0f, y), border: true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
		{
			if (imChild.Success)
			{
				DrawClassUnlockCategory("Special jobs", ClassUnlockCategory.Special);
				DrawClassUnlockCategory("ARR base combat classes", ClassUnlockCategory.ArrCombat);
				DrawClassUnlockCategory("Disciples of the Hand / Land", ClassUnlockCategory.CraftingGathering);
				DrawClassUnlockCategory("Expansion jobs", ClassUnlockCategory.Expansion);
			}
		}
		if (ImGui.Checkbox("Unlock during Stop Point rotation", ref v))
		{
			configuration.ClassUnlocks.UnlockDuringStopPointRotation = v;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("After every completed quest, pause before the next quest and unlock newly eligible selected targets.\nThe current character continues toward the same Stop Point afterwards.");
		}
		if (v)
		{
			ClassUnlockTargetDefinition[] array = (from target in ClassUnlockCatalog.Targets
				where target.CanContinueStopPointRotation && configuration.ClassUnlocks.SelectedClassJobIds.Contains(target.ClassJobId)
				orderby target.RequiredCombatLevel
				select target).ThenBy<ClassUnlockTargetDefinition, string>((ClassUnlockTargetDefinition target) => target.Abbreviation, StringComparer.OrdinalIgnoreCase).ToArray();
			NormalizeClassUnlockSwitchTargets();
			if (array.Length != 0)
			{
				ImGui.TextDisabled("Continue after unlocking");
				int[] array2 = new int[4] { 50, 60, 70, 80 };
				foreach (int level in array2)
				{
					if (!array.Any((ClassUnlockTargetDefinition target) => target.RequiredCombatLevel == level))
					{
						continue;
					}
					ClassUnlockTargetDefinition[] array3 = array.Where((ClassUnlockTargetDefinition target) => target.RequiredCombatLevel <= level).ToArray();
					configuration.ClassUnlocks.SwitchToClassJobIdByLevel.TryGetValue(level, out var selectedId);
					ClassUnlockTargetDefinition classUnlockTargetDefinition = array3.FirstOrDefault((ClassUnlockTargetDefinition target) => target.ClassJobId == selectedId);
					string text = classUnlockTargetDefinition?.Name ?? "Keep Current Class";
					ImU8String text2 = new ImU8String(7, 1);
					text2.AppendLiteral("Level ");
					text2.AppendFormatted(level);
					text2.AppendLiteral(":");
					ImGui.TextDisabled(text2);
					ImGui.SameLine(90f);
					ImGui.SetNextItemWidth(250f);
					ImU8String label = new ImU8String(32, 1);
					label.AppendLiteral("##ClassUnlockRotationSwitchLevel");
					label.AppendFormatted(level);
					if (ImGui.BeginCombo(label, text))
					{
						if (ImGui.Selectable("Keep Current Class", classUnlockTargetDefinition == null))
						{
							configuration.ClassUnlocks.SwitchToClassJobIdByLevel.Remove(level);
							configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.Remove(level);
							configuration.ClassUnlocks.SwitchToClassJobId = 0u;
							configuration.Save();
						}
						ClassUnlockTargetDefinition[] array4 = array3;
						foreach (ClassUnlockTargetDefinition classUnlockTargetDefinition2 in array4)
						{
							ImU8String label2 = new ImU8String(3, 2);
							label2.AppendFormatted(classUnlockTargetDefinition2.Name);
							label2.AppendLiteral(" (");
							label2.AppendFormatted(classUnlockTargetDefinition2.Abbreviation);
							label2.AppendLiteral(")");
							if (ImGui.Selectable(label2, selectedId == classUnlockTargetDefinition2.ClassJobId))
							{
								configuration.ClassUnlocks.SwitchToClassJobIdByLevel[level] = classUnlockTargetDefinition2.ClassJobId;
								configuration.ClassUnlocks.SwitchToClassJobId = 0u;
								configuration.Save();
							}
						}
						ImGui.EndCombo();
					}
					if (!(classUnlockTargetDefinition != null))
					{
						continue;
					}
					ImGui.SameLine();
					int value;
					bool v2 = configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.TryGetValue(level, out value);
					ImU8String label3 = new ImU8String(58, 1);
					label3.AppendLiteral("Stay on current job if it is level##ClassUnlockKeepCurrent");
					label3.AppendFormatted(level);
					if (ImGui.Checkbox(label3, ref v2))
					{
						if (v2)
						{
							value = level;
							configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier[level] = value;
						}
						else
						{
							configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.Remove(level);
						}
						configuration.Save();
					}
					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("If the character's current job is at this level or higher, the Stop Point rotation stays on that job.\nThe selected new job is still unlocked, equipped with recommended gear, and saved as a gearset.");
					}
					if (v2)
					{
						ImGui.SameLine();
						ImGui.SetNextItemWidth(55f);
						ImU8String label4 = new ImU8String(29, 1);
						label4.AppendLiteral("##ClassUnlockKeepCurrentLevel");
						label4.AppendFormatted(level);
						if (ImGui.InputInt(label4, ref value))
						{
							configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier[level] = Math.Clamp(value, 1, 100);
							configuration.Save();
						}
						ImGui.SameLine();
						ImGui.TextUnformatted("or higher");
					}
				}
			}
		}
		ImGui.EndDisabled();
		int value2 = characterSelection.Count<KeyValuePair<string, bool>>((KeyValuePair<string, bool> entry) => entry.Value);
		int count = configuration.ClassUnlocks.SelectedClassJobIds.Count;
		ImU8String text3 = new ImU8String(26, 2);
		text3.AppendLiteral("Characters: ");
		text3.AppendFormatted(value2);
		text3.AppendLiteral("  |  Targets: ");
		text3.AppendFormatted(count);
		ImGui.TextUnformatted(text3);
		ImGui.TextDisabled("Order per character: current city first, then Limsa Lominsa, Gridania, Ul'dah and Ishgard.");
		if (state.IsRunning)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
			if (ImGui.Button("Stop Class Unlock", new Vector2(170f, 30f)))
			{
				classUnlockRotationService.Stop();
			}
			ImGui.PopStyleColor();
		}
		else if (ImGui.Button("Start Class Unlock", new Vector2(170f, 30f)))
		{
			IEnumerable<string> characters = from entry in characterSelection
				where entry.Value
				select entry.Key;
			if (!classUnlockRotationService.TryStart(characters, configuration.ClassUnlocks.SelectedClassJobIds, out string error))
			{
				classUnlockUiMessage = error;
			}
			else
			{
				classUnlockUiMessage = string.Empty;
			}
		}
		if (!string.IsNullOrWhiteSpace(classUnlockUiMessage))
		{
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImGui.TextWrapped(classUnlockUiMessage);
			ImGui.PopStyleColor();
		}
		state = classUnlockRotationService.State;
		ImGui.Spacing();
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Status");
		ImGui.PopStyleColor();
		ImU8String text4 = new ImU8String(2, 2);
		text4.AppendFormatted(state.Phase);
		text4.AppendLiteral(": ");
		text4.AppendFormatted(state.Status);
		ImGui.TextWrapped(text4);
		if (!string.IsNullOrWhiteSpace(state.CurrentCharacter))
		{
			ImU8String text5 = new ImU8String(11, 2);
			text5.AppendFormatted(state.CurrentCharacter);
			text5.AppendLiteral("  |  Quest ");
			text5.AppendFormatted(state.CurrentQuestId);
			ImGui.TextDisabled(text5);
		}
		if (state.Results.Count == 0 || !ImGui.BeginTable("ClassUnlockResults", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(0f, 170f)))
		{
			return;
		}
		ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.4f);
		ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 60f);
		ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 105f);
		ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 2f);
		ImGui.TableHeadersRow();
		foreach (ClassUnlockTargetResult result in state.Results)
		{
			ClassUnlockTargetDefinition classUnlockTargetDefinition3 = ClassUnlockCatalog.Find(result.ClassJobId);
			ImGui.TableNextRow();
			ImGui.TableSetColumnIndex(0);
			ImGui.TextUnformatted(result.Character);
			ImGui.TableSetColumnIndex(1);
			ImGui.TextUnformatted(classUnlockTargetDefinition3?.Abbreviation ?? result.ClassJobId.ToString());
			ImGui.TableSetColumnIndex(2);
			ImGui.TextUnformatted(result.Kind.ToString());
			ImGui.TableSetColumnIndex(3);
			ImGui.TextWrapped(result.Message);
		}
		ImGui.EndTable();
	}

	private void DrawClassUnlockCategory(string label, ClassUnlockCategory category)
	{
		ClassUnlockTargetDefinition[] array = ClassUnlockCatalog.Targets.Where((ClassUnlockTargetDefinition target) => target.Category == category).OrderBy<ClassUnlockTargetDefinition, string>((ClassUnlockTargetDefinition target) => target.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		uint[] array2 = (from target in array
			where target.IsAvailable
			select target.ClassJobId).ToArray();
		int num = array2.Count(configuration.ClassUnlocks.SelectedClassJobIds.Contains);
		bool v = array2.Length != 0 && num == array2.Length;
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImU8String label2 = new ImU8String(21, 2);
		label2.AppendFormatted(label);
		label2.AppendLiteral("##ClassUnlockCategory");
		label2.AppendFormatted(category);
		if (ImGui.Checkbox(label2, ref v))
		{
			SetClassUnlockCategory(category, v);
		}
		ImGui.PopStyleColor();
		ImGui.SameLine();
		ImU8String text = new ImU8String(1, 2);
		text.AppendFormatted(num);
		text.AppendLiteral("/");
		text.AppendFormatted(array2.Length);
		ImGui.TextDisabled(text);
		int column = Math.Clamp((int)(MathF.Max(1f, ImGui.GetContentRegionAvail().X) / 145f), 2, 6);
		ImU8String strId = new ImU8String(23, 1);
		strId.AppendLiteral("ClassUnlockCategoryGrid");
		strId.AppendFormatted(category);
		if (ImGui.BeginTable(strId, column, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
		{
			ClassUnlockTargetDefinition[] array3 = array;
			foreach (ClassUnlockTargetDefinition classUnlockTargetDefinition in array3)
			{
				ImGui.TableNextColumn();
				bool v2 = configuration.ClassUnlocks.SelectedClassJobIds.Contains(classUnlockTargetDefinition.ClassJobId);
				ImGui.BeginDisabled(!classUnlockTargetDefinition.IsAvailable);
				ImU8String label3 = new ImU8String(13, 1);
				label3.AppendLiteral("##ClassUnlock");
				label3.AppendFormatted(classUnlockTargetDefinition.ClassJobId);
				if (ImGui.Checkbox(label3, ref v2))
				{
					if (v2)
					{
						configuration.ClassUnlocks.SelectedClassJobIds.Add(classUnlockTargetDefinition.ClassJobId);
					}
					else
					{
						configuration.ClassUnlocks.SelectedClassJobIds.Remove(classUnlockTargetDefinition.ClassJobId);
					}
					configuration.ClassUnlocks.SelectedClassJobIds = configuration.ClassUnlocks.SelectedClassJobIds.Distinct().ToList();
					NormalizeClassUnlockSwitchTargets();
					configuration.Save();
				}
				ImGui.SameLine();
				DrawGameIcon(62100 + classUnlockTargetDefinition.ClassJobId, 22f);
				ImGui.SameLine();
				ImGui.TextUnformatted(classUnlockTargetDefinition.Abbreviation);
				ImGui.EndDisabled();
				if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				{
					ImU8String tooltip = new ImU8String(12, 3);
					tooltip.AppendFormatted(classUnlockTargetDefinition.Name);
					tooltip.AppendLiteral("\n");
					tooltip.AppendFormatted(classUnlockTargetDefinition.Requirement);
					tooltip.AppendLiteral("\nStarts in ");
					tooltip.AppendFormatted(FormatClassUnlockHub(classUnlockTargetDefinition.Hub));
					ImGui.SetTooltip(tooltip);
				}
			}
			ImGui.EndTable();
		}
		ImGui.Spacing();
	}

	private void SetClassUnlockCategory(ClassUnlockCategory category, bool selected)
	{
		HashSet<uint> hashSet = (from target in ClassUnlockCatalog.Targets
			where target.Category == category && target.IsAvailable
			select target.ClassJobId).ToHashSet();
		if (selected)
		{
			configuration.ClassUnlocks.SelectedClassJobIds.AddRange(hashSet);
		}
		else
		{
			configuration.ClassUnlocks.SelectedClassJobIds.RemoveAll(hashSet.Contains);
		}
		configuration.ClassUnlocks.SelectedClassJobIds = configuration.ClassUnlocks.SelectedClassJobIds.Distinct().ToList();
		NormalizeClassUnlockSwitchTargets();
		configuration.Save();
	}

	private void NormalizeClassUnlockSwitchTargets()
	{
		KeyValuePair<int, uint>[] array = configuration.ClassUnlocks.SwitchToClassJobIdByLevel.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<int, uint> keyValuePair = array[i];
			ClassUnlockTargetDefinition classUnlockTargetDefinition = ClassUnlockCatalog.Find(keyValuePair.Value);
			bool flag;
			switch (keyValuePair.Key)
			{
			case 50:
			case 60:
			case 70:
			case 80:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (!flag || (object)classUnlockTargetDefinition == null || !classUnlockTargetDefinition.CanContinueStopPointRotation || classUnlockTargetDefinition.RequiredCombatLevel > keyValuePair.Key || !configuration.ClassUnlocks.SelectedClassJobIds.Contains(keyValuePair.Value))
			{
				configuration.ClassUnlocks.SwitchToClassJobIdByLevel.Remove(keyValuePair.Key);
			}
		}
		int[] array2 = configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.Keys.ToArray();
		foreach (int num in array2)
		{
			bool flag;
			switch (num)
			{
			case 50:
			case 60:
			case 70:
			case 80:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (!flag || !configuration.ClassUnlocks.SwitchToClassJobIdByLevel.ContainsKey(num))
			{
				configuration.ClassUnlocks.KeepCurrentClassAtLevelByUnlockTier.Remove(num);
			}
		}
		configuration.ClassUnlocks.SwitchToClassJobId = 0u;
	}

	private static string FormatClassUnlockHub(ClassUnlockHub hub)
	{
		return hub switch
		{
			ClassUnlockHub.LimsaLominsa => "Limsa Lominsa", 
			ClassUnlockHub.Gridania => "Gridania", 
			ClassUnlockHub.Uldah => "Ul'dah", 
			ClassUnlockHub.Ishgard => "Ishgard", 
			_ => hub.ToString(), 
		};
	}

	public NewMainWindow(Plugin plugin, AutoRetainerIPC autoRetainerIpc, QuestTrackingService questTrackingService, QuestRotationExecutionService questRotationService, EventQuestExecutionService eventQuestService, AlliedSocietyRotationService alliedSocietyRotationService, AlliedSocietyPriorityWindow alliedSocietyPriorityWindow, DataCenterService dataCenterService, MSQProgressionService msqProgressionService, Configuration configuration, IPluginLog log, IUiBuilder uiBuilder, IDataManager dataManager, IClientState clientState, IObjectTable objectTable, HuntLogAutomationService huntLogAutomationService, XADatabaseIPC xadbIpc, RetainerCreationService retainerCreationService, ClassUnlockRotationService classUnlockRotationService)
		: base("Questionable Companion##NewMainWindow", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground)
	{
		this.plugin = plugin;
		this.autoRetainerIpc = autoRetainerIpc;
		this.questTrackingService = questTrackingService;
		this.questRotationService = questRotationService;
		this.eventQuestService = eventQuestService;
		this.alliedSocietyRotationService = alliedSocietyRotationService;
		this.alliedSocietyPriorityWindow = alliedSocietyPriorityWindow;
		this.dataCenterService = dataCenterService;
		this.msqProgressionService = msqProgressionService;
		this.huntLogAutomationService = huntLogAutomationService;
		this.xadbIpc = xadbIpc;
		this.retainerCreationService = retainerCreationService;
		this.classUnlockRotationService = classUnlockRotationService;
		this.configuration = configuration;
		this.log = log;
		this.uiBuilder = uiBuilder;
		this.dataManager = dataManager;
		this.clientState = clientState;
		this.objectTable = objectTable;
		selectedDCFilter = Math.Max(0, availableDataCenters.FindIndex((string dataCenter) => string.Equals(dataCenter, configuration.CharacterFilters.DataCenter, StringComparison.OrdinalIgnoreCase)));
		selectedTab = ((configuration.CompletedInitialSetupVersion < 1 && configuration.DismissedInitialSetupVersion < 1) ? 17 : selectedDCFilter);
		base.BgAlpha = 0f;
		try
		{
			dataCenterService.InitializeWorldMapping();
		}
		catch (Exception ex)
		{
			log.Error("[NewMainWindow] Failed to initialize data center mapping: " + ex.Message);
		}
		initialLoadStartTime = DateTime.Now;
		log.Information("[NewMainWindow] Delayed character loading started (will retry with exponential backoff)");
		Random random = new Random();
		for (int num = 0; num < 80; num++)
		{
			Vector4 color = random.Next(3) switch
			{
				0 => colorPrimary, 
				1 => colorSecondary, 
				_ => colorAccent, 
			};
			particles.Add(new Particle
			{
				Position = new Vector2(random.Next(0, 900), random.Next(0, 600)),
				Velocity = new Vector2((float)(random.NextDouble() - 0.5) * 25f, (float)(random.NextDouble() - 0.5) * 25f),
				Size = (float)random.NextDouble() * 3f + 1f,
				Alpha = (float)random.NextDouble() * 0.5f + 0.2f,
				Color = color
			});
		}
	}

	public void Dispose()
	{
	}

	public override void OnOpen()
	{
		base.OnOpen();
		log.Debug("[NewMainWindow] Window opened - clearing character progress cache and refreshing character list");
		RefreshCharacterList(forceIpcCheck: true);
		characterProgressCache.Clear();
	}

	public override void Draw()
	{
		if (base.IsOpen)
		{
			float num = Math.Clamp(configuration.WindowOpacity, 0.1f, 1f);
			ImGui.PushStyleVar(ImGuiStyleVar.Alpha, num);
			float deltaTime = ImGui.GetIO().DeltaTime;
			animTime += deltaTime;
			particleTime += deltaTime * 0.5f;
			glowPulse = (MathF.Sin(animTime * 2f) + 1f) * 0.5f;
			ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
			Vector2 windowSize = ImGui.GetWindowSize();
			Vector2 windowPos = ImGui.GetWindowPos();
			float num2 = 200f;
			float num3 = 30f;
			if (!isMinimized)
			{
				lastNormalWindowSize = new Vector2(MathF.Max(windowSize.X, NormalMinWindowSize.X), MathF.Max(windowSize.Y, NormalMinWindowSize.Y));
			}
			if (HandleTitleBarControls(windowPos, windowSize))
			{
				ImGui.PopStyleVar();
				return;
			}
			if (isMinimized)
			{
				windowSize = MinimizedWindowSize;
				DrawCustomTitleBar(windowDrawList, windowPos, windowSize, num3, num);
				ImGui.PopStyleVar();
				return;
			}
			TryInitialCharacterLoad();
			DrawGradientBackground(num);
			DrawAnimatedParticles(windowDrawList, windowPos, windowSize, deltaTime, num);
			DrawScanningLine(windowDrawList, windowPos, windowSize);
			DrawCustomTitleBar(windowDrawList, windowPos, windowSize, num3, num);
			DrawSemiTransparentBackgrounds(windowPos + new Vector2(0f, num3), windowSize - new Vector2(0f, num3), num2, num);
			ImGui.SetCursorPos(new Vector2(0f, num3));
			DrawSidebar(num2, windowSize.Y - num3);
			ImGui.SameLine();
			DrawContentArea(windowSize.X - num2 - 20f, windowSize.Y - num3);
			DrawWorldSelectionDialogs();
			ImGui.PopStyleVar();
		}
	}

	private void RefreshCharacterList(bool forceIpcCheck = false)
	{
		try
		{
			log.Debug($"[NewMainWindow] RefreshCharacterList called (forceIpcCheck: {forceIpcCheck})");
			if (forceIpcCheck)
			{
				log.Debug("[NewMainWindow] Forcing IPC availability check...");
				autoRetainerIpc.TryReinitialize();
			}
			if (!autoRetainerIpc.IsAvailable)
			{
				log.Warning("[NewMainWindow] AutoRetainer IPC not available during character refresh");
				return;
			}
			registeredCharacters = autoRetainerIpc.GetRegisteredCharacters();
			classUnlockRotationService.RefreshOfflineClassJobSnapshots(registeredCharacters);
			characterGrandCompanyRankFilterCache.Clear();
			lastCharacterRefresh = DateTime.Now;
			log.Information($"[NewMainWindow] Loaded {registeredCharacters.Count} characters from AutoRetainer");
			if (registeredCharacters.Count > 0)
			{
				initialCharacterLoadComplete = true;
			}
			charactersByDataCenter = dataCenterService.GroupCharactersByDataCenter(registeredCharacters);
			availableWorlds = (from w in (from c in registeredCharacters
					select c.Split('@') into parts
					where parts.Length > 1
					select parts[1]).Distinct()
				orderby w
				select w).ToList();
			foreach (string registeredCharacter in registeredCharacters)
			{
				if (!characterSelection.ContainsKey(registeredCharacter))
				{
					characterSelection[registeredCharacter] = false;
				}
			}
			foreach (string item in configuration.SelectedCharactersForUI)
			{
				if (characterSelection.ContainsKey(item))
				{
					characterSelection[item] = true;
				}
			}
		}
		catch (Exception ex)
		{
			log.Error("[NewMainWindow] RefreshCharacterList failed: " + ex.Message);
			log.Error("[NewMainWindow] Stack trace: " + ex.StackTrace);
		}
	}

	private void TryInitialCharacterLoad()
	{
		if (initialCharacterLoadComplete || characterLoadAttempts >= 5)
		{
			return;
		}
		double totalSeconds = (DateTime.Now - initialLoadStartTime).TotalSeconds;
		int num = ((characterLoadAttempts < retryDelaysSeconds.Length) ? retryDelaysSeconds[characterLoadAttempts] : retryDelaysSeconds[^1]);
		if (!(totalSeconds < (double)num))
		{
			characterLoadAttempts++;
			log.Information($"[NewMainWindow] Character load attempt {characterLoadAttempts}/{5} (after {totalSeconds:F1}s)");
			RefreshCharacterList(forceIpcCheck: true);
			if (!initialCharacterLoadComplete)
			{
				initialLoadStartTime = DateTime.Now;
				log.Debug($"[NewMainWindow] Next retry in {((characterLoadAttempts < retryDelaysSeconds.Length) ? retryDelaysSeconds[characterLoadAttempts] : retryDelaysSeconds[^1])}s");
			}
		}
	}

	private void TryWarningMenuQuestionableCheck()
	{
		if (warningMenuRetryCycleComplete)
		{
			return;
		}
		if (warningMenuRetryStartTime == DateTime.MinValue)
		{
			warningMenuRetryStartTime = DateTime.Now;
		}
		double totalSeconds = (DateTime.Now - warningMenuRetryStartTime).TotalSeconds;
		int num = Math.Min(warningMenuRetryAttempts, warningMenuRetryDelaysSeconds.Length - 1);
		int num2 = warningMenuRetryDelaysSeconds[num];
		if (!(totalSeconds < (double)num2))
		{
			warningMenuRetryAttempts++;
			log.Debug($"[NewMainWindow] Warning Menu retry attempt {warningMenuRetryAttempts} (after {totalSeconds:F1}s)");
			if (plugin.QuestionableIPC.TryEnsureAvailableSilent() && plugin.QuestionableIPC.ValidateFeatureCompatibility())
			{
				warningMenuRetryCycleComplete = true;
				selectedTab = 5;
				log.Information("[NewMainWindow] WigglyMuffin Questionable and its IPC endpoints became available while the window was open");
			}
			else
			{
				warningMenuRetryStartTime = DateTime.Now;
			}
		}
	}

	private void DrawSidebar(float width, float height)
	{
		using ImRaii.ImChild imChild = ImRaii.Child("Sidebar", new Vector2(width, height - 10f), border: false);
		if (!imChild.Success)
		{
			return;
		}
		DrawSidebarCategory("CHARACTERS", ref charactersExpanded, delegate
		{
			DrawSidebarItem("All", 0, registeredCharacters.Count);
			DrawSidebarItem("EU", 1, GetCharacterCountForDC("EU"));
			DrawSidebarItem("NA", 2, GetCharacterCountForDC("NA"));
			DrawSidebarItem("JP", 3, GetCharacterCountForDC("JP"));
			DrawSidebarItem("OCE", 4, GetCharacterCountForDC("OCE"));
		});
		ImGuiHelpers.ScaledDummy(5f);
		DrawSidebarCategory("MENU", ref menuExpanded, delegate
		{
			if (plugin.QuestionableIPC.ValidateFeatureCompatibility())
			{
				DrawSidebarSubcategory("ROTATIONS", ref rotationsMenuExpanded, delegate
				{
					DrawSidebarItem("Stop Points", 5, 0);
					DrawSidebarItem("Allied Society", 10, 0);
					DrawSidebarItem("Event Quest", 6, 0);
					DrawSidebarItem("Hunt Logs", 13, 0);
					DrawSidebarItem("Classes Unlock", 16, 0);
					DrawSidebarItem("Retainers", 15, 0);
				});
				DrawSidebarSubcategory("TOOLS", ref toolsMenuExpanded, delegate
				{
					DrawSidebarItem("MSQ Progression", 7, 0);
					DrawSidebarItem("Data Center Travel", 8, 0);
					DrawSidebarItem("Multiboxing", 12, 0);
				});
				DrawSidebarSubcategory("PLUGIN", ref pluginMenuExpanded, delegate
				{
					DrawSidebarItem("Initial Setup", 17, 0);
					DrawSidebarItem("Settings", 9, 0);
					DrawSidebarItem("Dependencies", 14, 0);
				});
			}
			else
			{
				if (warningMenuRetryCycleComplete)
				{
					warningMenuRetryCycleComplete = false;
					warningMenuRetryAttempts = 0;
					warningMenuRetryStartTime = DateTime.MinValue;
				}
				TryWarningMenuQuestionableCheck();
				DrawSidebarItem("Warning", 11, 0);
				DrawSidebarItem("Initial Setup", 17, 0);
				DrawSidebarItem("Dependencies", 14, 0);
			}
		});
		DrawKoFiFooter();
	}

	private void DrawKoFiFooter()
	{
		float y = ImGui.GetContentRegionAvail().Y;
		if (y > 25f)
		{
			ImGui.Dummy(new Vector2(0f, y - 25f));
		}
		float num = ImGui.CalcTextSize("♥ Ko-fi").X + ImGui.GetStyle().FramePadding.X * 2f;
		float x = ImGui.GetContentRegionAvail().X;
		ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (x - num) / 2f));
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, colorAccent);
		ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
		if (ImGui.SmallButton("♥ Ko-fi"))
		{
			OpenKoFiPage();
		}
		ImGui.PopStyleColor(4);
	}

	private void OpenKoFiPage()
	{
		try
		{
			Process.Start(new ProcessStartInfo("https://ko-fi.com/macarondream")
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			log.Error("[NewMainWindow] Failed to open Ko-fi page: " + ex.Message);
		}
	}

	private void DrawSidebarCategory(string name, ref bool expanded, System.Action drawItems)
	{
		ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
		Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
		float num = ImGui.GetContentRegionAvail().X - 10f;
		float y = 30f;
		Vector4 input = new Vector4(colorPrimary.X * 0.25f, colorPrimary.Y * 0.25f, colorPrimary.Z * 0.25f, 0.7f);
		uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(colorPrimary.X, colorPrimary.Y, colorPrimary.Z, 0.6f));
		Vector2 vector = cursorScreenPos + new Vector2(5f, 0f);
		windowDrawList.AddRectFilled(vector, vector + new Vector2(num, y), ImGui.ColorConvertFloat4ToU32(input), 6f);
		windowDrawList.AddRect(vector, vector + new Vector2(num, y), col, 6f, ImDrawFlags.None, 1.5f);
		string obj = (expanded ? "v" : ">");
		ImGui.SetCursorScreenPos(vector + new Vector2(10f, 8f));
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.Text(obj);
		ImGui.PopStyleColor();
		ImGui.SameLine();
		ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 1f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.Text(name);
		ImGui.PopStyleColor();
		if (ImGui.IsMouseHoveringRect(vector, vector + new Vector2(num, y)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
		{
			expanded = !expanded;
		}
		ImGui.Dummy(new Vector2(num + 10f, y));
		ImGui.Spacing();
		if (expanded)
		{
			ImGui.Indent(10f);
			drawItems();
			ImGui.Unindent(10f);
			ImGui.Spacing();
		}
	}

	private void DrawSidebarItem(string label, int tabIndex, int count)
	{
		bool flag = selectedTab == tabIndex;
		string obj = ((count > 0) ? $"{label} ({count})" : label);
		if (flag)
		{
			ImGui.PushStyleColor(ImGuiCol.Header, colorPrimary);
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, colorHover);
		}
		if (ImGui.Selectable(obj, flag, ImGuiSelectableFlags.None, new Vector2(0f, 22f)))
		{
			selectedTab = tabIndex;
			if (tabIndex <= 4)
			{
				selectedDCFilter = tabIndex;
				configuration.CharacterFilters.DataCenter = availableDataCenters[tabIndex];
				configuration.CharacterFilters.World = "All";
				configuration.Save();
			}
		}
		ImGui.PopStyleColor();
	}

	private void DrawSidebarSubcategory(string name, ref bool expanded, System.Action drawItems)
	{
		string value = (expanded ? "v" : ">");
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.PushStyleColor(ImGuiCol.HeaderHovered, colorHover);
		ImU8String label = new ImU8String(22, 3);
		label.AppendFormatted(value);
		label.AppendLiteral("  ");
		label.AppendFormatted(name);
		label.AppendLiteral("##SidebarSubcategory");
		label.AppendFormatted(name);
		if (ImGui.Selectable(label, selected: false, ImGuiSelectableFlags.None, new Vector2(0f, 21f)))
		{
			expanded = !expanded;
		}
		ImGui.PopStyleColor(2);
		if (expanded)
		{
			ImGui.Indent(8f);
			drawItems();
			ImGui.Unindent(8f);
			ImGui.Spacing();
		}
	}

	private int GetCharacterCountForDC(string dcName)
	{
		if (charactersByDataCenter.TryGetValue(dcName, out List<string> value))
		{
			return value.Count;
		}
		return 0;
	}

	private void DrawCustomTitleBar(ImDrawListPtr drawList, Vector2 windowPos, Vector2 windowSize, float height, float opacity = 1f)
	{
		uint num = ImGui.ColorConvertFloat4ToU32(new Vector4(colorPrimary.X * 0.4f, colorPrimary.Y * 0.4f, colorPrimary.Z * 0.4f, opacity));
		uint num2 = ImGui.ColorConvertFloat4ToU32(new Vector4(colorSecondary.X * 0.3f, colorSecondary.Y * 0.3f, colorSecondary.Z * 0.3f, opacity));
		drawList.AddRectFilledMultiColor(windowPos, windowPos + new Vector2(windowSize.X, height), num, num2, num2, num);
		Vector2 pos = windowPos + new Vector2(10f, 7f);
		uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));
		ImU8String text = new ImU8String(25, 1);
		text.AppendLiteral("Questionable Companion V.");
		text.AppendFormatted(DisplayVersion);
		drawList.AddText(pos, col, text);
		Vector2 vector = windowPos + new Vector2(windowSize.X - 60f, 3f);
		Vector2 vector2 = new Vector2(24f, 24f);
		if (ImGui.IsMouseHoveringRect(vector, vector + vector2))
		{
			drawList.AddRectFilled(vector, vector + vector2, ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.4f, 0.4f, 0.8f)), 4f);
		}
		uint col2 = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
		Vector2 vector3 = vector + new Vector2(12f, 12f);
		if (isMinimized)
		{
			Vector2 p = vector3 + new Vector2(-4f, -5f);
			Vector2 p2 = vector3 + new Vector2(-4f, 5f);
			Vector2 p3 = vector3 + new Vector2(4f, 0f);
			drawList.AddTriangleFilled(p, p2, p3, col2);
		}
		else
		{
			Vector2 p4 = vector3 + new Vector2(-5f, -4f);
			Vector2 p5 = vector3 + new Vector2(5f, -4f);
			Vector2 p6 = vector3 + new Vector2(0f, 4f);
			drawList.AddTriangleFilled(p4, p5, p6, col2);
		}
		Vector2 vector4 = windowPos + new Vector2(windowSize.X - 30f, 3f);
		Vector2 vector5 = new Vector2(24f, 24f);
		if (ImGui.IsMouseHoveringRect(vector4, vector4 + vector5))
		{
			drawList.AddRectFilled(vector4, vector4 + vector5, ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.2f, 0.2f, 0.8f)), 4f);
		}
		drawList.AddText(vector4 + new Vector2(7f, 2f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), "X");
	}

	private bool HandleTitleBarControls(Vector2 windowPos, Vector2 windowSize)
	{
		Vector2 vector = windowPos + new Vector2(windowSize.X - 60f, 3f);
		Vector2 vector2 = new Vector2(24f, 24f);
		if (ImGui.IsMouseHoveringRect(vector, vector + vector2) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
		{
			if (isMinimized)
			{
				isMinimized = false;
				ImGui.SetWindowSize(lastNormalWindowSize, ImGuiCond.Always);
			}
			else
			{
				lastNormalWindowSize = new Vector2(MathF.Max(windowSize.X, NormalMinWindowSize.X), MathF.Max(windowSize.Y, NormalMinWindowSize.Y));
				isMinimized = true;
				ImGui.SetWindowSize(MinimizedWindowSize, ImGuiCond.Always);
			}
			return false;
		}
		Vector2 vector3 = windowPos + new Vector2(windowSize.X - 30f, 3f);
		Vector2 vector4 = new Vector2(24f, 24f);
		if (ImGui.IsMouseHoveringRect(vector3, vector3 + vector4) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
		{
			CloseWindow();
			return true;
		}
		float y = (isMinimized ? MinimizedWindowSize.Y : 30f);
		Vector2 rMax = windowPos + new Vector2(windowSize.X, y);
		bool num = ImGui.IsMouseHoveringRect(windowPos, rMax);
		bool flag = ImGui.IsMouseHoveringRect(vector, vector + vector2) || ImGui.IsMouseHoveringRect(vector3, vector3 + vector4);
		if (num && !flag && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
		{
			if (isMinimized)
			{
				isMinimized = false;
				ImGui.SetWindowSize(lastNormalWindowSize, ImGuiCond.Always);
			}
			else
			{
				lastNormalWindowSize = new Vector2(MathF.Max(windowSize.X, NormalMinWindowSize.X), MathF.Max(windowSize.Y, NormalMinWindowSize.Y));
				isMinimized = true;
				ImGui.SetWindowSize(MinimizedWindowSize, ImGuiCond.Always);
			}
			return false;
		}
		return false;
	}

	private void CloseWindow()
	{
		base.IsOpen = false;
		isMinimized = false;
		showSelectWorldDialog = false;
		showDeselectWorldDialog = false;
	}

	private void DrawSemiTransparentBackgrounds(Vector2 windowPos, Vector2 windowSize, float sidebarWidth, float opacity = 1f)
	{
		ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
		windowDrawList.AddRectFilled(col: ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.15f, 0.85f * opacity)), pMin: windowPos, pMax: windowPos + new Vector2(sidebarWidth, windowSize.Y));
		float num = 20f;
		for (int i = 0; i < 20; i++)
		{
			float num2 = (float)i / 20f;
			windowDrawList.AddRectFilled(col: ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f + 0.030000009f * num2, 0.12f + 0.030000009f * num2, 0.15f + 0.030000001f * num2, (0.85f - 0.05f * num2) * opacity)), pMin: windowPos + new Vector2(sidebarWidth + (float)i, 0f), pMax: windowPos + new Vector2(sidebarWidth + (float)i + 1f, windowSize.Y));
		}
		windowDrawList.AddRectFilled(col: ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 0.8f * opacity)), pMin: windowPos + new Vector2(sidebarWidth + num, 0f), pMax: windowPos + windowSize);
	}

	private void DrawGradientBackground(float opacity = 1f)
	{
		ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
		Vector2 windowPos = ImGui.GetWindowPos();
		Vector2 windowSize = ImGui.GetWindowSize();
		float num = 2f;
		float num2 = MathF.Sin(animTime * 0.3f) * 0.15f;
		float num3 = MathF.Cos(animTime * 0.25f) * 0.12f;
		uint num4 = ImGui.ColorConvertFloat4ToU32(new Vector4(0.47843137f + num2, 35f / 51f + num3, 0.8784314f - num2 * 0.5f, opacity));
		uint colUprRight = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9490196f - num3 * 0.5f, 0.76862746f + num2, 33f / 85f + num3, opacity));
		uint colBotRight = ImGui.ColorConvertFloat4ToU32(new Vector4(62f / 85f + num3, 0.29411766f - num2, 0.18431373f + num2 * 0.8f, opacity));
		windowDrawList.AddRectFilledMultiColor(windowPos - new Vector2(num, num), windowPos + windowSize + new Vector2(num, num), num4, colUprRight, colBotRight, num4);
	}

	private void DrawContentArea(float width, float height)
	{
		using ImRaii.ImChild imChild = ImRaii.Child("ContentArea", new Vector2(width, height - 10f), border: false);
		if (!imChild.Success)
		{
			return;
		}
		switch (selectedTab)
		{
		case 0:
		case 1:
		case 2:
		case 3:
		case 4:
			DrawCharactersTab();
			break;
		case 5:
			DrawStopPointsTab();
			break;
		case 6:
			DrawEventQuestTab();
			break;
		case 7:
			DrawMSQProgressionTab();
			break;
		case 8:
			DrawDCTravelTab();
			break;
		case 9:
			DrawSettingsTab();
			break;
		case 10:
			DrawAlliedSocietyTab();
			break;
		case 11:
			DrawWarningTab();
			break;
		case 12:
			DrawMultiboxingTab();
			break;
		case 13:
			DrawHuntLogsTab();
			break;
		case 14:
			DrawDependenciesTab();
			break;
		case 15:
			if (plugin.QuestionableIPC.TryEnsureAvailableSilent() && plugin.QuestionableIPC.ValidateFeatureCompatibility())
			{
				DrawRetainersTab();
			}
			else
			{
				DrawWarningTab();
			}
			break;
		case 16:
			DrawClassUnlocksTab();
			break;
		case 17:
			DrawInitialSetupTab();
			break;
		}
	}

	private void DrawAnimatedParticles(ImDrawListPtr drawList, Vector2 pos, Vector2 size, float deltaTime, float opacity = 1f)
	{
		foreach (Particle particle in particles)
		{
			particle.Position += particle.Velocity * deltaTime;
			if (particle.Position.X < pos.X)
			{
				particle.Position.X = pos.X + size.X;
			}
			if (particle.Position.X > pos.X + size.X)
			{
				particle.Position.X = pos.X;
			}
			if (particle.Position.Y < pos.Y)
			{
				particle.Position.Y = pos.Y + size.Y;
			}
			if (particle.Position.Y > pos.Y + size.Y)
			{
				particle.Position.Y = pos.Y;
			}
			float num = 0.8f + glowPulse * 0.2f;
			float num2 = particle.Alpha * (0.6f + glowPulse * 0.4f) * opacity;
			uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(particle.Color.X * num, particle.Color.Y * num, particle.Color.Z * num, num2));
			float w = num2 * 0.3f;
			uint col2 = ImGui.ColorConvertFloat4ToU32(new Vector4(particle.Color.X * num, particle.Color.Y * num, particle.Color.Z * num, w));
			drawList.AddCircleFilled(particle.Position, particle.Size * 2f, col2, 12);
			drawList.AddCircleFilled(particle.Position, particle.Size, col, 8);
		}
	}

	private void DrawScanningLine(ImDrawListPtr drawList, Vector2 pos, Vector2 size)
	{
		float y = pos.Y + animTime * 0.3f % 1f * size.Y;
		float y2 = pos.Y + (animTime * 0.25f + 0.33f) % 1f * size.Y;
		float y3 = pos.Y + (animTime * 0.2f + 0.66f) % 1f * size.Y;
		uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(colorPrimary.X, colorPrimary.Y, colorPrimary.Z, 0.15f * glowPulse));
		uint col2 = ImGui.ColorConvertFloat4ToU32(new Vector4(colorSecondary.X, colorSecondary.Y, colorSecondary.Z, 0.15f * glowPulse));
		uint col3 = ImGui.ColorConvertFloat4ToU32(new Vector4(colorAccent.X, colorAccent.Y, colorAccent.Z, 0.15f * glowPulse));
		drawList.AddLine(new Vector2(pos.X, y), new Vector2(pos.X + size.X, y), col, 2f);
		drawList.AddLine(new Vector2(pos.X, y2), new Vector2(pos.X + size.X, y2), col2, 2f);
		drawList.AddLine(new Vector2(pos.X, y3), new Vector2(pos.X + size.X, y3), col3, 2f);
	}

	private void DrawDCTravelTab()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Data Center Travel Configuration");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		Configuration configuration = plugin.Configuration;
		if (string.IsNullOrEmpty(selectedDataCenter) && !string.IsNullOrEmpty(configuration.DCTravelDataCenter))
		{
			selectedDataCenter = configuration.DCTravelDataCenter;
		}
		if (string.IsNullOrEmpty(selectedWorld) && !string.IsNullOrEmpty(configuration.DCTravelWorld))
		{
			selectedWorld = configuration.DCTravelWorld;
		}
		if (string.IsNullOrEmpty(selectedDataCenter))
		{
			selectedDataCenter = dataCenterWorlds.Keys.First();
		}
		if (string.IsNullOrEmpty(selectedWorld))
		{
			List<string> list = dataCenterWorlds.GetValueOrDefault(selectedDataCenter) ?? new List<string>();
			if (list.Count > 0)
			{
				selectedWorld = list[0];
			}
		}
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextWrapped("Configure automatic Data Center travel for quest rotation. The plugin will travel to the specified Data Center and World before starting quests.");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(15f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Select Data Center:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		ImGui.SetNextItemWidth(350f);
		ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.15f, 0.15f, 0.18f, 0.9f));
		ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(colorPrimary.X * 0.3f, colorPrimary.Y * 0.3f, colorPrimary.Z * 0.3f, 0.9f));
		if (ImGui.BeginCombo("##DataCenterCombo", selectedDataCenter))
		{
			foreach (string item in dataCenterWorlds.Keys.OrderBy((string k) => k))
			{
				bool flag = selectedDataCenter == item;
				if (ImGui.Selectable(item, flag))
				{
					selectedDataCenter = item;
					List<string> list2 = dataCenterWorlds.GetValueOrDefault(selectedDataCenter) ?? new List<string>();
					if (list2.Count > 0)
					{
						selectedWorld = list2[0];
					}
				}
				if (flag)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGuiHelpers.ScaledDummy(15f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Select World:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		List<string> list3 = dataCenterWorlds.GetValueOrDefault(selectedDataCenter) ?? new List<string>();
		ImGui.SetNextItemWidth(350f);
		ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.15f, 0.15f, 0.18f, 0.9f));
		ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(colorPrimary.X * 0.3f, colorPrimary.Y * 0.3f, colorPrimary.Z * 0.3f, 0.9f));
		if (ImGui.BeginCombo("##WorldCombo", selectedWorld))
		{
			foreach (string item2 in list3)
			{
				bool flag2 = selectedWorld == item2;
				if (ImGui.Selectable(item2, flag2))
				{
					selectedWorld = item2;
				}
				if (flag2)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGui.PopStyleColor(2);
		ImGuiHelpers.ScaledDummy(15f);
		LifestreamIPC lifestreamIPC = Plugin.Instance?.LifestreamIPC;
		if (lifestreamIPC != null && !lifestreamIPC.IsAvailable)
		{
			lifestreamIPC.ForceCheckAvailability();
		}
		bool flag3 = lifestreamIPC?.IsAvailable ?? false;
		if (!flag3)
		{
			ImGui.BeginDisabled();
		}
		bool v = configuration.EnableDCTravel;
		if (ImGui.Checkbox("Enable Data Center Travel", ref v))
		{
			configuration.EnableDCTravel = v;
			configuration.Save();
		}
		if (!flag3)
		{
			ImGui.EndDisabled();
		}
		ImGui.SameLine();
		DrawInfoIcon("Automatically travels to the specified Data Center and World before starting quest rotation.\nRequires Lifestream plugin to be installed and configured.\nImpact: Characters will travel to target DC/World at rotation start.");
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Lifestream Return Command:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		LifestreamCommandType lifestreamCommand = configuration.LifestreamCommand;
		ImGui.SetNextItemWidth(350f);
		ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.15f, 0.15f, 0.18f, 0.9f));
		ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(colorPrimary.X * 0.3f, colorPrimary.Y * 0.3f, colorPrimary.Z * 0.3f, 0.9f));
		if (ImGui.BeginCombo("##LifestreamCmd", lifestreamCommand.ToString()))
		{
			foreach (LifestreamCommandType value2 in Enum.GetValues(typeof(LifestreamCommandType)))
			{
				bool flag4 = lifestreamCommand == value2;
				if (ImGui.Selectable(value2.ToString(), flag4))
				{
					configuration.LifestreamCommand = value2;
					configuration.Save();
				}
				if (flag4)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGui.PopStyleColor(2);
		ImGui.SameLine();
		DrawInfoIcon("Command to execute when returning to homeworld or skipping character.\nAuto: /li auto (Move to Inn / FC on Homeworld)\nLi: /li (Just travel back to Homeworld)\nNone: No command (relies on manual travel or other plugins)");
		if (!flag3)
		{
			ImGuiHelpers.ScaledDummy(5f);
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.5f, 0f, 1f));
			ImGui.TextWrapped("Lifestream plugin is not available! DC Travel requires Lifestream to be installed and enabled.");
			ImGui.PopStyleColor();
			ImGuiHelpers.ScaledDummy(5f);
			if (ImGui.Button("Check Lifestream Again") && lifestreamIPC != null)
			{
				bool value = lifestreamIPC.ForceCheckAvailability();
				log.Information($"[DCTravel UI] Manual Lifestream check result: {value}");
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Manually check if Lifestream is available.\nCheck the logs for detailed information.");
			}
		}
		ImGuiHelpers.ScaledDummy(20f);
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(15f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Current Configuration:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		ImGui.Indent(10f);
		ImU8String text = new ImU8String(13, 0);
		text.AppendLiteral("Data Center: ");
		ImGui.TextUnformatted(text);
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted((configuration.DCTravelDataCenter.Length > 0) ? configuration.DCTravelDataCenter : "Not Set");
		ImGui.PopStyleColor();
		ImU8String text2 = new ImU8String(14, 0);
		text2.AppendLiteral("Target World: ");
		ImGui.TextUnformatted(text2);
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted((configuration.DCTravelWorld.Length > 0) ? configuration.DCTravelWorld : "Not Set");
		ImGui.PopStyleColor();
		ImU8String text3 = new ImU8String(8, 0);
		text3.AppendLiteral("Status: ");
		ImGui.TextUnformatted(text3);
		ImGui.SameLine();
		if (configuration.EnableDCTravel)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
			ImGui.TextUnformatted("Enabled");
			ImGui.PopStyleColor();
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImGui.TextUnformatted("Disabled");
			ImGui.PopStyleColor();
		}
		ImGui.Unindent(10f);
		ImGuiHelpers.ScaledDummy(20f);
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Apply", new Vector2(120f, 30f)))
		{
			configuration.DCTravelDataCenter = selectedDataCenter;
			configuration.DCTravelWorld = selectedWorld;
			configuration.Save();
			log.Information("[DCTravel] Configuration saved: " + selectedDataCenter + " -> " + selectedWorld);
		}
		ImGui.PopStyleColor(2);
		ImGui.SameLine();
		if (ImGui.Button("Cancel", new Vector2(120f, 30f)))
		{
			selectedDataCenter = configuration.DCTravelDataCenter;
			selectedWorld = configuration.DCTravelWorld;
			if (string.IsNullOrEmpty(selectedDataCenter))
			{
				selectedDataCenter = dataCenterWorlds.Keys.First();
			}
			if (string.IsNullOrEmpty(selectedWorld))
			{
				List<string> list4 = dataCenterWorlds.GetValueOrDefault(selectedDataCenter) ?? new List<string>();
				if (list4.Count > 0)
				{
					selectedWorld = list4[0];
				}
			}
		}
		ImGuiHelpers.ScaledDummy(10f);
		if (!configuration.EnableDCTravel)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextWrapped("Note: Data Center Travel is currently disabled. Enable it above to use this feature.");
			ImGui.PopStyleColor();
		}
	}

	private void DrawDependenciesTab()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Dependencies");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		ImGui.TextWrapped("Questionable must be WigglyMuffin and CryoTechnic's build. The Companion accepts either their known Dalamud repository or a loaded manifest naming both authors. Other entries are feature-specific installation guidance.");
		ImGuiHelpers.ScaledDummy(10f);
		using ImRaii.ImChild imChild = ImRaii.Child("DependenciesScrollArea", new Vector2(0f, 0f), border: false);
		if (!imChild.Success)
		{
			return;
		}
		using ImRaii.ImTable imTable = ImRaii.Table("DependenciesTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY);
		if (!imTable.Success)
		{
			return;
		}
		ImGui.TableSetupColumn("Feature", ImGuiTableColumnFlags.WidthFixed, 90f);
		ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 175f);
		ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 145f);
		ImGui.TableSetupColumn("Repository", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 70f);
		ImGui.TableSetupScrollFreeze(0, 1);
		ImGui.TableHeadersRow();
		for (int i = 0; i < DependencyEntries.Count; i++)
		{
			DependencyEntry dependency = DependencyEntries[i];
			IExposedPlugin exposedPlugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin plugin) => string.Equals(plugin.InternalName, dependency.InternalName, StringComparison.Ordinal));
			var (text, col) = GetDependencyStatus(dependency, exposedPlugin != null, exposedPlugin?.IsLoaded ?? false, exposedPlugin?.IsOutdated ?? false, (exposedPlugin != null && exposedPlugin.IsBanned) || (exposedPlugin?.IsDecommissioned ?? false), exposedPlugin?.Version?.ToString() ?? string.Empty);
			if (string.Equals(dependency.InternalName, "Questionable", StringComparison.Ordinal) && exposedPlugin != null && exposedPlugin.IsLoaded)
			{
				if (plugin.QuestionableIPC.ValidateFeatureCompatibility())
				{
					text = "Wiggly repository";
					col = colorSuccess;
				}
				else
				{
					text = "Wiggly repo required";
					col = colorAccent;
				}
			}
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.TextUnformatted(dependency.Feature);
			ImGui.TableNextColumn();
			ImGui.TextUnformatted(dependency.Name);
			ImGui.TableNextColumn();
			ImGui.TextColored(in col, text);
			ImGui.TableNextColumn();
			string buf = dependency.RepositoryUrl;
			ImGui.SetNextItemWidth(-1f);
			ImU8String label = new ImU8String(16, 1);
			label.AppendLiteral("##DependencyRepo");
			label.AppendFormatted(i);
			ImGui.InputText(label, ref buf, 512, ImGuiInputTextFlags.ReadOnly);
			ImGui.TableNextColumn();
			ImU8String label2 = new ImU8String(24, 1);
			label2.AppendLiteral("Copy##DependencyRepoCopy");
			label2.AppendFormatted(i);
			if (ImGui.Button(label2))
			{
				ImGui.SetClipboardText(dependency.RepositoryUrl);
			}
			if (ImGui.IsItemHovered())
			{
				ImU8String tooltip = new ImU8String(24, 1);
				tooltip.AppendLiteral("Copy repository URL for ");
				tooltip.AppendFormatted(dependency.Name);
				ImGui.SetTooltip(tooltip);
			}
		}
	}

	private (string Status, Vector4 Color) GetDependencyStatus(DependencyEntry dependency, bool isInstalled, bool isLoaded, bool isOutdated, bool isUnavailable, string version)
	{
		if (!isInstalled)
		{
			if (!dependency.IsStub)
			{
				return (Status: "Not installed", Color: colorAccent);
			}
			return (Status: "Stub / planned", Color: colorSecondary);
		}
		if (isUnavailable)
		{
			return (Status: "Unavailable", Color: colorAccent);
		}
		if (isOutdated)
		{
			return (Status: "Update required", Color: colorSecondary);
		}
		if (!isLoaded)
		{
			return (Status: "Installed / disabled", Color: colorSecondary);
		}
		return (Status: string.IsNullOrWhiteSpace(version) ? "Loaded" : ("Loaded " + version), Color: colorSuccess);
	}

	private void DrawHuntLogsTab()
	{
		HuntLogSettings huntLogs = configuration.HuntLogs;
		List<string> selectedCharacters = (from kvp in characterSelection
			where kvp.Value
			select kvp.Key).ToList();
		HuntLogAutomationState currentState = huntLogAutomationService.GetCurrentState();
		if (huntLogAutomationService.IsRunning)
		{
			selectedHuntLogMode = currentState.Mode;
		}
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Hunt Logs");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(8f);
		using ImRaii.ImChild imChild = ImRaii.Child("HuntLogsScroll", new Vector2(0f, 0f), border: false);
		if (imChild.Success)
		{
			DrawHuntLogControls(selectedCharacters, currentState);
			ImGuiHelpers.ScaledDummy(10f);
			ImGui.Separator();
			ImGuiHelpers.ScaledDummy(10f);
			DrawHuntLogStatus(selectedCharacters, currentState);
			ImGuiHelpers.ScaledDummy(10f);
			ImGui.Separator();
			ImGuiHelpers.ScaledDummy(10f);
			DrawHuntLogSettings(huntLogs);
		}
	}

	private void DrawHuntLogControls(List<string> selectedCharacters, HuntLogAutomationState serviceState)
	{
		ImGui.TextColored(in colorSecondary, "Mode");
		if (ImGui.RadioButton("Class Logs", selectedHuntLogMode == HuntLogMode.Class))
		{
			selectedHuntLogMode = HuntLogMode.Class;
		}
		ImGui.SameLine();
		if (ImGui.RadioButton("Grand Company Logs", selectedHuntLogMode == HuntLogMode.GrandCompany))
		{
			selectedHuntLogMode = HuntLogMode.GrandCompany;
		}
		ImGui.SameLine();
		if (ImGui.RadioButton("All", selectedHuntLogMode == HuntLogMode.All))
		{
			selectedHuntLogMode = HuntLogMode.All;
		}
		ImGuiHelpers.ScaledDummy(8f);
		if (huntLogAutomationService.IsRunning)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
			if (ImGui.Button("Stop Hunt Logs", new Vector2(170f, 32f)))
			{
				huntLogAutomationService.Stop();
			}
			ImGui.PopStyleColor();
			ImGui.SameLine();
			ImGui.TextColored(ImGuiColors.DalamudYellow, serviceState.Phase.ToString());
		}
		else if (selectedCharacters.Count == 0)
		{
			ImGui.TextColored(in colorSecondary, "Select characters in the Characters tab before starting.");
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
			if (ImGui.Button("Start Hunt Logs", new Vector2(170f, 32f)) && !huntLogAutomationService.Start(selectedHuntLogMode, selectedCharacters))
			{
				log.Warning("[HuntLogs] Start rejected. See Hunt Logs status for details.");
			}
			ImGui.PopStyleColor(2);
		}
		if (serviceState.Phase == HuntLogPhase.Error && !string.IsNullOrEmpty(serviceState.ErrorMessage))
		{
			ImGui.TextColored(in colorAccent, serviceState.ErrorMessage);
		}
	}

	private void DrawHuntLogStatus(List<string> selectedCharacters, HuntLogAutomationState serviceState)
	{
		ImGui.TextColored(in colorSecondary, "Status");
		ImU8String text = new ImU8String(7, 1);
		text.AppendLiteral("Phase: ");
		text.AppendFormatted(serviceState.Phase);
		ImGui.TextUnformatted(text);
		if (!string.IsNullOrEmpty(serviceState.CurrentCharacter))
		{
			ImU8String text2 = new ImU8String(11, 1);
			text2.AppendLiteral("Character: ");
			text2.AppendFormatted(serviceState.CurrentCharacter);
			ImGui.TextUnformatted(text2);
		}
		if (!string.IsNullOrEmpty(serviceState.CurrentStep))
		{
			ImU8String text3 = new ImU8String(6, 1);
			text3.AppendLiteral("Step: ");
			text3.AppendFormatted(serviceState.CurrentStep);
			ImGui.TextUnformatted(text3);
		}
		if (!string.IsNullOrEmpty(serviceState.CurrentMarkName))
		{
			ImU8String text4 = new ImU8String(6, 1);
			text4.AppendLiteral("Mark: ");
			text4.AppendFormatted(serviceState.CurrentMarkName);
			ImGui.TextUnformatted(text4);
		}
		if (!string.IsNullOrEmpty(serviceState.DutyBackend))
		{
			ImU8String text5 = new ImU8String(14, 1);
			text5.AppendLiteral("Duty backend: ");
			text5.AppendFormatted(serviceState.DutyBackend);
			ImGui.TextUnformatted(text5);
		}
		if (!string.IsNullOrEmpty(serviceState.DutyBlocker))
		{
			ImU8String text6 = new ImU8String(14, 1);
			text6.AppendLiteral("Duty blocker: ");
			text6.AppendFormatted(serviceState.DutyBlocker);
			ImGui.TextWrapped(text6);
		}
		if (serviceState.CurrentRank > 0)
		{
			ImU8String text7 = new ImU8String(6, 1);
			text7.AppendLiteral("Rank: ");
			text7.AppendFormatted(serviceState.CurrentRank);
			ImGui.TextUnformatted(text7);
		}
		if (serviceState.CurrentCombatJobId != 0)
		{
			ImU8String text8 = new ImU8String(13, 1);
			text8.AppendLiteral("Current job: ");
			text8.AppendFormatted(serviceState.CurrentCombatJobLabel);
			ImGui.TextUnformatted(text8);
		}
		if (serviceState.SelectedCombatJobId != 0)
		{
			ImU8String text9 = new ImU8String(14, 1);
			text9.AppendLiteral("Selected job: ");
			text9.AppendFormatted(serviceState.SelectedCombatJobLabel);
			ImGui.TextUnformatted(text9);
		}
		if (serviceState.CompletedCharacters.Count > 0)
		{
			ImU8String text10 = new ImU8String(12, 2);
			text10.AppendLiteral("Completed: ");
			text10.AppendFormatted(serviceState.CompletedCharacters.Count);
			text10.AppendLiteral("/");
			text10.AppendFormatted(serviceState.SelectedCharacters.Count);
			ImGui.TextUnformatted(text10);
		}
		if (serviceState.SkippedCharacters.Count > 0)
		{
			ref readonly Vector4 col = ref colorSecondary;
			ImU8String text11 = new ImU8String(17, 1);
			text11.AppendLiteral("Skipped/blocked: ");
			text11.AppendFormatted(string.Join(", ", serviceState.SkippedCharacters));
			ImGui.TextColored(in col, text11);
		}
		if (serviceState.FailedCharacters.Count > 0)
		{
			ref readonly Vector4 col2 = ref colorAccent;
			ImU8String text12 = new ImU8String(8, 1);
			text12.AppendLiteral("Failed: ");
			text12.AppendFormatted(string.Join(", ", serviceState.FailedCharacters));
			ImGui.TextColored(in col2, text12);
		}
		ImGuiHelpers.ScaledDummy(6f);
		List<string> list = ((serviceState.SelectedCharacters.Count > 0) ? serviceState.SelectedCharacters : selectedCharacters);
		if (list.Count == 0)
		{
			return;
		}
		using ImRaii.ImTable imTable = ImRaii.Table("HuntLogCharacterStatus", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg);
		if (!imTable.Success)
		{
			return;
		}
		ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 0.25f);
		ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.3f);
		ImGui.TableSetupColumn("Snapshot", ImGuiTableColumnFlags.WidthStretch, 0.45f);
		ImGui.TableHeadersRow();
		foreach (string item in list)
		{
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.TextColored(string.Equals(item, serviceState.CurrentCharacter, StringComparison.OrdinalIgnoreCase) ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudWhite, item);
			ImGui.TableNextColumn();
			DrawWrappedHuntLogTableText(serviceState.CharacterStatuses.TryGetValue(item, out string value) ? value : (selectedCharacters.Contains(item) ? "Selected" : "-"));
			ImGui.TableNextColumn();
			if (configuration.HuntLogs.CharacterSnapshots.TryGetValue(item, out HuntLogCharacterSnapshot value2))
			{
				DrawWrappedHuntLogTableText(GetCachedHuntLogSnapshotText(item, value2));
			}
			else
			{
				ImGui.TextDisabled("-");
			}
		}
	}

	private string GetCachedHuntLogSnapshotText(string character, HuntLogCharacterSnapshot snapshot)
	{
		if (huntLogSnapshotDisplayCache.TryGetValue(character, out HuntLogSnapshotDisplayCacheEntry value) && value.LastUpdatedUtc == snapshot.LastUpdatedUtc && value.ClassJobId == snapshot.ClassJobId && value.SelectedCombatJobId == snapshot.SelectedCombatJobId && value.Level == snapshot.Level && value.ClassLogRank == snapshot.ClassLogRank && value.GrandCompanyRank == snapshot.GrandCompanyRank && value.GrandCompanyLogRank == snapshot.GrandCompanyLogRank)
		{
			return value.Text;
		}
		string huntLogClassJobLabel = GetHuntLogClassJobLabel(snapshot.ClassJobId);
		string value2 = ((snapshot.SelectedCombatJobId != 0) ? GetHuntLogClassJobLabel(snapshot.SelectedCombatJobId) : "-");
		string value3 = ((snapshot.GrandCompanyRank >= 0) ? $"GC {snapshot.GrandCompanyRank}, log {snapshot.GrandCompanyLogRank + 1}" : "No GC");
		string text = $"{huntLogClassJobLabel}; selected {value2}; Lv {snapshot.Level}, class log {snapshot.ClassLogRank + 1}; {value3}";
		huntLogSnapshotDisplayCache[character] = new HuntLogSnapshotDisplayCacheEntry(snapshot.LastUpdatedUtc, snapshot.ClassJobId, snapshot.SelectedCombatJobId, snapshot.Level, snapshot.ClassLogRank, snapshot.GrandCompanyRank, snapshot.GrandCompanyLogRank, text);
		return text;
	}

	private static void DrawWrappedHuntLogTableText(string text)
	{
		float num = Math.Max(ImGui.GetContentRegionAvail().X, 1f);
		ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + num);
		ImGui.TextUnformatted(text);
		ImGui.PopTextWrapPos();
	}

	private void DrawHuntLogSettings(HuntLogSettings settings)
	{
		ImGui.TextColored(in colorSecondary, "Settings");
		bool v = settings.AutoGrandCompanyRankUp;
		if (ImGui.Checkbox("Auto GC rank-up", ref v))
		{
			settings.AutoGrandCompanyRankUp = v;
			configuration.Save();
		}
		bool v2 = settings.ResumeIncompleteRuns;
		if (ImGui.Checkbox("Resume incomplete runs", ref v2))
		{
			settings.ResumeIncompleteRuns = v2;
			configuration.Save();
		}
		int v3 = settings.StopAfterClassRank;
		if (ImGui.SliderInt("Stop after class rank", ref v3, 1, 5))
		{
			settings.StopAfterClassRank = Math.Clamp(v3, 1, 5);
			configuration.Save();
		}
		int v4 = settings.StopAfterGrandCompanyRank;
		if (ImGui.SliderInt("Stop after GC rank", ref v4, 1, 11))
		{
			settings.StopAfterGrandCompanyRank = Math.Clamp(v4, 1, 11);
			configuration.Save();
		}
		bool v5 = settings.SkipDutyMarks;
		if (ImGui.Checkbox("Skip duty marks", ref v5))
		{
			settings.SkipDutyMarks = v5;
			configuration.Save();
		}
		bool v6 = settings.SoloUnsyncedLogDuty;
		if (ImGui.Checkbox("Solo unsynced log duties", ref v6))
		{
			settings.SoloUnsyncedLogDuty = v6;
			configuration.Save();
		}
		bool v7 = settings.ReturnOnceDone;
		if (ImGui.Checkbox("Return once done", ref v7))
		{
			settings.ReturnOnceDone = v7;
			configuration.Save();
		}
		if (settings.ReturnOnceDone)
		{
			int currentItem = (int)settings.ReturnDestination;
			string[] names = Enum.GetNames<HuntLogReturnDestination>();
			if (ImGui.Combo("Return destination", ref currentItem, names, names.Length))
			{
				settings.ReturnDestination = (HuntLogReturnDestination)currentItem;
				configuration.Save();
			}
		}
		ImGuiHelpers.ScaledDummy(8f);
		ImGui.TextColored(in colorSecondary, "Combat Job");
		if (ImGui.RadioButton("Automatically select highest combat job", settings.CombatJobMode == HuntLogCombatJobMode.HighestCombatJob))
		{
			settings.CombatJobMode = HuntLogCombatJobMode.HighestCombatJob;
			configuration.Save();
		}
		if (ImGui.RadioButton("Use current combat job", settings.CombatJobMode == HuntLogCombatJobMode.CurrentCombatJob))
		{
			settings.CombatJobMode = HuntLogCombatJobMode.CurrentCombatJob;
			configuration.Save();
		}
		if (ImGui.RadioButton("Pick a combat job", settings.CombatJobMode == HuntLogCombatJobMode.SpecificJob))
		{
			settings.CombatJobMode = HuntLogCombatJobMode.SpecificJob;
			if (settings.PreferredCombatJobId == 0)
			{
				settings.PreferredCombatJobId = GetHuntLogCombatJobOptions().FirstOrDefault().Id;
			}
			configuration.Save();
		}
		if (settings.CombatJobMode == HuntLogCombatJobMode.SpecificJob)
		{
			DrawHuntLogCombatJobSelection(settings);
		}
		ImGuiHelpers.ScaledDummy(8f);
		ImGui.TextColored(in colorSecondary, "Movement and Combat");
		bool v8 = settings.UseMountBetweenMarks;
		if (ImGui.Checkbox("Use mount between marks", ref v8))
		{
			settings.UseMountBetweenMarks = v8;
			configuration.Save();
		}
		if (settings.UseMountBetweenMarks)
		{
			DrawHuntLogMountSelection(settings);
			int v9 = (int)settings.MountDistance;
			if (ImGui.SliderInt("Mount distance", ref v9, 10, 200))
			{
				settings.MountDistance = v9;
				configuration.Save();
			}
		}
		int v10 = (int)settings.GroundApproachDistance;
		if (ImGui.SliderInt("Ground-only target distance", ref v10, 5, 100, "%d yalms"))
		{
			settings.GroundApproachDistance = v10;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Loaded Hunt Log targets at or inside this distance are approached without mounting or flight.");
		}
		bool v11 = settings.SummonChocobo;
		if (ImGui.Checkbox("Summon chocobo companion", ref v11))
		{
			settings.SummonChocobo = v11;
			configuration.Save();
		}
		if (settings.SummonChocobo)
		{
			int currentItem2 = Array.IndexOf(HuntLogCompanionStances, settings.CompanionStance);
			if (currentItem2 < 0)
			{
				currentItem2 = 0;
			}
			if (ImGui.Combo("Companion stance", ref currentItem2, HuntLogCompanionStances, HuntLogCompanionStances.Length))
			{
				settings.CompanionStance = HuntLogCompanionStances[currentItem2];
				configuration.Save();
			}
			HuntLogAutomationService.CompanionUpkeepStatus companionUpkeepStatus = huntLogAutomationService.GetCompanionUpkeepStatus();
			string value = (companionUpkeepStatus.TimeLeft.HasValue ? FormatHuntLogCompanionTimer(companionUpkeepStatus.TimeLeft.Value) : "unavailable");
			string value2 = companionUpkeepStatus.GreensCount?.ToString() ?? "unavailable";
			string value3 = (companionUpkeepStatus.Enabled ? "enabled" : "disabled");
			ImU8String text = new ImU8String(45, 3);
			text.AppendLiteral("Saved: ");
			text.AppendFormatted(value3);
			text.AppendLiteral(" | Companion timer: ");
			text.AppendFormatted(value);
			text.AppendLiteral(" | Gysahl Greens: ");
			text.AppendFormatted(value2);
			ImGui.TextDisabled(text);
			if (!string.IsNullOrWhiteSpace(companionUpkeepStatus.Diagnostic))
			{
				ImGui.TextDisabled(companionUpkeepStatus.Diagnostic);
			}
		}
		ImGuiHelpers.ScaledDummy(8f);
		ImGui.TextColored(in colorSecondary, "Combat setup");
		bool v12 = settings.AutoSyncFateTargets;
		if (ImGui.Checkbox("Land, wait, and level sync FATE hunt targets", ref v12))
		{
			settings.AutoSyncFateTargets = v12;
			configuration.Save();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Enabled: land, dismount, and level sync configured FATE marks and runtime FATE variants before combat.\nDisabled: wait airborne and unsynced where possible; over-level FATE targets will not be engaged.");
		}
		FrenRiderAvailability frenRiderAvailability = huntLogAutomationService.GetFrenRiderAvailability();
		if (ImGui.RadioButton("Standard combat setup", settings.CombatMode == HuntLogCombatMode.Standard))
		{
			settings.CombatMode = HuntLogCombatMode.Standard;
			configuration.Save();
		}
		if (settings.CombatMode == HuntLogCombatMode.Standard)
		{
			ImGui.Indent();
			bool v13 = settings.EnableRotationSolverReborn;
			if (ImGui.Checkbox("Enable RSR while fighting", ref v13))
			{
				settings.EnableRotationSolverReborn = v13;
				configuration.Save();
			}
			bool v14 = settings.EnableVBMAI;
			if (ImGui.Checkbox("Enable VBM AI while fighting", ref v14))
			{
				settings.EnableVBMAI = v14;
				configuration.Save();
			}
			bool v15 = settings.EnableBMRAI;
			if (ImGui.Checkbox("Enable BMR AI while fighting", ref v15))
			{
				settings.EnableBMRAI = v15;
				configuration.Save();
			}
			ImGui.Unindent();
		}
		if (!frenRiderAvailability.CanSelect)
		{
			ImGui.BeginDisabled();
		}
		if (ImGui.RadioButton("Use FrenRider", settings.CombatMode == HuntLogCombatMode.FrenRider))
		{
			settings.CombatMode = HuntLogCombatMode.FrenRider;
			configuration.Save();
		}
		if (!frenRiderAvailability.CanSelect)
		{
			ImGui.EndDisabled();
		}
		ImGui.TextColored(frenRiderAvailability.CanSelect ? colorSuccess : ImGuiColors.DalamudYellow, frenRiderAvailability.Message);
		FrenRiderAvailabilityKind kind = frenRiderAvailability.Kind;
		if ((kind == FrenRiderAvailabilityKind.Missing || kind == FrenRiderAvailabilityKind.Incompatible) ? true : false)
		{
			string buf = "https://aethertek.io/x.json";
			ImGui.SetNextItemWidth(420f);
			ImGui.InputText("##FrenRiderRepository", ref buf, 256, ImGuiInputTextFlags.ReadOnly);
			ImGui.SameLine();
			if (ImGui.Button("Copy##FrenRiderRepository"))
			{
				ImGui.SetClipboardText(buf);
			}
		}
	}

	private void DrawHuntLogMountSelection(HuntLogSettings settings)
	{
		ImGui.TextUnformatted("Mount Name");
		string[] array = GetHuntLogMountNames();
		string selectedMount = settings.SelectedMount;
		ImGui.SetNextItemWidth(320f);
		if (!ImGui.BeginCombo("##HuntLogMountSelect", string.IsNullOrEmpty(selectedMount) ? "(none)" : selectedMount))
		{
			return;
		}
		ImGui.SetNextItemWidth(-1f);
		ImGui.InputText("##HuntLogMountSearch", ref huntLogMountSearch, 64);
		ImGui.Separator();
		ImGui.BeginChild("##HuntLogMountList", new Vector2(0f, 200f));
		for (int i = 0; i < array.Length; i++)
		{
			if (string.IsNullOrEmpty(huntLogMountSearch) || array[i].Contains(huntLogMountSearch, StringComparison.OrdinalIgnoreCase))
			{
				bool flag = array[i] == selectedMount;
				if (ImGui.Selectable(array[i], flag))
				{
					settings.SelectedMount = array[i];
					configuration.Save();
					huntLogMountSearch = string.Empty;
				}
				if (flag)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		}
		ImGui.EndChild();
		ImGui.EndCombo();
	}

	private void DrawHuntLogCombatJobSelection(HuntLogSettings settings)
	{
		(uint, string)[] array = GetHuntLogCombatJobOptions();
		int num = Array.FindIndex(array, ((uint Id, string Label) x) => x.Id == settings.PreferredCombatJobId);
		string text = ((num >= 0) ? array[num].Item2 : ((settings.PreferredCombatJobId == 0) ? "(select job)" : GetHuntLogClassJobLabel(settings.PreferredCombatJobId)));
		ImGui.SetNextItemWidth(320f);
		if (!ImGui.BeginCombo("Combat job##HuntLogCombatJob", text))
		{
			return;
		}
		(uint, string)[] array2 = array;
		for (int num2 = 0; num2 < array2.Length; num2++)
		{
			(uint, string) tuple = array2[num2];
			bool flag = tuple.Item1 == settings.PreferredCombatJobId;
			if (ImGui.Selectable(tuple.Item2, flag))
			{
				settings.PreferredCombatJobId = tuple.Item1;
				configuration.Save();
			}
			if (flag)
			{
				ImGui.SetItemDefaultFocus();
			}
		}
		ImGui.EndCombo();
	}

	private string[] GetHuntLogMountNames()
	{
		if (huntLogMountNames != null)
		{
			return huntLogMountNames;
		}
		try
		{
			List<string> list = new List<string> { "Mount Roulette" };
			ExcelSheet<Mount> excelSheet = dataManager.GetExcelSheet<Mount>();
			if (excelSheet != null)
			{
				foreach (Mount item in excelSheet)
				{
					string text = item.Singular.ToString();
					if (!string.IsNullOrWhiteSpace(text))
					{
						list.Add(text);
					}
				}
			}
			if (list.Count > 1)
			{
				list.Sort(1, list.Count - 1, StringComparer.OrdinalIgnoreCase);
			}
			huntLogMountNames = list.ToArray();
			return huntLogMountNames;
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Failed to load mount names: " + ex.Message);
			huntLogMountNames = new string[2] { "Mount Roulette", "Company Chocobo" };
			return huntLogMountNames;
		}
	}

	private (uint Id, string Label)[] GetHuntLogCombatJobOptions()
	{
		if (huntLogCombatJobOptions != null)
		{
			return huntLogCombatJobOptions;
		}
		try
		{
			ExcelSheet<ClassJob> sheet = dataManager.GetExcelSheet<ClassJob>();
			huntLogCombatJobOptions = (from jobId in JobClassification.CombatJobs
				select ((uint Id, string Label))(Id: jobId, Label: TryGetHuntLogClassJobLabel(sheet, jobId)) into x
				where !string.IsNullOrWhiteSpace(x.Label)
				orderby x.Id
				select x).ToArray();
			return huntLogCombatJobOptions;
		}
		catch (Exception ex)
		{
			log.Warning("[HuntLogs] Failed to load combat jobs: " + ex.Message);
			huntLogCombatJobOptions = Array.Empty<(uint, string)>();
			return huntLogCombatJobOptions;
		}
	}

	private string GetHuntLogClassJobLabel(uint classJobId)
	{
		try
		{
			return TryGetHuntLogClassJobLabel(dataManager.GetExcelSheet<ClassJob>(), classJobId);
		}
		catch
		{
			return (classJobId == 0) ? "Unknown" : $"Job {classJobId}";
		}
	}

	private static string TryGetHuntLogClassJobLabel(ExcelSheet<ClassJob> sheet, uint classJobId)
	{
		if (classJobId == 0)
		{
			return "Unknown";
		}
		if (!sheet.TryGetRow(classJobId, out var row))
		{
			return $"Job {classJobId}";
		}
		string text = row.Abbreviation.ToString();
		string text2 = row.Name.ToString();
		if (string.IsNullOrWhiteSpace(text))
		{
			if (!string.IsNullOrWhiteSpace(text2))
			{
				return text2;
			}
			return $"Job {classJobId}";
		}
		if (!string.IsNullOrWhiteSpace(text2))
		{
			return text + " (" + text2 + ")";
		}
		return text;
	}

	private static string FormatHuntLogCompanionTimer(float seconds)
	{
		if (seconds <= 0f)
		{
			return "inactive";
		}
		TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
		return $"{(int)timeSpan.TotalMinutes}:{timeSpan.Seconds:00}";
	}

	private void DrawInitialSetupTab()
	{
		RefreshInitialSetupDependencyChecks();
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Initial Setup");
		ImGui.PopStyleColor();
		ImU8String text = new ImU8String(10, 1);
		text.AppendLiteral("Step ");
		text.AppendFormatted(initialSetupStep + 1);
		text.AppendLiteral(" of 6");
		ImGui.TextDisabled(text);
		ImGui.ProgressBar((float)(initialSetupStep + 1) / 6f, new Vector2(-1f, 5f), string.Empty);
		ImGui.Spacing();
		using (ImRaii.ImChild imChild = ImRaii.Child("InitialSetupContent", new Vector2(0f, -48f), border: false))
		{
			if (imChild.Success)
			{
				switch (initialSetupStep)
				{
				case 0:
					DrawInitialSetupWelcome();
					break;
				case 1:
					DrawInitialSetupDependencies();
					break;
				case 2:
					DrawInitialSetupCharacters();
					break;
				case 3:
					DrawInitialSetupMultiboxing();
					break;
				case 4:
					DrawInitialSetupCombat();
					break;
				case 5:
					DrawInitialSetupFinish();
					break;
				}
			}
		}
		if (initialSetupStep > 0 && ImGui.Button("Back", new Vector2(90f, 28f)))
		{
			initialSetupStep--;
		}
		if (initialSetupStep > 0)
		{
			ImGui.SameLine();
		}
		if (initialSetupStep < 5)
		{
			if (ImGui.Button((initialSetupStep == 0) ? "Start Setup" : "Next", new Vector2(110f, 28f)))
			{
				initialSetupStep++;
			}
			ImGui.SameLine();
			if (ImGui.Button("Continue later", new Vector2(120f, 28f)))
			{
				configuration.DismissedInitialSetupVersion = 1;
				configuration.Save();
				selectedTab = selectedDCFilter;
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Do not open the wizard automatically again for this setup version. You can reopen it from Plugin > Initial Setup.");
			}
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorSecondary);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorPrimary);
			ImGui.PushStyleColor(ImGuiCol.Text, colorDarkButtonText);
			if (ImGui.Button("Finish Setup", new Vector2(130f, 28f)))
			{
				configuration.CompletedInitialSetupVersion = 1;
				configuration.DismissedInitialSetupVersion = 1;
				configuration.Save();
				selectedTab = selectedDCFilter;
				initialSetupStep = 0;
			}
			ImGui.PopStyleColor(3);
		}
	}

	private void DrawInitialSetupWelcome()
	{
		ImGui.TextWrapped("This wizard checks the plugins and character data used by Questionable Companion, then guides you through the existing Multiboxing and combat settings. It does not create a second configuration: every choice changes the same settings used by the normal menus.");
		ImGui.Spacing();
		ImGui.TextColored(in colorSecondary, "The wizard can:");
		ImGui.BulletText("Add required Dalamud repositories after you click the action");
		ImGui.BulletText("Install and activate missing plugins through Dalamud");
		ImGui.BulletText("Run the normal Refresh Data operation");
		ImGui.BulletText("Configure this client as a Quester or High-Level Helper");
		ImGui.BulletText("Configure standard combat and Solo Duty commands");
	}

	private void DrawInitialSetupDependencies()
	{
		ImGui.TextColored(in colorSecondary, "Required plugins");
		ImGui.TextWrapped("Repository and installation actions only run when you click them. Newly installed plugins are loaded immediately by Dalamud. An already installed but disabled plugin can be enabled in the Plugin Installer.");
		ImGui.Spacing();
		if (ImGui.BeginTable("InitialSetupDependencies", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
		{
			ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 150f);
			ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Repository", ImGuiTableColumnFlags.WidthFixed, 105f);
			ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 135f);
			ImGui.TableHeadersRow();
			string[] requiredSetupDependencies = RequiredSetupDependencies;
			foreach (string internalName in requiredSetupDependencies)
			{
				DependencyEntry dependency = DependencyEntries.First((DependencyEntry entry) => string.Equals(entry.InternalName, internalName, StringComparison.Ordinal));
				DrawInitialSetupDependencyRow(dependency);
			}
			ImGui.EndTable();
		}
		if (!string.IsNullOrWhiteSpace(initialSetupStatus))
		{
			ImGui.Spacing();
			ImGui.TextColored(initialSetupStatusSucceeded ? colorSuccess : colorAccent, initialSetupStatus);
		}
		ImGui.Spacing();
		if (ImGui.Button("Retry checks"))
		{
			RefreshInitialSetupDependencyChecks(force: true);
			initialSetupStatus = "Dependency states refreshed.";
			initialSetupStatusSucceeded = true;
		}
		ImGui.SameLine();
		if (ImGui.Button("Open Plugin Installer"))
		{
			Plugin.CommandManager.ProcessCommand("/xlplugins");
		}
	}

	private void DrawInitialSetupDependencyRow(DependencyEntry dependency)
	{
		IExposedPlugin exposedPlugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin candidate) => string.Equals(candidate.InternalName, dependency.InternalName, StringComparison.Ordinal));
		bool flag = exposedPlugin?.IsLoaded ?? false;
		bool flag2 = !string.Equals(dependency.InternalName, "Questionable", StringComparison.Ordinal) || (flag && initialSetupQuestionableReady);
		bool flag3 = setupInstallingDependencies.ContainsKey(dependency.InternalName);
		bool flag4 = !setupInstallingDependencies.IsEmpty;
		string text = ((exposedPlugin == null) ? "Not installed" : ((!flag) ? "Installed / disabled" : (flag2 ? "Ready" : "Wiggly repository required")));
		Vector4 col = ((exposedPlugin != null && flag && flag2) ? colorSuccess : ((exposedPlugin == null) ? colorAccent : colorSecondary));
		ImGui.TableNextRow();
		ImGui.TableNextColumn();
		ImGui.TextUnformatted(dependency.Name);
		ImGui.TableNextColumn();
		ImGui.TextColored(in col, flag3 ? "Installing..." : text);
		ImGui.TableNextColumn();
		bool flag5;
		try
		{
			flag5 = DalamudReflector.HasRepo(dependency.RepositoryUrl);
		}
		catch
		{
			flag5 = false;
		}
		if (flag5)
		{
			ImGui.TextColored(in colorSuccess, "Added");
		}
		else
		{
			ImGui.BeginDisabled(flag4);
			ImU8String label = new ImU8String(19, 1);
			label.AppendLiteral("Add repo##SetupRepo");
			label.AppendFormatted(dependency.InternalName);
			if (ImGui.SmallButton(label))
			{
				TryAddSetupRepository(dependency);
			}
			ImGui.EndDisabled();
		}
		ImGui.TableNextColumn();
		ImGui.BeginDisabled(flag4 || (exposedPlugin != null && flag && flag2));
		if (exposedPlugin == null)
		{
			ImU8String label2 = new ImU8String(32, 1);
			label2.AppendLiteral("Install & activate##SetupInstall");
			label2.AppendFormatted(dependency.InternalName);
			if (ImGui.Button(label2))
			{
				InstallSetupDependencyAsync(dependency);
			}
		}
		else if (!flag || !flag2)
		{
			ImU8String label3 = new ImU8String(27, 1);
			label3.AppendLiteral("Open Installer##SetupEnable");
			label3.AppendFormatted(dependency.InternalName);
			if (ImGui.Button(label3))
			{
				Plugin.CommandManager.ProcessCommand("/xlplugins");
			}
		}
		else
		{
			ImGui.TextColored(in colorSuccess, "Ready");
		}
		ImGui.EndDisabled();
	}

	private void TryAddSetupRepository(DependencyEntry dependency)
	{
		try
		{
			DalamudReflector.AddRepo(dependency.RepositoryUrl, enabled: true);
			DalamudReflector.SaveDalamudConfig();
			DalamudReflector.ReloadPluginMasters();
			initialSetupStatus = "Added and enabled the " + dependency.Name + " repository.";
			initialSetupStatusSucceeded = true;
		}
		catch (Exception ex)
		{
			initialSetupStatus = "Could not add the " + dependency.Name + " repository: " + ex.Message;
			initialSetupStatusSucceeded = false;
			log.Error(ex, initialSetupStatus);
		}
	}

	private async Task InstallSetupDependencyAsync(DependencyEntry dependency)
	{
		setupInstallingDependencies.TryAdd(dependency.InternalName, 0);
		initialSetupStatus = "Installing " + dependency.Name + "...";
		initialSetupStatusSucceeded = true;
		try
		{
			if (!DalamudReflector.HasRepo(dependency.RepositoryUrl))
			{
				DalamudReflector.AddRepo(dependency.RepositoryUrl, enabled: true);
				DalamudReflector.SaveDalamudConfig();
				DalamudReflector.ReloadPluginMasters();
			}
			bool flag = await InstallDalamudPluginAsync(dependency.RepositoryUrl, dependency.InternalName);
			DalamudReflector.SaveDalamudConfig();
			initialSetupStatus = (flag ? ("Installed and activated " + dependency.Name + ".") : ("Dalamud could not install " + dependency.Name + ". Check the plugin log or use the Plugin Installer."));
			initialSetupStatusSucceeded = flag;
		}
		catch (Exception ex)
		{
			initialSetupStatus = "Could not install " + dependency.Name + ": " + ex.Message;
			initialSetupStatusSucceeded = false;
			log.Error(ex, initialSetupStatus);
		}
		finally
		{
			setupInstallingDependencies.TryRemove(dependency.InternalName, out var _);
		}
	}

	private static async Task<bool> InstallDalamudPluginAsync(string repositoryUrl, string internalName)
	{
		object obj = (await DalamudReflector.GetPluginMaster(repositoryUrl))?.FirstOrDefault((object candidate) => string.Equals(candidate.GetType().GetProperty("InternalName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(candidate) as string, internalName, StringComparison.Ordinal));
		if (obj == null)
		{
			throw new InvalidOperationException("Plugin '" + internalName + "' was not found in the repository manifest.");
		}
		object pluginManager = DalamudReflector.GetPluginManager();
		MethodInfo methodInfo = (from method in pluginManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
			where method.Name == "InstallPluginAsync"
			orderby method.GetParameters().Length
			select method).FirstOrDefault(delegate(MethodInfo method)
		{
			int num = method.GetParameters().Length;
			return (uint)(num - 3) <= 1u;
		}) ?? throw new MissingMethodException(pluginManager.GetType().FullName, "InstallPluginAsync(RemotePluginManifest, bool, PluginLoadReason[, Guid?])");
		ParameterInfo[] parameters = methodInfo.GetParameters();
		object obj2 = Enum.Parse(parameters[2].ParameterType, "Installer");
		object[] parameters2 = ((parameters.Length != 3) ? new object[4] { obj, false, obj2, null } : new object[3] { obj, false, obj2 });
		object obj3 = methodInfo.Invoke(pluginManager, parameters2);
		if (!(obj3 is Task installTask))
		{
			throw new InvalidOperationException("Dalamud did not return an installation task.");
		}
		await installTask;
		object obj4 = installTask.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(installTask) ?? throw new InvalidOperationException("Dalamud completed installation without returning the installed plugin.");
		return obj4.GetType().GetProperty("IsLoaded", BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj4) as bool? == true;
	}

	private void DrawInitialSetupCharacters()
	{
		ImGui.TextColored(in colorSecondary, "Character data");
		ImGui.TextWrapped("Refresh Data uses the exact same operation as the Characters tab. AutoRetainer provides the roster; XADB enriches the saved character progress used by filters and rotations.");
		ImGui.Spacing();
		ImU8String text = new ImU8String(21, 1);
		text.AppendLiteral("Characters detected: ");
		text.AppendFormatted(registeredCharacters.Count);
		ImGui.TextUnformatted(text);
		foreach (string item in availableDataCenters.Where((string value) => value != "All"))
		{
			ImU8String text2 = new ImU8String(2, 2);
			text2.AppendFormatted(item);
			text2.AppendLiteral(": ");
			text2.AppendFormatted(GetCharacterCountForDC(item));
			ImGui.BulletText(text2);
		}
		ImGui.Spacing();
		if (ImGui.Button("Refresh Data", new Vector2(130f, 28f)))
		{
			RefreshCharacterList(forceIpcCheck: true);
			initialSetupStatus = $"Refresh Data completed: {registeredCharacters.Count} character(s) detected.";
			initialSetupStatusSucceeded = registeredCharacters.Count > 0;
		}
		if (!string.IsNullOrWhiteSpace(initialSetupStatus))
		{
			ImGui.Spacing();
			ImGui.TextColored(initialSetupStatusSucceeded ? colorSuccess : colorAccent, initialSetupStatus);
		}
	}

	private void DrawInitialSetupMultiboxing()
	{
		Configuration configuration = this.configuration;
		ImGui.TextColored(in colorSecondary, "Multiboxing");
		ImGui.TextWrapped("Choose the role and the helper-party settings this client will actually use during Companion rotations.");
		ImGui.Spacing();
		int num = (configuration.IsQuester ? 1 : (configuration.IsHighLevelHelper ? 2 : 0));
		if (ImGui.RadioButton("Disabled", num == 0))
		{
			ApplyInitialSetupRole(isQuester: false, isHelper: false);
		}
		ImGui.SameLine();
		if (ImGui.RadioButton("Quester / Coordinator", num == 1))
		{
			ApplyInitialSetupRole(isQuester: true, isHelper: false);
		}
		ImGui.SameLine();
		if (ImGui.RadioButton("High-Level Helper", num == 2))
		{
			ApplyInitialSetupRole(isQuester: false, isHelper: true);
		}
		if (configuration.IsQuester)
		{
			ImGui.Spacing();
			ImGui.TextColored(in colorSecondary, "Dungeon automation");
			bool v = configuration.EnableAutoDutyUnsynced;
			if (ImGui.Checkbox("Enable Auto Duty (Unsynced)", ref v))
			{
				configuration.EnableAutoDutyUnsynced = v;
				configuration.EnableARRPrimalCheck = v;
				configuration.Save();
				plugin.GetDungeonAutomation()?.SetDutyModeBasedOnConfig();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Configures Questionable for Unsync Party before helper-party duties. Rotation completion resets it to Support.");
			}
			if (configuration.EnableAutoDutyUnsynced)
			{
				int v2 = configuration.AutoDutyPartySize;
				ImGui.SetNextItemWidth(220f);
				if (ImGui.SliderInt("Minimum party size", ref v2, 1, 4))
				{
					configuration.AutoDutyPartySize = v2;
					configuration.Save();
				}
				DependencyEntry dependency = DependencyEntries.First((DependencyEntry entry) => string.Equals(entry.InternalName, "AutoDuty", StringComparison.Ordinal));
				IExposedPlugin exposedPlugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin candidate) => string.Equals(candidate.InternalName, "AutoDuty", StringComparison.Ordinal));
				if (exposedPlugin == null || !exposedPlugin.IsLoaded)
				{
					ImGui.TextColored(in colorSecondary, (exposedPlugin == null) ? "AutoDuty is not installed." : "AutoDuty is installed but disabled.");
					ImGui.SameLine();
					if (exposedPlugin == null)
					{
						ImGui.BeginDisabled(!setupInstallingDependencies.IsEmpty);
						if (ImGui.SmallButton("Install & activate AutoDuty"))
						{
							InstallSetupDependencyAsync(dependency);
						}
						ImGui.EndDisabled();
					}
					else if (ImGui.SmallButton("Open Plugin Installer"))
					{
						Plugin.CommandManager.ProcessCommand("/xlplugins");
					}
				}
			}
			ImGui.Spacing();
			ImGui.TextColored(in colorSecondary, "Helper selection");
			if (ImGui.RadioButton("First available", configuration.HelperSelection == HelperSelectionMode.Auto))
			{
				configuration.HelperSelection = HelperSelectionMode.Auto;
				configuration.PreferredHelper = string.Empty;
				configuration.ManualHelperName = string.Empty;
				configuration.Save();
			}
			ImGui.SameLine();
			if (ImGui.RadioButton("Select helper", configuration.HelperSelection == HelperSelectionMode.Dropdown))
			{
				configuration.HelperSelection = HelperSelectionMode.Dropdown;
				configuration.ManualHelperName = string.Empty;
				configuration.Save();
			}
			ImGui.SameLine();
			if (ImGui.RadioButton("Manual input", configuration.HelperSelection == HelperSelectionMode.ManualInput))
			{
				configuration.HelperSelection = HelperSelectionMode.ManualInput;
				configuration.PreferredHelper = string.Empty;
				configuration.Save();
			}
			List<(string, ushort)> availableHelpers = plugin.GetAvailableHelpers();
			if (configuration.HelperSelection == HelperSelectionMode.Dropdown)
			{
				string text = (string.IsNullOrWhiteSpace(configuration.PreferredHelper) ? "Select a detected helper..." : configuration.PreferredHelper);
				ImGui.SetNextItemWidth(300f);
				if (ImGui.BeginCombo("##SetupPreferredHelper", text))
				{
					foreach (var item in availableHelpers)
					{
						string text2 = item.Item1 + "@" + WorldNameHelper.GetWorldName(item.Item2);
						if (ImGui.Selectable(text2, configuration.PreferredHelper == text2))
						{
							configuration.PreferredHelper = text2;
							configuration.Save();
						}
					}
					if (availableHelpers.Count == 0)
					{
						ImGui.TextDisabled("No helpers detected.");
					}
					ImGui.EndCombo();
				}
			}
			else if (configuration.HelperSelection == HelperSelectionMode.ManualInput)
			{
				string buf = configuration.ManualHelperName;
				ImGui.SetNextItemWidth(300f);
				if (ImGui.InputTextWithHint("##SetupManualHelper", "Character Name@World", ref buf, 100))
				{
					configuration.ManualHelperName = buf;
					configuration.Save();
				}
				ImGui.TextDisabled("Manual input is used for dungeon invites only; Chauffeur and Following require IPC discovery.");
			}
			bool v3 = configuration.EnableFreeTrialHelperInvite;
			if (ImGui.Checkbox("Free Trial Mode (helper invites the Quester)", ref v3))
			{
				configuration.EnableFreeTrialHelperInvite = v3;
				configuration.Save();
			}
		}
		else if (configuration.IsHighLevelHelper)
		{
			ImGui.Spacing();
			if (configuration.IsHelperAutomationActive)
			{
				ImGui.TextColored(in colorSuccess, "Helper logic is active");
				if (ImGui.Button("Deactivate Helper"))
				{
					plugin.GetHelperManager()?.SetHelperAutomationActive(active: false);
				}
			}
			else
			{
				ImGui.TextColored(in colorSecondary, "Helper logic is inactive");
				ImGui.TextWrapped("Activate it when this client should respond to Questers and control AutoDuty for helper runs.");
				if (ImGui.Button("Activate Helper"))
				{
					plugin.GetHelperManager()?.SetHelperAutomationActive(active: true);
				}
			}
			bool v4 = configuration.AlwaysAutoAcceptInvites;
			if (ImGui.Checkbox("Always auto-accept party invites", ref v4))
			{
				configuration.AlwaysAutoAcceptInvites = v4;
				configuration.Save();
			}
		}
		ImGui.Spacing();
		ImGui.TextColored(in colorSecondary, "Communication");
		ImGui.BulletText("Local IPC: always available for clients on this PC");
		bool v5 = configuration.EnableLANHelpers;
		if (ImGui.Checkbox("Enable LAN Helpers for clients on another PC", ref v5))
		{
			configuration.EnableLANHelpers = v5;
			if (!v5 && configuration.StartLANServer)
			{
				configuration.StartLANServer = false;
				plugin.ToggleLANServer(enable: false);
			}
			configuration.Save();
		}
		if (configuration.EnableLANHelpers)
		{
			int data = configuration.LANServerPort;
			ImGui.SetNextItemWidth(160f);
			if (ImGui.InputInt("LAN port", ref data))
			{
				configuration.LANServerPort = Math.Clamp(data, 1024, 65535);
				configuration.Save();
			}
			bool v6 = configuration.StartLANServer;
			if (ImGui.Checkbox("Start LAN server on this client", ref v6))
			{
				configuration.StartLANServer = v6;
				configuration.Save();
				plugin.ToggleLANServer(v6);
			}
		}
		ImGui.Spacing();
		List<(string, ushort)> availableHelpers2 = plugin.GetAvailableHelpers();
		ImU8String text3 = new ImU8String(18, 1);
		text3.AppendLiteral("Detected helpers: ");
		text3.AppendFormatted(availableHelpers2.Count);
		ImGui.TextUnformatted(text3);
		foreach (var item2 in availableHelpers2)
		{
			ImU8String text4 = new ImU8String(1, 2);
			text4.AppendFormatted(item2.Item1);
			text4.AppendLiteral("@");
			text4.AppendFormatted(WorldNameHelper.GetWorldName(item2.Item2));
			ImGui.BulletText(text4);
		}
		if (ImGui.Button("Open advanced Multiboxing settings"))
		{
			returnToInitialSetupFromMultiboxing = true;
			selectedTab = 12;
		}
	}

	private void ApplyInitialSetupRole(bool isQuester, bool isHelper)
	{
		bool isHighLevelHelper = configuration.IsHighLevelHelper;
		configuration.IsQuester = isQuester;
		configuration.IsHighLevelHelper = isHelper;
		if (!isHelper)
		{
			configuration.HelperAutomationEnabled = false;
		}
		configuration.Save();
		plugin.GetHelperManager()?.HandleLocalRoleChanged(isHighLevelHelper);
	}

	private void DrawInitialSetupCombat()
	{
		Configuration configuration = this.configuration;
		ImGui.TextColored(in colorSecondary, "Combat and Solo Duties");
		ImGui.TextWrapped("These are the existing Standard Stop Point and Solo Duty settings. Questionable handles normal quest combat.");
		ImGui.Spacing();
		bool v = configuration.EnableCombatHandling;
		if (ImGui.Checkbox("Enable Standard Stop Point combat handling", ref v))
		{
			configuration.EnableCombatHandling = v;
			configuration.Save();
		}
		if (configuration.EnableCombatHandling)
		{
			DrawInitialSetupCombatMode("Standard Stop Point", soloDuty: false, ref stopPointCombatStartCommandInput, ref stopPointCombatEndCommandInput, "SetupStandard");
		}
		ImGui.Separator();
		DrawInitialSetupCombatMode("Solo Duty", soloDuty: true, ref soloDutyCombatStartCommandInput, ref soloDutyCombatEndCommandInput, "SetupSolo");
	}

	private void DrawInitialSetupCombatMode(string label, bool soloDuty, ref string startInput, ref string endInput, string id)
	{
		CombatHandlingMode combatHandlingMode = (soloDuty ? configuration.SoloDutyCombatHandlingMode : configuration.StopPointCombatHandlingMode);
		bool v = (soloDuty ? configuration.EnableSoloDutyRSR : configuration.EnableStopPointRSR);
		bool v2 = (soloDuty ? configuration.EnableSoloDutyVBM : configuration.EnableStopPointVBM);
		bool v3 = (soloDuty ? configuration.EnableSoloDutyBMRAI : configuration.EnableStopPointBMRAI);
		string savedCommands = (soloDuty ? configuration.SoloDutyCombatStartCommands : configuration.StopPointCombatStartCommands);
		string savedCommands2 = (soloDuty ? configuration.SoloDutyCombatEndCommands : configuration.StopPointCombatEndCommands);
		ImGui.TextColored(in colorSecondary, label);
		ImU8String label2 = new ImU8String(38, 1);
		label2.AppendLiteral("Use supported combat backends##");
		label2.AppendFormatted(id);
		label2.AppendLiteral("Default");
		if (ImGui.RadioButton(label2, combatHandlingMode == CombatHandlingMode.DefaultBackends))
		{
			combatHandlingMode = CombatHandlingMode.DefaultBackends;
			if (soloDuty)
			{
				configuration.SoloDutyCombatHandlingMode = combatHandlingMode;
			}
			else
			{
				configuration.StopPointCombatHandlingMode = combatHandlingMode;
			}
			configuration.Save();
		}
		ImGui.SameLine();
		ImU8String label3 = new ImU8String(24, 1);
		label3.AppendLiteral("Use own commands##");
		label3.AppendFormatted(id);
		label3.AppendLiteral("Custom");
		if (ImGui.RadioButton(label3, combatHandlingMode == CombatHandlingMode.CustomCommands))
		{
			combatHandlingMode = CombatHandlingMode.CustomCommands;
			if (soloDuty)
			{
				configuration.SoloDutyCombatHandlingMode = combatHandlingMode;
			}
			else
			{
				configuration.StopPointCombatHandlingMode = combatHandlingMode;
			}
			configuration.Save();
		}
		if (combatHandlingMode == CombatHandlingMode.DefaultBackends)
		{
			ImU8String label4 = new ImU8String(8, 1);
			label4.AppendLiteral("RSR##");
			label4.AppendFormatted(id);
			label4.AppendLiteral("RSR");
			int num = 0 | (ImGui.Checkbox(label4, ref v) ? 1 : 0);
			ImGui.SameLine();
			ImU8String label5 = new ImU8String(8, 1);
			label5.AppendLiteral("VBM##");
			label5.AppendFormatted(id);
			label5.AppendLiteral("VBM");
			int num2 = num | (ImGui.Checkbox(label5, ref v2) ? 1 : 0);
			ImGui.SameLine();
			ImU8String label6 = new ImU8String(11, 1);
			label6.AppendLiteral("BMR AI##");
			label6.AppendFormatted(id);
			label6.AppendLiteral("BMR");
			int num3 = num2 | (ImGui.Checkbox(label6, ref v3) ? 1 : 0);
			if (!v && !v2 && !v3)
			{
				v = true;
			}
			if (num3 != 0)
			{
				if (soloDuty)
				{
					configuration.EnableSoloDutyRSR = v;
					configuration.EnableSoloDutyVBM = v2;
					configuration.EnableSoloDutyBMRAI = v3;
				}
				else
				{
					configuration.EnableStopPointRSR = v;
					configuration.EnableStopPointVBM = v2;
					configuration.EnableStopPointBMRAI = v3;
				}
				configuration.Save();
			}
			return;
		}
		DrawStopPointCommandList("Commands when " + label + " starts", id + "Start", ref startInput, savedCommands, delegate(string commands)
		{
			if (soloDuty)
			{
				configuration.SoloDutyCombatStartCommands = commands;
			}
			else
			{
				configuration.StopPointCombatStartCommands = commands;
			}
			configuration.Save();
		});
		DrawStopPointCommandList("Commands when " + label + " ends", id + "End", ref endInput, savedCommands2, delegate(string commands)
		{
			if (soloDuty)
			{
				configuration.SoloDutyCombatEndCommands = commands;
			}
			else
			{
				configuration.StopPointCombatEndCommands = commands;
			}
			configuration.Save();
		});
	}

	private void DrawInitialSetupFinish()
	{
		ImGui.TextColored(in colorSecondary, "Setup summary");
		int num = RequiredSetupDependencies.Count(delegate(string internalName)
		{
			IExposedPlugin? exposedPlugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin candidate) => string.Equals(candidate.InternalName, internalName, StringComparison.Ordinal));
			if (exposedPlugin == null || !exposedPlugin.IsLoaded)
			{
				return false;
			}
			return internalName != "Questionable" || initialSetupQuestionableReady;
		});
		DrawInitialSetupSummaryLine(num == RequiredSetupDependencies.Length, $"Required plugins: {num}/{RequiredSetupDependencies.Length} ready");
		DrawInitialSetupSummaryLine(registeredCharacters.Count > 0, $"Characters detected: {registeredCharacters.Count}");
		DrawInitialSetupSummaryLine(configuration.IsQuester || configuration.IsHighLevelHelper, configuration.IsQuester ? "Multiboxing role: Quester / Coordinator" : (configuration.IsHighLevelHelper ? ("Multiboxing role: High-Level Helper (" + (configuration.IsHelperAutomationActive ? "active" : "inactive") + ")") : "Multiboxing: disabled"));
		ImGui.Spacing();
		ImGui.TextColored(in colorSecondary, "Stop Points");
		ImGui.TextWrapped("On an initial installation, Stop Points are pulled from Questionable. The Companion only offers an upload when local points actually exist from an older setup or migration.");
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Open Questionable Settings"))
		{
			Plugin.CommandManager.ProcessCommand("/qst config");
			log.Information("[InitialSetup] Opened Questionable settings for Stop Point configuration");
		}
		ImGui.PopStyleColor(2);
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Open Questionable to configure Stop Points, then import them into the Companion.");
		}
		ImGui.SameLine();
		if (ImGui.Button("Import from Questionable"))
		{
			questRotationService.ImportStopPointsFromQuestionable();
			initialSetupStatus = "Imported the current Stop Points from Questionable in Pause mode.";
			initialSetupStatusSucceeded = true;
		}
		if (configuration.StopPoints.Count > 0)
		{
			ImGui.SameLine();
			if (ImGui.Button("Import local Stop Points into Questionable"))
			{
				StopPointImportResult stopPointImportResult = questRotationService.ImportCompanionStopPointsIntoQuestionable();
				initialSetupStatus = (stopPointImportResult.Succeeded ? $"Imported or refreshed {stopPointImportResult.Added + stopPointImportResult.Updated} Stop Point(s) in Questionable." : (stopPointImportResult.ErrorMessage ?? "Questionable did not accept the local Stop Points."));
				initialSetupStatusSucceeded = stopPointImportResult.Succeeded;
			}
		}
		if (!string.IsNullOrWhiteSpace(initialSetupStatus))
		{
			ImGui.Spacing();
			ImGui.TextColored(initialSetupStatusSucceeded ? colorSuccess : colorAccent, initialSetupStatus);
		}
	}

	private void DrawInitialSetupSummaryLine(bool ready, string text)
	{
		ImGui.TextColored(ready ? colorSuccess : colorSecondary, ready ? "Ready" : "Attention");
		ImGui.SameLine(90f);
		ImGui.TextUnformatted(text);
	}

	private void RefreshInitialSetupDependencyChecks(bool force = false)
	{
		if (force || !(DateTime.UtcNow - initialSetupLastDependencyCheck < TimeSpan.FromSeconds(1L)))
		{
			initialSetupLastDependencyCheck = DateTime.UtcNow;
			initialSetupQuestionableReady = plugin.QuestionableIPC.TryEnsureAvailableSilent() && plugin.QuestionableIPC.ValidateFeatureCompatibility();
		}
	}

	private void DrawMultiboxingTab()
	{
		if (returnToInitialSetupFromMultiboxing)
		{
			if (ImGui.Button("Back to Initial Setup"))
			{
				returnToInitialSetupFromMultiboxing = false;
				selectedTab = 17;
			}
			ImGui.Separator();
			ImGuiHelpers.ScaledDummy(5f);
		}
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Multiboxing Settings");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		using ImRaii.ImChild imChild = ImRaii.Child("MultiboxingScrollArea", new Vector2(0f, 0f), border: false);
		if (!imChild.Success)
		{
			return;
		}
		Configuration config = plugin.Configuration;
		DrawSettingSection("Dungeon Automation", delegate
		{
			bool enableAutoDutyUnsynced = config.EnableAutoDutyUnsynced;
			config.EnableAutoDutyUnsynced = DrawSettingWithInfo("Enable Auto Duty (Unsynced)", config.EnableAutoDutyUnsynced, "Automatically handles dungeon entries and party formation.\nQuestionable 7.5.6+ already handles safe overleveled 4-player duties.\nUpdates Questionable's default Duty Mode immediately: enabled = Unsync Party, disabled = Support.\nRotation stop/completion resets it to Support; the next start reapplies Unsync Party while enabled.\nEnable this only for Companion helper-party orchestration or unsupported duties.\nUses AutoDuty plugin for unsynced dungeon runs.\nIncludes automatic check for ARR Hard Mode Primals (Ifrit/Garuda/Titan).\nImpact: Dungeons will be automated during quest rotation.");
			if (config.EnableAutoDutyUnsynced != enableAutoDutyUnsynced)
			{
				config.EnableARRPrimalCheck = config.EnableAutoDutyUnsynced;
				config.Save();
				plugin.GetDungeonAutomation()?.SetDutyModeBasedOnConfig();
			}
			if (config.EnableAutoDutyUnsynced)
			{
				ImGui.Indent();
				int v = config.AutoDutyPartySize;
				if (ImGui.SliderInt("Minimum Party Size", ref v, 1, 4))
				{
					config.AutoDutyPartySize = v;
					config.Save();
				}
				DrawInfoIcon("Minimum number of party members required before entering dungeon.");
				int v2 = config.AutoDutyMaxWaitForParty;
				if (ImGui.SliderInt("Max Wait for Party (seconds)", ref v2, 10, 120))
				{
					config.AutoDutyMaxWaitForParty = v2;
					config.Save();
				}
				DrawInfoIcon("Maximum time to wait for party members before timing out.");
				int v3 = config.AutoDutyReInviteInterval;
				if (ImGui.SliderInt("Re-Invite Interval (seconds)", ref v3, 5, 60))
				{
					config.AutoDutyReInviteInterval = v3;
					config.Save();
				}
				DrawInfoIcon("How often to re-send party invites if members don't join.");
				int v4 = config.AutoLeaveDelaySeconds;
				if (ImGui.SliderInt("Auto Leave Delay (seconds)", ref v4, 0, 30))
				{
					config.AutoLeaveDelaySeconds = v4;
					config.Save();
				}
				DrawInfoIcon("Delay before auto-leaving the duty after completion.\nOnly active during Dungeon Automation + Quest Rotation.");
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.Unindent();
			}
		}, config.EnableAutoDutyUnsynced);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Multi-Client Role", delegate
		{
			ImGui.TextWrapped("Select your role for multi-client features (party management, chauffeur mode):");
			ImGuiHelpers.ScaledDummy(5f);
			int num = 0;
			if (config.IsQuester)
			{
				num = 1;
			}
			else if (config.IsHighLevelHelper)
			{
				num = 2;
			}
			if (ImGui.RadioButton("None", num == 0))
			{
				bool isHighLevelHelper = config.IsHighLevelHelper;
				config.IsQuester = false;
				config.IsHighLevelHelper = false;
				config.HelperAutomationEnabled = false;
				config.Save();
				plugin.GetHelperManager()?.HandleLocalRoleChanged(isHighLevelHelper);
			}
			ImGui.SameLine();
			DrawInfoIcon("No multi-client features enabled");
			if (ImGui.RadioButton("Quester", num == 1))
			{
				bool isHighLevelHelper2 = config.IsHighLevelHelper;
				config.IsQuester = true;
				config.IsHighLevelHelper = false;
				config.HelperAutomationEnabled = false;
				config.Save();
				plugin.GetHelperManager()?.HandleLocalRoleChanged(isHighLevelHelper2);
				Plugin.Log.Information("[Multiboxing] Role changed to: Quester");
			}
			ImGui.SameLine();
			DrawInfoIcon("This client will quest and invite helpers for dungeons");
			if (config.IsQuester)
			{
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.Indent();
				bool v = config.EnableFreeTrialHelperInvite;
				if (ImGui.Checkbox("Free Trial Mode (Reverse Invite)", ref v))
				{
					config.EnableFreeTrialHelperInvite = v;
					config.Save();
				}
				DrawInfoIcon("Enable if your Quester is a Free Trial character.\\nFree Trial cannot invite to party, so Helpers will invite the Quester instead.\\nAfter joining, the Helper will promote the Quester to Party Leader.");
				ImGui.Unindent();
			}
			if (ImGui.RadioButton("High-Level Helper", num == 2))
			{
				bool isHighLevelHelper3 = config.IsHighLevelHelper;
				config.IsQuester = false;
				config.IsHighLevelHelper = true;
				config.Save();
				plugin.GetHelperManager()?.HandleLocalRoleChanged(isHighLevelHelper3);
				Plugin.Log.Information("[Multiboxing] Role changed to: High-Level Helper");
			}
			ImGui.SameLine();
			DrawInfoIcon("Configures this client as a High-Level Helper.\nHelper requests and AutoDuty control only run while Helper logic is activated below.");
			if (config.IsHighLevelHelper)
			{
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.Indent();
				if (config.IsHelperAutomationActive)
				{
					ImGui.TextColored(in colorSuccess, "Helper logic is active");
					ImGui.TextWrapped("This client may respond to Quester requests and control AutoDuty during helper runs.");
					if (ImGui.Button("Deactivate Helper"))
					{
						plugin.GetHelperManager()?.SetHelperAutomationActive(active: false);
					}
				}
				else
				{
					ImGui.TextColored(in colorSecondary, "Helper logic is inactive");
					ImGui.TextWrapped("No helper requests, automatic party handling, Chauffeur actions, or helper AutoDuty start/stop commands will run.");
					if (ImGui.Button("Activate Helper"))
					{
						plugin.GetHelperManager()?.SetHelperAutomationActive(active: true);
					}
				}
				ImGuiHelpers.ScaledDummy(8f);
				bool v2 = config.AlwaysAutoAcceptInvites;
				if (ImGui.Checkbox("Always Auto-Accept Party Invites", ref v2))
				{
					config.AlwaysAutoAcceptInvites = v2;
					config.Save();
				}
				DrawInfoIcon("Continuously accept ALL party invites while Helper logic is active (useful for ManualInput mode without IPC)");
				ImGui.Unindent();
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.Indent();
				int v3 = config.RepairThresholdPercent;
				if (ImGui.SliderInt("Helper Repair Threshold (%)", ref v3, 0, 99))
				{
					config.RepairThresholdPercent = v3;
					config.Save();
				}
				ImGui.SameLine();
				ImGui.TextDisabled("(?)");
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("If a Helper's gear condition drops below this percentage after a dungeon, they will auto-repair via AutoDuty before becoming available again.");
				}
				ImGui.Unindent();
			}
			ImGuiHelpers.ScaledDummy(10f);
			ImGui.Separator();
			ImGuiHelpers.ScaledDummy(5f);
			ImGui.TextColored(in colorPrimary, "LAN Multi-PC Helper System");
			ImGui.TextWrapped("Connect helpers on different PCs in your HOME NETWORK.");
			ImGuiHelpers.ScaledDummy(3f);
			config.EnableLANHelpers = DrawSettingWithInfo("Enable LAN Helper System", config.EnableLANHelpers, "Connect to helpers on other PCs in YOUR home network.\nNOT accessible from internet! Only devices in your home can connect.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableLANHelpers)
			{
				ImGui.Spacing();
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 0f, 1f));
				ImGui.TextWrapped("⚠ WARNING: If your Helper is located on the same PC as your Quester do not turn this on!");
				ImGui.PopStyleColor();
				ImGui.Spacing();
			}
			if (config.EnableLANHelpers)
			{
				ImGui.Indent();
				if (config.IsHighLevelHelper)
				{
					bool flag = DrawSettingWithInfo("Start LAN Server on this PC", config.StartLANServer, "Enable so OTHER PCs in your home can connect to THIS PC.\nNOT exposed to internet! Only devices in your home can connect.");
					if (flag != config.StartLANServer)
					{
						config.StartLANServer = flag;
						config.Save();
						plugin.ToggleLANServer(flag);
					}
				}
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.TextColored(in colorSecondary, "Server Port:");
				ImGui.SetNextItemWidth(150f);
				int data = config.LANServerPort;
				if (ImGui.InputInt("##LANPort", ref data) && data >= 1024 && data <= 65535)
				{
					config.LANServerPort = data;
					config.Save();
				}
				ImGui.SameLine();
				DrawInfoIcon("Port for local network communication (default: 47788).\nFirewall may need to allow this port.");
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.TextColored(in colorSecondary, "Helper PC IP Addresses:");
				ImGui.TextWrapped("Add  IPs of OTHER PCs in your home with helper characters:");
				ImGuiHelpers.ScaledDummy(3f);
				if (config.LANHelperIPs == null)
				{
					config.LANHelperIPs = new List<string>();
				}
				for (int i = 0; i < config.LANHelperIPs.Count; i++)
				{
					ImU8String strId = new ImU8String(3, 1);
					strId.AppendLiteral("IP_");
					strId.AppendFormatted(i);
					ImGui.PushID(strId);
					ImGui.BulletText(config.LANHelperIPs[i]);
					ImGui.SameLine();
					if (ImGui.SmallButton("\ud83d\udd04 Reconnect"))
					{
						string ip = config.LANHelperIPs[i];
						LANHelperClient lanClient = plugin.GetLANHelperClient();
						if (lanClient != null)
						{
							Task.Run(async delegate
							{
								Plugin.Log.Information("[UI] Manual reconnect to " + ip + "...");
								await lanClient.ConnectToHelperAsync(ip);
							});
						}
					}
					ImGui.SameLine();
					if (ImGui.SmallButton("Remove"))
					{
						config.LANHelperIPs.RemoveAt(i);
						config.Save();
						i--;
					}
					ImGui.PopID();
				}
				if (config.LANHelperIPs.Count == 0)
				{
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "No IPs configured");
					ImGui.TextWrapped("Add IPs below:");
				}
				ImGuiHelpers.ScaledDummy(3f);
				ImGui.TextColored(in colorSecondary, "Add new IP:");
				ImGui.SetNextItemWidth(200f);
				string buf = newLANHelperIP ?? "";
				if (ImGui.InputText("##NewIP", ref buf, 50))
				{
					newLANHelperIP = buf;
				}
				ImGui.SameLine();
				if (ImGui.Button("Add IP") && !string.IsNullOrWhiteSpace(newLANHelperIP))
				{
					string trimmedIP = newLANHelperIP.Trim();
					if (!config.LANHelperIPs.Contains(trimmedIP))
					{
						config.LANHelperIPs.Add(trimmedIP);
						config.Save();
						newLANHelperIP = "";
						LANHelperClient lanClient2 = plugin.GetLANHelperClient();
						if (lanClient2 != null)
						{
							Task.Run(async delegate
							{
								await lanClient2.ConnectToHelperAsync(trimmedIP);
							});
						}
					}
				}
				ImGuiHelpers.ScaledDummy(3f);
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "\ud83d\udca1 Tip: Run 'ipconfig' and use your IPv4-Adresse (like 192.168.x.x)");
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.8f, 0.4f, 1f));
				if (config.StartLANServer)
				{
					ImU8String text = new ImU8String(48, 1);
					text.AppendLiteral("✓ LAN Server enabled (LOCAL network only, port ");
					text.AppendFormatted(config.LANServerPort);
					text.AppendLiteral(")");
					ImGui.TextWrapped(text);
				}
				if (config.LANHelperIPs.Count > 0)
				{
					ImU8String text2 = new ImU8String(37, 1);
					text2.AppendLiteral("✓ Will connect to ");
					text2.AppendFormatted(config.LANHelperIPs.Count);
					text2.AppendLiteral(" local helper PC(s)");
					ImGui.TextWrapped(text2);
				}
				ImGui.PopStyleColor();
				ImGui.Unindent();
			}
			ImGuiHelpers.ScaledDummy(10f);
			if (config.IsQuester)
			{
				ImGui.Separator();
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.TextColored(in colorPrimary, "Auto-Discovered Helpers");
				ImGui.TextWrapped("Helpers are automatically discovered via IPC when they have 'I'm a High-Level Helper' enabled:");
				ImGuiHelpers.ScaledDummy(5f);
				List<(string, ushort)> availableHelpers = plugin.GetAvailableHelpers();
				if (availableHelpers.Count == 0)
				{
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "No helpers discovered yet");
					ImGui.TextWrapped("Make sure helper clients are running with 'I'm a High-Level Helper' enabled.");
				}
				else
				{
					Vector4 col = new Vector4(0.2f, 1f, 0.2f, 1f);
					ImU8String text3 = new ImU8String(20, 1);
					text3.AppendFormatted(availableHelpers.Count);
					text3.AppendLiteral(" helper(s) available");
					ImGui.TextColored(in col, text3);
				}
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.Separator();
				ImGuiHelpers.ScaledDummy(5f);
				ImGui.TextColored(in colorPrimary, "Helper Selection Mode");
				ImGuiHelpers.ScaledDummy(3f);
				int helperSelection = (int)config.HelperSelection;
				if (ImGui.RadioButton("Auto", helperSelection == 0))
				{
					config.HelperSelection = HelperSelectionMode.Auto;
					config.PreferredHelper = "";
					config.ManualHelperName = "";
					config.Save();
				}
				ImGui.SameLine();
				DrawInfoIcon("First available helper via IPC");
				if (ImGui.RadioButton("Dropdown", helperSelection == 1))
				{
					config.HelperSelection = HelperSelectionMode.Dropdown;
					config.ManualHelperName = "";
					config.Save();
				}
				ImGui.SameLine();
				DrawInfoIcon("Select specific helper from list");
				if (config.HelperSelection == HelperSelectionMode.Dropdown && availableHelpers.Count > 0)
				{
					ImGui.Indent();
					ImGui.SetNextItemWidth(250f);
					string text4 = (string.IsNullOrEmpty(config.PreferredHelper) ? "-- Select --" : config.PreferredHelper);
					if (ImGui.BeginCombo("##PreferredHelper", text4))
					{
						foreach (var item5 in availableHelpers)
						{
							string item = item5.Item1;
							ushort item2 = item5.Item2;
							ExcelSheet<World> excelSheet = Plugin.DataManager.GetExcelSheet<World>();
							string text5 = "Unknown";
							if (excelSheet != null)
							{
								foreach (World item6 in excelSheet)
								{
									if (item6.RowId == item2)
									{
										text5 = item6.Name.ExtractText();
										break;
									}
								}
							}
							string text6 = item + "@" + text5;
							bool selected = config.PreferredHelper == text6;
							if (ImGui.Selectable(text6, selected))
							{
								config.PreferredHelper = text6;
								config.Save();
							}
						}
						ImGui.EndCombo();
					}
					if (!string.IsNullOrEmpty(config.PreferredHelper))
					{
						string text7 = (Plugin.Instance?.GetChauffeurMode())?.GetHelperStatus(config.PreferredHelper);
						Vector4 col;
						Vector4 col2;
						ImU8String text8;
						switch (text7)
						{
						case "Available":
							col = new Vector4(0.2f, 1f, 0.2f, 1f);
							goto IL_0e33;
						case "Transporting":
							col = new Vector4(1f, 0.8f, 0f, 1f);
							goto IL_0e33;
						case "InDungeon":
							col = new Vector4(1f, 0.3f, 0.3f, 1f);
							goto IL_0e33;
						default:
							col = colorSecondary;
							goto IL_0e33;
						case null:
							break;
							IL_0e33:
							col2 = col;
							ImGui.SameLine();
							text8 = new ImU8String(2, 1);
							text8.AppendLiteral("[");
							text8.AppendFormatted(text7);
							text8.AppendLiteral("]");
							ImGui.TextColored(in col2, text8);
							break;
						}
					}
					ImGui.Unindent();
				}
				else if (config.HelperSelection == HelperSelectionMode.Dropdown && availableHelpers.Count == 0)
				{
					ImGui.Indent();
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "⚠ No helpers available to select");
					ImGui.Unindent();
				}
				if (ImGui.RadioButton("Manual Input", helperSelection == 2))
				{
					config.HelperSelection = HelperSelectionMode.ManualInput;
					config.PreferredHelper = "";
					config.Save();
				}
				ImGui.SameLine();
				DrawInfoIcon("Manual entry (Dungeon invites only - NOT Chauffeur/Following!)");
				if (config.HelperSelection == HelperSelectionMode.ManualInput)
				{
					ImGui.Indent();
					ImGui.SetNextItemWidth(250f);
					string buf2 = config.ManualHelperName;
					if (ImGui.InputText("##ManualHelperInput", ref buf2, 100))
					{
						config.ManualHelperName = buf2;
						config.Save();
					}
					ImGui.SameLine();
					DrawInfoIcon("Format: CharacterName@WorldName");
					if (!string.IsNullOrEmpty(config.ManualHelperName))
					{
						if (config.ManualHelperName.Contains("@"))
						{
							ImGui.SameLine();
							ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "✓");
						}
						else
						{
							ImGui.SameLine();
							ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "⚠");
						}
					}
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.8f, 0.2f, 1f));
					ImGui.TextWrapped("⚠ Cannot be used with Chauffeur/Following (requires IPC)");
					ImGui.PopStyleColor();
					ImGui.Unindent();
				}
				if (availableHelpers.Count > 0)
				{
					ImGuiHelpers.ScaledDummy(5f);
					ImGui.TextUnformatted("Available Helpers:");
					ChauffeurModeService chauffeurModeService = Plugin.Instance?.GetChauffeurMode();
					foreach (var item7 in availableHelpers)
					{
						string item3 = item7.Item1;
						ushort item4 = item7.Item2;
						ExcelSheet<World> excelSheet2 = Plugin.DataManager.GetExcelSheet<World>();
						string text9 = "Unknown";
						if (excelSheet2 != null)
						{
							foreach (World item8 in excelSheet2)
							{
								if (item8.RowId == item4)
								{
									text9 = item8.Name.ExtractText();
									break;
								}
							}
						}
						string text10 = item3 + "@" + text9;
						string text11 = chauffeurModeService?.GetHelperStatus(text10);
						ImU8String text12 = new ImU8String(4, 1);
						text12.AppendLiteral("  • ");
						text12.AppendFormatted(text10);
						ImGui.TextUnformatted(text12);
						if (text11 != null)
						{
							ImGui.SameLine();
							Vector4 col3 = text11 switch
							{
								"Available" => new Vector4(0.2f, 1f, 0.2f, 1f), 
								"Transporting" => new Vector4(1f, 0.8f, 0f, 1f), 
								"InDungeon" => new Vector4(1f, 0.3f, 0.3f, 1f), 
								_ => colorSecondary, 
							};
							ImU8String text13 = new ImU8String(2, 1);
							text13.AppendLiteral("[");
							text13.AppendFormatted(text11);
							text13.AppendLiteral("]");
							ImGui.TextColored(in col3, text13);
						}
					}
				}
			}
		}, config.IsQuester || config.IsHighLevelHelper);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Chauffeur Mode", delegate
		{
			ImGui.TextWrapped("Multi-character transport system. Helper transports Quester to quest objectives using multi-seater mounts.");
			ImGuiHelpers.ScaledDummy(5f);
			if (!config.IsQuester && !config.IsHighLevelHelper)
			{
				ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Please select a role above to configure Chauffeur Mode");
			}
			else
			{
				bool v = config.ChauffeurModeEnabled;
				if (ImGui.Checkbox("Enable Chauffeur Mode", ref v))
				{
					config.ChauffeurModeEnabled = v;
					config.Save();
					Plugin.Log.Information("[Multiboxing] Chauffeur Mode: " + (v ? "ENABLED" : "DISABLED"));
				}
				DrawInfoIcon("Enable automatic helper summoning for long-distance travel in non-flying zones");
				bool v2 = config.QuesterInvitesHelper;
				if (ImGui.Checkbox("Quester Invites Helper", ref v2))
				{
					config.QuesterInvitesHelper = v2;
					config.Save();
				}
				DrawInfoIcon("If enabled, the Quester will invite the Helper to the party when they are close enough.\nThe Helper will wait for the invite before mounting.");
				if (config.ChauffeurModeEnabled)
				{
					ImGuiHelpers.ScaledDummy(5f);
					ImGui.Separator();
					ImGuiHelpers.ScaledDummy(5f);
					if (config.IsQuester)
					{
						ImGui.TextColored(in colorPrimary, "Quester Settings");
						ImGuiHelpers.ScaledDummy(3f);
						float v3 = config.ChauffeurDistanceThreshold;
						ImGui.SetNextItemWidth(200f);
						if (ImGui.SliderFloat("Distance Threshold (yalms)", ref v3, 105f, 300f, "%.0f"))
						{
							config.ChauffeurDistanceThreshold = v3;
							config.Save();
						}
						DrawInfoIcon("Helper will be summoned when task is further than this distance\nand flying is not available in the zone");
						ImU8String text = new ImU8String(15, 1);
						text.AppendLiteral("Current: ");
						text.AppendFormatted(config.ChauffeurDistanceThreshold, "F0");
						text.AppendLiteral(" yalms");
						ImGui.TextWrapped(text);
					}
					if (config.IsHighLevelHelper)
					{
						ImGui.TextColored(in colorPrimary, "Helper Settings");
						ImGuiHelpers.ScaledDummy(3f);
						Vector4 col = config.CurrentHelperStatus switch
						{
							HelperStatus.Available => new Vector4(0.2f, 1f, 0.2f, 1f), 
							HelperStatus.Transporting => new Vector4(1f, 0.8f, 0f, 1f), 
							HelperStatus.InDungeon => new Vector4(1f, 0.3f, 0.3f, 1f), 
							_ => colorSecondary, 
						};
						string value = config.CurrentHelperStatus switch
						{
							HelperStatus.Available => "Available", 
							HelperStatus.Transporting => "Transporting", 
							HelperStatus.InDungeon => "In Dungeon", 
							_ => "Unknown", 
						};
						ImU8String text2 = new ImU8String(8, 1);
						text2.AppendLiteral("Status: ");
						text2.AppendFormatted(value);
						ImGui.TextColored(in col, text2);
						ImGuiHelpers.ScaledDummy(3f);
						if (!string.IsNullOrEmpty(config.AssignedQuester))
						{
							string text3 = config.AssignedQuester;
							if (text3.Contains("@"))
							{
								string[] array = text3.Split('@');
								if (array.Length == 2 && ushort.TryParse(array[1], out var result))
								{
									text3 = array[0] + "@" + WorldNameHelper.GetWorldName(result);
								}
							}
							Vector4 col2 = new Vector4(0.2f, 1f, 0.2f, 1f);
							ImU8String text4 = new ImU8String(18, 1);
							text4.AppendLiteral("Assigned Quester: ");
							text4.AppendFormatted(text3);
							ImGui.TextColored(in col2, text4);
							ImGuiHelpers.ScaledDummy(3f);
						}
						else
						{
							ImGui.TextColored(in colorSecondary, "Assigned Quester: None");
							ImGuiHelpers.ScaledDummy(3f);
						}
						float v4 = config.ChauffeurStopDistance;
						ImGui.SetNextItemWidth(200f);
						if (ImGui.SliderFloat("Stop Distance (yalms)", ref v4, 2f, 15f, "%.1f"))
						{
							config.ChauffeurStopDistance = v4;
							config.Save();
						}
						DrawInfoIcon("How close you bring the quester to their destination\n(2-15 yalms, default: 5)");
						ImU8String text5 = new ImU8String(15, 1);
						text5.AppendLiteral("Current: ");
						text5.AppendFormatted(config.ChauffeurStopDistance, "F1");
						text5.AppendLiteral(" yalms");
						ImGui.TextWrapped(text5);
						ImGuiHelpers.ScaledDummy(5f);
						List<(uint, string, byte)> list = (Plugin.Instance?.GetChauffeurMode())?.GetMultiSeaterMounts() ?? new List<(uint, string, byte)>();
						if (list.Count == 0)
						{
							ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "No multi-seater mounts found!");
							ImGui.TextWrapped("Make sure you have unlocked at least one multi-seater mount.");
						}
						else
						{
							ImGui.TextWrapped("Select Multi-Seater Mount:");
							ImGuiHelpers.ScaledDummy(3f);
							int num = 0;
							List<string> list2 = new List<string>();
							for (int i = 0; i < list.Count; i++)
							{
								var (num2, value2, value3) = list[i];
								list2.Add($"{value2} (Passengers: {value3})");
								if (num2 == config.ChauffeurMountId)
								{
									num = i;
								}
							}
							list2.Insert(0, "-- Not Selected --");
							num = ((config.ChauffeurMountId != 0) ? (num + 1) : 0);
							ImGui.SetNextItemWidth(300f);
							if (ImGui.Combo("##MountSelect", ref num, list2.ToArray(), list2.Count))
							{
								if (num == 0)
								{
									config.ChauffeurMountId = 0u;
								}
								else
								{
									(uint, string, byte) tuple2 = list[num - 1];
									config.ChauffeurMountId = tuple2.Item1;
								}
								config.Save();
							}
							DrawInfoIcon("This mount will be used to transport the Quester");
							if (config.ChauffeurMountId == 0)
							{
								ImGuiHelpers.ScaledDummy(3f);
								ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Please select a mount to enable Chauffeur Mode");
							}
						}
					}
				}
			}
		}, config.ChauffeurModeEnabled);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Helper Following", delegate
		{
			ImGui.TextWrapped("Helper passively follows Quester and maintains a configurable distance. Automatically navigates when too far away.");
			ImGuiHelpers.ScaledDummy(5f);
			if (config.IsQuester)
			{
				ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Quester Settings:");
				ImGui.TextWrapped("Select which Helper should follow you. Your position will be broadcasted to this Helper.");
				ImGuiHelpers.ScaledDummy(3f);
				string assignedHelperForFollowing = config.AssignedHelperForFollowing;
				ImGui.Text("Assigned Helper:");
				ImGui.SameLine();
				if (string.IsNullOrEmpty(assignedHelperForFollowing))
				{
					ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "None");
				}
				else
				{
					ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), assignedHelperForFollowing);
				}
				ImGuiHelpers.ScaledDummy(3f);
				ImGui.Text("Auto-Discovered Helpers:");
				ImGui.SetNextItemWidth(300f);
				List<(string, ushort)> availableHelpers = plugin.GetAvailableHelpers();
				if (ImGui.BeginCombo("##HelperDropdown", string.IsNullOrEmpty(assignedHelperForFollowing) ? "Select Helper..." : assignedHelperForFollowing))
				{
					if (availableHelpers.Count == 0)
					{
						ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "No helpers discovered yet");
						ImGui.TextWrapped("Helpers are auto-discovered via IPC when they have 'I'm a High-Level Helper' enabled.");
					}
					else
					{
						foreach (var item9 in availableHelpers)
						{
							string item = item9.Item1;
							ushort item2 = item9.Item2;
							ExcelSheet<World> excelSheet = Plugin.DataManager.GetExcelSheet<World>();
							string text = "Unknown";
							if (excelSheet != null)
							{
								foreach (World item10 in excelSheet)
								{
									if (item10.RowId == item2)
									{
										text = item10.Name.ExtractText();
										break;
									}
								}
							}
							string text2 = item + "@" + text;
							bool selected = assignedHelperForFollowing == text2;
							if (ImGui.Selectable(text2, selected))
							{
								config.AssignedHelperForFollowing = text2;
								config.Save();
							}
						}
					}
					ImGui.EndCombo();
				}
				DrawInfoIcon("Select the Helper from auto-discovered helpers.\nHelpers are discovered via IPC when they broadcast their status.");
				ImGuiHelpers.ScaledDummy(5f);
				bool v = config.EnableHelperFollowing;
				if (string.IsNullOrEmpty(config.AssignedHelperForFollowing))
				{
					ImGui.BeginDisabled();
				}
				if (ImGui.Checkbox("Enable Position Broadcasting", ref v))
				{
					config.EnableHelperFollowing = v;
					config.Save();
					Plugin.Log.Information("[Multiboxing] Helper Following (Quester): " + (v ? "ENABLED" : "DISABLED"));
				}
				if (string.IsNullOrEmpty(config.AssignedHelperForFollowing))
				{
					ImGui.EndDisabled();
				}
				DrawInfoIcon("Enable to broadcast your position to the assigned Helper.\nThe Helper can then follow you automatically.");
				ImGuiHelpers.ScaledDummy(3f);
				if (config.EnableHelperFollowing && !string.IsNullOrEmpty(config.AssignedHelperForFollowing))
				{
					ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "✓ Broadcasting position to Helper");
				}
				else if (!string.IsNullOrEmpty(config.AssignedHelperForFollowing))
				{
					ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "⚠ Enable broadcasting to start");
				}
				else
				{
					ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "⚠ Select a Helper first");
				}
			}
			else if (!config.IsHighLevelHelper)
			{
				ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Please select a role (Quester or Helper) above");
			}
			else
			{
				ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Helper Settings:");
				ImGui.TextWrapped("Select which Quester to follow. You will only follow this specific Quester.");
				ImGuiHelpers.ScaledDummy(3f);
				string assignedQuesterForFollowing = config.AssignedQuesterForFollowing;
				ImGui.Text("Assigned Quester:");
				ImGui.SameLine();
				if (string.IsNullOrEmpty(assignedQuesterForFollowing))
				{
					ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "⚠ None - Helper Following disabled!");
				}
				else
				{
					string text3 = assignedQuesterForFollowing;
					if (text3.Contains("@"))
					{
						string[] array = text3.Split('@');
						if (array.Length == 2 && ushort.TryParse(array[1], out var result))
						{
							text3 = array[0] + "@" + WorldNameHelper.GetWorldName(result);
						}
					}
					ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), text3);
				}
				ImGuiHelpers.ScaledDummy(3f);
				ImGui.Text("Auto-Discovered Questers:");
				ImGui.SetNextItemWidth(300f);
				List<string> list = (Plugin.Instance?.GetChauffeurMode())?.GetDiscoveredQuesters() ?? new List<string>();
				string text4 = assignedQuesterForFollowing;
				if (!string.IsNullOrEmpty(text4) && text4.Contains("@"))
				{
					string[] array2 = text4.Split('@');
					if (array2.Length == 2 && ushort.TryParse(array2[1], out var result2))
					{
						text4 = array2[0] + "@" + WorldNameHelper.GetWorldName(result2);
					}
				}
				if (ImGui.BeginCombo("##QuesterDropdown", string.IsNullOrEmpty(text4) ? "Select Quester..." : text4))
				{
					if (list.Count == 0)
					{
						ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "No questers discovered yet");
						ImGui.TextWrapped("Questers are auto-discovered when they broadcast position.");
						ImGui.TextWrapped("Make sure the Quester has Helper Following enabled and has assigned you as Helper.");
					}
					else
					{
						foreach (string item11 in list)
						{
							string text5 = item11;
							if (text5.Contains("@"))
							{
								string[] array3 = text5.Split('@');
								if (array3.Length == 2 && ushort.TryParse(array3[1], out var result3))
								{
									text5 = array3[0] + "@" + WorldNameHelper.GetWorldName(result3);
								}
							}
							bool selected2 = assignedQuesterForFollowing == item11;
							if (ImGui.Selectable(text5, selected2))
							{
								config.AssignedQuesterForFollowing = text5;
								config.Save();
							}
						}
					}
					ImGui.EndCombo();
				}
				DrawInfoIcon("Questers are automatically discovered via IPC when they broadcast position.\nSelect the Quester you want to follow from the list.");
				ImGuiHelpers.ScaledDummy(5f);
				bool v2 = config.EnableHelperFollowing;
				if (string.IsNullOrEmpty(config.AssignedQuesterForFollowing))
				{
					ImGui.BeginDisabled();
				}
				if (ImGui.Checkbox("Enable Helper Following", ref v2))
				{
					config.EnableHelperFollowing = v2;
					config.Save();
					Plugin.Log.Information("[Multiboxing] Helper Following (Helper): " + (v2 ? "ENABLED" : "DISABLED"));
				}
				if (string.IsNullOrEmpty(config.AssignedQuesterForFollowing))
				{
					ImGui.EndDisabled();
				}
				DrawInfoIcon("Helper will automatically follow the assigned Quester and maintain distance.\nStops in restricted zones (Main Cities) and when Chauffeur Mode is active.");
				if (config.EnableHelperFollowing)
				{
					ImGuiHelpers.ScaledDummy(5f);
					ImGui.Indent();
					float v3 = config.HelperFollowDistance;
					if (ImGui.SliderFloat("Follow Distance (yalms)", ref v3, 50f, 200f, "%.0f"))
					{
						config.HelperFollowDistance = v3;
						config.Save();
					}
					DrawInfoIcon("Distance to maintain from Quester.\nHelper will navigate when further than this distance.\nRecommended: 80-120 yalms");
					ImGuiHelpers.ScaledDummy(3f);
					int v4 = config.HelperFollowCheckInterval;
					if (ImGui.SliderInt("Check Interval (seconds)", ref v4, 3, 15))
					{
						config.HelperFollowCheckInterval = v4;
						config.Save();
					}
					DrawInfoIcon("How often to check distance to Quester.\nLower values = more responsive but more CPU usage.\nRecommended: 5 seconds");
					ImGui.Unindent();
					ImGuiHelpers.ScaledDummy(5f);
					ImGui.Separator();
					ImGuiHelpers.ScaledDummy(3f);
					ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Status:");
					if (Plugin.Instance?.GetChauffeurMode() != null)
					{
						ImU8String text6 = new ImU8String(23, 1);
						text6.AppendLiteral("Follow Distance: ");
						text6.AppendFormatted(config.HelperFollowDistance, "F0");
						text6.AppendLiteral(" yalms");
						ImGui.TextWrapped(text6);
						ImU8String text7 = new ImU8String(17, 1);
						text7.AppendLiteral("Check Interval: ");
						text7.AppendFormatted(config.HelperFollowCheckInterval);
						text7.AppendLiteral("s");
						ImGui.TextWrapped(text7);
					}
					ImGuiHelpers.ScaledDummy(3f);
					ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Note: Helper Following automatically stops when:");
					ImGui.BulletText("Entering restricted zones (Main Cities)");
					ImGui.BulletText("Chauffeur Mode summon is active");
					ImGui.BulletText("Quester leaves party");
					ImGui.BulletText("Quester changes to different zone");
				}
			}
		}, config.EnableHelperFollowing);
	}

	private void DrawRetainersTab()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Retainer Setup");
		ImGui.PopStyleColor();
		ImGui.TextWrapped("XADB's saved zero identifies new targets. Once a batch starts, Vocate entitlement and open slots come directly from the game's native RetainerManager, matching Henchman's flow; existing partial setups resume only exact ContentId/retainer checkpoints owned by Companion.");
		ImGuiHelpers.ScaledDummy(6f);
		if (!xadbIpc.IsInstalled)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImGui.TextWrapped("XA Database must be installed to discover a new saved-zero target. It is not required by an already-started batch or its reload recovery.");
			ImGui.PopStyleColor();
		}
		ImGui.BeginDisabled(retainerCreationService.Snapshot.IsRunning || retainerCreationService.HasPendingRecovery);
		if (ImGui.Button("Refresh XADB and characters"))
		{
			autoRetainerIpc.ClearCache();
			RefreshCharacterList();
			XadbImportResult xadbImportResult = ImportXadbProgress(registeredCharacters);
			foreach (var (value, key) in autoRetainerIpc.GetRegisteredCharacterMap())
			{
				characterContentIds[key] = value;
			}
			characterProgressCache.Clear();
			retainerUiMessage = xadbImportResult.Status;
			LogXadbRefreshSummary("Retainer-tab refresh", xadbImportResult, registeredCharacters.Count);
		}
		if (!string.IsNullOrWhiteSpace(retainerUiMessage))
		{
			ImGui.SameLine();
			ImGui.TextWrapped(retainerUiMessage);
		}
		ImGui.Separator();
		DrawRetainerGlobalSettings();
		ImGui.Separator();
		DrawRetainerCharacterTable();
		ImGui.Separator();
		DrawRetainerNameSamples();
		ImGui.EndDisabled();
		ImGui.Separator();
		DrawRetainerRunControls();
	}

	private void DrawRetainerGlobalSettings()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Global settings");
		ImGui.PopStyleColor();
		RetainerSetupConfiguration retainerSetup = configuration.RetainerSetup;
		bool flag = false;
		RetainerStarterCity value = retainerSetup.City;
		flag |= DrawRetainerEnumCombo("City", "##RetainerCity", ref value, FormatRetainerEnum);
		retainerSetup.City = value;
		ImGui.SameLine();
		RetainerAppearanceRace value2 = retainerSetup.Appearance;
		flag |= DrawRetainerEnumCombo("Appearance", "##RetainerAppearance", ref value2, FormatRetainerEnum);
		retainerSetup.Appearance = value2;
		ImGui.SameLine();
		RetainerGender value3 = retainerSetup.Gender;
		flag |= DrawRetainerEnumCombo("Gender", "##RetainerGender", ref value3, FormatRetainerEnum);
		retainerSetup.Gender = value3;
		RetainerClan value4 = retainerSetup.Clan;
		flag |= DrawRetainerEnumCombo("Clan", "##RetainerClan", ref value4, FormatRetainerEnum);
		retainerSetup.Clan = value4;
		ImGui.SameLine();
		RetainerPersonality value5 = retainerSetup.Personality;
		flag |= DrawRetainerEnumCombo("Personality", "##RetainerPersonality", ref value5, FormatRetainerEnum);
		retainerSetup.Personality = value5;
		ImGui.SameLine();
		RetainerStopAfter value6 = retainerSetup.StopAfter;
		flag |= DrawRetainerEnumCombo("Stop after", "##RetainerStopAfter", ref value6, FormatRetainerEnum);
		retainerSetup.StopAfter = value6;
		bool num = value6 == RetainerStopAfter.AutoRetainerBootstrapped;
		bool v = RetainerAutoRetainerBootstrapPolicy.ShouldAttachStarterPlan(value6, retainerSetup.AttachStarterPlan);
		ImGui.BeginDisabled(num);
		if (ImGui.Checkbox(num ? "Attach the standard starter plan (required at 100%)" : "Attach the standard starter plan", ref v))
		{
			retainerSetup.AttachStarterPlan = v;
			flag = true;
		}
		ImGui.EndDisabled();
		ImGui.SameLine();
		bool v2 = retainerSetup.EnableNewRetainers;
		if (ImGui.Checkbox("Enable newly created retainers", ref v2))
		{
			retainerSetup.EnableNewRetainers = v2;
			flag = true;
		}
		ImGui.SameLine();
		bool v3 = retainerSetup.EnableCharacter;
		if (ImGui.Checkbox("Enable character in AutoRetainer", ref v3))
		{
			retainerSetup.EnableCharacter = v3;
			flag = true;
		}
		if (flag)
		{
			retainerSetup.SampleNames.Clear();
			retainerSamplesInitialized = false;
			configuration.Save();
		}
	}

	private void DrawRetainerCharacterTable()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Selected characters");
		ImGui.PopStyleColor();
		string[] array = registeredCharacters.Where((string character) => characterSelection.GetValueOrDefault(character)).OrderBy<string, string>((string character) => character, StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			ImGui.TextWrapped("Select characters on the Characters tab. New rows require confirmed zero; every existing non-complete checkpoint can be revalidated.");
			return;
		}
		DrawRetainerBulkJobControls(array);
		ImGuiHelpers.ScaledDummy(4f);
		if (!ImGui.BeginTable("RetainerSelectedCharacters", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
		{
			return;
		}
		ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2f);
		ImGui.TableSetupColumn("XADB roster", ImGuiTableColumnFlags.WidthStretch, 1.35f);
		ImGui.TableSetupColumn("Setup", ImGuiTableColumnFlags.WidthStretch, 1.3f);
		ImGui.TableSetupColumn("Retainer type", ImGuiTableColumnFlags.WidthStretch, 1.2f);
		ImGui.TableSetupColumn("Combat starter class", ImGuiTableColumnFlags.WidthStretch, 1.5f);
		ImGui.TableHeadersRow();
		string[] array2 = array;
		foreach (string text in array2)
		{
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			ImGui.TextUnformatted(text);
			if (!TryResolveContentId(text, out var contentId))
			{
				ImGui.TableNextColumn();
				ImGui.TextUnformatted("Unknown ContentId");
				ImGui.TableNextColumn();
				ImGui.TextUnformatted("Unavailable");
				ImGui.TableNextColumn();
				ImGui.TextUnformatted("-");
				ImGui.TableNextColumn();
				ImGui.TextUnformatted("-");
				continue;
			}
			XadbRetainerSnapshot xadbRetainerSnapshot = xadbRetainerSnapshots.GetValueOrDefault(text) ?? XadbRetainerSnapshot.Unknown("No current XADB row", 0uL);
			configuration.RetainerSetup.Checkpoints.TryGetValue(contentId, out CharacterRetainerSetupCheckpoint value);
			bool flag = RetainerSetupLogic.IsEligibleForExplicitRun(value) && value != null && value.State == RetainerCheckpointState.Failed;
			ImGui.TableNextColumn();
			XadbRetainerRosterStatus status = xadbRetainerSnapshot.Status;
			string text2;
			if (status != XadbRetainerRosterStatus.ConfirmedZero)
			{
				if (status == XadbRetainerRosterStatus.Populated)
				{
					if (xadbRetainerSnapshot.OwnerContentId != contentId)
					{
						goto IL_02b4;
					}
					text2 = $"{xadbRetainerSnapshot.DeclaredCount} (highest {xadbRetainerSnapshot.HighestLevel})" + (xadbRetainerSnapshot.EvidenceValidated ? string.Empty : " - stale");
				}
				else
				{
					text2 = ((!flag) ? "Unknown" : "Revalidation required");
				}
			}
			else
			{
				if (xadbRetainerSnapshot.OwnerContentId != contentId)
				{
					goto IL_02b4;
				}
				text2 = "Confirmed: 0";
			}
			goto IL_02d1;
			IL_02d1:
			ImGui.TextUnformatted(text2);
			if ((!xadbRetainerSnapshot.EvidenceValidated || xadbRetainerSnapshot.Status == XadbRetainerRosterStatus.Unknown) && ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(xadbRetainerSnapshot.FailureReason))
			{
				ImGui.SetTooltip(xadbRetainerSnapshot.FailureReason);
			}
			ImGui.TableNextColumn();
			text2 = ((value != null) ? (flag ? $"{value.ProgressPercent}% - Revalidation required" : $"{value.ProgressPercent}% - {value.State}") : (xadbRetainerSnapshot.Status switch
			{
				XadbRetainerRosterStatus.ConfirmedZero => "Not started", 
				XadbRetainerRosterStatus.Populated => "Existing / untracked", 
				_ => "Unknown", 
			}));
			ImGui.TextUnformatted(text2);
			if (value != null && !string.IsNullOrWhiteSpace(value.LastError) && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(value.LastError);
			}
			bool flag2 = value != null && RetainerSetupLogic.HasLockedChoice(value);
			bool flag3 = !flag2;
			CharacterRetainerSetupChoice characterRetainerSetupChoice = (flag2 ? value.LockedChoice : GetOrCreateRetainerChoice(contentId, text));
			ImGui.TableNextColumn();
			if (flag3)
			{
				RetainerType value2 = characterRetainerSetupChoice.Type;
				if (DrawRetainerEnumCombo(string.Empty, $"##RetainerType{contentId}", ref value2, FormatRetainerEnum))
				{
					characterRetainerSetupChoice.Type = value2;
					configuration.Save();
				}
			}
			else
			{
				ImGui.TextUnformatted(flag2 ? $"{characterRetainerSetupChoice.Type} (locked)" : "Unavailable");
			}
			ImGui.TableNextColumn();
			if (characterRetainerSetupChoice.Type != RetainerType.Combat)
			{
				ImGui.TextUnformatted("-");
			}
			else if (flag3)
			{
				DrawCombatStarterClassCombo(contentId, characterRetainerSetupChoice);
			}
			else
			{
				ImGui.TextUnformatted(flag2 ? (GetHuntLogClassJobLabel(characterRetainerSetupChoice.CombatStarterClassId) + " (locked)") : "Unavailable");
			}
			continue;
			IL_02b4:
			text2 = "Owner mismatch";
			goto IL_02d1;
		}
		ImGui.EndTable();
	}

	private void DrawRetainerBulkJobControls(IReadOnlyList<string> selectedCharacters)
	{
		ImGui.TextUnformatted("Set every editable selected setup to one retainer job:");
		ImGui.SameLine();
		ImGui.SetNextItemWidth(190f);
		if (ImGui.BeginCombo("##RetainerBulkJob", GetHuntLogClassJobLabel(retainerBulkJobId)))
		{
			uint[] array = new uint[11]
			{
				1u, 2u, 3u, 4u, 5u, 6u, 7u, 26u, 16u, 17u,
				18u
			};
			foreach (uint num in array)
			{
				bool flag = retainerBulkJobId == num;
				if (ImGui.Selectable(GetHuntLogClassJobLabel(num), flag))
				{
					retainerBulkJobId = num;
				}
				if (flag)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGui.SameLine();
		if (!ImGui.Button("Apply job to all selected characters"))
		{
			return;
		}
		int num2 = 0;
		int num3 = 0;
		foreach (string selectedCharacter in selectedCharacters)
		{
			if (!TryResolveContentId(selectedCharacter, out var contentId))
			{
				num3++;
				continue;
			}
			configuration.RetainerSetup.Checkpoints.TryGetValue(contentId, out CharacterRetainerSetupCheckpoint value);
			if (value != null && RetainerSetupLogic.HasLockedChoice(value))
			{
				num3++;
				continue;
			}
			CharacterRetainerSetupChoice orCreateRetainerChoice = GetOrCreateRetainerChoice(contentId, selectedCharacter);
			switch (retainerBulkJobId)
			{
			case 16u:
				orCreateRetainerChoice.Type = RetainerType.Mining;
				break;
			case 17u:
				orCreateRetainerChoice.Type = RetainerType.Botany;
				break;
			case 18u:
				orCreateRetainerChoice.Type = RetainerType.Fishing;
				break;
			default:
				orCreateRetainerChoice.Type = RetainerType.Combat;
				orCreateRetainerChoice.CombatStarterClassId = retainerBulkJobId;
				break;
			}
			num2++;
		}
		configuration.Save();
		retainerUiMessage = $"Applied {GetHuntLogClassJobLabel(retainerBulkJobId)} to {num2} selected setup(s); skipped {num3} locked or unavailable.";
	}

	private void DrawRetainerNameSamples()
	{
		RetainerSetupConfiguration retainerSetup = configuration.RetainerSetup;
		if (!retainerSamplesInitialized)
		{
			retainerSamplesInitialized = true;
			if (retainerSetup.SampleNames.Count != 10 || RetainerNameLogic.ShouldRegenerateHybridSampleCache(retainerSetup.SampleNames))
			{
				RegenerateRetainerSamples();
			}
		}
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Generated sample names");
		ImGui.PopStyleColor();
		ImGui.SameLine();
		if (ImGui.Button("Regenerate"))
		{
			RegenerateRetainerSamples();
		}
		if (retainerSetup.SampleNames.Count == 0)
		{
			ImGui.TextUnformatted("No valid unique names could be generated from CharaMakeName data.");
			return;
		}
		for (int i = 0; i < retainerSetup.SampleNames.Count; i++)
		{
			if (i % 5 != 0)
			{
				ImGui.SameLine(0f, 20f);
			}
			ImGui.TextUnformatted(retainerSetup.SampleNames[i]);
		}
	}

	private void DrawRetainerRunControls()
	{
		RetainerCreationSnapshot snapshot = retainerCreationService.Snapshot;
		if (snapshot.IsRunning)
		{
			ImU8String text = new ImU8String(2, 2);
			text.AppendFormatted(snapshot.CurrentStage);
			text.AppendLiteral(": ");
			text.AppendFormatted(snapshot.CurrentCharacter);
			ImGui.TextWrapped(text);
			ImGui.TextWrapped(snapshot.LastMessage);
			float fraction = ((snapshot.TotalCharacters == 0) ? 0f : ((float)snapshot.CompletedCharacters / (float)snapshot.TotalCharacters));
			Vector2 sizeArg = new Vector2(-1f, 0f);
			ImU8String overlay = new ImU8String(12, 2);
			overlay.AppendFormatted(snapshot.CompletedCharacters);
			overlay.AppendLiteral("/");
			overlay.AppendFormatted(snapshot.TotalCharacters);
			overlay.AppendLiteral(" characters");
			ImGui.ProgressBar(fraction, sizeArg, overlay);
			if (snapshot.CanCancel && ImGui.Button("Cancel after guarded cleanup"))
			{
				retainerCreationService.Cancel();
			}
			return;
		}
		if (!string.Equals(snapshot.CurrentStage, "Idle", StringComparison.OrdinalIgnoreCase))
		{
			ImU8String text2 = new ImU8String(2, 2);
			text2.AppendFormatted(snapshot.CurrentStage);
			text2.AppendLiteral(": ");
			text2.AppendFormatted(snapshot.CurrentCharacter);
			ImGui.TextWrapped(text2);
		}
		if (!string.IsNullOrWhiteSpace(snapshot.LastMessage))
		{
			ImGui.TextWrapped(snapshot.LastMessage);
		}
		if (retainerCreationService.HasPendingRecovery)
		{
			ImGui.TextWrapped("A durable retainer batch is suspended and remains reserved for automatic recovery.");
			return;
		}
		List<RetainerSetupTarget> list = BuildRetainerTargets();
		ImGui.BeginDisabled(list.Count == 0);
		ImU8String label = new ImU8String(26, 1);
		label.AppendLiteral("Run selected characters (");
		label.AppendFormatted(list.Count);
		label.AppendLiteral(")");
		if (ImGui.Button(label))
		{
			ImGui.OpenPopup("Confirm retainer creation##Retainers");
		}
		ImGui.EndDisabled();
		if (list.Count == 0)
		{
			ImGui.TextWrapped("No selected character is confirmed empty or has an existing non-complete checkpoint to revalidate.");
		}
		bool open = true;
		if (!ImGui.BeginPopupModal("Confirm retainer creation##Retainers", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			return;
		}
		ImGui.TextWrapped("This is a consequential live action. Companion will relog the explicitly selected eligible characters, fill every available retainer entitlement, and stop at the configured checkpoint. MultiMode and the manual AutoRetainer scheduler will remain off afterward.");
		ImGui.Spacing();
		if (ImGui.Button("Create/configure retainers"))
		{
			if (!retainerCreationService.TryStart(BuildRetainerTargets(), GetKnownXadbRetainerNames(), out string error))
			{
				retainerUiMessage = error;
			}
			ImGui.CloseCurrentPopup();
		}
		ImGui.SameLine();
		if (ImGui.Button("Cancel"))
		{
			ImGui.CloseCurrentPopup();
		}
		ImGui.EndPopup();
	}

	private List<RetainerSetupTarget> BuildRetainerTargets()
	{
		List<RetainerSetupTarget> list = new List<RetainerSetupTarget>();
		foreach (string item in registeredCharacters.Where((string key) => characterSelection.GetValueOrDefault(key)))
		{
			if (TryResolveContentId(item, out var contentId))
			{
				XadbRetainerSnapshot xadbRetainerSnapshot = xadbRetainerSnapshots.GetValueOrDefault(item) ?? XadbRetainerSnapshot.Unknown("No current XADB row", 0uL);
				configuration.RetainerSetup.Checkpoints.TryGetValue(contentId, out CharacterRetainerSetupCheckpoint value);
				if (RetainerSetupLogic.IsEligibleForExplicitTarget(contentId, xadbRetainerSnapshot, value))
				{
					CharacterRetainerSetupChoice choice = ((value != null && RetainerSetupLogic.HasLockedChoice(value)) ? value.LockedChoice : GetOrCreateRetainerChoice(contentId, item));
					list.Add(new RetainerSetupTarget(contentId, item, xadbRetainerSnapshot, choice));
				}
			}
		}
		return list;
	}

	private IReadOnlyList<string> GetKnownXadbRetainerNames()
	{
		return (from retainer in xadbRetainerSnapshots.Values.SelectMany((XadbRetainerSnapshot snapshot) => snapshot.Retainers)
			select retainer.Name into name
			where !string.IsNullOrWhiteSpace(name)
			select name).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private bool TryResolveContentId(string character, out ulong contentId)
	{
		if (characterContentIds.TryGetValue(character, out contentId) && contentId != 0L)
		{
			return true;
		}
		if (autoRetainerIpc.TryGetContentId(character, out contentId))
		{
			characterContentIds[character] = contentId;
			return true;
		}
		return false;
	}

	private CharacterRetainerSetupChoice GetOrCreateRetainerChoice(ulong contentId, string character)
	{
		if (!configuration.RetainerSetup.CharacterChoices.TryGetValue(contentId, out CharacterRetainerSetupChoice value))
		{
			value = new CharacterRetainerSetupChoice
			{
				CharacterKey = character
			};
			configuration.RetainerSetup.CharacterChoices[contentId] = value;
		}
		return value;
	}

	private void DrawCombatStarterClassCombo(ulong contentId, CharacterRetainerSetupChoice choice)
	{
		uint[] array = new uint[8] { 1u, 2u, 3u, 4u, 5u, 6u, 7u, 26u };
		string huntLogClassJobLabel = GetHuntLogClassJobLabel(choice.CombatStarterClassId);
		ImGui.SetNextItemWidth(175f);
		ImU8String label = new ImU8String(20, 1);
		label.AppendLiteral("##CombatStarterClass");
		label.AppendFormatted(contentId);
		if (!ImGui.BeginCombo(label, huntLogClassJobLabel))
		{
			return;
		}
		uint[] array2 = array;
		foreach (uint num in array2)
		{
			bool flag = choice.CombatStarterClassId == num;
			if (ImGui.Selectable(GetHuntLogClassJobLabel(num), flag))
			{
				choice.CombatStarterClassId = num;
				configuration.Save();
			}
			if (flag)
			{
				ImGui.SetItemDefaultFocus();
			}
		}
		ImGui.EndCombo();
	}

	private void RegenerateRetainerSamples()
	{
		retainerSamplesInitialized = true;
		try
		{
			IEnumerable<string> unavailableNames = (from retainer in xadbRetainerSnapshots.Values.SelectMany((XadbRetainerSnapshot snapshot) => snapshot.Retainers)
				select retainer.Name).Concat(configuration.RetainerSetup.Checkpoints.Values.SelectMany((CharacterRetainerSetupCheckpoint checkpoint) => checkpoint.ReservedNames));
			configuration.RetainerSetup.SampleNames = retainerCreationService.GenerateSamples(unavailableNames).ToList();
			configuration.Save();
			retainerUiMessage = ((configuration.RetainerSetup.SampleNames.Count == 10) ? "Generated ten unique hybrid sample names from Lumina CharaMakeName data." : $"Generated {configuration.RetainerSetup.SampleNames.Count}/10 unique hybrid sample names within the bounded attempt limit.");
		}
		catch (Exception ex)
		{
			configuration.RetainerSetup.SampleNames.Clear();
			configuration.Save();
			retainerUiMessage = "Name generation failed: " + ex.Message;
			log.Warning("[RetainerSetup] " + retainerUiMessage);
		}
	}

	private static bool DrawRetainerEnumCombo<T>(string label, string id, ref T value, Func<Enum, string> formatter, float width = 145f) where T : struct, Enum
	{
		if (!string.IsNullOrWhiteSpace(label))
		{
			ImGui.TextUnformatted(label);
			ImGui.SameLine();
		}
		ImGui.SetNextItemWidth(width);
		bool result = false;
		if (ImGui.BeginCombo(id, formatter(value)))
		{
			T[] values = Enum.GetValues<T>();
			foreach (T val in values)
			{
				bool flag = EqualityComparer<T>.Default.Equals(value, val);
				if (ImGui.Selectable(formatter(val), flag))
				{
					value = val;
					result = true;
				}
				if (flag)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		return result;
	}

	private static string FormatRetainerEnum(Enum value)
	{
		if (value is RetainerStarterCity retainerStarterCity)
		{
			switch (retainerStarterCity)
			{
			case RetainerStarterCity.LimsaLominsa:
				return "Limsa Lominsa";
			case RetainerStarterCity.Uldah:
				return "Ul'dah";
			}
		}
		else if (value is RetainerAppearanceRace)
		{
			if ((RetainerAppearanceRace)(object)value == RetainerAppearanceRace.AuRa)
			{
				return "Au Ra";
			}
		}
		else if (value is RetainerStopAfter)
		{
			switch ((RetainerStopAfter)(object)value)
			{
			case RetainerStopAfter.ArrivedAtVocate:
				return "Arrived at Vocate (0%)";
			case RetainerStopAfter.RetainersHired:
				return "Retainers hired (20%)";
			case RetainerStopAfter.VenturesUnlocked:
				return "Ventures unlocked (40%)";
			case RetainerStopAfter.StarterGearReady:
				return "Starter gear ready (60%)";
			case RetainerStopAfter.ClassAndGearAssigned:
				return "Class and gear assigned (80%)";
			case RetainerStopAfter.AutoRetainerBootstrapped:
				return "AutoRetainer bootstrapped (100%)";
			}
		}
		return value.ToString();
	}

	private void DrawSettingsTabFull()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Plugin Settings");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		using ImRaii.ImChild imChild = ImRaii.Child("SettingsScrollArea", new Vector2(0f, 0f), border: false);
		if (!imChild.Success)
		{
			return;
		}
		Configuration config = plugin.Configuration;
		DrawSettingSection("Appearance", delegate
		{
			int v = (int)(config.WindowOpacity * 100f);
			if (ImGui.SliderInt("Window Opacity##Opacity", ref v, 10, 100, "%d%%"))
			{
				config.WindowOpacity = (float)v / 100f;
				config.Save();
			}
			DrawInfoIcon("Controls the transparency of the entire window including the title bar.\nMinimum 10%, Maximum 100%.");
		});
		ImGuiHelpers.ScaledDummy(5f);
		DrawSettingSection("Equipment Fallback", delegate
		{
			config.EnableFriendshipCirclet = DrawSettingWithInfo("Force Friendship Circlet through Level 25", config.EnableFriendshipCirclet, "Questionable 7.5.6+ includes smart equipment handling.\nEnable this only to force-equip the Friendship Circlet through level 25.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
		}, config.EnableFriendshipCirclet);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Submarine Management", delegate
		{
			config.EnableSubmarineCheck = DrawSettingWithInfo("Enable Submarine Monitoring", config.EnableSubmarineCheck, "Automatically monitors submarines and pauses quest rotation when submarines are ready.\nPrevents quest progression while submarines need attention.\nImpact: Rotation will pause when submarines are detected.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableSubmarineCheck)
			{
				ImGui.Indent();
				int v = config.SubmarineCheckInterval;
				if (ImGui.SliderInt("Check Interval (seconds)##Submarine", ref v, 30, 600))
				{
					config.SubmarineCheckInterval = v;
					config.Save();
				}
				DrawInfoIcon("How often to check for submarine status.\nMinimum 30s.");
				int v2 = config.SubmarineReloginCooldown;
				if (ImGui.SliderInt("Cooldown after Relog (seconds)", ref v2, 60, 300))
				{
					config.SubmarineReloginCooldown = v2;
					config.Save();
				}
				DrawInfoIcon("Time to wait after character switch before checking submarines again.");
				int v3 = config.SubmarineWaitTime;
				if (ImGui.SliderInt("Wait time before submarine (seconds)", ref v3, 10, 120))
				{
					config.SubmarineWaitTime = v3;
					config.Save();
				}
				DrawInfoIcon("Delay before starting submarine operations after detection.");
				ImGui.Unindent();
			}
		}, config.EnableSubmarineCheck);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("AutoRetainer Post Process Event Quests", delegate
		{
			config.RunEventQuestsOnARPostProcess = DrawSettingWithInfo("Run Event Quests on AR Post Process", config.RunEventQuestsOnARPostProcess, "AUTO-DETECTION: Automatically detects and runs active Event Quests when AutoRetainer completes a character.\nEvent Quests are detected via Questionable IPC (same as manual Event Quest tab).\nAll prerequisites will be automatically resolved and executed.\nAutoRetainer will wait until all Event Quests are completed before proceeding.\nImpact: Extends AR post-process time but ensures Event Quests are completed.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.RunEventQuestsOnARPostProcess)
			{
				ImGui.Indent();
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.8f, 0.4f, 1f));
				ImGui.TextUnformatted("Auto-Detection Enabled");
				ImGui.PopStyleColor();
				ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
				ImGui.TextWrapped("Event Quests will be automatically detected from Questionable when AR Post Process starts. No manual configuration needed - just enable this setting and the plugin will handle the rest!");
				ImGui.PopStyleColor();
				ImGuiHelpers.ScaledDummy(5f);
				int v = config.EventQuestPostProcessTimeoutMinutes;
				if (ImGui.SliderInt("Timeout (minutes)", ref v, 10, 60))
				{
					config.EventQuestPostProcessTimeoutMinutes = v;
					config.Save();
				}
				DrawInfoIcon("Maximum time to wait for Event Quests to complete.\nAfter timeout, AR will proceed with next character.");
				ImGui.Unindent();
			}
		}, config.RunEventQuestsOnARPostProcess);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Movement Monitor", delegate
		{
			config.EnableMovementMonitor = DrawSettingWithInfo("Enable Movement Monitor", config.EnableMovementMonitor, "Detects a stationary character and restarts Questionable with /qst reload followed by /qst start.\nIf the optional Stuck Rotation Strategy skips the character, that character switch takes priority.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableMovementMonitor)
			{
				ImGui.Indent();
				int data = config.MovementCheckInterval;
				if (ImGui.InputInt("Movement Monitoring Interval (seconds)", ref data))
				{
					config.MovementCheckInterval = Math.Max(1, data);
					config.Save();
				}
				DrawInfoIcon("How often to check player position.\nLower values = faster stuck detection.");
				int v = config.MovementStuckThreshold;
				if (ImGui.SliderInt("Stuck Threshold (seconds)", ref v, 5, 120))
				{
					config.MovementStuckThreshold = v;
					config.Save();
				}
				DrawInfoIcon("Time without movement before considering player stuck.\nHigher values = less false positives.");
				ImGui.Unindent();
			}
		}, config.EnableMovementMonitor);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Standard Stop Point Rotation - Combat Handling", delegate
		{
			config.EnableCombatHandling = DrawSettingWithInfo("Enable Combat Handling", config.EnableCombatHandling, "Handles overworld combat only while the Standard Stop Point Rotation is active.\nQuestionable 7.5.6+ includes combat handling for its quests.\nEnable only as a Companion-specific backend override; Solo Duties and Hunt Logs are unaffected.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableCombatHandling)
			{
				ImGui.Indent();
				int v = config.CombatHPThreshold;
				if (ImGui.SliderInt("HP Threshold (%)", ref v, 1, 99))
				{
					config.CombatHPThreshold = v;
					config.Save();
				}
				DrawInfoIcon("Start the selected Stop Point combat handling when HP reaches this percentage.");
				ImGuiHelpers.ScaledDummy(5f);
				if (ImGui.RadioButton("Use Default Combat Handling", config.StopPointCombatHandlingMode == CombatHandlingMode.DefaultBackends))
				{
					config.StopPointCombatHandlingMode = CombatHandlingMode.DefaultBackends;
					config.Save();
				}
				if (config.StopPointCombatHandlingMode == CombatHandlingMode.DefaultBackends)
				{
					ImGui.Indent();
					bool v2 = config.EnableStopPointRSR;
					if (ImGui.Checkbox("RSR##StopPointCombat", ref v2) && (v2 || config.EnableStopPointVBM || config.EnableStopPointBMRAI))
					{
						config.EnableStopPointRSR = v2;
						config.Save();
					}
					bool v3 = config.EnableStopPointVBM;
					if (ImGui.Checkbox("VBM##StopPointCombat", ref v3) && (v3 || config.EnableStopPointRSR || config.EnableStopPointBMRAI))
					{
						config.EnableStopPointVBM = v3;
						config.Save();
					}
					bool v4 = config.EnableStopPointBMRAI;
					if (ImGui.Checkbox("BMR##StopPointCombat", ref v4) && (v4 || config.EnableStopPointRSR || config.EnableStopPointVBM))
					{
						config.EnableStopPointBMRAI = v4;
						config.Save();
					}
					DrawInfoIcon("Select one or more supported combat backends. At least one remains enabled.");
					ImGui.Unindent();
				}
				if (ImGui.RadioButton("Use Own Commands", config.StopPointCombatHandlingMode == CombatHandlingMode.CustomCommands))
				{
					config.StopPointCombatHandlingMode = CombatHandlingMode.CustomCommands;
					config.Save();
				}
				if (config.StopPointCombatHandlingMode == CombatHandlingMode.CustomCommands)
				{
					ImGui.Indent();
					DrawStopPointCommandList("Commands when combat starts", "StopPointCombatStart", ref stopPointCombatStartCommandInput, config.StopPointCombatStartCommands, delegate(string commands)
					{
						config.StopPointCombatStartCommands = commands;
						config.Save();
					});
					DrawStopPointCommandList("Commands after combat is over", "StopPointCombatEnd", ref stopPointCombatEndCommandInput, config.StopPointCombatEndCommands, delegate(string commands)
					{
						config.StopPointCombatEndCommands = commands;
						config.Save();
					});
					ImGui.Unindent();
				}
				ImGui.Unindent();
			}
		}, config.EnableCombatHandling);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Solo Duty - Combat Handling", delegate
		{
			if (ImGui.RadioButton("Use Default Combat Handling##SoloDuty", config.SoloDutyCombatHandlingMode == CombatHandlingMode.DefaultBackends))
			{
				config.SoloDutyCombatHandlingMode = CombatHandlingMode.DefaultBackends;
				config.Save();
			}
			if (config.SoloDutyCombatHandlingMode == CombatHandlingMode.DefaultBackends)
			{
				ImGui.Indent();
				bool v = config.EnableSoloDutyRSR;
				if (ImGui.Checkbox("RSR##SoloDutyCombat", ref v) && (v || config.EnableSoloDutyVBM || config.EnableSoloDutyBMRAI))
				{
					config.EnableSoloDutyRSR = v;
					config.Save();
				}
				bool v2 = config.EnableSoloDutyVBM;
				if (ImGui.Checkbox("VBM##SoloDutyCombat", ref v2) && (v2 || config.EnableSoloDutyRSR || config.EnableSoloDutyBMRAI))
				{
					config.EnableSoloDutyVBM = v2;
					config.Save();
				}
				bool v3 = config.EnableSoloDutyBMRAI;
				if (ImGui.Checkbox("BMR##SoloDutyCombat", ref v3) && (v3 || config.EnableSoloDutyRSR || config.EnableSoloDutyVBM))
				{
					config.EnableSoloDutyBMRAI = v3;
					config.Save();
				}
				DrawInfoIcon("Select one or more Solo Duty combat backends. At least one remains enabled.");
				ImGui.Unindent();
			}
			if (ImGui.RadioButton("Use Own Commands##SoloDuty", config.SoloDutyCombatHandlingMode == CombatHandlingMode.CustomCommands))
			{
				config.SoloDutyCombatHandlingMode = CombatHandlingMode.CustomCommands;
				config.Save();
			}
			if (config.SoloDutyCombatHandlingMode == CombatHandlingMode.CustomCommands)
			{
				ImGui.Indent();
				DrawStopPointCommandList("Commands when the Solo Duty starts", "SoloDutyCombatStart", ref soloDutyCombatStartCommandInput, config.SoloDutyCombatStartCommands, delegate(string commands)
				{
					config.SoloDutyCombatStartCommands = commands;
					config.Save();
				});
				DrawStopPointCommandList("Commands after the Solo Duty is over", "SoloDutyCombatEnd", ref soloDutyCombatEndCommandInput, config.SoloDutyCombatEndCommands, delegate(string commands)
				{
					config.SoloDutyCombatEndCommands = commands;
					config.Save();
				});
				ImGui.Unindent();
			}
		}, config.SoloDutyCombatHandlingMode == CombatHandlingMode.CustomCommands || config.EnableSoloDutyRSR || config.EnableSoloDutyVBM || config.EnableSoloDutyBMRAI);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Death Handling", delegate
		{
			config.EnableDeathHandling = DrawSettingWithInfo("Enable Death Handling", config.EnableDeathHandling, "Automatically respawns and teleports back to death location.\nSaves position before death and returns after respawn.\nIncluded in Questionable 7.5.6+ through Death Recovery. Keep disabled for Questionable-controlled quests.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableDeathHandling)
			{
				ImGui.Indent();
				int v = config.DeathRespawnDelay;
				if (ImGui.SliderInt("Teleport Delay (seconds)", ref v, 1, 30))
				{
					config.DeathRespawnDelay = v;
					config.Save();
				}
				DrawInfoIcon("Time to wait after respawn before teleporting back to death location.\nAllows time for loading and stabilization.");
				ImGui.Unindent();
			}
		}, config.EnableDeathHandling);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Stuck Rotation", delegate
		{
			config.EnableStuckRotation = DrawSettingWithInfo("Enable Stuck Rotation Strategy", config.EnableStuckRotation, "Automatically skips the current character if they get stuck repeatedly.\nQuestionable 7.5.6+ handles step recovery; this is only a final character-rotation fallback.\nReplaces the character with the next one in the rotation.\nImpact: Prevents getting stuck on infinite loops.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableStuckRotation)
			{
				ImGui.Indent();
				int v = config.StuckRotationThreshold;
				if (ImGui.SliderInt("Stuck Threshold (Detections)", ref v, 3, 20))
				{
					config.StuckRotationThreshold = v;
					config.Save();
				}
				DrawInfoIcon("Number of final-fallback stuck detections before skipping the character.\nExample: If set to 5, the 5th detection triggers a skip.");
				int v2 = config.SkippedCharacterRetryCount;
				if (ImGui.SliderInt("Retry Skipped Characters", ref v2, 0, 99))
				{
					config.SkippedCharacterRetryCount = Math.Clamp(v2, 0, 99);
					config.Save();
				}
				DrawInfoIcon("How often skipped characters should be tried again before the rotation moves on. 0 disables retry.");
				ImGui.Unindent();
			}
		}, config.EnableStuckRotation);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Character Management", delegate
		{
			config.EnableMultiModeAfterRotation = DrawSettingWithInfo("Enable Multi-Mode After Rotation", config.EnableMultiModeAfterRotation, "Automatically enables AutoRetainer multi-mode after rotation completes.\nAllows retainer/submarine management after quest rotation.\nImpact: Multi-mode will activate when all quests are done.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			config.ReturnToHomeworldOnStopQuest = DrawSettingWithInfo("Return to Homeworld on Stop Quest", config.ReturnToHomeworldOnStopQuest, "Automatically returns character to home world when rotation stops.\nUses /li command to return home.\nImpact: Characters will be sent home after completing their quests.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
		}, config.EnableMultiModeAfterRotation);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Pre Character-Switch Cleanup", delegate
		{
			config.EnableAutoRepair = DrawSettingWithInfo("Enable Auto-Repair", config.EnableAutoRepair, "Checks the main character's equipped gear condition before character switch.\nIf the lowest item is at or below the threshold, runs /ad repair and waits for completion.\nQuestionable 7.5.6+ handles repair at quest boundaries and duty gates; this remains a pre-switch fallback.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			if (config.EnableAutoRepair)
			{
				ImGui.Indent();
				int v = config.RepairThreshold;
				if (ImGui.SliderInt("Repair Threshold (%)", ref v, 0, 99))
				{
					config.RepairThreshold = v;
					config.Save();
				}
				DrawInfoIcon("Lowest equipped gear condition at or below this value triggers /ad repair.");
				ImGui.Unindent();
			}
			config.EnableAysDiscard = DrawSettingWithInfo("Enable AYS Discard", config.EnableAysDiscard, "Runs /ays discard after optional repair and waits for AutoRetainer to finish.\nRuns independently of Data Center Travel.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
		}, config.EnableAutoRepair || config.EnableAysDiscard);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Safe Wait Settings", delegate
		{
			config.EnableSafeWaitBeforeCharacterSwitch = DrawSettingWithInfo("Enable Safe Wait Before Character Switch", config.EnableSafeWaitBeforeCharacterSwitch, "Waits for safe conditions before switching characters.\nChecks for combat, cutscenes, and loading screens.\nImpact: Character switches will be delayed until safe.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
			config.EnableSafeWaitAfterCharacterSwitch = DrawSettingWithInfo("Enable Safe Wait After Character Switch", config.EnableSafeWaitAfterCharacterSwitch, "Waits for safe conditions after logging in new character.\nEnsures character is fully loaded before starting quests.\nImpact: Quest start will be delayed until character is ready.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
		}, config.EnableSafeWaitBeforeCharacterSwitch);
		ImGuiHelpers.ScaledDummy(10f);
		DrawSettingSection("Post Moogle Service", delegate
		{
			config.EnablePostMoogleMailCheck = DrawSettingWithInfo("Enable Post Moogle Mail Check", config.EnablePostMoogleMailCheck, "Automatically checks for mail notifications before starting quest or DC Travel.\nTeleports to a main city to check mail if needed.\nIf no Mail is found it will use all of your Consumables in your Inventory.\nQuestionable may already redeem supported quest rewards; Companion processes supported items left in the inventory.\nImpact: Will teleport character if mail notification is detected and use all consumables.");
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				config.Save();
			}
		}, config.EnablePostMoogleMailCheck);
		ImGuiHelpers.ScaledDummy(10f);
	}

	private void DrawSettingSection(string title, System.Action drawContent, bool isEnabled = false)
	{
		Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
		float x = ImGui.GetContentRegionAvail().X;
		ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
		ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.15f, 0.8f));
		Vector4 input;
		if (isEnabled)
		{
			float num = (MathF.Sin((float)ImGui.GetTime() * 2f) + 1f) / 2f;
			input = new Vector4(0.47f, 0.69f, 0.88f, 0.5f + num * 0.5f);
		}
		else
		{
			input = new Vector4(colorPrimary.X * 0.5f, colorPrimary.Y * 0.5f, colorPrimary.Z * 0.5f, 0.6f);
		}
		uint col = ImGui.ColorConvertFloat4ToU32(input);
		Vector2 pMin = cursorScreenPos;
		ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(colorPrimary.X * 0.3f, colorPrimary.Y * 0.3f, colorPrimary.Z * 0.3f, 0.5f));
		ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(colorPrimary.X * 0.4f, colorPrimary.Y * 0.4f, colorPrimary.Z * 0.4f, 0.6f));
		ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(colorPrimary.X * 0.5f, colorPrimary.Y * 0.5f, colorPrimary.Z * 0.5f, 0.7f));
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		bool num2 = ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen);
		ImGui.PopStyleColor(4);
		if (num2)
		{
			ImGui.Indent(10f);
			drawContent();
			ImGui.Unindent(10f);
			ImGuiHelpers.ScaledDummy(5f);
		}
		Vector2 cursorScreenPos2 = ImGui.GetCursorScreenPos();
		windowDrawList.AddRect(pMin, cursorScreenPos2 + new Vector2(x, 0f), col, 4f, ImDrawFlags.None, isEnabled ? 2.5f : 1.5f);
	}

	private static List<string> ParseSavedStopPointCommands(string commands)
	{
		List<string> list = new List<string>();
		string[] array = (commands ?? string.Empty).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.Length > 0)
			{
				list.Add(text);
			}
		}
		return list;
	}

	private void DrawStopPointCommandList(string label, string id, ref string input, string savedCommands, Action<string> save)
	{
		ImGui.TextUnformatted(label + ":");
		List<string> list = ParseSavedStopPointCommands(savedCommands);
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
		DrawInfoIcon("Type one command and press Enter to save it as a separate command entry.");
	}

	private bool DrawSettingWithInfo(string label, bool value, string infoText)
	{
		ImGui.Checkbox(label, ref value);
		ImGui.SameLine();
		DrawInfoIcon(infoText);
		return value;
	}

	private void DrawInfoIcon(string tooltipText)
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("[i]");
		ImGui.PopStyleColor();
		if (ImGui.IsItemHovered())
		{
			ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.1f, 0.1f, 0.1f, 0.95f));
			ImGui.PushStyleColor(ImGuiCol.Border, colorPrimary);
			ImGui.BeginTooltip();
			ImGui.PushTextWrapPos(400f);
			ImGui.TextUnformatted(tooltipText);
			ImGui.PopTextWrapPos();
			ImGui.EndTooltip();
			ImGui.PopStyleColor(2);
		}
	}

	private void DrawCharactersTab()
	{
		if (questRotationService.UpdateCurrentCharacterJobLevels())
		{
			string currentCharacter = autoRetainerIpc.GetCurrentCharacter();
			if (!string.IsNullOrEmpty(currentCharacter))
			{
				characterProgressCache.Remove(currentCharacter);
				characterGrandCompanyRankFilterCache.Remove(currentCharacter);
			}
		}
		object obj = selectedDCFilter switch
		{
			0 => "All Characters", 
			1 => "EU Characters", 
			2 => "NA Characters", 
			3 => "JP Characters", 
			4 => "OCE Characters", 
			_ => "Characters", 
		};
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted((string?)obj);
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		List<string> filteredCharacters = GetFilteredCharacters();
		if (!initialCharacterLoadComplete)
		{
			if (characterLoadAttempts < 5)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
				double totalSeconds = (DateTime.Now - initialLoadStartTime).TotalSeconds;
				int num = ((characterLoadAttempts < retryDelaysSeconds.Length) ? (retryDelaysSeconds[characterLoadAttempts] - (int)totalSeconds) : 0);
				if (num > 0)
				{
					ImU8String text = new ImU8String(36, 3);
					text.AppendLiteral("Loading characters... (Retry ");
					text.AppendFormatted(characterLoadAttempts);
					text.AppendLiteral("/");
					text.AppendFormatted(5);
					text.AppendLiteral(" in ");
					text.AppendFormatted(num);
					text.AppendLiteral("s)");
					ImGui.TextUnformatted(text);
				}
				else
				{
					ImU8String text2 = new ImU8String(33, 2);
					text2.AppendLiteral("Loading characters... (Attempt ");
					text2.AppendFormatted(characterLoadAttempts + 1);
					text2.AppendLiteral("/");
					text2.AppendFormatted(5);
					text2.AppendLiteral(")");
					ImGui.TextUnformatted(text2);
				}
				ImGui.PopStyleColor();
				ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
				ImGui.TextUnformatted("Waiting for AutoRetainer to initialize...");
				ImGui.PopStyleColor();
				ImGuiHelpers.ScaledDummy(10f);
				if (ImGui.Button("Retry Now"))
				{
					characterLoadAttempts = 0;
					initialLoadStartTime = DateTime.MinValue;
					autoRetainerIpc.TryReinitialize();
					RefreshCharacterList();
				}
			}
			else
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
				ImGui.TextUnformatted("AutoRetainer not available");
				ImGui.PopStyleColor();
				ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
				ImGui.TextUnformatted("Please ensure AutoRetainer plugin is installed and enabled.");
				ImU8String text3 = new ImU8String(29, 1);
				text3.AppendLiteral("Tried ");
				text3.AppendFormatted(5);
				text3.AppendLiteral(" times without success.");
				ImGui.TextUnformatted(text3);
				ImGui.PopStyleColor();
				ImGuiHelpers.ScaledDummy(10f);
				if (ImGui.Button("Retry Connection"))
				{
					characterLoadAttempts = 0;
					initialLoadStartTime = DateTime.Now;
					initialCharacterLoadComplete = false;
					autoRetainerIpc.TryReinitialize();
					RefreshCharacterList();
				}
			}
			return;
		}
		if (!autoRetainerIpc.IsAvailable)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.8f, 0f, 1f));
			ImGui.TextUnformatted("AutoRetainer connection lost");
			ImGui.PopStyleColor();
			ImGui.TextUnformatted("The connection to AutoRetainer was lost.");
			if (ImGui.Button("Reconnect"))
			{
				autoRetainerIpc.TryReinitialize();
				RefreshCharacterList();
			}
			return;
		}
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Refresh data"))
		{
			autoRetainerIpc.ClearCache();
			RefreshCharacterList();
			XadbImportResult import = ImportXadbProgress(registeredCharacters);
			characterProgressCache.Clear();
			LogXadbRefreshSummary("Character refresh", import, registeredCharacters.Count);
		}
		ImGui.PopStyleColor(2);
		ImGui.SameLine();
		if (ImGui.Button("Select visible"))
		{
			foreach (string item2 in filteredCharacters)
			{
				characterSelection[item2] = true;
			}
			SaveCharacterSelection();
		}
		ImGui.SameLine();
		if (ImGui.Button("Clear visible"))
		{
			foreach (string item3 in filteredCharacters)
			{
				characterSelection[item3] = false;
			}
			SaveCharacterSelection();
		}
		ImGui.SameLine();
		if (ImGui.Button("Clear all"))
		{
			string[] array = characterSelection.Keys.ToArray();
			foreach (string key in array)
			{
				characterSelection[key] = false;
			}
			SaveCharacterSelection();
		}
		ImGui.SameLine();
		if (ImGui.Button("Select current"))
		{
			IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
			if (localPlayer != null)
			{
				string value = localPlayer.HomeWorld.Value.Name.ExtractText();
				string text4 = $"{localPlayer.Name}@{value}";
				if (characterSelection.ContainsKey(text4))
				{
					characterSelection[text4] = true;
					SaveCharacterSelection();
					log.Information("[CharactersTab] Selected current character: " + text4);
				}
				else
				{
					log.Warning("[CharactersTab] Current character '" + text4 + "' not found in character list");
				}
			}
			else
			{
				log.Warning("[CharactersTab] LocalPlayer is null - cannot get current character");
			}
		}
		CharacterFilterConfiguration characterFilters = configuration.CharacterFilters;
		ImGuiHelpers.ScaledDummy(8f);
		ImGui.BeginChild("CharacterFilterFrame", new Vector2(0f, 142f * ImGuiHelpers.GlobalScale), border: true);
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Filters");
		ImGui.PopStyleColor();
		ImGui.TextUnformatted("Data center");
		ImGui.SameLine();
		ImGui.SetNextItemWidth(90f);
		if (ImGui.BeginCombo("##DataCenterFilter", characterFilters.DataCenter))
		{
			for (int j = 0; j < availableDataCenters.Count; j++)
			{
				bool flag = string.Equals(characterFilters.DataCenter, availableDataCenters[j], StringComparison.OrdinalIgnoreCase);
				if (ImGui.Selectable(availableDataCenters[j], flag))
				{
					selectedDCFilter = j;
					selectedTab = j;
					characterFilters.DataCenter = availableDataCenters[j];
					characterFilters.World = "All";
					configuration.Save();
					filteredCharacters = GetFilteredCharacters();
				}
				if (flag)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGui.SameLine();
		ImGui.TextUnformatted("World");
		ImGui.SameLine();
		ImGui.SetNextItemWidth(150f);
		List<string> worldsForCurrentDatacenter = GetWorldsForCurrentDatacenter();
		if (ImGui.BeginCombo("##WorldFilter", characterFilters.World))
		{
			if (ImGui.Selectable("All", characterFilters.World == "All"))
			{
				characterFilters.World = "All";
				configuration.Save();
				filteredCharacters = GetFilteredCharacters();
			}
			foreach (string item4 in worldsForCurrentDatacenter.OrderBy((string w) => w))
			{
				if (ImGui.Selectable(item4, characterFilters.World == item4))
				{
					characterFilters.World = item4;
					configuration.Save();
					filteredCharacters = GetFilteredCharacters();
				}
			}
			ImGui.EndCombo();
		}
		ImGui.SameLine();
		bool v = characterFilters.BelowGrandCompanyRank9;
		if (ImGui.Checkbox("Below GC rank 9", ref v))
		{
			characterFilters.BelowGrandCompanyRank9 = v;
			characterGrandCompanyRankFilterCache.Clear();
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Show only characters below Grand Company rank 9. Characters with no Grand Company or an unknown rank are included.");
		}
		bool v2 = characterFilters.AboveLevelEnabled;
		if (ImGui.Checkbox("> Level", ref v2))
		{
			characterFilters.AboveLevelEnabled = v2;
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		ImGui.SameLine();
		ImGui.SetNextItemWidth(55f);
		int data = characterFilters.AboveLevel;
		if (ImGui.InputInt("##AboveLevelValue", ref data))
		{
			characterFilters.AboveLevel = Math.Clamp(data, 0, 100);
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Strict lower bound. Unknown levels are excluded while enabled.");
		}
		ImGui.SameLine();
		bool v3 = characterFilters.BelowLevelEnabled;
		if (ImGui.Checkbox("< Level", ref v3))
		{
			characterFilters.BelowLevelEnabled = v3;
			configuration.RetainerSetup.FilterBelowLevelEnabled = v3;
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		ImGui.SameLine();
		ImGui.SetNextItemWidth(55f);
		int data2 = characterFilters.BelowLevel;
		if (ImGui.InputInt("##BelowLevelValue", ref data2))
		{
			characterFilters.BelowLevel = Math.Clamp(data2, 1, 100);
			configuration.RetainerSetup.FilterBelowLevel = characterFilters.BelowLevel;
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Strict upper bound. Unknown levels are excluded while enabled.");
		}
		ImGui.SameLine();
		bool v4 = characterFilters.MissingRetainers;
		if (ImGui.Checkbox("Missing retainers", ref v4))
		{
			characterFilters.MissingRetainers = v4;
			configuration.RetainerSetup.FilterIncompleteSetup = v4;
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Includes Confirmed zero, uncollected/Unknown roster candidates, and every non-complete Companion checkpoint. Unknown rows are display candidates and still require native validation before hiring.");
		}
		ImGui.TextUnformatted("Class / job");
		ImGui.SameLine();
		ImGui.SetNextItemWidth(190f);
		ClassUnlockTargetDefinition classUnlockTargetDefinition = ClassUnlockCatalog.Find(characterFilters.ClassJobId);
		string text5 = ((classUnlockTargetDefinition == null) ? "All" : (classUnlockTargetDefinition.Abbreviation + " - " + classUnlockTargetDefinition.Name));
		if (ImGui.BeginCombo("##ClassJobUnlockFilter", text5))
		{
			if (ImGui.Selectable("All", characterFilters.ClassJobId == 0))
			{
				characterFilters.ClassJobId = 0u;
				characterFilters.ClassUnlockStatus = ClassUnlockFilterStatus.All;
				configuration.Save();
				filteredCharacters = GetFilteredCharacters();
			}
			ClassUnlockTargetDefinition[] source = ClassUnlockCatalog.Targets.Where((ClassUnlockTargetDefinition target) => target.IsAvailable).ToArray();
			(string, ClassUnlockTargetDefinition[])[] array2 = new(string, ClassUnlockTargetDefinition[])[2]
			{
				("DoW / DoM", source.Where((ClassUnlockTargetDefinition target) => target.Category != ClassUnlockCategory.CraftingGathering).OrderBy<ClassUnlockTargetDefinition, string>((ClassUnlockTargetDefinition target) => target.Name, StringComparer.OrdinalIgnoreCase).ToArray()),
				("DoH / DoL", source.Where((ClassUnlockTargetDefinition target) => target.Category == ClassUnlockCategory.CraftingGathering).OrderBy<ClassUnlockTargetDefinition, string>((ClassUnlockTargetDefinition target) => target.Name, StringComparer.OrdinalIgnoreCase).ToArray())
			};
			for (int i = 0; i < array2.Length; i++)
			{
				(string, ClassUnlockTargetDefinition[]) tuple = array2[i];
				ImGui.Spacing();
				ImGui.TextDisabled(tuple.Item1);
				ImGui.Separator();
				ClassUnlockTargetDefinition[] item = tuple.Item2;
				foreach (ClassUnlockTargetDefinition classUnlockTargetDefinition2 in item)
				{
					bool selected = characterFilters.ClassJobId == classUnlockTargetDefinition2.ClassJobId;
					ImU8String label = new ImU8String(3, 2);
					label.AppendFormatted(classUnlockTargetDefinition2.Abbreviation);
					label.AppendLiteral(" - ");
					label.AppendFormatted(classUnlockTargetDefinition2.Name);
					if (ImGui.Selectable(label, selected))
					{
						characterFilters.ClassJobId = classUnlockTargetDefinition2.ClassJobId;
						if (characterFilters.ClassUnlockStatus == ClassUnlockFilterStatus.All)
						{
							characterFilters.ClassUnlockStatus = ClassUnlockFilterStatus.Unlocked;
						}
						configuration.Save();
						filteredCharacters = GetFilteredCharacters();
					}
				}
			}
			ImGui.EndCombo();
		}
		ImGui.SameLine();
		ImGui.TextUnformatted("Unlock status");
		ImGui.SameLine();
		ImGui.SetNextItemWidth(135f);
		if (ImGui.BeginCombo("##ClassUnlockStatusFilter", FormatClassUnlockFilterStatus(characterFilters.ClassUnlockStatus)))
		{
			ClassUnlockFilterStatus[] values = Enum.GetValues<ClassUnlockFilterStatus>();
			foreach (ClassUnlockFilterStatus classUnlockFilterStatus in values)
			{
				if (ImGui.Selectable(FormatClassUnlockFilterStatus(classUnlockFilterStatus), characterFilters.ClassUnlockStatus == classUnlockFilterStatus))
				{
					characterFilters.ClassUnlockStatus = classUnlockFilterStatus;
					configuration.Save();
					filteredCharacters = GetFilteredCharacters();
				}
			}
			ImGui.EndCombo();
		}
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Uses cached AutoRetainer class/job levels. Unknown means no complete offline snapshot is available; no IPC or reflection calls occur while drawing.");
		}
		ImGui.EndChild();
		if (!CharacterFilterLogic.IsLevelRangeValid(characterFilters))
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImGui.TextUnformatted("The lower level bound must be less than the upper level bound; no characters match this range.");
			ImGui.PopStyleColor();
		}
		int num3 = characterSelection.Count<KeyValuePair<string, bool>>((KeyValuePair<string, bool> entry) => entry.Value);
		int value2 = filteredCharacters.Count((string character) => characterSelection.GetValueOrDefault(character));
		string activeLevelRangeLabel = GetActiveLevelRangeLabel(characterFilters);
		ImU8String text6 = new ImU8String(47, 5);
		text6.AppendLiteral("Showing ");
		text6.AppendFormatted(filteredCharacters.Count);
		text6.AppendLiteral(" of ");
		text6.AppendFormatted(registeredCharacters.Count);
		text6.AppendLiteral("  |  Selected ");
		text6.AppendFormatted(num3);
		text6.AppendLiteral(" total, ");
		text6.AppendFormatted(value2);
		text6.AppendLiteral(" visible  |  ");
		text6.AppendFormatted(activeLevelRangeLabel);
		ImGui.TextUnformatted(text6);
		ImGui.SameLine();
		if (ImGui.SmallButton("Reset filters"))
		{
			characterFilters.Reset();
			configuration.RetainerSetup.FilterBelowLevelEnabled = false;
			configuration.RetainerSetup.FilterBelowLevel = 100;
			configuration.RetainerSetup.FilterIncompleteSetup = false;
			selectedDCFilter = 0;
			selectedTab = 0;
			characterGrandCompanyRankFilterCache.Clear();
			configuration.Save();
			filteredCharacters = GetFilteredCharacters();
		}
		bool num4 = num3 > 0;
		bool v5 = configuration.AutoStartOnLogin;
		ImGui.BeginDisabled(!num4);
		if (ImGui.Checkbox("Auto Start on Boot", ref v5))
		{
			configuration.AutoStartOnLogin = v5;
			configuration.Save();
		}
		ImGui.EndDisabled();
		if (!num4)
		{
			ImGui.SameLine();
			ImGui.TextDisabled("Select at least one character to enable automatic startup.");
		}
		ImGuiHelpers.ScaledDummy(5f);
		if (filteredCharacters.Count == 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("No characters found");
			ImGui.PopStyleColor();
			return;
		}
		float num5 = (ImGui.GetContentRegionAvail().X - 20f) / 2f;
		using ImRaii.ImChild imChild = ImRaii.Child("CharacterCards", new Vector2(0f, 0f), border: false);
		if (!imChild.Success)
		{
			return;
		}
		int itemsCount = (filteredCharacters.Count + 1) / 2;
		ImGuiListClipperPtr imGuiListClipperPtr = ImGui.ImGuiListClipper();
		imGuiListClipperPtr.Begin(itemsCount, 208f + ImGui.GetStyle().ItemSpacing.Y);
		while (imGuiListClipperPtr.Step())
		{
			for (int num6 = imGuiListClipperPtr.DisplayStart; num6 < imGuiListClipperPtr.DisplayEnd; num6++)
			{
				for (int num7 = 0; num7 < 2; num7++)
				{
					int num8 = num6 * 2 + num7;
					if (num8 >= filteredCharacters.Count)
					{
						break;
					}
					string text7 = filteredCharacters[num8];
					string[] array3 = text7.Split('@');
					string text8 = ((array3.Length != 0) ? array3[0] : text7);
					string text9 = ((array3.Length > 1) ? array3[1] : "Unknown");
					if (num7 == 1)
					{
						ImGui.SameLine();
					}
					ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
					Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
					Vector4 input = new Vector4(0.15f, 0.15f, 0.18f, 0.9f);
					uint col = (characterSelection.GetValueOrDefault(text7, defaultValue: false) ? ImGui.ColorConvertFloat4ToU32(new Vector4(colorPrimary.X, colorPrimary.Y, colorPrimary.Z, 0.8f)) : ImGui.ColorConvertFloat4ToU32(new Vector4(colorPrimary.X * 0.3f, colorPrimary.Y * 0.3f, colorPrimary.Z * 0.3f, 0.5f)));
					windowDrawList.AddRectFilled(cursorScreenPos, cursorScreenPos + new Vector2(num5, 208f), ImGui.ColorConvertFloat4ToU32(input), 6f);
					windowDrawList.AddRect(cursorScreenPos, cursorScreenPos + new Vector2(num5, 208f), col, 6f, ImDrawFlags.None, 2f);
					ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(10f, 10f));
					using (ImRaii.PushId(text7))
					{
						bool v6 = characterSelection.GetValueOrDefault(text7, defaultValue: false);
						if (ImGui.Checkbox("##Select", ref v6))
						{
							characterSelection[text7] = v6;
							configuration.SelectedCharactersForUI = (from kvp in characterSelection
								where kvp.Value
								select kvp.Key).ToList();
							configuration.Save();
						}
					}
					ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(40f, 8f));
					ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
					ImGui.Text(text8);
					ImGui.PopStyleColor();
					ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(40f, 26f));
					ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
					ImGui.Text(text9);
					ImGui.PopStyleColor();
					if (characterProgressCache.TryGetValue(text7, out CharacterProgressInfo value3))
					{
						bool flag2 = false;
						if (value3.HighestCombatJobId != 0)
						{
							ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(10f, 50f));
							flag2 = DrawGameIcon(62100 + value3.HighestCombatJobId, 18f);
						}
						ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(flag2 ? 32f : 10f, 51f));
						ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
						string value4 = ((value3.HighestCombatJobLevel > 0) ? $"Lv. {value3.HighestCombatJobLevel}" : "Lv. -");
						ImU8String text10 = new ImU8String(2, 1);
						text10.AppendFormatted(value4);
						text10.AppendLiteral(" |");
						ImGui.TextUnformatted(text10);
						ImGui.SameLine(0f, 4f);
						if (value3.GrandCompanyRank > 0)
						{
							if (DrawGameIcon(GetGrandCompanyRankIconId(value3.GrandCompanyId, value3.GrandCompanyRank), 22f))
							{
								ImGui.SameLine(0f, 4f);
							}
							ImU8String text11 = new ImU8String(2, 1);
							text11.AppendLiteral("(");
							text11.AppendFormatted(value3.GrandCompanyRank);
							text11.AppendLiteral(")");
							ImGui.TextUnformatted(text11);
							ImGui.SameLine(0f, 4f);
							if (!DrawGameIcon(GetGrandCompanyCrestIconId(value3.GrandCompanyId), 22f))
							{
								ImGui.TextUnformatted("Unknown GC");
							}
						}
						else
						{
							ImGui.TextUnformatted("No GC");
						}
						ImGui.PopStyleColor();
						float num9 = num5 - 20f;
						Vector2 vector = cursorScreenPos + new Vector2(10f, 80f);
						Vector2 vector2 = vector + new Vector2(num9, 18f);
						float num10 = Math.Clamp(value3.MSQCompletionPercentage / 100f, 0f, 1f);
						uint col2 = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 0.8f));
						uint col3 = ImGui.ColorConvertFloat4ToU32(new Vector4(colorSecondary.X, colorSecondary.Y, colorSecondary.Z, 0.9f));
						windowDrawList.AddRectFilled(vector, vector2, col2, 4f);
						float num11 = vector.X + num9 * num10;
						if (num11 > vector.X)
						{
							windowDrawList.AddRectFilled(vector, new Vector2(num11, vector2.Y), col3, 4f);
						}
						string text12 = $"{value3.MSQCompletionPercentage:F0}% | {value3.CompletedQuestCount} Quests";
						Vector2 vector3 = ImGui.CalcTextSize(text12);
						Vector2 pos = new Vector2(vector.X + num9 / 2f - vector3.X / 2f, vector.Y + MathF.Max(0f, (18f - vector3.Y) / 2f));
						uint col4 = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.09f, 0.02f, 1f));
						uint col5 = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
						if (num11 > vector.X)
						{
							windowDrawList.PushClipRect(vector, new Vector2(num11, vector2.Y), intersectWithCurrentClipRect: true);
							windowDrawList.AddText(pos, col4, text12);
							windowDrawList.PopClipRect();
						}
						if (num11 < vector2.X)
						{
							windowDrawList.PushClipRect(new Vector2(num11, vector.Y), vector2, intersectWithCurrentClipRect: true);
							windowDrawList.AddText(pos, col5, text12);
							windowDrawList.PopClipRect();
						}
						ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(10f, 106f));
						ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
						ImGui.TextUnformatted(value3.RetainerRosterStatus switch
						{
							XadbRetainerRosterStatus.ConfirmedZero => "Retainers: 0", 
							XadbRetainerRosterStatus.Populated => $"Retainers: {value3.RetainerCount.GetValueOrDefault()} | Highest Lv. {value3.HighestRetainerLevel}" + (value3.RetainerEvidenceValidated ? string.Empty : " "), 
							_ => "Retainers: Unknown", 
						});
						ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(10f, 126f));
						ImGui.TextUnformatted(value3.RetainerSetupPercent.HasValue ? $"Setup: {value3.RetainerSetupPercent.Value}% | {value3.RetainerSetupStatus}" : ("Setup: " + value3.RetainerSetupStatus));
						if (ImGui.IsItemHovered() && TryGetRetainerCheckpoint(text7, out CharacterRetainerSetupCheckpoint checkpoint) && !string.IsNullOrWhiteSpace(checkpoint.LastError))
						{
							ImGui.SetTooltip(checkpoint.LastError);
						}
						ImGui.PopStyleColor();
					}
					else
					{
						ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(10f, 50f));
						ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
						ImGui.Text("Loading...");
						ImGui.PopStyleColor();
						GetCharacterProgress(text7);
					}
					DrawQuestRotationCombatJobControl(text7, cursorScreenPos, num5);
					ImGui.SetCursorScreenPos(cursorScreenPos);
					ImGui.Dummy(new Vector2(num5, 208f));
				}
			}
		}
		imGuiListClipperPtr.End();
		imGuiListClipperPtr.Destroy();
	}

	private void DrawQuestRotationCombatJobControl(string character, Vector2 cardPosition, float cardWidth)
	{
		configuration.QuestRotationCombatJobByCharacter.TryGetValue(character, out var value);
		bool flag = configuration.QuestRotationCombatJobByCharacter.ContainsKey(character);
		CharacterJobLevelSnapshot value2;
		IReadOnlyDictionary<uint, int> readOnlyDictionary = (configuration.CharacterJobLevels.TryGetValue(character, out value2) ? CombatJobResolverLogic.MergeTrustedAndObservedLevels(value2.CombatJobLevels, value2.XadbObservedCombatJobLevels).Levels : null);
		(uint, int, string)[] array = (from entry in readOnlyDictionary?.Where((KeyValuePair<uint, int> entry) => entry.Key != 0 && entry.Key <= 255 && entry.Value > 0 && JobClassification.IsCombatJob((byte)entry.Key))
			select (JobId: entry.Key, Level: entry.Value, Label: GetHuntLogClassJobLabel(entry.Key)) into entry
			orderby entry.Level descending
			select entry).ThenBy<(uint, int, string), string>(((uint JobId, int Level, string Label) entry) => entry.Label, StringComparer.OrdinalIgnoreCase).ThenBy<(uint, int, string), uint>(((uint JobId, int Level, string Label) entry) => entry.JobId).ToArray() ?? Array.Empty<(uint, int, string)>();
		ImGui.SetCursorScreenPos(cardPosition + new Vector2(10f, 156f));
		using (ImRaii.PushId("QuestRotationJob::" + character))
		{
			if (array.Length != 0)
			{
				string text = ((!flag) ? "No job change" : ((value == 0) ? "Use highest combat job" : $"{GetHuntLogClassJobLabel(value)} - Lv. {readOnlyDictionary.GetValueOrDefault(value)}"));
				ImGui.SetNextItemWidth(Math.Max(1f, cardWidth - 20f));
				bool num = ImGui.BeginCombo("##QuestRotationCombatJob", text);
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("The saved choice is shared by quest rotation and Hunt Logs. No job change disables quest-rotation setup and lets Hunt Logs use its own job setting.");
				}
				if (!num)
				{
					return;
				}
				if (ImGui.Selectable("No job change", !flag))
				{
					configuration.QuestRotationCombatJobByCharacter.Remove(character);
					configuration.Save();
				}
				if (!flag)
				{
					ImGui.SetItemDefaultFocus();
				}
				bool flag2 = flag && value == 0;
				if (ImGui.Selectable("Use highest combat job", flag2))
				{
					configuration.QuestRotationCombatJobByCharacter[character] = 0u;
					configuration.Save();
				}
				if (flag2)
				{
					ImGui.SetItemDefaultFocus();
				}
				ImGui.Separator();
				(uint, int, string)[] array2 = array;
				for (int num2 = 0; num2 < array2.Length; num2++)
				{
					(uint, int, string) tuple = array2[num2];
					bool flag3 = flag && value == tuple.Item1;
					ImU8String label = new ImU8String(7, 2);
					label.AppendFormatted(tuple.Item3);
					label.AppendLiteral(" - Lv. ");
					label.AppendFormatted(tuple.Item2);
					if (ImGui.Selectable(label, flag3))
					{
						configuration.QuestRotationCombatJobByCharacter[character] = tuple.Item1;
						configuration.Save();
					}
					if (flag3)
					{
						ImGui.SetItemDefaultFocus();
					}
				}
				ImGui.EndCombo();
				return;
			}
			bool v = flag && value == 0;
			if (ImGui.Checkbox("Use highest combat job", ref v))
			{
				if (v)
				{
					configuration.QuestRotationCombatJobByCharacter[character] = 0u;
				}
				else
				{
					configuration.QuestRotationCombatJobByCharacter.Remove(character);
				}
				configuration.Save();
			}
			ImGui.SetCursorScreenPos(cardPosition + new Vector2(10f, 136f));
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImGui.TextUnformatted("No XADB jobs; highest uses Hunt Logs");
			ImGui.PopStyleColor();
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("XA Database job data is unavailable for this character. If enabled, Hunt Logs' saved-gearset logic selects the highest combat job after login.");
			}
		}
	}

	private void DrawStopPointsTab()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Quest Rotation System");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		List<string> list = (from kvp in characterSelection
			where kvp.Value
			select kvp.Key).ToList();
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextWrapped("Rotates your selected Characters depending on the Stop Configurations you have enabled in Questionable. Please Configure a Quest and / or Level for a Rotation to be able to start.");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorPrimary);
		if (ImGui.Button("Import from Questionable"))
		{
			questRotationService.ImportStopPointsFromQuestionable();
			log.Information("[StopPoints] Imported stop points from Questionable");
		}
		ImGui.PopStyleColor(2);
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Pull stop quests and sequences from Questionable configuration");
		}
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Button, colorSecondary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.Text, colorDarkButtonText);
		if (ImGui.Button("Import into Questionable"))
		{
			StopPointImportResult stopPointImportResult = questRotationService.ImportCompanionStopPointsIntoQuestionable();
			stopPointTransferSucceeded = stopPointImportResult.Succeeded;
			if (stopPointImportResult.Succeeded)
			{
				stopPointTransferStatus = $"Imported {stopPointImportResult.Added} new and refreshed {stopPointImportResult.Updated} existing stop point(s)." + ((stopPointImportResult.SequencesImported > 0) ? $" Restored {stopPointImportResult.SequencesImported} sequence value(s)." : string.Empty) + " Questionable stop conditions are enabled.";
			}
			else if (!string.IsNullOrEmpty(stopPointImportResult.ErrorMessage))
			{
				stopPointTransferStatus = stopPointImportResult.ErrorMessage;
			}
			else
			{
				stopPointTransferStatus = $"Imported {stopPointImportResult.Added + stopPointImportResult.Updated} of {stopPointImportResult.Total} stop point(s); {stopPointImportResult.Failed} failed. Check the plugin log for the affected quest IDs.";
			}
			log.Information("[StopPoints] " + stopPointTransferStatus);
		}
		ImGui.PopStyleColor(3);
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Add the current Companion stop points to Questionable via IPC.\nImported points use Pause mode and retain their quest sequence.\nExisting Questionable stop points are not removed.");
		}
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Open Questionable Settings"))
		{
			Plugin.CommandManager.ProcessCommand("/qst config");
			log.Information("[StopPoints] Opened Questionable settings");
		}
		ImGui.PopStyleColor(2);
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Open Questionable plugin settings window");
		}
		ImGuiHelpers.ScaledDummy(6f);
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Copy Stop Points"))
		{
			var (text, num) = questRotationService.CreateStopPointClipboardPayload();
			if (num == 0)
			{
				stopPointTransferSucceeded = false;
				stopPointTransferStatus = "There are no Companion stop points to copy.";
			}
			else
			{
				ImGui.SetClipboardText(text);
				stopPointTransferSucceeded = true;
				stopPointTransferStatus = $"Copied {num} stop point(s) to the clipboard.";
			}
			log.Information("[StopPoints] " + stopPointTransferStatus);
		}
		ImGui.PopStyleColor(2);
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Copy quest IDs and sequence values from the current Companion list.\nCharacter progress and runtime state are not copied.");
		}
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Button, colorSecondary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.Text, colorDarkButtonText);
		if (ImGui.Button("Paste Stop Points"))
		{
			if (!questRotationService.TryPasteStopPointClipboardPayload(ImGui.GetClipboardText(), out string message))
			{
				stopPointTransferSucceeded = false;
				stopPointTransferStatus = message;
			}
			else
			{
				StopPointImportResult stopPointImportResult2 = questRotationService.ImportCompanionStopPointsIntoQuestionable();
				stopPointTransferSucceeded = stopPointImportResult2.Succeeded;
				if (stopPointImportResult2.Succeeded)
				{
					stopPointTransferStatus = message + " Imported the list into Questionable in Pause mode.";
				}
				else
				{
					string text2 = ((!string.IsNullOrWhiteSpace(stopPointImportResult2.ErrorMessage)) ? stopPointImportResult2.ErrorMessage : $"{stopPointImportResult2.Failed} Questionable import(s) failed.");
					stopPointTransferStatus = message + " The local copy was saved, but Questionable could not be updated: " + text2 + " Use 'Import into Questionable' to retry.";
				}
			}
			log.Information("[StopPoints] " + stopPointTransferStatus);
		}
		ImGui.PopStyleColor(3);
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Merge stop points copied from another Companion client and import them into Questionable.\nMatching quest IDs receive the copied sequence; other local points remain.\nIf Questionable is unavailable, the local copy is kept for a later retry.");
		}
		if (!string.IsNullOrEmpty(stopPointTransferStatus))
		{
			ImGuiHelpers.ScaledDummy(6f);
			ImGui.PushStyleColor(ImGuiCol.Text, stopPointTransferSucceeded ? colorSuccess : colorAccent);
			ImGui.TextWrapped(stopPointTransferStatus);
			ImGui.PopStyleColor();
		}
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Level Stop Condition:");
		ImGui.PopStyleColor();
		StopConditionData levelStopCondition = plugin.QuestionableIPC.GetLevelStopCondition();
		if (levelStopCondition != null && levelStopCondition.Enabled)
		{
			ImGui.SetWindowFontScale(1.2f);
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImU8String text3 = new ImU8String(15, 1);
			text3.AppendLiteral("Stop at Level: ");
			text3.AppendFormatted(levelStopCondition.TargetValue);
			ImGui.TextUnformatted(text3);
			ImGui.PopStyleColor();
			ImGui.SetWindowFontScale(1f);
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
			ImGui.TextUnformatted("Not configured");
			ImGui.PopStyleColor();
		}
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(10f);
		List<StopPoint> allStopPoints = questRotationService.GetAllStopPoints();
		ImU8String label = new ImU8String(39, 1);
		label.AppendLiteral("Active Stop Points (");
		label.AppendFormatted(allStopPoints.Count);
		label.AppendLiteral(")##ActiveStopPoints");
		if (ImGui.CollapsingHeader(label))
		{
			ImGuiHelpers.ScaledDummy(5f);
			if (allStopPoints.Count == 0)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
				ImGui.TextUnformatted("No stop points configured.");
				ImGui.PopStyleColor();
			}
			else
			{
				using ImRaii.ImTable imTable = ImRaii.Table("StopPointsTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg);
				if (imTable.Success)
				{
					ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
					ImGui.TableSetupColumn("Stop Point", ImGuiTableColumnFlags.WidthStretch, 2.2f);
					ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80f);
					ImGui.TableSetupColumn("Remaining", ImGuiTableColumnFlags.WidthFixed, 85f);
					ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthStretch, 1.3f);
					ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 38f);
					ImGui.TableHeadersRow();
					for (int num2 = 0; num2 < allStopPoints.Count; num2++)
					{
						StopPoint stopPoint = allStopPoints[num2];
						List<string> list2 = (from kvp in characterSelection
							where kvp.Value
							select kvp.Key).ToList();
						int num3;
						int num4;
						if (list2.Count > 0)
						{
							(num3, num4) = questRotationService.GetRotationProgress(stopPoint, list2);
						}
						else
						{
							(num3, num4) = questRotationService.GetRotationProgress(stopPoint);
						}
						int num5 = num4 - num3;
						ImGui.TableNextRow();
						ImGui.TableNextColumn();
						using (ImRaii.PushId($"stop-drag-{num2}"))
						{
							ImGui.BeginDisabled(questRotationService.IsRotationActive);
							ImGui.PushFont(UiBuilder.IconFont);
							ImGui.Button(FontAwesomeIcon.ArrowsUpDownLeftRight.ToIconString());
							ImGui.PopFont();
							ImGui.EndDisabled();
							if (ImGui.IsItemHovered() && !questRotationService.IsRotationActive)
							{
								ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
								ImGui.SetTooltip("Drag to reorder this stop point in Companion only.");
							}
							if (!questRotationService.IsRotationActive && ImGui.BeginDragDropSource())
							{
								draggedStopPointIndex = num2;
								ImGuiDragDrop.SetDragDropPayload("QSTCOMP_STOP_POINT_ORDER", num2);
								ImGui.TextUnformatted(stopPoint.DisplayName);
								ImGui.EndDragDropSource();
							}
							else if (draggedStopPointIndex == num2 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
							{
								draggedStopPointIndex = null;
							}
							if (!questRotationService.IsRotationActive && ImGui.BeginDragDropTarget())
							{
								if (ImGuiDragDrop.AcceptDragDropPayload<int>("QSTCOMP_STOP_POINT_ORDER", out var payload) && payload != num2 && questRotationService.MoveStopPoint(payload, num2))
								{
									draggedStopPointIndex = num2;
								}
								ImGui.EndDragDropTarget();
							}
						}
						ImGui.TableNextColumn();
						ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
						ImGui.TextUnformatted(stopPoint.DisplayName);
						ImGui.PopTextWrapPos();
						ImGui.TableNextColumn();
						if (stopPoint.IsActive)
						{
							ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
							ImGui.TextUnformatted("Active");
							ImGui.PopStyleColor();
						}
						else if (num3 == num4 && num4 > 0)
						{
							ImGui.PushStyleColor(ImGuiCol.Text, colorSuccess);
							ImGui.TextUnformatted("Completed");
							ImGui.PopStyleColor();
						}
						else
						{
							ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
							ImGui.TextUnformatted("Queued");
							ImGui.PopStyleColor();
						}
						ImGui.TableNextColumn();
						Vector4 col = (stopPoint.IsActive ? colorPrimary : ((num5 == 0) ? colorSuccess : colorSecondary));
						ImGui.PushStyleColor(ImGuiCol.Text, col);
						ImU8String text4 = new ImU8String(1, 2);
						text4.AppendFormatted(num3);
						text4.AppendLiteral("/");
						text4.AppendFormatted(num4);
						ImGui.TextUnformatted(text4);
						ImGui.PopStyleColor();
						ImGui.TableNextColumn();
						float num6 = ((num4 > 0) ? ((float)num3 / (float)num4) : 0f);
						ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
						Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
						float x = ImGui.GetContentRegionAvail().X;
						float y = 20f;
						uint col2 = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 0.8f));
						windowDrawList.AddRectFilled(cursorScreenPos, cursorScreenPos + new Vector2(x, y), col2, 4f);
						Vector4 vector = ((num6 >= 1f) ? colorSuccess : ((num6 >= 0.85f) ? colorPrimary : ((num6 >= 0.5f) ? colorSecondary : colorAccent)));
						uint col3 = ImGui.ColorConvertFloat4ToU32(new Vector4(vector.X, vector.Y, vector.Z, 0.9f));
						windowDrawList.AddRectFilled(cursorScreenPos, cursorScreenPos + new Vector2(x * num6, y), col3, 4f);
						string text5 = $"{(int)(num6 * 100f)}%";
						Vector2 vector2 = ImGui.CalcTextSize(text5);
						windowDrawList.AddText(cursorScreenPos + new Vector2(x / 2f - vector2.X / 2f, 2f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), text5);
						ImGui.Dummy(new Vector2(x, y));
						ImGui.TableNextColumn();
						using (ImRaii.PushId(num2))
						{
							ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
							ImGui.BeginDisabled(questRotationService.IsRotationActive);
							if (ImGui.Button("X"))
							{
								questRotationService.RemoveStopPoint(stopPoint.QuestId);
								log.Information($"[StopPoints] Removed stop quest {stopPoint.QuestId}");
							}
							ImGui.EndDisabled();
							if (ImGui.IsItemHovered())
							{
								ImGui.SetTooltip("Removes this stop point from the current Companion rotation only.\nThe Questionable configuration is not changed.");
							}
							ImGui.PopStyleColor();
						}
					}
				}
			}
		}
		ImGuiHelpers.ScaledDummy(15f);
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Current Status:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		RotationState currentState = questRotationService.GetCurrentState();
		ImU8String text6 = new ImU8String(7, 0);
		text6.AppendLiteral("Phase: ");
		ImGui.TextUnformatted(text6);
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Text, currentState.Phase switch
		{
			RotationPhase.Idle => colorSecondary, 
			RotationPhase.Questing => colorPrimary, 
			RotationPhase.QuestActive => colorPrimary, 
			RotationPhase.InCombat => new Vector4(1f, 0.5f, 0.2f, 1f), 
			RotationPhase.InDungeon => new Vector4(0.8f, 0.4f, 1f, 1f), 
			RotationPhase.HandlingSubmarines => new Vector4(0.2f, 0.8f, 1f, 1f), 
			RotationPhase.WaitingForChauffeur => new Vector4(1f, 1f, 0.4f, 1f), 
			RotationPhase.TravellingWithChauffeur => new Vector4(0.4f, 1f, 0.4f, 1f), 
			RotationPhase.DCTraveling => new Vector4(0.5f, 0.5f, 1f, 1f), 
			RotationPhase.Completed => colorPrimary, 
			RotationPhase.Error => colorAccent, 
			_ => colorSecondary, 
		});
		ImGui.TextUnformatted(currentState.Phase.ToString());
		ImGui.PopStyleColor();
		if (!string.IsNullOrEmpty(currentState.CurrentCharacter))
		{
			ImU8String text7 = new ImU8String(11, 0);
			text7.AppendLiteral("Logged In: ");
			ImGui.TextUnformatted(text7);
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
			ImGui.TextUnformatted(currentState.CurrentCharacter);
			ImGui.PopStyleColor();
		}
		if (currentState.CurrentStopQuestId != 0)
		{
			ImU8String text8 = new ImU8String(14, 0);
			text8.AppendLiteral("Target Quest: ");
			ImGui.TextUnformatted(text8);
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
			ImGui.TextUnformatted(currentState.CurrentStopQuestId.ToString());
			ImGui.PopStyleColor();
		}
		if (!string.IsNullOrEmpty(currentState.NextCharacter))
		{
			ImU8String text9 = new ImU8String(16, 0);
			text9.AppendLiteral("Next Character: ");
			ImGui.TextUnformatted(text9);
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted(currentState.NextCharacter);
			ImGui.PopStyleColor();
		}
		if (currentState.RemainingCharacters.Count > 0)
		{
			ImGui.TextUnformatted("Remaining:");
			ImGui.Indent();
			ImGui.TextWrapped((currentState.RemainingCharacters.Count > 0) ? string.Join(", ", currentState.RemainingCharacters) : "None");
			ImGui.Unindent();
		}
		if (currentState.SkippedCharacters.Count > 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0f, 1f));
			ImGui.TextUnformatted("Skipped:");
			ImGui.Indent();
			ImGui.TextWrapped(string.Join(", ", currentState.SkippedCharacters));
			ImGui.Unindent();
			ImGui.PopStyleColor();
		}
		if (currentState.CompletedCharacters.Count > 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 1f, 0f, 1f));
			ImGui.TextUnformatted("Completed:");
			ImGui.Indent();
			ImGui.TextWrapped(string.Join(", ", currentState.CompletedCharacters));
			ImGui.Unindent();
			ImGui.PopStyleColor();
		}
		if (currentState.Phase == RotationPhase.Error && !string.IsNullOrEmpty(currentState.ErrorMessage))
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImU8String text10 = new ImU8String(7, 1);
			text10.AppendLiteral("Error: ");
			text10.AppendFormatted(currentState.ErrorMessage);
			ImGui.TextWrapped(text10);
			ImGui.PopStyleColor();
		}
		if (currentState.SelectedCharacters.Count > 0)
		{
			ImGuiHelpers.ScaledDummy(5f);
			StopPoint currentStopPoint = questRotationService.GetCurrentStopPoint();
			if (currentStopPoint != null)
			{
				(int completed, int total) rotationProgress = questRotationService.GetRotationProgress(currentStopPoint, currentState.SelectedCharacters);
				int item = rotationProgress.completed;
				int item2 = rotationProgress.total;
				float fraction = ((item2 > 0) ? ((float)item / (float)item2) : 0f);
				string text11 = $"{item}/{item2} completed ({currentStopPoint.DisplayName})";
				Vector2 cursorScreenPos2 = ImGui.GetCursorScreenPos();
				Vector2 sizeArg = new Vector2(ImGui.GetContentRegionAvail().X, 0f);
				ImGui.ProgressBar(fraction, sizeArg, "");
				Vector2 vector3 = ImGui.CalcTextSize(text11);
				ImDrawListPtr windowDrawList2 = ImGui.GetWindowDrawList();
				Vector2 pos = new Vector2(cursorScreenPos2.X + sizeArg.X / 2f - vector3.X / 2f, cursorScreenPos2.Y + 2f);
				windowDrawList2.AddText(pos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), text11);
			}
			else
			{
				float fraction2 = (float)currentState.CompletedCharacters.Count / (float)currentState.SelectedCharacters.Count;
				string text12 = $"{currentState.CompletedCharacters.Count}/{currentState.SelectedCharacters.Count} completed";
				Vector2 cursorScreenPos3 = ImGui.GetCursorScreenPos();
				Vector2 sizeArg2 = new Vector2(ImGui.GetContentRegionAvail().X, 0f);
				ImGui.ProgressBar(fraction2, sizeArg2, "");
				Vector2 vector4 = ImGui.CalcTextSize(text12);
				ImDrawListPtr windowDrawList3 = ImGui.GetWindowDrawList();
				Vector2 pos2 = new Vector2(cursorScreenPos3.X + sizeArg2.X / 2f - vector4.X / 2f, cursorScreenPos3.Y + 2f);
				windowDrawList3.AddText(pos2, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), text12);
			}
		}
		if (currentState.Phase == RotationPhase.Completed)
		{
			ImGuiHelpers.ScaledDummy(10f);
			ImGui.PushStyleColor(ImGuiCol.Border, colorAccent);
			ImGui.BeginChild("FinalSummary", new Vector2(0f, 100f), border: true);
			ImGui.TextUnformatted("Rotation Completed Summary:");
			ImGui.Separator();
			ImU8String text13 = new ImU8String(18, 1);
			text13.AppendLiteral("Total Characters: ");
			text13.AppendFormatted(currentState.SelectedCharacters.Count);
			ImGui.Text(text13);
			ImU8String text14 = new ImU8String(12, 1);
			text14.AppendLiteral("Successful: ");
			text14.AppendFormatted(currentState.CompletedCharacters.Count);
			ImGui.Text(text14);
			if (currentState.SkippedCharacters.Count > 0)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0f, 1f));
				ImU8String text15 = new ImU8String(9, 1);
				text15.AppendLiteral("Skipped: ");
				text15.AppendFormatted(currentState.SkippedCharacters.Count);
				ImGui.Text(text15);
				ImGui.PopStyleColor();
			}
			ImGui.EndChild();
			ImGui.PopStyleColor();
		}
		ImGuiHelpers.ScaledDummy(10f);
		if (list.Count == 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("Select characters in the Characters tab to start rotation");
			ImGui.PopStyleColor();
		}
		else if (allStopPoints.Count == 0 && (levelStopCondition == null || !levelStopCondition.Enabled))
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("Configure a Quest or Level stop condition above to start rotation");
			ImGui.PopStyleColor();
		}
		else if (currentState.Phase == RotationPhase.Idle || currentState.Phase == RotationPhase.Completed || currentState.Phase == RotationPhase.Error)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
			if (ImGui.Button("Start Rotation", new Vector2(200f, 30f)))
			{
				log.Information("[StopPoints] Start Rotation button clicked!");
				log.Information($"[StopPoints] Selected characters: {list.Count}");
				log.Information($"[StopPoints] Stop points: {allStopPoints.Count}");
				if (allStopPoints.Count > 0)
				{
					foreach (StopPoint item3 in allStopPoints)
					{
						item3.IsActive = false;
					}
					bool flag = false;
					for (int num7 = 0; num7 < allStopPoints.Count; num7++)
					{
						StopPoint stopPoint2 = allStopPoints[num7];
						var (num8, num9) = questRotationService.GetRotationProgress(stopPoint2, list);
						if (num8 < num9)
						{
							log.Information("[StopPoints] Starting rotation with: " + stopPoint2.DisplayName);
							log.Information($"[StopPoints] Progress: {num8}/{num9} completed");
							log.Information($"[StopPoints] Total stop points in queue: {allStopPoints.Count - num7}");
							for (int num10 = num7; num10 < allStopPoints.Count; num10++)
							{
								allStopPoints[num10].IsActive = num10 == num7;
							}
							if (questRotationService.StartRotation(stopPoint2.QuestId, list))
							{
								log.Information("[StopPoints] Rotation started successfully!");
								flag = true;
							}
							else
							{
								log.Error("[StopPoints] Failed to start rotation");
							}
							break;
						}
						log.Information($"[StopPoints] Skipping {stopPoint2.DisplayName} - all characters completed ({num8}/{num9})");
					}
					if (!flag)
					{
						log.Warning("[StopPoints] All stop points already completed by all characters!");
					}
				}
				else if (levelStopCondition != null && levelStopCondition.Enabled)
				{
					log.Information($"[StopPoints] Starting level-only rotation (target level: {levelStopCondition.TargetValue})");
					if (questRotationService.StartRotationLevelOnly(list))
					{
						log.Information("[StopPoints] Level-only rotation started successfully!");
					}
					else
					{
						log.Error("[StopPoints] Failed to start level-only rotation");
					}
				}
				else
				{
					log.Warning("[StopPoints] No stop points or level condition configured!");
				}
			}
			ImGui.PopStyleColor(2);
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
			if (ImGui.Button("Stop Rotation", new Vector2(200f, 30f)))
			{
				questRotationService.AbortRotation();
				log.Information("[StopPoints] Stopped rotation");
			}
			ImGui.PopStyleColor();
		}
	}

	private void DrawMSQProgressionTab()
	{
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted("Main Scenario Quest Progression");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		List<string> filteredCharacters = GetFilteredCharacters();
		Configuration configuration = plugin.Configuration;
		if (filteredCharacters.Count == 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("No characters to display");
			ImGui.PopStyleColor();
			return;
		}
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Refresh Progress"))
		{
			XadbImportResult import = ImportXadbProgress(filteredCharacters);
			characterProgressCache.Clear();
			LogXadbRefreshSummary("MSQ progress refresh", import, filteredCharacters.Count);
		}
		ImGui.PopStyleColor(2);
		ImGui.SameLine();
		RotationState currentState = questRotationService.GetCurrentState();
		if (currentState.Phase == RotationPhase.Idle || currentState.Phase == RotationPhase.Completed || currentState.Phase == RotationPhase.Error || currentState.CurrentStopQuestId != 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorSecondary);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorPrimary);
			ImGui.PushStyleColor(ImGuiCol.Text, colorDarkButtonText);
			if (ImGui.Button("First Time Sync"))
			{
				List<string> list = filteredCharacters.Where((string c) => characterSelection.GetValueOrDefault(c, defaultValue: false)).ToList();
				List<string> list2 = ((list.Count > 0) ? list : filteredCharacters);
				log.Information($"[MSQProgression] First Time Sync requested (Selected: {list.Count}, Using: {list2.Count})");
				XadbImportResult xadbImportResult = ImportXadbProgress(list2);
				List<string> list3 = list2.Where(CharacterNeedsFirstTimeSync).ToList();
				log.Information($"[XADatabaseIPC] Sync summary: roster rows={xadbImportResult.ReadSummary.RosterRows}, quest rows={xadbImportResult.ReadSummary.QuestRows}, matched characters={xadbImportResult.ReadSummary.QuestMatchedCharacters}, requested imports={xadbImportResult.MatchedCharacters}/{list2.Count}, fallback count={list3.Count}, name fallback matches={xadbImportResult.ReadSummary.NameFallbackMatches}. {xadbImportResult.Status}");
				if (list3.Count == 0)
				{
					log.Information("[MSQProgression] First Time Sync completed from existing QST/XADB data; no relogs required");
					characterProgressCache.Clear();
				}
				else if (questRotationService.StartSyncRotation(list3, filterCharactersWithExistingQuestData: false))
				{
					log.Information($"[MSQProgression] Sync rotation started for {list3.Count} character(s) still missing data");
				}
				else
				{
					log.Information("[MSQProgression] No characters need sync or failed to start");
				}
			}
			ImGui.PopStyleColor(3);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Import available Level/MSQ data from XA Database, then relog only characters still missing data");
			}
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorPrimary);
			if (ImGui.Button("Stop Syncing"))
			{
				log.Information("[MSQProgression] Stop Syncing requested");
				questRotationService.AbortRotation();
				characterProgressCache.Clear();
			}
			ImGui.PopStyleColor(2);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Stop the sync rotation");
			}
		}
		ImGui.SameLine();
		ImGui.TextUnformatted("Display Mode:");
		ImGui.SameLine();
		int currentItem = (int)configuration.MSQDisplayMode;
		ImGui.SetNextItemWidth(200f);
		if (ImGui.Combo("##MSQDisplayMode", ref currentItem, "Current Expansion\0Overall Progress\0Expansion Breakdown\0"))
		{
			configuration.MSQDisplayMode = (MSQDisplayMode)currentItem;
			configuration.Save();
		}
		ImGuiHelpers.ScaledDummy(10f);
		switch (configuration.MSQDisplayMode)
		{
		case MSQDisplayMode.CurrentExpansion:
			DrawMSQCurrentExpansion(filteredCharacters);
			break;
		case MSQDisplayMode.Overall:
			DrawMSQOverall(filteredCharacters);
			break;
		case MSQDisplayMode.ExpansionBreakdown:
			DrawMSQExpansionBreakdown(filteredCharacters);
			break;
		}
	}

	private void DrawMSQCurrentExpansion(List<string> characters)
	{
		using ImRaii.ImTable imTable = ImRaii.Table("MSQCurrentExpTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY);
		if (!imTable.Success)
		{
			return;
		}
		ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 70f);
		ImGui.TableSetupColumn("Current Expansion", ImGuiTableColumnFlags.WidthFixed, 150f);
		ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 100f);
		ImGui.TableSetupColumn("Completion", ImGuiTableColumnFlags.WidthFixed, 120f);
		ImGui.TableSetupScrollFreeze(0, 1);
		ImGui.TableHeadersRow();
		foreach (string character in characters)
		{
			if (!characterProgressCache.TryGetValue(character, out CharacterProgressInfo value))
			{
				GetCharacterProgress(character);
				continue;
			}
			List<uint> completedQuestsByCharacter = questRotationService.GetCompletedQuestsByCharacter(character);
			MSQExpansionData.Expansion currentExpansion = MSQExpansionData.GetCurrentExpansion(completedQuestsByCharacter);
			ExpansionInfo expansionInfo = new ExpansionInfo
			{
				Name = MSQExpansionData.GetExpansionName(currentExpansion),
				ShortName = MSQExpansionData.GetExpansionShortName(currentExpansion),
				MinQuestId = 0u,
				MaxQuestId = 0u,
				ExpectedQuestCount = MSQExpansionData.GetExpectedQuestCount(currentExpansion)
			};
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			string[] array = character.Split('@');
			ImGui.TextUnformatted((array.Length != 0) ? array[0] : character);
			ImGui.TableNextColumn();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted((value.HighestCombatJobLevel > 0) ? value.HighestCombatJobLevel.ToString() : "-");
			ImGui.PopStyleColor();
			ImGui.TableNextColumn();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted(expansionInfo?.Name ?? "A Realm Reborn");
			ImGui.PopStyleColor();
			ImGui.TableNextColumn();
			if (expansionInfo != null)
			{
				(int completed, int total) valueOrDefault = msqProgressionService.GetExpansionProgressForCharacter(completedQuestsByCharacter).GetValueOrDefault(expansionInfo.ShortName, (0, 0));
				int item = valueOrDefault.completed;
				int item2 = valueOrDefault.total;
				ImU8String text = new ImU8String(1, 2);
				text.AppendFormatted(item);
				text.AppendLiteral("/");
				text.AppendFormatted(item2);
				ImGui.TextUnformatted(text);
			}
			else
			{
				ImGui.TextUnformatted("0/0");
			}
			ImGui.TableNextColumn();
			if (expansionInfo != null)
			{
				(int completed, int total) valueOrDefault2 = msqProgressionService.GetExpansionProgressForCharacter(completedQuestsByCharacter).GetValueOrDefault(expansionInfo.ShortName, (0, 0));
				int item3 = valueOrDefault2.completed;
				int item4 = valueOrDefault2.total;
				float num = ((item4 > 0) ? ((float)item3 / (float)item4) : 0f);
				Vector2 sizeArg = new Vector2(-1f, 0f);
				ImU8String overlay = new ImU8String(1, 1);
				overlay.AppendFormatted((int)(num * 100f));
				overlay.AppendLiteral("%");
				ImGui.ProgressBar(num, sizeArg, overlay);
			}
			else
			{
				ImGui.ProgressBar(0f, new Vector2(-1f, 0f), "0%");
			}
		}
	}

	private void DrawMSQOverall(List<string> characters)
	{
		using ImRaii.ImTable imTable = ImRaii.Table("MSQOverallTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY);
		if (!imTable.Success)
		{
			return;
		}
		ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 70f);
		ImGui.TableSetupColumn("MSQ Progress", ImGuiTableColumnFlags.WidthFixed, 120f);
		ImGui.TableSetupColumn("Current MSQ", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("Completion %", ImGuiTableColumnFlags.WidthFixed, 100f);
		ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 70f);
		ImGui.TableSetupScrollFreeze(0, 1);
		ImGui.TableHeadersRow();
		for (int i = 0; i < characters.Count; i++)
		{
			string text = characters[i];
			if (!characterProgressCache.TryGetValue(text, out CharacterProgressInfo value))
			{
				GetCharacterProgress(text);
				continue;
			}
			ImGui.TableNextRow();
			ImGui.TableNextColumn();
			string[] array = text.Split('@');
			ImGui.TextUnformatted((array.Length != 0) ? array[0] : text);
			ImGui.TableNextColumn();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted((value.HighestCombatJobLevel > 0) ? value.HighestCombatJobLevel.ToString() : "-");
			ImGui.PopStyleColor();
			ImGui.TableNextColumn();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted(value.HasMSQProgressData ? $"{value.CompletedMSQCount} / {value.TotalMSQCount}" : "-");
			ImGui.PopStyleColor();
			if (value.MSQProgressBasis == MsqProgressBasis.XadbMilestones && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Imported from XA Database. This is completed milestone progress, not a complete quest scan.");
			}
			ImGui.TableNextColumn();
			ImGui.TextUnformatted(value.HasCurrentMSQData ? value.LastCompletedMSQName : "—");
			if (value.UsesXadbSummary && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Imported from XA Database. Uses the active MSQ when available, otherwise the latest completed milestone.");
			}
			ImGui.TableNextColumn();
			if (value.HasMSQProgressData)
			{
				float mSQCompletionPercentage = value.MSQCompletionPercentage;
				float fraction = mSQCompletionPercentage / 100f;
				Vector2 sizeArg = new Vector2(-1f, 0f);
				ImU8String overlay = new ImU8String(1, 1);
				overlay.AppendFormatted(mSQCompletionPercentage, "F1");
				overlay.AppendLiteral("%");
				ImGui.ProgressBar(fraction, sizeArg, overlay);
				if (value.MSQProgressBasis == MsqProgressBasis.XadbMilestones && ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Imported from XA Database. This percentage is based on milestones, not all MSQ quests.");
				}
			}
			else
			{
				ImGui.TextDisabled("-");
			}
			ImGui.TableNextColumn();
			using (ImRaii.PushId(i))
			{
				ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
				ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(colorAccent.X * 1.2f, colorAccent.Y * 1.2f, colorAccent.Z * 1.2f, 1f));
				if (ImGui.Button("Reset"))
				{
					questRotationService.ClearCharacterQuestData(text);
					characterProgressCache.Remove(text);
					log.Information("[MSQProgression] Reset quest data for " + text);
				}
				ImGui.PopStyleColor(2);
				if (ImGui.IsItemHovered())
				{
					ImU8String tooltip = new ImU8String(85, 1);
					tooltip.AppendLiteral("Reset all quest completion data for ");
					tooltip.AppendFormatted(text);
					tooltip.AppendLiteral(".\nUse this if data was corrupted during rotation.");
					ImGui.SetTooltip(tooltip);
				}
			}
		}
	}

	private void DrawMSQExpansionBreakdown(List<string> characters)
	{
		List<ExpansionInfo> expansions = msqProgressionService.GetExpansions();
		foreach (string character in characters)
		{
			string[] array = character.Split('@');
			string obj = ((array.Length != 0) ? array[0] : character);
			string text = ((array.Length > 1) ? array[1] : "Unknown");
			string value = obj + " @ " + text;
			ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
			ImU8String label = new ImU8String(15, 2);
			label.AppendFormatted(value);
			label.AppendLiteral("##MSQBreakdown_");
			label.AppendFormatted(character);
			if (ImGui.CollapsingHeader(label))
			{
				ImGui.PopStyleColor();
				ImGui.Indent(15f);
				List<uint> completedQuestsByCharacter = questRotationService.GetCompletedQuestsByCharacter(character);
				int currentExpansion = (int)MSQExpansionData.GetCurrentExpansion(completedQuestsByCharacter);
				Dictionary<string, (int, int)> expansionProgressForCharacter = msqProgressionService.GetExpansionProgressForCharacter(completedQuestsByCharacter);
				int num = 0;
				int num2 = 0;
				foreach (ExpansionInfo exp in expansions)
				{
					var (num3, num4) = expansionProgressForCharacter.GetValueOrDefault(exp.ShortName, (0, 0));
					if ((int)MSQExpansionData.GetAllExpansions().FirstOrDefault((MSQExpansionData.Expansion e) => MSQExpansionData.GetExpansionShortName(e) == exp.ShortName) < currentExpansion)
					{
						num3 = num4;
					}
					num += num3;
					num2 += num4;
				}
				ImU8String text2 = new ImU8String(13, 0);
				text2.AppendLiteral("Overall MSQ: ");
				ImGui.TextUnformatted(text2);
				ImGui.SameLine();
				ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
				ImU8String text3 = new ImU8String(1, 2);
				text3.AppendFormatted(num);
				text3.AppendLiteral("/");
				text3.AppendFormatted(num2);
				ImGui.TextUnformatted(text3);
				ImGui.PopStyleColor();
				float num5 = ((num2 > 0) ? ((float)num / (float)num2) : 0f);
				Vector2 sizeArg = new Vector2(-1f, 0f);
				ImU8String overlay = new ImU8String(1, 1);
				overlay.AppendFormatted((int)(num5 * 100f));
				overlay.AppendLiteral("%");
				ImGui.ProgressBar(num5, sizeArg, overlay);
				ImGuiHelpers.ScaledDummy(10f);
				ImGui.TextUnformatted("Expansion Breakdown:");
				ImGuiHelpers.ScaledDummy(5f);
				foreach (ExpansionInfo exp2 in expansions)
				{
					var (num6, num7) = expansionProgressForCharacter.GetValueOrDefault(exp2.ShortName, (0, 0));
					if ((int)MSQExpansionData.GetAllExpansions().FirstOrDefault((MSQExpansionData.Expansion e) => MSQExpansionData.GetExpansionShortName(e) == exp2.ShortName) < currentExpansion)
					{
						num6 = num7;
					}
					float num8 = ((num7 > 0) ? ((float)num6 / (float)num7) : 0f);
					bool num9 = num6 == num7 && num7 > 0;
					ImU8String text4 = new ImU8String(6, 2);
					text4.AppendLiteral("  ");
					text4.AppendFormatted(exp2.Name);
					text4.AppendLiteral(" (");
					text4.AppendFormatted(exp2.ShortName);
					text4.AppendLiteral("):");
					ImGui.TextUnformatted(text4);
					ImGui.SameLine();
					if (num9)
					{
						ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
						ImU8String text5 = new ImU8String(10, 2);
						text5.AppendFormatted(num6);
						text5.AppendLiteral("/");
						text5.AppendFormatted(num7);
						text5.AppendLiteral(" Complete");
						ImGui.TextUnformatted(text5);
						ImGui.PopStyleColor();
					}
					else if (num6 == 0)
					{
						ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
						ImU8String text6 = new ImU8String(13, 2);
						text6.AppendFormatted(num6);
						text6.AppendLiteral("/");
						text6.AppendFormatted(num7);
						text6.AppendLiteral(" Not Started");
						ImGui.TextUnformatted(text6);
						ImGui.PopStyleColor();
					}
					else
					{
						ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
						ImU8String text7 = new ImU8String(1, 2);
						text7.AppendFormatted(num6);
						text7.AppendLiteral("/");
						text7.AppendFormatted(num7);
						ImGui.TextUnformatted(text7);
						ImGui.PopStyleColor();
					}
					ImGui.Indent(20f);
					Vector2 sizeArg2 = new Vector2(-1f, 0f);
					ImU8String overlay2 = new ImU8String(1, 1);
					overlay2.AppendFormatted((int)(num8 * 100f));
					overlay2.AppendLiteral("%");
					ImGui.ProgressBar(num8, sizeArg2, overlay2);
					ImGui.Unindent(20f);
				}
				ImGui.Unindent(15f);
			}
			else
			{
				ImGui.PopStyleColor();
			}
			ImGuiHelpers.ScaledDummy(5f);
		}
	}

	private void DrawSettingsTab()
	{
		DrawSettingsTabFull();
	}

	private List<string> GetWorldsForCurrentDatacenter()
	{
		string dataCenter = configuration.CharacterFilters.DataCenter;
		if (string.Equals(dataCenter, "All", StringComparison.OrdinalIgnoreCase))
		{
			return availableWorlds;
		}
		string dataCenterName = dataCenter;
		List<string> charactersForDataCenter = dataCenterService.GetCharactersForDataCenter(registeredCharacters, dataCenterName, charactersByDataCenter);
		HashSet<string> hashSet = new HashSet<string>();
		foreach (string item in charactersForDataCenter)
		{
			string[] array = item.Split('@');
			if (array.Length > 1)
			{
				hashSet.Add(array[1]);
			}
		}
		return hashSet.ToList();
	}

	private List<string> GetFilteredCharacters()
	{
		CharacterFilterConfiguration filters = configuration.CharacterFilters;
		List<string> list = ((!string.Equals(filters.DataCenter, "All", StringComparison.OrdinalIgnoreCase)) ? dataCenterService.GetCharactersForDataCenter(registeredCharacters, filters.DataCenter, charactersByDataCenter) : registeredCharacters);
		IEnumerable<string> source = list;
		if (!string.Equals(filters.World, "All", StringComparison.OrdinalIgnoreCase))
		{
			source = source.Where((string c) => c.EndsWith("@" + filters.World, StringComparison.OrdinalIgnoreCase));
		}
		if (filters.BelowGrandCompanyRank9)
		{
			source = source.Where((string c) => GetGrandCompanyRankForFilter(c) < 9);
		}
		if (filters.HasActiveLevelRange)
		{
			source = source.Where((string character) => CharacterFilterLogic.MatchesLevel(configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot value) ? CharacterFilterLogic.GetHighestKnownCombatJobLevel(value.CombatJobLevels, value.XadbObservedCombatJobLevels) : ((int?)null), filters));
		}
		if (filters.MissingRetainers)
		{
			Dictionary<string, CharacterRetainerSetupCheckpoint> checkpointsByCharacter = BuildRetainerCheckpointLookup();
			source = source.Where(delegate(string character)
			{
				XadbRetainerRosterStatus rosterStatus = XadbRetainerRosterStatus.Unknown;
				if (xadbRetainerSnapshots.TryGetValue(character, out XadbRetainerSnapshot value) && characterContentIds.TryGetValue(character, out var value2) && value2 != 0L && value.OwnerContentId == value2)
				{
					rosterStatus = value.Status;
				}
				checkpointsByCharacter.TryGetValue(character, out CharacterRetainerSetupCheckpoint value3);
				return CharacterFilterLogic.MatchesMissingRetainers(filterEnabled: true, rosterStatus, value3);
			});
		}
		if (filters.ClassJobId != 0 && filters.ClassUnlockStatus != ClassUnlockFilterStatus.All)
		{
			source = source.Where(delegate(string character)
			{
				configuration.CharacterJobLevels.TryGetValue(character, out CharacterJobLevelSnapshot value);
				return CharacterFilterLogic.MatchesClassUnlock(filters.ClassJobId, filters.ClassUnlockStatus, value);
			});
		}
		return source.ToList();
	}

	private Dictionary<string, CharacterRetainerSetupCheckpoint> BuildRetainerCheckpointLookup()
	{
		Dictionary<ulong, CharacterRetainerSetupCheckpoint> checkpoints = configuration.RetainerSetup.Checkpoints;
		Dictionary<string, CharacterRetainerSetupCheckpoint> dictionary = new Dictionary<string, CharacterRetainerSetupCheckpoint>(StringComparer.OrdinalIgnoreCase);
		foreach (CharacterRetainerSetupCheckpoint value2 in checkpoints.Values)
		{
			if (!string.IsNullOrWhiteSpace(value2.CharacterKey))
			{
				dictionary.TryAdd(value2.CharacterKey, value2);
			}
		}
		foreach (var (key, num2) in characterContentIds)
		{
			if (num2 != 0L && checkpoints.TryGetValue(num2, out var value))
			{
				dictionary[key] = value;
			}
		}
		return dictionary;
	}

	private void SaveCharacterSelection()
	{
		configuration.SelectedCharactersForUI = (from entry in characterSelection
			where entry.Value
			select entry.Key).ToList();
		configuration.Save();
	}

	private static string GetActiveLevelRangeLabel(CharacterFilterConfiguration filters)
	{
		if (!filters.HasActiveLevelRange)
		{
			return "Level: any";
		}
		if (filters.AboveLevelEnabled && filters.BelowLevelEnabled)
		{
			return $"Level: > {filters.AboveLevel} and < {filters.BelowLevel}";
		}
		if (!filters.AboveLevelEnabled)
		{
			return $"Level: < {filters.BelowLevel}";
		}
		return $"Level: > {filters.AboveLevel}";
	}

	private static string FormatClassUnlockFilterStatus(ClassUnlockFilterStatus status)
	{
		return status switch
		{
			ClassUnlockFilterStatus.All => "All", 
			ClassUnlockFilterStatus.Unlocked => "Unlocked", 
			ClassUnlockFilterStatus.NotUnlocked => "Not unlocked", 
			ClassUnlockFilterStatus.Unknown => "Unknown", 
			_ => status.ToString(), 
		};
	}

	private int GetGrandCompanyRankForFilter(string characterName)
	{
		if (characterGrandCompanyRankFilterCache.TryGetValue(characterName, out var value))
		{
			return value;
		}
		CharacterJobLevelSnapshot value2;
		int val = (configuration.CharacterJobLevels.TryGetValue(characterName, out value2) ? value2.GrandCompanyRank : 0);
		int val2 = (autoRetainerIpc.IsAvailable ? autoRetainerIpc.GetGrandCompanyRank(characterName) : 0);
		int val3 = Math.Max(val, val2);
		val3 = Math.Max(val3, 0);
		characterGrandCompanyRankFilterCache[characterName] = val3;
		return val3;
	}

	private static uint GetGrandCompanyRankIconId(uint grandCompanyId, int rank)
	{
		if (rank <= 0)
		{
			return 0u;
		}
		return grandCompanyId switch
		{
			1u => (uint)(83600 + rank), 
			2u => (uint)(83650 + rank), 
			3u => (uint)(83700 + rank), 
			_ => 0u, 
		};
	}

	private static uint GetGrandCompanyCrestIconId(uint grandCompanyId)
	{
		return grandCompanyId switch
		{
			1u => 60871u, 
			2u => 60872u, 
			3u => 60873u, 
			_ => 0u, 
		};
	}

	private static bool DrawGameIcon(uint iconId, float size)
	{
		if (iconId == 0)
		{
			return false;
		}
		ISharedImmediateTexture fromGameIcon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup
		{
			IconId = iconId
		});
		if (fromGameIcon == null || !fromGameIcon.TryGetWrap(out IDalamudTextureWrap texture, out Exception _))
		{
			return false;
		}
		ImGui.Image(texture.Handle, new Vector2(size, size));
		return true;
	}

	private CharacterProgressInfo GetCharacterProgress(string characterName)
	{
		if (characterProgressCache.TryGetValue(characterName, out CharacterProgressInfo value) && (DateTime.Now - value.LastUpdatedUtc).TotalSeconds < 300.0)
		{
			ApplyRetainerProgress(characterName, value);
			return value;
		}
		string[] array = characterName.Split('@');
		string world = ((array.Length > 1) ? array[1] : "Unknown");
		List<uint> completedQuestsByCharacter = questRotationService.GetCompletedQuestsByCharacter(characterName);
		uint num = 0u;
		string text = "â€”";
		if (completedQuestsByCharacter.Count > 0)
		{
			num = completedQuestsByCharacter.Max();
			text = msqProgressionService.GetQuestName(num);
			if (text == "Unknown Quest")
			{
				text = $"Quest {num}";
			}
		}
		List<uint> list = completedQuestsByCharacter.Where((uint q) => msqProgressionService.IsMSQ(q)).ToList();
		uint num2 = 0u;
		string text2 = "â€”";
		MSQExpansionData.Expansion currentExpansion = MSQExpansionData.GetCurrentExpansion(completedQuestsByCharacter);
		string expansionName = MSQExpansionData.GetExpansionName(currentExpansion);
		List<uint> list2 = list.Where((uint q) => MSQExpansionData.GetExpansionForQuest(q) == currentExpansion).ToList();
		if (list2.Count > 0)
		{
			num2 = list2.Max();
			text2 = msqProgressionService.GetQuestName(num2);
			if (text2 == "Unknown Quest")
			{
				text2 = $"Quest {num2}";
			}
			text2 = "[" + expansionName + "] " + text2;
		}
		else if (list.Count > 0)
		{
			num2 = list.Max();
			text2 = msqProgressionService.GetQuestName(num2);
			if (text2 == "Unknown Quest")
			{
				text2 = $"Quest {num2}";
			}
		}
		int num3 = msqProgressionService.GetTotalMSQCount();
		int num4 = list.Count;
		float mSQCompletionPercentage = ((num3 > 0) ? ((float)num4 / (float)num3 * 100f) : 0f);
		bool flag = list.Count > 0;
		bool flag2 = num2 != 0;
		bool flag3 = false;
		MsqProgressBasis mSQProgressBasis = (flag ? MsqProgressBasis.CompleteQuestScan : MsqProgressBasis.Unknown);
		if (configuration.XadbMsqProgressByCharacter.TryGetValue(characterName, out XadbMsqProgressSnapshot value2))
		{
			if (!flag && value2.HasMsqProgress && value2.TotalMsqCount > 0)
			{
				num4 = value2.CompletedMsqCount;
				num3 = value2.TotalMsqCount;
				mSQCompletionPercentage = (float)num4 / (float)num3 * 100f;
				flag = true;
				flag3 = true;
				mSQProgressBasis = value2.ProgressBasis;
			}
			if (!flag2 && value2.HasCurrentMsq)
			{
				num2 = value2.CurrentMsqId;
				text2 = value2.CurrentMsqName;
				flag2 = !string.IsNullOrWhiteSpace(text2);
				flag3 = flag3 || flag2;
			}
		}
		int num5 = 0;
		uint highestCombatJobId = 0u;
		uint num6 = 0u;
		int num7 = 0;
		if (configuration.CharacterJobLevels.TryGetValue(characterName, out CharacterJobLevelSnapshot value3))
		{
			CombatJobResolution combatJobResolution = CombatJobResolverLogic.MergeTrustedAndObservedLevels(value3.CombatJobLevels, value3.XadbObservedCombatJobLevels);
			num5 = combatJobResolution.HighestLevel;
			highestCombatJobId = combatJobResolution.HighestJobId;
			num6 = value3.GrandCompanyId;
			num7 = value3.GrandCompanyRank;
		}
		if ((num6 == 0 || num7 <= 0) && configuration.HuntLogs.CharacterSnapshots.TryGetValue(characterName, out HuntLogCharacterSnapshot value4))
		{
			if (num6 == 0)
			{
				num6 = value4.GrandCompanyId;
			}
			if (num7 <= 0)
			{
				num7 = value4.GrandCompanyRank;
			}
		}
		if (num5 <= 0)
		{
			(num5, highestCombatJobId) = autoRetainerIpc.GetHighestCombatJobLevelAndId(characterName);
		}
		if (num6 == 0 || num7 <= 0)
		{
			(uint, int) grandCompanyInfo = autoRetainerIpc.GetGrandCompanyInfo(characterName);
			if (grandCompanyInfo.Item1 != 0)
			{
				(num6, _) = grandCompanyInfo;
			}
			if (grandCompanyInfo.Item2 > 0)
			{
				num7 = grandCompanyInfo.Item2;
			}
		}
		CharacterProgressInfo characterProgressInfo = new CharacterProgressInfo
		{
			World = world,
			CompletedQuestCount = completedQuestsByCharacter.Count,
			LastQuestId = num,
			LastQuestName = text,
			LastCompletedMSQId = num2,
			LastCompletedMSQName = text2,
			CompletedMSQCount = num4,
			TotalMSQCount = num3,
			MSQCompletionPercentage = mSQCompletionPercentage,
			HasMSQProgressData = flag,
			HasCurrentMSQData = flag2,
			UsesXadbSummary = flag3,
			MSQProgressBasis = mSQProgressBasis,
			HighestCombatJobLevel = num5,
			HighestCombatJobId = highestCombatJobId,
			GrandCompanyId = num6,
			GrandCompanyRank = num7,
			LastUpdatedUtc = DateTime.UtcNow
		};
		ApplyRetainerProgress(characterName, characterProgressInfo);
		characterProgressCache[characterName] = characterProgressInfo;
		return characterProgressInfo;
	}

	private bool CharacterNeedsFirstTimeSync(string characterName)
	{
		characterProgressCache.Remove(characterName);
		CharacterProgressInfo characterProgress = GetCharacterProgress(characterName);
		if (characterProgress.HighestCombatJobLevel > 0 && characterProgress.HasMSQProgressData)
		{
			return !characterProgress.HasCurrentMSQData;
		}
		return true;
	}

	private XadbImportResult ImportXadbProgress(IReadOnlyCollection<string> characters)
	{
		if (!xadbIpc.TryGetCharacterProgress(out IReadOnlyDictionary<string, XadbCharacterProgress> characters2, out XadbProgressReadSummary summary, out string status))
		{
			foreach (string character in characters)
			{
				xadbRetainerSnapshots.Remove(character);
				characterContentIds.Remove(character);
				characterProgressCache.Remove(character);
			}
			return new XadbImportResult(summary, 0, status);
		}
		bool flag = false;
		int num = 0;
		foreach (string character2 in characters)
		{
			if (!characters2.TryGetValue(character2, out var value))
			{
				xadbRetainerSnapshots.Remove(character2);
				characterProgressCache.Remove(character2);
				continue;
			}
			xadbRetainerSnapshots[character2] = value.RetainerSnapshot;
			if (value.ContentId != 0L)
			{
				characterContentIds[character2] = value.ContentId;
			}
			bool flag2 = false;
			CharacterJobLevelSnapshot value2;
			CharacterJobLevelSnapshot targetLevels;
			DateTime dateTime;
			bool flag5;
			int num3;
			int num4;
			if (value.HasLevel)
			{
				configuration.CharacterJobLevels.TryGetValue(character2, out value2);
				targetLevels = value2 ?? new CharacterJobLevelSnapshot();
				CharacterJobLevelSnapshot characterJobLevelSnapshot = targetLevels;
				if (characterJobLevelSnapshot.CombatJobLevels == null)
				{
					Dictionary<uint, int> dictionary = (characterJobLevelSnapshot.CombatJobLevels = new Dictionary<uint, int>());
				}
				characterJobLevelSnapshot = targetLevels;
				if (characterJobLevelSnapshot.XadbObservedCombatJobLevels == null)
				{
					Dictionary<uint, int> dictionary = (characterJobLevelSnapshot.XadbObservedCombatJobLevels = new Dictionary<uint, int>());
				}
				bool flag3 = targetLevels.XadbObservedCombatJobLevels.Count != value.ObservedCombatJobLevels.Count || value.ObservedCombatJobLevels.Any((KeyValuePair<uint, int> entry) => !targetLevels.XadbObservedCombatJobLevels.TryGetValue(entry.Key, out var value5) || value5 != entry.Value);
				dateTime = ((!(value.SourceUpdatedUtc == DateTime.MinValue)) ? value.SourceUpdatedUtc : (flag3 ? DateTime.UtcNow : ((targetLevels.XadbObservedCombatJobLevelsUpdatedUtc == DateTime.MinValue) ? DateTime.UtcNow : targetLevels.XadbObservedCombatJobLevelsUpdatedUtc)));
				bool num2 = value2 != null && value.SourceUpdatedUtc != DateTime.MinValue && value.SourceUpdatedUtc < targetLevels.XadbObservedCombatJobLevelsUpdatedUtc;
				bool flag4 = targetLevels.XadbObservedCombatJobLevelsUpdatedUtc < dateTime;
				flag5 = !num2 && (value2 == null || flag3 || flag4);
				if (value.HighestCombatJobLevel > 0)
				{
					num3 = ((value.CombatJobLevels.Count > 0) ? 1 : 0);
					if (num3 != 0)
					{
						num4 = ((value2 == null || targetLevels.JobEvidenceVersion != 1 || targetLevels.HighestCombatJobLevel != value.HighestCombatJobLevel || targetLevels.HighestCombatJobId != value.HighestCombatJobId || targetLevels.InventoryEvidenceValid != value.InventoryEvidenceValid || !targetLevels.VerifiedSoulCrystalItemIds.OrderBy((uint itemId) => itemId).SequenceEqual(value.VerifiedSoulCrystalItemIds.OrderBy((uint itemId) => itemId)) || targetLevels.CombatJobLevels.Count != value.CombatJobLevels.Count || value.CombatJobLevels.Any((KeyValuePair<uint, int> entry) => !targetLevels.CombatJobLevels.TryGetValue(entry.Key, out var value5) || value5 != entry.Value)) ? 1 : 0);
						goto IL_0375;
					}
				}
				else
				{
					num3 = 0;
				}
				num4 = 0;
				goto IL_0375;
			}
			goto IL_0631;
			IL_0631:
			if (value.HasQuestSnapshotRow)
			{
				configuration.XadbMsqProgressByCharacter.TryGetValue(character2, out XadbMsqProgressSnapshot value3);
				bool flag6 = value3 == null || value3.HasMsqProgress != value.HasMsqProgress || value3.CompletedMsqCount != value.CompletedMsqCount || value3.TotalMsqCount != value.TotalMsqCount || value3.HasCurrentMsq != value.HasCurrentMsq || value3.CurrentMsqId != value.CurrentMsqId || !string.Equals(value3.CurrentMsqName, value.CurrentMsqName, StringComparison.Ordinal) || value3.ProgressBasis != MsqProgressBasis.XadbMilestones;
				DateTime dateTime2 = ((value.SourceUpdatedUtc != DateTime.MinValue) ? value.SourceUpdatedUtc : (flag6 ? DateTime.UtcNow : (value3?.SourceUpdatedUtc ?? DateTime.UtcNow)));
				bool num5 = value3 != null && value.SourceUpdatedUtc != DateTime.MinValue && value.SourceUpdatedUtc < value3.SourceUpdatedUtc;
				bool flag7 = value3 != null && value3.SourceUpdatedUtc < dateTime2;
				if (!num5 && (flag6 || flag7))
				{
					configuration.XadbMsqProgressByCharacter[character2] = new XadbMsqProgressSnapshot
					{
						CompletedMsqCount = value.CompletedMsqCount,
						TotalMsqCount = value.TotalMsqCount,
						CurrentMsqId = value.CurrentMsqId,
						CurrentMsqName = value.CurrentMsqName,
						HasMsqProgress = value.HasMsqProgress,
						HasCurrentMsq = value.HasCurrentMsq,
						ProgressBasis = MsqProgressBasis.XadbMilestones,
						SourceUpdatedUtc = dateTime2
					};
					flag = true;
				}
				flag2 = true;
			}
			if (flag2)
			{
				num++;
				characterProgressCache.Remove(character2);
			}
			continue;
			IL_0375:
			bool flag8 = (byte)num4 != 0;
			DateTime dateTime3 = ((!(value.SourceUpdatedUtc == DateTime.MinValue)) ? value.SourceUpdatedUtc : (flag8 ? DateTime.UtcNow : ((targetLevels.JobEvidenceUpdatedUtc == DateTime.MinValue) ? DateTime.UtcNow : targetLevels.JobEvidenceUpdatedUtc)));
			bool flag9 = value2 != null && ((value.SourceUpdatedUtc == DateTime.MinValue) ? (targetLevels.JobEvidenceUpdatedUtc != DateTime.MinValue) : (value.SourceUpdatedUtc < targetLevels.JobEvidenceUpdatedUtc));
			bool flag10 = targetLevels.JobEvidenceUpdatedUtc < dateTime3;
			int num6;
			if (num3 != 0 && !flag9)
			{
				num6 = ((value2 == null || flag8 || flag10) ? 1 : 0);
				if (num6 != 0)
				{
					targetLevels.JobEvidenceVersion = 1;
					targetLevels.HighestCombatJobLevel = value.HighestCombatJobLevel;
					targetLevels.HighestCombatJobId = value.HighestCombatJobId;
					targetLevels.CombatJobLevels = new Dictionary<uint, int>(value.CombatJobLevels);
					targetLevels.InventoryEvidenceValid = value.InventoryEvidenceValid;
					targetLevels.VerifiedSoulCrystalItemIds = value.VerifiedSoulCrystalItemIds.ToList();
					targetLevels.JobEvidenceSource = value.JobEvidenceSource;
					targetLevels.JobEvidenceUpdatedUtc = dateTime3;
				}
			}
			else
			{
				num6 = 0;
			}
			if (flag5)
			{
				targetLevels.XadbObservedCombatJobLevels = new Dictionary<uint, int>(value.ObservedCombatJobLevels);
				targetLevels.XadbObservedCombatJobLevelsUpdatedUtc = dateTime;
			}
			if (((uint)num6 | (flag5 ? 1u : 0u)) != 0)
			{
				targetLevels.LastUpdatedUtc = new DateTime[3] { targetLevels.LastUpdatedUtc, targetLevels.JobEvidenceUpdatedUtc, targetLevels.XadbObservedCombatJobLevelsUpdatedUtc }.Max();
				configuration.CharacterJobLevels[character2] = targetLevels;
				if (configuration.QuestRotationCombatJobByCharacter.TryGetValue(character2, out var value4) && value4 != 0 && !targetLevels.CombatJobLevels.ContainsKey(value4) && !targetLevels.XadbObservedCombatJobLevels.ContainsKey(value4))
				{
					configuration.QuestRotationCombatJobByCharacter.Remove(character2);
					log.Warning($"[XADatabaseIPC] Cleared uncorroborated job selection {value4} for {character2}.");
				}
				flag = true;
			}
			flag2 = true;
			goto IL_0631;
		}
		if (flag)
		{
			configuration.Save();
		}
		return new XadbImportResult(summary, num, status);
	}

	private void ApplyRetainerProgress(string character, CharacterProgressInfo progress)
	{
		XadbRetainerSnapshot xadbRetainerSnapshot = xadbRetainerSnapshots.GetValueOrDefault(character) ?? XadbRetainerSnapshot.Unknown("no current XADB retainer snapshot", 0uL);
		if (xadbRetainerSnapshot.Status != XadbRetainerRosterStatus.Unknown && (!characterContentIds.TryGetValue(character, out var value) || xadbRetainerSnapshot.OwnerContentId != value))
		{
			xadbRetainerSnapshot = XadbRetainerSnapshot.Unknown("XADB retainer owner did not match the selected character ContentId", xadbRetainerSnapshot.OwnerContentId);
		}
		progress.RetainerRosterStatus = xadbRetainerSnapshot.Status;
		progress.RetainerEvidenceValidated = xadbRetainerSnapshot.EvidenceValidated;
		progress.RetainerCount = xadbRetainerSnapshot.DeclaredCount;
		progress.HighestRetainerLevel = xadbRetainerSnapshot.HighestLevel;
		if (TryGetRetainerCheckpoint(character, out CharacterRetainerSetupCheckpoint checkpoint))
		{
			progress.RetainerSetupPercent = checkpoint.ProgressPercent;
			CharacterProgressInfo characterProgressInfo = progress;
			characterProgressInfo.RetainerSetupStatus = checkpoint.State switch
			{
				RetainerCheckpointState.Complete => "Complete", 
				RetainerCheckpointState.Failed => "Revalidation required", 
				RetainerCheckpointState.DeliberatelyStopped => "Stopped after " + FormatRetainerCheckpoint(checkpoint.LastVerifiedCheckpoint), 
				RetainerCheckpointState.Running => "Running: " + FormatRetainerCheckpoint(checkpoint.LastVerifiedCheckpoint), 
				_ => FormatRetainerCheckpoint(checkpoint.LastVerifiedCheckpoint), 
			};
		}
		else
		{
			progress.RetainerSetupPercent = null;
			CharacterProgressInfo characterProgressInfo = progress;
			characterProgressInfo.RetainerSetupStatus = xadbRetainerSnapshot.Status switch
			{
				XadbRetainerRosterStatus.ConfirmedZero => "Not started", 
				XadbRetainerRosterStatus.Populated => "Existing / untracked", 
				_ => "Unknown", 
			};
		}
	}

	private bool TryGetRetainerCheckpoint(string character, out CharacterRetainerSetupCheckpoint checkpoint)
	{
		if (characterContentIds.TryGetValue(character, out var value) && configuration.RetainerSetup.Checkpoints.TryGetValue(value, out checkpoint))
		{
			return true;
		}
		return (checkpoint = configuration.RetainerSetup.Checkpoints.Values.FirstOrDefault((CharacterRetainerSetupCheckpoint characterRetainerSetupCheckpoint) => string.Equals(characterRetainerSetupCheckpoint.CharacterKey, character, StringComparison.OrdinalIgnoreCase))) != null;
	}

	private static string FormatRetainerCheckpoint(RetainerStopAfter checkpoint)
	{
		return checkpoint switch
		{
			RetainerStopAfter.ArrivedAtVocate => "Arrived at Vocate", 
			RetainerStopAfter.RetainersHired => "Retainers hired", 
			RetainerStopAfter.VenturesUnlocked => "Ventures unlocked", 
			RetainerStopAfter.StarterGearReady => "Starter gear ready", 
			RetainerStopAfter.ClassAndGearAssigned => "Class and gear assigned", 
			RetainerStopAfter.AutoRetainerBootstrapped => "AutoRetainer bootstrapped", 
			_ => checkpoint.ToString(), 
		};
	}

	private void LogXadbRefreshSummary(string operation, XadbImportResult import, int requestedCharacters)
	{
		log.Information($"[XADatabaseIPC] {operation}: roster rows={import.ReadSummary.RosterRows}, quest rows={import.ReadSummary.QuestRows}, matched characters={import.ReadSummary.QuestMatchedCharacters}, requested imports={import.MatchedCharacters}/{requestedCharacters}, name fallback matches={import.ReadSummary.NameFallbackMatches}, retainer known/unknown={import.ReadSummary.RetainerKnownCharacters}/{import.ReadSummary.RetainerUnknownCharacters}. {import.Status}");
	}

	public void DrawWorldSelectionDialogs()
	{
		if (showSelectWorldDialog)
		{
			ImGui.OpenPopup("Select World##SelectWorldDialog");
		}
		if (ImGui.BeginPopupModal("Select World##SelectWorldDialog", ref showSelectWorldDialog, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.TextUnformatted("Select a world to check all characters:");
			ImGuiHelpers.ScaledDummy(10f);
			ImGui.SetNextItemWidth(200f);
			if (ImGui.BeginCombo("##WorldSelect", selectedWorldForBulkAction))
			{
				foreach (string availableWorld in availableWorlds)
				{
					bool flag = selectedWorldForBulkAction == availableWorld;
					if (ImGui.Selectable(availableWorld, flag))
					{
						selectedWorldForBulkAction = availableWorld;
					}
					if (flag)
					{
						ImGui.SetItemDefaultFocus();
					}
				}
				ImGui.EndCombo();
			}
			ImGuiHelpers.ScaledDummy(10f);
			if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
			{
				showSelectWorldDialog = false;
			}
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
			if (ImGui.Button("Select", new Vector2(100f, 0f)))
			{
				foreach (string registeredCharacter in registeredCharacters)
				{
					if (registeredCharacter.EndsWith("@" + selectedWorldForBulkAction))
					{
						characterSelection[registeredCharacter] = true;
					}
				}
				showSelectWorldDialog = false;
				log.Information("[NewMainWindow] Selected all characters from " + selectedWorldForBulkAction);
			}
			ImGui.PopStyleColor();
			ImGui.EndPopup();
		}
		if (showDeselectWorldDialog)
		{
			ImGui.OpenPopup("Deselect World##DeselectWorldDialog");
		}
		if (!ImGui.BeginPopupModal("Deselect World##DeselectWorldDialog", ref showDeselectWorldDialog, ImGuiWindowFlags.AlwaysAutoResize))
		{
			return;
		}
		ImGui.TextUnformatted("Select a world to uncheck all characters:");
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.SetNextItemWidth(200f);
		if (ImGui.BeginCombo("##WorldDeselect", selectedWorldForBulkAction))
		{
			foreach (string availableWorld2 in availableWorlds)
			{
				bool flag2 = selectedWorldForBulkAction == availableWorld2;
				if (ImGui.Selectable(availableWorld2, flag2))
				{
					selectedWorldForBulkAction = availableWorld2;
				}
				if (flag2)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGuiHelpers.ScaledDummy(10f);
		if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
		{
			showDeselectWorldDialog = false;
		}
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
		if (ImGui.Button("Deselect", new Vector2(100f, 0f)))
		{
			foreach (string registeredCharacter2 in registeredCharacters)
			{
				if (registeredCharacter2.EndsWith("@" + selectedWorldForBulkAction))
				{
					characterSelection[registeredCharacter2] = false;
				}
			}
			showDeselectWorldDialog = false;
			log.Information("[NewMainWindow] Deselected all characters from " + selectedWorldForBulkAction);
		}
		ImGui.PopStyleColor();
		ImGui.EndPopup();
	}

	private void DrawEventQuestTab()
	{
		Vector4 vector = new Vector4(0.949f, 0.769f, 0.388f, 1f);
		Vector4 col = new Vector4(1f, 0.6f, 0.2f, 1f);
		ImGui.PushStyleColor(ImGuiCol.Text, vector);
		ImGui.TextUnformatted("Event Quest System");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		List<string> list = (from kvp in characterSelection
			where kvp.Value
			select kvp.Key).ToList();
		if ((DateTime.Now - lastEventQuestRefresh).TotalSeconds > 5.0 || availableEventQuests.Count == 0)
		{
			List<string> currentlyActiveEventQuests = plugin.QuestionableIPC.GetCurrentlyActiveEventQuests();
			EventQuestResolver eventQuestResolver = new EventQuestResolver(dataManager, log);
			availableEventQuests.Clear();
			foreach (string item3 in currentlyActiveEventQuests)
			{
				log.Information($"[EventQuest] Questionable returned quest ID: '{item3}' (Length: {item3.Length})");
				string questName = eventQuestResolver.GetQuestName(item3);
				availableEventQuests.Add((item3, questName));
			}
			lastEventQuestRefresh = DateTime.Now;
			log.Debug($"[EventQuest] Loaded {availableEventQuests.Count} active event quests from Questionable");
		}
		ImGui.TextUnformatted("Active Event Quests:");
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Select an active Event Quest. Prerequisites will be automatically resolved.");
		ImU8String text = new ImU8String(54, 1);
		text.AppendLiteral("Currently ");
		text.AppendFormatted(availableEventQuests.Count);
		text.AppendLiteral(" event quest(s) available from Questionable.");
		ImGui.TextUnformatted(text);
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		ImGui.TextUnformatted("Event Quest:");
		ImGui.SameLine();
		ImGui.SetNextItemWidth(400f);
		string text2 = (string.IsNullOrEmpty(selectedEventQuestId) ? "Select Event Quest..." : (availableEventQuests.FirstOrDefault<(string, string)>(((string QuestId, string QuestName) q) => q.QuestId == selectedEventQuestId).Item2 ?? selectedEventQuestId));
		if (ImGui.BeginCombo("##EventQuestCombo", text2))
		{
			foreach (var availableEventQuest in availableEventQuests)
			{
				string item = availableEventQuest.QuestId;
				string item2 = availableEventQuest.QuestName;
				bool flag = selectedEventQuestId == item;
				ImU8String label = new ImU8String(3, 2);
				label.AppendFormatted(item2);
				label.AppendLiteral(" (");
				label.AppendFormatted(item);
				label.AppendLiteral(")");
				if (ImGui.Selectable(label, flag))
				{
					selectedEventQuestId = item;
					EventQuestResolver eventQuestResolver2 = new EventQuestResolver(dataManager, log);
					resolvedPrerequisites = eventQuestResolver2.ResolveEventQuestDependencies(item);
					log.Information($"[EventQuest] Selected quest {item}, resolved {resolvedPrerequisites.Count} prerequisites");
				}
				if (flag)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
			ImGui.EndCombo();
		}
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Button, vector);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, col);
		ImGui.PushStyleColor(ImGuiCol.Text, colorDarkButtonText);
		if (ImGui.Button("Refresh from Questionable"))
		{
			List<string> currentlyActiveEventQuests2 = plugin.QuestionableIPC.GetCurrentlyActiveEventQuests();
			EventQuestResolver eventQuestResolver3 = new EventQuestResolver(dataManager, log);
			availableEventQuests.Clear();
			foreach (string item4 in currentlyActiveEventQuests2)
			{
				string questName2 = eventQuestResolver3.GetQuestName(item4);
				availableEventQuests.Add((item4, questName2));
			}
			lastEventQuestRefresh = DateTime.Now;
			log.Information($"[EventQuest] Refreshed event quest list from Questionable: {availableEventQuests.Count} quests found");
		}
		ImGui.PopStyleColor(3);
		ImGuiHelpers.ScaledDummy(10f);
		if (!string.IsNullOrEmpty(selectedEventQuestId))
		{
			string value = availableEventQuests.FirstOrDefault<(string, string)>(((string QuestId, string QuestName) q) => q.QuestId == selectedEventQuestId).Item2 ?? "Unknown";
			ImGui.PushStyleColor(ImGuiCol.Text, vector);
			ImU8String text3 = new ImU8String(16, 1);
			text3.AppendLiteral("Selected Quest: ");
			text3.AppendFormatted(value);
			ImGui.TextUnformatted(text3);
			ImGui.PopStyleColor();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImU8String text4 = new ImU8String(10, 1);
			text4.AppendLiteral("Quest ID: ");
			text4.AppendFormatted(selectedEventQuestId);
			ImGui.TextUnformatted(text4);
			ImGui.PopStyleColor();
			ImGuiHelpers.ScaledDummy(5f);
		}
		if (!string.IsNullOrEmpty(selectedEventQuestId) && resolvedPrerequisites.Count > 0)
		{
			ImGui.Separator();
			ImGuiHelpers.ScaledDummy(5f);
			ImGui.PushStyleColor(ImGuiCol.Text, col);
			ImU8String text5 = new ImU8String(17, 1);
			text5.AppendLiteral("Prerequisites (");
			text5.AppendFormatted(resolvedPrerequisites.Count);
			text5.AppendLiteral("):");
			ImGui.TextUnformatted(text5);
			ImGui.PopStyleColor();
			ImGuiHelpers.ScaledDummy(3f);
			EventQuestResolver eventQuestResolver4 = new EventQuestResolver(dataManager, log);
			using (ImRaii.ImTable imTable = ImRaii.Table("PrerequisitesTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
			{
				if (imTable.Success)
				{
					ImGui.TableSetupColumn("Quest Name", ImGuiTableColumnFlags.WidthStretch);
					ImGui.TableSetupColumn("Quest ID", ImGuiTableColumnFlags.WidthFixed, 80f);
					ImGui.TableHeadersRow();
					foreach (string resolvedPrerequisite in resolvedPrerequisites)
					{
						ImGui.TableNextRow();
						ImGui.TableNextColumn();
						ImGui.TextUnformatted(eventQuestResolver4.GetQuestName(resolvedPrerequisite));
						ImGui.TableNextColumn();
						ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
						ImGui.TextUnformatted(resolvedPrerequisite);
						ImGui.PopStyleColor();
					}
				}
			}
			ImGuiHelpers.ScaledDummy(5f);
		}
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.PushStyleColor(ImGuiCol.Text, vector);
		ImGui.TextUnformatted("Current Status:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		EventQuestState currentState = eventQuestService.GetCurrentState();
		ImU8String text6 = new ImU8String(7, 0);
		text6.AppendLiteral("Phase: ");
		ImGui.TextUnformatted(text6);
		ImGui.SameLine();
		ImGui.PushStyleColor(ImGuiCol.Text, currentState.Phase switch
		{
			EventQuestPhase.Idle => colorSecondary, 
			EventQuestPhase.QuestActive => vector, 
			EventQuestPhase.Completed => colorPrimary, 
			EventQuestPhase.Error => colorAccent, 
			_ => colorSecondary, 
		});
		ImGui.TextUnformatted(currentState.Phase.ToString());
		ImGui.PopStyleColor();
		if (!string.IsNullOrEmpty(currentState.CurrentCharacter))
		{
			ImU8String text7 = new ImU8String(19, 0);
			text7.AppendLiteral("Current Character: ");
			ImGui.TextUnformatted(text7);
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, vector);
			ImGui.TextUnformatted(currentState.CurrentCharacter);
			ImGui.PopStyleColor();
		}
		if (!string.IsNullOrEmpty(currentState.EventQuestName))
		{
			ImU8String text8 = new ImU8String(13, 0);
			text8.AppendLiteral("Event Quest: ");
			ImGui.TextUnformatted(text8);
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, vector);
			ImU8String text9 = new ImU8String(3, 2);
			text9.AppendFormatted(currentState.EventQuestName);
			text9.AppendLiteral(" (");
			text9.AppendFormatted(currentState.EventQuestId);
			text9.AppendLiteral(")");
			ImGui.TextUnformatted(text9);
			ImGui.PopStyleColor();
		}
		if (currentState.DependencyQuests.Count > 0)
		{
			ImU8String text10 = new ImU8String(15, 2);
			text10.AppendLiteral("Dependencies: ");
			text10.AppendFormatted(currentState.DependencyIndex + 1);
			text10.AppendLiteral("/");
			text10.AppendFormatted(currentState.DependencyQuests.Count);
			ImGui.TextUnformatted(text10);
		}
		if (!string.IsNullOrEmpty(currentState.NextCharacter))
		{
			ImU8String text11 = new ImU8String(16, 0);
			text11.AppendLiteral("Next Character: ");
			ImGui.TextUnformatted(text11);
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted(currentState.NextCharacter);
			ImGui.PopStyleColor();
		}
		if (currentState.Phase == EventQuestPhase.Error && !string.IsNullOrEmpty(currentState.ErrorMessage))
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
			ImU8String text12 = new ImU8String(7, 1);
			text12.AppendLiteral("Error: ");
			text12.AppendFormatted(currentState.ErrorMessage);
			ImGui.TextUnformatted(text12);
			ImGui.PopStyleColor();
		}
		if (currentState.SelectedCharacters.Count > 0)
		{
			ImGuiHelpers.ScaledDummy(5f);
			float num = (float)currentState.CompletedCharacters.Count / (float)currentState.SelectedCharacters.Count;
			ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
			Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
			float x = ImGui.GetContentRegionAvail().X;
			float y = 25f;
			uint col2 = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 0.8f));
			windowDrawList.AddRectFilled(cursorScreenPos, cursorScreenPos + new Vector2(x, y), col2, 4f);
			uint col3 = ImGui.ColorConvertFloat4ToU32(new Vector4(vector.X, vector.Y, vector.Z, 0.9f));
			windowDrawList.AddRectFilled(cursorScreenPos, cursorScreenPos + new Vector2(x * num, y), col3, 4f);
			string text13 = $"{currentState.CompletedCharacters.Count}/{currentState.SelectedCharacters.Count} completed";
			Vector2 vector2 = ImGui.CalcTextSize(text13);
			windowDrawList.AddText(cursorScreenPos + new Vector2(x / 2f - vector2.X / 2f, 4f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), text13);
			ImGui.Dummy(new Vector2(x, y));
		}
		ImGuiHelpers.ScaledDummy(10f);
		if (currentState.SelectedCharacters.Count > 0)
		{
			ImGui.Separator();
			ImGuiHelpers.ScaledDummy(5f);
			ImGui.PushStyleColor(ImGuiCol.Button, (eventQuestViewMode == 0) ? vector : colorDarkBg);
			ImU8String label2 = new ImU8String(12, 1);
			label2.AppendLiteral("Remaining (");
			label2.AppendFormatted(currentState.RemainingCharacters.Count);
			label2.AppendLiteral(")");
			if (ImGui.Button(label2))
			{
				eventQuestViewMode = 0;
			}
			ImGui.PopStyleColor();
			ImGui.SameLine();
			ImGui.PushStyleColor(ImGuiCol.Button, (eventQuestViewMode == 1) ? vector : colorDarkBg);
			ImU8String label3 = new ImU8String(12, 1);
			label3.AppendLiteral("Completed (");
			label3.AppendFormatted(currentState.CompletedCharacters.Count);
			label3.AppendLiteral(")");
			if (ImGui.Button(label3))
			{
				eventQuestViewMode = 1;
			}
			ImGui.PopStyleColor();
			ImGuiHelpers.ScaledDummy(5f);
			List<string> list2 = ((eventQuestViewMode == 0) ? currentState.RemainingCharacters : currentState.CompletedCharacters);
			if (list2.Count > 0)
			{
				using ImRaii.ImChild imChild = ImRaii.Child("CharacterList", new Vector2(0f, 150f), border: true);
				if (imChild.Success)
				{
					foreach (string item5 in list2)
					{
						string[] array = item5.Split('@');
						string value2 = ((array.Length != 0) ? array[0] : item5);
						string value3 = ((array.Length > 1) ? array[1] : "Unknown");
						ImGui.PushStyleColor(ImGuiCol.Text, (eventQuestViewMode == 0) ? colorSecondary : vector);
						ImU8String text14 = new ImU8String(4, 1);
						text14.AppendLiteral("â€¢ ");
						text14.AppendFormatted(value2);
						ImGui.TextUnformatted(text14);
						ImGui.PopStyleColor();
						ImGui.SameLine();
						ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
						ImU8String text15 = new ImU8String(2, 1);
						text15.AppendLiteral("@ ");
						text15.AppendFormatted(value3);
						ImGui.TextUnformatted(text15);
						ImGui.PopStyleColor();
					}
				}
			}
			else
			{
				ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
				ImGui.TextUnformatted((eventQuestViewMode == 0) ? "No remaining characters" : "No completed characters");
				ImGui.PopStyleColor();
			}
			ImGuiHelpers.ScaledDummy(10f);
		}
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(10f);
		if (list.Count == 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("Select characters in the Characters tab to start Event Quest rotation");
			ImGui.PopStyleColor();
		}
		else if (string.IsNullOrEmpty(selectedEventQuestId))
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("Select an Event Quest from the dropdown above to start");
			ImGui.PopStyleColor();
		}
		else if (!eventQuestService.IsRotationActive)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, vector);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, col);
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, col);
			ImGui.PushStyleColor(ImGuiCol.Text, colorDarkButtonText);
			if (ImGui.Button("â–¶ Start Event Quest Rotation", new Vector2(250f, 35f)))
			{
				log.Information("[EventQuest] Start button clicked!");
				log.Information("[EventQuest] Event Quest ID: " + selectedEventQuestId);
				log.Information($"[EventQuest] Selected characters: {list.Count}");
				log.Information($"[EventQuest] Prerequisites: {resolvedPrerequisites.Count}");
				if (eventQuestService.StartEventQuestRotation(selectedEventQuestId, list))
				{
					log.Information("[EventQuest] Rotation started successfully!");
				}
				else
				{
					log.Error("[EventQuest] Failed to start rotation");
				}
			}
			ImGui.PopStyleColor(4);
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorAccent);
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, colorAccent);
			if (ImGui.Button("â\u008f¹ Abort Rotation", new Vector2(200f, 30f)))
			{
				eventQuestService.AbortRotation();
				log.Information("[EventQuest] Rotation aborted");
			}
			ImGui.PopStyleColor(3);
		}
		ImGuiHelpers.ScaledDummy(5f);
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		if (ImGui.Button("Refresh Characters"))
		{
			RefreshCharacterList();
			log.Information("[EventQuest] Character list refreshed");
		}
		ImGui.PopStyleColor(2);
		ImGuiHelpers.ScaledDummy(15f);
		ImGui.Separator();
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.PushStyleColor(ImGuiCol.Text, vector);
		ImGui.TextUnformatted("Completion Data:");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(5f);
		Dictionary<string, List<string>> eventQuestCompletionByCharacter = plugin.Configuration.EventQuestCompletionByCharacter;
		if (eventQuestCompletionByCharacter.Count == 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
			ImGui.TextUnformatted("No completion data recorded yet.");
			ImGui.PopStyleColor();
		}
		else
		{
			using ImRaii.ImTable imTable2 = ImRaii.Table("CompletionDataTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0f, 150f));
			if (imTable2.Success)
			{
				ImGui.TableSetupColumn("Event Quest ID", ImGuiTableColumnFlags.WidthFixed, 120f);
				ImGui.TableSetupColumn("Completed", ImGuiTableColumnFlags.WidthFixed, 100f);
				ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableSetupScrollFreeze(0, 1);
				ImGui.TableHeadersRow();
				EventQuestResolver eventQuestResolver5 = new EventQuestResolver(dataManager, log);
				foreach (KeyValuePair<string, List<string>> item6 in eventQuestCompletionByCharacter)
				{
					string key = item6.Key;
					List<string> value4 = item6.Value;
					ImGui.TableNextRow();
					ImGui.TableNextColumn();
					ImGui.TextUnformatted(eventQuestResolver5.GetQuestName(key));
					ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
					ImU8String text16 = new ImU8String(4, 1);
					text16.AppendLiteral("ID: ");
					text16.AppendFormatted(key);
					ImGui.TextUnformatted(text16);
					ImGui.PopStyleColor();
					ImGui.TableNextColumn();
					ImGui.PushStyleColor(ImGuiCol.Text, vector);
					ImU8String text17 = new ImU8String(0, 1);
					text17.AppendFormatted(value4.Count);
					ImGui.TextUnformatted(text17);
					ImGui.PopStyleColor();
					ImGui.TableNextColumn();
					using (ImRaii.PushId(key))
					{
						ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
						if (ImGui.Button("Clear"))
						{
							eventQuestCompletionByCharacter.Remove(key);
							plugin.Configuration.Save();
							log.Information("[EventQuest] Cleared completion data for quest " + key);
						}
						ImGui.PopStyleColor();
					}
				}
			}
		}
		ImGuiHelpers.ScaledDummy(10f);
		if (eventQuestCompletionByCharacter.Count > 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, colorAccent);
			if (ImGui.Button("ðŸ—‘ Clear All Completion Data"))
			{
				eventQuestCompletionByCharacter.Clear();
				plugin.Configuration.Save();
				log.Information("[EventQuest] Cleared all completion data");
			}
			ImGui.PopStyleColor();
		}
	}

	private void DrawWarningTab()
	{
		TryWarningMenuQuestionableCheck();
		ImGuiHelpers.ScaledDummy(50f);
		float x = ImGui.GetContentRegionAvail().X;
		string text = (string.IsNullOrWhiteSpace(plugin.QuestionableIPC.CompatibilityMessage) ? "WigglyMuffin's version of Questionable is required." : plugin.QuestionableIPC.CompatibilityMessage);
		ImGui.SetWindowFontScale(2f);
		ImGui.SetCursorPosX(MathF.Max(0f, (x - ImGui.CalcTextSize("Compatible Questionable unavailable.").X) * 0.5f));
		ImGui.PushStyleColor(ImGuiCol.Text, colorAccent);
		ImGui.TextUnformatted("Compatible Questionable unavailable.");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(10f);
		ImGui.SetWindowFontScale(1.25f);
		ImGui.SetCursorPosX(40f);
		ImGui.PushTextWrapPos(x - 40f);
		ImGui.TextColored(in colorAccent, text);
		ImGui.PopTextWrapPos();
		ImGuiHelpers.ScaledDummy(30f);
		ImGui.SetWindowFontScale(1f);
		double num = ((warningMenuRetryStartTime == DateTime.MinValue) ? 0.0 : (DateTime.Now - warningMenuRetryStartTime).TotalSeconds);
		int num2 = Math.Min(warningMenuRetryAttempts, warningMenuRetryDelaysSeconds.Length - 1);
		int num3 = Math.Max(0, warningMenuRetryDelaysSeconds[num2] - (int)num);
		string obj = ((num3 > 0) ? $"Checking again in {num3}s (attempt {warningMenuRetryAttempts + 1})" : $"Checking Questionable source and IPC... (attempt {warningMenuRetryAttempts + 1})");
		ImGui.SetCursorPosX((x - ImGui.CalcTextSize(obj).X) * 0.5f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorPrimary);
		ImGui.TextUnformatted(obj);
		ImGui.PopStyleColor();
		ImGui.SetCursorPosX((x - ImGui.CalcTextSize("Retrying automatically while this window is open.").X) * 0.5f);
		ImGui.PushStyleColor(ImGuiCol.Text, colorSecondary);
		ImGui.TextUnformatted("Retrying automatically while this window is open.");
		ImGui.PopStyleColor();
		ImGuiHelpers.ScaledDummy(15f);
		float num4 = 120f;
		float y = 40f;
		ImGui.SetCursorPosX((x - num4) * 0.5f);
		ImGui.PushStyleColor(ImGuiCol.Button, colorPrimary);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colorSecondary);
		ImGui.PushStyleColor(ImGuiCol.ButtonActive, colorSecondary);
		if (ImGui.Button("Refresh", new Vector2(num4, y)))
		{
			if (plugin.QuestionableIPC.TryEnsureAvailableSilent() && plugin.QuestionableIPC.ValidateFeatureCompatibility())
			{
				selectedTab = 5;
			}
			else
			{
				warningMenuRetryStartTime = DateTime.Now;
				warningMenuRetryCycleComplete = false;
			}
		}
		ImGui.PopStyleColor(3);
	}

	static NewMainWindow()
	{
		Version version = typeof(NewMainWindow).Assembly.GetName().Version;
		DisplayVersion = (((object)version != null) ? $"{version.Major}.{version.Minor}.{version.Build}" : "unknown");
		NormalMinWindowSize = new Vector2(900f, 600f);
		MinimizedWindowSize = new Vector2(320f, 35f);
		DependencyEntries = new global::_003C_003Ez__ReadOnlyArray<DependencyEntry>(new DependencyEntry[14]
		{
			new DependencyEntry("Questing", "Questionable", "Questionable", "https://github.com/WigglyMuffin/DalamudPlugins/raw/main/pluginmaster.json"),
			new DependencyEntry("Nav", "vnavmesh", "vnavmesh", "https://puni.sh/api/repository/veyn"),
			new DependencyEntry("Duties", "DAD", "dad", "https://aethertek.io/x.json", IsStub: true),
			new DependencyEntry("Duties", "AI Duty Solver", "ADS", "https://aethertek.io/x.json"),
			new DependencyEntry("Duties", "AutoDuty", "AutoDuty", "https://puni.sh/api/repository/erdelf"),
			new DependencyEntry("Combat", "BossMod Reborn", "BossModReborn", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"),
			new DependencyEntry("Combat", "Wrath Combo", "WrathCombo", "https://love.puni.sh/ment.json"),
			new DependencyEntry("Combat", "Rotation Solver Reborn", "RotationSolver", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"),
			new DependencyEntry("Combat", "FrenRider", "FrenRider", "https://aethertek.io/x.json"),
			new DependencyEntry("Data", "XA Database", "XADatabase", "https://aethertek.io/x.json"),
			new DependencyEntry("Data", "XA Slave", "XASlave", "https://aethertek.io/x.json"),
			new DependencyEntry("Support", "AutoRetainer", "AutoRetainer", "https://love.puni.sh/ment.json"),
			new DependencyEntry("Support", "Lifestream", "Lifestream", "https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json"),
			new DependencyEntry("Support", "Yes Already", "YesAlready", "https://love.puni.sh/ment.json")
		});
		HuntLogCompanionStances = new string[5] { "Free Stance", "Defender Stance", "Attacker Stance", "Healer Stance", "Follow" };
		RequiredSetupDependencies = new string[4] { "Questionable", "AutoRetainer", "Lifestream", "XADatabase" };
	}
}
