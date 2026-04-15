using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Runtime;

namespace ThreadPoolDemo;


static class SyncOverAsyncCop
{
    /// <summary>
    /// Known method name substrings that indicate a synchronous block.
    /// </summary>
    private static readonly string[] s_syncBlockPatterns =
    [
        "System.Threading.Monitor.Enter",
        "System.Threading.Monitor.ReliableEnter",
        "System.Threading.Monitor.ObjWait",
        "System.Threading.ManualResetEventSlim.Wait",
        "System.Threading.SemaphoreSlim.Wait",
        "System.Threading.Tasks.Task.Wait",
        "System.Threading.Tasks.Task.InnerWait",
        "System.Threading.Tasks.Task`1.GetResultCore",       // Task<T>.Result
        "System.Threading.Tasks.Task.InternalWaitCore",
        "System.Threading.Tasks.Task+SpinThenBlockingWait",
        "System.Threading.Thread.Sleep",
        "System.Threading.WaitHandle.WaitOne",
        "System.Threading.WaitHandle.WaitAny",
        "System.Threading.WaitHandle.WaitAll",
        "System.Threading.CountdownEvent.Wait",
    ];

    /// <summary>
    /// Known method name substrings that indicate an async state machine (the
    /// caller was in an async method).
    /// </summary>
    private static readonly string[] s_asyncPatterns =
    [
        "MoveNext()",                                         // async state machine entry
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncVoidMethodBuilder",
        "System.Threading.Tasks.Task.ContinueWith",
        "System.Threading.ExecutionContext.RunInternal",
    ];

    public sealed record SyncOverAsyncViolation(
        int ManagedThreadId,
        string BlockingMethod,
        string AsyncMethod,
        IReadOnlyList<string> FullStack);

    /// <summary>
    /// Attaches to the current process via ClrMD and walks every managed thread
    /// (except the calling thread) looking for stack frames where a sync-blocking
    /// call sits above an async state-machine MoveNext or continuation frame.
    /// </summary>
    public static IReadOnlyList<SyncOverAsyncViolation> Detect()
    {
        var violations = new List<SyncOverAsyncViolation>();
        int currentManagedThreadId = Environment.CurrentManagedThreadId;
        int pid = Environment.ProcessId;

        using DataTarget dataTarget = DataTarget.AttachToProcess(pid, suspend: false);
        using ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        foreach (ClrThread thread in runtime.Threads)
        {
            if (!thread.IsAlive || thread.ManagedThreadId == currentManagedThreadId)
                continue;

            var frames = new List<string>();
            foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                string? name = frame.Method?.Signature;
                if (name is not null)
                    frames.Add(name);
            }

            // Walk top-down: find a sync-blocking frame, then see if any frame
            // below it is an async frame.
            for (int i = 0; i < frames.Count; i++)
            {
                string? blockingMatch = MatchesAny(frames[i], s_syncBlockPatterns);
                if (blockingMatch is null)
                    continue;

                for (int j = i + 1; j < frames.Count; j++)
                {
                    string? asyncMatch = MatchesAny(frames[j], s_asyncPatterns);
                    if (asyncMatch is not null)
                    {
                        violations.Add(new SyncOverAsyncViolation(
                            thread.ManagedThreadId,
                            blockingMatch,
                            frames[j],
                            frames));
                        break; // one violation per blocking frame is enough
                    }
                }
            }
        }

        return violations;
    }

    public static void DetectAndPrint()
    {
        var violations = Detect();

        if (violations.Count == 0)
        {
            Console.WriteLine("[SyncOverAsyncCop] No sync-over-async violations detected.");
            return;
        }

        Console.WriteLine($"[SyncOverAsyncCop] {violations.Count} violation(s) detected:");
        Console.WriteLine();

        foreach (var v in violations)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Thread {v.ManagedThreadId}: {v.BlockingMethod}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    above async frame: {v.AsyncMethod}");
            Console.ResetColor();
            Console.WriteLine("    Stack:");
            foreach (var frame in v.FullStack)
            {
                Console.WriteLine($"      {frame}");
            }

            Console.WriteLine();
        }
    }

    private static string? MatchesAny(string frame, string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            if (frame.Contains(pattern, StringComparison.Ordinal))
                return pattern;
        }

        return null;
    }
}

internal static class Program
{
    private static int s_buildNodesRemaining;
    private static int s_analysisNodesRemaining;
    private static int s_peakCompletedBuildNodesWaiting;

    public static async Task<int> Main(string[] args)
    {
        new Thread(() => 
        {
            while(true)
            {
                SyncOverAsyncCop.DetectAndPrint();
                Thread.Sleep(1000);
            }
        }).Start();

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
        Console.WriteLine($"Sign nodes: {options.SignNodes:N0}");
        Console.WriteLine($"Files per sign node: {options.FilesPerSignNode:N0}");
        Console.WriteLine($"Build nodes: {options.BuildNodes:N0}");
        Console.WriteLine($"Sign work per file: {options.SignWorkMilliseconds} ms");
        Console.WriteLine($"Build completion range: {options.BuildWorkMilliseconds} ms");
        Console.WriteLine();
    }

