namespace QuestionableCompanion.Models;

public class LANHeartbeat
{
	public string ClientName { get; set; } = string.Empty;

	public ushort ClientWorldId { get; set; }

	public string ClientRole { get; set; } = string.Empty;
}
