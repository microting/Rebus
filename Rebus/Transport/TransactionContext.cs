﻿using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable SuggestBaseTypeForParameter
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable EmptyGeneralCatchClause

namespace Rebus.Transport;

class TransactionContext : ITransactionContext
{
    // Note: C# generates thread-safe add/remove. They use a compare-and-exchange loop.
    event Func<ITransactionContext, Task> _onCommitted;
    event Func<ITransactionContext, Task> _onAck;
    event Func<ITransactionContext, Task> _onNack;
    event Func<ITransactionContext, Task> _onRollback;
    event Action<ITransactionContext> _onDisposed;

    //bool _mustAbort;

    bool? _mustCommit;
    bool? _mustAck;

    bool _completed;
    bool _disposed;

    public ConcurrentDictionary<string, object> Items { get; } = new();

    public void OnCommit(Func<ITransactionContext, Task> commitAction)
    {
        if (_completed) ThrowCompletedException();
        _onCommitted += commitAction;
    }

    public void OnAck(Func<ITransactionContext, Task> ackAction)
    {
        if (_completed) ThrowCompletedException();
        _onAck += ackAction;
    }

    public void OnNack(Func<ITransactionContext, Task> nackAction)
    {
        if (_completed) ThrowCompletedException();
        _onNack += nackAction;
    }

    public void OnRollback(Func<ITransactionContext, Task> rollbackAction)
    {
        if (_completed) ThrowCompletedException();
        _onRollback += rollbackAction;
    }

    public void OnDisposed(Action<ITransactionContext> disposeAction)
    {
        if (_completed) ThrowCompletedException();
        _onDisposed += disposeAction;
    }

    public void SetResult(bool commit, bool ack)
    {
        _mustAck = ack;
        _mustCommit = commit;
    }

    public async Task Complete()
    {
        if (_mustCommit == null || _mustAck == null)
        {
            throw new InvalidOperationException(
                $"Tried to complete the transaction context, but {nameof(SetResult)} has not been invoked!");
        }

        try
        {
            if (_mustCommit == true)
            {
                var onCommitted = Interlocked.Exchange(ref _onCommitted, null);
                await InvokeAsync(onCommitted);
            }
            else
            {
                var onRollback = Interlocked.Exchange(ref _onRollback, null);
                await InvokeAsync(onRollback);
            }
        }
        catch
        {
            var onNack = Interlocked.Exchange(ref _onNack, null);
            try
            {
                await InvokeAsync(onNack);
            }
            catch { }

            throw;
        }

        if (_mustAck == true)
        {
            var onAck = Interlocked.Exchange(ref _onAck, null);
            await InvokeAsync(onAck);
        }
        else
        {
            var onNack = Interlocked.Exchange(ref _onNack, null);
            await InvokeAsync(onNack);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _onDisposed?.Invoke(this);

            //if (!_completed)
            //{
            //    RaiseAborted();
            //}
        }
        finally
        {
            _disposed = true;

            //if (!_cleanedUp)
            //{
            //    try
            //    {
            //        _onDisposed?.Invoke(this);
            //    }
            //    finally
            //    {
            //        _cleanedUp = true;
            //    }
            //}
        }
    }

    static void ThrowCompletedException([CallerMemberName] string actionName = null) => throw new InvalidOperationException($"Cannot add {actionName} action on a completed transaction context.");

    //void RaiseAborted()
    //{
    //    if (_aborted) return;
    //    _onRollback?.Invoke(this);
    //    _aborted = true;
    //}

    Task RaiseCommitted()
    {
        // RaiseCommitted() can be called multiple time.
        // So we atomically extract the current list of subscribers and reset the event to null (empty)
        var onCommitted = Interlocked.Exchange(ref _onCommitted, null);
        return InvokeAsync(onCommitted);
    }

    async Task RaiseCompleted(bool ack)
    {
        await InvokeAsync(ack ? _onAck : _onNack);

        _completed = true;
    }

    async Task InvokeAsync(Func<ITransactionContext, Task> actions)
    {
        if (actions == null) return;

        var delegates = actions.GetInvocationList();

        for (var index = 0; index < delegates.Length; index++)
        {
            // they're always of this type, so no need to check the type here
            var asyncTxContextCallback = (Func<ITransactionContext, Task>)delegates[index];

            await asyncTxContextCallback(this);
        }
    }
}