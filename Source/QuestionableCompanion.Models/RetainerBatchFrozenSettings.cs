using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Models;

[Serializable]
internal sealed class RetainerBatchFrozenSettings
{
	public RetainerStarterCity City { get; set; }

	public RetainerAppearanceRace Appearance { get; set; }

	public RetainerGender Gender { get; set; }

	public RetainerClan Clan { get; set; }

	public RetainerPersonality Personality { get; set; }

	public RetainerStopAfter StopAfter { get; set; } = RetainerStopAfter.AutoRetainerBootstrapped;

	public bool AttachStarterPlan { get; set; } = true;

	public bool EnableNewRetainers { get; set; } = true;

	public bool EnableCharacter { get; set; } = true;

	public List<string> SampleNames { get; set; } = new List<string>();

	public List<string> UnavailableNames { get; set; } = new List<string>();
}
