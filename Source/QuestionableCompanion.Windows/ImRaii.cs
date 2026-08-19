using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace QuestionableCompanion.Windows;

internal static class ImRaii
{
	internal readonly struct ImChild(bool success) : IDisposable
	{
		public bool Success { get; } = success;

		public void Dispose()
		{
			if (Success)
			{
				ImGui.EndChild();
			}
		}
	}

	internal readonly struct ImTable(bool success) : IDisposable
	{
		public bool Success { get; } = success;

		public void Dispose()
		{
			if (Success)
			{
				ImGui.EndTable();
			}
		}
	}

	[StructLayout(LayoutKind.Sequential, Size = 1)]
	internal readonly struct ImId : IDisposable
	{
		public void Dispose()
		{
			ImGui.PopID();
		}
	}

	public static ImChild Child(string id, Vector2 size, bool border, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
	{
		return new ImChild(ImGui.BeginChild(id, size, border, flags));
	}

	public static ImTable Table(string id, int columns, ImGuiTableFlags flags)
	{
		return new ImTable(ImGui.BeginTable(id, columns, flags));
	}

	public static ImTable Table(string id, int columns, ImGuiTableFlags flags, Vector2 outerSize)
	{
		return new ImTable(ImGui.BeginTable(id, columns, flags, outerSize));
	}

	public static ImId PushId(string id)
	{
		ImGui.PushID(id);
		return default(ImId);
	}

	public static ImId PushId(int id)
	{
		ImGui.PushID(id);
		return default(ImId);
	}
}
