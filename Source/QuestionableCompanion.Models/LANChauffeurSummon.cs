namespace QuestionableCompanion.Models;

public class LANChauffeurSummon
{
	public string QuesterName { get; set; } = string.Empty;

	public ushort QuesterWorldId { get; set; }

	public ushort QuesterCurrentWorldId { get; set; }

	public uint ZoneId { get; set; }

	public float TargetX { get; set; }

	public float TargetY { get; set; }

	public float TargetZ { get; set; }

	public float QuesterX { get; set; }

	public float QuesterY { get; set; }

	public float QuesterZ { get; set; }

	public bool IsAttuneAetheryte { get; set; }

	public string? NearestAetheryteName { get; set; }
}
