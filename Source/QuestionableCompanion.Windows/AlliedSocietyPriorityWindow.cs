using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;
using QuestionableCompanion.Services;

namespace QuestionableCompanion.Windows;

public class AlliedSocietyPriorityWindow : Window, IDisposable
{
	private readonly Configuration configuration;

	private readonly AlliedSocietyDatabase database;

	private List<AlliedSocietyPriority> editingPriorities = new List<AlliedSocietyPriority>();

	private int? draggedIndex;

	private const string DragDropId = "ALLIED_SOCIETY_PRIORITY";

	private readonly Dictionary<byte, string> societyNames = new Dictionary<byte, string>
	{
		{ 1, "Amalj'aa" },
		{ 2, "Sylphs" },
		{ 3, "Kobolds" },
		{ 4, "Sahagin" },
		{ 5, "Ixal" },
		{ 6, "Vanu Vanu" },
		{ 7, "Vath" },
		{ 8, "Moogles" },
		{ 9, "Kojin" },
		{ 10, "Ananta" },
		{ 11, "Namazu" },
		{ 12, "Pixies" },
		{ 13, "Qitari" },
		{ 14, "Dwarves" },
		{ 15, "Arkasodara" },
		{ 16, "Omicrons" },
		{ 17, "Loporrits" },
		{ 18, "Pelupelu" },
		{ 19, "Mamool Ja" },
		{ 20, "Yok Huy" }
	};

	public AlliedSocietyPriorityWindow(Configuration configuration, AlliedSocietyDatabase database)
		: base("Allied Society Priority Configuration", ImGuiWindowFlags.NoCollapse)
	{
		this.configuration = configuration;
		this.database = database;
		base.Size = new Vector2(400f, 600f);
		base.SizeCondition = ImGuiCond.FirstUseEver;
	}

	public void Dispose()
	{
	}

	public override void OnOpen()
	{
		if (configuration.AlliedSociety.RotationConfig.Priorities.Count == 0)
		{
			configuration.AlliedSociety.RotationConfig.InitializeDefaults();
			database.SaveToConfig();
		}
		editingPriorities = (from p in configuration.AlliedSociety.RotationConfig.Priorities
			orderby p.Order
			select new AlliedSocietyPriority
			{
				SocietyId = p.SocietyId,
				Enabled = p.Enabled,
				Order = p.Order
			}).ToList();
	}

	public override void Draw()
	{
		ImGui.TextWrapped("Drag societies to reorder priorities. Uncheck to disable specific societies.");
		ImGui.Separator();
		float y = ImGui.GetWindowSize().Y - 120f;
		ImGui.BeginChild("PriorityList", new Vector2(0f, y), border: true);
		for (int i = 0; i < editingPriorities.Count; i++)
		{
			AlliedSocietyPriority alliedSocietyPriority = editingPriorities[i];
			string text = (societyNames.ContainsKey(alliedSocietyPriority.SocietyId) ? societyNames[alliedSocietyPriority.SocietyId] : $"Unknown ({alliedSocietyPriority.SocietyId})");
			ImGui.PushID(i);
			if (draggedIndex == i)
			{
				uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0.7f, 0f, 0.3f));
				Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
				Vector2 vector = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight());
				ImGui.GetWindowDrawList().AddRectFilled(cursorScreenPos, cursorScreenPos + vector, col);
			}
			ImGui.PushFont(UiBuilder.IconFont);
			ImU8String label = new ImU8String(6, 1);
			label.AppendFormatted(FontAwesomeIcon.ArrowsUpDownLeftRight.ToIconString());
			label.AppendLiteral("##drag");
			ImGui.Button(label);
			ImGui.PopFont();
			if (ImGui.IsItemHovered())
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
			}
			if (ImGui.BeginDragDropSource())
			{
				draggedIndex = i;
				ImGuiDragDrop.SetDragDropPayload("ALLIED_SOCIETY_PRIORITY", i);
				ImGui.Text(text);
				ImGui.EndDragDropSource();
			}
			else if (draggedIndex == i && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				draggedIndex = null;
			}
			if (ImGui.BeginDragDropTarget())
			{
				if (ImGuiDragDrop.AcceptDragDropPayload<int>("ALLIED_SOCIETY_PRIORITY", out var payload) && payload != i)
				{
					AlliedSocietyPriority item = editingPriorities[payload];
					editingPriorities.RemoveAt(payload);
					editingPriorities.Insert(i, item);
					UpdateOrders();
					draggedIndex = i;
				}
				ImGui.EndDragDropTarget();
			}
			ImGui.SameLine();
			bool v = alliedSocietyPriority.Enabled;
			ImU8String label2 = new ImU8String(9, 0);
			label2.AppendLiteral("##enabled");
			if (ImGui.Checkbox(label2, ref v))
			{
				alliedSocietyPriority.Enabled = v;
			}
			ImGui.SameLine();
			ImGui.Text(text);
			ImGui.PopID();
		}
		ImGui.EndChild();
		ImGui.Separator();
		if (ImGui.Button("Save"))
		{
			Save();
			base.IsOpen = false;
		}
		ImGui.SameLine();
		if (ImGui.Button("Cancel"))
		{
			base.IsOpen = false;
		}
	}

	private void UpdateOrders()
	{
		for (int i = 0; i < editingPriorities.Count; i++)
		{
			editingPriorities[i].Order = i;
		}
	}

	private void Save()
	{
		UpdateOrders();
		configuration.AlliedSociety.RotationConfig.Priorities = editingPriorities;
		database.SaveToConfig();
	}
}
