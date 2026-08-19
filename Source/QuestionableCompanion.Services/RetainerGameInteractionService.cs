using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using QuestionableCompanion.Models;
using QuestionableCompanion.Utils;

namespace QuestionableCompanion.Services;

public sealed class RetainerGameInteractionService
{
	private sealed record NpcRoute(string Name, uint BaseId, uint AetheryteId, string AetheryteName, uint ArrivalTerritory, uint TargetTerritory, Vector3 Position, Vector3? ZoneTransition);

	private sealed record VocateSelectStringObservation(bool Visible, bool Readable, IReadOnlyList<string> Entries);

	private const string RetainerDeskSheet = "custom/000/CmnDefRetainerDesk_00009";

	private const string RetainerCallSheet = "custom/000/CmnDefRetainerCall_00010";

	private const int PollMilliseconds = 150;

	private const int HenchmanGeneralDelayMilliseconds = 250;

	private const float NpcApproachTolerance = 2.5f;

	private const float NpcInteractionDistance = 6f;

	private const uint VentureTokenItemId = 21072u;

	private static readonly string[] VocateCreationAddons = new string[5] { "_CharaMakeRaceGender", "_CharaMakeTribe", "_CharaMakeFeature", "_CharaMakeProgress", "InputString" };

	private readonly IFramework framework;

	private readonly IClientState clientState;

	private readonly ICondition condition;

	private readonly IPlayerState playerState;

	private readonly IObjectTable objectTable;

	private readonly ITargetManager targetManager;

	private readonly IGameGui gameGui;

	private readonly IAddonLifecycle addonLifecycle;

	private readonly IDataManager dataManager;

	private readonly VNavmeshIPC vnavmesh;

	private readonly LifestreamIPC lifestream;

	private readonly QuestionableIPC questionable;

	private readonly HuntLogAutomationService huntLogs;

	private readonly JobStoneGearsetReconciliationService jobStoneGearsetReconciliation;

	private readonly YesAlreadyIPC yesAlready;

	private bool ownsVocateFlow;

	private bool ownsVendorFlow;

	private bool ownsRetainerList;

	private bool ownsSummoningBellDialogue;

	private bool ownsMovement;

	private bool vocateTalkSkipping;

	private bool restoreYesAlreadyAfterVocate;

	private readonly RetainerVocateUiActionGate vocateUiActionGate = new RetainerVocateUiActionGate();

	private uint ownedShopId;

	public RetainerGameInteractionService(IFramework framework, IClientState clientState, ICondition condition, IPlayerState playerState, IObjectTable objectTable, ITargetManager targetManager, IGameGui gameGui, IAddonLifecycle addonLifecycle, IDataManager dataManager, VNavmeshIPC vnavmesh, LifestreamIPC lifestream, QuestionableIPC questionable, HuntLogAutomationService huntLogs, JobStoneGearsetReconciliationService jobStoneGearsetReconciliation, YesAlreadyIPC yesAlready)
	{
		this.framework = framework;
		this.clientState = clientState;
		this.condition = condition;
		this.playerState = playerState;
		this.objectTable = objectTable;
		this.targetManager = targetManager;
		this.gameGui = gameGui;
		this.addonLifecycle = addonLifecycle;
		this.dataManager = dataManager;
		this.vnavmesh = vnavmesh;
		this.lifestream = lifestream;
		this.questionable = questionable;
		this.huntLogs = huntLogs;
		this.jobStoneGearsetReconciliation = jobStoneGearsetReconciliation;
		this.yesAlready = yesAlready;
	}

