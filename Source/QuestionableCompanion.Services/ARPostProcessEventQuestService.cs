using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableCompanion.Services;

public class ARPostProcessEventQuestService : IDisposable
{
	private readonly IDalamudPluginInterface pluginInterface;

	private readonly QuestionableIPC questionableIPC;

	private readonly EventQuestResolver eventQuestResolver;

	private readonly Configuration configuration;

	private readonly IPluginLog log;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly LifestreamIPC lifestreamIPC;

	private ICallGateSubscriber<object>? characterAdditionalTaskSubscriber;

	private ICallGateSubscriber<string, object>? characterPostProcessSubscriber;

	private Action? characterAdditionalTaskHandler;

	private Action<string>? characterPostProcessHandler;

	private bool isProcessingEventQuests;

	private DateTime postProcessStartTime;

	private List<string> currentQuestHierarchy = new List<string>();

	private string currentPluginName = string.Empty;

	private string? savedQuestionablePriority;

	private const string PLUGIN_NAME = "QuestionableCompanion";

	private const string AR_CHARACTER_ADDITIONAL_TASK = "AutoRetainer.OnCharacterAdditionalTask";

	private const string AR_CHARACTER_POST_PROCESS_EVENT = "AutoRetainer.OnCharacterReadyForPostprocess";

	private const string AR_FINISH_CHARACTER_POST_PROCESS = "AutoRetainer.FinishCharacterPostprocessRequest";

	private const string AR_REQUEST_CHARACTER_POST_PROCESS = "AutoRetainer.RequestCharacterPostprocess";

	public ARPostProcessEventQuestService(IDalamudPluginInterface pluginInterface, QuestionableIPC questionableIPC, EventQuestResolver eventQuestResolver, Configuration configuration, IPluginLog log, IFramework framework, ICommandManager commandManager, LifestreamIPC lifestreamIPC)
	{
		this.pluginInterface = pluginInterface;
		this.questionableIPC = questionableIPC;
		this.eventQuestResolver = eventQuestResolver;
		this.configuration = configuration;
		this.log = log;
		this.framework = framework;
		this.commandManager = commandManager;
		this.lifestreamIPC = lifestreamIPC;
		InitializeIPC();
	}

	private void InitializeIPC()
	{
		try
		{
			characterAdditionalTaskSubscriber = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask");
			if (characterAdditionalTaskSubscriber == null)
			{
				return;
			}
			characterAdditionalTaskHandler = delegate
			{
				try
				{
					RegisterWithAutoRetainer();
				}
				catch
				{
				}
			};
			characterAdditionalTaskSubscriber.Subscribe(characterAdditionalTaskHandler);
			characterPostProcessSubscriber = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess");
			if (characterPostProcessSubscriber == null)
			{
				return;
			}
			characterPostProcessHandler = delegate(string pluginName)
			{
				try
				{
					OnARCharacterPostProcessStarted(pluginName);
				}
				catch
				{
				}
			};
			characterPostProcessSubscriber.Subscribe(characterPostProcessHandler);
		}
		catch
		{
		}
	}

	private void RegisterWithAutoRetainer()
	{
		try
		{
			pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess").InvokeAction("QuestionableCompanion");
		}
		catch
		{
		}
	}

	private void OnARCharacterPostProcessStarted(string pluginName)
	{
		try
		{
			if (pluginName != "QuestionableCompanion")
			{
				return;
			}
			if (!configuration.RunEventQuestsOnARPostProcess)
			{
				FinishPostProcess();
				return;
			}
			currentPluginName = pluginName;
			postProcessStartTime = DateTime.Now;
			framework.RunOnTick(async delegate
			{
				try
				{
					await ProcessEventQuestsAsync();
				}
				catch
				{
					FinishPostProcess();
				}
			});
		}
		catch
		{
			try
			{
				FinishPostProcess();
			}
			catch
			{
			}
		}
	}

	private async Task ProcessEventQuestsAsync()
	{
		if (isProcessingEventQuests)
		{
			return;
		}
		isProcessingEventQuests = true;
		bool shouldFinishPostProcess = false;
		try
		{
			_ = 1;
			try
			{
				List<string> detectedEventQuests = DetectActiveEventQuests();
				if (detectedEventQuests.Count == 0)
				{
					shouldFinishPostProcess = true;
					return;
				}
				if (!questionableIPC.TryExportQuestPriority(out string encodedQuestPriority))
				{
					log.Warning("[ARPostProcess] Questionable priority queue could not be saved; skipping event quests");
					shouldFinishPostProcess = true;
					return;
				}
				savedQuestionablePriority = encodedQuestPriority;
				currentQuestHierarchy = new List<string>(detectedEventQuests);
				await ImportEventQuestsForPostProcess(detectedEventQuests);
				await WaitForEventQuestsCompletion(detectedEventQuests);
				shouldFinishPostProcess = true;
			}
			catch
			{
				shouldFinishPostProcess = true;
			}
		}
		finally
		{
			if (shouldFinishPostProcess)
			{
				await RestorePriorityQuests();
				FinishPostProcess();
			}
			isProcessingEventQuests = false;
		}
	}

