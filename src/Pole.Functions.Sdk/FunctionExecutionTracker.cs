namespace Pole.Functions.Sdk;

internal sealed class FunctionExecutionTracker
{
    private readonly Lock gate = new();
    private TaskCompletionSource? drained;
    private bool accepting = true;
    private int active;

    public bool TryBegin(out FunctionExecutionRegistration registration)
    {
        lock (gate)
        {
            if (!accepting)
            {
                registration = null!;
                return false;
            }

            active++;
            registration = new FunctionExecutionRegistration(this);
            return true;
        }
    }

    public Task BeginDrain()
    {
        lock (gate)
        {
            accepting = false;
            if (active == 0)
                return Task.CompletedTask;

            drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return drained.Task;
        }
    }

    internal void Complete()
    {
        TaskCompletionSource? completion = null;
        lock (gate)
        {
            active--;
            if (!accepting && active == 0)
                completion = drained;
        }

        completion?.TrySetResult();
    }
}

internal sealed class FunctionExecutionRegistration(FunctionExecutionTracker tracker) : IDisposable
{
    private int completed;

    public void Attach(Task execution)
    {
        _ = execution.ContinueWith(
            (_, state) => ((FunctionExecutionRegistration)state!).Dispose(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            tracker.Complete();
    }
}
