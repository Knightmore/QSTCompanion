using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal sealed class AutoRetainerReflectionBridge
{
	private sealed record ConfigFields(FieldInfo OfflineData, FieldInfo AdditionalData, FieldInfo SelectedRetainers);

	private sealed record OfflineFields(FieldInfo ContentId, FieldInfo Name, FieldInfo World, FieldInfo Enabled, FieldInfo RetainerData, FieldInfo ClassJobLevelArray, FieldInfo GrandCompanyRank);

	private sealed record RetainerFields(FieldInfo RetainerId, FieldInfo Name, FieldInfo HasVenture, FieldInfo VentureId, FieldInfo VentureEndsAt, FieldInfo Level, FieldInfo Job);

	private sealed record AdditionalFields(FieldInfo VenturePlan, FieldInfo VenturePlanIndex, FieldInfo EnablePlanner, FieldInfo PlanList, FieldInfo PlanCompleteBehavior, Type PlannedVentureType, FieldInfo PlannedVentureId, FieldInfo PlannedVentureNum);

	private sealed record ReflectedOwner(object Value, OfflineFields Fields, ulong ContentId, string Name, string HomeWorld, uint GrandCompanyRank, IReadOnlyList<int> ClassJobLevels, IReadOnlyList<AutoRetainerOfflineRetainer> Retainers);

	private sealed record ReflectionMutation(Action Apply, Action Rollback);

	private sealed record ReflectionTransaction(object Config, AutoRetainerCharacterSnapshot Snapshot, IReadOnlyList<ReflectionMutation> Mutations);

	private const string PluginTypeName = "AutoRetainer.AutoRetainer";

	private const string ConfigTypeName = "AutoRetainer.PluginData.Config";

	private const string OfflineCharacterTypeName = "AutoRetainerAPI.Configuration.OfflineCharacterData";

	private const string OfflineRetainerTypeName = "AutoRetainerAPI.Configuration.OfflineRetainerData";

	private const string AdditionalDataTypeName = "AutoRetainerAPI.Configuration.AdditionalRetainerData";

	private const string VenturePlanTypeName = "AutoRetainerAPI.Configuration.VenturePlan";

	private const string PlannedVentureTypeName = "AutoRetainerAPI.Configuration.PlannedVenture";

	private readonly Func<AutoRetainerReflectionTarget?> resolveTarget;

	internal AutoRetainerReflectionBridge(Func<AutoRetainerReflectionTarget?> resolveTarget)
	{
		this.resolveTarget = resolveTarget;
	}

	internal AutoRetainerReflectionReadResult ReadCharacter(ulong contentId)
	{
		if (contentId == 0L)
		{
			return AutoRetainerReflectionReadResult.Fail("The requested AutoRetainer ContentId is invalid.");
		}
		if (!TryResolveTarget(out AutoRetainerReflectionTarget target, out string error) || target == null)
		{
			return AutoRetainerReflectionReadResult.Fail(error);
		}
		if (!TryOpenConfig(target, out object config, out ConfigFields fields, out error) || config == null || fields == null)
		{
			return AutoRetainerReflectionReadResult.Fail(error);
		}
		if (!TryReadOwner(fields.OfflineData.GetValue(config), contentId, null, null, out ReflectedOwner owner, out error) || owner == null)
		{
			return AutoRetainerReflectionReadResult.Fail(error);
		}
		return new AutoRetainerReflectionReadResult(Success: true, string.Empty, CreateDetachedSnapshot(owner, plansReady: false, selected: false));
	}

	internal AutoRetainerReflectionReadResult ReadExact(AutoRetainerReflectionRequest request)
	{
		if (!ValidateRequest(request, out string error))
		{
			return AutoRetainerReflectionReadResult.Fail(error);
		}
		if (!TryResolveTarget(out AutoRetainerReflectionTarget target, out error) || target == null)
		{
			return AutoRetainerReflectionReadResult.Fail(error);
		}
		if (!TryBuildTransaction(target, request, out ReflectionTransaction transaction, out error) || !(transaction != null))
		{
			return AutoRetainerReflectionReadResult.Fail(error);
		}
		return new AutoRetainerReflectionReadResult(Success: true, string.Empty, transaction.Snapshot);
	}

	internal AutoRetainerReflectionApplyResult Apply(AutoRetainerReflectionRequest request)
	{
		if (!ValidateRequest(request, out string error))
		{
			return AutoRetainerReflectionApplyResult.Fail(error);
		}
		if (!TryResolveTarget(out AutoRetainerReflectionTarget target, out error) || target == null)
		{
			return AutoRetainerReflectionApplyResult.Fail(error);
		}
		if (!TryBuildTransaction(target, request, out ReflectionTransaction transaction, out error) || transaction == null)
		{
			return AutoRetainerReflectionApplyResult.Fail(error);
		}
		List<ReflectionMutation> list = new List<ReflectionMutation>();
		int saveCalls = 0;
		try
		{
			foreach (ReflectionMutation mutation in transaction.Mutations)
			{
				try
				{
					mutation.Apply();
					list.Add(mutation);
				}
				catch
				{
					mutation.Rollback();
					throw;
				}
			}
			if (!TryBuildTransaction(target, request, out ReflectionTransaction transaction2, out error) || transaction2?.Snapshot == null || !RequestedStateIsReady(request, transaction2.Snapshot, out error))
			{
				Rollback(list);
				return AutoRetainerReflectionApplyResult.Fail("AutoRetainer reflected application verification failed: " + error);
			}
			if (list.Count == 0)
			{
				return new AutoRetainerReflectionApplyResult(Success: true, Changed: false, 0, string.Empty, transaction2.Snapshot);
			}
			saveCalls++;
			target.Save();
			if (target.ReadEzConfig() != transaction.Config)
			{
				throw new InvalidOperationException("AutoRetainer EzConfig.Config changed during the transaction.");
			}
			if (!TryBuildTransaction(target, request, out ReflectionTransaction transaction3, out error) || transaction3?.Snapshot == null || !RequestedStateIsReady(request, transaction3.Snapshot, out error))
			{
				Rollback(list);
				TrySaveRollback(target, transaction.Config, ref saveCalls);
				return AutoRetainerReflectionApplyResult.Fail("AutoRetainer reflected persistence reread failed: " + error, saveCalls);
			}
			return new AutoRetainerReflectionApplyResult(Success: true, Changed: true, saveCalls, string.Empty, transaction3.Snapshot);
		}
		catch (Exception ex)
		{
			Rollback(list);
			if (saveCalls > 0)
			{
				TrySaveRollback(target, transaction.Config, ref saveCalls);
			}
			return AutoRetainerReflectionApplyResult.Fail("AutoRetainer reflected transaction failed and was rolled back: " + Unwrap(ex).Message, saveCalls);
		}
	}

	private bool TryResolveTarget(out AutoRetainerReflectionTarget? target, out string error)
	{
		try
		{
			target = resolveTarget();
			if (target?.Plugin == null || target.LoadContext == null)
			{
				error = "The current AutoRetainer plugin instance/load context is unavailable.";
				return false;
			}
			if (AssemblyLoadContext.GetLoadContext(target.Plugin.GetType().Assembly) != target.LoadContext)
			{
				error = "The resolved AutoRetainer plugin instance belongs to a different load context.";
				return false;
			}
			error = string.Empty;
			return true;
		}
		catch (Exception ex)
		{
			target = null;
			error = "The current AutoRetainer plugin/load context could not be resolved: " + Unwrap(ex).Message;
			return false;
		}
	}

	private static bool TryOpenConfig(AutoRetainerReflectionTarget target, out object? config, out ConfigFields? fields, out string error)
	{
		config = null;
		fields = null;
		Type type = target.Plugin.GetType();
		if (!string.Equals(type.FullName, "AutoRetainer.AutoRetainer", StringComparison.Ordinal))
		{
			error = "Unexpected AutoRetainer plugin type '" + type.FullName + "'.";
			return false;
		}
		FieldInfo field = type.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic);
		if (field == null || !string.Equals(field.FieldType.FullName, "AutoRetainer.PluginData.Config", StringComparison.Ordinal))
		{
			error = "AutoRetainer private live config field is unavailable or renamed.";
			return false;
		}
		config = field.GetValue(target.Plugin);
		if (config == null || config != target.ReadEzConfig())
		{
			error = "AutoRetainer EzConfig.Config is not the same private live config instance.";
			return false;
		}
		if (!TryField(config.GetType(), "OfflineData", out FieldInfo field2, out error) || !TryField(config.GetType(), "AdditionalData", out FieldInfo field3, out error) || !TryField(config.GetType(), "SelectedRetainers", out FieldInfo field4, out error))
		{
			return false;
		}
		if (!(field2.GetValue(config) is IList) || !(field3.GetValue(config) is IDictionary) || !(field4.GetValue(config) is IDictionary))
		{
			error = "AutoRetainer config collection fields do not expose the expected mutable shapes.";
			return false;
		}
		fields = new ConfigFields(field2, field3, field4);
		error = string.Empty;
		return true;
	}

	private static bool TryBuildTransaction(AutoRetainerReflectionTarget target, AutoRetainerReflectionRequest request, out ReflectionTransaction? transaction, out string error)
	{
		transaction = null;
		if (!TryOpenConfig(target, out object config, out ConfigFields fields, out error) || config == null || fields == null)
		{
			return false;
		}
		if (!TryReadOwner(fields.OfflineData.GetValue(config), request.ContentId, request.CharacterKey, request.Retainers, out ReflectedOwner owner, out error) || owner == null)
		{
			return false;
		}
		IDictionary additionalData = (IDictionary)fields.AdditionalData.GetValue(config);
		IDictionary selectedRetainers = (IDictionary)fields.SelectedRetainers.GetValue(config);
		if (!TryGetDictionaryTypes(fields.AdditionalData.FieldType, typeof(string), out Type valueType, out error) || valueType == null || !string.Equals(valueType.FullName, "AutoRetainerAPI.Configuration.AdditionalRetainerData", StringComparison.Ordinal))
		{
			error = (string.IsNullOrEmpty(error) ? "AutoRetainer AdditionalData dictionary value type is unavailable or changed." : error);
			return false;
		}
		if (!TryAdditionalFields(valueType, out AdditionalFields fields2, out error) || fields2 == null)
		{
			return false;
		}
		if (!TryGetDictionaryTypes(fields.SelectedRetainers.FieldType, typeof(ulong), out Type valueType2, out error) || valueType2 == null || !TrySelectedSetMethods(valueType2, out MethodInfo _, out MethodInfo _, out MethodInfo _, out error))
		{
			return false;
		}
		List<ReflectionMutation> list = new List<ReflectionMutation>();
		bool flag = true;
		foreach (AutoRetainerExpectedRetainer retainer in request.Retainers)
		{
			string key = AdditionalDataKey(request.ContentId, retainer.Name);
			object obj = (additionalData.Contains(key) ? additionalData[key] : null);
			if (obj != null && obj.GetType() != valueType)
			{
				error = "AutoRetainer AdditionalData for " + retainer.Name + " has an unexpected runtime type.";
				return false;
			}
			bool flag2 = obj != null && PlanIsReady(obj, fields2, request.Type, out error);
			if (!string.IsNullOrEmpty(error))
			{
				return false;
			}
			flag = flag && flag2;
			if (!request.AttachStarterPlan || flag2)
			{
				continue;
			}
			if (obj == null)
			{
				obj = Activator.CreateInstance(valueType);
				if (obj == null || !ConfigureDetachedPlan(obj, fields2, request.Type, out error))
				{
					return false;
				}
				object created = obj;
				list.Add(new ReflectionMutation(delegate
				{
					additionalData[key] = created;
				}, delegate
				{
					if (additionalData.Contains(key) && additionalData[key] == created)
					{
						additionalData.Remove(key);
					}
				}));
			}
			else
			{
				if (!TryCreatePlanMutation(obj, fields2, request.Type, out ReflectionMutation mutation, out error) || mutation == null)
				{
					return false;
				}
				list.Add(mutation);
			}
		}
		FieldInfo enabledField = owner.Fields.Enabled;
		bool ownerEnabled = (bool)enabledField.GetValue(owner.Value);
		if (request.EnableCharacter && !ownerEnabled)
		{
			list.Add(new ReflectionMutation(delegate
			{
				enabledField.SetValue(owner.Value, true);
			}, delegate
			{
				enabledField.SetValue(owner.Value, ownerEnabled);
			}));
		}
		if (!TryReadSelection(selectedRetainers, valueType2, request.ContentId, request.Retainers.Select((AutoRetainerExpectedRetainer x) => x.Name), out var selected, out error))
		{
			return false;
		}
		if (request.EnableRetainers && !selected)
		{
			if (!TryCreateSelectionMutation(selectedRetainers, valueType2, request.ContentId, request.Retainers.Select((AutoRetainerExpectedRetainer x) => x.Name), out ReflectionMutation mutation2, out error) || mutation2 == null)
			{
				return false;
			}
			list.Add(mutation2);
		}
		transaction = new ReflectionTransaction(config, CreateDetachedSnapshot(owner, flag, selected), list);
		error = string.Empty;
		return true;
	}

	private static bool TryReadOwner(object? rawOfflineData, ulong contentId, string? expectedCharacterKey, IReadOnlyList<AutoRetainerExpectedRetainer>? expectedRetainers, out ReflectedOwner? owner, out string error)
	{
		owner = null;
		error = string.Empty;
		if (!(rawOfflineData is IList list))
		{
			error = "AutoRetainer OfflineData is unavailable.";
			return false;
		}
		List<object> list2 = new List<object>();
		OfflineFields offlineFields = null;
		foreach (object item in list)
		{
			if (item == null || !string.Equals(item.GetType().FullName, "AutoRetainerAPI.Configuration.OfflineCharacterData", StringComparison.Ordinal) || !TryOfflineFields(item.GetType(), out OfflineFields fields, out error) || fields == null)
			{
				return false;
			}
			if (Convert.ToUInt64(fields.ContentId.GetValue(item)) == contentId)
			{
				list2.Add(item);
				offlineFields = fields;
			}
		}
		if (list2.Count != 1 || offlineFields == null)
		{
			error = ((list2.Count == 0) ? "AutoRetainer OfflineData does not contain the exact ContentId." : "AutoRetainer OfflineData contains duplicate entries for the exact ContentId.");
			return false;
		}
		object obj = list2[0];
		string text = offlineFields.Name.GetValue(obj)?.ToString() ?? string.Empty;
		string text2 = offlineFields.World.GetValue(obj)?.ToString() ?? string.Empty;
		if (!string.IsNullOrEmpty(expectedCharacterKey) && !string.Equals(text + "@" + text2, expectedCharacterKey, StringComparison.OrdinalIgnoreCase))
		{
			error = "AutoRetainer OfflineData returned a different character identity.";
			return false;
		}
		if (!TryReadRetainers(offlineFields.RetainerData.GetValue(obj), out IReadOnlyList<AutoRetainerOfflineRetainer> retainers, out error))
		{
			return false;
		}
		if (expectedRetainers != null && !ExactRetainersMatch(expectedRetainers, retainers, out error))
		{
			return false;
		}
		IReadOnlyList<int> classJobLevels = ReadLevels(offlineFields.ClassJobLevelArray.GetValue(obj));
		uint grandCompanyRank = Convert.ToUInt32(offlineFields.GrandCompanyRank.GetValue(obj));
		owner = new ReflectedOwner(obj, offlineFields, contentId, text, text2, grandCompanyRank, classJobLevels, retainers);
		error = string.Empty;
		return true;
	}

	private static bool TryReadRetainers(object? rawRetainers, out IReadOnlyList<AutoRetainerOfflineRetainer> retainers, out string error)
	{
		List<AutoRetainerOfflineRetainer> list = new List<AutoRetainerOfflineRetainer>();
		error = string.Empty;
		if (!(rawRetainers is IList list2))
		{
			retainers = list;
			error = "AutoRetainer RetainerData is unavailable.";
			return false;
		}
		foreach (object item in list2)
		{
			if (item == null || !string.Equals(item.GetType().FullName, "AutoRetainerAPI.Configuration.OfflineRetainerData", StringComparison.Ordinal) || !TryRetainerFields(item.GetType(), out RetainerFields fields, out error) || fields == null)
			{
				retainers = list;
				return false;
			}
			list.Add(new AutoRetainerOfflineRetainer(Convert.ToUInt64(fields.RetainerId.GetValue(item)), fields.Name.GetValue(item)?.ToString() ?? string.Empty, Convert.ToBoolean(fields.HasVenture.GetValue(item)), Convert.ToUInt32(fields.VentureId.GetValue(item)), Convert.ToInt64(fields.VentureEndsAt.GetValue(item)), Convert.ToInt32(fields.Level.GetValue(item)), Convert.ToUInt32(fields.Job.GetValue(item))));
		}
		retainers = list;
		error = string.Empty;
		return true;
	}

	private static bool ExactRetainersMatch(IReadOnlyList<AutoRetainerExpectedRetainer> expected, IReadOnlyList<AutoRetainerOfflineRetainer> actual, out string error)
	{
		if (actual.Count != expected.Count)
		{
			error = $"AutoRetainer returned {actual.Count} retainers; {expected.Count} exact retainers were required.";
			return false;
		}
		if (actual.Any((AutoRetainerOfflineRetainer x) => x.RetainerId == 0L || string.IsNullOrWhiteSpace(x.Name)) || actual.Select((AutoRetainerOfflineRetainer x) => x.RetainerId).Distinct().Count() != actual.Count || actual.Select((AutoRetainerOfflineRetainer x) => x.Name).Distinct<string>(StringComparer.OrdinalIgnoreCase).Count() != actual.Count)
		{
			error = "AutoRetainer returned duplicate or invalid retainer identity data.";
			return false;
		}
		foreach (AutoRetainerExpectedRetainer item in expected)
		{
			if (!actual.Any((AutoRetainerOfflineRetainer x) => x.RetainerId == item.RetainerId && string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
			{
				error = "AutoRetainer did not return exact retainer " + item.Name + ".";
				return false;
			}
		}
		error = string.Empty;
		return true;
	}

	private static bool PlanIsReady(object additional, AdditionalFields fields, RetainerType type, out string error)
	{
		error = string.Empty;
		object value = fields.VenturePlan.GetValue(additional);
		if (value == null || value.GetType() != fields.VenturePlan.FieldType)
		{
			error = "AutoRetainer VenturePlan live value is unavailable or changed.";
			return false;
		}
		if (!(fields.PlanList.GetValue(value) is IList list))
		{
			error = "AutoRetainer VenturePlan.List live value is unavailable.";
			return false;
		}
		(uint, uint) tuple = AutoRetainerStarterPlans.Get(type);
		List<(uint, int)> list2 = new List<(uint, int)>();
		foreach (object item in list)
		{
			if (item == null || item.GetType() != fields.PlannedVentureType)
			{
				error = "AutoRetainer planned venture has an unexpected runtime type.";
				return false;
			}
			list2.Add((Convert.ToUInt32(fields.PlannedVentureId.GetValue(item)), Convert.ToInt32(fields.PlannedVentureNum.GetValue(item))));
		}
		error = string.Empty;
		if (Convert.ToUInt32(fields.VenturePlanIndex.GetValue(additional)) == 0 && Convert.ToBoolean(fields.EnablePlanner.GetValue(additional)) && string.Equals(fields.PlanCompleteBehavior.GetValue(value)?.ToString(), "Assign_Quick_Venture", StringComparison.Ordinal))
		{
			return list2.SequenceEqual(new(uint, int)[2]
			{
				(tuple.Item1, 1),
				(tuple.Item2, 1)
			});
		}
		return false;
	}

	private static bool ConfigureDetachedPlan(object additional, AdditionalFields fields, RetainerType type, out string error)
	{
		error = string.Empty;
		object obj = fields.VenturePlan.GetValue(additional);
		if (obj == null)
		{
			obj = Activator.CreateInstance(fields.VenturePlan.FieldType);
			if (obj == null)
			{
				error = "AutoRetainer VenturePlan could not be created.";
				return false;
			}
			fields.VenturePlan.SetValue(additional, obj);
		}
		if (!(fields.PlanList.GetValue(obj) is IList { IsReadOnly: false, IsFixedSize: false } list) || !TryCreatePlanItems(fields, type, out object[] items, out error))
		{
			return false;
		}
		list.Clear();
		object[] array = items;
		foreach (object value in array)
		{
			list.Add(value);
		}
		fields.VenturePlanIndex.SetValue(additional, 0u);
		fields.EnablePlanner.SetValue(additional, true);
		fields.PlanCompleteBehavior.SetValue(obj, Enum.Parse(fields.PlanCompleteBehavior.FieldType, "Assign_Quick_Venture", ignoreCase: false));
		error = string.Empty;
		return true;
	}

	private static bool TryCreatePlanMutation(object additional, AdditionalFields fields, RetainerType type, out ReflectionMutation? mutation, out string error)
	{
		mutation = null;
		error = string.Empty;
		object plan = fields.VenturePlan.GetValue(additional);
		if (plan != null)
		{
			object value = fields.PlanList.GetValue(plan);
			IList list = value as IList;
			if (list != null && !list.IsReadOnly && !list.IsFixedSize && TryCreatePlanItems(fields, type, out object[] items, out error))
			{
				object[] originalItems = list.Cast<object>().ToArray();
				object originalIndex = fields.VenturePlanIndex.GetValue(additional);
				object originalEnabled = fields.EnablePlanner.GetValue(additional);
				object originalBehavior = fields.PlanCompleteBehavior.GetValue(plan);
				object behavior = Enum.Parse(fields.PlanCompleteBehavior.FieldType, "Assign_Quick_Venture", ignoreCase: false);
				mutation = new ReflectionMutation(delegate
				{
					list.Clear();
					object[] array = items;
					foreach (object value2 in array)
					{
						list.Add(value2);
					}
					fields.VenturePlanIndex.SetValue(additional, 0u);
					fields.EnablePlanner.SetValue(additional, true);
					fields.PlanCompleteBehavior.SetValue(plan, behavior);
				}, delegate
				{
					list.Clear();
					object[] array = originalItems;
					foreach (object value2 in array)
					{
						list.Add(value2);
					}
					fields.VenturePlanIndex.SetValue(additional, originalIndex);
					fields.EnablePlanner.SetValue(additional, originalEnabled);
					fields.PlanCompleteBehavior.SetValue(plan, originalBehavior);
				});
				error = string.Empty;
				return true;
			}
		}
		error = (string.IsNullOrEmpty(error) ? "AutoRetainer live VenturePlan is not mutable." : error);
		return false;
	}

	private static bool TryCreatePlanItems(AdditionalFields fields, RetainerType type, out object[] items, out string error)
	{
		try
		{
			(uint, uint) tuple = AutoRetainerStarterPlans.Get(type);
			items = new object[2]
			{
				CreatePlanItem(fields, tuple.Item1),
				CreatePlanItem(fields, tuple.Item2)
			};
			error = string.Empty;
			return true;
		}
		catch (Exception ex)
		{
			items = Array.Empty<object>();
			error = "AutoRetainer planned venture could not be staged: " + Unwrap(ex).Message;
			return false;
		}
	}

	private static object CreatePlanItem(AdditionalFields fields, uint id)
	{
		object obj = Activator.CreateInstance(fields.PlannedVentureType) ?? throw new InvalidOperationException("PlannedVenture constructor returned null.");
		fields.PlannedVentureId.SetValue(obj, id);
		fields.PlannedVentureNum.SetValue(obj, 1);
		return obj;
	}

	private static bool TryReadSelection(IDictionary selectedRetainers, Type valueType, ulong contentId, IEnumerable<string> expectedNames, out bool selected, out string error)
	{
		selected = false;
		error = string.Empty;
		if (!selectedRetainers.Contains(contentId))
		{
			error = string.Empty;
			return true;
		}
		object set = selectedRetainers[contentId];
		if (set == null || set.GetType() != valueType || !TrySelectedSetMethods(valueType, out MethodInfo _, out MethodInfo contains, out MethodInfo _, out error) || contains == null)
		{
			return false;
		}
		selected = expectedNames.All(delegate(string name)
		{
			object obj = contains.Invoke(set, new object[1] { name });
			return obj is bool && (bool)obj;
		});
		error = string.Empty;
		return true;
	}

	private static bool TryCreateSelectionMutation(IDictionary selectedRetainers, Type valueType, ulong contentId, IEnumerable<string> expectedNames, out ReflectionMutation? mutation, out string error)
	{
		mutation = null;
		if (!TrySelectedSetMethods(valueType, out MethodInfo add, out MethodInfo _, out MethodInfo clear, out error) || add == null || clear == null)
		{
			return false;
		}
		string[] names = expectedNames.ToArray();
		if (!selectedRetainers.Contains(contentId))
		{
			object created = Activator.CreateInstance(valueType);
			if (created == null)
			{
				error = "AutoRetainer selected-retainer set could not be created.";
				return false;
			}
			string[] array = names;
			foreach (string text in array)
			{
				add.Invoke(created, new object[1] { text });
			}
			mutation = new ReflectionMutation(delegate
			{
				selectedRetainers[contentId] = created;
			}, delegate
			{
				if (selectedRetainers.Contains(contentId) && selectedRetainers[contentId] == created)
				{
					selectedRetainers.Remove(contentId);
				}
			});
			error = string.Empty;
			return true;
		}
		object existing = selectedRetainers[contentId];
		if (existing == null || existing.GetType() != valueType || !(existing is IEnumerable source))
		{
			error = "AutoRetainer selected-retainer set has an unexpected live shape.";
			return false;
		}
		string[] original = (from object x in source
			select x?.ToString() ?? string.Empty).ToArray();
		mutation = new ReflectionMutation(delegate
		{
			string[] array2 = names;
			foreach (string text2 in array2)
			{
				add.Invoke(existing, new object[1] { text2 });
			}
		}, delegate
		{
			clear.Invoke(existing, Array.Empty<object>());
			string[] array2 = original;
			foreach (string text2 in array2)
			{
				add.Invoke(existing, new object[1] { text2 });
			}
		});
		error = string.Empty;
		return true;
	}

	private static bool TryOfflineFields(Type type, out OfflineFields? fields, out string error)
	{
		fields = null;
		if (!TryField(type, "CID", out FieldInfo field, out error) || !TryField(type, "Name", out FieldInfo field2, out error) || !TryField(type, "World", out FieldInfo field3, out error) || !TryField(type, "Enabled", out FieldInfo field4, out error) || !TryField(type, "RetainerData", out FieldInfo field5, out error) || !TryField(type, "ClassJobLevelArray", out FieldInfo field6, out error) || !TryField(type, "GCRank", out FieldInfo field7, out error))
		{
			return false;
		}
		fields = new OfflineFields(field, field2, field3, field4, field5, field6, field7);
		return true;
	}

	private static bool TryRetainerFields(Type type, out RetainerFields? fields, out string error)
	{
		fields = null;
		if (!TryField(type, "RetainerID", out FieldInfo field, out error) || !TryField(type, "Name", out FieldInfo field2, out error) || !TryField(type, "HasVenture", out FieldInfo field3, out error) || !TryField(type, "VentureID", out FieldInfo field4, out error) || !TryField(type, "VentureEndsAt", out FieldInfo field5, out error) || !TryField(type, "Level", out FieldInfo field6, out error) || !TryField(type, "Job", out FieldInfo field7, out error))
		{
			return false;
		}
		fields = new RetainerFields(field, field2, field3, field4, field5, field6, field7);
		return true;
	}

	private static bool TryAdditionalFields(Type type, out AdditionalFields? fields, out string error)
	{
		fields = null;
		if (!TryField(type, "VenturePlan", out FieldInfo field, out error) || !TryField(type, "VenturePlanIndex", out FieldInfo field2, out error) || !TryField(type, "EnablePlanner", out FieldInfo field3, out error) || !string.Equals(type.FullName, "AutoRetainerAPI.Configuration.AdditionalRetainerData", StringComparison.Ordinal) || !string.Equals(field.FieldType.FullName, "AutoRetainerAPI.Configuration.VenturePlan", StringComparison.Ordinal) || !TryField(field.FieldType, "List", out FieldInfo field4, out error) || !TryField(field.FieldType, "PlanCompleteBehavior", out FieldInfo field5, out error))
		{
			error = (string.IsNullOrEmpty(error) ? "AutoRetainer AdditionalData/VenturePlan field types changed." : error);
			return false;
		}
		Type type2 = field4.FieldType.GetGenericArguments().SingleOrDefault();
		if (type2 == null || !string.Equals(type2.FullName, "AutoRetainerAPI.Configuration.PlannedVenture", StringComparison.Ordinal) || type2.GetConstructor(Type.EmptyTypes) == null || !TryField(type2, "ID", out FieldInfo field6, out error) || !TryField(type2, "Num", out FieldInfo field7, out error) || !field5.FieldType.IsEnum || Enum.GetNames(field5.FieldType).All((string x) => x != "Assign_Quick_Venture"))
		{
			error = (string.IsNullOrEmpty(error) ? "AutoRetainer PlannedVenture or PlanCompleteBehavior shape changed." : error);
			return false;
		}
		fields = new AdditionalFields(field, field2, field3, field4, field5, type2, field6, field7);
		return true;
	}

	private static bool TrySelectedSetMethods(Type type, out MethodInfo? add, out MethodInfo? contains, out MethodInfo? clear, out string error)
	{
		add = type.GetMethod("Add", new Type[1] { typeof(string) });
		contains = type.GetMethod("Contains", new Type[1] { typeof(string) });
		clear = type.GetMethod("Clear", Type.EmptyTypes);
		if (add == null || contains == null || clear == null || !typeof(IEnumerable).IsAssignableFrom(type))
		{
			error = "AutoRetainer SelectedRetainers value type is not a mutable string set.";
			return false;
		}
		error = string.Empty;
		return true;
	}

	private static bool TryGetDictionaryTypes(Type dictionaryType, Type expectedKey, out Type? valueType, out string error)
	{
		valueType = null;
		Type[] genericArguments = dictionaryType.GetGenericArguments();
		if (genericArguments.Length != 2 || genericArguments[0] != expectedKey)
		{
			error = "AutoRetainer config dictionary key type changed.";
			return false;
		}
		valueType = genericArguments[1];
		error = string.Empty;
		return true;
	}

	private static bool TryField(Type type, string name, out FieldInfo field, out string error)
	{
		field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
		if (field == null)
		{
			error = $"Required AutoRetainer field {type.FullName}.{name} is unavailable or renamed.";
			return false;
		}
		if (field.IsInitOnly)
		{
			error = $"Required AutoRetainer field {type.FullName}.{name} is unexpectedly read-only.";
			return false;
		}
		error = string.Empty;
		return true;
	}

	private static bool ValidateRequest(AutoRetainerReflectionRequest request, out string error)
	{
		if (request.ContentId == 0L || string.IsNullOrWhiteSpace(request.CharacterKey) || request.CharacterKey.LastIndexOf('@') <= 0 || request.Retainers == null || request.Retainers.Count == 0 || request.Retainers.Any((AutoRetainerExpectedRetainer x) => x.RetainerId == 0L || string.IsNullOrWhiteSpace(x.Name)) || request.Retainers.Select((AutoRetainerExpectedRetainer x) => x.RetainerId).Distinct().Count() != request.Retainers.Count || request.Retainers.Select((AutoRetainerExpectedRetainer x) => x.Name).Distinct<string>(StringComparer.OrdinalIgnoreCase).Count() != request.Retainers.Count)
		{
			error = "The reflected AutoRetainer request contains invalid or duplicate owner/retainer identity data.";
			return false;
		}
		try
		{
			AutoRetainerStarterPlans.Get(request.Type);
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
		error = string.Empty;
		return true;
	}

	private static bool RequestedStateIsReady(AutoRetainerReflectionRequest request, AutoRetainerCharacterSnapshot snapshot, out string error)
	{
		if (request.AttachStarterPlan && !snapshot.StarterPlansConfigured)
		{
			error = "the exact starter plans were not preserved";
		}
		else if (request.EnableCharacter && !snapshot.Enabled)
		{
			error = "the character was not preserved as enabled";
		}
		else
		{
			if (!request.EnableRetainers || snapshot.ExactRetainersSelected)
			{
				error = string.Empty;
				return true;
			}
			error = "the exact retainer selection was not preserved";
		}
		return false;
	}

	private static AutoRetainerCharacterSnapshot CreateDetachedSnapshot(ReflectedOwner owner, bool plansReady, bool selected)
	{
		return new AutoRetainerCharacterSnapshot(owner.ContentId, owner.Name, owner.HomeWorld, Convert.ToBoolean(owner.Fields.Enabled.GetValue(owner.Value)), owner.GrandCompanyRank, owner.ClassJobLevels.ToArray(), plansReady, selected, owner.Retainers.Select((AutoRetainerOfflineRetainer x) => x._003CClone_003E_0024()).ToArray());
	}

	private static IReadOnlyList<int> ReadLevels(object? value)
	{
		if (!(value is short[] source))
		{
			if (!(value is int[] source2))
			{
				if (!(value is IEnumerable<short> source3))
				{
					if (value is IEnumerable<int> source4)
					{
						return source4.ToArray();
					}
					return Array.Empty<int>();
				}
				return source3.Select((Func<short, int>)((short x) => x)).ToArray();
			}
			return source2.ToArray();
		}
		return ((IEnumerable<short>)source).Select((Func<short, int>)((short x) => x)).ToArray();
	}

	private static string AdditionalDataKey(ulong contentId, string name)
	{
		return $"#{contentId:X16} {name}";
	}

	private static void Rollback(IEnumerable<ReflectionMutation> mutations)
	{
		foreach (ReflectionMutation item in mutations.Reverse())
		{
			try
			{
				item.Rollback();
			}
			catch
			{
			}
		}
	}

	private static void TrySaveRollback(AutoRetainerReflectionTarget target, object expectedConfig, ref int saveCalls)
	{
		try
		{
			if (target.ReadEzConfig() == expectedConfig)
			{
				saveCalls++;
				target.Save();
			}
		}
		catch
		{
		}
	}

	private static Exception Unwrap(Exception ex)
	{
		if (!(ex is TargetInvocationException ex2) || ex.InnerException == null)
		{
			return ex;
		}
		return ex2.InnerException;
	}
}
