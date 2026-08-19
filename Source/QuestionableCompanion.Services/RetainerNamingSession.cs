using System.Collections.Generic;

namespace QuestionableCompanion.Services;

internal sealed record RetainerNamingSession(string BaseName, IReadOnlyList<string> Candidates);
