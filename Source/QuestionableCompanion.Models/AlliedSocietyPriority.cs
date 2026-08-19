namespace QuestionableCompanion.Models;

public class AlliedSocietyPriority
{
	public required byte SocietyId { get; set; }

	public bool Enabled { get; set; }

	public int Order { get; set; }
}
