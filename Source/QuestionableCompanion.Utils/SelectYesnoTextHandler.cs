using System;
using System.Text.RegularExpressions;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuestionableCompanion.Utils;

public static class SelectYesnoTextHandler
{
	public unsafe static string? GetDialogText(AtkUnitBase* addon)
	{
		if (addon == null)
		{
			return null;
		}
		try
		{
			if (((AddonSelectYesno*)addon)->PromptText == null)
			{
				return null;
			}
			return MemoryHelper.ReadSeStringNullTerminated(new IntPtr((byte*)((AddonSelectYesno*)addon)->AtkValues->String)).TextValue.Replace('\n', ' ').Trim();
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static bool MatchesPattern(string text, string pattern, bool isRegex)
	{
		if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
		{
			return false;
		}
		if (isRegex)
		{
			try
			{
				return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled).IsMatch(text);
			}
			catch (Exception)
			{
				return false;
			}
		}
		return text.Contains(pattern, StringComparison.InvariantCultureIgnoreCase);
	}

	public unsafe static bool ClickYesButton(AtkUnitBase* addon)
	{
		if (addon == null)
		{
			return false;
		}
		try
		{
			AtkComponentButton* yesButton = ((AddonSelectYesno*)addon)->YesButton;
			if (yesButton == null)
			{
				return false;
			}
			if (!yesButton->IsEnabled || !yesButton->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
			{
				return false;
			}
			ClickButton(addon, yesButton);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public unsafe static bool ClickNoButton(AtkUnitBase* addon)
	{
		if (addon == null)
		{
			return false;
		}
		try
		{
			AtkComponentButton* noButton = ((AddonSelectYesno*)addon)->NoButton;
			if (noButton == null)
			{
				return false;
			}
			if (!noButton->IsEnabled || !noButton->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
			{
				return false;
			}
			ClickButton(addon, noButton);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private unsafe static void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
	{
		if (button != null && addon != null)
		{
			AtkResNode atkResNode = button->AtkComponentBase.OwnerNode->AtkResNode;
			AtkEvent* ptr = atkResNode.AtkEventManager.Event;
			if (ptr != null)
			{
				addon->ReceiveEvent(ptr->State.EventType, (int)ptr->Param, atkResNode.AtkEventManager.Event, null);
			}
		}
	}
}
