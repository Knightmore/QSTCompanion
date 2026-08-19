using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace QuestionableCompanion.Services;

public class QuestDetectionService : IDisposable
{
	private readonly IFramework framework;

	private readonly IPluginLog log;

	private readonly IClientState clientState;

	private readonly HashSet<uint> acceptedQuests = new HashSet<uint>();

	private readonly HashSet<uint> completedQuests = new HashSet<uint>();

	private HashSet<uint> completedQuestCache = new HashSet<uint>();

	private string trackedCharacter = string.Empty;

	private DateTime lastCacheRefresh = DateTime.MinValue;

	private const int CACHE_REFRESH_MINUTES = 5;

	public event Action<uint, string>? QuestAccepted;

	public event Action<uint, string>? QuestCompleted;

	public QuestDetectionService(IFramework framework, IPluginLog log, IClientState clientState)
	{
		this.framework = framework;
		this.log = log;
		this.clientState = clientState;
		framework.Update += OnFrameworkUpdate;
		log.Information("[QuestDetection] Service initialized");
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!clientState.IsLoggedIn)
		{
			return;
		}
		try
		{
			CheckQuestUpdates();
		}
		catch (Exception ex)
		{
			log.Debug("[QuestDetection] Error in framework update: " + ex.Message);
		}
	}

	private unsafe void CheckQuestUpdates()
	{
		IPlayerCharacter localPlayer = Plugin.ObjectTable.LocalPlayer;
		string text = ((localPlayer == null) ? string.Empty : $"{localPlayer.Name}@{localPlayer.HomeWorld.Value.Name}");
		if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, trackedCharacter, StringComparison.OrdinalIgnoreCase))
		{
			acceptedQuests.Clear();
			completedQuests.Clear();
			completedQuestCache.Clear();
			lastCacheRefresh = DateTime.MinValue;
			trackedCharacter = text;
			log.Information("[QuestDetection] Tracking reset for " + trackedCharacter);
		}
		QuestManager* ptr = QuestManager.Instance();
		if (ptr == null)
		{
			log.Debug("[QuestDetection] QuestManager instance is null");
			return;
		}
		try
		{
			Span<QuestWork> normalQuests = ptr->NormalQuests;
			if (normalQuests.Length == 0)
			{
				log.Debug("[QuestDetection] NormalQuests array is empty");
				return;
			}
			int num = Math.Min(normalQuests.Length, 30);
			for (int i = 0; i < num; i++)
			{
				try
				{
					QuestWork questWork = normalQuests[i];
					if (questWork.QuestId != 0)
					{
						uint questId = questWork.QuestId;
						if (!acceptedQuests.Contains(questId) && !IsQuestComplete(questId))
						{
							acceptedQuests.Add(questId);
							string questName = GetQuestName(questId);
							log.Information($"[QuestDetection] Quest Accepted: {questId} - {questName}");
							this.QuestAccepted?.Invoke(questId, questName);
						}
					}
				}
				catch (IndexOutOfRangeException)
				{
					break;
				}
				catch (Exception)
				{
				}
			}
			List<uint> list = new List<uint>();
			foreach (uint acceptedQuest in acceptedQuests)
			{
				if (!completedQuests.Contains(acceptedQuest) && IsQuestComplete(acceptedQuest))
				{
					list.Add(acceptedQuest);
				}
			}
			foreach (uint item in list)
			{
				completedQuests.Add(item);
				string questName2 = GetQuestName(item);
				log.Information($"[QuestDetection] Quest Completed: {item} - {questName2}");
				this.QuestCompleted?.Invoke(item, questName2);
			}
		}
		catch (Exception ex3)
		{
			log.Warning("[QuestDetection] Error accessing quest data: " + ex3.Message);
		}
	}

	private bool IsQuestComplete(uint questId)
	{
		try
		{
			return QuestManager.IsQuestComplete(questId);
		}
		catch
		{
			return false;
		}
	}

	public unsafe bool IsQuestCompletedDirect(uint questId)
	{
		try
		{
			if (QuestManager.Instance() == null)
			{
				log.Warning("[QuestDetection] QuestManager instance not available");
				return false;
			}
			bool flag = QuestManager.IsQuestComplete(questId);
			log.Debug($"[QuestDetection] Quest {questId} completion status: {flag}");
			return flag;
		}
		catch (Exception ex)
		{
			log.Error($"[QuestDetection] Failed to check quest {questId}: {ex.Message}");
			return false;
		}
	}

	public unsafe List<uint> GetAllCompletedQuestIds()
	{
		List<uint> list = new List<uint>();
		try
		{
			if (QuestManager.Instance() == null)
			{
				log.Warning("[QuestDetection] QuestManager instance not available");
				return list;
			}
			log.Information("[QuestDetection] Scanning for completed quests...");
			foreach (var item3 in new List<(uint, uint)>
			{
				(1u, 3000u),
				(65000u, 71000u)
			})
			{
				uint item = item3.Item1;
				uint item2 = item3.Item2;
				for (uint num = item; num <= item2; num++)
				{
					try
					{
						if (QuestManager.IsQuestComplete(num))
						{
							list.Add(num);
						}
					}
					catch
					{
					}
				}
			}
			log.Information($"[QuestDetection] Retrieved {list.Count} completed quests");
		}
		catch (Exception ex)
		{
			log.Error("[QuestDetection] Error while fetching completed quests: " + ex.Message);
		}
		return list;
	}

	public void RefreshQuestCache()
	{
		try
		{
			log.Information("[QuestDetection] Refreshing quest cache...");
			List<uint> allCompletedQuestIds = GetAllCompletedQuestIds();
			completedQuestCache = new HashSet<uint>(allCompletedQuestIds);
			lastCacheRefresh = DateTime.Now;
			log.Information($"[QuestDetection] Quest cache refreshed with {completedQuestCache.Count} completed quests");
		}
		catch (Exception ex)
		{
			log.Error("[QuestDetection] Failed to refresh quest cache: " + ex.Message);
		}
	}

	public bool IsQuestCompletedCached(uint questId)
	{
		if (completedQuestCache.Count == 0 || (DateTime.Now - lastCacheRefresh).TotalMinutes > 5.0)
		{
			RefreshQuestCache();
		}
		return completedQuestCache.Contains(questId);
	}

	private string GetQuestName(uint questId)
	{
		try
		{
			return $"Quest {questId}";
		}
		catch
		{
			return $"Quest {questId}";
		}
	}

	public void ResetTracking()
	{
		acceptedQuests.Clear();
		completedQuests.Clear();
		completedQuestCache.Clear();
		lastCacheRefresh = DateTime.MinValue;
		log.Information("[QuestDetection] Tracking reset");
	}

	public bool IsQuestAccepted(uint questId)
	{
		return acceptedQuests.Contains(questId);
	}

	public bool IsQuestCompleted(uint questId)
	{
		return completedQuests.Contains(questId);
	}

	public void Dispose()
	{
		framework.Update -= OnFrameworkUpdate;
		acceptedQuests.Clear();
		completedQuests.Clear();
		completedQuestCache.Clear();
		log.Information("[QuestDetection] Service disposed");
	}
}
