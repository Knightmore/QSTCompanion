using System.Collections.Generic;

namespace QuestionableCompanion.Services;

public sealed record JobStoneGearsetObservation(bool IsLoggedIn, bool DalamudPlayerStateLoaded, bool NativePlayerStateLoaded, ulong DalamudContentId, ulong NativeContentId, ulong GearsetContentId, uint DalamudClassJobId, uint NativeClassJobId, bool EquippedItemsLoaded, uint EquippedSoulCrystalItemId, bool GearsetDataAvailable, bool GearsetIsVirtual, bool SafeToMutate, int ActiveGearsetId, IReadOnlyList<JobStoneGearsetState> Gearsets);
