using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

internal sealed class QuestionableReflection(IDalamudPluginInterface pluginInterface, IPluginLog log)
{
	private const string QuestionableInternalName = "Questionable";

	private const string QuestionablePluginTypeName = "Questionable.QuestionablePlugin";

	public bool TryGetManifestAuthor(out string manifestAuthor, out string failureReason)
	{
		manifestAuthor = string.Empty;
		failureReason = string.Empty;
		try
		{
			IExposedPlugin exposedPlugin = pluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin plugin) => string.Equals(plugin.InternalName, "Questionable", StringComparison.Ordinal));
			if (exposedPlugin == null)
			{
				failureReason = "Questionable is not installed.";
				return false;
			}
			if (!exposedPlugin.IsLoaded)
			{
				failureReason = "Questionable is installed, but it is not enabled or loaded for the current Dalamud profile.";
				return false;
			}
			manifestAuthor = exposedPlugin.Manifest.Author?.Trim() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(manifestAuthor))
			{
				failureReason = "Questionable's loaded manifest does not declare an author.";
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			failureReason = "Questionable manifest inspection failed (" + ex.GetType().Name + ").";
			log.Debug("[QuestionableReflection] " + failureReason + " " + ex.Message);
			return false;
		}
	}

	public bool TryGetSourceRepository(out string sourceRepository, out string failureReason)
	{
		sourceRepository = string.Empty;
		failureReason = string.Empty;
		try
		{
			IExposedPlugin exposedPlugin = pluginInterface.InstalledPlugins.FirstOrDefault((IExposedPlugin plugin) => string.Equals(plugin.InternalName, "Questionable", StringComparison.Ordinal));
			if (exposedPlugin == null)
			{
				failureReason = "Questionable is not installed.";
				return false;
			}
			if (!exposedPlugin.IsLoaded)
			{
				failureReason = "Questionable is installed, but it is not enabled or loaded for the current Dalamud profile.";
				return false;
			}
			object obj = FindDalamudPluginInstance("Questionable");
			if (obj == null)
			{
				return TryGetManifestRepository(exposedPlugin, out sourceRepository, out failureReason, "Questionable is loaded, but its live plugin instance could not be reflected.");
			}
			Type type = obj.GetType();
			if (!string.Equals(type.FullName, "Questionable.QuestionablePlugin", StringComparison.Ordinal))
			{
				failureReason = "The loaded Questionable entry has an unexpected plugin type (" + (type.FullName ?? type.Name) + ").";
				return false;
			}
			if (!(FindField(type, "_serviceProvider")?.GetValue(obj) is IServiceProvider serviceProvider))
			{
				return TryGetManifestRepository(exposedPlugin, out sourceRepository, out failureReason, "Questionable's runtime service provider could not be resolved.");
			}
			if (!(serviceProvider.GetService(typeof(IDalamudPluginInterface)) is IDalamudPluginInterface dalamudPluginInterface))
			{
				return TryGetManifestRepository(exposedPlugin, out sourceRepository, out failureReason, "Questionable's Dalamud plugin interface could not be resolved.");
			}
			sourceRepository = dalamudPluginInterface.SourceRepository?.Trim() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(sourceRepository))
			{
				return TryGetManifestRepository(exposedPlugin, out sourceRepository, out failureReason, "Questionable does not report an installation repository through its live plugin interface.");
			}
			return true;
		}
		catch (Exception ex)
		{
			failureReason = "Questionable repository reflection failed (" + ex.GetType().Name + ").";
			log.Debug("[QuestionableReflection] " + failureReason + " " + ex.Message);
			return false;
		}
	}

	private bool TryGetManifestRepository(IExposedPlugin exposedPlugin, out string sourceRepository, out string failureReason, string reflectionFailure)
	{
		sourceRepository = exposedPlugin.Manifest.InstalledFromUrl?.Trim() ?? string.Empty;
		if (!string.IsNullOrWhiteSpace(sourceRepository))
		{
			failureReason = string.Empty;
			log.Debug("[QuestionableReflection] " + reflectionFailure + " Using the loaded installation's manifest repository instead.");
			return true;
		}
		failureReason = reflectionFailure + " No installation repository is recorded in its manifest.";
		return false;
	}

	private object? FindDalamudPluginInstance(string internalName)
	{
		Assembly assembly = typeof(IDalamudPluginInterface).Assembly;
		Type type = assembly.GetType("Dalamud.Service`1");
		Type type2 = assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
		if (type == null || type2 == null)
		{
			return FindInstanceFromExposedPlugins(internalName);
		}
		object obj = type.MakeGenericType(type2).GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
		if (obj?.GetType().GetProperty("InstalledPlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj) is IEnumerable enumerable)
		{
			foreach (object item in enumerable)
			{
				if (item != null && string.Equals(GetStringProperty(item, "InternalName"), internalName, StringComparison.Ordinal))
				{
					return FindField(item.GetType(), "instance")?.GetValue(item);
				}
			}
		}
		return FindInstanceFromExposedPlugins(internalName);
	}

	private object? FindInstanceFromExposedPlugins(string internalName)
	{
		foreach (IExposedPlugin installedPlugin in pluginInterface.InstalledPlugins)
		{
			if (!string.Equals(installedPlugin.InternalName, internalName, StringComparison.Ordinal))
			{
				continue;
			}
			FieldInfo[] fields = installedPlugin.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			for (int i = 0; i < fields.Length; i++)
			{
				object value = fields[i].GetValue(installedPlugin);
				if (value != null)
				{
					object obj = FindField(value.GetType(), "instance")?.GetValue(value);
					if (obj != null)
					{
						return obj;
					}
				}
			}
			return null;
		}
		return null;
	}

	private static string? GetStringProperty(object instance, string propertyName)
	{
		return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)?.ToString();
	}

	private static FieldInfo? FindField(Type? type, string fieldName)
	{
		while (type != null)
		{
			FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				return field;
			}
			type = type.BaseType;
		}
		return null;
	}
}
