using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PierCam.Sky;

/// <summary>
/// A task scheduler with a fixed number of below-normal-priority threads. Calibration runs its
/// parallel search on this rather than the thread pool, whose threads run at normal priority:
/// then however much work the search has, the capture loop and the encoder always win the CPU.
/// </summary>
internal sealed class LowPriorityScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _queue = new();
    private readonly List<Thread> _threads = new();

    public LowPriorityScheduler(int threads)
    {
        for (var i = 0; i < Math.Max(1, threads); i++)
        {
            var t = new Thread(() =>
            {
                foreach (var task in _queue.GetConsumingEnumerable()) TryExecuteTask(task);
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"PierCam calibration {i}" };
            t.Start();
            _threads.Add(t);
        }
    }

    public override int MaximumConcurrencyLevel => _threads.Count;

    protected override void QueueTask(Task task) => _queue.Add(task);

    // Inline only on our own threads, so no normal-priority thread ends up doing the work.
    protected override bool TryExecuteTaskInline(Task task, bool previouslyQueued) =>
        Thread.CurrentThread.Priority == ThreadPriority.BelowNormal && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    public void Dispose() => _queue.CompleteAdding();
}
