using MFAAvalonia.ViewModels.UsersControls.Settings;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("condition was not reached");
        await Task.Delay(10);
    }
}

static async Task DebouncesToLatestChangeAsync()
{
    var calls = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(50));

    action.Schedule();
    await Task.Delay(10);
    action.Schedule();
    await Task.Delay(10);
    action.Schedule();
    await WaitUntilAsync(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(1));
    await Task.Delay(80);
    Assert(calls == 1, $"debounce expected one save, got {calls}");
}

static async Task SerializesChangesArrivingDuringSaveAsync()
{
    var calls = 0;
    var active = 0;
    var maximumActive = 0;
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var action = new DebouncedAsyncAction(
        async () =>
        {
            var currentCall = Interlocked.Increment(ref calls);
            var nowActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, nowActive);
            if (currentCall == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            Interlocked.Decrement(ref active);
        },
        TimeSpan.FromMilliseconds(20));

    action.Schedule();
    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    action.Schedule();
    await Task.Delay(50);
    releaseFirst.SetResult();
    await WaitUntilAsync(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(1));
    Assert(maximumActive == 1, $"saves overlapped: max active {maximumActive}");
}

static async Task RetriesOnceAsync()
{
    var attempts = 0;
    var failures = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("transient");
            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(10),
        _ =>
        {
            Interlocked.Increment(ref failures);
            return Task.CompletedTask;
        });

    action.Schedule();
    await WaitUntilAsync(() => Volatile.Read(ref attempts) == 2, TimeSpan.FromSeconds(1));
    Assert(failures == 0, "successful retry reported a terminal failure");
}

static async Task ReportsTerminalFailureAfterRetryAsync()
{
    var attempts = 0;
    var failures = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("persistent");
        },
        TimeSpan.FromMilliseconds(10),
        _ =>
        {
            Interlocked.Increment(ref failures);
            return Task.CompletedTask;
        });

    action.Schedule();
    await WaitUntilAsync(() => Volatile.Read(ref failures) == 1, TimeSpan.FromSeconds(1));
    Assert(attempts == 2, $"expected two attempts, got {attempts}");
}

static async Task CancelPreventsPendingSaveAsync()
{
    var calls = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(100));
    action.Schedule();
    action.Cancel();
    await Task.Delay(180);
    Assert(calls == 0, "cancelled save still ran");
}

await DebouncesToLatestChangeAsync();
await SerializesChangesArrivingDuringSaveAsync();
await RetriesOnceAsync();
await ReportsTerminalFailureAfterRetryAsync();
await CancelPreventsPendingSaveAsync();
Console.WriteLine("MFA auto-save tests passed: 5");
