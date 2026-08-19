using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestionableCompanion.Models;

[Serializable]
public sealed class RetainerSetupConfiguration
{
	public const int CurrentMigrationVersion = 2;

	public int MigrationVersion { get; set; }

	public bool FilterBelowLevelEnabled { get; set; }

	public int FilterBelowLevel { get; set; } = 100;

	public bool FilterIncompleteSetup { get; set; }

	public RetainerStarterCity City { get; set; }

	public RetainerAppearanceRace Appearance { get; set; }

	public RetainerGender Gender { get; set; }

	public RetainerClan Clan { get; set; }

	public RetainerPersonality Personality { get; set; }

	public RetainerStopAfter StopAfter { get; set; } = RetainerStopAfter.AutoRetainerBootstrapped;

	public bool AttachStarterPlan { get; set; } = true;

	public bool EnableNewRetainers { get; set; } = true;

	public bool EnableCharacter { get; set; } = true;

	public Dictionary<ulong, CharacterRetainerSetupChoice> CharacterChoices { get; set; } = new Dictionary<ulong, CharacterRetainerSetupChoice>();

	public Dictionary<ulong, CharacterRetainerSetupCheckpoint> Checkpoints { get; set; } = new Dictionary<ulong, CharacterRetainerSetupCheckpoint>();

	public List<string> SampleNames { get; set; } = new List<string>();

	public void Normalize()
	{
		bool flag = MigrationVersion < 2;
		FilterBelowLevel = Math.Clamp(FilterBelowLevel, 1, 100);
		City = (Enum.IsDefined(City) ? City : RetainerStarterCity.Automatic);
		Appearance = (Enum.IsDefined(Appearance) ? Appearance : RetainerAppearanceRace.Random);
		Gender = (Enum.IsDefined(Gender) ? Gender : RetainerGender.Random);
		Clan = (Enum.IsDefined(Clan) ? Clan : RetainerClan.Random);
		Personality = (Enum.IsDefined(Personality) ? Personality : RetainerPersonality.Random);
		StopAfter = (RetainerStopAfter)Math.Clamp((int)StopAfter, 0, 5);
		if (CharacterChoices == null)
		{
			Dictionary<ulong, CharacterRetainerSetupChoice> dictionary = (CharacterChoices = new Dictionary<ulong, CharacterRetainerSetupChoice>());
		}
		if (Checkpoints == null)
		{
			Dictionary<ulong, CharacterRetainerSetupCheckpoint> dictionary3 = (Checkpoints = new Dictionary<ulong, CharacterRetainerSetupCheckpoint>());
		}
		SampleNames = (from x in SampleNames ?? new List<string>()
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
		ulong[] array = CharacterChoices.Keys.ToArray();
		foreach (ulong key in array)
		{
			CharacterRetainerSetupChoice characterRetainerSetupChoice = CharacterChoices[key] ?? new CharacterRetainerSetupChoice();
			CharacterChoices[key] = characterRetainerSetupChoice;
			CharacterRetainerSetupChoice characterRetainerSetupChoice2 = characterRetainerSetupChoice;
			if (characterRetainerSetupChoice2.CharacterKey == null)
			{
				string text = (characterRetainerSetupChoice2.CharacterKey = string.Empty);
			}
			characterRetainerSetupChoice.Type = (Enum.IsDefined(characterRetainerSetupChoice.Type) ? characterRetainerSetupChoice.Type : RetainerType.Combat);
			characterRetainerSetupChoice.CombatStarterClassId = ((!IsStarterCombatClass(characterRetainerSetupChoice.CombatStarterClassId)) ? 1u : characterRetainerSetupChoice.CombatStarterClassId);
		}
		array = Checkpoints.Keys.ToArray();
		foreach (ulong key2 in array)
		{
			CharacterRetainerSetupCheckpoint characterRetainerSetupCheckpoint = Checkpoints[key2] ?? new CharacterRetainerSetupCheckpoint();
			Checkpoints[key2] = characterRetainerSetupCheckpoint;
			characterRetainerSetupCheckpoint.Normalize(key2);
			if (flag && IsStructurallyProvenLegacyDisposalCancellation(characterRetainerSetupCheckpoint))
			{
				characterRetainerSetupCheckpoint.State = RetainerCheckpointState.Failed;
				characterRetainerSetupCheckpoint.Disposition = RetainerCheckpointDisposition.InterruptedBeforeSideEffects;
				characterRetainerSetupCheckpoint.LastError = "Interrupted before side effects — revalidation required";
				characterRetainerSetupCheckpoint.CleanupVerified = false;
				characterRetainerSetupCheckpoint.DisallowAutomaticRequeue = true;
				characterRetainerSetupCheckpoint.PendingCheckpoint = null;
			}
		}
		MigrationVersion = Math.Max(MigrationVersion, 2);
	}

	public static bool IsStarterCombatClass(uint classJobId)
	{
		switch (classJobId)
		{
		case 1u:
		case 2u:
		case 3u:
		case 4u:
		case 5u:
		case 6u:
		case 7u:
		case 26u:
			return true;
		default:
			return false;
		}
	}

	private static bool IsStructurallyProvenLegacyDisposalCancellation(CharacterRetainerSetupCheckpoint checkpoint)
	{
		if (checkpoint.State == RetainerCheckpointState.Failed && checkpoint.LastError.Equals("Cancelled by operator", StringComparison.OrdinalIgnoreCase) && checkpoint.IntendedRetainerCount == 0 && checkpoint.Retainers.Count == 0 && checkpoint.ReservedNames.Count == 0 && !checkpoint.PendingCheckpoint.HasValue && checkpoint.StarterItemId == 0 && checkpoint.StarterGearAcquiredCount == 0)
		{
			return !checkpoint.CleanupVerified;
		}
		return false;
	}
}
