using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using QuestionableCompanion.Helpers;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public sealed class RetainerCreationService : IDisposable
{
	private sealed class UnsafeCleanupException : Exception
	{
		public UnsafeCleanupException(string message)
			: base(message)
		{
		}
	}

	private sealed class RetainerBatchSuspendedException : OperationCanceledException
	{
	}

	private sealed class RetainerBatchTerminalException : InvalidOperationException
	{
		public RetainerBatchTerminalException(string message, Exception? inner = null)
			: base(message, inner)
		{
		}
	}

	private enum RetainerCommandTimestamp
	{
		None,
		Relog,
		QuestStart,
		AutoRetainerStart
	}

	private readonly object sync = new object();

	private readonly Configuration configuration;

	private readonly AutoRetainerIPC autoRetainer;

	private readonly RetainerGameInteractionService game;

	private readonly QuestionableIPC questionable;

	private readonly RetainerNameGenerator names;

	private readonly IFramework framework;

	private readonly ICommandManager commandManager;

	private readonly IPluginLog log;

	private CancellationTokenSource? cancellationSource;

	private readonly CancellationTokenSource disposalCancellationSource = new CancellationTokenSource();

	private Task? runnerTask;

	private RetainerCreationSnapshot snapshot = RetainerCreationSnapshot.Idle;

	private bool disposalRequested;

	private bool textAdvanceEnabledAfterToolsForCurrentCharacter;

	public RetainerCreationSnapshot Snapshot
	{
		get
		{
			lock (sync)
			{
				return snapshot._003CClone_003E_0024();
			}
		}
	}

	internal bool HasPendingRecovery
	{
		get
		{
			lock (sync)
			{
				Task task = runnerTask;
				return (task != null && !task.IsCompleted) || RetainerBatchHandoffLogic.Validate(configuration.RetainerBatchHandoff, DateTime.UtcNow) == RetainerBatchHandoffValidation.Valid;
			}
		}
	}

	public RetainerCreationService(Configuration configuration, AutoRetainerIPC autoRetainer, RetainerGameInteractionService game, QuestionableIPC questionable, RetainerNameGenerator names, IFramework framework, ICommandManager commandManager, IPluginLog log)
	{
		this.configuration = configuration;
		this.autoRetainer = autoRetainer;
		this.game = game;
		this.questionable = questionable;
		this.names = names;
		this.framework = framework;
		this.commandManager = commandManager;
		this.log = log;
		RecoverOrClearHandoffOnLoad();
	}

	public bool TryStart(IEnumerable<RetainerSetupTarget> requestedTargets, IEnumerable<string> knownXadbRetainerNames, out string error)
	{
		if (!questionable.TryEnsureAvailableSilent() || !questionable.ValidateFeatureCompatibility())
		{
			error = (string.IsNullOrWhiteSpace(questionable.CompatibilityMessage) ? "WigglyMuffin's compatible Questionable version is required for Retainer Setup." : questionable.CompatibilityMessage);
			return false;
		}
		RetainerSetupTarget[] array = (from target in requestedTargets
			where target.ContentId != 0L && !string.IsNullOrWhiteSpace(target.CharacterKey)
			group target by target.ContentId into @group
			select @group.First()with
			{
				Choice = CloneChoice(@group.First().Choice)
			}).ToArray();
		RetainerSetupTarget[] array2 = array;
		foreach (RetainerSetupTarget retainerSetupTarget in array2)
		{
			if (string.IsNullOrWhiteSpace(retainerSetupTarget.Choice.CharacterKey))
			{
				retainerSetupTarget.Choice.CharacterKey = retainerSetupTarget.CharacterKey;
			}
		}
		if (array.Length == 0)
		{
			error = "Select at least one confirmed-empty, retryable, or tracked partial setup.";
			return false;
		}
		lock (sync)
		{
			Task task = runnerTask;
			if (task != null && !task.IsCompleted)
			{
				error = "A retainer setup batch is already running.";
				return false;
			}
			if (configuration.RetainerBatchHandoff != null)
			{
				RetainerBatchHandoffValidation retainerBatchHandoffValidation = RetainerBatchHandoffLogic.Validate(configuration.RetainerBatchHandoff, DateTime.UtcNow);
				if (retainerBatchHandoffValidation == RetainerBatchHandoffValidation.Valid)
				{
					error = "A durable retainer batch is still pending recovery; reload the plugin or cancel that batch first.";
					return false;
				}
				RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
				string batchId = retainerBatchHandoff.BatchId;
				configuration.RetainerBatchHandoff = null;
				try
				{
					configuration.Save();
					log.Warning($"[RetainerSetup] Cleared {retainerBatchHandoffValidation.ToString().ToLowerInvariant()} handoff {batchId} before an explicit new batch.");
				}
				catch (Exception ex)
				{
					configuration.RetainerBatchHandoff = retainerBatchHandoff;
					error = "The stale retainer handoff could not be cleared: " + ex.Message;
					log.Error($"[RetainerSetup] Stale handoff clearance failed: {ex}");
					return false;
				}
			}
			cancellationSource?.Dispose();
			cancellationSource = new CancellationTokenSource();
			RetainerSetupConfiguration retainerSetupConfiguration = CloneSettings(configuration.RetainerSetup);
			string[] array3 = (from name in knownXadbRetainerNames
				where !string.IsNullOrWhiteSpace(name)
				select name.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
			DateTime utcNow = DateTime.UtcNow;
			configuration.RetainerBatchHandoff = RetainerBatchHandoffLogic.Create(ToFrozenSettings(retainerSetupConfiguration, array3), array.Select((RetainerSetupTarget target) => new RetainerBatchTargetCheckpoint
			{
				ContentId = target.ContentId,
				CharacterKey = target.CharacterKey,
				Choice = CloneChoice(target.Choice),
				XadbBaselineUpdatedUtc = target.XadbSnapshot.SourceUpdatedUtc,
				AllowSameBatchRequeue = (!configuration.RetainerSetup.Checkpoints.TryGetValue(target.ContentId, out CharacterRetainerSetupCheckpoint value) || !value.DisallowAutomaticRequeue)
			}), utcNow);
			try
			{
				configuration.Save();
			}
			catch (Exception ex2)
			{
				configuration.RetainerBatchHandoff = null;
				cancellationSource.Dispose();
				cancellationSource = null;
				error = "The retainer batch handoff could not be persisted: " + ex2.Message;
				log.Error($"[RetainerSetup] Handoff creation failed: {ex2}");
				return false;
			}
			log.Information($"[RetainerSetup] Created durable batch handoff {configuration.RetainerBatchHandoff.BatchId} for {array.Length} ordered target(s).");
			DisableTextAdvanceForBatchStart();
			SetSnapshot(new RetainerCreationSnapshot(IsRunning: true, string.Empty, "Starting", 0, array.Length, "Disabling AutoRetainer schedulers before the batch.", CanCancel: true));
			runnerTask = RunBatchObservedAsync(array, retainerSetupConfiguration, array3, cancellationSource.Token, recovering: false);
		}
		error = string.Empty;
		return true;
	}

	private void DisableTextAdvanceForBatchStart()
	{
		try
		{
			if (commandManager.ProcessCommand("/at n"))
			{
				textAdvanceEnabledAfterToolsForCurrentCharacter = false;
				log.Information("[RetainerSetup] Sent /at n at retainer batch start; QST owns retainer dialogue progression.");
			}
			else
			{
				log.Warning("[RetainerSetup] /at n was not accepted at retainer batch start.");
			}
		}
		catch (Exception ex)
		{
			log.Warning("[RetainerSetup] Could not send /at n at retainer batch start: " + ex.Message);
		}
	}

	public void Cancel()
	{
		bool cancellationRequested = false;
		bool suspendedByDisposal = false;
		RetainerBatchRecoveryStage recoveryStage = RetainerBatchRecoveryStage.Created;
		RetainerBatchPendingAction pendingAction = RetainerBatchPendingAction.None;
		DateTime updatedUtc = DateTime.MinValue;
		RetainerCreationSnapshot retainerCreationSnapshot;
		CancellationTokenSource cancellationTokenSource;
		RetainerBatchHandoffCheckpoint retainerBatchHandoff;
		lock (sync)
		{
			retainerCreationSnapshot = snapshot;
			cancellationTokenSource = cancellationSource;
			retainerBatchHandoff = configuration.RetainerBatchHandoff;
			if (retainerBatchHandoff != null)
			{
				cancellationRequested = retainerBatchHandoff.CancellationRequested;
				suspendedByDisposal = retainerBatchHandoff.SuspendedByDisposal;
				recoveryStage = retainerBatchHandoff.RecoveryStage;
				pendingAction = retainerBatchHandoff.PendingAction;
				updatedUtc = retainerBatchHandoff.UpdatedUtc;
				retainerBatchHandoff.CancellationRequested = true;
				retainerBatchHandoff.SuspendedByDisposal = false;
				retainerBatchHandoff.RecoveryStage = RetainerBatchRecoveryStage.Cancelling;
				retainerBatchHandoff.PendingAction = RetainerBatchPendingAction.Cleanup;
				retainerBatchHandoff.UpdatedUtc = DateTime.UtcNow;
			}
			if (snapshot.IsRunning)
			{
				snapshot = retainerCreationSnapshot with
				{
					CurrentStage = "Cancelling",
					LastMessage = "Cancellation persisted; performing guarded cleanup.",
					CanCancel = false
				};
			}
		}
		try
		{
			configuration.Save();
			log.Information("[RetainerSetup] Explicit cancellation persisted; cleanup is terminal and reload-safe.");
			cancellationTokenSource?.Cancel();
		}
		catch (Exception ex)
		{
			lock (sync)
			{
				if (retainerBatchHandoff != null && configuration.RetainerBatchHandoff == retainerBatchHandoff)
				{
					retainerBatchHandoff.CancellationRequested = cancellationRequested;
					retainerBatchHandoff.SuspendedByDisposal = suspendedByDisposal;
					retainerBatchHandoff.RecoveryStage = recoveryStage;
					retainerBatchHandoff.PendingAction = pendingAction;
					retainerBatchHandoff.UpdatedUtc = updatedUtc;
				}
				snapshot = retainerCreationSnapshot with
				{
					LastMessage = "Cancellation could not be persisted; the runner remains active: " + ex.Message,
					CanCancel = true
				};
			}
			log.Error($"[RetainerSetup] Explicit cancellation persistence failed: {ex}");
		}
	}

	public IReadOnlyList<string> GenerateSamples(IEnumerable<string> unavailableNames)
	{
		return names.GenerateSamples(configuration.RetainerSetup.Appearance, configuration.RetainerSetup.Gender, configuration.RetainerSetup.Clan, unavailableNames);
	}

	private async Task RunBatchObservedAsync(IReadOnlyList<RetainerSetupTarget> targets, RetainerSetupConfiguration settings, IReadOnlyCollection<string> knownXadbRetainerNames, CancellationToken token, bool recovering)
	{
		int completed = configuration.RetainerBatchHandoff?.CompletedTargetContentIds.Count ?? 0;
		string finalMessage = "Retainer setup batch ended.";
		RetainerBatchLifecycleResult finalResult = RetainerBatchLifecycleResult.TerminalFailure;
		string finalCharacter = string.Empty;
		string lastFailedCharacter = string.Empty;
		string lastFailedStage = string.Empty;
		string lastFailedError = string.Empty;
		int lastFailedProgress = 0;
		bool shutdownVerified = false;
		bool reloadDialogsNeedReconciliation = recovering;
		try
		{
			if (recovering)
			{
				await WaitForRecoveryDependenciesAsync(token);
				if (RequireValidHandoff().CancellationRequested)
				{
					await CompletePersistedCancellationAsync(targets, token);
					shutdownVerified = true;
					finalResult = RetainerBatchLifecycleResult.Cancelled;
					finalMessage = "Retainer setup batch cancelled after reload and guarded cleanup.";
					return;
				}
			}
			await MarkHandoffActionAsync(RetainerBatchRecoveryStage.DisablingSchedulers, RetainerBatchPendingAction.DisableSchedulers, "disabling AutoRetainer schedulers");
			bool flag = !autoRetainer.SetMultiModeEnabled(enabled: false);
			if (!flag)
			{
				flag = !(await autoRetainer.DisableAllFunctionsAsync());
			}
			if (flag)
			{
				throw new InvalidOperationException("AutoRetainer scheduler-disable capabilities are unavailable; no retainer setup was started.");
			}
			await autoRetainer.SendCommandAsync("/ays d");
			flag = !autoRetainer.TryGetMultiModeEnabled(out var enabled) || enabled;
			if (!flag)
			{
				flag = !(await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(10L), token));
			}
			if (flag)
			{
				throw new InvalidOperationException("AutoRetainer MultiMode/idle state could not be verified before the batch; no retainer setup was started.");
			}
			await MarkHandoffBoundaryAsync(RetainerBatchRecoveryStage.SchedulersDisabled, "AutoRetainer schedulers are disabled and stably idle");
			HashSet<string> unavailableNames = new HashSet<string>(knownXadbRetainerNames, StringComparer.OrdinalIgnoreCase);
			foreach (CharacterRetainerSetupCheckpoint value4 in configuration.RetainerSetup.Checkpoints.Values)
			{
				unavailableNames.UnionWith(value4.ReservedNames);
				unavailableNames.UnionWith(value4.Retainers.Select((TrackedRetainerCheckpoint retainer) => retainer.Name));
			}
			Dictionary<ulong, RetainerSetupTarget> targetsByContentId = targets.ToDictionary((RetainerSetupTarget retainerSetupTarget) => retainerSetupTarget.ContentId);
			while (RequireValidHandoff().RemainingQueue.Count > 0)
			{
				token.ThrowIfCancellationRequested();
				RetainerBatchQueueEntry retainerBatchQueueEntry = RequireValidHandoff().RemainingQueue[0];
				if (!targetsByContentId.TryGetValue(retainerBatchQueueEntry.ContentId, out var target))
				{
					throw new RetainerBatchTerminalException($"Persisted queue target {retainerBatchQueueEntry.ContentId} is not present in the frozen batch.");
				}
				bool isRequeue = retainerBatchQueueEntry.IsRequeue;
				await SetCurrentHandoffTargetAsync(target);
				SetSnapshot(new RetainerCreationSnapshot(IsRunning: true, target.CharacterKey, "Relogging", completed, targets.Count, isRequeue ? ("Revalidating " + target.CharacterKey + " once at the end of this batch.") : ("Verifying exact identity for " + target.CharacterKey + "."), CanCancel: true));
				CharacterRetainerSetupCheckpoint checkpoint = GetOrCreateCheckpoint(target);
				try
				{
					checkpoint.State = RetainerCheckpointState.Running;
					checkpoint.Disposition = RetainerCheckpointDisposition.Unclassified;
					checkpoint.CleanupVerified = false;
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					await RelogAndVerifyAsync(target, recovering, token);
					await MarkHandoffBoundaryAsync(RetainerBatchRecoveryStage.ExactLoginConfirmed, "exact login confirmed for " + target.CharacterKey);
					if (reloadDialogsNeedReconciliation)
					{
						await MarkHandoffActionAsync(RetainerBatchRecoveryStage.ReconcilingRoster, RetainerBatchPendingAction.ReadNativeRoster, "reconciling known retainer dialogs after reload for " + target.CharacterKey);
						if (!(await game.ReconcileAndCloseKnownDialogsAfterReloadAsync(target.ContentId, target.CharacterKey, token)))
						{
							throw new UnsafeCleanupException("Known retainer dialogs for " + target.CharacterKey + " could not be reconciled after reload.");
						}
						reloadDialogsNeedReconciliation = false;
						await MarkHandoffBoundaryAsync(RetainerBatchRecoveryStage.ExactLoginConfirmed, "known retainer dialogs reconciled after reload for " + target.CharacterKey);
					}
					SetSnapshot(Snapshot with
					{
						CurrentStage = "Waiting for safe state",
						LastMessage = "Verifying that " + target.CharacterKey + " is idle and safe for retainer automation."
					});
					await MarkHandoffActionAsync(RetainerBatchRecoveryStage.WaitingForSafeState, RetainerBatchPendingAction.ReadNativeRoster, "waiting for a stable safe state for " + target.CharacterKey);
					await game.WaitForSafeStartingStateAsync(target.ContentId, target.CharacterKey, token);
					await RunCharacterAsync(target, checkpoint, settings, unavailableNames, token);
					await MarkHandoffActionAsync(RetainerBatchRecoveryStage.CleaningUp, RetainerBatchPendingAction.Cleanup, "cleaning owned state for " + target.CharacterKey);
					if (!(await CleanupCharacterAsync(target, checkpoint, token)))
					{
						throw new UnsafeCleanupException("Cleanup for " + target.CharacterKey + " was not verified; the batch will not relog another character.");
					}
					checkpoint.Disposition = (checkpoint.IsComplete ? RetainerCheckpointDisposition.Complete : RetainerCheckpointDisposition.ResumablePartial);
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					completed++;
					await FinishCurrentQueueEntryAsync(target.ContentId, completedSuccessfully: true, requeue: false);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					if (disposalRequested)
					{
						throw new RetainerBatchSuspendedException();
					}
					checkpoint.State = RetainerCheckpointState.Failed;
					checkpoint.LastError = "Cancelled by operator";
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					if (!(await CleanupCharacterAsync(target, checkpoint, disposalCancellationSource.Token)))
					{
						throw new UnsafeCleanupException("Cleanup after cancellation could not prove AutoRetainer idle and owned windows closed.");
					}
					checkpoint.Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					await ClearHandoffAsync("explicit cancellation cleanup completed", RetainerBatchLifecycleEvent.ExplicitCancellationCompleted);
					shutdownVerified = true;
					throw;
				}
				catch (UnsafeCleanupException)
				{
					throw;
				}
				catch (Exception ex3)
				{
					string failedStage = Snapshot.CurrentStage;
					checkpoint.State = RetainerCheckpointState.Failed;
					checkpoint.LastError = ex3.Message;
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					log.Error($"[RetainerSetup] {target.CharacterKey} failed at {checkpoint.ProgressPercent}%: {ex3}");
					SetSnapshot(Snapshot with
					{
						CurrentStage = "Recovering",
						LastMessage = target.CharacterKey + ": " + ex3.Message
					});
					if (!(await CleanupCharacterAsync(target, checkpoint, token)))
					{
						checkpoint.Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
						checkpoint.UpdatedUtc = DateTime.UtcNow;
						await PersistAsync();
						throw new UnsafeCleanupException("Cleanup for " + target.CharacterKey + " was not verified; the batch will not relog another character.");
					}
					checkpoint.Disposition = RetainerSetupLogic.ClassifyFailure(checkpoint, ex3 is RetainerTerminalCharacterException, cancellationRequested: false, cleanupVerified: true);
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					lastFailedCharacter = target.CharacterKey;
					lastFailedStage = failedStage;
					lastFailedError = ex3.Message;
					lastFailedProgress = checkpoint.ProgressPercent;
					if (ex3 is RetainerTerminalCharacterException)
					{
						await ClearHandoffAsync("terminal ownership/data conflict for " + target.CharacterKey + ": " + ex3.Message, RetainerBatchLifecycleEvent.TerminalConflict);
						throw new RetainerBatchTerminalException(ex3.Message, ex3);
					}
					if (!RetainerBatchRequeuePolicy.ShouldRequeueAtEnd(checkpoint, isRequeue || !CanCurrentTargetRequeue(target.ContentId), cancellationRequested: false))
					{
						await FinishCurrentQueueEntryAsync(target.ContentId, completedSuccessfully: false, requeue: false);
					}
					else
					{
						SetSnapshot(Snapshot with
						{
							LastMessage = target.CharacterKey + " had no retainer side effects and cleanup was verified; it will be revalidated once at the end of this batch."
						});
						await FinishCurrentQueueEntryAsync(target.ContentId, completedSuccessfully: false, requeue: true);
					}
				}
				target = null;
			}
			if (disposalRequested)
			{
				throw new RetainerBatchSuspendedException();
			}
			if (RequireValidHandoff().CancellationRequested || token.IsCancellationRequested)
			{
				await CompletePersistedCancellationAsync(targets, disposalCancellationSource.Token);
				shutdownVerified = true;
				finalResult = RetainerBatchLifecycleResult.Cancelled;
				finalMessage = "Retainer setup batch cancelled after guarded cleanup.";
				return;
			}
			RetainerBatchLifecycleResult processedResult = RetainerBatchResultLogic.FromProcessedCounts(completed, targets.Count);
			shutdownVerified = await ShutdownSchedulersAsync(disposalCancellationSource.Token);
			if (configuration.RetainerBatchHandoff?.CancellationRequested ?? false)
			{
				await CompletePersistedCancellationAsync(targets, disposalCancellationSource.Token);
				shutdownVerified = true;
				finalResult = RetainerBatchLifecycleResult.Cancelled;
				finalMessage = "Retainer setup batch cancelled after guarded cleanup.";
				return;
			}
			if (!shutdownVerified)
			{
				finalResult = RetainerBatchLifecycleResult.UnsafeCleanup;
				finalMessage = "Retainer setup reached the final target, but scheduler shutdown was not fully verified.";
			}
			else if (processedResult == RetainerBatchLifecycleResult.Complete)
			{
				finalResult = RetainerBatchLifecycleResult.Complete;
				finalCharacter = "All selected characters";
				finalMessage = $"Retainer setup batch complete: {completed}/{targets.Count} characters reached their configured stop point.";
			}
			else
			{
				finalResult = RetainerBatchLifecycleResult.ProcessedWithFailures;
				finalCharacter = lastFailedCharacter;
				string value = (string.IsNullOrWhiteSpace(lastFailedStage) ? "an unknown stage" : lastFailedStage);
				string value2 = (string.IsNullOrWhiteSpace(lastFailedError) ? "No error detail was retained." : lastFailedError);
				finalMessage = $"Retainer setup processed with failures: {completed}/{targets.Count} characters reached their configured stop point. {lastFailedCharacter} failed at {value}: {value2} The character remains resumable at {lastFailedProgress}%.";
			}
			await ClearHandoffAsync(finalResult switch
			{
				RetainerBatchLifecycleResult.Complete => "verified normal completion", 
				RetainerBatchLifecycleResult.ProcessedWithFailures => "processed every target with one or more preserved failures", 
				_ => "final scheduler cleanup was unsafe", 
			}, finalResult switch
			{
				RetainerBatchLifecycleResult.Complete => RetainerBatchLifecycleEvent.NormalCompletion, 
				RetainerBatchLifecycleResult.ProcessedWithFailures => RetainerBatchLifecycleEvent.ProcessedWithFailures, 
				_ => RetainerBatchLifecycleEvent.UnsafeCleanup, 
			});
		}
		catch (RetainerBatchSuspendedException)
		{
			finalResult = RetainerBatchLifecycleResult.Suspended;
			finalCharacter = Snapshot.CurrentCharacter;
			finalMessage = "Retainer setup suspended by plugin disposal; durable recovery is preserved.";
			log.Information("[RetainerSetup] In-memory runner suspended by disposal without operator-cancel classification or cleanup.");
		}
		catch (OperationCanceledException) when (disposalRequested)
		{
			finalResult = RetainerBatchLifecycleResult.Suspended;
			finalCharacter = Snapshot.CurrentCharacter;
			finalMessage = "Retainer setup suspended by plugin disposal; durable recovery is preserved.";
			log.Information("[RetainerSetup] In-memory runner suspended by disposal before a character stage; no cleanup was started.");
		}
		catch (OperationCanceledException)
		{
			finalResult = RetainerBatchLifecycleResult.Cancelled;
			finalCharacter = Snapshot.CurrentCharacter;
			finalMessage = "Retainer setup batch cancelled after guarded cleanup.";
			if (configuration.RetainerBatchHandoff?.CancellationRequested ?? false)
			{
				await CompletePersistedCancellationAsync(targets, disposalCancellationSource.Token);
				shutdownVerified = true;
			}
		}
		catch (UnsafeCleanupException ex7)
		{
			finalResult = RetainerBatchLifecycleResult.UnsafeCleanup;
			finalCharacter = Snapshot.CurrentCharacter;
			finalMessage = ex7.Message;
			log.Error("[RetainerSetup] Batch terminated on unverified cleanup: " + ex7.Message);
			await ClearHandoffAsync(ex7.Message, RetainerBatchLifecycleEvent.UnsafeCleanup);
		}
		catch (RetainerBatchTerminalException ex8)
		{
			finalResult = RetainerBatchLifecycleResult.TerminalFailure;
			finalCharacter = Snapshot.CurrentCharacter;
			finalMessage = "Retainer setup batch terminated: " + ex8.Message;
			log.Error($"[RetainerSetup] Terminal batch conflict: {ex8}");
			if (configuration.RetainerBatchHandoff != null)
			{
				try
				{
					await ClearHandoffAsync(ex8.Message, RetainerBatchLifecycleEvent.TerminalConflict);
				}
				catch (Exception value3)
				{
					log.Error($"[RetainerSetup] Terminal handoff clearance failed: {value3}");
				}
			}
		}
		catch (Exception ex9)
		{
			finalResult = RetainerBatchLifecycleResult.TerminalFailure;
			finalCharacter = Snapshot.CurrentCharacter;
			finalMessage = "Retainer setup batch terminated: " + ex9.Message;
			log.Error($"[RetainerSetup] Unexpected batch failure: {ex9}");
		}
		finally
		{
			if (!disposalRequested && !shutdownVerified && configuration.RetainerBatchHandoff != null)
			{
				try
				{
					await ShutdownSchedulersAsync(disposalCancellationSource.Token);
				}
				catch (Exception ex10)
				{
					log.Error("[RetainerSetup] Final scheduler shutdown failed: " + ex10.Message);
				}
			}
			if (string.IsNullOrWhiteSpace(finalCharacter))
			{
				finalCharacter = ((finalResult == RetainerBatchLifecycleResult.Complete) ? "All selected characters" : "Selected batch");
			}
			SetSnapshot(new RetainerCreationSnapshot(IsRunning: false, finalCharacter, RetainerBatchResultLogic.PresentationStage(finalResult), completed, targets.Count, finalMessage, CanCancel: false));
		}
	}

	private async Task RunCharacterAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, RetainerSetupConfiguration settings, ISet<string> unavailableNames, CancellationToken token)
	{
		if (checkpoint.IsComplete)
		{
			SetSnapshot(Snapshot with
			{
				CurrentStage = "Complete",
				LastMessage = target.CharacterKey + " is already complete."
			});
			return;
		}
		checkpoint.State = RetainerCheckpointState.Running;
		checkpoint.Disposition = RetainerCheckpointDisposition.Unclassified;
		checkpoint.LastError = string.Empty;
		checkpoint.CleanupVerified = false;
		checkpoint.AutoRetainerResetIssued = false;
		checkpoint.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
		RetainerStarterCity retainerStarterCity = ((checkpoint.ResolvedCity != RetainerStarterCity.Automatic) ? checkpoint.ResolvedCity : (await game.ResolveCityAsync(settings.City, token)));
		RetainerStarterCity city = retainerStarterCity;
		checkpoint.ResolvedCity = city;
		checkpoint.PendingCheckpoint = RetainerStopAfter.ArrivedAtVocate;
		await PersistAsync();
		SetStage(target, "Arriving at Vocate", "Routing to the character's validated Vocate and reading entitlement data.");
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.ArrivingAtVocate, RetainerBatchPendingAction.NavigateToVocate, "routing " + target.CharacterKey + " to the Vocate");
		RetainerEntitlementInfo vocateEntitlements = await ExecuteRetriableStageAsync(target, "Vocate navigation and entitlement acquisition", () => game.ArriveAtVocateAsync(city, target.ContentId, target.CharacterKey, token), token);
		SetStage(target, "Reading native roster", "Using Henchman's native RetainerManager entitlement and roster flow before hiring.");
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.NativeProofBeforeXadb, RetainerBatchPendingAction.ReadNativeRoster, "reading the native roster for " + target.CharacterKey);
		StableNativeRetainerEvidence nativeEvidence = await game.ReadStableNativeRosterAsync(target.ContentId, target.CharacterKey, token);
		ValidateNativeRosterAtVocate(vocateEntitlements, nativeEvidence);
		string pendingHireName = GetPendingHireName(target.ContentId);
		ReconcileReservedHires(checkpoint, target.Choice, nativeEvidence.Roster, pendingHireName);
		ValidateOwnedRoster(checkpoint, nativeEvidence.Roster);
		unavailableNames.UnionWith(nativeEvidence.Roster.Select((LiveRetainerInfo retainer) => retainer.Name));
		await ClearPendingHireAsync(target.ContentId);
		RetainerNativeCapacityPlan retainerNativeCapacityPlan = RetainerNativeCapacityLogic.Plan(vocateEntitlements.CurrentCount, vocateEntitlements.MaximumCount, nativeEvidence.Snapshot, nativeEvidence.Roster.Count, checkpoint.Retainers.Count, checkpoint.IntendedRetainerCount);
		if (!retainerNativeCapacityPlan.IsValid)
		{
			throw new RetainerTerminalCharacterException(retainerNativeCapacityPlan.Error);
		}
		checkpoint.IntendedRetainerCount = retainerNativeCapacityPlan.IntendedCount;
		log.Information($"[RetainerSetup] Stable native capacity for {target.CharacterKey}: {nativeEvidence.Snapshot.CurrentCount}/{nativeEvidence.Snapshot.MaximumCount}; QST owns {checkpoint.Retainers.Count}, so {retainerNativeCapacityPlan.RemainingHires} hire(s) remain.");
		checkpoint.PendingCheckpoint = null;
		RefreshLastVerifiedCheckpoint(checkpoint);
		await PersistAsync();
		if (await StopIfRequestedAsync(checkpoint, settings.StopAfter, RetainerStopAfter.ArrivedAtVocate))
		{
			return;
		}
		SetStage(target, "Hiring retainers", "Filling every currently available entitled slot with owned, recorded names.");
		while (checkpoint.Retainers.Count < checkpoint.IntendedRetainerCount)
		{
			token.ThrowIfCancellationRequested();
			if (!TryCreateInitialNamingSessions(settings, unavailableNames, out RetainerNamingSession original, out RetainerNamingSession reversed))
			{
				throw new InvalidOperationException("Unique retainer naming-session generation exhausted its bounded attempts.");
			}
			List<RetainerNamingSession> sessions = new List<RetainerNamingSession> { original, reversed };
			RetainerNamingSessionResult namingResult = null;
			for (int sessionIndex = 0; sessionIndex < 3; sessionIndex++)
			{
				if (sessionIndex == 2)
				{
					if (!TryCreateFreshNamingSession(settings, unavailableNames, out RetainerNamingSession session))
					{
						throw new InvalidOperationException("Fresh third naming-session generation exhausted its bounded attempts.");
					}
					sessions.Add(session);
				}
				RetainerNamingSession retainerNamingSession = sessions[sessionIndex];
				SetSnapshot(Snapshot with
				{
					LastMessage = $"Naming session {sessionIndex + 1}/{3}: {string.Join(", ", retainerNamingSession.Candidates)}"
				});
				namingResult = await HireNamingSessionWithRecoveryAsync(target, checkpoint, city, settings, retainerNamingSession, unavailableNames, token);
				if (RetainerNamingSessionOutcomeLogic.MustStopWithoutOuterRecovery(namingResult.Outcome))
				{
					if (RetainerNamingSessionOutcomeLogic.PreservesAcceptedSideEffect(namingResult.Outcome) && namingResult.Retainer != null)
					{
						TrackAcceptedRetainer(checkpoint, target.Choice, namingResult.Retainer);
						checkpoint.PendingCheckpoint = null;
						RefreshLastVerifiedCheckpoint(checkpoint);
					}
					string closureError = "Naming session closure was not verified; stopping without another cancellation, session, cleanup attempt, or relog: " + namingResult.Error;
					checkpoint.State = RetainerCheckpointState.Failed;
					checkpoint.Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
					checkpoint.CleanupVerified = false;
					checkpoint.LastError = closureError;
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					throw new UnsafeCleanupException(closureError);
				}
				if (namingResult.Outcome == RetainerNamingSessionOutcome.Failed)
				{
					throw new InvalidOperationException(namingResult.Error);
				}
				RetainerNamingSequenceDecision sequenceDecision = RetainerNamingSequenceLogic.AfterSession(sessionIndex, namingResult.Outcome == RetainerNamingSessionOutcome.Accepted);
				if (sequenceDecision == RetainerNamingSequenceDecision.Complete)
				{
					break;
				}
				if (!RetainerNamingSessionOutcomeLogic.CanAdvanceAfterVerifiedClosure(namingResult.Outcome))
				{
					throw new InvalidOperationException("The next naming session was requested without verified exhaustion of the current session.");
				}
				await ClearPendingHireAsync(target.ContentId);
				if (sequenceDecision == RetainerNamingSequenceDecision.Fail)
				{
					throw new InvalidOperationException($"The game rejected all {9} " + "bounded retainer names across the original, reversed, and fresh sessions.");
				}
			}
			if ((object)namingResult == null || namingResult.Outcome != RetainerNamingSessionOutcome.Accepted || (object)namingResult.Retainer == null)
			{
				throw new InvalidOperationException("The bounded retainer naming sequence ended without native-roster acceptance.");
			}
			LiveRetainerInfo hiredRetainer = namingResult.Retainer;
			TrackAcceptedRetainer(checkpoint, target.Choice, hiredRetainer);
			checkpoint.PendingCheckpoint = null;
			RefreshLastVerifiedCheckpoint(checkpoint);
			await PersistAsync();
			await ClearPendingHireAsync(target.ContentId);
			await MarkHandoffBoundaryAsync(RetainerBatchRecoveryStage.HiringRetainers, $"verified hire {hiredRetainer.Name} ({hiredRetainer.RetainerId})");
		}
		MarkAllUnits(checkpoint, 1);
		await PersistAsync();
		await new RetainerPostCreationCoordinator().ExecuteAsync(actions: new RetainerPostCreationActions((CancellationToken ct) => UnlockVenturesPostCreationAsync(target, city, ct), (CancellationToken ct) => PurchaseStarterEquipmentPostCreationAsync(target, checkpoint, city, ct), (CancellationToken ct) => AssignClassAndEquipmentPostCreationAsync(target, checkpoint, ct), (CancellationToken ct) => BootstrapAutoRetainerAsync(target, checkpoint, settings, ct), (RetainerPostCreationStage stage, CancellationToken ct) => OnPostCreationStageStartingAsync(target, stage, ct), (RetainerPostCreationStage stage, CancellationToken ct) => OnPostCreationStageCompletedAsync(stage, ct), (CancellationToken _) => PersistAsync()), checkpoint: checkpoint, stopAfter: settings.StopAfter, token: token);
	}

	private async Task UnlockVenturesPostCreationAsync(RetainerSetupTarget target, RetainerStarterCity vendorCity, CancellationToken token)
	{
		uint preferredJob = GetPreferredCombatJob(target.CharacterKey);
		await ExecuteRetriableStageAsync(target, "venture-unlock quest and combat-job preparation", () => game.CompleteVentureUnlockQuestAsync(vendorCity, preferredJob, target.ContentId, target.CharacterKey, ReadQuestionablePriorityBackup, PersistQuestionablePriorityBackupAsync, ClearQuestionablePriorityBackupAsync, token), token);
	}

	private RetainerQuestionablePriorityBackup? ReadQuestionablePriorityBackup()
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
		if (retainerBatchHandoff == null || !retainerBatchHandoff.QuestionablePriorityIsolationActive)
		{
			return null;
		}
		return new RetainerQuestionablePriorityBackup(retainerBatchHandoff.QuestionablePrioritySnapshot ?? string.Empty, retainerBatchHandoff.QuestionableWasRunningBeforePriorityIsolation, retainerBatchHandoff.QuestionableQuestBeforePriorityIsolation ?? string.Empty, retainerBatchHandoff.QuestionableIsolatedQuestId ?? string.Empty);
	}

	private async Task PersistQuestionablePriorityBackupAsync(RetainerQuestionablePriorityBackup backup, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		RetainerBatchHandoffCheckpoint retainerBatchHandoffCheckpoint = RequireValidHandoff();
		retainerBatchHandoffCheckpoint.QuestionablePrioritySnapshot = backup.EncodedPriority ?? string.Empty;
		retainerBatchHandoffCheckpoint.QuestionableWasRunningBeforePriorityIsolation = backup.WasRunning;
		retainerBatchHandoffCheckpoint.QuestionableQuestBeforePriorityIsolation = backup.PreviousQuestId ?? string.Empty;
		retainerBatchHandoffCheckpoint.QuestionableIsolatedQuestId = backup.IsolatedQuestId ?? string.Empty;
		retainerBatchHandoffCheckpoint.QuestionablePriorityIsolationActive = true;
		retainerBatchHandoffCheckpoint.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
	}

	private async Task ClearQuestionablePriorityBackupAsync(CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		RetainerBatchHandoffCheckpoint retainerBatchHandoffCheckpoint = RequireValidHandoff();
		retainerBatchHandoffCheckpoint.QuestionablePriorityIsolationActive = false;
		retainerBatchHandoffCheckpoint.QuestionablePrioritySnapshot = string.Empty;
		retainerBatchHandoffCheckpoint.QuestionableWasRunningBeforePriorityIsolation = false;
		retainerBatchHandoffCheckpoint.QuestionableQuestBeforePriorityIsolation = string.Empty;
		retainerBatchHandoffCheckpoint.QuestionableIsolatedQuestId = string.Empty;
		retainerBatchHandoffCheckpoint.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
	}

	private async Task PurchaseStarterEquipmentPostCreationAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, RetainerStarterCity vendorCity, CancellationToken token)
	{
		uint classJobId = RetainerGameInteractionService.ResolveRetainerClass(checkpoint.LockedChoice);
		int pendingGearAssignments = checkpoint.Retainers.Count((TrackedRetainerCheckpoint retainer) => retainer.CompletedWorkUnits < 4);
		bool flag = checkpoint.Retainers.Any((TrackedRetainerCheckpoint retainer) => retainer.CompletedWorkUnits < 3);
		if (pendingGearAssignments > 0 && flag)
		{
			RetainerStarterGearPurchaseResult retainerStarterGearPurchaseResult = await ExecuteRetriableStageAsync(target, "starter-gear vendor acquisition", () => game.PurchaseStarterGearAsync(vendorCity, classJobId, pendingGearAssignments, checkpoint.StarterGearSlots, target.ContentId, target.CharacterKey, token), token);
			checkpoint.StarterItemId = retainerStarterGearPurchaseResult.ItemId;
			checkpoint.StarterGearSlots = retainerStarterGearPurchaseResult.OwnedSlots.ToList();
			checkpoint.StarterGearAcquiredCount = Math.Max(checkpoint.StarterGearAcquiredCount, pendingGearAssignments);
		}
	}

	private async Task AssignClassAndEquipmentPostCreationAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, CancellationToken token)
	{
		uint classJobId = RetainerGameInteractionService.ResolveRetainerClass(checkpoint.LockedChoice);
		foreach (TrackedRetainerCheckpoint retainer in RetainerStarterEquipmentLogic.SelectPendingExactRetainers(checkpoint))
		{
			RetainerStarterGearSlotCheckpoint ownedSlot = checkpoint.StarterGearSlots.FirstOrDefault((RetainerStarterGearSlotCheckpoint slot) => slot.ItemId == checkpoint.StarterItemId);
			if (ownedSlot == null)
			{
				retainer.CompletedWorkUnits = Math.Min(retainer.CompletedWorkUnits, 2);
				RefreshLastVerifiedCheckpoint(checkpoint);
				await PersistAsync();
				throw new InvalidOperationException("No freshly purchased starter main-hand slot remains for " + retainer.Name + "; the purchase stage must be reconciled without consuming pre-existing gearset equipment.");
			}
			await MarkHandoffActionAsync(RetainerBatchRecoveryStage.AssigningClassAndGear, RetainerBatchPendingAction.AssignClassAndGear, "assigning class and gear to " + retainer.Name);
			if (await ExecuteRetriableStageAsync(target, "class and equipment assignment for " + retainer.Name, () => game.AssignClassAndGearAsync(new TrackedRetainerCheckpoint[1] { retainer }, classJobId, checkpoint.StarterItemId, ownedSlot, target.ContentId, target.CharacterKey, token), token))
			{
				checkpoint.StarterGearSlots.RemoveAll((RetainerStarterGearSlotCheckpoint slot) => slot.ContainerType == ownedSlot.ContainerType && slot.Slot == ownedSlot.Slot);
			}
			retainer.CompletedWorkUnits = 4;
			RefreshLastVerifiedCheckpoint(checkpoint);
			await PersistAsync();
			await MarkHandoffBoundaryAsync(RetainerBatchRecoveryStage.AssigningClassAndGear, "class and main hand verified for " + retainer.Name);
		}
	}

	private async Task OnPostCreationStageStartingAsync(RetainerSetupTarget target, RetainerPostCreationStage stage, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (stage == RetainerPostCreationStage.AssignClassAndEquipment && !textAdvanceEnabledAfterToolsForCurrentCharacter)
		{
			EnableTextAdvanceAfterStarterToolsVerified();
		}
		if (stage == RetainerPostCreationStage.UnlockVentures)
		{
			await WaitForConsequentialActionRetryAsync(RequireValidHandoff().QuestStartCommandIssuedUtc, token);
		}
		var (stage2, message, stage3, action, reason, commandTimestamp) = stage switch
		{
			RetainerPostCreationStage.UnlockVentures => ("Unlocking ventures", "Questionable is completing the native starting-town venture quest.", RetainerBatchRecoveryStage.UnlockingVentures, RetainerBatchPendingAction.StartVentureQuest, "starting or observing the native starting-town venture quest for " + target.CharacterKey, RetainerCommandTimestamp.QuestStart), 
			RetainerPostCreationStage.PurchaseStarterEquipment => ("Buying starter gear", "Acquiring one validated Weathered main hand per tracked retainer.", RetainerBatchRecoveryStage.BuyingStarterGear, RetainerBatchPendingAction.PurchaseStarterGear, "purchasing verified starter gear for " + target.CharacterKey, RetainerCommandTimestamp.None), 
			RetainerPostCreationStage.AssignClassAndEquipment => ("Assigning class and gear", "Assigning only exact checkpoint-owned retainers and verifying their main hands.", RetainerBatchRecoveryStage.AssigningClassAndGear, RetainerBatchPendingAction.AssignClassAndGear, "assigning exact checkpoint retainers for " + target.CharacterKey, RetainerCommandTimestamp.None), 
			RetainerPostCreationStage.BootstrapAutoRetainer => ("Bootstrapping AutoRetainer", "Applying guarded IPC settings and waiting for exact first ventures.", RetainerBatchRecoveryStage.BootstrappingAutoRetainer, RetainerBatchPendingAction.ConfigureAutoRetainer, "configuring AutoRetainer for " + target.CharacterKey, RetainerCommandTimestamp.None), 
			_ => throw new ArgumentOutOfRangeException("stage", stage, null), 
		};
		SetStage(target, stage2, message);
		await MarkHandoffActionAsync(stage3, action, reason, "", commandTimestamp);
	}

	private async Task OnPostCreationStageCompletedAsync(RetainerPostCreationStage stage, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		var (stage2, reason) = stage switch
		{
			RetainerPostCreationStage.UnlockVentures => (RetainerBatchRecoveryStage.UnlockingVentures, "native venture-unlock quest completion verified"), 
			RetainerPostCreationStage.PurchaseStarterEquipment => (RetainerBatchRecoveryStage.BuyingStarterGear, "starter-gear inventory proof verified"), 
			RetainerPostCreationStage.AssignClassAndEquipment => (RetainerBatchRecoveryStage.AssigningClassAndGear, "class and main hand verified for every exact checkpoint retainer"), 
			RetainerPostCreationStage.BootstrapAutoRetainer => (RetainerBatchRecoveryStage.BootstrappingAutoRetainer, "initial AutoRetainer assignment pass reached its normal terminal condition and cleanup was verified"), 
			_ => throw new ArgumentOutOfRangeException("stage", stage, null), 
		};
		await MarkHandoffBoundaryAsync(stage2, reason);
		if (stage == RetainerPostCreationStage.PurchaseStarterEquipment)
		{
			EnableTextAdvanceAfterStarterToolsVerified();
		}
	}

	private void EnableTextAdvanceAfterStarterToolsVerified()
	{
		if (!textAdvanceEnabledAfterToolsForCurrentCharacter)
		{
			if (!commandManager.ProcessCommand("/at y"))
			{
				throw new InvalidOperationException("TextAdvance did not accept /at y after starter-tool purchase was verified.");
			}
			textAdvanceEnabledAfterToolsForCurrentCharacter = true;
			log.Information("[RetainerSetup] Sent /at y after starter-tool purchase was verified.");
		}
	}

	private void DisableTextAdvanceBeforeCharacterRelog(string characterKey)
	{
		if (!commandManager.ProcessCommand("/at n"))
		{
			throw new InvalidOperationException("TextAdvance did not accept /at n before relogging to " + characterKey + ".");
		}
		textAdvanceEnabledAfterToolsForCurrentCharacter = false;
		log.Information("[RetainerSetup] Sent /at n before relogging to " + characterKey + ".");
	}

	private static void ValidateNativeRosterAtVocate(RetainerEntitlementInfo vocateEntitlements, StableNativeRetainerEvidence nativeEvidence)
	{
		RetainerNativeRosterSnapshot retainerNativeRosterSnapshot = nativeEvidence.Snapshot;
		if (retainerNativeRosterSnapshot.MaximumCount <= 0)
		{
			throw new RetainerTerminalCharacterException("The live client reports no retainer entitlement.");
		}
		if (retainerNativeRosterSnapshot.CurrentCount < 0 || retainerNativeRosterSnapshot.CurrentCount > retainerNativeRosterSnapshot.MaximumCount || retainerNativeRosterSnapshot.CurrentCount != retainerNativeRosterSnapshot.RosterCount || retainerNativeRosterSnapshot.RosterCount != nativeEvidence.Roster.Count)
		{
			throw new RetainerTerminalCharacterException("Native retainer entitlement and roster counts did not agree; the roster will not be modified.");
		}
		if (vocateEntitlements.CurrentCount != retainerNativeRosterSnapshot.CurrentCount || vocateEntitlements.MaximumCount != retainerNativeRosterSnapshot.MaximumCount)
		{
			throw new RetainerTerminalCharacterException("Native retainer entitlement changed between the Vocate interaction and the stable roster read.");
		}
	}

	private async Task<T> ExecuteRetriableStageAsync<T>(RetainerSetupTarget target, string description, Func<Task<T>> action, CancellationToken token)
	{
		Exception lastFailure = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			token.ThrowIfCancellationRequested();
			try
			{
				return await action();
			}
			catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is RetainerTerminalCharacterException))
			{
				lastFailure = ex;
				if (RetainerAttemptPolicy.CanRetry(attempt, terminalFailure: false))
				{
					SetSnapshot(Snapshot with
					{
						LastMessage = $"{description} attempt {attempt} failed; reconciling live state before retry: {ex.Message}"
					});
					await game.RecoverForRetryAsync(target.ContentId, target.CharacterKey, token);
					continue;
				}
			}
			break;
		}
		throw new InvalidOperationException($"{description} exhausted {3} attempts: " + (lastFailure?.Message ?? "unknown failure"), lastFailure);
	}

	private async Task<RetainerNamingSessionResult> HireNamingSessionWithRecoveryAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, RetainerStarterCity city, RetainerSetupConfiguration settings, RetainerNamingSession session, ISet<string> unavailableNames, CancellationToken token)
	{
		string lastFailure = string.Empty;
		int submittedCount = 0;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			token.ThrowIfCancellationRequested();
			RetainerNamingSessionResult result;
			try
			{
				result = await game.HireRetainerSessionAsync(city, settings, session, async delegate(string candidate, CancellationToken submissionToken)
				{
					submissionToken.ThrowIfCancellationRequested();
					if (!session.Candidates.Contains<string>(candidate, StringComparer.OrdinalIgnoreCase) || !unavailableNames.Add(candidate))
					{
						throw new RetainerTerminalCharacterException("Naming candidate " + candidate + " collided immediately before submission.");
					}
					if (!checkpoint.ReservedNames.Contains<string>(candidate, StringComparer.OrdinalIgnoreCase))
					{
						checkpoint.ReservedNames.Add(candidate);
					}
					checkpoint.PendingCheckpoint = RetainerStopAfter.RetainersHired;
					checkpoint.UpdatedUtc = DateTime.UtcNow;
					await PersistAsync();
					await MarkHandoffActionAsync(RetainerBatchRecoveryStage.HiringRetainers, RetainerBatchPendingAction.HireRetainer, "submitting reserved retainer name " + candidate + " for " + target.CharacterKey, candidate);
				}, target.ContentId, target.CharacterKey, token);
			}
			catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is RetainerTerminalCharacterException))
			{
				result = RetainerNamingSessionResult.Failed(ex.Message);
			}
			submittedCount += result.SubmittedCount;
			if (!RetainerNamingSessionOutcomeLogic.RequiresOuterRecovery(result.Outcome))
			{
				return result with
				{
					SubmittedCount = submittedCount
				};
			}
			lastFailure = result.Error;
			LiveRetainerInfo[] array = (await game.ReadLiveRosterAsync(target.ContentId, target.CharacterKey, token)).Where((LiveRetainerInfo retainer) => session.Candidates.Contains<string>(retainer.Name, StringComparer.OrdinalIgnoreCase) && checkpoint.ReservedNames.Contains<string>(retainer.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
			if (array.Length > 1)
			{
				throw new RetainerTerminalCharacterException("Multiple reserved naming-session candidates appeared in the live roster.");
			}
			LiveRetainerInfo completedSideEffect = array.SingleOrDefault();
			if (completedSideEffect != null)
			{
				if (checkpoint.Retainers.Any((TrackedRetainerCheckpoint retainer) => retainer.RetainerId != completedSideEffect.RetainerId && string.Equals(retainer.Name, completedSideEffect.Name, StringComparison.OrdinalIgnoreCase)))
				{
					throw new RetainerTerminalCharacterException("Retainer name " + completedSideEffect.Name + " reconciled to an unexpected live ID.");
				}
				if (!(await game.CloseAcceptedHireFlowAsync(target.ContentId, target.CharacterKey, token)))
				{
					return RetainerNamingSessionResult.AcceptedClosureUnverified(completedSideEffect, "Retainer " + completedSideEffect.Name + " was created, but direct accepted-hire InputString closure and four closed reads could not be verified.", submittedCount);
				}
				return RetainerNamingSessionResult.Accepted(completedSideEffect, submittedCount);
			}
			if (session.Candidates.Any((string candidate) => checkpoint.ReservedNames.Contains<string>(candidate, StringComparer.OrdinalIgnoreCase)))
			{
				SetSnapshot(Snapshot with
				{
					LastMessage = "A naming-session structural failure followed a submitted reservation; cleaning owned dialogs without replaying the candidate: " + result.Error
				});
				await game.RecoverForRetryAsync(target.ContentId, target.CharacterKey, token);
				return RetainerNamingSessionResult.Failed("Naming session failed after a submitted candidate and was not replayed: " + result.Error, submittedCount);
			}
			if (!RetainerAttemptPolicy.CanRetry(attempt, terminalFailure: false))
			{
				break;
			}
			SetSnapshot(Snapshot with
			{
				LastMessage = $"Retainer naming-session setup attempt {attempt} failed before submission; cleaning owned dialogs before retry: {result.Error}"
			});
			await game.RecoverForRetryAsync(target.ContentId, target.CharacterKey, token);
		}
		return RetainerNamingSessionResult.Failed($"Retainer naming-session setup exhausted {3} pre-submission attempts: {lastFailure}", submittedCount);
	}

	private Task ExecuteRetriableStageAsync(RetainerSetupTarget target, string description, Func<Task> action, CancellationToken token)
	{
		return ExecuteRetriableStageAsync(target, description, async delegate
		{
			await action();
			return (object?)null;
		}, token);
	}

	private async Task BootstrapAutoRetainerAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, RetainerSetupConfiguration settings, CancellationToken token)
	{
		await ExecuteRetriableStageAsync(target, "summoning-bell addon acquisition", () => game.OpenSummoningBellAsync(target.ContentId, target.CharacterKey, token), token);
		uint expectedFirstVentureId = (RetainerAutoRetainerBootstrapPolicy.ShouldAttachStarterPlan(settings.StopAfter, settings.AttachStarterPlan) ? AutoRetainerStarterPlans.Get(checkpoint.LockedChoice.Type).First : 0u);
		foreach (TrackedRetainerCheckpoint retainer in checkpoint.Retainers)
		{
			retainer.ExpectedFirstVentureId = expectedFirstVentureId;
		}
		await PersistAsync();
		AutoRetainerReflectionRequest reflectionRequest = CreateAutoRetainerReflectionRequest(target, checkpoint, settings);
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.BootstrappingAutoRetainer, RetainerBatchPendingAction.ConfigureAutoRetainer, "atomically configuring and rereading AutoRetainer for " + target.CharacterKey);
		AutoRetainerReflectionApplyResult autoRetainerReflectionApplyResult = await autoRetainer.ConfigureRetainerBootstrapAsync(reflectionRequest);
		if (!autoRetainerReflectionApplyResult.Success || autoRetainerReflectionApplyResult.Snapshot == null)
		{
			throw new InvalidOperationException("AutoRetainer reflected bootstrap failed before scheduler start: " + autoRetainerReflectionApplyResult.Error);
		}
		log.Information($"[RetainerSetup] AutoRetainer reflected bootstrap verified for {target.CharacterKey}; changed={autoRetainerReflectionApplyResult.Changed}, saves={autoRetainerReflectionApplyResult.SaveCalls}.");
		await game.WaitForSafeStartingStateAsync(target.ContentId, target.CharacterKey, token);
		bool alreadyRunningExactVentures = await ExactFirstVenturesAlreadyObservedAsync(target, checkpoint, token);
		uint? initialVentureTokens = await game.ReadVentureTokenCountAsync(target.ContentId, target.CharacterKey, token);
		bool initiallyTokenLimited = RetainerInitialAssignmentLogic.Decide(0, checkpoint.Retainers.Count, initialVentureTokens) == RetainerInitialAssignmentDecision.InsufficientVentureTokens;
		AutoRetainerReflectionReadResult autoRetainerReflectionReadResult = await autoRetainer.ReadRetainerSnapshotAsync(reflectionRequest);
		if (!autoRetainerReflectionReadResult.Success || autoRetainerReflectionReadResult.Snapshot == null)
		{
			throw new InvalidOperationException("AutoRetainer reflected eligibility reread failed: " + autoRetainerReflectionReadResult.Error);
		}
		RetainerAutoRetainerStartDecision retainerAutoRetainerStartDecision = RetainerAutoRetainerStartLogic.Decide(alreadyRunningExactVentures, initiallyTokenLimited, autoRetainerReflectionReadResult.Snapshot.StarterPlansConfigured, autoRetainerReflectionReadResult.Snapshot.Enabled, autoRetainerReflectionReadResult.Snapshot.ExactRetainersSelected);
		switch (retainerAutoRetainerStartDecision)
		{
		case RetainerAutoRetainerStartDecision.FailPlansUnavailable:
			throw new InvalidOperationException("AutoRetainer did not preserve every mandatory exact starter plan. The verified class/equipment checkpoint is preserved for a resumable rerun.");
		case RetainerAutoRetainerStartDecision.FailCharacterDisabled:
			throw new InvalidOperationException((settings.EnableCharacter ? "AutoRetainer did not preserve the requested character enablement." : "EnableCharacter is off and the character was not already eligible.") + " The verified class/equipment checkpoint is preserved for a resumable rerun.");
		case RetainerAutoRetainerStartDecision.FailRetainersDisabled:
			throw new InvalidOperationException((settings.EnableNewRetainers ? "AutoRetainer did not preserve the requested exact-retainer enablement." : "EnableNewRetainers is off and one or more exact retainers were not already eligible.") + " The verified class/equipment checkpoint is preserved for a resumable rerun.");
		case RetainerAutoRetainerStartDecision.Start:
		{
			await WaitForConsequentialActionRetryAsync(RequireValidHandoff().AutoRetainerStartCommandIssuedUtc, token);
			if (!(await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(8L), token)))
			{
				throw new InvalidOperationException("AutoRetainer was not stably idle immediately before the guarded initial-assignment start.");
			}
			RetainerBellMenuReadiness retainerBellMenuReadiness = await game.EnsureOwnedSummoningBellListReadyForAutoRetainerStartAsync(target.ContentId, target.CharacterKey, token);
			if (!retainerBellMenuReadiness.Success)
			{
				throw new InvalidOperationException(retainerBellMenuReadiness.Error);
			}
			AutoRetainerReflectionReadResult autoRetainerReflectionReadResult2 = await autoRetainer.ReadRetainerSnapshotAsync(reflectionRequest);
			if (!autoRetainerReflectionReadResult2.Success || autoRetainerReflectionReadResult2.Snapshot == null || RetainerAutoRetainerStartLogic.Decide(exactVenturesAlreadyObserved: false, insufficientVentureTokens: false, autoRetainerReflectionReadResult2.Snapshot.StarterPlansConfigured, autoRetainerReflectionReadResult2.Snapshot.Enabled, autoRetainerReflectionReadResult2.Snapshot.ExactRetainersSelected) != RetainerAutoRetainerStartDecision.Start)
			{
				throw new InvalidOperationException("AutoRetainer reflected plan/character/exact-retainer eligibility drifted immediately before /ays e.");
			}
			await MarkHandoffActionAsync(RetainerBatchRecoveryStage.BootstrappingAutoRetainer, RetainerBatchPendingAction.StartAutoRetainer, "starting AutoRetainer once for " + target.CharacterKey, "", RetainerCommandTimestamp.AutoRetainerStart);
			await autoRetainer.SendCommandAsync("/ays e");
			break;
		}
		default:
			log.Information((retainerAutoRetainerStartDecision == RetainerAutoRetainerStartDecision.SuppressAlreadyAssigned) ? ("[RetainerSetup] Exact first ventures were already observed for " + target.CharacterKey + "; duplicate /ays e suppressed.") : $"[RetainerSetup] Native venture-token inventory is {initialVentureTokens}; fewer than two tokens remain, so /ays e was not started.");
			break;
		}
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90 + 45 * checkpoint.Retainers.Count);
		DateTime noProgressDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60L);
		int lastCompleted = -1;
		bool assignmentTerminal = false;
		bool stoppedForTokenExhaustion = false;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			await game.VerifyIdentityAsync(target.ContentId, target.CharacterKey, token);
			AutoRetainerReflectionReadResult currentReflection = await autoRetainer.ReadRetainerSnapshotAsync(reflectionRequest);
			if (!currentReflection.Success || currentReflection.Snapshot == null)
			{
				throw new InvalidOperationException("AutoRetainer reflected venture snapshot failed: " + currentReflection.Error);
			}
			IReadOnlyList<AutoRetainerOfflineRetainer> offlineRetainers = currentReflection.Snapshot.Retainers;
			IReadOnlyList<LiveRetainerInfo> liveRetainers = await game.ReadLiveRosterAsync(target.ContentId, target.CharacterKey, token);
			long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			TrackedRetainerCheckpoint[] completedRetainers = checkpoint.Retainers.Where((TrackedRetainerCheckpoint expected) => OfflineVentureMatches(expected, offlineRetainers, now) && LiveVentureMatches(expected, liveRetainers, now)).ToArray();
			bool flag = false;
			int num = ((completedRetainers.Length == checkpoint.Retainers.Count) ? Math.Max(0, checkpoint.Retainers.Count - 1) : completedRetainers.Length);
			int num2 = checkpoint.Retainers.Count((TrackedRetainerCheckpoint retainer) => retainer.CompletedWorkUnits >= 5);
			TrackedRetainerCheckpoint[] array = completedRetainers;
			foreach (TrackedRetainerCheckpoint trackedRetainerCheckpoint in array)
			{
				if (trackedRetainerCheckpoint.CompletedWorkUnits < 5 && num2 < num)
				{
					trackedRetainerCheckpoint.CompletedWorkUnits = 5;
					num2++;
					flag = true;
				}
			}
			if (flag)
			{
				RefreshLastVerifiedCheckpoint(checkpoint);
				await PersistAsync();
			}
			int completed = completedRetainers.Length;
			uint? ventureTokens = await game.ReadVentureTokenCountAsync(target.ContentId, target.CharacterKey, token);
			if (completed > lastCompleted)
			{
				lastCompleted = completed;
				noProgressDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60L);
				SetSnapshot(Snapshot with
				{
					LastMessage = $"AutoRetainer first ventures verified: {completed}/{checkpoint.Retainers.Count} (IsBusy diagnostic: {ReadBusyDiagnostic()})."
				});
			}
			RetainerInitialAssignmentDecision assignmentDecision = RetainerInitialAssignmentLogic.Decide(completed, checkpoint.Retainers.Count, ventureTokens);
			if (assignmentDecision == RetainerInitialAssignmentDecision.AllExpectedVenturesAssigned && currentReflection.Snapshot != null)
			{
				EnsureSuccess(VerifySnapshotFirstVentures(currentReflection.Snapshot, checkpoint.Retainers, now));
				if (await game.VerifyLiveFirstVenturesAsync(checkpoint.Retainers, target.ContentId, target.CharacterKey, token))
				{
					assignmentTerminal = true;
					break;
				}
			}
			if (assignmentDecision == RetainerInitialAssignmentDecision.InsufficientVentureTokens)
			{
				assignmentTerminal = true;
				stoppedForTokenExhaustion = true;
				log.Information($"[RetainerSetup] AutoRetainer initial assignment pass stopped normally at {completed}/{checkpoint.Retainers.Count}: native venture-token inventory is {ventureTokens}, below the cost of two.");
				break;
			}
			if (DateTime.UtcNow >= noProgressDeadline)
			{
				throw new TimeoutException("AutoRetainer made no exact per-retainer venture progress for 60 seconds.");
			}
			await Task.Delay(500, token);
		}
		if (!assignmentTerminal)
		{
			throw new TimeoutException("AutoRetainer deadline elapsed before every exact retainer had its expected future venture or native venture-token inventory fell below two.");
		}
		bool flag2;
		if (!stoppedForTokenExhaustion)
		{
			AutoRetainerReflectionReadResult autoRetainerReflectionReadResult3 = await autoRetainer.ReadRetainerSnapshotAsync(reflectionRequest);
			long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			flag2 = !autoRetainerReflectionReadResult3.Success || autoRetainerReflectionReadResult3.Snapshot == null || !VerifySnapshotFirstVentures(autoRetainerReflectionReadResult3.Snapshot, checkpoint.Retainers, nowUnixSeconds).Success;
			if (!flag2)
			{
				flag2 = !(await game.VerifyLiveFirstVenturesAsync(checkpoint.Retainers, target.ContentId, target.CharacterKey, token));
			}
			if (flag2)
			{
				throw new TimeoutException("AutoRetainer terminal exact-venture proof drifted before scheduler shutdown.");
			}
		}
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.CleaningUp, RetainerBatchPendingAction.StopAutoRetainer, stoppedForTokenExhaustion ? ("stopping AutoRetainer after native venture tokens fell below two for " + target.CharacterKey) : ("stopping AutoRetainer after exact venture proof for " + target.CharacterKey));
		await autoRetainer.SendCommandAsync("/ays d");
		flag2 = !(await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(20L), token));
		if (!flag2)
		{
			flag2 = !(await game.CloseOwnedWindowsAsync(target.ContentId, target.CharacterKey, token));
		}
		if (flag2)
		{
			throw new InvalidOperationException("AutoRetainer initial assignment pass ended, but stable idle/window cleanup was not verified.");
		}
	}

	private async Task<bool> CleanupCharacterAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (disposalRequested)
		{
			throw new RetainerBatchSuspendedException();
		}
		try
		{
			if (ReadQuestionablePriorityBackup() != null)
			{
				await game.RestoreQuestionablePriorityAsync(ReadQuestionablePriorityBackup, ClearQuestionablePriorityBackupAsync, token);
			}
			bool multiModeDisabled = autoRetainer.SetMultiModeEnabled(enabled: false);
			bool functionsDisabled = await autoRetainer.DisableAllFunctionsAsync();
			await autoRetainer.SendCommandAsync("/ays d");
			await autoRetainer.AbortAllTasksAsync();
			bool settled = await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(8L), token);
			if (!settled && !checkpoint.AutoRetainerResetIssued)
			{
				checkpoint.AutoRetainerResetIssued = true;
				await PersistAsync();
				await autoRetainer.SendCommandAsync("/ays reset");
				settled = await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(15L), token);
			}
			bool relevantWindowsClosed = await game.CloseOwnedWindowsAsync(target.ContentId, target.CharacterKey, token);
			bool enabled;
			bool flag = autoRetainer.TryGetMultiModeEnabled(out enabled) && !enabled;
			RetainerCleanupDecision retainerCleanupDecision = RetainerBatchRecovery.Decide(settled, checkpoint.AutoRetainerResetIssued, settled, relevantWindowsClosed);
			checkpoint.CleanupVerified = retainerCleanupDecision == RetainerCleanupDecision.ContinueBatch && multiModeDisabled && functionsDisabled && flag;
			if (!checkpoint.CleanupVerified)
			{
				checkpoint.Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
			}
			checkpoint.UpdatedUtc = DateTime.UtcNow;
			await PersistAsync();
			return checkpoint.CleanupVerified;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			checkpoint.CleanupVerified = false;
			checkpoint.Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
			checkpoint.LastError = (string.IsNullOrWhiteSpace(checkpoint.LastError) ? ("Cleanup failed: " + ex2.Message) : (checkpoint.LastError + " | Cleanup failed: " + ex2.Message));
			checkpoint.UpdatedUtc = DateTime.UtcNow;
			await PersistAsync();
			log.Error($"[RetainerSetup] Cleanup failed for {target.CharacterKey}: {ex2}");
			return false;
		}
	}

	private void RecoverOrClearHandoffOnLoad()
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
		if (retainerBatchHandoff == null)
		{
			return;
		}
		RetainerBatchHandoffValidation retainerBatchHandoffValidation = RetainerBatchHandoffLogic.Validate(retainerBatchHandoff, DateTime.UtcNow);
		if (retainerBatchHandoffValidation != RetainerBatchHandoffValidation.Valid)
		{
			string text = ((retainerBatchHandoffValidation == RetainerBatchHandoffValidation.Expired) ? "checkpoint expired after 30 minutes of inactivity" : "checkpoint was malformed");
			ClearHandoffOnLoad(text);
			SetSnapshot(RetainerCreationSnapshot.Idle with
			{
				CurrentStage = ((retainerBatchHandoffValidation == RetainerBatchHandoffValidation.Expired) ? "Expired" : "Recovery failed"),
				LastMessage = "Retainer batch recovery was cleared because the " + text + "."
			});
			return;
		}
		try
		{
			RetainerSetupConfiguration settings = FromFrozenSettings(retainerBatchHandoff.FrozenSettings);
			RetainerSetupTarget[] targets = retainerBatchHandoff.OrderedTargets.Select((RetainerBatchTargetCheckpoint target) => new RetainerSetupTarget(target.ContentId, target.CharacterKey, XadbRetainerSnapshot.Unknown("Recovered handoff uses the native live roster; no runtime XADB save is required", target.ContentId, target.XadbBaselineUpdatedUtc), CloneChoice(target.Choice))).ToArray();
			string[] knownXadbRetainerNames = retainerBatchHandoff.FrozenSettings.UnavailableNames.ToArray();
			cancellationSource = new CancellationTokenSource();
			int recoveryCount = retainerBatchHandoff.RecoveryCount;
			DateTime updatedUtc = retainerBatchHandoff.UpdatedUtc;
			retainerBatchHandoff.RecoveryCount++;
			retainerBatchHandoff.UpdatedUtc = DateTime.UtcNow;
			try
			{
				configuration.Save();
			}
			catch (Exception ex)
			{
				retainerBatchHandoff.RecoveryCount = recoveryCount;
				retainerBatchHandoff.UpdatedUtc = updatedUtc;
				cancellationSource.Dispose();
				cancellationSource = null;
				SetSnapshot(RetainerCreationSnapshot.Idle with
				{
					CurrentStage = "Suspended",
					LastMessage = "Retainer batch recovery could not be persisted and remains suspended: " + ex.Message
				});
				log.Error($"[RetainerSetup] Recovery persistence failed; handoff preserved: {ex}");
				return;
			}
			string currentStage = (retainerBatchHandoff.CancellationRequested ? "Cancelling" : "Recovered");
			SetSnapshot(new RetainerCreationSnapshot(IsRunning: true, retainerBatchHandoff.CurrentTargetCharacterKey, currentStage, retainerBatchHandoff.CompletedTargetContentIds.Count, retainerBatchHandoff.OrderedTargets.Count, retainerBatchHandoff.CancellationRequested ? "Recovered explicit cancellation; only guarded cleanup will run." : $"Recovering suspended retainer batch {retainerBatchHandoff.BatchId} from {retainerBatchHandoff.RecoveryStage}.", !retainerBatchHandoff.CancellationRequested));
			log.Information($"[RetainerSetup] Recovering durable batch handoff {retainerBatchHandoff.BatchId} at {retainerBatchHandoff.RecoveryStage} (recovery #{retainerBatchHandoff.RecoveryCount}).");
			DisableTextAdvanceForBatchStart();
			runnerTask = RunBatchObservedAsync(targets, settings, knownXadbRetainerNames, cancellationSource.Token, recovering: true);
		}
		catch (Exception ex2)
		{
			log.Error($"[RetainerSetup] Handoff reconstruction failed: {ex2}");
			ClearHandoffOnLoad("reconstruction failed: " + ex2.Message);
			SetSnapshot(RetainerCreationSnapshot.Idle with
			{
				CurrentStage = "Recovery failed",
				LastMessage = "Retainer batch recovery was cleared: " + ex2.Message
			});
		}
	}

	private async Task WaitForRecoveryDependenciesAsync(CancellationToken token)
	{
		RetainerBatchHandoffCheckpoint initialHandoff = RequireValidHandoff();
		initialHandoff.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
		log.Information($"[RetainerSetup] Handoff {initialHandoff.BatchId} is waiting for reload dependencies without discarding stage {initialHandoff.RecoveryStage} or pending action {initialHandoff.PendingAction}.");
		while (true)
		{
			token.ThrowIfCancellationRequested();
			RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
			RetainerBatchHandoffValidation retainerBatchHandoffValidation = RetainerBatchHandoffLogic.Validate(retainerBatchHandoff, DateTime.UtcNow);
			if (retainerBatchHandoffValidation != RetainerBatchHandoffValidation.Valid)
			{
				RetainerBatchLifecycleEvent lifecycleEvent = ((retainerBatchHandoffValidation == RetainerBatchHandoffValidation.Expired) ? RetainerBatchLifecycleEvent.Expired : RetainerBatchLifecycleEvent.Malformed);
				string reason = ((retainerBatchHandoffValidation == RetainerBatchHandoffValidation.Expired) ? "checkpoint expired while waiting for dependencies" : "checkpoint became malformed while waiting for dependencies");
				await ClearHandoffAsync(reason, lifecycleEvent);
				throw new RetainerBatchTerminalException(reason);
			}
			autoRetainer.TryReinitialize();
			if ((!retainerBatchHandoff.CancellationRequested) ? (autoRetainer.IsAvailable && game.TryPrepareRecoveryDependencies()) : (autoRetainer.IsAvailable && game.TryPrepareCleanupDependencies()))
			{
				break;
			}
			SetSnapshot(Snapshot with
			{
				CurrentStage = (retainerBatchHandoff.CancellationRequested ? "Cancelling" : "Recovered"),
				LastMessage = (retainerBatchHandoff.CancellationRequested ? "Waiting for AutoRetainer and vnavmesh so guarded cancellation cleanup can resume." : "Waiting for AutoRetainer, Questionable, and vnavmesh readiness; XADB is not part of runtime recovery.")
			});
			await Task.Delay(1000, token);
		}
		log.Information("[RetainerSetup] Reload recovery dependencies are ready.");
	}

	private RetainerBatchHandoffCheckpoint RequireValidHandoff()
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
		if (retainerBatchHandoff == null || retainerBatchHandoff.SchemaVersion != 1)
		{
			throw new RetainerBatchTerminalException("The durable retainer batch handoff is unavailable or malformed.");
		}
		return retainerBatchHandoff;
	}

	private async Task SetCurrentHandoffTargetAsync(RetainerSetupTarget target)
	{
		RetainerBatchHandoffCheckpoint handoff = RequireValidHandoff();
		if (handoff.CurrentTargetContentId != target.ContentId || !string.Equals(handoff.CurrentTargetCharacterKey, target.CharacterKey, StringComparison.OrdinalIgnoreCase))
		{
			handoff.CurrentTargetContentId = target.ContentId;
			handoff.CurrentTargetCharacterKey = target.CharacterKey;
			handoff.RecoveryStage = RetainerBatchRecoveryStage.RelogPending;
			handoff.PendingAction = RetainerBatchPendingAction.None;
			handoff.PendingRetainerName = string.Empty;
			handoff.RelogCommandIssuedUtc = DateTime.MinValue;
			handoff.QuestStartCommandIssuedUtc = DateTime.MinValue;
			handoff.AutoRetainerStartCommandIssuedUtc = DateTime.MinValue;
			handoff.UpdatedUtc = DateTime.UtcNow;
			await PersistAsync();
			log.Information($"[RetainerSetup] Handoff {handoff.BatchId} advanced to target {target.CharacterKey} ({target.ContentId}).");
		}
	}

	private async Task MarkHandoffActionAsync(RetainerBatchRecoveryStage stage, RetainerBatchPendingAction action, string reason, string pendingRetainerName = "", RetainerCommandTimestamp commandTimestamp = RetainerCommandTimestamp.None)
	{
		RetainerBatchHandoffCheckpoint handoff = RequireValidHandoff();
		RetainerBatchRecoveryStage previousStage = handoff.RecoveryStage;
		handoff.RecoveryStage = (handoff.CancellationRequested ? RetainerBatchRecoveryStage.Cancelling : stage);
		handoff.PendingAction = (handoff.CancellationRequested ? RetainerBatchPendingAction.Cleanup : action);
		if (!string.IsNullOrWhiteSpace(pendingRetainerName))
		{
			handoff.PendingRetainerName = pendingRetainerName.Trim();
		}
		DateTime utcNow = DateTime.UtcNow;
		switch (commandTimestamp)
		{
		case RetainerCommandTimestamp.Relog:
			handoff.RelogCommandIssuedUtc = utcNow;
			break;
		case RetainerCommandTimestamp.QuestStart:
			handoff.QuestStartCommandIssuedUtc = utcNow;
			break;
		case RetainerCommandTimestamp.AutoRetainerStart:
			handoff.AutoRetainerStartCommandIssuedUtc = utcNow;
			break;
		}
		handoff.UpdatedUtc = utcNow;
		await PersistAsync();
		log.Information($"[RetainerSetup] Handoff {handoff.BatchId}: {previousStage} -> {handoff.RecoveryStage}; pending {action} ({reason}).");
	}

	private async Task MarkHandoffBoundaryAsync(RetainerBatchRecoveryStage stage, string reason)
	{
		RetainerBatchHandoffCheckpoint handoff = RequireValidHandoff();
		RetainerBatchRecoveryStage previousStage = handoff.RecoveryStage;
		handoff.RecoveryStage = (handoff.CancellationRequested ? RetainerBatchRecoveryStage.Cancelling : stage);
		handoff.PendingAction = (handoff.CancellationRequested ? RetainerBatchPendingAction.Cleanup : RetainerBatchPendingAction.None);
		handoff.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
		log.Information($"[RetainerSetup] Handoff {handoff.BatchId}: verified {previousStage} -> {handoff.RecoveryStage} ({reason}).");
	}

	private async Task FinishCurrentQueueEntryAsync(ulong contentId, bool completedSuccessfully, bool requeue)
	{
		RetainerBatchHandoffCheckpoint handoff = RequireValidHandoff();
		if (!RetainerBatchHandoffLogic.AdvanceQueueAfterAttempt(handoff, contentId, completedSuccessfully, requeue))
		{
			throw new RetainerBatchTerminalException("The durable retainer queue changed while a target was running.");
		}
		handoff.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
		log.Information($"[RetainerSetup] Handoff {handoff.BatchId} finished target {contentId}; success={completedSuccessfully}, requeue={requeue}, remaining={handoff.RemainingQueue.Count}.");
	}

	private bool CanCurrentTargetRequeue(ulong contentId)
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoffCheckpoint = RequireValidHandoff();
		RetainerBatchTargetCheckpoint? retainerBatchTargetCheckpoint = RetainerBatchHandoffLogic.FindTarget(retainerBatchHandoffCheckpoint, contentId);
		if (retainerBatchTargetCheckpoint != null && retainerBatchTargetCheckpoint.AllowSameBatchRequeue)
		{
			return !retainerBatchHandoffCheckpoint.SameBatchRequeuedContentIds.Contains(contentId);
		}
		return false;
	}

	private string GetPendingHireName(ulong contentId)
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
		if (retainerBatchHandoff == null || retainerBatchHandoff.CurrentTargetContentId != contentId)
		{
			return string.Empty;
		}
		return retainerBatchHandoff.PendingRetainerName?.Trim() ?? string.Empty;
	}

	private async Task ClearPendingHireAsync(ulong contentId)
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoffCheckpoint = RequireValidHandoff();
		if (retainerBatchHandoffCheckpoint.CurrentTargetContentId == contentId && !string.IsNullOrWhiteSpace(retainerBatchHandoffCheckpoint.PendingRetainerName))
		{
			retainerBatchHandoffCheckpoint.PendingRetainerName = string.Empty;
			retainerBatchHandoffCheckpoint.UpdatedUtc = DateTime.UtcNow;
			await PersistAsync();
		}
	}

	private async Task WaitForConsequentialActionRetryAsync(DateTime issuedUtc, CancellationToken token)
	{
		while (!RetainerBatchHandoffLogic.ShouldRetryConsequentialAction(issuedUtc, DateTime.UtcNow))
		{
			token.ThrowIfCancellationRequested();
			await Task.Delay(250, token);
		}
	}

	private async Task<bool> ExactFirstVenturesAlreadyObservedAsync(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, CancellationToken token)
	{
		AutoRetainerReflectionReadResult autoRetainerReflectionReadResult = await autoRetainer.ReadRetainerSnapshotAsync(CreateAutoRetainerReflectionRequest(target, checkpoint, attachStarterPlan: false, enableCharacter: false, enableRetainers: false));
		if (!autoRetainerReflectionReadResult.Success || autoRetainerReflectionReadResult.Snapshot == null)
		{
			return false;
		}
		long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		bool flag = VerifySnapshotFirstVentures(autoRetainerReflectionReadResult.Snapshot, checkpoint.Retainers, nowUnixSeconds).Success;
		if (flag)
		{
			flag = await game.VerifyLiveFirstVenturesAsync(checkpoint.Retainers, target.ContentId, target.CharacterKey, token);
		}
		return flag;
	}

	private async Task<bool> ShutdownSchedulersAsync(CancellationToken token)
	{
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.CleaningUp, RetainerBatchPendingAction.StopAutoRetainer, "performing final scheduler shutdown");
		bool multiModeDisabled = autoRetainer.SetMultiModeEnabled(enabled: false);
		bool functionsDisabled = await autoRetainer.DisableAllFunctionsAsync();
		await autoRetainer.SendCommandAsync("/ays d");
		bool flag = await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(20L), token);
		bool enabled;
		bool flag2 = autoRetainer.TryGetMultiModeEnabled(out enabled) && !enabled;
		return multiModeDisabled && functionsDisabled && flag && flag2;
	}

	private async Task CompletePersistedCancellationAsync(IReadOnlyList<RetainerSetupTarget> targets, CancellationToken token)
	{
		RetainerBatchHandoffCheckpoint handoff = RequireValidHandoff();
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.Cancelling, RetainerBatchPendingAction.Cleanup, "resuming explicit cancellation cleanup after reload");
		bool multiModeDisabled = autoRetainer.SetMultiModeEnabled(enabled: false);
		bool functionsDisabled = await autoRetainer.DisableAllFunctionsAsync();
		await autoRetainer.SendCommandAsync("/ays d");
		await autoRetainer.AbortAllTasksAsync();
		bool idle = await WaitForStableAutoRetainerIdleAsync(TimeSpan.FromSeconds(20L), token);
		RetainerSetupTarget target = targets.FirstOrDefault((RetainerSetupTarget candidate) => candidate.ContentId == handoff.CurrentTargetContentId);
		bool flag = target == null;
		if (!flag)
		{
			flag = await game.ReconcileAndCloseKnownDialogsAfterReloadAsync(target.ContentId, target.CharacterKey, token);
		}
		bool flag2 = flag;
		bool enabled;
		bool flag3 = autoRetainer.TryGetMultiModeEnabled(out enabled) && !enabled;
		bool cleanupVerified = multiModeDisabled && functionsDisabled && idle && flag2 && flag3;
		if (target != null && configuration.RetainerSetup.Checkpoints.TryGetValue(target.ContentId, out CharacterRetainerSetupCheckpoint value))
		{
			value.State = RetainerCheckpointState.Failed;
			value.Disposition = RetainerCheckpointDisposition.UnsafeOrTerminal;
			value.LastError = "Cancelled by operator";
			value.CleanupVerified = cleanupVerified;
			value.PendingCheckpoint = null;
			value.UpdatedUtc = DateTime.UtcNow;
			await PersistAsync();
		}
		await ClearHandoffAsync(cleanupVerified ? "explicit cancellation cleanup completed after reload" : "explicit cancellation cleanup could not be fully verified after reload", cleanupVerified ? RetainerBatchLifecycleEvent.ExplicitCancellationCompleted : RetainerBatchLifecycleEvent.UnsafeCleanup);
		log.Information($"[RetainerSetup] Recovered explicit cancellation completed; cleanupVerified={cleanupVerified}.");
	}

	private async Task ClearHandoffAsync(string reason, RetainerBatchLifecycleEvent lifecycleEvent)
	{
		if (RetainerBatchHandoffLogic.ShouldClearForLifecycle(lifecycleEvent) && configuration.RetainerBatchHandoff != null)
		{
			RetainerBatchHandoffCheckpoint checkpoint = configuration.RetainerBatchHandoff;
			string batchId = checkpoint.BatchId;
			configuration.RetainerBatchHandoff = null;
			try
			{
				await PersistAsync();
			}
			catch
			{
				configuration.RetainerBatchHandoff = checkpoint;
				throw;
			}
			log.Information($"[RetainerSetup] Cleared durable batch handoff {batchId}: {reason}.");
		}
	}

	private void ClearHandoffOnLoad(string reason)
	{
		RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
		string value = retainerBatchHandoff?.BatchId ?? "unknown";
		configuration.RetainerBatchHandoff = null;
		try
		{
			configuration.Save();
		}
		catch
		{
			configuration.RetainerBatchHandoff = retainerBatchHandoff;
			throw;
		}
		log.Warning($"[RetainerSetup] Cleared durable batch handoff {value}: {reason}.");
	}

	private async Task RelogAndVerifyAsync(RetainerSetupTarget target, bool recovering, CancellationToken token)
	{
		try
		{
			await game.VerifyIdentityAsync(target.ContentId, target.CharacterKey, token, TimeSpan.FromSeconds(5L));
			return;
		}
		catch (RetainerIdentityMismatchException)
		{
			if (string.Equals((await game.ObserveRecoveryRuntimeAsync(target.ContentId, target.CharacterKey)).ObservedCharacterKey, target.CharacterKey, StringComparison.OrdinalIgnoreCase) || RequireValidHandoff().RecoveryStage >= RetainerBatchRecoveryStage.ExactLoginConfirmed)
			{
				await ClearHandoffAsync("definitive ContentId/character mismatch for " + target.CharacterKey, RetainerBatchLifecycleEvent.DefinitiveIdentityMismatch);
				throw new RetainerBatchTerminalException("Exact ContentId ownership for " + target.CharacterKey + " did not match the handoff.");
			}
		}
		catch (Exception ex2) when (((ex2 is InvalidOperationException || ex2 is TimeoutException) ? 1 : 0) != 0)
		{
		}
		if (autoRetainer.TryGetContentId(target.CharacterKey, out var contentId) && contentId != target.ContentId)
		{
			await ClearHandoffAsync($"AutoRetainer maps {target.CharacterKey} to {contentId}, not {target.ContentId}", RetainerBatchLifecycleEvent.DefinitiveIdentityMismatch);
			throw new RetainerBatchTerminalException("AutoRetainer ContentId ownership changed for " + target.CharacterKey + ".");
		}
		while (true)
		{
			token.ThrowIfCancellationRequested();
			RetainerRecoveryRuntimeObservation runtime = await game.ObserveRecoveryRuntimeAsync(target.ContentId, target.CharacterKey);
			bool exactTarget = runtime.Identity.Kind == RetainerIdentityObservationKind.Exact;
			bool busy;
			bool externalBusy = autoRetainer.TryGetBusy(out busy) && busy;
			if (RetainerBatchHandoffLogic.ShouldIssueRecoveryRelog(RequireValidHandoff(), DateTime.UtcNow, exactTarget, runtime.TransitionActive, externalBusy))
			{
				break;
			}
			try
			{
				await game.VerifyIdentityAsync(target.ContentId, target.CharacterKey, token, TimeSpan.FromSeconds(2L));
				return;
			}
			catch (RetainerIdentityMismatchException)
			{
				if (string.Equals(runtime.ObservedCharacterKey, target.CharacterKey, StringComparison.OrdinalIgnoreCase) || RequireValidHandoff().RecoveryStage >= RetainerBatchRecoveryStage.ExactLoginConfirmed)
				{
					await ClearHandoffAsync("definitive ContentId/character mismatch for " + target.CharacterKey, RetainerBatchLifecycleEvent.DefinitiveIdentityMismatch);
					throw new RetainerBatchTerminalException("Exact ContentId ownership for " + target.CharacterKey + " did not match the handoff.");
				}
				await Task.Delay(500, token);
			}
			catch (Exception ex4) when (((ex4 is InvalidOperationException || ex4 is TimeoutException) ? 1 : 0) != 0)
			{
				await Task.Delay(500, token);
			}
		}
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.RelogPending, RetainerBatchPendingAction.Relog, "issuing " + (recovering ? "recovery " : string.Empty) + "relog to " + target.CharacterKey, "", RetainerCommandTimestamp.Relog);
		DisableTextAdvanceBeforeCharacterRelog(target.CharacterKey);
		if (!autoRetainer.SwitchCharacter(target.CharacterKey))
		{
			throw new InvalidOperationException("AutoRetainer rejected relog to " + target.CharacterKey + ".");
		}
		await MarkHandoffActionAsync(RetainerBatchRecoveryStage.WaitingForExactLogin, RetainerBatchPendingAction.Relog, "waiting for exact login " + target.CharacterKey);
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3L);
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			try
			{
				await game.VerifyIdentityAsync(target.ContentId, target.CharacterKey, token, TimeSpan.FromSeconds(5L));
				await Task.Delay(TimeSpan.FromSeconds(3L), token);
				await game.VerifyIdentityAsync(target.ContentId, target.CharacterKey, token);
				return;
			}
			catch (RetainerIdentityMismatchException)
			{
				if (string.Equals((await game.ObserveRecoveryRuntimeAsync(target.ContentId, target.CharacterKey)).ObservedCharacterKey, target.CharacterKey, StringComparison.OrdinalIgnoreCase))
				{
					await ClearHandoffAsync("relog reached " + target.CharacterKey + " with a different ContentId", RetainerBatchLifecycleEvent.DefinitiveIdentityMismatch);
					throw new RetainerBatchTerminalException("Relog reached the expected character key with the wrong ContentId for " + target.CharacterKey + ".");
				}
				await Task.Delay(500, token);
			}
			catch (Exception ex6) when (((ex6 is InvalidOperationException || ex6 is TimeoutException) ? 1 : 0) != 0)
			{
				await Task.Delay(500, token);
			}
		}
		throw new TimeoutException($"Relog did not produce stable exact identity {target.CharacterKey} ({target.ContentId}).");
	}

	private async Task<bool> WaitForStableAutoRetainerIdleAsync(TimeSpan timeout, CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		int stableReads = 0;
		while (DateTime.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			stableReads = (autoRetainer.TryGetBusy(out var busy) ? ((!busy) ? (stableReads + 1) : 0) : 0);
			if (stableReads >= 4)
			{
				return true;
			}
			await Task.Delay(250, token);
		}
		return false;
	}

	private string ReadBusyDiagnostic()
	{
		if (!autoRetainer.TryGetBusy(out var busy))
		{
			return "unknown";
		}
		return busy.ToString();
	}

	private CharacterRetainerSetupCheckpoint GetOrCreateCheckpoint(RetainerSetupTarget target)
	{
		if (configuration.RetainerSetup.Checkpoints.TryGetValue(target.ContentId, out CharacterRetainerSetupCheckpoint value))
		{
			value.Normalize(target.ContentId);
			if (!RetainerSetupLogic.IsEligibleForExplicitRun(value))
			{
				return value;
			}
			if (string.IsNullOrWhiteSpace(GetPendingHireName(target.ContentId)) && RetainerSetupLogic.RestartStructurallyZeroProgressCheckpoint(value, target.ContentId, target.CharacterKey, target.Choice))
			{
				log.Information("[RetainerSetup] Restarted structurally zero-progress checkpoint for " + target.CharacterKey + "; the Henchman-style native Vocate flow will be repeated from the beginning.");
				return value;
			}
			log.Information("[RetainerSetup] Reopened checkpoint with recorded progress for " + target.CharacterKey + "; persisted retainer IDs and work units will be reconciled against the native live roster.");
			return value;
		}
		CharacterRetainerSetupCheckpoint characterRetainerSetupCheckpoint = new CharacterRetainerSetupCheckpoint
		{
			ContentId = target.ContentId,
			CharacterKey = target.CharacterKey,
			LockedChoice = new CharacterRetainerSetupChoice
			{
				CharacterKey = target.CharacterKey
			},
			State = RetainerCheckpointState.NotStarted,
			UpdatedUtc = DateTime.UtcNow
		};
		configuration.RetainerSetup.Checkpoints[target.ContentId] = characterRetainerSetupCheckpoint;
		return characterRetainerSetupCheckpoint;
	}

	private void ReconcileReservedHires(CharacterRetainerSetupCheckpoint checkpoint, CharacterRetainerSetupChoice frozenChoice, IReadOnlyList<LiveRetainerInfo> live, string pendingHireName)
	{
		List<string> reservedNames = (string.IsNullOrWhiteSpace(pendingHireName) ? checkpoint.ReservedNames : checkpoint.ReservedNames.Where((string name) => string.Equals(name, pendingHireName, StringComparison.OrdinalIgnoreCase)).ToList());
		RetainerReservedHireAdoptionResult result = RetainerReservedHireAdoptionLogic.Decide(checkpoint.Retainers.Select((TrackedRetainerCheckpoint retainer) => new RetainerRosterIdentity(retainer.RetainerId, retainer.Name)).ToArray(), reservedNames, live.Select((LiveRetainerInfo retainer) => new RetainerRosterIdentity(retainer.RetainerId, retainer.Name)).ToArray());
		if (result.Decision == RetainerReservedHireAdoptionDecision.Conflict)
		{
			throw new RetainerTerminalCharacterException(result.Error);
		}
		if (result.Decision == RetainerReservedHireAdoptionDecision.Adopt && !(result.Retainer == null))
		{
			LiveRetainerInfo liveRetainerInfo = live.Single((LiveRetainerInfo retainer) => retainer.RetainerId == result.Retainer.RetainerId && string.Equals(retainer.Name, result.Retainer.Name, StringComparison.OrdinalIgnoreCase));
			TrackAcceptedRetainer(checkpoint, frozenChoice, liveRetainerInfo);
			log.Information($"[RetainerSetup] Adopted QST-reserved native retainer {liveRetainerInfo.Name} ({liveRetainerInfo.RetainerId}) at work unit 1 " + (string.IsNullOrWhiteSpace(pendingHireName) ? "after the pending hire handoff had cleared." : "from the persisted pending hire handoff."));
		}
	}

	private static void TrackAcceptedRetainer(CharacterRetainerSetupCheckpoint checkpoint, CharacterRetainerSetupChoice frozenChoice, LiveRetainerInfo accepted)
	{
		TrackedRetainerCheckpoint trackedRetainerCheckpoint = checkpoint.Retainers.FirstOrDefault((TrackedRetainerCheckpoint retainer) => retainer.RetainerId == accepted.RetainerId);
		TrackedRetainerCheckpoint trackedRetainerCheckpoint2 = checkpoint.Retainers.FirstOrDefault((TrackedRetainerCheckpoint retainer) => string.Equals(retainer.Name, accepted.Name, StringComparison.OrdinalIgnoreCase));
		if ((trackedRetainerCheckpoint != null && !string.Equals(trackedRetainerCheckpoint.Name, accepted.Name, StringComparison.OrdinalIgnoreCase)) || (trackedRetainerCheckpoint2 != null && trackedRetainerCheckpoint2.RetainerId != accepted.RetainerId))
		{
			throw new RetainerTerminalCharacterException($"Accepted retainer {accepted.Name} ({accepted.RetainerId}) conflicts with the persisted checkpoint.");
		}
		TrackedRetainerCheckpoint trackedRetainerCheckpoint3 = trackedRetainerCheckpoint ?? trackedRetainerCheckpoint2;
		if (trackedRetainerCheckpoint3 == null)
		{
			RetainerSetupLogic.LockChoiceForFirstExactRetainer(checkpoint, frozenChoice);
			checkpoint.Retainers.Add(new TrackedRetainerCheckpoint
			{
				RetainerId = accepted.RetainerId,
				Name = accepted.Name,
				CompletedWorkUnits = 1
			});
		}
		else
		{
			trackedRetainerCheckpoint3.CompletedWorkUnits = Math.Max(trackedRetainerCheckpoint3.CompletedWorkUnits, 1);
		}
	}

	private static void ValidateOwnedRoster(CharacterRetainerSetupCheckpoint checkpoint, IReadOnlyList<LiveRetainerInfo> live)
	{
		if (checkpoint.Retainers.Count == 0 && live.Count > 0)
		{
			throw new InvalidOperationException("The native live roster contains untracked retainers; no retainers will be modified.");
		}
		foreach (TrackedRetainerCheckpoint expected in checkpoint.Retainers)
		{
			if (!live.Any((LiveRetainerInfo actual) => actual.RetainerId == expected.RetainerId && string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException($"Tracked retainer {expected.Name} ({expected.RetainerId}) is missing or mismatched.");
			}
		}
		HashSet<ulong> trackedIds = checkpoint.Retainers.Select((TrackedRetainerCheckpoint retainer) => retainer.RetainerId).ToHashSet();
		LiveRetainerInfo liveRetainerInfo = live.FirstOrDefault((LiveRetainerInfo actual) => !trackedIds.Contains(actual.RetainerId));
		if (liveRetainerInfo != null)
		{
			throw new RetainerTerminalCharacterException($"Live retainer {liveRetainerInfo.Name} ({liveRetainerInfo.RetainerId}) is not owned by this Companion checkpoint.");
		}
	}

	private bool TryCreateInitialNamingSessions(RetainerSetupConfiguration settings, ISet<string> unavailableNames, out RetainerNamingSession original, out RetainerNamingSession reversed)
	{
		if (!RetainerNameLogic.ShouldRegenerateHybridSampleCache(settings.SampleNames))
		{
			string[] array = settings.SampleNames.ToArray();
			foreach (string text in array)
			{
				if (RetainerNameGenerator.IsValidGeneratedName(text) && !unavailableNames.Contains(text) && names.TryCreateInitialSessionsFromBase(text, unavailableNames, 250, out original, out reversed))
				{
					settings.SampleNames.Remove(text);
					return true;
				}
			}
		}
		return names.TryGenerateInitialSessions(settings.Appearance, settings.Gender, settings.Clan, unavailableNames, 250, out original, out reversed);
	}

	private bool TryCreateFreshNamingSession(RetainerSetupConfiguration settings, ISet<string> unavailableNames, out RetainerNamingSession session)
	{
		if (!RetainerNameLogic.ShouldRegenerateHybridSampleCache(settings.SampleNames))
		{
			string[] array = settings.SampleNames.ToArray();
			foreach (string text in array)
			{
				if (RetainerNameGenerator.IsValidGeneratedName(text) && !unavailableNames.Contains(text) && names.TryCreateSessionFromBase(text, unavailableNames, 250, out session))
				{
					settings.SampleNames.Remove(text);
					return true;
				}
			}
		}
		return names.TryGenerateFreshSession(settings.Appearance, settings.Gender, settings.Clan, unavailableNames, 250, out session);
	}

	private uint GetPreferredCombatJob(string characterKey)
	{
		if (!configuration.QuestRotationCombatJobByCharacter.TryGetValue(characterKey, out var value) || value == 0 || value > 255 || !JobClassification.IsCombatJob((byte)value))
		{
			return 0u;
		}
		if (!configuration.CharacterJobLevels.TryGetValue(characterKey, out CharacterJobLevelSnapshot value2) || (!value2.CombatJobLevels.ContainsKey(value) && !value2.XadbObservedCombatJobLevels.ContainsKey(value)))
		{
			configuration.QuestRotationCombatJobByCharacter.Remove(characterKey);
			log.Warning($"[RetainerSetup] Cleared uncorroborated combat-job selection {value} for {characterKey}.");
			return 0u;
		}
		return value;
	}

	private static void MarkAllUnits(CharacterRetainerSetupCheckpoint checkpoint, int units)
	{
		foreach (TrackedRetainerCheckpoint retainer in checkpoint.Retainers)
		{
			retainer.CompletedWorkUnits = Math.Max(retainer.CompletedWorkUnits, units);
		}
		checkpoint.PendingCheckpoint = null;
		checkpoint.State = ((units < 5) ? RetainerCheckpointState.Running : RetainerCheckpointState.Complete);
		checkpoint.Disposition = ((units >= 5) ? RetainerCheckpointDisposition.Complete : RetainerCheckpointDisposition.ResumablePartial);
		RefreshLastVerifiedCheckpoint(checkpoint);
	}

	private static void RefreshLastVerifiedCheckpoint(CharacterRetainerSetupCheckpoint checkpoint)
	{
		checkpoint.LastVerifiedCheckpoint = ((checkpoint.Retainers.Count != 0) ? ((RetainerStopAfter)checkpoint.Retainers.Min((TrackedRetainerCheckpoint retainer) => Math.Clamp(retainer.CompletedWorkUnits, 0, 5))) : RetainerStopAfter.ArrivedAtVocate);
		checkpoint.UpdatedUtc = DateTime.UtcNow;
	}

	private static bool OfflineVentureMatches(TrackedRetainerCheckpoint expected, IEnumerable<AutoRetainerOfflineRetainer> retainers, long nowUnixSeconds)
	{
		return retainers.Any((AutoRetainerOfflineRetainer actual) => actual.RetainerId == expected.RetainerId && string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase) && actual.HasVenture && actual.VentureId != 0 && (expected.ExpectedFirstVentureId == 0 || actual.VentureId == expected.ExpectedFirstVentureId) && actual.VentureEndsAt > nowUnixSeconds);
	}

	private static AutoRetainerReflectionRequest CreateAutoRetainerReflectionRequest(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, RetainerSetupConfiguration settings)
	{
		return CreateAutoRetainerReflectionRequest(target, checkpoint, RetainerAutoRetainerBootstrapPolicy.ShouldAttachStarterPlan(settings.StopAfter, settings.AttachStarterPlan), settings.EnableCharacter, settings.EnableNewRetainers);
	}

	private static AutoRetainerReflectionRequest CreateAutoRetainerReflectionRequest(RetainerSetupTarget target, CharacterRetainerSetupCheckpoint checkpoint, bool attachStarterPlan, bool enableCharacter, bool enableRetainers)
	{
		return new AutoRetainerReflectionRequest(target.ContentId, target.CharacterKey, checkpoint.LockedChoice.Type, checkpoint.Retainers.Select((TrackedRetainerCheckpoint retainer) => new AutoRetainerExpectedRetainer(retainer.RetainerId, retainer.Name)).ToArray(), attachStarterPlan, enableCharacter, enableRetainers);
	}

	private static AutoRetainerMutationResult VerifySnapshotFirstVentures(AutoRetainerCharacterSnapshot snapshot, IReadOnlyCollection<TrackedRetainerCheckpoint> expectedRetainers, long nowUnixSeconds)
	{
		foreach (TrackedRetainerCheckpoint expectedRetainer in expectedRetainers)
		{
			if (!OfflineVentureMatches(expectedRetainer, snapshot.Retainers, nowUnixSeconds))
			{
				return AutoRetainerMutationResult.Fail("AutoRetainer did not preserve the exact future first venture for " + expectedRetainer.Name + ".");
			}
		}
		return AutoRetainerMutationResult.Ok;
	}

	private static bool LiveVentureMatches(TrackedRetainerCheckpoint expected, IEnumerable<LiveRetainerInfo> retainers, long nowUnixSeconds)
	{
		return retainers.Any((LiveRetainerInfo actual) => actual.RetainerId == expected.RetainerId && string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase) && actual.VentureId != 0 && (expected.ExpectedFirstVentureId == 0 || actual.VentureId == expected.ExpectedFirstVentureId) && actual.VentureCompleteUnixSeconds > nowUnixSeconds);
	}

	private async Task<bool> StopIfRequestedAsync(CharacterRetainerSetupCheckpoint checkpoint, RetainerStopAfter requested, RetainerStopAfter current)
	{
		if (requested != current)
		{
			return false;
		}
		checkpoint.State = ((current == RetainerStopAfter.AutoRetainerBootstrapped) ? RetainerCheckpointState.Complete : RetainerCheckpointState.DeliberatelyStopped);
		checkpoint.Disposition = ((current == RetainerStopAfter.AutoRetainerBootstrapped) ? RetainerCheckpointDisposition.Complete : RetainerCheckpointDisposition.ResumablePartial);
		checkpoint.PendingCheckpoint = null;
		checkpoint.UpdatedUtc = DateTime.UtcNow;
		await PersistAsync();
		return true;
	}

	private async Task PersistAsync()
	{
		await framework.RunOnFrameworkThread((Action)configuration.Save);
	}

	private void SetStage(RetainerSetupTarget target, string stage, string message)
	{
		SetSnapshot(Snapshot with
		{
			CurrentCharacter = target.CharacterKey,
			CurrentStage = stage,
			LastMessage = message
		});
	}

	private void SetSnapshot(RetainerCreationSnapshot value)
	{
		lock (sync)
		{
			snapshot = value;
		}
	}

	private static void EnsureSuccess(AutoRetainerMutationResult result)
	{
		if (!result.Success)
		{
			throw new InvalidOperationException(result.Error);
		}
	}

	private static CharacterRetainerSetupChoice CloneChoice(CharacterRetainerSetupChoice source)
	{
		return new CharacterRetainerSetupChoice
		{
			CharacterKey = source.CharacterKey,
			Type = source.Type,
			CombatStarterClassId = source.CombatStarterClassId
		};
	}

	private static RetainerSetupConfiguration CloneSettings(RetainerSetupConfiguration source)
	{
		return new RetainerSetupConfiguration
		{
			FilterBelowLevelEnabled = source.FilterBelowLevelEnabled,
			FilterBelowLevel = source.FilterBelowLevel,
			FilterIncompleteSetup = source.FilterIncompleteSetup,
			City = source.City,
			Appearance = source.Appearance,
			Gender = source.Gender,
			Clan = source.Clan,
			Personality = source.Personality,
			StopAfter = source.StopAfter,
			AttachStarterPlan = RetainerAutoRetainerBootstrapPolicy.ShouldAttachStarterPlan(source.StopAfter, source.AttachStarterPlan),
			EnableNewRetainers = source.EnableNewRetainers,
			EnableCharacter = source.EnableCharacter,
			SampleNames = source.SampleNames.ToList(),
			Checkpoints = source.Checkpoints,
			CharacterChoices = source.CharacterChoices
		};
	}

	private static RetainerBatchFrozenSettings ToFrozenSettings(RetainerSetupConfiguration source, IEnumerable<string> unavailableNames)
	{
		return new RetainerBatchFrozenSettings
		{
			City = source.City,
			Appearance = source.Appearance,
			Gender = source.Gender,
			Clan = source.Clan,
			Personality = source.Personality,
			StopAfter = source.StopAfter,
			AttachStarterPlan = RetainerAutoRetainerBootstrapPolicy.ShouldAttachStarterPlan(source.StopAfter, source.AttachStarterPlan),
			EnableNewRetainers = source.EnableNewRetainers,
			EnableCharacter = source.EnableCharacter,
			SampleNames = source.SampleNames.ToList(),
			UnavailableNames = (from name in unavailableNames
				where !string.IsNullOrWhiteSpace(name)
				select name.Trim()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList()
		};
	}

	private static RetainerSetupConfiguration FromFrozenSettings(RetainerBatchFrozenSettings source)
	{
		return new RetainerSetupConfiguration
		{
			City = source.City,
			Appearance = source.Appearance,
			Gender = source.Gender,
			Clan = source.Clan,
			Personality = source.Personality,
			StopAfter = source.StopAfter,
			AttachStarterPlan = RetainerAutoRetainerBootstrapPolicy.ShouldAttachStarterPlan(source.StopAfter, source.AttachStarterPlan),
			EnableNewRetainers = source.EnableNewRetainers,
			EnableCharacter = source.EnableCharacter,
			SampleNames = source.SampleNames.ToList()
		};
	}

	public void Dispose()
	{
		CancellationTokenSource cancellationTokenSource;
		Task task;
		lock (sync)
		{
			disposalRequested = true;
			cancellationTokenSource = cancellationSource;
			task = runnerTask;
			RetainerBatchHandoffCheckpoint retainerBatchHandoff = configuration.RetainerBatchHandoff;
			if (retainerBatchHandoff != null)
			{
				retainerBatchHandoff.SuspendedByDisposal = true;
				retainerBatchHandoff.UpdatedUtc = DateTime.UtcNow;
				snapshot = snapshot with
				{
					IsRunning = false,
					CurrentStage = "Suspended",
					LastMessage = "Plugin disposal suspended the in-memory runner; durable recovery is preserved.",
					CanCancel = false
				};
			}
		}
		disposalCancellationSource.Cancel();
		cancellationTokenSource?.Cancel();
		game.StopVocateTalkSkippingForDisposal();
		try
		{
			if (configuration.RetainerBatchHandoff != null)
			{
				configuration.Save();
				log.Information("[RetainerSetup] Suspended batch " + configuration.RetainerBatchHandoff.BatchId + " for plugin disposal; no operator cancellation or asynchronous cleanup was started.");
			}
		}
		catch (Exception ex)
		{
			log.Error("[RetainerSetup] Could not stamp handoff suspension during disposal: " + ex.Message);
		}
		task?.ContinueWith((Task task2) => task2.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}
}
