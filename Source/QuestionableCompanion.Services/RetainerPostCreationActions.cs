using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuestionableCompanion.Services;

internal sealed record RetainerPostCreationActions(Func<CancellationToken, Task> UnlockVenturesAsync, Func<CancellationToken, Task> PurchaseStarterEquipmentAsync, Func<CancellationToken, Task> AssignClassAndEquipmentAsync, Func<CancellationToken, Task> BootstrapAutoRetainerAsync, Func<RetainerPostCreationStage, CancellationToken, Task> StageStartingAsync, Func<RetainerPostCreationStage, CancellationToken, Task> StageCompletedAsync, Func<CancellationToken, Task> PersistCheckpointAsync);
