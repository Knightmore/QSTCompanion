using System;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class PluginLogger
{
	private readonly IPluginLog dalamudLog;

	public PluginLogger(IPluginLog dalamudLog)
	{
		this.dalamudLog = dalamudLog;
	}

	public void Debug(string message, string component = "Plugin")
	{
		dalamudLog.Debug(message);
	}

	public void Information(string message, string component = "Plugin")
	{
		dalamudLog.Information(message);
	}

	public void Warning(string message, string component = "Plugin")
	{
		dalamudLog.Warning(message);
	}

	public void Error(string message, string component = "Plugin")
	{
		dalamudLog.Error(message);
	}

	public void Error(Exception ex, string message, string component = "Plugin")
	{
		dalamudLog.Error(ex, message);
	}
}
