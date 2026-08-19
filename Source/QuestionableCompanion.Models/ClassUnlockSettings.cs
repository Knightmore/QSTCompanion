using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class ClassUnlockSettings
{
	public List<uint> SelectedClassJobIds { get; set; } = new List<uint>();

	public bool UnlockDuringStopPointRotation { get; set; }

	public Dictionary<int, uint> SwitchToClassJobIdByLevel { get; set; } = new Dictionary<int, uint>();

	public Dictionary<int, int> KeepCurrentClassAtLevelByUnlockTier { get; set; } = new Dictionary<int, int>();

	public uint SwitchToClassJobId { get; set; }

	public Dictionary<string, ClassUnlockAnchorGearset> AnchorGearsets { get; set; } = new Dictionary<string, ClassUnlockAnchorGearset>(StringComparer.OrdinalIgnoreCase);

	public void Normalize()
	{
		if (SelectedClassJobIds == null)
		{
			List<uint> list = (SelectedClassJobIds = new List<uint>());
		}
		if (SwitchToClassJobIdByLevel == null)
		{
			Dictionary<int, uint> dictionary = (SwitchToClassJobIdByLevel = new Dictionary<int, uint>());
		}
		if (KeepCurrentClassAtLevelByUnlockTier == null)
		{
			Dictionary<int, int> dictionary3 = (KeepCurrentClassAtLevelByUnlockTier = new Dictionary<int, int>());
		}
		if (SwitchToClassJobIdByLevel.Count == 0 && SwitchToClassJobId != 0)
		{
			int[] array = new int[4] { 50, 60, 70, 80 };
			foreach (int key in array)
			{
				SwitchToClassJobIdByLevel[key] = SwitchToClassJobId;
			}
		}
		SwitchToClassJobIdByLevel = SwitchToClassJobIdByLevel.Where(delegate(KeyValuePair<int, uint> entry)
		{
			bool flag;
			switch (entry.Key)
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
			return flag && entry.Value != 0 && SelectedClassJobIds.Contains(entry.Value);
		}).ToDictionary((KeyValuePair<int, uint> entry) => entry.Key, (KeyValuePair<int, uint> entry) => entry.Value);
		KeepCurrentClassAtLevelByUnlockTier = KeepCurrentClassAtLevelByUnlockTier.Where(delegate(KeyValuePair<int, int> entry)
		{
			bool flag;
			switch (entry.Key)
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
			if (flag)
			{
				int value = entry.Value;
				if (value >= 1)
				{
					return value <= 100;
				}
				return false;
			}
			return false;
		}).ToDictionary((KeyValuePair<int, int> entry) => entry.Key, (KeyValuePair<int, int> entry) => entry.Value);
		SwitchToClassJobId = 0u;
		AnchorGearsets = ((AnchorGearsets == null) ? new Dictionary<string, ClassUnlockAnchorGearset>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, ClassUnlockAnchorGearset>(AnchorGearsets, StringComparer.OrdinalIgnoreCase));
	}
}
