// TODO reimplement without the usage of AutoFixture, it's a bit overkill for this test
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Exceptions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Retry;
using Rebus.Retry.FailFast;
using Rebus.Retry.Simple;
using Rebus.Transport;

namespace Rebus.Tests.Retry.Simple;

[TestFixture]
public class TestDefaultRetryStep
{
    class FakeExceptionInfoFactory : IExceptionInfoFactory
    {
        public List<Exception> CapturedExceptions { get; } = new();
        public Exception LastCapturedException => CapturedExceptions.LastOrDefault();

        public ExceptionInfo CreateInfo(Exception exception)
        {
            CapturedExceptions.Add(exception);
            return new ExceptionInfo(
                exception.GetType().FullName,
                exception.Message,
                exception.StackTrace,
                DateTimeOffset.UtcNow
            );
        }
    }

    class FakeFailFastChecker : IFailFastChecker
    {
        private readonly Func<string, Exception, bool> _shouldFailFast;

        public FakeFailFastChecker(Func<string, Exception, bool> shouldFailFast)
        {
            _shouldFailFast = shouldFailFast;
        }

        public bool ShouldFailFast(string messageId, Exception exception) => _shouldFailFast(messageId, exception);
    }

    class FakeErrorTracker : IErrorTracker
    {
        private readonly Func<string, Task<bool>> _hasFailedTooManyTimes;

        public Action<string, Exception> RegisterError { get; set; } = (_, _) => { };
        public Func<string, Task<IEnumerable<ExceptionInfo>>> GetExceptions { get; set; } = _ =>
        {
            Console.WriteLine("FakeErrorTracker.GetExceptions called");
            return Task.FromResult(Enumerable.Empty<ExceptionInfo>());
        };

        public FakeErrorTracker(Func<string, Task<bool>> hasFailedTooManyTimes)
        {
            _hasFailedTooManyTimes = hasFailedTooManyTimes;
        }

        public Task<bool> HasFailedTooManyTimes(string messageId) => _hasFailedTooManyTimes(messageId);

        public Task<string> GetFullErrorDescription(string messageId)
        {
            Console.WriteLine($"GetFullErrorDescription called with messageId: {messageId}");
            return Task.FromResult("Fake full error description");
        }

        Task<IReadOnlyList<ExceptionInfo>> IErrorTracker.GetExceptions(string messageId)
        {
            Console.WriteLine($"IErrorTracker.GetExceptions called with messageId: {messageId}");
            return Task.FromResult<IReadOnlyList<ExceptionInfo>>(new List<ExceptionInfo>());
        }

        Task IErrorTracker.RegisterError(string messageId, Exception exception)
        {
            RegisterError(messageId, exception);
            return Task.CompletedTask;
        }

        public Task<string> GetErrorDescription(string messageId) => Task.FromResult("Fake error description");

        public Task MarkAsFinal(string messageId)
        {
            Console.WriteLine($"MarkAsFinal called for messageId: {messageId}");
            return Task.CompletedTask;
        }

        public Task CleanUp(string messageId)
        {
            Console.WriteLine($"CleanUp called for messageId: {messageId}");
            return Task.CompletedTask;
        }

        public void RegisterDelivery(string messageId)
        {
            Console.WriteLine($"RegisterDelivery called for messageId: {messageId}");
        }
    }

    class FakeErrorHandler : IErrorHandler
    {
        public TransportMessage CapturedMessage { get; private set; }
        public Exception CapturedException { get; private set; }

        public Task HandlePoisonMessage(TransportMessage transportMessage, string errorDescription, Exception exception)
        {
            CapturedMessage = transportMessage;
            CapturedException = exception;
            Console.WriteLine($"HandlePoisonMessage (legacy) called with error: {exception.Message}");
            return Task.CompletedTask;
        }

        public Task HandlePoisonMessage(TransportMessage transportMessage, ITransactionContext transactionContext, ExceptionInfo exception)
        {
            CapturedMessage = transportMessage;
            CapturedException = new Exception(exception.Message);
            Console.WriteLine($"HandlePoisonMessage called with ExceptionInfo: {exception.Message}");
            return Task.CompletedTask;
        }
    }

    class FakeLoggerFactory : IRebusLoggerFactory
    {
        public ILog GetLogger(Type type) => new FakeLog();

        public ILog GetLogger<T>()
        {
            Console.WriteLine($"GetLogger<{typeof(T).Name}> called");
            return new FakeLog();
        }
    }

    class FakeLog : ILog
    {
        public void Debug(string message, params object[] objs) => Console.WriteLine("[DEBUG] " + message, objs);
        public void Info(string message, params object[] objs) => Console.WriteLine("[INFO] " + message, objs);
        public void Warn(string message, params object[] objs) => Console.WriteLine("[WARN] " + message, objs);
        public void Warn(Exception exception, string message, params object[] objs) => Console.WriteLine("[WARN] " + message + " Exception: " + exception.Message, objs);
        public void Error(string message, params object[] objs) => Console.WriteLine("[ERROR] " + message, objs);
        public void Error(Exception exception, string message, params object[] objs) => Console.WriteLine("[ERROR] " + message + " Exception: " + exception.Message, objs);
    }

