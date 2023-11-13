// Licensed to the .NET Core Community under one or more agreements.
// The .NET Core Community licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Mocha.Core.Buffer.Memory;

internal sealed class MemoryBufferConsumer<T> : IBufferConsumer<T>
{
    private readonly BufferConsumerOptions _options;
    private readonly MemoryBufferQueue<T> _queue;
    private volatile MemoryBufferPartition<T>[] _assignedPartitions;

    private int _partitionIndex;
    private MemoryBufferPartition<T>? _partitionBeingConsumed;
    private readonly ManualResetEventSlim _readyToConsumeEvent;

    private volatile int _dataAvailableVersion;
    private volatile TaskCompletionSource<MemoryBufferPartition<T>>? _dataAvailableTaskCompletionSource;
    private readonly SemaphoreSlim _dataAvailableSemaphore;

    public MemoryBufferConsumer(BufferConsumerOptions options, MemoryBufferQueue<T> queue)
    {
        _options = options;
        _queue = queue;

        _assignedPartitions = Array.Empty<MemoryBufferPartition<T>>();
        _readyToConsumeEvent = new ManualResetEventSlim(true);

        _dataAvailableVersion = 0;
        _dataAvailableSemaphore = new SemaphoreSlim(1, 1);
    }

    public string GroupName => _options.GroupName;

    public MemoryBufferPartition<T>? PartitionBeingConsumed => _partitionBeingConsumed;

    public bool IsConsuming => _partitionBeingConsumed != null;

    public void AssignPartitions(params MemoryBufferPartition<T>[] partitions)
    {
        _assignedPartitions = partitions;
        foreach (var partition in partitions)
        {
            partition.RegisterConsumer(this);
        }
    }

    public void Pause() => _readyToConsumeEvent.Reset();

    public void Resume() => _readyToConsumeEvent.Set();

    public async IAsyncEnumerable<T> ConsumeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_assignedPartitions.Length == 0)
        {
            throw new InvalidOperationException("No partition is assigned.");
        }

        while (true)
        {
            var dataAvailableVersion = _dataAvailableVersion;

            var partition = SelectPartition();

            if (TryPull(partition, out var item))
            {
                yield return item;
                continue;
            }

            // Try to pull from other partitions
            T itemFromOtherPartition = default!;
            var hasItemFromOtherPartition = false;
            
            foreach (var t in _assignedPartitions)
            {
                partition = t;

                if (partition == _partitionBeingConsumed)
                {
                    continue;
                }

                if (TryPull(partition, out item))
                {
                    itemFromOtherPartition = item;
                    hasItemFromOtherPartition = true;
                    break;
                }
            }

            if (hasItemFromOtherPartition)
            {
                yield return itemFromOtherPartition;
                continue;
            }

            try
            {
                await _dataAvailableSemaphore.WaitAsync(cancellationToken);

                if (_dataAvailableVersion != dataAvailableVersion)
                {
                    continue;
                }

                _dataAvailableTaskCompletionSource =
                    new TaskCompletionSource<MemoryBufferPartition<T>>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            }
            finally
            {
                _dataAvailableSemaphore.Release();
            }

            var partitionWithNewData = await _dataAvailableTaskCompletionSource.Task;

            if (TryPull(partitionWithNewData, out item))
            {
                yield return item;
            }
        }
    }

    public ValueTask CommitAsync()
    {
        if (_options.AutoCommit)
        {
            throw new InvalidOperationException("Auto commit is enabled.");
        }

        var partition = _partitionBeingConsumed ??
                        throw new InvalidOperationException("No partition is in consumption.");

        _readyToConsumeEvent.Wait();

        partition.Commit(_options.GroupName);

        _partitionBeingConsumed = null;

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        _queue.RemoveConsumer(this);
        _readyToConsumeEvent.Dispose();
        return ValueTask.CompletedTask;
    }

    public void NotifyNewDataAvailable(MemoryBufferPartition<T> partition)
    {
        Interlocked.Increment(ref _dataAvailableVersion);

        if (_dataAvailableTaskCompletionSource == null)
        {
            return;
        }

        if (_dataAvailableTaskCompletionSource.Task.IsCompleted)
        {
            return;
        }

        _dataAvailableSemaphore.Wait();
        var tsc = _dataAvailableTaskCompletionSource;
        try
        {
            if (tsc == null)
            {
                return;
            }

            _dataAvailableTaskCompletionSource = null;
        }
        finally
        {
            _dataAvailableSemaphore.Release();
        }

        tsc.SetResult(partition);
    }

    private bool TryPull(MemoryBufferPartition<T> partition, out T item)
    {
        _partitionBeingConsumed = partition;
        var dataAvailable = partition.TryPull(_options.GroupName, out item);

        if (dataAvailable)
        {
            AutoCommitIfEnabled(partition);
        }
        return dataAvailable;
    }

    private void AutoCommitIfEnabled(MemoryBufferPartition<T> partition)
    {
        if (_options.AutoCommit)
        {
            _readyToConsumeEvent.Wait();
            partition.Commit(_options.GroupName);
        }
    }

    private MemoryBufferPartition<T> SelectPartition()
    {
        var partitions = _assignedPartitions;

        if (partitions.Length == 0)
        {
            throw new InvalidOperationException("No partition is assigned.");
        }

        var index = _partitionIndex++ % partitions.Length;
        return partitions[index];
    }
}