    private static async Task<ScenarioResult> RunScenarioAsync(DemoOptions options)
    {
        s_buildNodesRemaining = options.BuildNodes;
        s_analysisNodesRemaining = options.BuildNodes / 2;
        s_peakCompletedBuildNodesWaiting = 0;

        var stopwatch = Stopwatch.StartNew();
        var buildContinuationDelays = new ConcurrentBag<double>();

        await Task.Run(async () => {
            Task[] buildTasks = new Task[options.BuildNodes];
            Task[] analysisTasks = new Task[options.BuildNodes / 2];
            Task[] signTasks = new Task[options.SignNodes];
            int offset = 0;
            for (int i = 0; i < options.BuildNodes; i++)
            {
                buildTasks[i] = RunBuildNodeAsync(i, options, buildContinuationDelays, offset);
                offset += 10;
            }

            for (int i = 0; i < options.BuildNodes / 2; i++)
            {
                analysisTasks[i] = RunAnalysisNodeAsync(i, options, buildContinuationDelays, offset);
                offset += 30;
            }

            for (int i = 0; i < options.SignNodes; i++)
            {
                signTasks[i] = RunSignNode(i, options);
            }

            await Task.WhenAll(signTasks.Concat(buildTasks));
        });


        stopwatch.Stop();

        return new ScenarioResult(
            stopwatch.Elapsed,
            Volatile.Read(ref s_peakCompletedBuildNodesWaiting),
            BuildSummary.FromOvershoots(buildContinuationDelays));
    }

    static async Task RunSignNode(int signNodeIndex, DemoOptions options)
    {
        Task[] tasks = new Task[options.FilesPerSignNode];
        for (int i = 0; i < options.FilesPerSignNode; i++)
        {
            tasks[i] = ExecuteSignWorkAsync(options);
        }

        await Task.WhenAll(tasks);
        Console.WriteLine($"\x1b[34mSign node {signNodeIndex} end\x1b[0m");
    }

    static async Task ExecuteSignWorkAsync(DemoOptions options)
    {
        if (options.Mode == DemoMode.GlobalQueue)
        {
            await Task.Yield();
            Thread.Sleep(options.SignWorkMilliseconds);
        }
        else
        {
            await Task.Run(() => { Thread.Sleep(options.SignWorkMilliseconds); });
        }
    }

    private static async Task RunBuildNodeAsync(
        int buildNodeIndex,
        DemoOptions options,
        ConcurrentBag<double> buildContinuationDelays,
        int offset)
    {
        // Console.WriteLine($"Build node {buildNodeIndex} start");
        Stopwatch sw = Stopwatch.StartNew();

        await Task.Delay(options.BuildWorkMilliseconds + offset);
        Console.WriteLine($"\x1b[32mBuild node {buildNodeIndex} end\x1b[0m");

        if (Interlocked.Decrement(ref s_buildNodesRemaining) == 0)
        {
            Console.WriteLine($"\x1b[32m------------------------------------BUILD DONE------------------------------------\x1b[0m");
        }

        buildContinuationDelays.Add(sw.ElapsedMilliseconds - (options.BuildWorkMilliseconds + offset));
    }

    private static async Task RunAnalysisNodeAsync(
        int buildNodeIndex,
        DemoOptions options,
        ConcurrentBag<double> buildContinuationDelays,
        int offset)
    {
        // Console.WriteLine($"Build node {buildNodeIndex} start");
        Stopwatch sw = Stopwatch.StartNew();

        await Task.Delay(0 + offset);
        Console.WriteLine($"\x1b[33mAnalysis node {buildNodeIndex} end\x1b[0m");

        if (Interlocked.Decrement(ref s_analysisNodesRemaining) == 0)
        {
            Console.WriteLine($"\x1b[33m------------------------------------ANALYSIS DONE------------------------------------\x1b[0m");
        }

        buildContinuationDelays.Add(sw.ElapsedMilliseconds - (0 + offset));
    }

    private static void PrintSummary(DemoOptions options, ScenarioResult result, RuntimeMetrics metrics)
    {
        Console.WriteLine("Summary");
        Console.WriteLine($"Elapsed: {result.Elapsed.TotalSeconds:F2} s");
        Console.WriteLine($"Peak process threads: {metrics.PeakProcessThreadCount:N0}");
        Console.WriteLine($"Peak ThreadPool threads: {metrics.PeakThreadPoolThreadCount:N0}");
        Console.WriteLine($"Build continuation delay p50: {result.BuildSummary.P50Milliseconds:F2} ms");
        Console.WriteLine($"Build continuation delay p95: {result.BuildSummary.P95Milliseconds:F2} ms");
        Console.WriteLine($"Build continuation delay p99: {result.BuildSummary.P99Milliseconds:F2} ms");
        Console.WriteLine($"Build continuation delay max: {result.BuildSummary.MaxMilliseconds:F2} ms");
        Console.WriteLine();
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
        int SignNodes,
        int FilesPerSignNode,
        int BuildNodes,
        int SignWorkMilliseconds,
        int BuildWorkMilliseconds)
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
                ParseEnum(values, "mode", DemoMode.GlobalQueue),
                ParsePositiveInt(values, "sign-nodes", 192),
                ParsePositiveInt(values, "files-per-sign", 512),
                ParsePositiveInt(values, "build-nodes", 512),
                ParsePositiveInt(values, "sign-ms", 3),
                ParseNonNegativeInt(values, "build-ms", 50));

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run -c Release --project ThreadPoolDemo -- --mode=localqueue");
            Console.WriteLine("  dotnet run -c Release --project ThreadPoolDemo -- --mode=globalqueue");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --mode=localqueue|globalqueue");
            Console.WriteLine("  --sign-nodes=<int>");
            Console.WriteLine("  --files-per-sign=<int>");
            Console.WriteLine("  --build-nodes=<int>");
            Console.WriteLine("  --sign-ms=<int>");
            Console.WriteLine("  --build-ms=<int>");
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
        GlobalQueue,
        LocalQueue,
    }
}
