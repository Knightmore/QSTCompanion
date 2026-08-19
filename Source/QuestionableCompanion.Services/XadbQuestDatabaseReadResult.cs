using System;
using System.Collections.Generic;

namespace QuestionableCompanion.Services;

internal sealed class XadbQuestDatabaseReadResult
{
	public bool IsAvailable { get; init; }

	public IReadOnlyList<XadbQuestDatabaseRow> Rows { get; init; } = Array.Empty<XadbQuestDatabaseRow>();

	public string FailureReason { get; init; } = string.Empty;
}
