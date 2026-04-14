using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace ThreadPoolDemo;

internal static class Program
{
    private static int s_completedBuildNodes;
    private static int s_peakCompletedBuildNodesWaiting;

    public static async Task<int> Main(string[] args)
    {
        DemoOptions options;

        try
        {
            options = DemoOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            DemoOptions.PrintUsage();
            return 1;
        }

        PrintHeader(options);

        using var sampler = new MetricsSampler();
        var result = await RunScenarioAsync(options);

        PrintSummary(options, result, sampler.Metrics);
        return 0;
    }

    private static void PrintHeader(DemoOptions options)
    {
        Console.WriteLine($"Mode: {options.Mode}");
        Console.WriteLine($"Sign work mode: {options.WorkMode}");
        Console.WriteLine($"Sign nodes: {options.SignNodes:N0}");
        Console.WriteLine($"Files per sign node: {options.FilesPerSignNode:N0}");
        Console.WriteLine($"Build nodes: {options.BuildNodes:N0}");
        Console.WriteLine($"Sign work per file: {options.SignWorkMilliseconds} ms");
        Console.WriteLine($"Build completion range: {options.BuildMinDelayMilliseconds}-{options.BuildMaxDelayMilliseconds} ms");
        Console.WriteLine($"Build start offset: {options.BuildLaunchDelayMilliseconds} ms after sign launch");

        if (options.Mode is DemoMode.Pfea)
        {
            Console.WriteLine($"PFEA per-sign-node dop: {options.PfeaDegreeOfParallelism}");
            Console.WriteLine($"Theoretical sign concurrency cap: {options.SignNodes * options.PfeaDegreeOfParallelism:N0}");
        }
        else
        {
            Console.WriteLine($"Global semaphore limit: {options.GlobalSemaphoreLimit:N0}");
        }

        Console.WriteLine();
    }

    private static async Task<ScenarioResult> RunScenarioAsync(DemoOptions options)
    {
        s_completedBuildNodes = 0;
        s_peakCompletedBuildNodesWaiting = 0;

        var stopwatch = Stopwatch.StartNew();
        var buildContinuationDelays = new ConcurrentBag<double>();
        Task[] signTasks;
        SemaphoreSlim? globalSemaphore = null;

        if (options.Mode is DemoMode.Pfea)
        {
            signTasks = Enumerable.Range(0, options.SignNodes)
                .Select(index => Task.Run(() => RunSignNodeWithParallelForEachAsync(index, options)))
                .ToArray();
        }
        else
        {
            globalSemaphore = new SemaphoreSlim(options.GlobalSemaphoreLimit, options.GlobalSemaphoreLimit);
            signTasks = Enumerable.Range(0, options.SignNodes)
                .Select(index => Task.Run(() => RunSignNodeWithGlobalSemaphoreAsync(index, options, globalSemaphore)))
                .ToArray();
        }

        if (options.BuildLaunchDelayMilliseconds > 0)
        {
            Thread.Sleep(options.BuildLaunchDelayMilliseconds);
        }

        using var completionScheduler = new BuildCompletionScheduler(options);

        var buildTasks = Enumerable.Range(0, options.BuildNodes)
            .Select(index => RunBuildNodeAsync(index, options, completionScheduler, buildContinuationDelays))
            .ToArray();

        try
        {
            await Task.WhenAll(signTasks.Concat(buildTasks));
        }
        finally
        {
            globalSemaphore?.Dispose();
        }

        stopwatch.Stop();

        return new ScenarioResult(
            stopwatch.Elapsed,
            Volatile.Read(ref s_peakCompletedBuildNodesWaiting),
            BuildSummary.FromOvershoots(buildContinuationDelays));
    }

