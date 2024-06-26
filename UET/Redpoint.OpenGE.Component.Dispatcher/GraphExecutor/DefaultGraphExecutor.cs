﻿namespace Redpoint.OpenGE.Component.Dispatcher.GraphExecutor
{
    using Google.Protobuf.Collections;
    using Grpc.Core;
    using Microsoft.Extensions.Logging;
    using Redpoint.OpenGE.Component.Dispatcher.Graph;
    using Redpoint.OpenGE.Component.Dispatcher.Remoting;
    using Redpoint.OpenGE.Component.Dispatcher.StallDiagnostics;
    using Redpoint.OpenGE.Component.Dispatcher.WorkerPool;
    using Redpoint.OpenGE.Core;
    using Redpoint.OpenGE.Protocol;
    using Redpoint.Tasks;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading.Tasks;
    using Redpoint.Concurrency;

    internal class DefaultGraphExecutor : IGraphExecutor
    {
        private readonly ILogger<DefaultGraphExecutor> _logger;
        private readonly IToolSynchroniser _toolSynchroniser;
        private readonly IBlobSynchroniser _blobSynchroniser;
        private readonly ITaskScheduler _taskScheduler;
        private readonly IStallMonitorFactory _stallMonitorFactory;

        public DefaultGraphExecutor(
            ILogger<DefaultGraphExecutor> logger,
            IToolSynchroniser toolSynchroniser,
            IBlobSynchroniser blobSynchroniser,
            ITaskScheduler taskScheduler,
            IStallMonitorFactory stallMonitorFactory)
        {
            _logger = logger;
            _toolSynchroniser = toolSynchroniser;
            _blobSynchroniser = blobSynchroniser;
            _taskScheduler = taskScheduler;
            _stallMonitorFactory = stallMonitorFactory;
        }

        public async Task ExecuteGraphAsync(
            ITaskApiWorkerPool workerPool,
            Graph graph,
            JobBuildBehaviour buildBehaviour,
            IGuardedResponseStream<JobResponse> responseStream,
            CancellationToken cancellationToken)
        {
            // Make sure there's at least one task. Empty graphs should not be passed
            // to this function.
            if (graph.Tasks.Count == 0)
            {
                throw new ArgumentException("There are no tasks defined in the graph.");
            }

            var graphStopwatch = Stopwatch.StartNew();

            // Create a task scheduler scope for running our tasks on.
            await using (_taskScheduler.CreateSchedulerScope($"ExecuteGraphAsync", cancellationToken).AsAsyncDisposable(out var schedulerScope).ConfigureAwait(false))
            {
                // Track the state of this graph execution.
                var instance = new GraphExecutionInstance(graph, cancellationToken)
                {
                    WorkerPool = workerPool,
                };

                // Create a stall monitor which dumps information if the execution stops making progress.
                await using (_stallMonitorFactory.CreateStallMonitor(
                    schedulerScope,
                    instance).AsAsyncDisposable(out var stallMonitor).ConfigureAwait(false))
                {
                    instance.StallMonitor = stallMonitor;

                    // Schedule up all of the tasks that can be immediately scheduled.
                    _logger.LogTrace("Scheduling initial tasks...");
                    await instance.ScheduleInitialTasksAsync().ConfigureAwait(false);

                    // At this point, if we don't have anything that can be
                    // scheduled, then the execution can never make any progress.
                    if (!await instance.AreAnyTasksScheduledAsync().ConfigureAwait(false))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "No task described by the job XML was immediately schedulable."));
                    }

                    // Pull tasks off the queue until we have no tasks remaining.
                    try
                    {
                        while (!instance.CancellationToken.IsCancellationRequested)
                        {
                            // Get the next task to schedule. This queue only contains
                            // tasks whose dependencies have all passed.
                            var (task, terminated) = await instance.QueuedTasksForScheduling.TryDequeueAsync(instance.CancellationToken).ConfigureAwait(false);
                            if (terminated)
                            {
                                // No more tasks to process.
                                _logger.LogTrace("No more tasks to execute. Ending execution graph.");
                                break;
                            }

                            // Schedule the task to run on the thread pool.
                            _logger.LogTrace("Pulled task from the queue and scheduling it's work.");
                            instance.ScheduledExecutions.Add(schedulerScope.RunAsync(
                                task!.GraphTaskSpec.Task.Name,
                                async (cancellationToken) =>
                                {
                                    await ExecuteTaskAsync(
                                        schedulerScope,
                                        instance,
                                        graph,
                                        task!,
                                        buildBehaviour,
                                        responseStream,
                                        cancellationToken).ConfigureAwait(false);
                                },
                                cancellationToken));
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        _logger.LogTrace("Checking if all tasks executed successfully...");
                        if (await instance.DidAllTasksCompleteSuccessfullyAsync().ConfigureAwait(false))
                        {
                            // All tasks completed successfully.
                            await responseStream.WriteAsync(new JobResponse
                            {
                                JobComplete = new JobCompleteResponse
                                {
                                    Status = JobCompletionStatus.JobCompletionSuccess,
                                    TotalSeconds = graphStopwatch.Elapsed.TotalSeconds,
                                }
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Something failed.
                            await responseStream.WriteAsync(new JobResponse
                            {
                                JobComplete = new JobCompleteResponse
                                {
                                    Status = JobCompletionStatus.JobCompletionFailure,
                                    TotalSeconds = graphStopwatch.Elapsed.TotalSeconds,
                                }
                            }, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                    {
                        // Convert the RPC exception into an OperationCanceledException.
                        throw new OperationCanceledException("Cancellation via RPC", ex);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // We're stopping because the caller cancelled the build (i.e. the client
                        // of the dispatcher RPC hit "Ctrl-C").
                        throw;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (instance.IsCancelledDueToException)
                        {
                            // This is a propagation of build cancellation due to
                            // task failure, which can happen due to the top-level
                            // instance.QueuedTaskAvailableForScheduling.WaitAsync call.
                            // In this case, we want to wait until all the scheduled
                            // executions complete so task-level failures propagate to
                            // the stream before we issue a JobCompleteResponse.
                            foreach (var execution in instance.ScheduledExecutions)
                            {
                                try
                                {
                                    await execution.ConfigureAwait(false);
                                }
                                catch
                                {
                                }
                            }
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                await responseStream.WriteAsync(new JobResponse
                                {
                                    JobComplete = new JobCompleteResponse
                                    {
                                        Status = JobCompletionStatus.JobCompletionFailure,
                                        TotalSeconds = graphStopwatch.Elapsed.TotalSeconds,
                                        ExceptionMessage = instance.ExceptionMessage ?? string.Empty,
                                    }
                                }, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }

            _logger.LogTrace("ExecuteGraphAsync work has completed.");
        }

        private async Task ExecuteTaskAsync(
            ITaskSchedulerScope schedulerScope,
            GraphExecutionInstance instance,
            Graph graph,
            GraphTask task,
            JobBuildBehaviour buildBehaviour,
            IGuardedResponseStream<JobResponse> responseStream,
            CancellationToken cancellationToken)
        {
            var status = TaskCompletionStatus.TaskCompletionException;
            var exitCode = 1;
            var exceptionMessage = string.Empty;
            var didStart = false;
            var didComplete = false;
            var taskStopwatch = new Stopwatch();
            var skipEmitComplete = false;
            var currentPhaseStopwatch = new Stopwatch();
            var currentPhaseStartTimestamp = DateTimeOffset.UtcNow;
            var currentPhase = TaskPhase.Initial;
            var finalPhaseMetadata = new Dictionary<string, string>();
            async Task SendPhaseChangeAsync(
                TaskPhase newPhase,
                IDictionary<string, string> previousPhaseMetadata)
            {
                currentPhase = newPhase;
                currentPhaseStartTimestamp = DateTimeOffset.UtcNow;
                var totalSecondsInToolSynchronisationPhase = currentPhaseStopwatch!.Elapsed.TotalSeconds;
                currentPhaseStopwatch.Restart();
                var phaseChange = new TaskPhaseChangeResponse
                {
                    Id = task.GraphTaskSpec.Task.Name,
                    DisplayName = task.GraphTaskSpec.Task.Caption,
                    NewPhase = currentPhase,
                    NewPhaseStartTimeUtcTicks = currentPhaseStartTimestamp.UtcTicks,
                    TotalSecondsInPreviousPhase = totalSecondsInToolSynchronisationPhase
                };
                phaseChange.PreviousPhaseMetadata.Add(previousPhaseMetadata);
                await responseStream.WriteAsync(new JobResponse
                {
                    TaskPhaseChange = phaseChange,
                }, cancellationToken).ConfigureAwait(false);
            }
            await instance.SetTaskStatusAsync(task, GraphTaskStatus.Starting).ConfigureAwait(false);
            try
            {
            restartTaskOnRemoteCancellation:
                try
                {
                    // Try to get a local core first, since this will let us run remote
                    // tasks much faster.
                    IWorkerCoreRequest<ITaskApiWorkerCore>? localCoreRequest = null;
                    if (buildBehaviour != null && buildBehaviour.ForceRemotingForLocalWorker)
                    {
                        _logger.LogTrace("Skipping fast local execution because this job requested forced remoting.");
                    }
                    else if (Debugger.IsAttached)
                    {
                        _logger.LogTrace("Skipping fast local execution because a debugger is attached.");
                    }
                    else
                    {
                        _logger.LogTrace("Trying to get a local core...");
                        var localCoreTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                            new CancellationTokenSource(250).Token,
                            instance.CancellationToken);
                        try
                        {
                            await instance.SetTaskStatusAsync(task, GraphTaskStatus.WaitingForFastLocalCore).ConfigureAwait(false);
                            localCoreRequest = await instance.WorkerPool.ReserveCoreAsync(
                                CoreAllocationPreference.RequireLocal,
                                localCoreTimeout.Token).ConfigureAwait(false);
                            _logger.LogTrace("Obtained a local core.");
                        }
                        catch (OperationCanceledException)
                        {
                            // Could not get a local core fast enough.
                            _logger.LogTrace("Unable to get a local core, potentially remoting instead.");
                        }
                    }
                    try
                    {
                        // Do descriptor generation based on the task.
                        await instance.SetTaskStatusAsync(task, GraphTaskStatus.ComputingTaskDescriptor).ConfigureAwait(false);
                        TaskDescriptor taskDescriptor;
                        switch (task)
                        {
                            case DescribingGraphTask describingGraphTask:
                                // Generate the task descriptor from the factory. This can take a while
                                // if we're parsing preprocessor headers.
                                var downstreamTasks = graph.TaskDependencies.WhatDependsOnTarget(task);
                                var isLocalCoreCandidateThatCanRunLocally =
                                    localCoreRequest != null &&
                                    downstreamTasks.Count == 1;
                                Stopwatch? prepareStopwatch = null;
                                IWorkerCoreRequest<ITaskApiWorkerCore>? describingCoreRequest = null;
                                try
                                {
                                    if (!string.IsNullOrWhiteSpace(describingGraphTask.TaskDescriptorFactory.PreparationOperationDescription) &&
                                        !isLocalCoreCandidateThatCanRunLocally)
                                    {
                                        // We need to allocate a core for this local work, since it might be
                                        // long running.
                                        describingCoreRequest = await instance.WorkerPool.ReserveCoreAsync(
                                            CoreAllocationPreference.RequireLocal,
                                            instance.CancellationToken).ConfigureAwait(false);

                                        // Notify the client where we're preparing the descriptor.
                                        var describingCore = await describingCoreRequest.WaitForCoreAsync(CancellationToken.None).ConfigureAwait(false);
                                        prepareStopwatch = Stopwatch.StartNew();
                                        await responseStream.WriteAsync(new JobResponse
                                        {
                                            TaskPreparing = new TaskPreparingResponse
                                            {
                                                Id = describingGraphTask.GraphTaskSpec.Task.Name,
                                                DisplayName = describingGraphTask.GraphTaskSpec.Task.Caption,
                                                OperationDescription = describingGraphTask.TaskDescriptorFactory.PreparationOperationDescription,
                                                WorkerMachineName = describingCore.WorkerMachineName,
                                                WorkerCoreNumber = describingCore.WorkerCoreNumber,
                                            }
                                        }, cancellationToken).ConfigureAwait(false);
                                    }
                                    describingGraphTask.TaskDescriptor = taskDescriptor = await describingGraphTask.TaskDescriptorFactory.CreateDescriptorForTaskSpecAsync(
                                        task.GraphTaskSpec,
                                        isLocalCoreCandidateThatCanRunLocally,
                                        instance.CancellationToken).ConfigureAwait(false);
                                    if (describingGraphTask.TaskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                        !taskDescriptor.Remote.UseFastLocalExecution)
                                    {
                                        // We must hash the tools and blobs ahead of time while we don't
                                        // hold a reservation on a remote worker. We can't spend time hashing
                                        // data with a reservation open because remote workers will kick us for
                                        // being idle.
                                        await Task.WhenAll(
                                            schedulerScope.RunAsync($"{task.GraphTaskSpec.Task.Name}:HashTool", async (cancellationToken) =>
                                            {
                                                describingGraphTask.ToolHashingResult = await _toolSynchroniser.HashToolAsync(
                                                    describingGraphTask.TaskDescriptor.Remote,
                                                    cancellationToken).ConfigureAwait(false);
                                            }, instance.CancellationToken),
                                            schedulerScope.RunAsync($"{task.GraphTaskSpec.Task.Name}:HashInputBlobs", async (cancellationToken) =>
                                            {
                                                describingGraphTask.BlobHashingResult = await _blobSynchroniser.HashInputBlobsAsync(
                                                    describingGraphTask.TaskDescriptor.Remote,
                                                    cancellationToken).ConfigureAwait(false);
                                            }, instance.CancellationToken)).ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    if (describingCoreRequest != null)
                                    {
                                        await describingCoreRequest.DisposeAsync().ConfigureAwait(false);
                                    }
                                }
                                if (prepareStopwatch != null &&
                                    !isLocalCoreCandidateThatCanRunLocally)
                                {
                                    await responseStream.WriteAsync(new JobResponse
                                    {
                                        TaskPrepared = new TaskPreparedResponse
                                        {
                                            Id = task.GraphTaskSpec.Task.Name,
                                            DisplayName = task.GraphTaskSpec.Task.Caption,
                                            TotalSeconds = prepareStopwatch!.Elapsed.TotalSeconds,
                                            OperationCompletedDescription = describingGraphTask.TaskDescriptorFactory.PreparationOperationCompletedDescription ?? string.Empty,
                                        }
                                    }, cancellationToken).ConfigureAwait(false);
                                }
                                if (isLocalCoreCandidateThatCanRunLocally)
                                {
                                    // Shortcut into the downstream execution task.
                                    await instance.FinishTaskAsync(
                                        task,
                                        TaskCompletionStatus.TaskCompletionSuccess,
                                        GraphExecutionDownstreamScheduling.ImmediatelyScheduledDueToFastExecution).ConfigureAwait(false);
                                    task = downstreamTasks.First();
                                }
                                break;
                            case FastExecutableGraphTask fastExecutableGraphTask:
                                taskDescriptor = await fastExecutableGraphTask.TaskDescriptorFactory.CreateDescriptorForTaskSpecAsync(
                                    fastExecutableGraphTask.GraphTaskSpec,
                                    localCoreRequest != null,
                                    instance.CancellationToken).ConfigureAwait(false);
                                if (taskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                    !taskDescriptor.Remote.UseFastLocalExecution)
                                {
                                    // We must hash the tools and blobs ahead of time while we don't
                                    // hold a reservation on a remote worker. We can't spend time hashing
                                    // data with a reservation open because remote workers will kick us for
                                    // being idle.
                                    await Task.WhenAll(
                                        schedulerScope.RunAsync($"{task.GraphTaskSpec.Task.Name}:HashTool", async (cancellationToken) =>
                                        {
                                            fastExecutableGraphTask.ToolHashingResult = await _toolSynchroniser.HashToolAsync(
                                                taskDescriptor.Remote,
                                                cancellationToken).ConfigureAwait(false);
                                        }, instance.CancellationToken),
                                        schedulerScope.RunAsync($"{task.GraphTaskSpec.Task.Name}:HashInputBlobs", async (cancellationToken) =>
                                        {
                                            if (taskDescriptor.Remote.StorageLayerCase == RemoteTaskDescriptor.StorageLayerOneofCase.TransferringStorageLayer)
                                            {
                                                fastExecutableGraphTask.BlobHashingResult = await _blobSynchroniser.HashInputBlobsAsync(
                                                    taskDescriptor.Remote,
                                                    cancellationToken).ConfigureAwait(false);
                                            }
                                        }, instance.CancellationToken)).ConfigureAwait(false);
                                }
                                break;
                            case ExecutableGraphTask executableGraphTask:
                                taskDescriptor = executableGraphTask.DescribingGraphTask.TaskDescriptor!;
                                break;
                            default:
                                throw new NotSupportedException();
                        }

                        // Do execution based on the task.
                        switch (task)
                        {
                            case DescribingGraphTask describingGraphTask:
                                // No execution work to do for this task.
                                if (describingGraphTask.TaskDescriptor == null ||
                                    (describingGraphTask.TaskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                        describingGraphTask.TaskDescriptor.Remote.UseFastLocalExecution))
                                {
                                    exitCode = 1;
                                    status = TaskCompletionStatus.TaskCompletionException;
                                    didComplete = true;
                                    skipEmitComplete = false;
                                    exceptionMessage = "Resulting descriptor was not valid from describing task!";
                                }
                                else
                                {
                                    exitCode = 0;
                                    status = TaskCompletionStatus.TaskCompletionSuccess;
                                    didComplete = true;
                                    skipEmitComplete = true;
                                }
                                break;
                            case IRemotableGraphTask remotableGraphTask:
                                {
                                    // Reserve a core from somewhere...
                                    _logger.LogTrace("Waiting for core reservation for task...");
                                    IWorkerCoreRequest<ITaskApiWorkerCore> coreRequest;
                                    if (localCoreRequest != null)
                                    {
                                        coreRequest = localCoreRequest;
                                        localCoreRequest = null; // So we don't call DisposeAsync twice on the same core.
                                    }
                                    else
                                    {
                                        if (taskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                            taskDescriptor.Remote.UseFastLocalExecution)
                                        {
                                            throw new InvalidOperationException("UseFastLocalExecution must not be set if we're not using a local core!");
                                        }
                                        await instance.SetTaskStatusAsync(task, GraphTaskStatus.WaitingForCore).ConfigureAwait(false);
                                        coreRequest = await instance.WorkerPool.ReserveCoreAsync(
                                            taskDescriptor.DescriptorCase != TaskDescriptor.DescriptorOneofCase.Remote
                                                ? CoreAllocationPreference.RequireLocal
                                                : CoreAllocationPreference.PreferRemote,
                                            instance.CancellationToken).ConfigureAwait(false);
                                    }
                                    await instance.SetTaskStatusAsync(task, GraphTaskStatus.ExecutingTaskDescriptor).ConfigureAwait(false);
                                    await using (coreRequest)
                                    {
                                        // We're now going to start doing the work for this task.
                                        var core = await coreRequest.WaitForCoreAsync(CancellationToken.None).ConfigureAwait(false);
                                        _logger.LogTrace($"Got core reservation from: {core.WorkerMachineName} {core.WorkerCoreNumber}");
                                        taskStopwatch.Start();
                                        currentPhaseStopwatch.Start();
                                        currentPhase = (
                                            taskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                            !taskDescriptor.Remote.UseFastLocalExecution)
                                                ? TaskPhase.RemoteToolSynchronisation
                                                : TaskPhase.TaskExecution;
                                        await responseStream.WriteAsync(new JobResponse
                                        {
                                            TaskStarted = new TaskStartedResponse
                                            {
                                                Id = task.GraphTaskSpec.Task.Name,
                                                DisplayName = task.GraphTaskSpec.Task.Caption,
                                                WorkerMachineName = core.WorkerMachineName,
                                                WorkerCoreNumber = core.WorkerCoreNumber,
                                                InitialPhaseStartTimeUtcTicks = currentPhaseStartTimestamp.UtcTicks,
                                                InitialPhase = currentPhase,
                                            },
                                        }, instance.CancellationToken).ConfigureAwait(false);
                                        didStart = true;

                                        // Perform synchronisation for remote tasks.
                                        if (taskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                            !taskDescriptor.Remote.UseFastLocalExecution)
                                        {
                                            // Synchronise the tool and determine the hash to
                                            // use for the actual request.
                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Synchronising tool...");
                                            var toolExecutionInfo = await _toolSynchroniser.SynchroniseToolAndGetXxHash64Async(
                                                core,
                                                remotableGraphTask.ToolHashingResult!,
                                                instance.CancellationToken).ConfigureAwait(false);
                                            taskDescriptor.Remote.ToolExecutionInfo = toolExecutionInfo;

                                            // Notify the client we're changing phases.
                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Sending phase change to client...");
                                            await SendPhaseChangeAsync(
                                                TaskPhase.RemoteInputBlobSynchronisation,
                                                new Dictionary<string, string>
                                                {
                                                    { "tool.xxHash64", taskDescriptor.Remote.ToolExecutionInfo.ToolXxHash64.HexString() },
                                                    { "tool.executableName", taskDescriptor.Remote.ToolExecutionInfo.ToolExecutableName },
                                                }).ConfigureAwait(false);

                                            // Synchronise all of the input blobs if needed.
                                            Dictionary<string, string> syncPhaseStats;
                                            if (taskDescriptor.Remote.StorageLayerCase == RemoteTaskDescriptor.StorageLayerOneofCase.TransferringStorageLayer)
                                            {
                                                _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Synchronising {remotableGraphTask.BlobHashingResult!.ContentHashesToContent.Count} blobs...");
                                                var inputBlobSynchronisation = await _blobSynchroniser.SynchroniseInputBlobsAsync(
                                                    core,
                                                    remotableGraphTask.BlobHashingResult!,
                                                    instance.CancellationToken).ConfigureAwait(false);
                                                taskDescriptor.Remote.TransferringStorageLayer.InputsByBlobXxHash64 = inputBlobSynchronisation.Result;
                                                syncPhaseStats = new Dictionary<string, string>
                                                {
                                                    {
                                                        "inputBlobSync.elapsedUtcTicksHashingInputFiles",
                                                        inputBlobSynchronisation.ElapsedUtcTicksHashingInputFiles.ToString(CultureInfo.InvariantCulture)
                                                    },
                                                    {
                                                        "inputBlobSync.elapsedUtcTicksQueryingMissingBlobs",
                                                        inputBlobSynchronisation.ElapsedUtcTicksQueryingMissingBlobs.ToString(CultureInfo.InvariantCulture)
                                                    },
                                                    {
                                                        "inputBlobSync.elapsedUtcTicksTransferringCompressedBlobs",
                                                        inputBlobSynchronisation.ElapsedUtcTicksTransferringCompressedBlobs.ToString(CultureInfo.InvariantCulture)
                                                    },
                                                    {
                                                        "inputBlobSync.compressedDataTransferLength",
                                                        inputBlobSynchronisation.CompressedDataTransferLength.ToString(CultureInfo.InvariantCulture)
                                                    },
                                                };
                                            }
                                            else
                                            {
                                                syncPhaseStats = new Dictionary<string, string>();
                                            }

                                            // Notify the client we're changing phases.
                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Sending phase change to client...");
                                            await SendPhaseChangeAsync(
                                                TaskPhase.TaskExecution,
                                                syncPhaseStats).ConfigureAwait(false);
                                        }

                                    // Execute the task on the core.
                                    restartExecutionOnPartialCompletion:
                                        _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Executing task...");
                                        var executeTaskRequest = new ExecuteTaskRequest
                                        {
                                            Descriptor_ = taskDescriptor,
                                        };
                                        if (task.GraphTaskSpec.Tool.AutoRecover != null)
                                        {
                                            executeTaskRequest.AutoRecover.AddRange(task.GraphTaskSpec.Tool.AutoRecover);
                                        }
                                        // @note: This hides MSVC's useless output where it shows you the filename
                                        // of the file you are compiling.
                                        executeTaskRequest.IgnoreLines.Add(task.GraphTaskSpec.Task.Caption);
                                        await core.Request.RequestStream.WriteAsync(new ExecutionRequest
                                        {
                                            ExecuteTask = executeTaskRequest
                                        }, instance.CancellationToken).ConfigureAwait(false);

                                        // Stream the results until we get an exit code.
                                        ExecuteTaskResponse? finalExecuteTaskResponse = null;
                                        await using (core.Request.GetAsyncEnumerator(instance.CancellationToken).AsAsyncDisposable(out var enumerable).ConfigureAwait(false))
                                        {
                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Begin streaming execution events from worker.");
                                            while (!didComplete && await enumerable.MoveNextAsync(instance.CancellationToken).ConfigureAwait(false))
                                            {
                                                var current = enumerable.Current;
                                                _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Received message type: {current.ResponseCase}");
                                                if (current.ResponseCase != ExecutionResponse.ResponseOneofCase.ExecuteTask)
                                                {
                                                    throw new RpcException(new Status(
                                                        StatusCode.InvalidArgument,
                                                        "Unexpected task execution response from worker RPC."));
                                                }
                                                switch (current.ExecuteTask.Response.DataCase)
                                                {
                                                    case ProcessResponse.DataOneofCase.StandardOutputLine:
                                                        if (_logger.IsEnabled(LogLevel.Trace))
                                                        {
                                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Received standard output line: {current.ExecuteTask.Response.StandardOutputLine}");
                                                        }
                                                        await responseStream.WriteAsync(new JobResponse
                                                        {
                                                            TaskOutput = new TaskOutputResponse
                                                            {
                                                                Id = task.GraphTaskSpec.Task.Name,
                                                                StandardOutputLine = current.ExecuteTask.Response.StandardOutputLine,
                                                            }
                                                        }, cancellationToken).ConfigureAwait(false);
                                                        break;
                                                    case ProcessResponse.DataOneofCase.StandardErrorLine:
                                                        if (_logger.IsEnabled(LogLevel.Trace))
                                                        {
                                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Received standard error line: {current.ExecuteTask.Response.StandardErrorLine}");
                                                        }
                                                        await responseStream.WriteAsync(new JobResponse
                                                        {
                                                            TaskOutput = new TaskOutputResponse
                                                            {
                                                                Id = task.GraphTaskSpec.Task.Name,
                                                                StandardErrorLine = current.ExecuteTask.Response.StandardErrorLine,
                                                            }
                                                        }, cancellationToken).ConfigureAwait(false);
                                                        break;
                                                    case ProcessResponse.DataOneofCase.ExitCode:
                                                        if (_logger.IsEnabled(LogLevel.Trace))
                                                        {
                                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Received exit code: {current.ExecuteTask.Response.ExitCode}");
                                                        }
                                                        exitCode = current.ExecuteTask.Response.ExitCode;
                                                        finalExecuteTaskResponse = current.ExecuteTask;
                                                        status = exitCode == 0
                                                            ? TaskCompletionStatus.TaskCompletionSuccess
                                                            : TaskCompletionStatus.TaskCompletionFailure;
                                                        didComplete = true;
                                                        break;
                                                }
                                            }
                                            _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Finished streaming execution events from worker, releasing reservation request...");
                                        }
                                        _logger.LogTrace($"{core.WorkerCoreUniqueAssignmentId}: Released core reservation request after execution finished.");

                                        if (!didComplete)
                                        {
                                            // The remote worker gracefully closed the connection without actually running.
                                            _logger.LogWarning("Worker gracefully closed connection without providing an exit code for the task. Automatically restarting the task execution.");
                                            goto restartExecutionOnPartialCompletion;
                                        }

                                        if (finalExecuteTaskResponse == null)
                                        {
                                            // This should never be null since we break the loop on ExitCode.
                                            throw new InvalidOperationException("Final execute task response was null, but we should not exit the loop without a final result.");
                                        }

                                        // Attach any metadata from the task execution.
                                        // @note: We don't have any metadata for task execution yet.

                                        // If we were successful, synchronise the output blobs back.
                                        if (taskDescriptor.DescriptorCase == TaskDescriptor.DescriptorOneofCase.Remote &&
                                            taskDescriptor.Remote.StorageLayerCase == RemoteTaskDescriptor.StorageLayerOneofCase.TransferringStorageLayer &&
                                            status == TaskCompletionStatus.TaskCompletionSuccess &&
                                            finalExecuteTaskResponse.OutputAbsolutePathsToBlobXxHash64 != null &&
                                            !taskDescriptor.Remote.UseFastLocalExecution)
                                        {
                                            // Notify the client we're changing phases.
                                            await SendPhaseChangeAsync(
                                                TaskPhase.RemoteOutputBlobSynchronisation,
                                                finalPhaseMetadata).ConfigureAwait(false);
                                            finalPhaseMetadata.Clear();

                                            var outputBlobSynchronisation = await _blobSynchroniser.SynchroniseOutputBlobsAsync(
                                                core,
                                                taskDescriptor.Remote,
                                                finalExecuteTaskResponse,
                                                instance.CancellationToken).ConfigureAwait(false);
                                            finalPhaseMetadata = new Dictionary<string, string>
                                            {
                                                {
                                                    "outputBlobSync.elapsedUtcTicksHashingInputFiles",
                                                    outputBlobSynchronisation.ElapsedUtcTicksHashingInputFiles.ToString(CultureInfo.InvariantCulture)
                                                },
                                                {
                                                    "outputBlobSync.elapsedUtcTicksQueryingMissingBlobs",
                                                    outputBlobSynchronisation.ElapsedUtcTicksQueryingMissingBlobs.ToString(CultureInfo.InvariantCulture)
                                                },
                                                {
                                                    "outputBlobSync.elapsedUtcTicksTransferringCompressedBlobs",
                                                    outputBlobSynchronisation.ElapsedUtcTicksTransferringCompressedBlobs.ToString(CultureInfo.InvariantCulture)
                                                },
                                                {
                                                    "outputBlobSync.compressedDataTransferLength",
                                                    outputBlobSynchronisation.CompressedDataTransferLength.ToString(CultureInfo.InvariantCulture)
                                                },
                                            };
                                        }
                                    }

                                    break;
                                }
                            default:
                                throw new NotSupportedException($"Unknown graph task to execute: {task.GetType().FullName}");
                        }
                    }
                    finally
                    {
                        if (localCoreRequest != null)
                        {
                            await localCoreRequest.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
                {
                    // We're stopping because the caller cancelled the build (i.e. the client
                    // of the dispatcher RPC hit "Ctrl-C").
                    status = TaskCompletionStatus.TaskCompletionCancelled;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && instance.CancellationToken.IsCancellationRequested)
                {
                    // We're stopping because something else cancelled the build.
                    status = TaskCompletionStatus.TaskCompletionCancelled;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // We're stopping because the caller cancelled the build (i.e. the client
                    // of the dispatcher RPC hit "Ctrl-C").
                    status = TaskCompletionStatus.TaskCompletionCancelled;
                }
                catch (OperationCanceledException) when (instance.CancellationToken.IsCancellationRequested)
                {
                    // We're stopping because something else cancelled the build.
                    status = TaskCompletionStatus.TaskCompletionCancelled;
                }
                catch (OperationCanceledException)
                {
                    // Automatically retry the task if we encountered unexpected cancellation from the remote core.
                    _logger.LogWarning("Remote core unexpectedly went away, automatically rescheduling work...");
                    goto restartTaskOnRemoteCancellation;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    // Automatically retry the task if we encountered unexpected cancellation from the remote core.
                    _logger.LogWarning("Remote core unexpectedly went away, automatically rescheduling work...");
                    goto restartTaskOnRemoteCancellation;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, $"Exception during task execution: {ex.Message}");
                    status = TaskCompletionStatus.TaskCompletionException;
                    skipEmitComplete = false;
                    exceptionMessage = ex.ToString();
                }
            }
            finally
            {
                try
                {
                    if (!skipEmitComplete)
                    {
                        if (!didStart)
                        {
                            // We never actually started this task because we failed
                            // to reserve, but we need to start it so we can then immediately
                            // convey the exception we ran into.
                            await responseStream.WriteAsync(new JobResponse
                            {
                                TaskStarted = new TaskStartedResponse
                                {
                                    Id = task.GraphTaskSpec.Task.Name,
                                    DisplayName = task.GraphTaskSpec.Task.Caption,
                                    WorkerMachineName = string.Empty,
                                    WorkerCoreNumber = 0,
                                },
                            }, instance.CancellationToken).ConfigureAwait(false);
                        }
                        await responseStream.WriteAsync(new JobResponse
                        {
                            TaskCompleted = new TaskCompletedResponse
                            {
                                Id = task.GraphTaskSpec.Task.Name,
                                DisplayName = task.GraphTaskSpec.Task.Caption,
                                Status = status,
                                ExitCode = exitCode,
                                ExceptionMessage = exceptionMessage,
                                TotalSeconds = taskStopwatch.Elapsed.TotalSeconds,
                                FinalPhaseEndTimeUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                                TotalSecondsInPreviousPhase = currentPhaseStopwatch.Elapsed.TotalSeconds,
                                PreviousPhaseMetadata = { finalPhaseMetadata },
                            }
                        }, instance.CancellationToken).ConfigureAwait(false);
                    }

                    await instance.FinishTaskAsync(task, status, GraphExecutionDownstreamScheduling.ScheduleByGraphExecution).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    // We're stopping because the caller cancelled the build (i.e. the client
                    // of the dispatcher RPC hit "Ctrl-C").
                    instance.CancelEntireBuildDueToException(ex);
                }
                catch (OperationCanceledException) when (instance.CancellationToken.IsCancellationRequested)
                {
                    // We're stopping because something else cancelled the build.
                }
                catch (ObjectDisposedException ex) when (ex.Message.Contains("Request has finished and HttpContext disposed.", StringComparison.Ordinal))
                {
                    // We can't send further messages because the response stream has died.
                }
                catch (Exception ex)
                {
                    // If any of this fails, we have to cancel the build.
                    _logger.LogCritical(ex, $"Exception during task execution finalisation: {ex.Message}");
                    instance.CancelEntireBuildDueToException(ex);
                }
            }
        }
    }
}
