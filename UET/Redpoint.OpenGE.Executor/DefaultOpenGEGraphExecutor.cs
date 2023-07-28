﻿namespace Redpoint.OpenGE.Executor
{
    using Crayon;
    using Microsoft.Extensions.Logging;
    using Redpoint.OpenGE.Executor.BuildSetData;
    using Redpoint.ProcessExecution;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using static Crayon.Output;

    internal class DefaultOpenGEGraphExecutor : IOpenGEGraphExecutor
    {
        private readonly ILogger<DefaultOpenGEGraphExecutor> _logger;
        private readonly IOpenGETaskExecutor[] _taskExecutors;
        private readonly Dictionary<string, string> _environmentVariables;
        private readonly bool _turnOffExtraLogInfo;
        private readonly string? _buildLogPrefix;

        private Dictionary<string, OpenGETask> _allTasks;
        private Dictionary<string, OpenGEProject> _allProjects;
        private ConcurrentQueue<OpenGETask> _queuedTasksForProcessing;
        private SemaphoreSlim _queuedTaskAvailableForProcessing;
        private SemaphoreSlim _updatingTaskForScheduling;
        private long _remainingTasks;
        private long _totalTasks;

        public DefaultOpenGEGraphExecutor(
            ILogger<DefaultOpenGEGraphExecutor> logger,
            IOpenGETaskExecutor[] taskExecutors,
            BuildSet buildSet,
            Dictionary<string, string> environmentVariables,
            bool turnOffExtraLogInfo,
            string? buildLogPrefix)
        {
            _logger = logger;
            _taskExecutors = taskExecutors;
            _environmentVariables = environmentVariables;
            _turnOffExtraLogInfo = turnOffExtraLogInfo;
            _buildLogPrefix = buildLogPrefix?.Trim() ?? string.Empty;

            _allProjects = new Dictionary<string, OpenGEProject>();
            _allTasks = new Dictionary<string, OpenGETask>();
            _queuedTasksForProcessing = new ConcurrentQueue<OpenGETask>();
            _queuedTaskAvailableForProcessing = new SemaphoreSlim(0);
            _updatingTaskForScheduling = new SemaphoreSlim(1);

            foreach (var project in buildSet.Projects)
            {
                _allProjects[project.Key] = new OpenGEProject
                {
                    BuildSetProject = project.Value,
                };

                foreach (var task in project.Value.Tasks)
                {
                    _allTasks[$"{project.Key}:{task.Key}"] = new OpenGETask
                    {
                        BuildSet = buildSet,
                        BuildSetProject = project.Value,
                        BuildSetTask = task.Value,
                    };
                }

                foreach (var task in project.Value.Tasks)
                {
                    _allTasks[$"{project.Key}:{task.Key}"].DependsOn.AddRange(
                        (task.Value.DependsOn ?? string.Empty)
                            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(x => _allTasks.ContainsKey($"{project.Key}:{x}"))
                            .Select(x => _allTasks[$"{project.Key}:{x}"]));
                    if (_allTasks[$"{project.Key}:{task.Key}"].DependsOn.Count == 0)
                    {
                        _allTasks[$"{project.Key}:{task.Key}"].Status = OpenGEStatus.Scheduled;
                        _queuedTasksForProcessing.Enqueue(_allTasks[$"{project.Key}:{task.Key}"]);
                        _queuedTaskAvailableForProcessing.Release();
                    }
                }

                foreach (var task in _allTasks)
                {
                    foreach (var dependsOn in task.Value.DependsOn)
                    {
                        dependsOn.Dependents.Add(task.Value);
                    }
                }
            }

            _remainingTasks = _allTasks.Count;
            _totalTasks = _remainingTasks;
        }

        public bool CancelledDueToFailure { get; set; }

        private (IOpenGETaskExecutor? executor, string[] arguments) GetExecutorForTask(OpenGETask task)
        {
            var env = task.BuildSet.Environments[task.BuildSetProject.Env];
            var tool = env.Tools[task.BuildSetTask.Tool];
            var arguments = SplitArguments(tool.Params);

            var currentScore = -1;
            IOpenGETaskExecutor? currentExecutor = null;
            foreach (var executor in _taskExecutors)
            {
                var score = executor.ScoreTask(
                    task,
                    env!,
                    tool!,
                    arguments);
                if (score != -1 && score > currentScore)
                {
                    currentExecutor = executor;
                }
            }

            return (currentExecutor, arguments);
        }

        public async Task<int> ExecuteAsync(CancellationTokenSource buildCancellationTokenSource)
        {
            try
            {
                var cancellationToken = buildCancellationTokenSource.Token;
                while (!cancellationToken.IsCancellationRequested && _remainingTasks > 0)
                {
                    // Get the next task to schedule. The queue only contains tasks whose dependencies
                    // have all passed.
                    await _queuedTaskAvailableForProcessing.WaitAsync(cancellationToken);
                    if (Interlocked.Read(ref _remainingTasks) == 0)
                    {
                        break;
                    }
                    if (!_queuedTasksForProcessing.TryDequeue(out var nextTask))
                    {
                        throw new InvalidOperationException("Task was available for processing but could not be pulled from queue!");
                    }

                    // Figure out where we're going to execute this task.
                    var (executor, arguments) = GetExecutorForTask(nextTask);
                    var couldSchedule = false;
                    if (executor != null)
                    {
                        try
                        {
                            // Reserve the virtual core that this task will run on.
                            var virtualCore = await executor.AllocateVirtualCoreForTaskExecutionAsync(cancellationToken);
                            try
                            {
                                // Schedule the worker into the task pool on that virtual core.
                                nextTask.ExecutingTask = Task.Run(async () =>
                                {
                                    using (virtualCore)
                                    {
                                        await ExecuteTaskAsync(
                                            executor,
                                            nextTask,
                                            arguments,
                                            virtualCore,
                                            buildCancellationTokenSource);
                                    }
                                });
                                couldSchedule = true;
                            }
                            finally
                            {
                                if (!couldSchedule)
                                {
                                    virtualCore.Dispose();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(0)} Failed to schedule task on virtual core: {ex}");
                        }
                    }
                    else
                    {
                        _logger.LogError($"{GetBuildStatusLogPrefix(0)} No executor available for running that task.");
                    }
                    if (!couldSchedule)
                    {
                        // We can't run this task.
                        var project = _allProjects[nextTask.BuildSetProject.Name];
                        nextTask.Status = OpenGEStatus.Failure;
                        project.Status = OpenGEStatus.Failure;
                        CancelledDueToFailure = true;
                        if (!_turnOffExtraLogInfo)
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(-1)} {nextTask.BuildSetTask.Caption} {Bright.Red("[build failed]")}");
                        }
                        else
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(-1)} {nextTask.BuildSetTask.Caption} [failed]");
                        }
                        buildCancellationTokenSource.Cancel();
                        continue;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return _allTasks.Values.Any(x => x.Status != OpenGEStatus.Success) ? 1 : 0;
            }
            finally
            {
                if (buildCancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Await all of the tasks that are in-progress.
                    _logger.LogTrace("OpenGE build was cancelled. Waiting for all tasks to exit...");
                    foreach (var task in _allTasks)
                    {
                        if (task.Value.ExecutingTask != null)
                        {
                            try
                            {
                                _logger.LogTrace($"Waiting for {task.Value.BuildSetTask.Caption} to exit...");
                                await task.Value.ExecutingTask;
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }
                    }
                    _logger.LogTrace("All tasks in the cancelled OpenGE build have exited.");
                }
            }
        }

        internal static string[] SplitArguments(string arguments)
        {
            var argumentList = new List<string>();
            var buffer = string.Empty;
            var inQuote = false;
            var isEscaping = false;
            for (int i = 0; i < arguments.Length; i++)
            {
                var chr = arguments[i];
                if (isEscaping)
                {
                    if (chr == '\\' || chr == '"')
                    {
                        buffer += chr;
                    }
                    else
                    {
                        buffer += '\\';
                        buffer += chr;
                    }
                    isEscaping = false;
                }
                else if (chr == '\\')
                {
                    isEscaping = true;
                }
                else if (chr == '"')
                {
                    // @todo: Do we need to handle \" sequence?
                    inQuote = !inQuote;
                }
                else if (inQuote)
                {
                    buffer += chr;
                }
                else if (chr == ' ')
                {
                    if (!string.IsNullOrWhiteSpace(buffer))
                    {
                        argumentList.Add(buffer);
                        buffer = string.Empty;
                    }
                }
                else
                {
                    buffer += chr;
                }
            }
            if (!string.IsNullOrWhiteSpace(buffer))
            {
                argumentList.Add(buffer);
            }
            return argumentList.ToArray();
        }

        private string GetBuildStatusLogPrefix(int remainingOffset)
        {
            var remainingTasks = _remainingTasks + remainingOffset;
            var percent = (1.0 - (_totalTasks == 0 ? 0.0 : ((double)remainingTasks / _totalTasks))) * 100.0;
            var totalTasksLength = _totalTasks.ToString().Length;
            return $"{(_buildLogPrefix == string.Empty ? string.Empty : $"{_buildLogPrefix} ")}[{percent,3:0}%, {(_totalTasks - remainingTasks).ToString().PadLeft(totalTasksLength)}/{_totalTasks}]";
        }

        private async Task ExecuteTaskAsync(IOpenGETaskExecutor executor, OpenGETask task, string[] arguments, IDisposable virtualCore, CancellationTokenSource buildCancellationTokenSource)
        {
            var cancellationToken = buildCancellationTokenSource.Token;

            try
            {
                // Check if the project is failed and whether we should skip on project failure.
                var project = _allProjects[task.BuildSetProject.Name];
                if (project.Status == OpenGEStatus.Failure && task.BuildSetTask.SkipIfProjectFailed)
                {
                    task.Status = OpenGEStatus.Skipped;
                    return;
                }

                // Check if any of our dependencies have failed or are skipped. If they have, we are skipped.
                if (task.DependsOn.Any(x => x.Status == OpenGEStatus.Failure || x.Status == OpenGEStatus.Skipped))
                {
                    task.Status = OpenGEStatus.Skipped;
                    return;
                }

                // Start the task.
                try
                {
                    task.Status = OpenGEStatus.Running;
                    if (!_turnOffExtraLogInfo)
                    {
                        _logger.LogInformation($"{GetBuildStatusLogPrefix(0)} {task.BuildSetTask.Caption} {Bright.Black($"[started on {virtualCore.ToString()}]")}");
                    }
                    else
                    {
                        _logger.LogInformation($"{GetBuildStatusLogPrefix(0)} {task.BuildSetTask.Caption}");
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var env = task.BuildSet.Environments[task.BuildSetProject.Env];
                    var tool = env.Tools[task.BuildSetTask.Tool];

                    bool needsRetry = false;
                    void CheckForRetry(string data)
                    {
                        if (data.Contains("error C3859"))
                        {
                            _logger.LogTrace($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} Detected out-of-memory for MSVC (marking as retry needed)");
                            needsRetry = true;
                        }
                        if (data.Contains("fatal error C1356"))
                        {
                            _logger.LogTrace($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} Detected high contention for MSVC (marking as retry needed)");
                            needsRetry = true;
                        }
                        if (data.Contains("error LNK1107"))
                        {
                            _logger.LogTrace($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} Detected out-of-memory for clang-tidy (marking as retry needed)");
                            needsRetry = true;
                        }
                        if (data.Contains("LLVM ERROR: out of memory"))
                        {
                            _logger.LogTrace($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} Detected out-of-memory for clang (marking as retry needed)");
                            needsRetry = true;
                        }
                    }

                    int exitCode;
                    do
                    {
                        needsRetry = false;
                        exitCode = await executor.ExecuteTaskAsync(
                            virtualCore,
                            GetBuildStatusLogPrefix(0),
                            task,
                            env!,
                            tool!,
                            arguments,
                            _environmentVariables,
                            CheckForRetry,
                            CheckForRetry,
                            cancellationToken);
                        if (exitCode == 0)
                        {
                            break;
                        }
                        if (needsRetry)
                        {
                            _logger.LogInformation($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} {Bright.Yellow($"[automatically retrying]")}");
                        }
                    } while (needsRetry);

                    if (exitCode == 0)
                    {
                        task.Status = OpenGEStatus.Success;
                        if (!_turnOffExtraLogInfo)
                        {
                            _logger.LogInformation($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} {Bright.Green($"[done in {stopwatch.Elapsed.TotalSeconds:F2} secs]")}");
                        }
                        else
                        {
                            _logger.LogInformation($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} [{stopwatch.Elapsed.TotalSeconds:F2} secs]");
                        }
                    }
                    else
                    {
                        task.Status = OpenGEStatus.Failure;
                        project.Status = OpenGEStatus.Failure;
                        _logger.LogTrace($"Setting CancelledDueToFailure = true because {task.BuildSetTask.Caption} returned with exit code {exitCode}");
                        CancelledDueToFailure = true;
                        if (!_turnOffExtraLogInfo)
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} {Bright.Red("[build failed]")}");
                        }
                        else
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} [failed]");
                        }

                        // @note: Is this correct?
                        buildCancellationTokenSource.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    task.Status = OpenGEStatus.Failure;
                    project.Status = OpenGEStatus.Failure;
                    if (!(ex is OperationCanceledException))
                    {
                        _logger.LogTrace($"Setting CancelledDueToFailure = true because {task.BuildSetTask.Caption} got non-cancellation exception");
                        CancelledDueToFailure = true;
                        if (!_turnOffExtraLogInfo)
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} {Output.Background.Red(Black("[executor exception]"))}");
                        }
                        else
                        {
                            _logger.LogError($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} {Bright.Red("[executor failed]")}");
                        }
                        _logger.LogError(ex, ex.Message);
                    }

                    // @note: Is this correct?
                    buildCancellationTokenSource.Cancel();

                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                if (!CancelledDueToFailure)
                {
                    if (!_turnOffExtraLogInfo)
                    {
                        _logger.LogWarning($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} {Bright.Yellow($"[terminating due to cancellation]")}");
                    }
                    else
                    {
                        _logger.LogWarning($"{GetBuildStatusLogPrefix(-1)} {task.BuildSetTask.Caption} [terminating due to cancellation]");
                    }
                }
            }
            finally
            {
                var remainingTaskCount = Interlocked.Decrement(ref _remainingTasks);
                if (remainingTaskCount == 0)
                {
                    // This will cause WaitAsync to exit in the main loop
                    // once all tasks are finished as well.
                    _queuedTaskAvailableForProcessing.Release();
                }
                else
                {
                    // For everything that depends on us, check if it's dependencies are met and that it is
                    // still in the Pending status. If it is, move it to the Scheduled status and put it
                    // on the queue. We use the 'Pending' vs 'Scheduled' status to ensure we don't queue
                    // the same thing twice.
                    await _updatingTaskForScheduling.WaitAsync(cancellationToken);
                    try
                    {
                        foreach (var dependent in task.Dependents)
                        {
                            if (dependent.Status == OpenGEStatus.Pending &&
                                dependent.DependsOn.All(x => x.Status == OpenGEStatus.Failure || x.Status == OpenGEStatus.Success || x.Status == OpenGEStatus.Skipped))
                            {
                                // @todo: Shortcut when dependent is skipped or failed.

                                dependent.Status = OpenGEStatus.Scheduled;
                                _queuedTasksForProcessing.Enqueue(dependent);
                                _queuedTaskAvailableForProcessing.Release();
                            }
                        }
                    }
                    finally
                    {
                        _updatingTaskForScheduling.Release();
                    }
                }
            }
        }
    }
}
