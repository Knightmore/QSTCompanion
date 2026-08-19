namespace QuestionableCompanion.Services;

public class ExpansionProgressInfo
{
	public string ExpansionName { get; set; } = "";

	public string ShortName { get; set; } = "";

	public int TotalQuests { get; set; }

	public int CompletedQuests { get; set; }

	public float Percentage { get; set; }
}
