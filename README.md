This is a demo of local vs global thread pool task queues and how they can impact task completion.

The only difference between these two demos is a one-liner calling `Task.Run()` vs `Task.Yield()`.

```
dotnet build ThreadPoolDemo -c Release
```

`--mode=localqueue`: queues Sign node tasks onto thread-local task queues via `Task.Run()`.
You'll see each Build and Analysis nodes complete as soon as they're ready.

```
dotnet build ThreadPoolDemo -c Release && dotnet ".\ThreadPoolDemo\bin\Release\net9.0\ThreadPoolDemo.dll" --mode=localqueue --build-nodes=150 --sign-nodes=150 --sign-ms=50 --files-per-sign=50 --build-ms=2000 --build-max-ms=1000
```

```
Build continuation delay p50: 18.00 ms
Build continuation delay p95: 53.00 ms
Build continuation delay p99: 60.00 ms
Build continuation delay max: 61.00 ms
```

`--mode=globalqueue`: queues Sign node tasks onto the global task queue via `Task.Yield()`.
You'll see progress at first until the global queue exceeds the completion rate, after which all tasks will appear to get stuck until the global queue is fully drained.

```
Run this and watch every task finish at the same time.
```
dotnet build ThreadPoolDemo -c Release && dotnet ".\ThreadPoolDemo\bin\Release\net9.0\ThreadPoolDemo.dll" --mode=globalqueue --build-nodes=150 --sign-nodes=150 --sign-ms=50 --files-per-sign=50 --build-ms=2000 --build-max-ms=1000
```

```
Build continuation delay p50: 9808.00 ms
Build continuation delay p95: 10673.00 ms
Build continuation delay p99: 11038.00 ms
Build continuation delay max: 11155.00 ms
```

