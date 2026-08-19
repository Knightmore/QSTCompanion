using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace QuestionableCompanion.Helpers;

public static class ImGuiDragDrop
{
	public static void SetDragDropPayload<T>(string type, T data, ImGuiCond cond = ImGuiCond.None) where T : struct
	{
		ReadOnlySpan<byte> data2 = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1));
		ImGui.SetDragDropPayload(type, data2, cond);
	}

	public unsafe static bool AcceptDragDropPayload<T>(string type, out T payload, ImGuiDragDropFlags flags = ImGuiDragDropFlags.None) where T : struct
	{
		ImGuiPayload* ptr = ImGui.AcceptDragDropPayload(type, flags);
		payload = ((ptr != null) ? Unsafe.Read<T>(ptr->Data) : default(T));
		return ptr != null;
	}
}
