namespace QuestionableCompanion.Data;

public class ExpansionProgress
{
	public MSQExpansionData.Expansion Expansion { get; set; }

	public int CompletedCount { get; set; }

	public int ExpectedCount { get; set; }

	public float Percentage { get; set; }

	public bool IsComplete { get; set; }

	public string ExpansionName => MSQExpansionData.GetExpansionName(Expansion);

	public string ExpansionShortName => MSQExpansionData.GetExpansionShortName(Expansion);
}
