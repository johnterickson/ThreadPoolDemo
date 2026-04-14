# ThreadPool Demo

This repo contains a `net9.0` console app that demonstrates delayed `Task` continuations under heavy sign-node fan-out.

The demo models two kinds of build-engine nodes:

- `sign` nodes: many nodes start first, each one processes many file items.
- `build` nodes: simple completion tasks with no internal parallelism.

The comparison is between these two sign-node implementations:

- `pfea`: each sign node uses `Parallel.ForEachAsync(..., MaxDegreeOfParallelism = 8)`.
- `semaphore`: each sign node uses a single shared global `SemaphoreSlim` and `WaitAsync()` before doing the same work.

The sign work is intentionally synchronous and blocking by default via `Thread.Sleep(...)` so the runtime has a reason to create many worker threads, closer to the real-world behavior being modeled.

## Build

```powershell
dotnet build ThreadPoolDemo -c Release
```

## Run

Default `Parallel.ForEachAsync` scenario:

```powershell
dotnet run -c Release --project ThreadPoolDemo -- --mode=pfea
```

Counter-example with a shared semaphore:

```powershell
dotnet run -c Release --project ThreadPoolDemo -- --mode=semaphore
```

Useful larger run that tends to create a much bigger backlog:

```powershell
dotnet run -c Release --project ThreadPoolDemo -- --mode=pfea --sign-nodes=256 --files-per-sign=1024 --build-nodes=1024 --sign-ms=4 --build-launch-delay-ms=100 --pfea-dop=8
```

Matching semaphore run:

```powershell
dotnet run -c Release --project ThreadPoolDemo -- --mode=semaphore --sign-nodes=256 --files-per-sign=1024 --build-nodes=1024 --sign-ms=4 --build-launch-delay-ms=100 --global-limit=32
```

## Output

Each run prints:

- elapsed wall-clock time
- peak process thread count
- peak `ThreadPool.ThreadCount`
- peak `ThreadPool.PendingWorkItemCount`
- peak completed build nodes waiting for continuations
- build-node continuation delay percentiles (`p50`, `p95`, `p99`, `max`)

Build continuation delay is the main signal for this demo. A dedicated scheduler thread marks build nodes complete at their due time, then the demo measures how much later the `await` continuation actually runs on the thread pool. Higher delay means nodes completed but their continuations resumed late.

## Options

```text
--mode=pfea|semaphore
--work-mode=sleep|spin
--sign-nodes=<int>
--files-per-sign=<int>
--build-nodes=<int>
--sign-ms=<int>
--build-min-ms=<int>
--build-max-ms=<int>
--pfea-dop=<int>
--global-limit=<int>
--build-launch-delay-ms=<int>
```

Notes:

- startup order is `sign-first`
- build nodes are armed after the sign nodes have already been launched
- `sleep` mode is usually better for reproducing large worker-thread growth
- `spin` mode is available if you want CPU-bound work instead of blocking waits
