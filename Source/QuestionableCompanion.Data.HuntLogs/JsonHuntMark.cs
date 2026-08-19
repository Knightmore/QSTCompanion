using System.Text.Json.Serialization;

namespace QuestionableCompanion.Data.HuntLogs;

public sealed class JsonHuntMark
{
	[JsonPropertyName("BnpcName")]
	public uint BnpcName { get; set; }

	[JsonPropertyName("X")]
	public float X { get; set; }

	[JsonPropertyName("Y")]
	public float Y { get; set; }

	[JsonPropertyName("Z")]
	public float Z { get; set; }

	[JsonPropertyName("TerritoryId")]
	public uint TerritoryId { get; set; }

	[JsonPropertyName("FateId")]
	public uint FateId { get; set; }

	[JsonPropertyName("Level")]
	public byte? Level { get; set; }
}