	private List<string> DetectActiveEventQuests()
	{
		try
		{
			return questionableIPC.GetCurrentlyActiveEventQuests() ?? new List<string>();
		}
		catch
		{
			return new List<string>();
		}
	}

	private async Task ImportEventQuestsForPostProcess(List<string> detectedEventQuests)
	{
		List<string> allQuestsToImport = new List<string>();
		try
		{
			foreach (string detectedEventQuest in detectedEventQuests)
			{
				foreach (string item in await GetQuestHierarchy(detectedEventQuest))
				{
					if (!allQuestsToImport.Contains(item))
					{
						allQuestsToImport.Add(item);
					}
				}
			}
			if (!questionableIPC.IsAvailable)
			{
				return;
			}
			try
			{
				questionableIPC.ClearQuestPriority();
				await Task.Delay(500);
			}
			catch
			{
			}
			foreach (string item2 in allQuestsToImport)
			{
				try
				{
					questionableIPC.AddQuestPriority(item2);
					await Task.Delay(100);
				}
				catch
				{
				}
			}
			await Task.Delay(500);
			try
			{
				await framework.RunOnFrameworkThread(delegate
				{
					commandManager.ProcessCommand("/qst start");
				});
			}
			catch
			{
			}
		}
		catch
		{
			throw;
		}
	}

	private async Task<List<string>> GetQuestHierarchy(string questId)
	{
		List<string> hierarchy = new List<string>();
		HashSet<string> visited = new HashSet<string>();
		await CollectPrerequisitesRecursive(questId, hierarchy, visited);
		return hierarchy;
	}

	private async Task CollectPrerequisitesRecursive(string questId, List<string> hierarchy, HashSet<string> visited)
	{
		if (visited.Contains(questId))
		{
			return;
		}
		visited.Add(questId);
		try
		{
			List<string> list = eventQuestResolver.ResolveEventQuestDependencies(questId);
			if (list.Count > 0)
			{
				foreach (string item in list)
				{
					await CollectPrerequisitesRecursive(item, hierarchy, visited);
				}
			}
			hierarchy.Add(questId);
		}
		catch
		{
			hierarchy.Add(questId);
		}
		await Task.CompletedTask;
	}

	private async Task WaitForEventQuestsCompletion(List<string> originalEventQuests)
	{
		TimeSpan maxWaitTime = TimeSpan.FromMinutes(configuration.EventQuestPostProcessTimeoutMinutes);
		DateTime startTime = DateTime.Now;
		TimeSpan checkInterval = TimeSpan.FromSeconds(2L);
		while (DateTime.Now - startTime < maxWaitTime)
		{
			try
			{
				if (!questionableIPC.IsAvailable)
				{
					await Task.Delay(checkInterval);
					continue;
				}
				bool flag = questionableIPC.IsRunning();
				List<string> currentEventQuests = DetectActiveEventQuests();
				if (originalEventQuests.Where((string q) => currentEventQuests.Contains(q)).ToList().Count == 0)
				{
					if (!flag)
					{
						break;
					}
					try
					{
						await framework.RunOnFrameworkThread(delegate
						{
							commandManager.ProcessCommand("/qst stop");
						});
						await Task.Delay(500);
						break;
					}
					catch
					{
						break;
					}
				}
			}
			catch
			{
			}
			await Task.Delay(checkInterval);
		}
	}

	private async Task RestorePriorityQuests()
	{
		try
		{
			if (savedQuestionablePriority != null)
			{
				if (!questionableIPC.RestoreQuestPriority(savedQuestionablePriority))
				{
					log.Warning("[ARPostProcess] Failed to restore the user's Questionable priority queue");
				}
				else
				{
					savedQuestionablePriority = null;
				}
				await Task.Delay(500);
			}
		}
		catch
		{
		}
	}

	private void FinishPostProcess()
	{
		try
		{
			pluginInterface.GetIpcSubscriber<object>("AutoRetainer.FinishCharacterPostprocessRequest").InvokeAction();
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		try
		{
			if (characterAdditionalTaskHandler != null && characterAdditionalTaskSubscriber != null)
			{
				characterAdditionalTaskSubscriber.Unsubscribe(characterAdditionalTaskHandler);
			}
			if (characterPostProcessHandler != null && characterPostProcessSubscriber != null)
			{
				characterPostProcessSubscriber.Unsubscribe(characterPostProcessHandler);
			}
		}
		catch
		{
		}
	}
}
