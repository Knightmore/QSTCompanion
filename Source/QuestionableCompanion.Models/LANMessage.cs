using System;
using Newtonsoft.Json;

namespace QuestionableCompanion.Models;

public class LANMessage
{
	public LANMessageType Type { get; set; }

	public DateTime Timestamp { get; set; } = DateTime.Now;

	public string? Data { get; set; }

	public LANMessage()
	{
	}

	public LANMessage(LANMessageType type, object? data = null)
	{
		Type = type;
		if (data != null)
		{
			Data = JsonConvert.SerializeObject(data);
		}
	}

	public T? GetData<T>()
	{
		if (string.IsNullOrEmpty(Data))
		{
			return default(T);
		}
		return JsonConvert.DeserializeObject<T>(Data);
	}
}