	public async System.Threading.Tasks.Task VerifyIdentityAsync(ulong contentId, string characterKey, CancellationToken token, TimeSpan? unavailableTimeout = null)
	{
		RetainerStableIdentityGate gate = new RetainerStableIdentityGate();
		DateTime deadline = DateTime.UtcNow + (unavailableTimeout ?? TimeSpan.FromSeconds(60L));
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			RetainerIdentityObservation retainerIdentityObservation = await framework.RunOnFrameworkThread(() => ObserveIdentityUnsafe(contentId, characterKey));
			RetainerIdentityObservationKind num = gate.Observe(retainerIdentityObservation);
			if (num == RetainerIdentityObservationKind.DefinitiveMismatch)
			{
				throw new RetainerIdentityMismatchException($"Active character stably differs from {characterKey} ({contentId}): {retainerIdentityObservation.Detail}.");
			}
			bool flag = num == RetainerIdentityObservationKind.Exact;
			if (flag)
			{
				flag = await framework.RunOnFrameworkThread((Func<bool>)IsCallbackSafeStateAvailableUnsafe);
			}
			if (flag)
			{
				return;
			}
			await System.Threading.Tasks.Task.Delay(150, token);
		}
		throw new TimeoutException($"Exact identity {characterKey} ({contentId}) did not become available for four stable reads.");
	}

	internal bool TryPrepareRecoveryDependencies()
	{
		bool num = questionable.TryEnsureAvailableSilent();
		bool ready;
		bool busy;
		bool flag = vnavmesh.TryGetActivity(out ready, out busy) && ready;
		return num && flag;
	}

	internal bool TryPrepareCleanupDependencies()
	{
		bool ready;
		bool busy;
		return vnavmesh.TryGetActivity(out ready, out busy) && ready;
	}

	internal void StopVocateTalkSkippingForDisposal()
	{
		ownsVocateFlow = false;
		DisableVocateTalkSkipping();
	}

	internal Task<RetainerRecoveryRuntimeObservation> ObserveRecoveryRuntimeAsync(ulong contentId, string characterKey)
	{
		return framework.RunOnFrameworkThread(delegate
		{
			RetainerIdentityObservation identity = ObserveIdentityUnsafe(contentId, characterKey);
			(ulong, string) tuple = ReadObservedIdentityUnsafe();
			bool flag = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || clientState.TerritoryType == 0;
			return new RetainerRecoveryRuntimeObservation(identity, tuple.Item1, tuple.Item2, flag, !flag && IsCallbackSafeStateAvailableUnsafe());
		});
	}

	internal async Task<bool> ReconcileAndCloseKnownDialogsAfterReloadAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		(bool, bool) obj = await framework.RunOnFrameworkThread(delegate
		{
			string[] source = new string[4] { "RetainerCharacter", "RetainerList", "RetainerTaskList", "RetainerTaskAsk" };
			ownsRetainerList = source.Any(IsAddonReadyUnsafe);
			VocateSelectStringObservation vocateSelectStringObservation = ObserveSelectStringUnsafe();
			bool flag = IsOwnedRetainerSelectStringUnsafe(vocateSelectStringObservation);
			bool flag2 = IsOwnedRetainerYesNoUnsafe();
			bool talkVisible = IsAddonVisibleUnsafe("Talk");
			bool flag3 = IsAddonVisibleUnsafe("SelectYesno");
			bool flag4 = IsExactTargetedVocateUnsafe();
			bool flag5 = RetainerVocateRecoveryLogic.CanAdoptLoneTalk(activeRetainerRecovery: true, flag4, talkVisible);
			bool flag6 = flag4 && (vocateSelectStringObservation.Visible || flag3);
			ownsVocateFlow = VocateCreationAddons.Any(IsAddonPresentUnsafe) || flag || flag2 || flag5 || flag6;
			ownsVendorFlow = flag;
			ownedShopId = new uint[3] { 262715u, 262720u, 262730u }.FirstOrDefault(IsExactShopOpenUnsafe);
			ownsMovement = true;
			return (HasKnownDialogs: ownsRetainerList || ownsVocateFlow || ownsVendorFlow || ownedShopId != 0, AdoptedLoneTalk: flag5);
		});
		if (obj.Item2)
		{
			Plugin.Log.Information("[RetainerSetup] Adopted a lone Vocate Talk after reload using active recovery and the exact targeted Vocate base ID.");
		}
		if (obj.Item1)
		{
			await VerifyIdentityAsync(contentId, characterKey, token);
		}
		return await CloseOwnedWindowsAsync(contentId, characterKey, token);
	}

	public async System.Threading.Tasks.Task WaitForSafeStartingStateAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		if (!(await WaitForStableStateAsync(() => IsSafeStartingStateUnsafe() && AreAutomationBackendsIdleUnsafe(requireReadyNavmesh: true), TimeSpan.FromSeconds(60L), contentId, characterKey, token)))
		{
			throw new InvalidOperationException("The character did not reach four stable reads of an exact, safe, idle state.");
		}
	}

	public async Task<RetainerStarterCity> ResolveCityAsync(RetainerStarterCity configured, CancellationToken token)
	{
		if (configured != RetainerStarterCity.Automatic)
		{
			return configured;
		}
		uint townId = await framework.RunOnFrameworkThread(() => playerState.StartTown.RowId);
		token.ThrowIfCancellationRequested();
		return RetainerSetupLogic.ResolveStarterCity(townId);
	}

	public async Task<RetainerEntitlementInfo> ArriveAtVocateAsync(RetainerStarterCity city, ulong contentId, string characterKey, CancellationToken token)
	{
		NpcRoute route = GetVocateRoute(city);
		await NavigateToAsync(route, contentId, characterKey, token);
		await VerifyIdentityAsync(contentId, characterKey, token);
		RetainerEntitlementInfo retainerEntitlementInfo = await framework.RunOnFrameworkThread((Func<RetainerEntitlementInfo>)ReadEntitlementsUnsafe);
		if (RetainerVocateFlowLogic.HasCachedEntitlement(retainerEntitlementInfo.MaximumCount))
		{
			Plugin.Log.Information($"[RetainerSetup] Henchman Vocate flow: using cached native entitlement {retainerEntitlementInfo.CurrentCount}/{retainerEntitlementInfo.MaximumCount} without opening dialogue.");
			return retainerEntitlementInfo;
		}
		ownsVocateFlow = true;
		EnableVocateTalkSkipping();
		try
		{
			if (!(await TargetAndInteractWithVocateAsync(route, contentId, characterKey, TimeSpan.FromSeconds(20L), token)))
			{
				throw new InvalidOperationException("Vocate " + route.Name + " was not reachable at the validated location.");
			}
			string hireText = ReadRawString("custom/000/CmnDefRetainerDesk_00009", 6u);
			if (!(await WaitUntilAsync(delegate
			{
				RetainerEntitlementInfo retainerEntitlementInfo4 = ReadEntitlementsUnsafe();
				if (retainerEntitlementInfo4.MaximumCount > 0 && retainerEntitlementInfo4.MaximumCount == retainerEntitlementInfo4.CurrentCount)
				{
					return true;
				}
				bool hireEntrySelected = SelectVocateEntryUnsafe(hireText, "Hire a Retainer");
				return RetainerVocateFlowLogic.EntitlementWaitCompleted(retainerEntitlementInfo4.CurrentCount, retainerEntitlementInfo4.MaximumCount, hireEntrySelected);
			}, TimeSpan.FromSeconds(20L), contentId, characterKey, token)))
			{
				throw new TimeoutException("Retainer entitlement data or the localized Hire a Retainer entry did not become available.");
			}
			await System.Threading.Tasks.Task.Delay(1000, token);
			RetainerEntitlementInfo retainerEntitlementInfo2 = await framework.RunOnFrameworkThread((Func<RetainerEntitlementInfo>)ReadEntitlementsUnsafe);
			if (retainerEntitlementInfo2.MaximumCount <= 0)
			{
				throw new InvalidOperationException("The Vocate interaction did not populate native retainer entitlement data.");
			}
			if (RetainerVocateFlowLogic.RequiresProbeDecline(retainerEntitlementInfo2.CurrentCount, retainerEntitlementInfo2.MaximumCount))
			{
				string hireConfirmation = ReadRawString("custom/000/CmnDefRetainerDesk_00009", 84u);
				if (!(await WaitUntilAsync(() => ProcessVocateYesNoUnsafe(accept: false, hireConfirmation, "No"), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException("The localized Vocate hire confirmation was unavailable for the entitlement probe.");
				}
			}
			if (!(await WaitForStableVocateClosureAsync(TimeSpan.FromSeconds(20L), contentId, characterKey, token)))
			{
				throw new TimeoutException("The Henchman-style Vocate entitlement interaction remained busy after its final Talk line.");
			}
			RetainerEntitlementInfo retainerEntitlementInfo3 = await framework.RunOnFrameworkThread((Func<RetainerEntitlementInfo>)ReadEntitlementsUnsafe);
			Plugin.Log.Information($"[RetainerSetup] Henchman Vocate flow: native entitlement probe closed at {retainerEntitlementInfo3.CurrentCount}/{retainerEntitlementInfo3.MaximumCount}; no Nothing entry or XADB proof was used.");
			ownsVocateFlow = false;
			return retainerEntitlementInfo3;
		}
		catch
		{
			Plugin.Log.Warning("[RetainerSetup] Henchman Vocate entitlement flow failed; owned cleanup remains bounded by the existing recovery policy.");
			throw;
		}
		finally
		{
			DisableVocateTalkSkipping();
		}
	}

	public async Task<IReadOnlyList<LiveRetainerInfo>> ReadLiveRosterAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		if (!(await WaitUntilAsync(IsLiveRosterReadyUnsafe, TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
		{
			throw new TimeoutException("The live retainer roster did not become readable.");
		}
		return await framework.RunOnFrameworkThread((Func<IReadOnlyList<LiveRetainerInfo>>)ReadLiveRosterUnsafe);
	}

	public async Task<StableNativeRetainerEvidence> ReadStableNativeRosterAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		RetainerStableIdentityGate identityGate = new RetainerStableIdentityGate();
		RetainerNativeRosterGate rosterGate = new RetainerNativeRosterGate();
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20L);
		Array.Empty<LiveRetainerInfo>();
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			(RetainerIdentityObservation, RetainerNativeRosterObservation, IReadOnlyList<LiveRetainerInfo>) tuple = await framework.RunOnFrameworkThread(delegate
			{
				RetainerIdentityObservation item2 = ObserveIdentityUnsafe(contentId, characterKey);
				if (!IsLiveRosterReadyUnsafe())
				{
					return ((RetainerIdentityObservation Identity, RetainerNativeRosterObservation Observation, IReadOnlyList<LiveRetainerInfo> Roster))(Identity: item2, Observation: new RetainerNativeRosterObservation(IsAvailable: false, 0, 0, 0, string.Empty), Roster: Array.Empty<LiveRetainerInfo>());
				}
				RetainerEntitlementInfo retainerEntitlementInfo = ReadEntitlementsUnsafe();
				IReadOnlyList<LiveRetainerInfo> readOnlyList = ReadLiveRosterUnsafe();
				string rosterFingerprint = string.Join('|', from retainer in readOnlyList
					orderby retainer.RetainerId
					select $"{retainer.RetainerId}:{retainer.Name}");
				return (Identity: item2, Observation: new RetainerNativeRosterObservation(IsAvailable: true, retainerEntitlementInfo.CurrentCount, retainerEntitlementInfo.MaximumCount, readOnlyList.Count, rosterFingerprint), Roster: readOnlyList);
			});
			switch (identityGate.Observe(tuple.Item1))
			{
			case RetainerIdentityObservationKind.DefinitiveMismatch:
				throw new RetainerIdentityMismatchException($"Active character stably differs from {characterKey} ({contentId}): {tuple.Item1.Detail}.");
			default:
				rosterGate.Reset();
				await System.Threading.Tasks.Task.Delay(150, token);
				break;
			case RetainerIdentityObservationKind.Exact:
			{
				IReadOnlyList<LiveRetainerInfo> item = tuple.Item3;
				RetainerNativeRosterSnapshot retainerNativeRosterSnapshot = rosterGate.Observe(tuple.Item2);
				if (retainerNativeRosterSnapshot != null)
				{
					return new StableNativeRetainerEvidence(retainerNativeRosterSnapshot, item);
				}
				await System.Threading.Tasks.Task.Delay(150, token);
				break;
			}
			}
		}
		throw new TimeoutException("Native retainer entitlement and roster data did not stabilize for four identical reads.");
	}

	public async Task<RetainerHireResult> HireRetainerAsync(RetainerStarterCity city, RetainerSetupConfiguration settings, string name, ulong contentId, string characterKey, CancellationToken token)
	{
		RetainerNamingSessionResult retainerNamingSessionResult = await HireRetainerSessionAsync(city, settings, new RetainerNamingSession(name, new string[1] { name }), (string _, CancellationToken _) => System.Threading.Tasks.Task.CompletedTask, contentId, characterKey, token);
		RetainerNamingSessionOutcome outcome = retainerNamingSessionResult.Outcome;
		RetainerHireResult result;
		if (outcome != RetainerNamingSessionOutcome.Accepted)
		{
			if (outcome != RetainerNamingSessionOutcome.Exhausted)
			{
				goto IL_011a;
			}
			result = RetainerHireResult.Rejected("The game rejected retainer name " + name);
		}
		else
		{
			if (!(retainerNamingSessionResult.Retainer != null))
			{
				goto IL_011a;
			}
			result = new RetainerHireResult(Success: true, NameRejected: false, retainerNamingSessionResult.Retainer, string.Empty);
		}
		goto IL_0127;
		IL_0127:
		return result;
		IL_011a:
		result = RetainerHireResult.Failed(retainerNamingSessionResult.Error);
		goto IL_0127;
	}

	internal async Task<RetainerNamingSessionResult> HireRetainerSessionAsync(RetainerStarterCity city, RetainerSetupConfiguration settings, RetainerNamingSession session, Func<string, CancellationToken, System.Threading.Tasks.Task> beforeSubmitAsync, ulong contentId, string characterKey, CancellationToken token)
	{
		if (session.Candidates.Count == 0)
		{
			return RetainerNamingSessionResult.Failed("The naming session contained no candidates.");
		}
		ownsVocateFlow = true;
		EnableVocateTalkSkipping();
		try
		{
			return await HireRetainerSessionCoreAsync(city, settings, session, beforeSubmitAsync, contentId, characterKey, token);
		}
		finally
		{
			DisableVocateTalkSkipping();
		}
	}

	private async Task<RetainerNamingSessionResult> HireRetainerSessionCoreAsync(RetainerStarterCity city, RetainerSetupConfiguration settings, RetainerNamingSession session, Func<string, CancellationToken, System.Threading.Tasks.Task> beforeSubmitAsync, ulong contentId, string characterKey, CancellationToken token)
	{
		NpcRoute route = GetVocateRoute(city);
		IReadOnlyList<LiveRetainerInfo> before = await ReadLiveRosterAsync(contentId, characterKey, token);
		if (!(await TargetAndInteractWithVocateAsync(route, contentId, characterKey, TimeSpan.FromSeconds(15L), token)))
		{
			return RetainerNamingSessionResult.Failed("Vocate interaction was unavailable");
		}
		string hireText = ReadRawString("custom/000/CmnDefRetainerDesk_00009", 6u);
		if (!(await WaitUntilAsync(() => SelectVocateEntryUnsafe(hireText, "Hire a Retainer"), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
		{
			return RetainerNamingSessionResult.Failed("Localized Hire a Retainer option was unavailable");
		}
		string savedAppearancePrompt = dataManager.GetExcelSheet<Lobby>().GetRow(2044u).Text.ExtractText();
		if (!(await WaitUntilAsync(() => ProcessHireConfirmationOrObserveAppearanceUnsafe(ReadRawString("custom/000/CmnDefRetainerDesk_00009", 84u), savedAppearancePrompt), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
		{
			return RetainerNamingSessionResult.Failed("Localized retainer-hire confirmation was unavailable");
		}
		int race = ResolveRace(settings.Appearance);
		int gender = ResolveGender(settings.Gender);
		int clan = ResolveClan(settings.Clan);
		bool flag = !(await WaitUntilAsync(() => ContinueAfterSavedAppearancePromptUnsafe(savedAppearancePrompt, () => SelectRaceGenderUnsafe(race + gender)), TimeSpan.FromSeconds(20L), contentId, characterKey, token));
		if (!flag)
		{
			flag = !(await WaitUntilAsync(() => ContinueAfterSavedAppearancePromptUnsafe(savedAppearancePrompt, () => SelectClanUnsafe(clan)), TimeSpan.FromSeconds(20L), contentId, characterKey, token));
		}
		bool flag2 = flag;
		if (!flag2)
		{
			flag2 = !(await WaitUntilAsync(() => ContinueAfterSavedAppearancePromptUnsafe(savedAppearancePrompt, RandomizeAppearanceUnsafe), TimeSpan.FromSeconds(20L), contentId, characterKey, token));
		}
		if (flag2)
		{
			return RetainerNamingSessionResult.Failed("Retainer appearance addons did not match the expected creation flow");
		}
		await System.Threading.Tasks.Task.Delay(500, token);
		if (!(await WaitUntilAsync(() => ContinueAfterSavedAppearancePromptUnsafe(savedAppearancePrompt, FinishAppearanceUnsafe), TimeSpan.FromSeconds(20L), contentId, characterKey, token)))
		{
			return RetainerNamingSessionResult.Failed("Retainer appearance finalization addon did not remain in the expected state");
		}
		string saveAppearancePrompt = dataManager.GetExcelSheet<Lobby>().GetRow(2176u).Text.ExtractText();
		string finalizeAppearancePrompt = dataManager.GetExcelSheet<Lobby>().GetRow(621u).Text.ExtractText();
		if (!(await WaitUntilAsync(() => AdvanceAppearanceFinalizationOrObservePersonalityUnsafe(saveAppearancePrompt, finalizeAppearancePrompt), TimeSpan.FromSeconds(30L), contentId, characterKey, token)))
		{
			return RetainerNamingSessionResult.Failed("Retainer appearance finalization did not reach the personality menu");
		}
		uint personalityRow = ResolvePersonality(settings.Personality);
		string finalHireConfirmation = ReadRawString("custom/000/CmnDefRetainerDesk_00009", 76u);
		DateTime nextPersonalitySelectionAttemptUtc = DateTime.MinValue;
		flag2 = !(await WaitUntilAsync(delegate
		{
			if (IsFinalHireConfirmationOrNameInputVisibleUnsafe(finalHireConfirmation))
			{
				return true;
			}
			if (DateTime.UtcNow < nextPersonalitySelectionAttemptUtc)
			{
				return false;
			}
			if (!SelectRetainerPersonalityUnsafe(personalityRow))
			{
				return false;
			}
			nextPersonalitySelectionAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1L);
			return false;
		}, TimeSpan.FromSeconds(20L), contentId, characterKey, token));
		if (!flag2)
		{
			flag2 = !(await WaitUntilAsync(() => ProcessFinalHireConfirmationOrObserveNameInputUnsafe(finalHireConfirmation), TimeSpan.FromSeconds(15L), contentId, characterKey, token));
		}
		if (flag2)
		{
			return RetainerNamingSessionResult.Failed("Retainer personality flow did not match localized data");
		}
		HashSet<ulong> beforeIds = before.Select((LiveRetainerInfo retainer) => retainer.RetainerId).ToHashSet();
		int submittedCount = 0;
		string localizedNameConfirmation = ReadRawString("custom/000/CmnDefRetainerDesk_00009", 83u);
		for (int candidateIndex = 0; candidateIndex < session.Candidates.Count; candidateIndex++)
		{
			string candidate = session.Candidates[candidateIndex];
			if (!RetainerNameGenerator.IsValidGeneratedName(candidate))
			{
				return RetainerNamingSessionResult.Failed("Naming-session candidate " + candidate + " is invalid.", submittedCount);
			}
			if (!(await WaitUntilAsync(() => IsAddonReadyUnsafe("InputString"), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
			{
				return RetainerNamingSessionResult.Failed("Retainer name input was unavailable.", submittedCount);
			}
			await beforeSubmitAsync(candidate, token);
			if (!(await WaitUntilAsync(() => SubmitRetainerNameUnsafe(candidate), TimeSpan.FromSeconds(5L), contentId, characterKey, token)))
			{
				return RetainerNamingSessionResult.Failed("Retainer name " + candidate + " could not be submitted.", submittedCount);
			}
			submittedCount++;
			Plugin.Log.Information($"[RetainerSetup] Naming session submitted {candidateIndex + 1}/{session.Candidates.Count}: {candidate}.");
			if (!(await WaitUntilAsync(() => ProcessVocateYesNoUnsafe(accept: true, localizedNameConfirmation, "Yes", candidate), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
			{
				return RetainerNamingSessionResult.Failed("Localized Row 83 confirmation was unavailable for " + candidate + ".", submittedCount);
			}
			await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(3L), token);
			(LiveRetainerInfo, bool, bool) tuple = await framework.RunOnFrameworkThread(() => (Accepted: ReadLiveRosterUnsafe().FirstOrDefault((LiveRetainerInfo retainer) => !beforeIds.Contains(retainer.RetainerId) && string.Equals(retainer.Name, candidate, StringComparison.OrdinalIgnoreCase)), InputStringReady: IsAddonReadyUnsafe("InputString"), EventOccupied: IsOccupiedInNpcEventUnsafe()));
			bool finalCandidate = candidateIndex == session.Candidates.Count - 1;
			RetainerNamingAttemptDecision decision = RetainerNamingAttemptLogic.Decide(tuple.Item1 != null, tuple.Item2, tuple.Item3, finalCandidate);
			if (decision == RetainerNamingAttemptDecision.Accepted && tuple.Item1 != null)
			{
				return await CompleteAcceptedNamingSessionAsync(tuple.Item1, submittedCount, contentId, characterKey, token);
			}
			switch (decision)
			{
			case RetainerNamingAttemptDecision.RetrySameEvent:
				break;
			case RetainerNamingAttemptDecision.StructuralFailure:
				return RetainerNamingSessionResult.Failed($"The game neither accepted {candidate} nor produced a consistent naming state (InputString ready={tuple.Item2}, Vocate event occupied={tuple.Item3}).", submittedCount);
			default:
			{
				StableNativeRetainerEvidence stableNativeRetainerEvidence = await ReadStableNativeRosterAsync(contentId, characterKey, token);
				LiveRetainerInfo liveRetainerInfo = stableNativeRetainerEvidence.Roster.FirstOrDefault((LiveRetainerInfo retainer) => !beforeIds.Contains(retainer.RetainerId) && session.Candidates.Contains<string>(retainer.Name, StringComparer.OrdinalIgnoreCase));
				if (liveRetainerInfo != null)
				{
					return await CompleteAcceptedNamingSessionAsync(liveRetainerInfo, submittedCount, contentId, characterKey, token);
				}
				if (!RostersMatch(before, stableNativeRetainerEvidence.Roster))
				{
					return RetainerNamingSessionResult.Failed("The native roster changed after the third naming attempt without matching a submitted candidate.", submittedCount);
				}
				bool flag3;
				try
				{
					flag3 = decision switch
					{
						RetainerNamingAttemptDecision.CloseExhaustedSession => await CloseExhaustedNamingSessionAsync(session, submittedCount, contentId, characterKey, token), 
						RetainerNamingAttemptDecision.VerifyExhaustedSessionClosure => await WaitForStableVocateClosureAsync(TimeSpan.FromSeconds(15L), contentId, characterKey, token), 
						_ => false, 
					};
				}
				catch (Exception ex) when (!(ex is OperationCanceledException))
				{
					return RetainerNamingSessionResult.ClosureUnverified("The exhausted naming session could not verify deliberate closure: " + ex.Message, submittedCount);
				}
				if (!flag3)
				{
					return RetainerNamingSessionResult.ClosureUnverified("The exhausted naming session could not verify four stable closed Vocate reads; no outer cancellation or relog is permitted.", submittedCount);
				}
				ownsVocateFlow = false;
				Plugin.Log.Information($"[RetainerSetup] Naming session exhausted all {submittedCount} candidates; native roster remained unchanged " + "and QST verified the Vocate event closed for four consecutive reads.");
				return RetainerNamingSessionResult.Exhausted(submittedCount);
			}
			}
			Plugin.Log.Information("[RetainerSetup] Game rejected " + candidate + "; InputString returned while the Vocate event remained occupied, so the next candidate will use the same event.");
		}
		return RetainerNamingSessionResult.Failed("The naming session ended without acceptance or a verified three-name exhaustion.", submittedCount);
	}

	internal async System.Threading.Tasks.Task CompleteVentureUnlockQuestAsync(RetainerStarterCity city, uint preferredCombatJobId, ulong contentId, string characterKey, Func<RetainerQuestionablePriorityBackup?> readPriorityBackup, Func<RetainerQuestionablePriorityBackup, CancellationToken, System.Threading.Tasks.Task> persistPriorityBackupAsync, Func<CancellationToken, System.Threading.Tasks.Task> clearPriorityBackupAsync, CancellationToken token)
	{
		RetainerVentureQuestCoordinator retainerVentureQuestCoordinator = new RetainerVentureQuestCoordinator();
		RetainerVentureQuestRuntime runtime = new RetainerVentureQuestRuntime((CancellationToken ct) => VerifyIdentityAsync(contentId, characterKey, ct), async delegate(CancellationToken ct)
		{
			await VerifyIdentityAsync(contentId, characterKey, ct);
			return await framework.RunOnFrameworkThread((Func<byte>)ReadNativeStartTownUnsafe);
		}, (uint rawQuestId, CancellationToken _) => framework.RunOnFrameworkThread(() => QuestManager.IsQuestComplete(rawQuestId)), (uint rawQuestId, CancellationToken _) => framework.RunOnFrameworkThread(() => IsNativeQuestAcceptedUnsafe(rawQuestId)), questionable.TryEnsureAvailableSilent, questionable.IsRunning, questionable.GetCurrentQuestId, async delegate(CancellationToken ct)
		{
			await jobStoneGearsetReconciliation.ReconcileCurrentAsync("retainer venture-quest combat-job selection", ct);
			bool prepared = await huntLogs.PrepareCombatJobForQuestRotationAsync(preferredCombatJobId, ct);
			uint num = await framework.RunOnFrameworkThread(() => playerState.ClassJob.RowId);
			if (preferredCombatJobId != 0 && (!prepared || num != preferredCombatJobId))
			{
				prepared = await huntLogs.PrepareCombatJobForQuestRotationAsync(0u, ct);
			}
			if (prepared)
			{
				await VerifyIdentityAsync(contentId, characterKey, ct);
			}
			return prepared;
		}, (string questId, CancellationToken ct) => PrepareVentureQuestPriorityAsync(questId, contentId, characterKey, readPriorityBackup, persistPriorityBackupAsync, ct), (CancellationToken ct) => RestoreQuestionablePriorityAsync(readPriorityBackup, clearPriorityBackupAsync, ct), questionable.StartSingleQuest, System.Threading.Tasks.Task.Delay, () => DateTime.UtcNow);
		await retainerVentureQuestCoordinator.CompleteAsync(runtime, TimeSpan.FromMinutes(30L), token);
	}

	private async System.Threading.Tasks.Task PrepareVentureQuestPriorityAsync(string questId, ulong contentId, string characterKey, Func<RetainerQuestionablePriorityBackup?> readPriorityBackup, Func<RetainerQuestionablePriorityBackup, CancellationToken, System.Threading.Tasks.Task> persistPriorityBackupAsync, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		if (!questionable.TryEnsureAvailableSilent())
		{
			throw new InvalidOperationException("Questionable is unavailable while preparing the venture priority handoff.");
		}
		RetainerQuestionablePriorityBackup backup = readPriorityBackup();
		if (backup == null)
		{
			if (!questionable.TryExportQuestPriority(out string encodedQuestPriority))
			{
				throw new InvalidOperationException("Questionable does not expose a readable priority snapshot; the venture handoff was not started.");
			}
			backup = new RetainerQuestionablePriorityBackup(encodedQuestPriority, questionable.IsRunning(), questionable.GetCurrentQuestId() ?? string.Empty, questId);
			await persistPriorityBackupAsync(backup, token);
		}
		else if (!string.Equals(backup.IsolatedQuestId, questId, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"Persisted Questionable priority handoff belongs to quest {backup.IsolatedQuestId}, not {questId}.");
		}
		await StopQuestionableForPriorityMutationAsync("isolating the retainer venture quest", token);
		if (!questionable.ClearQuestPriority() || !questionable.AddQuestPriority(questId))
		{
			throw new InvalidOperationException("Questionable priority isolation failed for venture quest " + questId + ".");
		}
		Plugin.Log.Information($"[RetainerSetup] Questionable priority isolated for venture quest {questId}; previousRunning={backup.WasRunning}, previousQuest={backup.PreviousQuestId}.");
	}

	internal async System.Threading.Tasks.Task RestoreQuestionablePriorityAsync(Func<RetainerQuestionablePriorityBackup?> readPriorityBackup, Func<CancellationToken, System.Threading.Tasks.Task> clearPriorityBackupAsync, CancellationToken token)
	{
		RetainerQuestionablePriorityBackup backup = readPriorityBackup();
		if (backup == null)
		{
			return;
		}
		if (!questionable.TryEnsureAvailableSilent())
		{
			throw new InvalidOperationException("Questionable is unavailable while restoring the saved priority list.");
		}
		await StopQuestionableForPriorityMutationAsync("restoring the saved priority list", token);
		if (!questionable.ClearQuestPriority())
		{
			throw new InvalidOperationException("Questionable rejected clearing the temporary venture priority.");
		}
		if (!string.IsNullOrWhiteSpace(backup.EncodedPriority) && !questionable.ImportQuestPriority(backup.EncodedPriority))
		{
			throw new InvalidOperationException("Questionable rejected restoration of the saved priority list.");
		}
		await clearPriorityBackupAsync(token);
		Plugin.Log.Information("[RetainerSetup] Restored the saved Questionable priority list.");
		if (!backup.WasRunning)
		{
			return;
		}
		bool flag = !string.IsNullOrWhiteSpace(backup.PreviousQuestId) && questionable.StartQuest(backup.PreviousQuestId);
		if (!flag)
		{
			flag = await framework.RunOnFrameworkThread(() => Plugin.CommandManager.ProcessCommand("/qst start"));
		}
		if (!flag)
		{
			Plugin.Log.Warning("[RetainerSetup] Saved Questionable priority was restored, but prior automation could not be resumed.");
		}
	}

	private async System.Threading.Tasks.Task StopQuestionableForPriorityMutationAsync(string reason, CancellationToken token)
	{
		if (!questionable.IsRunning())
		{
			return;
		}
		if (!(await framework.RunOnFrameworkThread(() => Plugin.CommandManager.ProcessCommand("/qst stop"))))
		{
			throw new InvalidOperationException("Questionable stop command was rejected while " + reason + ".");
		}
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5L);
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (!questionable.IsRunning())
			{
				return;
			}
			await System.Threading.Tasks.Task.Delay(100, token);
		}
		throw new TimeoutException("Questionable did not stop within 5 seconds while " + reason + ".");
	}

	private unsafe static byte ReadNativeStartTownUnsafe()
	{
		PlayerState* intPtr = PlayerState.Instance();
		if (intPtr == null)
		{
			throw new InvalidOperationException("Native PlayerState is unavailable while selecting the venture quest.");
		}
		return intPtr->StartTown;
	}

	private unsafe static bool IsNativeQuestAcceptedUnsafe(uint rawQuestId)
	{
		QuestManager* ptr = QuestManager.Instance();
		if (ptr != null)
		{
			return ptr->IsQuestAccepted(rawQuestId);
		}
		return false;
	}

	internal async Task<RetainerStarterGearPurchaseResult> PurchaseStarterGearAsync(RetainerStarterCity city, uint classJobId, int desiredOwnedSlotCount, IReadOnlyList<RetainerStarterGearSlotCheckpoint> existingOwnedSlots, ulong contentId, string characterKey, CancellationToken token)
	{
		if (desiredOwnedSlotCount <= 0)
		{
			throw new ArgumentOutOfRangeException("desiredOwnedSlotCount");
		}
		uint itemId = ResolveStarterMainHand(classJobId);
		if (itemId == 0)
		{
			throw new RetainerTerminalCharacterException($"No validated Weathered starter weapon/tool was found for class {classJobId}.");
		}
		uint shopId = ResolveGilShopId(itemId);
		if (shopId == 0)
		{
			throw new RetainerTerminalCharacterException($"No validated gil shop contains starter item {itemId}.");
		}
		uint topicSelectId = ResolveVendorTopicSelectId(shopId);
		if (topicSelectId == 0)
		{
			throw new RetainerTerminalCharacterException($"No validated vendor topic exposes gil shop {shopId}.");
		}
		await VerifyIdentityAsync(contentId, characterKey, token);
		List<RetainerStarterGearSlotCheckpoint> ownedSlots = await framework.RunOnFrameworkThread(() => ValidateOwnedStarterGearSlotsUnsafe(itemId, existingOwnedSlots));
		if (ownedSlots.Count >= desiredOwnedSlotCount)
		{
			return new RetainerStarterGearPurchaseResult(itemId, ownedSlots.Take(desiredOwnedSlotCount).ToArray());
		}
		NpcRoute route = GetVendorRoute(city, IsCombatClass(classJobId));
		await NavigateToAsync(route, contentId, characterKey, token);
		ownsVendorFlow = true;
		if (!(await WaitUntilAsync(() => OpenExactEventHandlerUnsafe(route.BaseId, route.Position, topicSelectId), TimeSpan.FromSeconds(20L), contentId, characterKey, token)))
		{
			throw new InvalidOperationException($"Validated vendor {route.Name} did not expose shop {shopId}.");
		}
		string shopName = dataManager.GetExcelSheet<GilShop>().GetRow(shopId).Name.ExtractText();
		bool flag = !(await SelectLocalizedOptionAsync(shopName, contentId, characterKey, TimeSpan.FromSeconds(15L), token));
		if (!flag)
		{
			flag = !(await WaitUntilAsync(() => IsExactShopOpenUnsafe(shopId), TimeSpan.FromSeconds(15L), contentId, characterKey, token));
		}
		if (flag)
		{
			throw new InvalidOperationException("Validated shop " + shopName + " did not open.");
		}
		ownedShopId = shopId;
		int count = desiredOwnedSlotCount - ownedSlots.Count;
		for (int index = 0; index < count; index++)
		{
			HashSet<(int ContainerType, int Slot)> slotsBefore = await framework.RunOnFrameworkThread(() => (from slot in ReadStarterItemSlotsUnsafe(itemId)
				select (ContainerType: slot.ContainerType, Slot: slot.Slot)).ToHashSet());
			if (!(await WaitUntilAsync(() => BuyItemFromExactShopUnsafe(shopId, itemId, 1), TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
			{
				throw new InvalidOperationException($"Starter item {itemId} was not visible in shop {shopId}.");
			}
			if (!(await WaitUntilAsync(() => !ShopTransactionInProgressUnsafe(shopId), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
			{
				throw new TimeoutException($"Starter item transaction {index + 1}/{count} did not settle.");
			}
			RetainerStarterGearSlotCheckpoint purchasedSlot = null;
			if (!(await WaitUntilAsync(delegate
			{
				purchasedSlot = ReadStarterItemSlotsUnsafe(itemId).FirstOrDefault((RetainerStarterGearSlotCheckpoint slot) => !slotsBefore.Contains((slot.ContainerType, slot.Slot)) && ownedSlots.All((RetainerStarterGearSlotCheckpoint owned) => owned.ContainerType != slot.ContainerType || owned.Slot != slot.Slot));
				return purchasedSlot != null;
			}, TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
			{
				throw new InvalidOperationException($"Starter item {itemId} purchase {index + 1}/{count} did not produce a newly owned inventory slot.");
			}
			ownedSlots.Add(purchasedSlot);
		}
		if (!(await WaitUntilAsync(delegate
		{
			CloseOwnedShopUnsafe();
			return true;
		}, TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
		{
			throw new InvalidOperationException("The guarded shop close callback could not be issued safely.");
		}
		if (!(await WaitUntilAsync(() => !IsExactShopOpenUnsafe(shopId), TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
		{
			throw new InvalidOperationException($"Validated shop {shopId} did not close cleanly.");
		}
		ownedShopId = 0u;
		string cancelText = dataManager.GetExcelSheet<Addon>().GetRow(2u).Text.ExtractText();
		if (!(await WaitUntilAsync(() => !IsAddonReadyUnsafe("SelectString") || SelectStringOptionUnsafe(cancelText), TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
		{
			throw new InvalidOperationException("Validated vendor conversation could not be closed.");
		}
		ownsVendorFlow = false;
		return new RetainerStarterGearPurchaseResult(itemId, ownedSlots.Take(desiredOwnedSlotCount).ToArray());
	}

	public async Task<bool> AssignClassAndGearAsync(IReadOnlyList<TrackedRetainerCheckpoint> retainers, uint classJobId, uint starterItemId, RetainerStarterGearSlotCheckpoint starterGearSlot, ulong contentId, string characterKey, CancellationToken token)
	{
		if (retainers.Count != 1)
		{
			throw new ArgumentException("Exact starter-gear ownership requires one retainer per assignment call.", "retainers");
		}
		if (!IsValidRetainerClass(classJobId))
		{
			throw new RetainerTerminalCharacterException($"Class/job {classJobId} is not a valid retainer starter class.");
		}
		string className = dataManager.GetExcelSheet<ClassJob>().GetRow(classJobId).Name.ExtractText();
		await OpenSummoningBellAsync(contentId, characterKey, token);
		using (IEnumerator<TrackedRetainerCheckpoint> enumerator = retainers.GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				TrackedRetainerCheckpoint expected = enumerator.Current;
				await VerifyIdentityAsync(contentId, characterKey, token);
				LiveRetainerInfo current = (await ReadLiveRosterAsync(contentId, characterKey, token)).FirstOrDefault((LiveRetainerInfo x) => x.RetainerId == expected.RetainerId && string.Equals(x.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
				if (current == null)
				{
					throw new RetainerTerminalCharacterException("Exact tracked retainer " + expected.Name + " is not in the live roster.");
				}
				if (current.ClassJobId != 0 && current.ClassJobId != classJobId)
				{
					throw new RetainerTerminalCharacterException($"Tracked retainer {expected.Name} already has unrelated class {current.ClassJobId}.");
				}
				if (!(await SelectRetainerByNameAsync(expected.Name, contentId, characterKey, token)))
				{
					throw new InvalidOperationException("Exact retainer list entry for " + expected.Name + " was unavailable.");
				}
				bool flag2;
				if (current.ClassJobId == 0)
				{
					bool flag = !(await SelectLocalizedOptionAsync(dataManager.GetExcelSheet<Addon>().GetRow(2391u).Text.ExtractText(), contentId, characterKey, TimeSpan.FromSeconds(15L), token));
					if (!flag)
					{
						flag = !(await SelectLocalizedOptionAsync(className, contentId, characterKey, TimeSpan.FromSeconds(15L), token));
					}
					flag2 = flag;
					if (!flag2)
					{
						flag2 = !(await ConfirmLocalizedYesNoAsync(ReadRawString("custom/000/CmnDefRetainerCall_00010", 208u), yes: true, className, contentId, characterKey, TimeSpan.FromSeconds(15L), token));
					}
					if (flag2)
					{
						throw new InvalidOperationException("Localized class-assignment flow failed for " + expected.Name + ".");
					}
				}
				string standardGearOption = dataManager.GetExcelSheet<Addon>().GetRow(2388u).Text.ExtractText();
				string noMainArmGearOption = dataManager.GetExcelSheet<Addon>().GetRow(2389u).Text.ExtractText();
				if (!(await WaitUntilAsync(() => SelectRetainerGearOptionUnsafe(noMainArmGearOption, standardGearOption), TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException("Localized retainer-gear option was unavailable for " + expected.Name + ".");
				}
				if (!(await WaitForStableStateAsync(IsRetainerGearWindowReadyUnsafe, TimeSpan.FromSeconds(15L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException("Retainer gear window for " + expected.Name + " did not become stably ready before equipping the starter main hand.");
				}
				Plugin.Log.Information("[RetainerSetup] Retainer gear window verified ready for four consecutive reads before equipping the starter main hand.");
				bool starterMoveIssued = false;
				bool usedFallbackSource = false;
				RetainerStarterGearSlotCheckpoint actualMoveSource = null;
				if (!(await WaitUntilAsync(delegate
				{
					if (!IsRetainerGearWindowReadyUnsafe())
					{
						return false;
					}
					if (IsRetainerMainHandUnsafe(starterItemId))
					{
						return true;
					}
					if (starterMoveIssued)
					{
						return false;
					}
					starterMoveIssued = TryMoveStarterItemToRetainerMainHandUnsafe(starterItemId, starterGearSlot, out actualMoveSource, out usedFallbackSource);
					return false;
				}, TimeSpan.FromSeconds(20L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException($"Starter main hand {starterItemId} could not be equipped to {expected.Name}.");
				}
				if (!(await WaitUntilAsync(() => IsRetainerGearWindowReadyUnsafe() && IsRetainerMainHandUnsafe(starterItemId), TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException($"Starter main hand {starterItemId} was not verified on {expected.Name}.");
				}
				if (!(await WaitUntilAsync(() => FireCallbackUnsafe("RetainerCharacter", -1), TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException("Retainer gear window for " + expected.Name + " could not be closed safely.");
				}
				if (!(await WaitUntilAsync(() => ReadLiveRosterUnsafe().Any((LiveRetainerInfo x) => x.RetainerId == expected.RetainerId && x.ClassJobId == classJobId), TimeSpan.FromSeconds(20L), contentId, characterKey, token)))
				{
					throw new InvalidOperationException($"Class {classJobId} was not verified on {expected.Name}.");
				}
				flag2 = !(await SelectLocalizedOptionAsync(dataManager.GetExcelSheet<Addon>().GetRow(917u).Text.ExtractText(), contentId, characterKey, TimeSpan.FromSeconds(15L), token));
				if (!flag2)
				{
					flag2 = !(await WaitUntilAsync(() => IsAddonReadyUnsafe("RetainerList"), TimeSpan.FromSeconds(15L), contentId, characterKey, token));
				}
				if (flag2)
				{
					throw new InvalidOperationException("Retainer session for " + expected.Name + " did not return to the list.");
				}
				if (starterMoveIssued && actualMoveSource != null)
				{
					Plugin.Log.Information(usedFallbackSource ? $"[RetainerSetup] Equipped starter main hand from live fallback slot {actualMoveSource.ContainerType}:{actualMoveSource.Slot}; the persisted source slot had become stale." : $"[RetainerSetup] Equipped starter main hand from checkpoint-owned slot {actualMoveSource.ContainerType}:{actualMoveSource.Slot}.");
				}
				else
				{
					Plugin.Log.Information("[RetainerSetup] Starter main hand was already equipped; no inventory source slot was consumed.");
				}
				return !(await framework.RunOnFrameworkThread(() => IsStarterItemAtSlotUnsafe(starterItemId, starterGearSlot)));
			}
		}
		throw new InvalidOperationException("Exact retainer assignment completed without processing a retainer.");
	}

	public async Task<bool> VerifyLiveFirstVenturesAsync(IEnumerable<TrackedRetainerCheckpoint> expectedRetainers, ulong contentId, string characterKey, CancellationToken token)
	{
		IReadOnlyList<LiveRetainerInfo> roster = await ReadLiveRosterAsync(contentId, characterKey, token);
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		return expectedRetainers.All((TrackedRetainerCheckpoint expected) => roster.Any((LiveRetainerInfo live) => live.RetainerId == expected.RetainerId && string.Equals(live.Name, expected.Name, StringComparison.OrdinalIgnoreCase) && live.VentureId != 0 && (expected.ExpectedFirstVentureId == 0 || live.VentureId == expected.ExpectedFirstVentureId) && live.VentureCompleteUnixSeconds > now));
	}

	internal async Task<uint?> ReadVentureTokenCountAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		return await framework.RunOnFrameworkThread((Func<uint?>)ReadVentureTokenCountUnsafe);
	}

	public async Task<bool> CloseOwnedWindowsAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		if (!ownsRetainerList && !ownsVocateFlow && !ownsVendorFlow && ownedShopId == 0)
		{
			await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
			if (!ownsMovement)
			{
				return true;
			}
			bool num = await WaitForStableVnavmeshIdleAsync(TimeSpan.FromSeconds(10L), token);
			if (num)
			{
				ownsMovement = false;
			}
			return num;
		}
		await VerifyIdentityAsync(contentId, characterKey, token);
		if (ownsVocateFlow)
		{
			if (!(await DismissOwnedVocateAsync(contentId, characterKey, token)))
			{
				return false;
			}
			ownsVocateFlow = false;
		}
		string[] retainerAddons = new string[4] { "RetainerCharacter", "RetainerList", "RetainerTaskList", "RetainerTaskAsk" };
		if (!(await WaitUntilAsync(delegate
		{
			CloseOwnedShopUnsafe();
			if (ownsRetainerList)
			{
				string[] array = retainerAddons;
				foreach (string addonName in array)
				{
					FireCallbackUnsafe(addonName, -1);
				}
			}
			if (ownsVendorFlow && IsOwnedRetainerSelectStringUnsafe())
			{
				SelectStringOptionUnsafe(dataManager.GetExcelSheet<Addon>().GetRow(2u).Text.ExtractText());
			}
			return true;
		}, TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
		{
			return false;
		}
		bool closed = await WaitUntilAsync(() => (!ownsRetainerList || retainerAddons.All((string addon) => !IsAddonReadyUnsafe(addon))) && (!ownsVendorFlow || !IsOwnedRetainerSelectStringUnsafe()) && (ownedShopId == 0 || !IsExactShopOpenUnsafe(ownedShopId)), TimeSpan.FromSeconds(10L), contentId, characterKey, token);
		if (closed)
		{
			ownsRetainerList = false;
			ownsVocateFlow = false;
			ownsVendorFlow = false;
			ownedShopId = 0u;
			await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
			bool flag = ownsMovement;
			if (flag)
			{
				flag = !(await WaitForStableVnavmeshIdleAsync(TimeSpan.FromSeconds(10L), token));
			}
			if (flag)
			{
				return false;
			}
			ownsMovement = false;
		}
		return closed;
	}

	public async System.Threading.Tasks.Task OpenSummoningBellAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		string bellName = dataManager.GetExcelSheet<EObjName>().GetRow(2000072u).Singular.ExtractText();
		(bool, bool, bool) tuple = await framework.RunOnFrameworkThread(() => (ListReady: IsAddonReadyUnsafe("RetainerList"), IndividualReady: IsIndividualRetainerWindowReadyUnsafe(), AtBell: IsAtSummoningBellUnsafe(bellName)));
		if (tuple.Item2)
		{
			throw new InvalidOperationException("An individual-retainer window is open; QST will not adopt it for AutoRetainer startup.");
		}
		if (tuple.Item1)
		{
			if (!tuple.Item3)
			{
				throw new InvalidOperationException("The open retainer list is not anchored at a loaded summoning bell.");
			}
			if (!ownsRetainerList)
			{
				throw new InvalidOperationException("A summoning-bell retainer list is already visible but is not owned by this QST operation.");
			}
			return;
		}
		(bool Found, Vector3 Position, uint Territory) found = await framework.RunOnFrameworkThread(delegate
		{
			IPlayerCharacter player = objectTable.LocalPlayer;
			IGameObject gameObject = (from gameObject2 in objectTable
				where string.Equals(gameObject2.Name.ToString(), bellName, StringComparison.OrdinalIgnoreCase)
				orderby (player != null) ? Vector3.Distance(player.Position, gameObject2.Position) : float.MaxValue
				select gameObject2).FirstOrDefault();
			return (gameObject != null) ? (Found: true, Position: gameObject.Position, Territory: clientState.TerritoryType) : (Found: false, Position: Vector3.Zero, Territory: 0u);
		});
		if (!found.Found)
		{
			throw new InvalidOperationException("No reachable summoning bell was loaded.");
		}
		await MoveToPositionAsync(found.Position, found.Territory, 2.5f, contentId, characterKey, token);
		ownsSummoningBellDialogue = true;
		EnableVocateTalkSkipping();
		try
		{
			bool flag = !(await WaitUntilAsync(() => TryInteractWithNameUnsafe(bellName, found.Position, 4f), TimeSpan.FromSeconds(15L), contentId, characterKey, token));
			if (!flag)
			{
				flag = !(await WaitUntilAsync(() => IsAddonReadyUnsafe("RetainerList"), TimeSpan.FromSeconds(15L), contentId, characterKey, token));
			}
			if (flag)
			{
				throw new InvalidOperationException("Summoning bell did not open the retainer list.");
			}
		}
		finally
		{
			ownsSummoningBellDialogue = false;
			DisableVocateTalkSkipping();
		}
		if (!(await framework.RunOnFrameworkThread(() => IsAtSummoningBellUnsafe(bellName))))
		{
			throw new InvalidOperationException("Summoning bell proximity drifted after the retainer list opened.");
		}
		ownsRetainerList = true;
	}

	internal async Task<RetainerBellMenuReadiness> EnsureOwnedSummoningBellListReadyForAutoRetainerStartAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		string bellName = dataManager.GetExcelSheet<EObjName>().GetRow(2000072u).Singular.ExtractText();
		string lastError = "the retainer list was unavailable";
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			token.ThrowIfCancellationRequested();
			await VerifyIdentityAsync(contentId, characterKey, token);
			(bool, bool, bool, RetainerBellMenuDecision) tuple = await framework.RunOnFrameworkThread(delegate
			{
				bool flag = IsIndividualRetainerWindowReadyUnsafe();
				bool flag2 = IsAddonReadyUnsafe("RetainerList");
				bool flag3 = IsAtSummoningBellUnsafe(bellName);
				return (individualReady: flag, listReady: flag2, atBell: flag3, RetainerBellMenuLogic.Decide(ownsRetainerList, flag2, flag3, flag));
			});
			if (tuple.Item4 == RetainerBellMenuDecision.Ready)
			{
				return RetainerBellMenuReadiness.Ready;
			}
			if (tuple.Item4 == RetainerBellMenuDecision.Block)
			{
				string text = (tuple.Item1 ? "an individual-retainer window is open" : ((tuple.Item2 && !tuple.Item3) ? "the retainer list is no longer anchored at the summoning bell" : "the visible retainer list is not owned by this QST operation"));
				return RetainerBellMenuReadiness.Fail("AutoRetainer start was blocked because " + text + "; /ays e was not issued.");
			}
			ownsRetainerList = false;
			try
			{
				await OpenSummoningBellAsync(contentId, characterKey, token);
				lastError = "the reacquired retainer list did not remain ready";
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex2)
			{
				lastError = ex2.Message;
			}
			if (attempt < 3)
			{
				await System.Threading.Tasks.Task.Delay(250, token);
			}
		}
		return RetainerBellMenuReadiness.Fail($"AutoRetainer start was blocked after {3} bounded summoning-bell reacquisition attempts: {lastError}; /ays e was not issued.");
	}

	private async System.Threading.Tasks.Task NavigateToAsync(NpcRoute route, ulong contentId, string characterKey, CancellationToken token)
	{
		Exception lastFailure = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			try
			{
				await NavigateAttemptAsync(route, contentId, characterKey, token);
				return;
			}
			catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is RetainerTerminalCharacterException))
			{
				lastFailure = ex;
				if (RetainerAttemptPolicy.CanRetry(attempt, terminalFailure: false))
				{
					await RecoverForRetryAsync(contentId, characterKey, token);
					continue;
				}
			}
			break;
		}
		throw new RetainerTerminalCharacterException($"Navigation to {route.Name} exhausted {3} attempts: " + (lastFailure?.Message ?? "unknown failure"), lastFailure);
	}

	private async System.Threading.Tasks.Task NavigateAttemptAsync(NpcRoute route, ulong contentId, string characterKey, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		if (!(await WaitForStableStateAsync(() => AreAutomationBackendsIdleUnsafe(requireReadyNavmesh: true), TimeSpan.FromSeconds(60L), contentId, characterKey, token)))
		{
			throw new RetainerTerminalCharacterException("Lifestream or vnavmesh did not provide a stable ready/idle capability state.");
		}
		for (int routeStep = 0; routeStep < 5; routeStep++)
		{
			(uint, bool) obj = await framework.RunOnFrameworkThread(() => (Territory: clientState.TerritoryType, Transition: IsTransitionActiveUnsafe()));
			var (num, _) = obj;
			switch (RetainerRouteRecovery.Decide(obj.Item2, num, route.ArrivalTerritory, route.TargetTerritory))
			{
			case RetainerRouteRecoveryDecision.WaitForTransition:
				if (!((num != 0) ? (await WaitForTerritorySettledAsync(num, TimeSpan.FromSeconds(90L), contentId, characterKey, token)) : (await WaitForStableStateAsync(() => !IsTransitionActiveUnsafe() && AreAutomationBackendsIdleUnsafe(requireReadyNavmesh: true), TimeSpan.FromSeconds(90L), contentId, characterKey, token))))
				{
					throw new TimeoutException("The active territory transition did not settle.");
				}
				continue;
			case RetainerRouteRecoveryDecision.Arrived:
				await WaitForTerritorySettledOrThrowAsync(route.TargetTerritory, TimeSpan.FromSeconds(60L), contentId, characterKey, token);
				await MoveToPositionAsync(route.Position, route.TargetTerritory, 2.5f, contentId, characterKey, token);
				await WaitForTerritorySettledOrThrowAsync(route.TargetTerritory, TimeSpan.FromSeconds(30L), contentId, characterKey, token);
				return;
			case RetainerRouteRecoveryDecision.ContinueCurrentRoute:
			{
				Vector3 position = route.ZoneTransition ?? throw new RetainerTerminalCharacterException($"No validated route from {route.ArrivalTerritory} to {route.TargetTerritory}.");
				await MoveThroughZoneTransitionAsync(position, route.ArrivalTerritory, route.TargetTerritory, contentId, characterKey, token);
				await WaitForTerritorySettledOrThrowAsync(route.TargetTerritory, TimeSpan.FromSeconds(45L), contentId, characterKey, token);
				continue;
			}
			}
			bool teleportInvoked = false;
			bool accepted = false;
			if (!(await WaitUntilAsync(delegate
			{
				if (!teleportInvoked)
				{
					teleportInvoked = true;
					accepted = lifestream.Teleport(route.AetheryteId, 0, route.AetheryteName);
				}
				return true;
			}, TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
			{
				throw new TimeoutException("A stable identity was not available to issue the teleport callback.");
			}
			if (!accepted)
			{
				throw new InvalidOperationException("Lifestream rejected teleport to " + route.AetheryteName + ".");
			}
			await WaitForTerritorySettledOrThrowAsync(route.ArrivalTerritory, TimeSpan.FromSeconds(90L), contentId, characterKey, token);
		}
		throw new InvalidOperationException($"Route recalculation did not reach territory {route.TargetTerritory} within the bounded route steps.");
	}

	private async System.Threading.Tasks.Task MoveToPositionAsync(Vector3 position, uint territory, float tolerance, ulong contentId, string characterKey, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		(bool, bool) tuple = await framework.RunOnFrameworkThread(() => (Available: vnavmesh.TryGetActivity(out var ready, out var _), Ready: ready));
		if (!tuple.Item1 || !tuple.Item2)
		{
			throw new RetainerTerminalCharacterException("vnavmesh is unavailable or not ready for movement.");
		}
		bool movementInvoked = false;
		bool started = false;
		if (!(await WaitUntilAsync(delegate
		{
			if (!movementInvoked)
			{
				movementInvoked = true;
				started = StartMovementUnsafe(RetainerMovementPolicy.SelectRequest(zoneTransition: false), position, tolerance);
			}
			return true;
		}, TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
		{
			throw new TimeoutException("A stable identity was not available to issue the movement request.");
		}
		if (!started)
		{
			throw new InvalidOperationException("vnavmesh rejected the validated movement request.");
		}
		ownsMovement = true;
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60L);
		RetainerStableIdentityGate identityGate = new RetainerStableIdentityGate();
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			(RetainerIdentityObservation Observation, uint Territory, float Distance) state = await framework.RunOnFrameworkThread(delegate
			{
				IPlayerCharacter localPlayer = objectTable.LocalPlayer;
				return (Observation: ObserveIdentityUnsafe(contentId, characterKey), Territory: clientState.TerritoryType, Distance: (localPlayer == null) ? float.MaxValue : Vector3.Distance(localPlayer.Position, position));
			});
			RetainerIdentityObservationKind retainerIdentityObservationKind = identityGate.Observe(state.Observation);
			if (retainerIdentityObservationKind == RetainerIdentityObservationKind.DefinitiveMismatch)
			{
				await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
				throw new RetainerIdentityMismatchException("Character identity stably changed during movement: " + state.Observation.Detail + ".");
			}
			RetainerMovementProgress retainerMovementProgress = RetainerMovementPolicy.Observe(RetainerMovementRequestKind.CloseTo, crossingInitiated: false, betweenAreas: false, state.Distance <= tolerance, state.Territory, territory, territory);
			if (retainerIdentityObservationKind == RetainerIdentityObservationKind.Exact && retainerMovementProgress.Decision == RetainerMovementProgressDecision.Complete)
			{
				await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
				if (!(await WaitForStableVnavmeshIdleAsync(TimeSpan.FromSeconds(10L), token)))
				{
					throw new TimeoutException("vnavmesh did not become stably idle after movement stopped.");
				}
				ownsMovement = false;
				return;
			}
			await System.Threading.Tasks.Task.Delay(150, token);
		}
		await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
		throw new TimeoutException($"Movement to {position} in territory {territory} timed out.");
	}

	private async System.Threading.Tasks.Task MoveThroughZoneTransitionAsync(Vector3 position, uint sourceTerritory, uint targetTerritory, ulong contentId, string characterKey, CancellationToken token)
	{
		await VerifyIdentityAsync(contentId, characterKey, token);
		(bool, bool) tuple = await framework.RunOnFrameworkThread(() => (Available: vnavmesh.TryGetActivity(out var ready, out var _), Ready: ready));
		if (!tuple.Item1 || !tuple.Item2)
		{
			throw new RetainerTerminalCharacterException("vnavmesh is unavailable or not ready for movement.");
		}
		RetainerMovementRequestKind request = RetainerMovementPolicy.SelectRequest(zoneTransition: true);
		bool movementInvoked = false;
		bool started = false;
		if (!(await WaitUntilAsync(delegate
		{
			if (!movementInvoked)
			{
				movementInvoked = true;
				started = StartMovementUnsafe(request, position, 0f);
			}
			return true;
		}, TimeSpan.FromSeconds(10L), contentId, characterKey, token)))
		{
			throw new TimeoutException("A stable identity was not available to issue the exact zone-transition movement request.");
		}
		if (!started)
		{
			throw new InvalidOperationException("vnavmesh rejected the exact zone-transition movement request.");
		}
		ownsMovement = true;
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60L);
		RetainerStableIdentityGate identityGate = new RetainerStableIdentityGate();
		bool crossingInitiated = false;
		try
		{
			while (DateTime.UtcNow < deadline)
			{
				token.ThrowIfCancellationRequested();
				(RetainerIdentityObservation, uint, bool, bool) tuple2 = await framework.RunOnFrameworkThread(() => (Observation: ObserveIdentityUnsafe(contentId, characterKey), Territory: clientState.TerritoryType, BetweenAreas: condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51], Transition: IsTransitionActiveUnsafe()));
				RetainerMovementProgress retainerMovementProgress = RetainerMovementPolicy.Observe(request, crossingInitiated, tuple2.Item3, withinTolerance: false, tuple2.Item2, sourceTerritory, targetTerritory);
				if (!crossingInitiated && retainerMovementProgress.CrossingInitiated)
				{
					Plugin.Log.Information($"[RetainerSetup] Exact zone crossing {sourceTerritory}->{targetTerritory} initiated after BetweenAreas/territory departure.");
				}
				crossingInitiated = retainerMovementProgress.CrossingInitiated;
				RetainerIdentityObservationKind retainerIdentityObservationKind = identityGate.Observe(tuple2.Item1);
				if (retainerIdentityObservationKind == RetainerIdentityObservationKind.DefinitiveMismatch)
				{
					throw new RetainerIdentityMismatchException("Character identity stably changed during zone crossing: " + tuple2.Item1.Detail + ".");
				}
				if (retainerMovementProgress.Decision == RetainerMovementProgressDecision.WrongTerritory)
				{
					throw new InvalidOperationException($"Exact zone crossing {sourceTerritory}->{targetTerritory} entered unexpected territory {tuple2.Item2}; the bounded route will be recalculated.");
				}
				if (retainerMovementProgress.Decision == RetainerMovementProgressDecision.Complete && retainerIdentityObservationKind == RetainerIdentityObservationKind.Exact && !tuple2.Item4)
				{
					await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
					if (!(await WaitForStableVnavmeshIdleAsync(TimeSpan.FromSeconds(10L), token)))
					{
						throw new TimeoutException("vnavmesh did not become stably idle after the exact zone crossing.");
					}
					ownsMovement = false;
					return;
				}
				await System.Threading.Tasks.Task.Delay(150, token);
			}
			throw new TimeoutException($"Exact zone crossing {sourceTerritory}->{targetTerritory} timed out before " + "BetweenAreas/territory departure and stable target-territory arrival.");
		}
		finally
		{
			if (ownsMovement)
			{
				await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
				ownsMovement = false;
			}
		}
	}

	private bool StartMovementUnsafe(RetainerMovementRequestKind request, Vector3 position, float tolerance)
	{
		if (request != RetainerMovementRequestKind.Exact)
		{
			return vnavmesh.PathfindAndMoveCloseTo(position, fly: false, tolerance);
		}
		return vnavmesh.PathfindAndMoveTo(position, fly: false);
	}

	private async System.Threading.Tasks.Task WaitForTerritorySettledOrThrowAsync(uint expectedTerritory, TimeSpan timeout, ulong contentId, string characterKey, CancellationToken token)
	{
		if (!(await WaitForTerritorySettledAsync(expectedTerritory, timeout, contentId, characterKey, token)))
		{
			throw new TimeoutException($"Territory {expectedTerritory} did not reach a stable exact idle state.");
		}
	}

	private async Task<bool> WaitForTerritorySettledAsync(uint expectedTerritory, TimeSpan timeout, ulong contentId, string characterKey, CancellationToken token)
	{
		bool num = await WaitForStableStateAsync(() => clientState.TerritoryType == expectedTerritory && !IsTransitionActiveUnsafe() && AreAutomationBackendsIdleUnsafe(requireReadyNavmesh: true), timeout, contentId, characterKey, token);
		if (num)
		{
			ownsMovement = false;
		}
		return num;
	}

	private async Task<RetainerNamingSessionResult> CompleteAcceptedNamingSessionAsync(LiveRetainerInfo created, int submittedCount, ulong contentId, string characterKey, CancellationToken token)
	{
		if (!(await CloseAcceptedHireFlowAsync(contentId, characterKey, token)))
		{
			return RetainerNamingSessionResult.AcceptedClosureUnverified(created, "The native roster recorded the hire, but direct accepted-hire InputString closure and four closed reads could not be verified.", submittedCount);
		}
		Plugin.Log.Information($"[RetainerSetup] Henchman Vocate flow: native roster recorded {created.Name} ({created.RetainerId}) after a submitted Row 83 confirmation.");
		return RetainerNamingSessionResult.Accepted(created, submittedCount);
	}

	internal async Task<bool> CloseAcceptedHireFlowAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		ownsVocateFlow = true;
		EnableVocateTalkSkipping();
		RetainerVocateClosureGate closureGate = new RetainerVocateClosureGate();
		try
		{
			bool num = await WaitUntilAsync(delegate
			{
				if (RetainerAcceptedHireCleanupLogic.Decide(IsAddonPresentUnsafe("InputString")) == RetainerAcceptedHireCleanupAction.DirectCloseInputString)
				{
					closureGate.Observe(completelyClosed: false);
					if (CloseAcceptedInputStringUnsafe())
					{
						Plugin.Log.Information("[RetainerSetup] Accepted hire: directly closed the stale InputString addon; InputString(-1) and localized Row 82 are not part of accepted cleanup.");
					}
					return false;
				}
				return closureGate.Observe(IsVocateInteractionClosedUnsafe());
			}, TimeSpan.FromSeconds(15L), contentId, characterKey, token);
			if (num)
			{
				ownsVocateFlow = false;
				Plugin.Log.Information("[RetainerSetup] Accepted hire: verified Talk, SelectString, SelectYesno, creation addons, and NPC event state closed for four consecutive reads.");
			}
			return num;
		}
		finally
		{
			DisableVocateTalkSkipping();
		}
	}

	private static bool RostersMatch(IReadOnlyList<LiveRetainerInfo> expected, IReadOnlyList<LiveRetainerInfo> actual)
	{
		if (expected.Count == actual.Count)
		{
			return expected.All((LiveRetainerInfo left) => actual.Any((LiveRetainerInfo right) => left.RetainerId == right.RetainerId && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)));
		}
		return false;
	}

	private async Task<LiveRetainerInfo?> WaitForNewRetainerAsync(IReadOnlyList<LiveRetainerInfo> before, string expectedName, ulong contentId, string characterKey, CancellationToken token)
	{
		HashSet<ulong> beforeIds = before.Select((LiveRetainerInfo x) => x.RetainerId).ToHashSet();
		LiveRetainerInfo created = null;
		await WaitUntilAsync(delegate
		{
			created = ReadLiveRosterUnsafe().FirstOrDefault((LiveRetainerInfo retainer) => !beforeIds.Contains(retainer.RetainerId) && string.Equals(retainer.Name, expectedName, StringComparison.OrdinalIgnoreCase));
			return created != null;
		}, TimeSpan.FromSeconds(25L), contentId, characterKey, token);
		return created;
	}

	private unsafe static uint ReadSavedAppearancePresetCountUnsafe()
	{
		Framework* ptr = Framework.Instance();
		if (ptr != null && ptr->CharamakeAvatarSaveData != null)
		{
			return ptr->CharamakeAvatarSaveData->Release.GetValidSlotCount();
		}
		return 0u;
	}

	private unsafe bool ContinueAfterSavedAppearancePromptUnsafe(string expected, Func<bool> continuation)
	{
		if (!TryGetAddonUnsafe("SelectYesno", out var addon))
		{
			return continuation();
		}
		AddonMaster.SelectYesno selectYesno = new AddonMaster.SelectYesno(addon);
		bool flag = PromptMatches(selectYesno.SeStringNullTerminated.ToString(), expected, null);
		bool flag2 = ReadSavedAppearancePresetCountUnsafe() != 0 && VocateCreationAddons.Any(IsAddonPresentUnsafe);
		if ((!flag && !flag2) || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		selectYesno.No();
		Plugin.Log.Information(flag ? "[RetainerSetup] Answered No to the localized saved-appearance prompt through ECommons." : "[RetainerSetup] Answered No to the flow-scoped saved-appearance prompt through ECommons; localized text extraction was unavailable.");
		return false;
	}

	private async Task<bool> SelectLocalizedOptionAsync(string expected, ulong contentId, string characterKey, TimeSpan timeout, CancellationToken token)
	{
		return await WaitUntilAsync(() => SelectStringOptionUnsafe(expected), timeout, contentId, characterKey, token);
	}

	private async Task<bool> SelectAnyLocalizedOptionAsync(IReadOnlyCollection<string> expected, ulong contentId, string characterKey, TimeSpan timeout, CancellationToken token)
	{
		return await WaitUntilAsync(() => SelectStringOptionUnsafe(expected), timeout, contentId, characterKey, token);
	}

	private async Task<bool> SelectRetainerByNameAsync(string name, ulong contentId, string characterKey, CancellationToken token)
	{
		return await WaitUntilAsync(() => SelectRetainerListEntryUnsafe(name), TimeSpan.FromSeconds(15L), contentId, characterKey, token);
	}

	private async Task<bool> ConfirmLocalizedYesNoAsync(string expected, bool yes, string? dynamicName, ulong contentId, string characterKey, TimeSpan timeout, CancellationToken token)
	{
		return await WaitUntilAsync(() => ClickYesNoIfMatchesUnsafe(expected, yes, dynamicName), timeout, contentId, characterKey, token);
	}

	private async Task<bool> CloseExhaustedNamingSessionAsync(RetainerNamingSession session, int submittedCount, ulong contentId, string characterKey, CancellationToken token)
	{
		bool callbackAttempted = false;
		bool callbackSucceeded = false;
		if (!(await WaitUntilAsync(delegate
		{
			callbackSucceeded = RequestInputStringCancellationUnsafe(requireOccupiedEvent: true, out var callbackAttempted2);
			if (!callbackAttempted2)
			{
				return false;
			}
			callbackAttempted = true;
			return true;
		}, TimeSpan.FromSeconds(5L), contentId, characterKey, token)) || !callbackAttempted || !callbackSucceeded)
		{
			Plugin.Log.Error($"[RetainerSetup] Naming session explicit closure failed before a verified InputString(-1) request after {submittedCount}/{session.Candidates.Count} candidates.");
			return false;
		}
		Plugin.Log.Information($"[RetainerSetup] Naming session explicit closure requested InputString(-1) once after {submittedCount}/{session.Candidates.Count} rejected candidates; waiting for localized Row 82.");
		bool num = await DismissOwnedVocateAsync(contentId, characterKey, token, inputCancellationAlreadyRequested: true, requireInputCancellationConfirmation: true);
		if (num)
		{
			Plugin.Log.Information("[RetainerSetup] Naming session explicit closure confirmed localized Row 82 and verified four closed reads.");
		}
		return num;
	}

	private async Task<bool> DismissOwnedVocateAsync(ulong contentId, string characterKey, CancellationToken token, bool inputCancellationAlreadyRequested = false, bool requireInputCancellationConfirmation = false)
	{
		EnableVocateTalkSkipping();
		RetainerVocateClosureGate closureGate = new RetainerVocateClosureGate();
		bool inputCancellationRequested = inputCancellationAlreadyRequested;
		bool inputCancellationConfirmed = false;
		bool inputCancellationVerified = !requireInputCancellationConfirmation;
		try
		{
			bool num = await WaitUntilAsync(delegate
			{
				string prompt;
				bool localizedInputCancellationPromptVisible = TryGetInputStringCancellationPromptUnsafe(out prompt);
				bool inputStringPresent = IsAddonPresentUnsafe("InputString");
				switch (RetainerVocateCleanupLogic.DecideInputCancellationAction(localizedInputCancellationPromptVisible, inputStringPresent, inputCancellationRequested, inputCancellationConfirmed))
				{
				case RetainerVocateCleanupAction.ConfirmInputCancellation:
					closureGate.Observe(completelyClosed: false);
					if (ConfirmInputStringCancellationUnsafe())
					{
						inputCancellationRequested = false;
						inputCancellationConfirmed = true;
						inputCancellationVerified = true;
						Plugin.Log.Information("[RetainerSetup] Vocate cleanup: answered Yes to localized Row 82 prompt " + prompt + " before touching InputString again.");
					}
					return false;
				case RetainerVocateCleanupAction.RequestInputCancellation:
					closureGate.Observe(completelyClosed: false);
					if (RequestInputStringCancellationUnsafe())
					{
						inputCancellationRequested = true;
						Plugin.Log.Information("[RetainerSetup] Vocate cleanup: requested InputString cancellation and is waiting for localized Row 82.");
					}
					return false;
				case RetainerVocateCleanupAction.WaitForInputCancellation:
					closureGate.Observe(completelyClosed: false);
					return false;
				case RetainerVocateCleanupAction.DirectCloseResidualInputString:
					closureGate.Observe(completelyClosed: false);
					if (CloseRejectedResidualInputStringUnsafe())
					{
						Plugin.Log.Information("[RetainerSetup] Vocate cleanup: directly closed one residual rejected-name InputString layer after localized Row 82; no second cancellation was sent.");
					}
					return false;
				default:
				{
					if (!inputCancellationVerified)
					{
						closureGate.Observe(completelyClosed: false);
						return false;
					}
					inputCancellationRequested = false;
					inputCancellationConfirmed = false;
					if (CloseOwnedRetainerYesNoUnsafe(out string matchedPrompt))
					{
						closureGate.Observe(completelyClosed: false);
						Plugin.Log.Information("[RetainerSetup] Vocate cleanup: answered No to owned prompt " + matchedPrompt + ".");
						return false;
					}
					if (CloseOneVocateCreationAddonUnsafe(out string addonName))
					{
						closureGate.Observe(completelyClosed: false);
						Plugin.Log.Information("[RetainerSetup] Vocate cleanup: closed owned creation addon " + addonName + ".");
						return false;
					}
					if (SelectExactStringOptionUnsafe(GetVocateSelectStringCleanupOptions(), out string selected))
					{
						closureGate.Observe(completelyClosed: false);
						Plugin.Log.Information("[RetainerSetup] Vocate cleanup: selected owned exit entry " + selected + ".");
						return false;
					}
					return closureGate.Observe(IsVocateInteractionClosedUnsafe());
				}
				}
			}, TimeSpan.FromSeconds(10L), contentId, characterKey, token);
			if (num)
			{
				Plugin.Log.Information("[RetainerSetup] Vocate cleanup: verified Talk, SelectString, SelectYesno, creation addons, and NPC event state closed for four consecutive reads.");
			}
			return num;
		}
		finally
		{
			DisableVocateTalkSkipping();
		}
	}

	private async Task<bool> TargetAndInteractWithVocateAsync(NpcRoute route, ulong contentId, string characterKey, TimeSpan timeout, CancellationToken token)
	{
		if (!(await WaitUntilAsync(() => TryTargetVocateUnsafe(route.BaseId, route.Position), timeout, contentId, characterKey, token)))
		{
			return false;
		}
		await System.Threading.Tasks.Task.Delay(750, token);
		bool num = await framework.RunOnFrameworkThread(() => InteractWithTargetedVocateUnsafe(route.BaseId, route.Position));
		if (num)
		{
			Plugin.Log.Information($"[RetainerSetup] Henchman Vocate flow: targeted and interacted with {route.Name} ({route.BaseId}).");
		}
		return num;
	}

	private bool TryTargetVocateUnsafe(uint baseId, Vector3 expectedPosition)
	{
		if (IsOccupiedInNpcEventUnsafe())
		{
			return false;
		}
		IPlayerCharacter player = objectTable.LocalPlayer;
		IGameObject gameObject = (from gameObject2 in objectTable
			where gameObject2.BaseId == baseId && gameObject2.IsTargetable && Vector3.Distance(gameObject2.Position, expectedPosition) <= 3f
			orderby (player != null) ? Vector3.Distance(gameObject2.Position, player.Position) : float.MaxValue
			select gameObject2).FirstOrDefault();
		if (gameObject == null)
		{
			return false;
		}
		targetManager.Target = gameObject;
		return true;
	}

	private bool InteractWithTargetedVocateUnsafe(uint baseId, Vector3 expectedPosition)
	{
		IPlayerCharacter player = objectTable.LocalPlayer;
		IGameObject gameObject = (from gameObject2 in objectTable
			where gameObject2.BaseId == baseId && gameObject2.IsTargetable && Vector3.Distance(gameObject2.Position, expectedPosition) <= 3f
			orderby (player != null) ? Vector3.Distance(gameObject2.Position, player.Position) : float.MaxValue
			select gameObject2).FirstOrDefault();
		if (player == null || gameObject == null)
		{
			Plugin.Log.Warning($"[RetainerSetup] Exact Vocate {baseId} was no longer available at interaction time.");
			return false;
		}
		float num = Vector3.Distance(player.Position, gameObject.Position);
		if (num > 6f)
		{
			Plugin.Log.Warning($"[RetainerSetup] Exact Vocate {baseId} remained {num:F2} yalms away after the guarded approach.");
			return false;
		}
		targetManager.Target = gameObject;
		if (TryInteractWithAddressUnsafe(gameObject.Address, gameObject.Position, 6f))
		{
			return true;
		}
		Plugin.Log.Warning($"[RetainerSetup] Exact Vocate {baseId} could not be invoked at {num:F2} yalms despite a valid target.");
		return false;
	}

	private bool SelectVocateEntryUnsafe(string expected, string label)
	{
		if (!SelectStringOptionUnsafe(expected, enforceVocateTick: true))
		{
			return false;
		}
		Plugin.Log.Information("[RetainerSetup] Henchman Vocate flow: selected localized " + label + " entry through ECommons.");
		return true;
	}

	private bool SubmitRetainerNameUnsafe(string name)
	{
		if (!IsAddonReadyUnsafe("InputString") || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		return FireCallbackUnsafe("InputString", 0, name, string.Empty);
	}

	private unsafe bool ProcessVocateYesNoUnsafe(bool accept, string expected, string responseLabel, string? dynamicName = null)
	{
		if (!TryGetAddonUnsafe("SelectYesno", out var addon))
		{
			return false;
		}
		AddonMaster.SelectYesno selectYesno = new AddonMaster.SelectYesno(addon);
		if (!PromptMatches(selectYesno.SeStringNullTerminated.ToString(), expected, dynamicName))
		{
			return false;
		}
		if (!TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		if (accept)
		{
			selectYesno.Yes();
		}
		else
		{
			selectYesno.No();
		}
		Plugin.Log.Information("[RetainerSetup] Henchman Vocate flow: answered " + responseLabel + " to the localized hire confirmation through ECommons.");
		return true;
	}

	private unsafe bool ProcessHireConfirmationOrObserveAppearanceUnsafe(string hireConfirmation, string savedAppearancePrompt)
	{
		if (ProcessVocateYesNoUnsafe(accept: true, hireConfirmation, "Yes"))
		{
			return true;
		}
		if (TryGetAddonUnsafe("SelectYesno", out var addon))
		{
			if (PromptMatches(new AddonMaster.SelectYesno(addon).SeStringNullTerminated.ToString(), savedAppearancePrompt, null))
			{
				Plugin.Log.Information("[RetainerSetup] Hire confirmation was already handled externally; the saved-appearance prompt is now active.");
				return true;
			}
			return false;
		}
		if (VocateCreationAddons.Any(IsAddonPresentUnsafe))
		{
			Plugin.Log.Information("[RetainerSetup] Hire confirmation was already handled externally; the retainer appearance flow is already active.");
			return true;
		}
		return false;
	}

	private unsafe bool SelectRetainerPersonalityUnsafe(uint personalityRow)
	{
		bool flag = ((personalityRow < 68 || personalityRow > 73) ? true : false);
		if (flag || !TryGetAddonUnsafe("SelectString", out var addon))
		{
			return false;
		}
		AddonMaster.SelectString.Entry[] entries = new AddonMaster.SelectString(addon).Entries;
		if (entries.Length < 6)
		{
			return false;
		}
		string[] actualTexts = new string[entries.Length];
		for (int i = 0; i < entries.Length; i++)
		{
			try
			{
				actualTexts[i] = entries[i].Text;
			}
			catch
			{
				actualTexts[i] = string.Empty;
			}
		}
		string[] array = (from row in Enumerable.Range(68, 6)
			select ReadRawString("custom/000/CmnDefRetainerDesk_00009", (uint)row)).ToArray();
		int num = array.Count((string expected) => actualTexts.Any((string actual) => PromptMatches(actual, expected, null)));
		if (num < 4)
		{
			return false;
		}
		string expectedPersonality = ReadRawString("custom/000/CmnDefRetainerDesk_00009", personalityRow);
		int num2 = Array.FindIndex(actualTexts, (string actual) => PromptMatches(actual, expectedPersonality, null));
		bool value = false;
		if (num2 < 0 && num == array.Length)
		{
			num2 = (int)(personalityRow - 68);
			value = true;
		}
		if (num2 < 0 || num2 >= entries.Length || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		entries[num2].Select();
		Plugin.Log.Information($"[RetainerSetup] Selected retainer personality row {personalityRow} at entry {num2} (verifiedLocalizedEntries={num}/6, orderFallback={value}).");
		return true;
	}

	private unsafe bool AdvanceAppearanceFinalizationOrObservePersonalityUnsafe(string saveAppearancePrompt, string finalizeAppearancePrompt)
	{
		if (IsRetainerPersonalityMenuVisibleUnsafe())
		{
			return true;
		}
		if (!TryGetAddonUnsafe("SelectYesno", out var addon))
		{
			return false;
		}
		AddonMaster.SelectYesno selectYesno = new AddonMaster.SelectYesno(addon);
		string actual = selectYesno.SeStringNullTerminated.ToString();
		if (PromptMatches(actual, saveAppearancePrompt, null))
		{
			if (!TryBeginVocateUiActionUnsafe())
			{
				return false;
			}
			selectYesno.No();
			Plugin.Log.Information("[RetainerSetup] Answered No to the localized Save Appearance Data prompt before personality selection.");
			return false;
		}
		if (PromptMatches(actual, finalizeAppearancePrompt, null))
		{
			if (!TryBeginVocateUiActionUnsafe())
			{
				return false;
			}
			selectYesno.Yes();
			Plugin.Log.Information("[RetainerSetup] Answered Yes to the localized appearance-finalization prompt before personality selection.");
		}
		return false;
	}

	private unsafe bool IsRetainerPersonalityMenuVisibleUnsafe()
	{
		if (!TryGetAddonUnsafe("SelectString", out var addon))
		{
			return false;
		}
		AddonMaster.SelectString.Entry[] entries = new AddonMaster.SelectString(addon).Entries;
		if (entries.Length < 6)
		{
			return false;
		}
		string[] actualTexts = new string[entries.Length];
		for (int i = 0; i < entries.Length; i++)
		{
			try
			{
				actualTexts[i] = entries[i].Text;
			}
			catch
			{
				actualTexts[i] = string.Empty;
			}
		}
		return (from row in Enumerable.Range(68, 6)
			select ReadRawString("custom/000/CmnDefRetainerDesk_00009", (uint)row)).Count((string expected) => actualTexts.Any((string actual) => PromptMatches(actual, expected, null))) >= 4;
	}

	private bool ProcessFinalHireConfirmationOrObserveNameInputUnsafe(string hireConfirmation)
	{
		if (ProcessVocateYesNoUnsafe(accept: true, hireConfirmation, "Yes"))
		{
			return true;
		}
		if (!IsAddonReadyUnsafe("InputString"))
		{
			return false;
		}
		Plugin.Log.Information("[RetainerSetup] Final hire confirmation was already handled externally; the retainer name input is now active.");
		return true;
	}

	private unsafe bool IsFinalHireConfirmationOrNameInputVisibleUnsafe(string hireConfirmation)
	{
		if (IsAddonReadyUnsafe("InputString"))
		{
			return true;
		}
		if (!TryGetAddonUnsafe("SelectYesno", out var addon))
		{
			return false;
		}
		return PromptMatches(new AddonMaster.SelectYesno(addon).SeStringNullTerminated.ToString(), hireConfirmation, null);
	}

	private bool IsVocateInteractionClosedUnsafe()
	{
		if (!IsAddonVisibleUnsafe("Talk") && !IsAddonVisibleUnsafe("SelectString") && !IsAddonVisibleUnsafe("SelectYesno") && !VocateCreationAddons.Any(IsAddonPresentUnsafe))
		{
			return !IsOccupiedInNpcEventUnsafe();
		}
		return false;
	}

	private async Task<bool> WaitForStableVocateClosureAsync(TimeSpan timeout, ulong contentId, string characterKey, CancellationToken token)
	{
		bool num = await WaitForStableStateAsync(IsVocateInteractionClosedUnsafe, timeout, contentId, characterKey, token);
		if (num)
		{
			Plugin.Log.Information("[RetainerSetup] Henchman Vocate flow: verified Talk, SelectString, SelectYesno, creation addons, and NPC event state closed for four consecutive reads.");
		}
		return num;
	}

	private void EnableVocateTalkSkipping()
	{
		if (vocateTalkSkipping)
		{
			return;
		}
		vocateTalkSkipping = true;
		try
		{
			vocateUiActionGate.Reset();
			framework.Update += AdvanceVocateFrameworkTick;
			addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", AdvanceOwnedVocateTalk);
			addonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", AdvanceOwnedVocateTalk);
			if (!yesAlready.PauseForRetainerFlow())
			{
				throw new InvalidOperationException("YesAlready could not be paused before QST acquired the retainer dialogue.");
			}
			restoreYesAlreadyAfterVocate = true;
		}
		catch
		{
			DisableVocateTalkSkipping();
			throw;
		}
	}

	private void DisableVocateTalkSkipping()
	{
		if (!vocateTalkSkipping && !restoreYesAlreadyAfterVocate)
		{
			return;
		}
		try
		{
			if (vocateTalkSkipping)
			{
				ReleaseVocateRegistration(delegate
				{
					addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", AdvanceOwnedVocateTalk);
				}, "Talk PostSetup listener");
				ReleaseVocateRegistration(delegate
				{
					addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", AdvanceOwnedVocateTalk);
				}, "Talk PostUpdate listener");
				ReleaseVocateRegistration(delegate
				{
					framework.Update -= AdvanceVocateFrameworkTick;
				}, "framework callback");
			}
		}
		finally
		{
			vocateTalkSkipping = false;
			if (restoreYesAlreadyAfterVocate)
			{
				restoreYesAlreadyAfterVocate = false;
				yesAlready.ResumeAfterRetainerFlow();
			}
		}
	}

	private static void ReleaseVocateRegistration(System.Action release, string name)
	{
		try
		{
			release();
		}
		catch (Exception exception)
		{
			Plugin.Log.Error(exception, "[RetainerSetup] Failed to release owned " + name + ".");
		}
	}

	private void AdvanceVocateFrameworkTick(IFramework _)
	{
		vocateUiActionGate.AdvanceFrameworkTick();
	}

	private bool TryBeginVocateUiActionUnsafe()
	{
		return vocateUiActionGate.TryBeginAction();
	}

	private unsafe void AdvanceOwnedVocateTalk(AddonEvent type, AddonArgs args)
	{
		if (ownsVocateFlow || ownsSummoningBellDialogue)
		{
			AtkUnitBase* address = (AtkUnitBase*)args.Addon.Address;
			if (address != null && address->IsVisible && TryBeginVocateUiActionUnsafe())
			{
				new AddonMaster.Talk((nint)args.Addon).Click();
				Plugin.Log.Information(ownsSummoningBellDialogue ? $"[RetainerSetup] Summoning bell flow: advanced Talk from {type} through ECommons." : $"[RetainerSetup] Henchman Vocate flow: advanced Talk from {type} through ECommons.");
			}
		}
	}

	private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, ulong contentId, string characterKey, CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		RetainerStableIdentityGate gate = new RetainerStableIdentityGate();
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			(bool, bool, string) tuple = await framework.RunOnFrameworkThread(delegate
			{
				RetainerIdentityObservation retainerIdentityObservation = ObserveIdentityUnsafe(contentId, characterKey);
				RetainerIdentityObservationKind retainerIdentityObservationKind = gate.Observe(retainerIdentityObservation);
				if (retainerIdentityObservationKind == RetainerIdentityObservationKind.DefinitiveMismatch)
				{
					return (Result: false, Mismatch: true, Detail: retainerIdentityObservation.Detail);
				}
				return (!RetainerSafeCallbackPolicy.CanInvoke(retainerIdentityObservationKind, IsCallbackSafeStateAvailableUnsafe())) ? (Result: false, Mismatch: false, Detail: retainerIdentityObservation.Detail) : (Result: predicate(), Mismatch: false, Detail: retainerIdentityObservation.Detail);
			});
			if (tuple.Item2)
			{
				throw new RetainerIdentityMismatchException("Character identity stably changed during a guarded retainer action: " + tuple.Item3 + ".");
			}
			if (tuple.Item1)
			{
				return true;
			}
			await System.Threading.Tasks.Task.Delay(150, token);
		}
		return false;
	}

	private async Task<bool> WaitForStableStateAsync(Func<bool> statePredicate, TimeSpan timeout, ulong contentId, string characterKey, CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		RetainerStableIdentityGate gate = new RetainerStableIdentityGate();
		int stableStateReads = 0;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			(RetainerIdentityObservation, bool) tuple = await framework.RunOnFrameworkThread(delegate
			{
				RetainerIdentityObservation retainerIdentityObservation = ObserveIdentityUnsafe(contentId, characterKey);
				bool item = retainerIdentityObservation.Kind == RetainerIdentityObservationKind.Exact && IsCallbackSafeStateAvailableUnsafe() && statePredicate();
				return (Observation: retainerIdentityObservation, StateAvailable: item);
			});
			RetainerIdentityObservationKind num = gate.Observe(tuple.Item1);
			if (num == RetainerIdentityObservationKind.DefinitiveMismatch)
			{
				throw new RetainerIdentityMismatchException("Character identity stably changed while waiting for a safe state: " + tuple.Item1.Detail + ".");
			}
			stableStateReads = (tuple.Item2 ? (stableStateReads + 1) : 0);
			if (num == RetainerIdentityObservationKind.Exact && stableStateReads >= 4)
			{
				return true;
			}
			await System.Threading.Tasks.Task.Delay(150, token);
		}
		return false;
	}

	public async System.Threading.Tasks.Task RecoverForRetryAsync(ulong contentId, string characterKey, CancellationToken token)
	{
		await framework.RunOnFrameworkThread((System.Action)vnavmesh.StopCompletely);
		if (!(await CloseOwnedWindowsAsync(contentId, characterKey, token)))
		{
			throw new InvalidOperationException("Owned retainer windows could not be closed before retrying.");
		}
		await WaitForSafeStartingStateAsync(contentId, characterKey, token);
	}

	private async Task<bool> WaitForStableVnavmeshIdleAsync(TimeSpan timeout, CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		int stableReads = 0;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			stableReads = ((await framework.RunOnFrameworkThread(() => vnavmesh.TryGetActivity(out var _, out var busy) && !busy)) ? (stableReads + 1) : 0);
			if (stableReads >= 4)
			{
				return true;
			}
			await System.Threading.Tasks.Task.Delay(150, token);
		}
		return false;
	}

	private RetainerIdentityObservation ObserveIdentityUnsafe(ulong contentId, string characterKey)
	{
		try
		{
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			uint rowId = playerState.HomeWorld.RowId;
			string value = ((rowId == 0) ? string.Empty : playerState.HomeWorld.Value.Name.ToString());
			string observedCharacterKey = ((localPlayer == null || string.IsNullOrWhiteSpace(value)) ? string.Empty : $"{localPlayer.Name}@{value}");
			return RetainerIdentityLogic.Classify(clientState.IsLoggedIn, IsTransitionActiveUnsafe(), playerState.ContentId, rowId, observedCharacterKey, contentId, characterKey);
		}
		catch
		{
			return new RetainerIdentityObservation(RetainerIdentityObservationKind.Unavailable, string.Empty, "identity fields could not be read");
		}
	}

	private (ulong ContentId, string CharacterKey) ReadObservedIdentityUnsafe()
	{
		try
		{
			IPlayerCharacter localPlayer = objectTable.LocalPlayer;
			string value = ((playerState.HomeWorld.RowId == 0) ? string.Empty : playerState.HomeWorld.Value.Name.ToString());
			string item = ((localPlayer == null || string.IsNullOrWhiteSpace(value)) ? string.Empty : $"{localPlayer.Name}@{value}");
			return (ContentId: playerState.ContentId, CharacterKey: item);
		}
		catch
		{
			return (ContentId: 0uL, CharacterKey: string.Empty);
		}
	}

	private bool IsTransitionActiveUnsafe()
	{
		if (!condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && !condition[ConditionFlag.LoggingOut])
		{
			return clientState.TerritoryType == 0;
		}
		return true;
	}

	private bool IsCallbackSafeStateAvailableUnsafe()
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		if (clientState.IsLoggedIn && !IsTransitionActiveUnsafe() && localPlayer != null && float.IsFinite(localPlayer.Position.X) && float.IsFinite(localPlayer.Position.Y))
		{
			return float.IsFinite(localPlayer.Position.Z);
		}
		return false;
	}

	private bool IsOccupiedInNpcEventUnsafe()
	{
		if (!condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent])
		{
			return condition[ConditionFlag.OccupiedInCutSceneEvent];
		}
		return true;
	}

	private bool IsSafeStartingStateUnsafe()
	{
		if (!condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && !condition[ConditionFlag.LoggingOut] && !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Casting] && !condition[ConditionFlag.BoundByDuty] && !condition[ConditionFlag.BoundByDuty56] && !condition[ConditionFlag.BoundByDuty95] && !condition[ConditionFlag.WaitingForDuty] && !condition[ConditionFlag.WaitingForDutyFinder] && !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent])
		{
			return !condition[ConditionFlag.OccupiedInCutSceneEvent];
		}
		return false;
	}

	private bool AreAutomationBackendsIdleUnsafe(bool requireReadyNavmesh)
	{
		if (!lifestream.TryGetBusy(out var busy) || busy)
		{
			return false;
		}
		if (!vnavmesh.TryGetActivity(out var ready, out var busy2) || busy2)
		{
			return false;
		}
		return !requireReadyNavmesh || ready;
	}

	private unsafe bool IsLiveRosterReadyUnsafe()
	{
		RetainerManager* ptr = RetainerManager.Instance();
		if (ptr != null)
		{
			return ptr->IsReady;
		}
		return false;
	}

	private unsafe RetainerEntitlementInfo ReadEntitlementsUnsafe()
	{
		RetainerManager* ptr = RetainerManager.Instance();
		if (ptr != null)
		{
			return new RetainerEntitlementInfo(ptr->GetRetainerCount(), ptr->MaxRetainerEntitlement);
		}
		return new RetainerEntitlementInfo(0, 0);
	}

	private unsafe IReadOnlyList<LiveRetainerInfo> ReadLiveRosterUnsafe()
	{
		RetainerManager* ptr = RetainerManager.Instance();
		if (ptr == null || !ptr->IsReady)
		{
			return Array.Empty<LiveRetainerInfo>();
		}
		List<LiveRetainerInfo> list = new List<LiveRetainerInfo>();
		for (int i = 0; i < ptr->Retainers.Length; i++)
		{
			RetainerManager.Retainer retainer = ptr->Retainers[i];
			if (retainer.RetainerId != 0L && retainer.Name[0] != 0)
			{
				list.Add(new LiveRetainerInfo(retainer.RetainerId, retainer.NameString, retainer.Level, retainer.ClassJob, retainer.VentureId, retainer.VentureComplete));
			}
		}
		return list;
	}

	private bool TryInteractWithBaseIdUnsafe(uint baseId, Vector3 expectedPosition, float maximumDistance)
	{
		IGameObject gameObject = (from gameObject2 in objectTable
			where gameObject2.BaseId == baseId && gameObject2.IsTargetable && Vector3.Distance(gameObject2.Position, expectedPosition) <= 3f
			orderby (objectTable.LocalPlayer != null) ? Vector3.Distance(gameObject2.Position, objectTable.LocalPlayer.Position) : float.MaxValue
			select gameObject2).FirstOrDefault();
		if (gameObject != null)
		{
			return TryInteractWithAddressUnsafe(gameObject.Address, gameObject.Position, maximumDistance);
		}
		return false;
	}

	private unsafe bool IsExactTargetedVocateUnsafe()
	{
		TargetSystem* ptr = TargetSystem.Instance();
		if (ptr == null)
		{
			return false;
		}
		nint targetAddress = (nint)ptr->GetTargetObject();
		if (targetAddress == IntPtr.Zero)
		{
			return false;
		}
		IGameObject target = objectTable.FirstOrDefault((IGameObject gameObject) => gameObject.Address == targetAddress);
		if (target == null)
		{
			return false;
		}
		return new NpcRoute[3]
		{
			GetVocateRoute(RetainerStarterCity.LimsaLominsa),
			GetVocateRoute(RetainerStarterCity.Gridania),
			GetVocateRoute(RetainerStarterCity.Uldah)
		}.Any((NpcRoute route) => clientState.TerritoryType == route.TargetTerritory && target.BaseId == route.BaseId && Vector3.Distance(target.Position, route.Position) <= 3f);
	}

	private bool TryInteractWithNameUnsafe(string name, Vector3 expectedPosition, float maximumDistance)
	{
		IGameObject gameObject = (from gameObject2 in objectTable
			where gameObject2.IsTargetable && string.Equals(gameObject2.Name.ToString(), name, StringComparison.OrdinalIgnoreCase) && Vector3.Distance(gameObject2.Position, expectedPosition) <= 3f
			orderby (objectTable.LocalPlayer != null) ? Vector3.Distance(gameObject2.Position, objectTable.LocalPlayer.Position) : float.MaxValue
			select gameObject2).FirstOrDefault();
		if (gameObject != null)
		{
			return TryInteractWithAddressUnsafe(gameObject.Address, gameObject.Position, maximumDistance);
		}
		return false;
	}

	private unsafe bool TryInteractWithAddressUnsafe(nint address, Vector3 position, float maximumDistance)
	{
		IPlayerCharacter localPlayer = objectTable.LocalPlayer;
		TargetSystem* ptr = TargetSystem.Instance();
		if (localPlayer == null || address == IntPtr.Zero || ptr == null || Vector3.Distance(localPlayer.Position, position) > maximumDistance)
		{
			return false;
		}
		ptr->InteractWithObject((GameObject*)address, checkLineOfSight: false);
		return true;
	}

	private unsafe bool SelectRaceGenderUnsafe(int raceGender)
	{
		if (!TryGetAddonUnsafe("_CharaMakeRaceGender", out var addon) || !IsAddonReadyUnsafe("_CharaMakeProgress"))
		{
			return false;
		}
		if (ReceiveButtonEvent(addon, raceGender))
		{
			return ReceiveButtonEvent(addon, 28);
		}
		return false;
	}

	private unsafe bool SelectClanUnsafe(int clan)
	{
		if (!TryGetAddonUnsafe("_CharaMakeTribe", out var addon))
		{
			return false;
		}
		if (ReceiveButtonEvent(addon, clan))
		{
			return ReceiveButtonEvent(addon, 3);
		}
		return false;
	}

	private unsafe bool RandomizeAppearanceUnsafe()
	{
		if (!TryGetAddonUnsafe("_CharaMakeFeature", out var addon))
		{
			return false;
		}
		return ReceiveButtonEvent(addon, 4);
	}

	private bool FinishAppearanceUnsafe()
	{
		return FireCallbackUnsafe("_CharaMakeFeature", 100);
	}

	private unsafe static bool ReceiveButtonEvent(AtkUnitBase* addon, int eventId)
	{
		AtkStage* ptr = AtkStage.Instance();
		if (ptr == null)
		{
			return false;
		}
		AtkEvent atkEvent = new AtkEvent
		{
			Node = null,
			Listener = &addon->AtkEventListener,
			Target = &ptr->AtkEventTarget,
			Param = 3u
		};
		AtkEventData atkEventData = default(AtkEventData);
		addon->ReceiveEvent(AtkEventType.ButtonClick, eventId, &atkEvent, &atkEventData);
		return true;
	}

	private bool SelectStringOptionUnsafe(string expected, bool enforceVocateTick = false)
	{
		return SelectStringOptionUnsafe(new string[1] { expected }, enforceVocateTick);
	}

	private unsafe bool SelectRetainerGearOptionUnsafe(string noMainArmOption, string standardOption)
	{
		if (!TryGetAddonUnsafe("SelectString", out var addon))
		{
			return false;
		}
		AddonMaster.SelectString.Entry[] entries = new AddonMaster.SelectString(addon).Entries;
		for (int i = 0; i < entries.Length; i++)
		{
			AddonMaster.SelectString.Entry entry = entries[i];
			try
			{
				string text = entry.Text;
				bool flag = LocalizedTextMatches(text, noMainArmOption);
				bool flag2 = !flag && LocalizedTextMatches(text, standardOption);
				if (!flag && !flag2)
				{
					continue;
				}
				entry.Select();
				Plugin.Log.Information(flag ? "[RetainerSetup] Selected the localized retainer gear entry with no main arm equipped." : "[RetainerSetup] Selected the localized standard retainer gear entry.");
				return true;
			}
			catch
			{
			}
		}
		return false;
	}

	private unsafe bool SelectStringOptionUnsafe(IReadOnlyCollection<string> expected, bool enforceVocateTick = false)
	{
		string[] array = expected.Where((string value) => !string.IsNullOrWhiteSpace(value)).ToArray();
		if (array.Length == 0)
		{
			return false;
		}
		if (!TryGetAddonUnsafe("SelectString", out var addon))
		{
			return false;
		}
		AddonMaster.SelectString.Entry[] entries = new AddonMaster.SelectString(addon).Entries;
		for (int num = 0; num < entries.Length; num++)
		{
			AddonMaster.SelectString.Entry entry = entries[num];
			try
			{
				if (!array.Any((string value) => LocalizedTextMatches(entry.Text, value)))
				{
					continue;
				}
				if (enforceVocateTick && !TryBeginVocateUiActionUnsafe())
				{
					return false;
				}
				entry.Select();
				return true;
			}
			catch
			{
			}
		}
		return false;
	}

	private bool SelectExactStringOptionUnsafe(string expected, out string selected)
	{
		return SelectExactStringOptionUnsafe(new string[1] { expected }, out selected);
	}

	private unsafe bool SelectExactStringOptionUnsafe(IReadOnlyCollection<string> expected, out string selected)
	{
		selected = string.Empty;
		string[] array = expected.Where((string value) => !string.IsNullOrWhiteSpace(value)).ToArray();
		if (array.Length == 0 || !TryGetAddonUnsafe("SelectString", out var addon))
		{
			return false;
		}
		try
		{
			AddonMaster.SelectString.Entry[] entries = new AddonMaster.SelectString(addon).Entries;
			if (entries.Length == 0)
			{
				return false;
			}
			string[] entryTexts = new string[entries.Length];
			for (int num = 0; num < entries.Length; num++)
			{
				entryTexts[num] = entries[num].Text;
				if (string.IsNullOrWhiteSpace(entryTexts[num]))
				{
					return false;
				}
			}
			int index;
			for (index = 0; index < entries.Length; index++)
			{
				if (array.Any((string value) => ExactLocalizedTextMatches(entryTexts[index], value)))
				{
					if (!TryBeginVocateUiActionUnsafe())
					{
						return false;
					}
					entries[index].Select();
					selected = entryTexts[index];
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private bool CloseOneVocateCreationAddonUnsafe(out string addonName)
	{
		addonName = string.Empty;
		string[] vocateCreationAddons = VocateCreationAddons;
		foreach (string text in vocateCreationAddons)
		{
			if (!(text == "InputString") && IsAddonReadyUnsafe(text))
			{
				if (!TryBeginVocateUiActionUnsafe())
				{
					return false;
				}
				addonName = text;
				return FireCallbackUnsafe(text, -1);
			}
		}
		return false;
	}

	private unsafe bool CloseAcceptedInputStringUnsafe()
	{
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)gameGui.GetAddonByName("InputString");
		if (ptr == null || !ptr->IsReady || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		ptr->Close(fireCallback: true);
		return true;
	}

	private unsafe bool CloseRejectedResidualInputStringUnsafe()
	{
		AtkUnitBase* ptr = (AtkUnitBase*)(nint)gameGui.GetAddonByName("InputString");
		if (ptr == null || !ptr->IsReady || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		ptr->Close(fireCallback: true);
		return true;
	}

	private unsafe bool TryGetInputStringCancellationPromptUnsafe(out string prompt)
	{
		prompt = string.Empty;
		if (!TryGetYesNoUnsafe(out AtkUnitBase* _, out string text))
		{
			return false;
		}
		string expected = ReadRawString("custom/000/CmnDefRetainerDesk_00009", 82u);
		if (!RetainerVocateCleanupLogic.MatchesInputCancellationPrompt(text, expected))
		{
			return false;
		}
		prompt = text;
		return true;
	}

	private unsafe bool ConfirmInputStringCancellationUnsafe()
	{
		if (!TryGetYesNoUnsafe(out AtkUnitBase* addon, out string text) || !RetainerVocateCleanupLogic.MatchesInputCancellationPrompt(text, ReadRawString("custom/000/CmnDefRetainerDesk_00009", 82u)) || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		new AddonMaster.SelectYesno(addon).Yes();
		return true;
	}

	private bool RequestInputStringCancellationUnsafe()
	{
		bool callbackAttempted;
		return RequestInputStringCancellationUnsafe(requireOccupiedEvent: false, out callbackAttempted);
	}

	private bool RequestInputStringCancellationUnsafe(bool requireOccupiedEvent, out bool callbackAttempted)
	{
		callbackAttempted = false;
		if (!IsAddonReadyUnsafe("InputString") || (requireOccupiedEvent && !IsOccupiedInNpcEventUnsafe()) || !TryBeginVocateUiActionUnsafe())
		{
			return false;
		}
		callbackAttempted = true;
		return FireCallbackUnsafe("InputString", -1);
	}

	private unsafe bool SelectRetainerListEntryUnsafe(string expectedName)
	{
		if (!TryGetAddonUnsafe("RetainerList", out var addon))
		{
			return false;
		}
		for (int i = 0; i < 10; i++)
		{
			int num = 3 + i * 10;
			if (num + 8 >= addon->AtkValuesCount)
			{
				break;
			}
			string a = ReadAtkValueString(addon->AtkValues[num]);
			AtkValue atkValue = addon->AtkValues[num + 8];
			if (atkValue.Type switch
			{
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool => atkValue.Byte != 0, 
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int => atkValue.Int != 0, 
				FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt => atkValue.UInt != 0, 
				_ => false, 
			} && string.Equals(a, expectedName, StringComparison.OrdinalIgnoreCase))
			{
				return FireCallbackUnsafe("RetainerList", 2, (uint)i, 0, 0);
			}
		}
		return false;
	}

	private unsafe VocateSelectStringObservation ObserveSelectStringUnsafe()
	{
		if (!TryGetVisibleAddonUnsafe("SelectString", out var addon))
		{
			return new VocateSelectStringObservation(Visible: false, Readable: false, Array.Empty<string>());
		}
		if (!addon->IsReady)
		{
			return new VocateSelectStringObservation(Visible: true, Readable: false, Array.Empty<string>());
		}
		try
		{
			AddonMaster.SelectString.Entry[] entries = new AddonMaster.SelectString(addon).Entries;
			if (entries.Length == 0)
			{
				return new VocateSelectStringObservation(Visible: true, Readable: false, Array.Empty<string>());
			}
			string[] array = new string[entries.Length];
			for (int i = 0; i < entries.Length; i++)
			{
				array[i] = entries[i].Text;
				if (string.IsNullOrWhiteSpace(array[i]))
				{
					return new VocateSelectStringObservation(Visible: true, Readable: false, Array.Empty<string>());
				}
			}
			return new VocateSelectStringObservation(Visible: true, Readable: true, array);
		}
		catch
		{
			return new VocateSelectStringObservation(Visible: true, Readable: false, Array.Empty<string>());
		}
	}

	private bool IsOwnedRetainerSelectStringUnsafe()
	{
		return IsOwnedRetainerSelectStringUnsafe(ObserveSelectStringUnsafe());
	}

	private bool IsOwnedRetainerSelectStringUnsafe(VocateSelectStringObservation observation)
	{
		if (!observation.Visible || !observation.Readable)
		{
			return false;
		}
		IReadOnlyCollection<string> known = GetOwnedRetainerSelectStringOptions();
		return observation.Entries.Any((string actual) => known.Any((string expected) => ExactLocalizedTextMatches(actual, expected)));
	}

	private IReadOnlyCollection<string> GetVocateSelectStringCleanupOptions()
	{
		return new string[3]
		{
			ReadRawString("custom/000/CmnDefRetainerDesk_00009", 12u),
			dataManager.GetExcelSheet<Addon>().GetRow(917u).Text.ExtractText(),
			dataManager.GetExcelSheet<Addon>().GetRow(2u).Text.ExtractText()
		};
	}

	private IReadOnlyCollection<string> GetOwnedRetainerSelectStringOptions()
	{
		List<string> list = new List<string>
		{
			ReadRawString("custom/000/CmnDefRetainerDesk_00009", 6u),
			ReadRawString("custom/000/CmnDefRetainerDesk_00009", 12u),
			dataManager.GetExcelSheet<Addon>().GetRow(2391u).Text.ExtractText(),
			dataManager.GetExcelSheet<Addon>().GetRow(2389u).Text.ExtractText(),
			dataManager.GetExcelSheet<Addon>().GetRow(2386u).Text.ExtractText(),
			dataManager.GetExcelSheet<Addon>().GetRow(917u).Text.ExtractText(),
			dataManager.GetExcelSheet<Addon>().GetRow(2u).Text.ExtractText()
		};
		for (uint num = 68u; num <= 73; num++)
		{
			list.Add(ReadRawString("custom/000/CmnDefRetainerDesk_00009", num));
		}
		return list;
	}

	private unsafe bool IsOwnedRetainerYesNoUnsafe()
	{
		if (!TryGetYesNoUnsafe(out AtkUnitBase* _, out string actual))
		{
			return false;
		}
		if (PromptMatches(actual, ReadRawString("custom/000/CmnDefRetainerDesk_00009", 82u), null))
		{
			return true;
		}
		return GetOwnedYesNoPrompts().Any((string expected) => PromptMatches(actual, expected, null));
	}

	private unsafe bool CloseOwnedRetainerYesNoUnsafe(out string matchedPrompt)
	{
		matchedPrompt = string.Empty;
		if (!TryGetYesNoUnsafe(out AtkUnitBase* addon, out string actual))
		{
			return false;
		}
		matchedPrompt = GetOwnedYesNoPrompts().FirstOrDefault((string expected) => PromptMatches(actual, expected, null)) ?? string.Empty;
		if (!string.IsNullOrWhiteSpace(matchedPrompt) && TryBeginVocateUiActionUnsafe())
		{
			return SelectYesnoTextHandler.ClickNoButton(addon);
		}
		return false;
	}

	private IEnumerable<string> GetOwnedYesNoPrompts()
	{
		yield return ReadRawString("custom/000/CmnDefRetainerDesk_00009", 84u);
		yield return ReadRawString("custom/000/CmnDefRetainerDesk_00009", 76u);
		yield return ReadRawString("custom/000/CmnDefRetainerDesk_00009", 83u);
		yield return ReadRawString("custom/000/CmnDefRetainerCall_00010", 208u);
		yield return dataManager.GetExcelSheet<Lobby>().GetRow(2044u).Text.ExtractText();
		yield return dataManager.GetExcelSheet<Lobby>().GetRow(2176u).Text.ExtractText();
		yield return dataManager.GetExcelSheet<Lobby>().GetRow(621u).Text.ExtractText();
	}

	private unsafe bool ClickYesNoIfMatchesUnsafe(string expected, bool yes, string? dynamicName)
	{
		if (!TryGetYesNoUnsafe(out AtkUnitBase* addon, out string text) || !PromptMatches(text, expected, dynamicName))
		{
			return false;
		}
		if (!yes)
		{
			return SelectYesnoTextHandler.ClickNoButton(addon);
		}
		return SelectYesnoTextHandler.ClickYesButton(addon);
	}

	private unsafe bool TryGetYesNoUnsafe(out AtkUnitBase* addon, out string text)
	{
		AtkUnitBasePtr addonByName = gameGui.GetAddonByName("SelectYesno");
		addon = (AtkUnitBase*)(nint)addonByName;
		if (addon == null || !addon->IsVisible || !addon->IsReady)
		{
			text = string.Empty;
			return false;
		}
		text = SelectYesnoTextHandler.GetDialogText(addon);
		return true;
	}

	private unsafe bool TryGetAddonUnsafe(string name, out AtkUnitBase* addon)
	{
		if (!TryGetVisibleAddonUnsafe(name, out addon))
		{
			return false;
		}
		return addon->IsReady;
	}

	private unsafe bool TryGetVisibleAddonUnsafe(string name, out AtkUnitBase* addon)
	{
		addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName(name);
		if (addon != null)
		{
			return addon->IsVisible;
		}
		return false;
	}

	private bool IsIndividualRetainerWindowReadyUnsafe()
	{
		if (!IsAddonReadyUnsafe("RetainerCharacter") && !IsAddonReadyUnsafe("RetainerTaskList"))
		{
			return IsAddonReadyUnsafe("RetainerTaskAsk");
		}
		return true;
	}

	private bool IsAtSummoningBellUnsafe(string bellName)
	{
		IPlayerCharacter player = objectTable.LocalPlayer;
		if (player == null)
		{
			return false;
		}
		return objectTable.Any((IGameObject gameObject) => string.Equals(gameObject.Name.ToString(), bellName, StringComparison.OrdinalIgnoreCase) && Vector3.Distance(player.Position, gameObject.Position) <= 4f);
	}

	private unsafe bool IsAddonReadyUnsafe(string name)
	{
		AtkUnitBase* addon;
		return TryGetAddonUnsafe(name, out addon);
	}

	private unsafe bool IsAddonVisibleUnsafe(string name)
	{
		AtkUnitBase* addon;
		return TryGetVisibleAddonUnsafe(name, out addon);
	}

	private bool IsAddonPresentUnsafe(string name)
	{
		return (nint)gameGui.GetAddonByName(name) != (nint)0;
	}

	private unsafe static List<string> ReadAddonStringValues(AtkUnitBase* addon, int start)
	{
		List<string> list = new List<string>();
		for (int i = start; i < addon->AtkValuesCount; i++)
		{
			AtkValue atkValue = addon->AtkValues[i];
			if (atkValue.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String || !atkValue.String.HasValue)
			{
				continue;
			}
			try
			{
				string text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated(new IntPtr((byte*)atkValue.String)).TextValue.Trim();
				if (!string.IsNullOrWhiteSpace(text))
				{
					list.Add(text);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	private unsafe static string ReadAtkValueString(AtkValue value)
	{
		if (value.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String || !value.String.HasValue)
		{
			return string.Empty;
		}
		try
		{
			return Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated(new IntPtr((byte*)value.String)).TextValue.Trim();
		}
		catch
		{
			return string.Empty;
		}
	}

	private unsafe bool FireCallbackUnsafe(string addonName, params object[] args)
	{
		if (!TryGetAddonUnsafe(addonName, out var addon))
		{
			return false;
		}
		List<nint> list = new List<nint>();
		try
		{
			AtkValue* ptr = stackalloc AtkValue[args.Length];
			for (int i = 0; i < args.Length; i++)
			{
				ptr[i] = default(AtkValue);
				object obj = args[i];
				if (!(obj is bool flag))
				{
					if (!(obj is int num))
					{
						if (!(obj is uint uInt))
						{
							if (!(obj is string text))
							{
								return false;
							}
							byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
							nint num2 = Marshal.AllocHGlobal(bytes.Length);
							Marshal.Copy(bytes, 0, num2, bytes.Length);
							list.Add(num2);
							ptr[i].SetString((byte*)num2);
						}
						else
						{
							ptr[i].SetUInt(uInt);
						}
					}
					else
					{
						ptr[i].SetInt(num);
					}
				}
				else
				{
					ptr[i].SetBool(flag);
				}
			}
			addon->FireCallback((uint)args.Length, ptr);
			return true;
		}
		finally
		{
			foreach (nint item in list)
			{
				Marshal.FreeHGlobal(item);
			}
		}
	}

	private unsafe bool OpenExactEventHandlerUnsafe(uint baseId, Vector3 expectedPosition, uint eventHandlerId)
	{
		IGameObject gameObject = objectTable.FirstOrDefault((IGameObject gameObject2) => gameObject2.BaseId == baseId && gameObject2.IsTargetable && Vector3.Distance(gameObject2.Position, expectedPosition) <= 3f);
		if (gameObject == null || !TryInteractWithAddressUnsafe(gameObject.Address, gameObject.Position, 6f))
		{
			return false;
		}
		GameObject* address = (GameObject*)gameObject.Address;
		EventHandlerSelector* ptr = EventHandlerSelector.Instance();
		if (ptr == null || ptr->Target == null)
		{
			return true;
		}
		if (ptr->Target != address)
		{
			return false;
		}
		EventFramework* ptr2 = EventFramework.Instance();
		if (ptr2 == null)
		{
			return false;
		}
		for (int num = 0; num < ptr->OptionsCount; num++)
		{
			FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler* handler = ptr->Options[num].Handler;
			if (handler != null && handler->Info.EventId.Id == eventHandlerId)
			{
				ptr2->InteractWithHandlerFromSelector(num);
				return true;
			}
		}
		return false;
	}

	private unsafe static bool IsExactShopOpenUnsafe(uint shopId)
	{
		AgentShop* ptr = AgentShop.Instance();
		EventFramework* ptr2 = EventFramework.Instance();
		if (ptr == null || ptr2 == null || !ptr->IsAgentActive() || ptr->EventReceiver == null || !ptr->IsAddonReady())
		{
			return false;
		}
		if (!ptr2->EventHandlerModule.EventHandlerMap.TryGetValuePointer(in shopId, out var value) || value == null || value->Value == null)
		{
			return false;
		}
		return ((ShopEventHandler.AgentProxy*)ptr->EventReceiver)->Handler == value->Value;
	}

	private unsafe static bool BuyItemFromExactShopUnsafe(uint shopId, uint itemId, int count)
	{
		EventFramework* ptr = EventFramework.Instance();
		if (ptr == null || !ptr->EventHandlerModule.EventHandlerMap.TryGetValuePointer(in shopId, out var value) || value == null || value->Value == null || value->Value->Info.EventId.ContentId != EventHandlerContent.Shop)
		{
			return false;
		}
		ShopEventHandler* value2 = (ShopEventHandler*)value->Value;
		for (int i = 0; i < value2->VisibleItemsCount; i++)
		{
			int num = value2->VisibleItems[i];
			if (value2->Items[num].ItemId == itemId)
			{
				value2->BuyItemIndex = num;
				value2->ExecuteBuy(count);
				return true;
			}
		}
		return false;
	}

	private unsafe static bool ShopTransactionInProgressUnsafe(uint shopId)
	{
		EventFramework* ptr = EventFramework.Instance();
		if (ptr == null || !ptr->EventHandlerModule.EventHandlerMap.TryGetValuePointer(in shopId, out var value) || value == null || value->Value == null || value->Value->Info.EventId.ContentId != EventHandlerContent.Shop)
		{
			return false;
		}
		return ((ShopEventHandler*)value->Value)->WaitingForTransactionToFinish;
	}

	private unsafe static void CloseShopUnsafe()
	{
		AgentShop* ptr = AgentShop.Instance();
		if (ptr != null && ptr->EventReceiver != null && ((ShopEventHandler.AgentProxy*)ptr->EventReceiver)->Handler != null)
		{
			AtkValue atkValue = default(AtkValue);
			AtkValue atkValue2 = default(AtkValue);
			((ShopEventHandler.AgentProxy*)ptr->EventReceiver)->Handler->CancelInteraction();
			atkValue2.SetInt(-1);
			ptr->ReceiveEvent(&atkValue, &atkValue2, 1u, 0uL);
		}
	}

	private void CloseOwnedShopUnsafe()
	{
		if (ownedShopId != 0 && IsExactShopOpenUnsafe(ownedShopId))
		{
			CloseShopUnsafe();
		}
	}

	private unsafe bool TryMoveStarterItemToRetainerMainHandUnsafe(uint itemId, RetainerStarterGearSlotCheckpoint preferredSlot, out RetainerStarterGearSlotCheckpoint? actualSource, out bool usedFallbackSource)
	{
		actualSource = null;
		usedFallbackSource = false;
		if (!IsRetainerGearWindowReadyUnsafe())
		{
			return false;
		}
		if (IsRetainerMainHandUnsafe(itemId))
		{
			return false;
		}
		List<RetainerStarterGearSlotCheckpoint> source = ReadStarterItemSlotsUnsafe(itemId);
		actualSource = source.FirstOrDefault((RetainerStarterGearSlotCheckpoint slot) => slot.ContainerType == preferredSlot.ContainerType && slot.Slot == preferredSlot.Slot);
		if (actualSource == null)
		{
			actualSource = source.FirstOrDefault();
			usedFallbackSource = actualSource != null;
		}
		if (actualSource == null)
		{
			return false;
		}
		InventoryManager* ptr = InventoryManager.Instance();
		InventoryType containerType = (InventoryType)actualSource.ContainerType;
		if (ptr == null || actualSource.ItemId != itemId || actualSource.Slot < 0 || !Enum.IsDefined(containerType))
		{
			return false;
		}
		InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(containerType);
		if (inventoryContainer == null || actualSource.Slot >= inventoryContainer->Size)
		{
			return false;
		}
		InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(actualSource.Slot);
		if (inventorySlot == null || inventorySlot->ItemId != itemId)
		{
			return false;
		}
		ptr->MoveItemSlot(containerType, (ushort)inventorySlot->Slot, InventoryType.RetainerEquippedItems, 0, a6: true);
		return true;
	}

	private bool IsRetainerGearWindowReadyUnsafe()
	{
		if (IsAddonReadyUnsafe("RetainerCharacter"))
		{
			return !IsAddonVisibleUnsafe("SelectString");
		}
		return false;
	}

	private unsafe static bool IsStarterItemAtSlotUnsafe(uint itemId, RetainerStarterGearSlotCheckpoint slotCheckpoint)
	{
		if (slotCheckpoint.ItemId != itemId || slotCheckpoint.Slot < 0)
		{
			return false;
		}
		InventoryType containerType = (InventoryType)slotCheckpoint.ContainerType;
		if (!Enum.IsDefined(containerType))
		{
			return false;
		}
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return false;
		}
		InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(containerType);
		if (inventoryContainer == null || slotCheckpoint.Slot >= inventoryContainer->Size)
		{
			return false;
		}
		InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(slotCheckpoint.Slot);
		if (inventorySlot != null)
		{
			return inventorySlot->ItemId == itemId;
		}
		return false;
	}

	private static List<RetainerStarterGearSlotCheckpoint> ValidateOwnedStarterGearSlotsUnsafe(uint itemId, IReadOnlyList<RetainerStarterGearSlotCheckpoint>? expectedSlots)
	{
		if (expectedSlots == null || expectedSlots.Count == 0)
		{
			return new List<RetainerStarterGearSlotCheckpoint>();
		}
		Dictionary<(int ContainerType, int Slot), RetainerStarterGearSlotCheckpoint> live = ReadStarterItemSlotsUnsafe(itemId).ToDictionary((RetainerStarterGearSlotCheckpoint slot) => (ContainerType: slot.ContainerType, Slot: slot.Slot));
		return (from slot in expectedSlots
			where slot != null && slot.ItemId == itemId && live.ContainsKey((slot.ContainerType, slot.Slot))
			group slot by (ContainerType: slot.ContainerType, Slot: slot.Slot) into @group
			select @group.First()).ToList();
	}

	private unsafe static List<RetainerStarterGearSlotCheckpoint> ReadStarterItemSlotsUnsafe(uint itemId)
	{
		InventoryManager* ptr = InventoryManager.Instance();
		List<RetainerStarterGearSlotCheckpoint> list = new List<RetainerStarterGearSlotCheckpoint>();
		if (ptr == null)
		{
			return list;
		}
		InventoryType[] array = new InventoryType[5]
		{
			InventoryType.ArmoryMainHand,
			InventoryType.Inventory1,
			InventoryType.Inventory2,
			InventoryType.Inventory3,
			InventoryType.Inventory4
		};
		foreach (InventoryType inventoryType in array)
		{
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(inventoryType);
			if (inventoryContainer == null)
			{
				continue;
			}
			for (int j = 0; j < inventoryContainer->Size; j++)
			{
				InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(j);
				if (inventorySlot != null && inventorySlot->ItemId == itemId)
				{
					list.Add(new RetainerStarterGearSlotCheckpoint
					{
						ContainerType = (int)inventoryType,
						Slot = inventorySlot->Slot,
						ItemId = itemId
					});
				}
			}
		}
		return list;
	}

	private unsafe static bool IsRetainerMainHandUnsafe(uint itemId)
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return false;
		}
		InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(InventoryType.RetainerEquippedItems);
		if (inventoryContainer == null)
		{
			return false;
		}
		InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(0);
		if (inventorySlot != null)
		{
			return inventorySlot->ItemId == itemId;
		}
		return false;
	}

	private unsafe static int CountStarterItemsUnsafe(uint itemId)
	{
		InventoryManager* ptr = InventoryManager.Instance();
		if (ptr == null)
		{
			return 0;
		}
		int num = 0;
		InventoryType[] array = new InventoryType[5]
		{
			InventoryType.ArmoryMainHand,
			InventoryType.Inventory1,
			InventoryType.Inventory2,
			InventoryType.Inventory3,
			InventoryType.Inventory4
		};
		foreach (InventoryType inventoryType in array)
		{
			InventoryContainer* inventoryContainer = ptr->GetInventoryContainer(inventoryType);
			if (inventoryContainer == null)
			{
				continue;
			}
			for (int j = 0; j < inventoryContainer->Size; j++)
			{
				InventoryItem* inventorySlot = inventoryContainer->GetInventorySlot(j);
				if (inventorySlot != null && inventorySlot->ItemId == itemId)
				{
					num += Math.Max(1, inventorySlot->Quantity);
				}
			}
		}
		return num;
	}

	private unsafe static uint? ReadVentureTokenCountUnsafe()
	{
		try
		{
			InventoryManager* ptr = InventoryManager.Instance();
			return (ptr == null) ? ((uint?)null) : new uint?((uint)ptr->GetInventoryItemCount(21072u, isHq: false, checkEquipped: true, checkArmory: true, 0));
		}
		catch
		{
			return null;
		}
	}

	private uint ResolveStarterMainHand(uint classJobId)
	{
		if (!dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(classJobId, out var row))
		{
			return 0u;
		}
		return RetainerStarterEquipmentLogic.ResolveWeatheredMainHand(row.Abbreviation.ExtractText(), from item in dataManager.GetExcelSheet<Item>(ClientLanguage.English)
			select new RetainerStarterItemCandidate(item.RowId, item.ClassJobCategory.Value.Name.ExtractText(), item.Name.ExtractText(), item.ItemUICategory.Value.Name.ExtractText()));
	}

	private uint ResolveGilShopId(uint itemId)
	{
		GilShopItem gilShopItem = dataManager.GetSubrowExcelSheet<GilShopItem>().Flatten().FirstOrDefault((GilShopItem item) => item.Item.RowId == itemId);
		if (gilShopItem.RowId != 0)
		{
			return dataManager.GetExcelSheet<GilShop>().GetRow(gilShopItem.RowId).RowId;
		}
		return 0u;
	}

	private uint ResolveVendorTopicSelectId(uint shopId)
	{
		return dataManager.GetExcelSheet<TopicSelect>().FirstOrDefault((TopicSelect topicSelect) => topicSelect.Shop.Any((RowRef shop) => shop.RowId == shopId)).RowId;
	}

	private string ReadRawString(string sheetName, uint rowId)
	{
		return dataManager.GetExcelSheet<RawRow>(dataManager.Language, sheetName).GetRow(rowId).ReadStringColumn(1)
			.ExtractText();
	}

	private static bool PromptMatches(string actual, string expected, string? dynamicName)
	{
		return RetainerSetupLogic.PromptMatches(actual, expected, dynamicName);
	}

	private static bool ExactLocalizedTextMatches(string actual, string expected)
	{
		if (!string.IsNullOrWhiteSpace(actual) && !string.IsNullOrWhiteSpace(expected))
		{
			return string.Equals(NormalizeText(actual), NormalizeText(expected), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static bool LocalizedTextMatches(string actual, string expected)
	{
		if (!string.IsNullOrWhiteSpace(expected))
		{
			if (!string.Equals(NormalizeText(actual), NormalizeText(expected), StringComparison.OrdinalIgnoreCase))
			{
				return NormalizeText(actual).StartsWith(NormalizeText(expected), StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private static string NormalizeText(string value)
	{
		return string.Join(' ', (value ?? string.Empty).Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
	}

	private static int ResolveRace(RetainerAppearanceRace race)
	{
		return race switch
		{
			RetainerAppearanceRace.Random => Random.Shared.Next(10, 18), 
			RetainerAppearanceRace.Hyur => 10, 
			RetainerAppearanceRace.Elezen => 11, 
			RetainerAppearanceRace.Lalafell => 12, 
			RetainerAppearanceRace.Miqote => 13, 
			RetainerAppearanceRace.Roegadyn => 14, 
			RetainerAppearanceRace.AuRa => 15, 
			RetainerAppearanceRace.Hrothgar => 16, 
			RetainerAppearanceRace.Viera => 17, 
			_ => 10, 
		};
	}

	private static int ResolveGender(RetainerGender gender)
	{
		return gender switch
		{
			RetainerGender.Random => (Random.Shared.Next(2) != 0) ? 9 : 0, 
			RetainerGender.Male => 0, 
			RetainerGender.Female => 9, 
			_ => 0, 
		};
	}

	private static int ResolveClan(RetainerClan clan)
	{
		return clan switch
		{
			RetainerClan.Random => Random.Shared.Next(1, 3), 
			RetainerClan.First => 1, 
			RetainerClan.Second => 2, 
			_ => 1, 
		};
	}

	private static uint ResolvePersonality(RetainerPersonality personality)
	{
		return personality switch
		{
			RetainerPersonality.Random => (uint)Random.Shared.Next(68, 74), 
			RetainerPersonality.Polite => 68u, 
			RetainerPersonality.Rough => 69u, 
			RetainerPersonality.Serious => 70u, 
			RetainerPersonality.Carefree => 71u, 
			RetainerPersonality.Independent => 72u, 
			RetainerPersonality.Lively => 73u, 
			_ => 68u, 
		};
	}

	public static uint ResolveRetainerClass(CharacterRetainerSetupChoice choice)
	{
		return RetainerStarterEquipmentLogic.ResolveClassJob(choice);
	}

	private static bool IsCombatClass(uint classJobId)
	{
		switch (classJobId)
		{
		case 1u:
		case 2u:
		case 3u:
		case 4u:
		case 5u:
		case 6u:
		case 7u:
		case 26u:
			return true;
		default:
			return false;
		}
	}

	private static bool IsValidRetainerClass(uint classJobId)
	{
		if (!IsCombatClass(classJobId))
		{
			if (classJobId >= 16)
			{
				return classJobId <= 18;
			}
			return false;
		}
		return true;
	}

	private static NpcRoute GetVocateRoute(RetainerStarterCity city)
	{
		return city switch
		{
			RetainerStarterCity.Gridania => new NpcRoute("Parnell", 1000233u, 2u, "New Gridania", 132u, 133u, new Vector3(168f, 15.5f, -94f), new Vector3(101f, 4.93f, 14f)), 
			RetainerStarterCity.Uldah => new NpcRoute("Chachabi", 1001963u, 9u, "Ul'dah - Steps of Nald", 130u, 131u, new Vector3(107.69f, 4.2f, -73.42f), new Vector3(101.57f, 4f, -104.66f)), 
			RetainerStarterCity.LimsaLominsa => new NpcRoute("Frydwyb", 1003275u, 8u, "Limsa Lominsa Lower Decks", 129u, 129u, new Vector3(-146.17f, 18.21f, 16.89f), null), 
			_ => throw new InvalidOperationException("An explicit starter city is required."), 
		};
	}

	private static NpcRoute GetVendorRoute(RetainerStarterCity city, bool combat)
	{
		switch (city)
		{
		case RetainerStarterCity.Gridania:
			if (combat)
			{
				return new NpcRoute("Geraint", 1000217u, 2u, "New Gridania", 132u, 133u, new Vector3(168.14f, 15.7f, -73.98f), new Vector3(101f, 4.93f, 14f));
			}
			return new NpcRoute("Admiranda", 1000218u, 2u, "New Gridania", 132u, 133u, new Vector3(162.75f, 15.7f, -58.83f), new Vector3(101f, 4.93f, 14f));
		case RetainerStarterCity.Uldah:
			if (combat)
			{
				return new NpcRoute("Jealous Juggernaut", 1000217u, 9u, "Ul'dah - Steps of Nald", 130u, 131u, new Vector3(137.97f, 4f, -9.6f), new Vector3(101.57f, 4f, -104.66f));
			}
			return new NpcRoute("Yoyobasa", 1001973u, 9u, "Ul'dah - Steps of Nald", 130u, 131u, new Vector3(150.02f, 4f, 0.25f), new Vector3(101.57f, 4f, -104.66f));
		case RetainerStarterCity.LimsaLominsa:
			if (combat)
			{
				return new NpcRoute("Faezghim", 1001205u, 8u, "Limsa Lominsa Lower Decks", 129u, 129u, new Vector3(-236.33f, 16.2f, 40.45f), null);
			}
			return new NpcRoute("Syneyhil", 1003254u, 8u, "Limsa Lominsa Lower Decks", 129u, 129u, new Vector3(-246.66f, 16.2f, 40.09f), null);
		default:
			throw new InvalidOperationException("An explicit starter city is required.");
		}
	}
}
