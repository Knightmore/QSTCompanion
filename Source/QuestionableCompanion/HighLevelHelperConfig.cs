using System;

namespace QuestionableCompanion;

[Serializable]
public class HighLevelHelperConfig
{
	public string CharacterName { get; set; } = string.Empty;

	public ushort WorldId { get; set; }

	public string WorldName { get; set; } = string.Empty;
}
