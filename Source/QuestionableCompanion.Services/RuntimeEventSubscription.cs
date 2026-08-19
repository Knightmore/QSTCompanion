using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

internal sealed class RuntimeEventSubscription : IDisposable
{
	private readonly object target;

	private readonly EventInfo eventInfo;

	private readonly Delegate handler;

	private readonly IPluginLog? log;

	private readonly string label;

	private RuntimeEventSubscription(object target, EventInfo eventInfo, Delegate handler, IPluginLog? log, string label)
	{
		this.target = target;
		this.eventInfo = eventInfo;
		this.handler = handler;
		this.log = log;
		this.label = label;
	}

	public static RuntimeEventSubscription? Subscribe(object target, string eventName, Action callback, IPluginLog? log = null, string? label = null)
	{
		try
		{
			EventInfo eventInfo = target.GetType().GetEvent(eventName);
			Type type = eventInfo?.EventHandlerType;
			MethodInfo methodInfo = type?.GetMethod("Invoke");
			if (eventInfo == null || type == null || methodInfo == null)
			{
				log?.Warning("[" + (label ?? eventName) + "] Event not available; subscription skipped.");
				return null;
			}
			ParameterExpression[] parameters = (from p in methodInfo.GetParameters()
				select Expression.Parameter(p.ParameterType, p.Name ?? "arg")).ToArray();
			InvocationExpression body = Expression.Invoke(Expression.Constant(callback));
			Delegate obj = Expression.Lambda(type, body, parameters).Compile();
			eventInfo.AddEventHandler(target, obj);
			return new RuntimeEventSubscription(target, eventInfo, obj, log, label ?? eventName);
		}
		catch (Exception ex)
		{
			log?.Warning("[" + (label ?? eventName) + "] Failed to subscribe event: " + ex.Message);
			return null;
		}
	}

	public void Dispose()
	{
		try
		{
			eventInfo.RemoveEventHandler(target, handler);
		}
		catch (Exception ex)
		{
			log?.Warning("[" + label + "] Failed to unsubscribe event: " + ex.Message);
		}
	}
}
