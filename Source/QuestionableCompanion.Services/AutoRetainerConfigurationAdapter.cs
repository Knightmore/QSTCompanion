using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class AutoRetainerConfigurationAdapter
{
	public static AutoRetainerMutationResult ConfigureStarterPlan(object additionalData, RetainerType type)
	{
		if (!TryGetMember(additionalData, "VenturePlan", out object value) || value == null)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer VenturePlan capability is unavailable");
		}
		if (!TryGetMember(value, "List", out object value2) || !(value2 is IList list))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer VenturePlan.List capability is unavailable");
		}
		if (list.IsReadOnly || list.IsFixedSize)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer VenturePlan.List is not mutable");
		}
		Type type2 = value2.GetType().GetGenericArguments().FirstOrDefault();
		if (type2 == null)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer planned-venture element type is unavailable");
		}
		(uint, uint) tuple = AutoRetainerStarterPlans.Get(type);
		object obj = CreatePlannedVenture(type2, tuple.Item1);
		object obj2 = CreatePlannedVenture(type2, tuple.Item2);
		if (obj == null || obj2 == null)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer planned-venture fields are unavailable");
		}
		Type memberType = GetMemberType(value.GetType(), "PlanCompleteBehavior");
		if (memberType == null || !memberType.IsEnum || !Enum.TryParse(memberType, "Assign_Quick_Venture", ignoreCase: false, out object result) || !CanSetMember(value, "PlanCompleteBehavior") || !CanSetMember(additionalData, "VenturePlanIndex") || !CanSetMember(additionalData, "EnablePlanner"))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer planner fields are unavailable");
		}
		try
		{
			list.Clear();
			list.Add(obj);
			list.Add(obj2);
			if (!TrySetMember(value, "PlanCompleteBehavior", result) || !TrySetMember(additionalData, "VenturePlanIndex", 0u) || !TrySetMember(additionalData, "EnablePlanner", true))
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer planner fields could not be mutated");
			}
		}
		catch (Exception ex)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer planner mutation failed: " + ex.Message);
		}
		return VerifyStarterPlan(additionalData, type);
	}

	public static AutoRetainerMutationResult VerifyStarterPlan(object additionalData, RetainerType type)
	{
		if (!TryGetMember(additionalData, "VenturePlanIndex", out object value) || Convert.ToUInt32(value) != 0 || !TryGetMember(additionalData, "EnablePlanner", out object value2) || !(value2 is bool) || !(bool)value2 || !TryGetMember(additionalData, "VenturePlan", out object value3) || value3 == null || !TryGetMember(value3, "List", out object value4) || !(value4 is IEnumerable enumerable) || !TryGetMember(value3, "PlanCompleteBehavior", out object value5) || !string.Equals(value5?.ToString(), "Assign_Quick_Venture", StringComparison.Ordinal))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer did not preserve the expected planner settings");
		}
		List<(uint, int)> list = new List<(uint, int)>();
		foreach (object item in enumerable)
		{
			if (item == null || !TryGetMember(item, "ID", out object value6) || !TryGetMember(item, "Num", out object value7))
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer returned an unreadable planned venture");
			}
			list.Add((Convert.ToUInt32(value6), Convert.ToInt32(value7)));
		}
		(uint, uint) tuple = AutoRetainerStarterPlans.Get(type);
		if (!list.SequenceEqual(new(uint, int)[2]
		{
			(tuple.Item1, 1),
			(tuple.Item2, 1)
		}))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer did not preserve the expected starter venture IDs");
		}
		return AutoRetainerMutationResult.Ok;
	}

	public static AutoRetainerMutationResult ConfigureCharacterEnabled(object offlineCharacterData, bool enabled)
	{
		if (!enabled)
		{
			return AutoRetainerMutationResult.Ok;
		}
		if (!TrySetMember(offlineCharacterData, "Enabled", true))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer character-enable capability is unavailable");
		}
		return AutoRetainerMutationResult.Ok;
	}

	public static AutoRetainerMutationResult VerifyCharacterIdentity(object offlineCharacterData, ulong contentId, string characterKey)
	{
		int num = characterKey.LastIndexOf('@');
		if (contentId == 0L || num <= 0 || num == characterKey.Length - 1)
		{
			return AutoRetainerMutationResult.Fail("The expected AutoRetainer character identity is invalid");
		}
		if (!TryGetMember(offlineCharacterData, "CID", out object value) || !TryConvertUInt64(value, out var result) || result != contentId || !TryGetMember(offlineCharacterData, "Name", out object value2))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer character ContentId/name capability is unavailable or mismatched");
		}
		if (!TryGetMember(offlineCharacterData, "World", out object value3))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer character home-world capability is unavailable");
		}
		string b = characterKey.Substring(0, num);
		string b2 = characterKey.Substring(num + 1);
		if (!string.Equals(value2?.ToString(), b, StringComparison.OrdinalIgnoreCase) || !string.Equals(value3?.ToString(), b2, StringComparison.OrdinalIgnoreCase))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer returned a different character identity");
		}
		return AutoRetainerMutationResult.Ok;
	}

	internal static AutoRetainerMutationResult VerifyCharacterEnabled(object offlineCharacterData)
	{
		if (!TryGetMember(offlineCharacterData, "Enabled", out object value) || !(value is bool) || !(bool)value)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer character is not enabled");
		}
		return AutoRetainerMutationResult.Ok;
	}

	private static bool TryConvertUInt64(object? value, out ulong result)
	{
		try
		{
			if (value == null)
			{
				result = 0uL;
				return false;
			}
			result = Convert.ToUInt64(value);
			return true;
		}
		catch
		{
			result = 0uL;
			return false;
		}
	}

	public static AutoRetainerMutationResult EnableRetainers(object enabledRetainerRegistry, ulong contentId, IEnumerable<string> retainerNames)
	{
		if (!(enabledRetainerRegistry is IDictionary dictionary))
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer enabled-retainer registry shape is unavailable");
		}
		object obj = (dictionary.Contains(contentId) ? dictionary[contentId] : null);
		if (obj == null)
		{
			Type type = enabledRetainerRegistry.GetType().GetGenericArguments().ElementAtOrDefault(1);
			if (type == null)
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer enabled-retainer value type is unavailable");
			}
			obj = Activator.CreateInstance(type);
			if (obj == null)
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer enabled-retainer set could not be created");
			}
			dictionary[contentId] = obj;
		}
		MethodInfo method = obj.GetType().GetMethod("Add", new Type[1] { typeof(string) });
		MethodInfo method2 = obj.GetType().GetMethod("Contains", new Type[1] { typeof(string) });
		if (method == null || method2 == null)
		{
			return AutoRetainerMutationResult.Fail("AutoRetainer enabled-retainer set is not mutable");
		}
		foreach (string item in retainerNames.Where((string x) => !string.IsNullOrWhiteSpace(x)))
		{
			method.Invoke(obj, new object[1] { item });
		}
		foreach (string item2 in retainerNames.Where((string x) => !string.IsNullOrWhiteSpace(x)))
		{
			object obj2 = method2.Invoke(obj, new object[1] { item2 });
			if (!(obj2 is bool) || !(bool)obj2)
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer did not retain enabled state for " + item2);
			}
		}
		return AutoRetainerMutationResult.Ok;
	}

	public static AutoRetainerMutationResult VerifyRetainersEnabled(object enabledRetainerRegistry, ulong contentId, IEnumerable<string> retainerNames)
	{
		if (enabledRetainerRegistry is IDictionary dictionary && dictionary.Contains(contentId))
		{
			object enabledNames = dictionary[contentId];
			if (enabledNames != null)
			{
				MethodInfo containsMethod = enabledNames.GetType().GetMethod("Contains", new Type[1] { typeof(string) });
				if (containsMethod == null)
				{
					return AutoRetainerMutationResult.Fail("AutoRetainer enabled-retainer set cannot be verified");
				}
				if (!retainerNames.All(delegate(string name)
				{
					object obj = containsMethod.Invoke(enabledNames, new object[1] { name });
					return obj is bool && (bool)obj;
				}))
				{
					return AutoRetainerMutationResult.Fail("AutoRetainer did not preserve enabled state for every exact retainer");
				}
				return AutoRetainerMutationResult.Ok;
			}
		}
		return AutoRetainerMutationResult.Fail("AutoRetainer returned no enabled-retainer set for the character");
	}

	public static IReadOnlyList<AutoRetainerOfflineRetainer> ReadOfflineRetainers(object offlineCharacterData)
	{
		if (!TryGetMember(offlineCharacterData, "RetainerData", out object value) || !(value is IEnumerable enumerable))
		{
			return Array.Empty<AutoRetainerOfflineRetainer>();
		}
		List<AutoRetainerOfflineRetainer> list = new List<AutoRetainerOfflineRetainer>();
		foreach (object item in enumerable)
		{
			if (item != null && TryGetMember(item, "RetainerID", out object value2) && TryGetMember(item, "Name", out object value3) && TryGetMember(item, "HasVenture", out object value4) && TryGetMember(item, "VentureID", out object value5) && TryGetMember(item, "VentureEndsAt", out object value6) && TryGetMember(item, "Level", out object value7) && TryGetMember(item, "Job", out object value8))
			{
				list.Add(new AutoRetainerOfflineRetainer(Convert.ToUInt64(value2), value3?.ToString() ?? string.Empty, Convert.ToBoolean(value4), Convert.ToUInt32(value5), Convert.ToInt64(value6), Convert.ToInt32(value7), Convert.ToUInt32(value8)));
			}
		}
		return list;
	}

	public static AutoRetainerMutationResult VerifyFirstVentures(object offlineCharacterData, IEnumerable<TrackedRetainerCheckpoint> expectedRetainers, long nowUnixSeconds)
	{
		IReadOnlyList<AutoRetainerOfflineRetainer> source = ReadOfflineRetainers(offlineCharacterData);
		foreach (TrackedRetainerCheckpoint expected in expectedRetainers)
		{
			AutoRetainerOfflineRetainer autoRetainerOfflineRetainer = source.FirstOrDefault((AutoRetainerOfflineRetainer retainer) => retainer.RetainerId == expected.RetainerId && string.Equals(retainer.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
			if (autoRetainerOfflineRetainer == null)
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer did not return exact retainer " + expected.Name);
			}
			if (!autoRetainerOfflineRetainer.HasVenture || autoRetainerOfflineRetainer.VentureId == 0 || (expected.ExpectedFirstVentureId != 0 && autoRetainerOfflineRetainer.VentureId != expected.ExpectedFirstVentureId) || autoRetainerOfflineRetainer.VentureEndsAt <= nowUnixSeconds)
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer has not assigned the expected first venture to " + expected.Name);
			}
		}
		return AutoRetainerMutationResult.Ok;
	}

	private static object? CreatePlannedVenture(Type type, uint id)
	{
		object obj;
		try
		{
			obj = Activator.CreateInstance(type);
		}
		catch
		{
			return null;
		}
		if (obj == null || !CanSetMember(obj, "ID") || !CanSetMember(obj, "Num"))
		{
			return null;
		}
		try
		{
			return (TrySetMember(obj, "ID", id) && TrySetMember(obj, "Num", 1)) ? obj : null;
		}
		catch
		{
			return null;
		}
	}

	private static Type? GetMemberType(Type type, string name)
	{
		object obj = type.GetField(name, BindingFlags.Instance | BindingFlags.Public)?.FieldType;
		if (obj == null)
		{
			PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
			if ((object)property == null)
			{
				return null;
			}
			obj = property.PropertyType;
		}
		return (Type?)obj;
	}

	private static bool CanSetMember(object target, string name)
	{
		Type type = target.GetType();
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
		if (field != null)
		{
			return !field.IsInitOnly;
		}
		return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.CanWrite ?? false;
	}

	private static bool TryGetMember(object target, string name, out object? value)
	{
		Type type = target.GetType();
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
		if (field != null)
		{
			value = field.GetValue(target);
			return true;
		}
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
		if (property != null && property.CanRead)
		{
			value = property.GetValue(target);
			return true;
		}
		value = null;
		return false;
	}

	private static bool TrySetMember(object target, string name, object value)
	{
		Type type = target.GetType();
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
		if (field != null && !field.IsInitOnly)
		{
			field.SetValue(target, ConvertValue(value, field.FieldType));
			return true;
		}
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
		if (property != null && property.CanWrite)
		{
			property.SetValue(target, ConvertValue(value, property.PropertyType));
			return true;
		}
		return false;
	}

	private static object ConvertValue(object value, Type destinationType)
	{
		if (!destinationType.IsInstanceOfType(value))
		{
			return Convert.ChangeType(value, destinationType);
		}
		return value;
	}
}
