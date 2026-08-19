using System;

namespace QuestionableCompanion.Models;

public class LANHelperInfo
{
	public string Name { get; set; } = string.Empty;

	public ushort WorldId { get; set; }

	public string IPAddress { get; set; } = string.Empty;

	public LANHelperStatus Status { get; set; }

	public DateTime LastSeen { get; set; } = DateTime.Now;
}
