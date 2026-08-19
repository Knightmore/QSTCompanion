using System;
using System.Text.Json.Serialization;

namespace QuestionableCompanion.Models;

[Serializable]
public class StopPoint
{
	public uint QuestId { get; set; }

	[JsonInclude]
	[JsonPropertyName("Sequence")]
	public byte? Sequence { get; set; }

	public bool IsActive { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.Now;

	public string? QuestName { get; set; }

	public string DisplayName
	{
		get
		{
			if (!string.IsNullOrEmpty(QuestName))
			{
				if (Sequence.HasValue)
				{
					return $"{QuestName} ({QuestId}, Seq {Sequence.Value})";
				}
				return $"{QuestName} ({QuestId})";
			}
			if (Sequence.HasValue)
			{
				return $"Quest {QuestId} (Seq {Sequence.Value})";
			}
			return $"Quest {QuestId}";
		}
	}
}
