namespace QuestionableCompanion.Services;

public class ExpansionInfo
{
	public string Name { get; set; } = "";

	public string ShortName { get; set; } = "";

	public uint MinQuestId { get; set; }

	public uint MaxQuestId { get; set; }

	public int ExpectedQuestCount { get; set; }
}
