namespace QuestionableCompanion.Models;

public class AlliedSocietyProgress
{
	public required string CharacterId { get; set; }

	public required byte SocietyId { get; set; }

	public int CurrentRank { get; set; }

	public bool IsMaxRank { get; set; }
}