    private static Task RunSignNodeWithParallelForEachAsync(int signNodeIndex, DemoOptions options)
    {
        return Parallel.ForEachAsync(
            Enumerable.Range(0, options.FilesPerSignNode),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.PfeaDegreeOfParallelism,
            },
            (fileIndex, cancellationToken) =>
            {
                ExecuteSignWork(signNodeIndex, fileIndex, options, cancellationToken);
                return ValueTask.CompletedTask;
            });
    }

    private static async Task RunSignNodeWithGlobalSemaphoreAsync(
        int signNodeIndex,
        DemoOptions options,
        SemaphoreSlim globalSemaphore)
    {
        for (var fileIndex = 0; fileIndex < options.FilesPerSignNode; fileIndex++)
        {
            await globalSemaphore.WaitAsync();

            try
            {
                await Task.Run(
                    () => ExecuteSignWork(signNodeIndex, fileIndex, options, CancellationToken.None));
            }
            finally
            {
                globalSemaphore.Release();
            }
        }
    }

    private static async Task RunBuildNodeAsync(
        int buildNodeIndex,
        DemoOptions options,
        BuildCompletionScheduler completionScheduler,
        ConcurrentBag<double> buildContinuationDelays)
    {
        var completionDelayMilliseconds = GetBuildDelayMilliseconds(buildNodeIndex, options);
        var completionSource = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        completionScheduler.Schedule(completionDelayMilliseconds, completionSource);

        var completionTimestamp = await completionSource.Task;
        var continuationTimestamp = Stopwatch.GetTimestamp();
        var continuationDelayMilliseconds = Math.Max(
            0,
            StopwatchTicksToMilliseconds(continuationTimestamp - completionTimestamp));

        Interlocked.Decrement(ref s_completedBuildNodes);
        buildContinuationDelays.Add(continuationDelayMilliseconds);
    }

    private static int GetBuildDelayMilliseconds(int buildNodeIndex, DemoOptions options)
    {
        var range = options.BuildMaxDelayMilliseconds - options.BuildMinDelayMilliseconds;

        if (range <= 0)
        {
            return options.BuildMinDelayMilliseconds;
        }

        return options.BuildMinDelayMilliseconds + (buildNodeIndex % (range + 1));
    }

    private static void ExecuteSignWork(
        int signNodeIndex,
        int fileIndex,
        DemoOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options.WorkMode is SignWorkMode.Sleep)
        {
            Thread.Sleep(options.SignWorkMilliseconds);
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(options.SignWorkMilliseconds);
        var hash = (signNodeIndex + 1) * 397 ^ fileIndex;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            hash = unchecked((hash * 16777619) ^ Environment.ProcessorCount);
        }

        GC.KeepAlive(hash);
    }

    private static long MillisecondsToStopwatchTicks(int milliseconds)
    {
        return (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
    }

    private static double StopwatchTicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static void ObserveCompletedBuildNode()
    {
        var completed = Interlocked.Increment(ref s_completedBuildNodes);

        while (true)
        {
            var currentPeak = Volatile.Read(ref s_peakCompletedBuildNodesWaiting);

            if (completed <= currentPeak)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref s_peakCompletedBuildNodesWaiting, completed, currentPeak) == currentPeak)
            {
                return;
            }
        }
    }

    private static void PrintSummary(DemoOptions options, ScenarioResult result, RuntimeMetrics metrics)
    {
        Console.WriteLine("Summary");
        Console.WriteLine($"Elapsed: {result.Elapsed.TotalSeconds:F2} s");
        Console.WriteLine($"Peak process threads: {metrics.PeakProcessThreadCount:N0}");
        Console.WriteLine($"Peak ThreadPool threads: {metrics.PeakThreadPoolThreadCount:N0}");
        Console.WriteLine($"Peak pending work items: {metrics.PeakPendingWorkItems:N0}");
        Console.WriteLine($"Peak completed build nodes waiting for continuations: {result.PeakCompletedBuildNodesWaiting:N0}");
        Console.WriteLine($"Build continuation delay p50: {result.BuildSummary.P50Milliseconds:F2} ms");
        Console.WriteLine($"Build continuation delay p95: {result.BuildSummary.P95Milliseconds:F2} ms");
        Console.WriteLine($"Build continuation delay p99: {result.BuildSummary.P99Milliseconds:F2} ms");
        Console.WriteLine($"Build continuation delay max: {result.BuildSummary.MaxMilliseconds:F2} ms");
        Console.WriteLine();

        if (options.Mode is DemoMode.Pfea)
        {
            Console.WriteLine("Interpretation: larger continuation delay here means build nodes completed, but their continuations ran later because sign work had already flooded the scheduler.");
        }
        else
        {
            Console.WriteLine("Interpretation: lower continuation delay here means the shared semaphore kept much less sign work active at the same time.");
        }
    }

    private sealed record ScenarioResult(
        TimeSpan Elapsed,
        int PeakCompletedBuildNodesWaiting,
        BuildSummary BuildSummary);

    private sealed record BuildSummary(
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaxMilliseconds)
    {
        public static BuildSummary FromOvershoots(IEnumerable<double> overshoots)
        {
            var sorted = overshoots.OrderBy(value => value).ToArray();

            if (sorted.Length is 0)
            {
                return new BuildSummary(0, 0, 0, 0);
            }

            return new BuildSummary(
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99),
                sorted[^1]);
        }

        private static double Percentile(double[] sortedValues, double percentile)
        {
            var index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
            index = Math.Clamp(index, 0, sortedValues.Length - 1);
            return sortedValues[index];
        }
    }

    private sealed class BuildCompletionScheduler : IDisposable
    {
        private readonly AutoResetEvent _signal = new(false);
        private readonly CancellationTokenSource _cts = new();
        private readonly PriorityQueue<ScheduledCompletion, long> _queue = new();
        private readonly object _lock = new();
        private readonly Thread _thread;

        public BuildCompletionScheduler(DemoOptions options)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "build-completion-scheduler",
                Priority = ThreadPriority.Highest,
            };

            _thread.Start();
        }

        public void Schedule(int delayMilliseconds, TaskCompletionSource<long> completionSource)
        {
            var dueTimestamp = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(delayMilliseconds);

            lock (_lock)
            {
                _queue.Enqueue(new ScheduledCompletion(dueTimestamp, completionSource), dueTimestamp);
            }

            _signal.Set();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _signal.Set();
            _thread.Join();
            _signal.Dispose();
            _cts.Dispose();
        }

        private void Run()
        {
            while (!_cts.IsCancellationRequested)
            {
                ScheduledCompletion? next;

                lock (_lock)
                {
                    next = _queue.Count > 0 ? _queue.Peek() : null;
                }

                if (next is null)
                {
                    _signal.WaitOne(50);
                    continue;
                }

                var remainingTicks = next.DueTimestamp - Stopwatch.GetTimestamp();

                if (remainingTicks > 0)
                {
                    var remainingMilliseconds = Math.Max(1, (int)Math.Min(int.MaxValue, StopwatchTicksToMilliseconds(remainingTicks)));
                    _signal.WaitOne(remainingMilliseconds);
                    continue;
                }

                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        continue;
                    }

                    next = _queue.Dequeue();
                }

                var completionTimestamp = Stopwatch.GetTimestamp();
                ObserveCompletedBuildNode();
                next.CompletionSource.SetResult(completionTimestamp);
            }
        }

        private sealed record ScheduledCompletion(long DueTimestamp, TaskCompletionSource<long> CompletionSource);
    }

    private sealed class MetricsSampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        public MetricsSampler()
        {
            Metrics = new RuntimeMetrics();
            _thread = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "metrics-sampler",
            };

            _thread.Start();
        }

        public RuntimeMetrics Metrics { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join();
            _cts.Dispose();
        }

        private void SampleLoop()
        {
            using var process = Process.GetCurrentProcess();

            while (!_cts.IsCancellationRequested)
            {
                process.Refresh();

                Metrics.PeakProcessThreadCount = Math.Max(Metrics.PeakProcessThreadCount, process.Threads.Count);
                Metrics.PeakThreadPoolThreadCount = Math.Max(Metrics.PeakThreadPoolThreadCount, ThreadPool.ThreadCount);
                Metrics.PeakPendingWorkItems = Math.Max(Metrics.PeakPendingWorkItems, ThreadPool.PendingWorkItemCount);

                Thread.Sleep(50);
            }
        }
    }

    private sealed class RuntimeMetrics
    {
        public int PeakProcessThreadCount { get; set; }

        public int PeakThreadPoolThreadCount { get; set; }

        public long PeakPendingWorkItems { get; set; }
    }

    private sealed record DemoOptions(
        DemoMode Mode,
        SignWorkMode WorkMode,
        int SignNodes,
        int FilesPerSignNode,
        int BuildNodes,
        int SignWorkMilliseconds,
        int BuildMinDelayMilliseconds,
        int BuildMaxDelayMilliseconds,
        int PfeaDegreeOfParallelism,
        int GlobalSemaphoreLimit,
        int BuildLaunchDelayMilliseconds)
    {
        public static DemoOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unrecognized argument '{arg}'. Expected --name=value.");
                }

                var separatorIndex = arg.IndexOf('=');

                if (separatorIndex < 0)
                {
                    throw new ArgumentException($"Argument '{arg}' is missing '=value'.");
                }

                values[arg[2..separatorIndex]] = arg[(separatorIndex + 1)..];
            }

            var options = new DemoOptions(
                ParseEnum(values, "mode", DemoMode.Pfea),
                ParseEnum(values, "work-mode", SignWorkMode.Sleep),
                ParsePositiveInt(values, "sign-nodes", 192),
                ParsePositiveInt(values, "files-per-sign", 512),
                ParsePositiveInt(values, "build-nodes", 512),
                ParsePositiveInt(values, "sign-ms", 3),
                ParseNonNegativeInt(values, "build-min-ms", 50),
                ParseNonNegativeInt(values, "build-max-ms", 250),
                ParsePositiveInt(values, "pfea-dop", 8),
                ParsePositiveInt(values, "global-limit", 32),
                ParseNonNegativeInt(values, "build-launch-delay-ms", 100));

            if (options.BuildMaxDelayMilliseconds < options.BuildMinDelayMilliseconds)
            {
                throw new ArgumentException("--build-max-ms must be greater than or equal to --build-min-ms.");
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run -c Release --project ThreadPoolDemo -- --mode=pfea");
            Console.WriteLine("  dotnet run -c Release --project ThreadPoolDemo -- --mode=semaphore");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --mode=pfea|semaphore");
            Console.WriteLine("  --work-mode=sleep|spin");
            Console.WriteLine("  --sign-nodes=<int>");
            Console.WriteLine("  --files-per-sign=<int>");
            Console.WriteLine("  --build-nodes=<int>");
            Console.WriteLine("  --sign-ms=<int>");
            Console.WriteLine("  --build-min-ms=<int>");
            Console.WriteLine("  --build-max-ms=<int>");
            Console.WriteLine("  --pfea-dop=<int>");
            Console.WriteLine("  --global-limit=<int>");
            Console.WriteLine("  --build-launch-delay-ms=<int>");
        }

        private static int ParsePositiveInt(IDictionary<string, string> values, string name, int defaultValue)
        {
            if (!values.TryGetValue(name, out var rawValue))
            {
                return defaultValue;
            }

            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            throw new ArgumentException($"Invalid value '{rawValue}' for --{name}. Expected a positive integer.");
        }

        private static int ParseNonNegativeInt(IDictionary<string, string> values, string name, int defaultValue)
        {
            if (!values.TryGetValue(name, out var rawValue))
            {
                return defaultValue;
            }

            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            {
                return parsed;
            }

            throw new ArgumentException($"Invalid value '{rawValue}' for --{name}. Expected a non-negative integer.");
        }

        private static TEnum ParseEnum<TEnum>(IDictionary<string, string> values, string name, TEnum defaultValue)
            where TEnum : struct, Enum
        {
            if (!values.TryGetValue(name, out var rawValue))
            {
                return defaultValue;
            }

            if (Enum.TryParse<TEnum>(rawValue, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException($"Invalid value '{rawValue}' for --{name}.");
        }
    }

    private enum DemoMode
    {
        Pfea,
        Semaphore,
    }

    private enum SignWorkMode
    {
        Sleep,
        Spin,
    }
}
