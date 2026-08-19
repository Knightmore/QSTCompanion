using System.Collections.Generic;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal sealed record RetainerStarterGearPurchaseResult(uint ItemId, IReadOnlyList<RetainerStarterGearSlotCheckpoint> OwnedSlots);
