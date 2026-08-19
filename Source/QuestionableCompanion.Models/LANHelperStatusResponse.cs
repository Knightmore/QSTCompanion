namespace QuestionableCompanion.Models;

public class LANHelperStatusResponse
{
	public string Name { get; set; } = string.Empty;

	public ushort WorldId { get; set; }

	public LANHelperStatus Status { get; set; }

	public string? CurrentActivity { get; set; }
}
