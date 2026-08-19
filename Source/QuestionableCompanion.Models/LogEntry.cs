using System;

namespace QuestionableCompanion.Models;

[Serializable]
public class LogEntry
{
	public DateTime Timestamp { get; set; } = DateTime.Now;

	public LogLevel Level { get; set; }

	public string Message { get; set; } = string.Empty;

	public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss");

	public string FormattedMessage => $"[{FormattedTimestamp}] {GetLevelIcon()} {Message}";

	private string GetLevelIcon()
	{
		return Level switch
		{
			LogLevel.Info => "→", 
			LogLevel.Success => "✓", 
			LogLevel.Warning => "⏳", 
			LogLevel.Error => "✗", 
			LogLevel.Debug => "▶", 
			_ => "•", 
		};
	}
}
