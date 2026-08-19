using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

internal sealed class RetainerPostCreationCoordinator
{
	private sealed record StageDefinition(RetainerPostCreationStage Stage, RetainerStopAfter Checkpoint, int WorkUnits, Func<RetainerPostCreationActions, Func<CancellationToken, Task>> SelectAction);

	private static readonly StageDefinition[] Stages = new StageDefinition[4]
	{
		new StageDefinition(RetainerPostCreationStage.UnlockVentures, RetainerStopAfter.VenturesUnlocked, 2, (RetainerPostCreationActions actions) => actions.UnlockVenturesAsync),
		new StageDefinition(RetainerPostCreationStage.PurchaseStarterEquipment, RetainerStopAfter.StarterGearReady, 3, (RetainerPostCreationActions actions) => actions.PurchaseStarterEquipmentAsync),
		new StageDefinition(RetainerPostCreationStage.AssignClassAndEquipment, RetainerStopAfter.ClassAndGearAssigned, 4, (RetainerPostCreationActions actions) => actions.AssignClassAndEquipmentAsync),
		new StageDefinition(RetainerPostCreationStage.BootstrapAutoRetainer, RetainerStopAfter.AutoRetainerBootstrapped, 5, (RetainerPostCreationActions actions) => actions.BootstrapAutoRetainerAsync)
	};

	public async Task<RetainerPostCreationResult> ExecuteAsync(CharacterRetainerSetupCheckpoint checkpoint, RetainerStopAfter stopAfter, RetainerPostCreationActions actions, CancellationToken token)
	{
		if (await StopIfRequestedAsync(checkpoint, stopAfter, RetainerStopAfter.RetainersHired, actions, token))
		{
			return RetainerPostCreationResult.DeliberatelyStopped;
		}
		StageDefinition[] stages = Stages;
		foreach (StageDefinition stage in stages)
		{
			token.ThrowIfCancellationRequested();
			if (checkpoint.Retainers.Any((TrackedRetainerCheckpoint retainer) => retainer.CompletedWorkUnits < stage.WorkUnits))
			{
				checkpoint.PendingCheckpoint = stage.Checkpoint;
				checkpoint.UpdatedUtc = DateTime.UtcNow;
				await actions.PersistCheckpointAsync(token);
				await actions.StageStartingAsync(stage.Stage, token);
				await stage.SelectAction(actions)(token);
				MarkStageVerified(checkpoint, stage.WorkUnits);
				if (stage.Checkpoint == RetainerStopAfter.AutoRetainerBootstrapped)
				{
					checkpoint.CleanupVerified = true;
				}
				await actions.PersistCheckpointAsync(token);
				await actions.StageCompletedAsync(stage.Stage, token);
			}
			if (await StopIfRequestedAsync(checkpoint, stopAfter, stage.Checkpoint, actions, token))
			{
				return (stage.Checkpoint != RetainerStopAfter.AutoRetainerBootstrapped) ? RetainerPostCreationResult.DeliberatelyStopped : RetainerPostCreationResult.Completed;
			}
		}
		return RetainerPostCreationResult.Completed;
	}

	private static void MarkStageVerified(CharacterRetainerSetupCheckpoint checkpoint, int workUnits)
	{
		foreach (TrackedRetainerCheckpoint retainer in checkpoint.Retainers)
		{
			retainer.CompletedWorkUnits = Math.Max(retainer.CompletedWorkUnits, workUnits);
		}
		checkpoint.PendingCheckpoint = null;
		checkpoint.LastVerifiedCheckpoint = (RetainerStopAfter)workUnits;
		checkpoint.State = ((workUnits < 5) ? RetainerCheckpointState.Running : RetainerCheckpointState.Complete);
		checkpoint.Disposition = ((workUnits >= 5) ? RetainerCheckpointDisposition.Complete : RetainerCheckpointDisposition.ResumablePartial);
		checkpoint.UpdatedUtc = DateTime.UtcNow;
	}

	private static async Task<bool> StopIfRequestedAsync(CharacterRetainerSetupCheckpoint checkpoint, RetainerStopAfter requested, RetainerStopAfter current, RetainerPostCreationActions actions, CancellationToken token)
	{
		if (requested != current)
		{
			return false;
		}
		checkpoint.State = ((current == RetainerStopAfter.AutoRetainerBootstrapped) ? RetainerCheckpointState.Complete : RetainerCheckpointState.DeliberatelyStopped);
		checkpoint.Disposition = ((current == RetainerStopAfter.AutoRetainerBootstrapped) ? RetainerCheckpointDisposition.Complete : RetainerCheckpointDisposition.ResumablePartial);
		checkpoint.PendingCheckpoint = null;
		checkpoint.UpdatedUtc = DateTime.UtcNow;
		await actions.PersistCheckpointAsync(token);
		return true;
	}
}
