using System.Collections.Generic;

namespace QuestionableCompanion.Services;

public sealed record StableNativeRetainerEvidence(RetainerNativeRosterSnapshot Snapshot, IReadOnlyList<LiveRetainerInfo> Roster);