    [Test]
    public async Task CreatesInfoForEmptyMessageException()
    {
        var exceptionInfoFactory = new FakeExceptionInfoFactory();
        var errorHandler = new FakeErrorHandler();
        var transactionContext = new FakeTransactionContext();

        var retryStep = new DefaultRetryStep(
            new FakeLoggerFactory(),
            errorHandler,
            new FakeErrorTracker(_ => Task.FromResult(false)),
            new FakeFailFastChecker((_, _) => false),
            exceptionInfoFactory,
            new RetryStrategySettings(),
            CancellationToken.None
        );

        var transportMessage = new TransportMessage(new Dictionary<string, string>(), new byte[0]);
        var context = new IncomingStepContext(transportMessage, transactionContext);
        context.Save(new OriginalTransportMessage(transportMessage));

        await retryStep.Process(context, () => Task.CompletedTask);

        Assert.That(exceptionInfoFactory.LastCapturedException.Message, Does.Contain("empty"));
        Assert.That(transactionContext.Ack, Is.True);
        Assert.That(transactionContext.Commit, Is.False);
    }

    [Test]
    public async Task CreatesInfoForStepExceptionWhenShouldFailFast()
    {
        var messageId = "fail-fast-id";
        var thrownException = new Exception("Something went wrong");

        var exceptionInfoFactory = new FakeExceptionInfoFactory();
        var errorHandler = new FakeErrorHandler();
        var transactionContext = new FakeTransactionContext();
        var failFastChecker = new FakeFailFastChecker((id, ex) => id == messageId && ex == thrownException);
        var errorTracker = new FakeErrorTracker(_ => Task.FromResult(false));

        var retryStep = new DefaultRetryStep(
            new FakeLoggerFactory(),
            errorHandler,
            errorTracker,
            failFastChecker,
            exceptionInfoFactory,
            new RetryStrategySettings(),
            CancellationToken.None
        );

        var transportMessage = new TransportMessage(new Dictionary<string, string>
        {
            [Headers.MessageId] = messageId
        }, new byte[0]);

        var context = new IncomingStepContext(transportMessage, transactionContext);
        context.Save(new OriginalTransportMessage(transportMessage));

        await retryStep.Process(context, () => throw thrownException);

        Assert.That(exceptionInfoFactory.LastCapturedException.Message, Does.Contain("Something went wrong"));
        Assert.That(transactionContext.Ack, Is.True);
        Assert.That(transactionContext.Commit, Is.False);
    }

    [Test]
    public async Task CreatesInfoForSecondLevelRetryException()
    {
        var messageId = "second-level-retry-id";
        var firstException = new Exception("Initial fail");
        var secondException = new Exception("Second-level fail");

        var exceptionInfoFactory = new FakeExceptionInfoFactory();
        var errorHandler = new FakeErrorHandler();
        var transactionContext = new FakeTransactionContext();

        var failFastChecker = new FakeFailFastChecker((_, _) => false);
        var errorTracker = new FakeErrorTracker(async id =>
        {
            await Task.Yield();
            return true;
        });

        var exceptions = new List<Exception>();
        errorTracker.RegisterError = (id, ex) => exceptions.Add(ex);
        errorTracker.GetExceptions = _ => Task.FromResult(exceptions.Select(e => exceptionInfoFactory.CreateInfo(e)));

        var retryStep = new DefaultRetryStep(
            new FakeLoggerFactory(),
            errorHandler,
            errorTracker,
            failFastChecker,
            exceptionInfoFactory,
            new RetryStrategySettings(maxDeliveryAttempts: 1, secondLevelRetriesEnabled: true),
            CancellationToken.None
        );

        var transportMessage = new TransportMessage(new Dictionary<string, string>
        {
            [Headers.MessageId] = messageId
        }, new byte[0]);

        var context = new IncomingStepContext(transportMessage, transactionContext);
        context.Save(new OriginalTransportMessage(transportMessage));

        var callCount = 0;

        await retryStep.Process(context, () =>
        {
            if (callCount++ == 0) throw firstException;
            throw secondException;
        });

        Assert.That(exceptionInfoFactory.LastCapturedException.Message, Does.Contain("Second-level fail"));
        Assert.That(errorHandler.CapturedException.Message, Is.EqualTo("1 unhandled exceptions"));
        Assert.That(transactionContext.Ack, Is.True);
        Assert.That(transactionContext.Commit, Is.False);
    }
}

class FakeTransactionContext : ITransactionContext
{
    private ConcurrentDictionary<string, object> _items = new();

    public bool WasCommitted { get; private set; }
    public bool WasRolledBack { get; private set; }
    public bool Commit { get; private set; }
    public bool Ack { get; private set; }

    public Dictionary<string, object> Items { get; } = new();

    public void Dispose() { }

    public ConcurrentDictionary<string, object> ItemsConcurrent => _items;

    public void SetResult(bool commit, bool ack)
    {
        Commit = commit;
        Ack = ack;
    }

    public void OnCommitted(Func<Task> callback) { }
    public void OnDisposed(Func<Task> callback) { }
    public void OnCompleted(Func<Task> callback) { }
    public void OnAborted(Func<Task> callback) { }

    public Task CommitAsync()
    {
        WasCommitted = true;
        return Task.CompletedTask;
    }

    public Task AbortAsync()
    {
        WasRolledBack = true;
        return Task.CompletedTask;
    }

    public void Enlist(Func<ITransactionContext, Task> callback) { }

    public void OnCommit(Func<ITransactionContext, Task> commitAction)
    {
        Console.WriteLine("OnCommit called");
    }

    public void OnRollback(Func<ITransactionContext, Task> abortedAction)
    {
        Console.WriteLine("OnRollback called");
    }

    public void OnAck(Func<ITransactionContext, Task> completedAction)
    {
        Console.WriteLine("OnAck called");
    }

    public void OnNack(Func<ITransactionContext, Task> commitAction)
    {
        Console.WriteLine("OnNack called");
    }

    public void OnDisposed(Action<ITransactionContext> disposedAction)
    {
        Console.WriteLine("OnDisposed called");
    }

    ConcurrentDictionary<string, object> ITransactionContext.Items => _items;
}
