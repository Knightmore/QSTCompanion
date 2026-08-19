namespace QuestionableCompanion.Models;

public sealed class StopPointImportResult
{
	public int Total { get; init; }

	public int Added { get; init; }

	public int Updated { get; init; }

	public int SequencesImported { get; init; }

	public int Failed { get; init; }

	public bool StopConditionsEnabled { get; init; }

	public string? ErrorMessage { get; init; }

	public bool Succeeded
	{
		get
		{
			if (string.IsNullOrEmpty(ErrorMessage) && Failed == 0)
			{
				return StopConditionsEnabled;
			}
			return false;
		}
	}
}
