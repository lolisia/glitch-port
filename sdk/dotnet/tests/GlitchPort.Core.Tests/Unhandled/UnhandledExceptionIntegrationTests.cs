using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GlitchPort.Tests.Scopes;
using Xunit;

namespace GlitchPort.Tests.Unhandled
{
    public sealed class UnhandledExceptionIntegrationTests
    {
        [Fact]
        public void Register_SubscribesToBothProcessWideEventsAndDisposeUnsubscribes()
        {
            using var client = CapturingClient.Create(out _);
            var unhandledBefore = CountUnhandledExceptionHandlers();
            var unobservedBefore = CountUnobservedTaskExceptionHandlers();

            var registration = UnhandledExceptionIntegration.Register(client);

            try
            {
                Assert.Equal(unhandledBefore + 1, CountUnhandledExceptionHandlers());
                Assert.Equal(unobservedBefore + 1, CountUnobservedTaskExceptionHandlers());
            }
            finally
            {
                registration.Dispose();
            }

            Assert.Equal(unhandledBefore, CountUnhandledExceptionHandlers());
            Assert.Equal(unobservedBefore, CountUnobservedTaskExceptionHandlers());

            // Disposing twice is safe and leaves the counts unchanged.
            registration.Dispose();
            Assert.Equal(unhandledBefore, CountUnhandledExceptionHandlers());
            Assert.Equal(unobservedBefore, CountUnobservedTaskExceptionHandlers());
        }

        [Fact]
        public void Client_WithCaptureUnhandledExceptions_RegistersInTheConstructorAndDisposes()
        {
            var unhandledBefore = CountUnhandledExceptionHandlers();
            var unobservedBefore = CountUnobservedTaskExceptionHandlers();

            var client = CapturingClient.Create(
                out _,
                options => options.CaptureUnhandledExceptions = true);

            try
            {
                Assert.Equal(unhandledBefore + 1, CountUnhandledExceptionHandlers());
                Assert.Equal(unobservedBefore + 1, CountUnobservedTaskExceptionHandlers());
            }
            finally
            {
                client.Dispose();
            }

            Assert.Equal(unhandledBefore, CountUnhandledExceptionHandlers());
            Assert.Equal(unobservedBefore, CountUnobservedTaskExceptionHandlers());
        }

        [Fact]
        public void Client_WithoutCaptureUnhandledExceptions_DoesNotRegister()
        {
            var unhandledBefore = CountUnhandledExceptionHandlers();
            var unobservedBefore = CountUnobservedTaskExceptionHandlers();

            using var client = CapturingClient.Create(out _);

            Assert.Equal(unhandledBefore, CountUnhandledExceptionHandlers());
            Assert.Equal(unobservedBefore, CountUnobservedTaskExceptionHandlers());
        }

        [Fact]
        public void UnhandledExceptionHandler_CapturesTheExceptionAsFatal()
        {
            using var client = CapturingClient.Create(out var relay);
            using var registration = UnhandledExceptionIntegration.Register(client);

            var handler = GetHandler<UnhandledExceptionEventHandler>(registration);
            var exception = new InvalidOperationException("boom");
            handler(new object(), new UnhandledExceptionEventArgs(exception, isTerminating: true));

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal(Level.Fatal, captured.Level);
            Assert.NotNull(captured.Exception);
            Assert.Equal("System.InvalidOperationException", captured.Exception!.Type);
            Assert.Equal("boom", captured.Exception.Value);
        }

        [Fact]
        public void UnobservedTaskExceptionHandler_CapturesTheExceptionAsError()
        {
            using var client = CapturingClient.Create(out var relay);
            using var registration = UnhandledExceptionIntegration.Register(client);

            var handler = GetHandler<EventHandler<UnobservedTaskExceptionEventArgs>>(registration);
            handler(
                null,
                new UnobservedTaskExceptionEventArgs(
                    new AggregateException(new InvalidOperationException("task boom"))));

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal(Level.Error, captured.Level);
            Assert.NotNull(captured.Exception);
            Assert.Equal("System.InvalidOperationException", captured.Exception!.Type);
        }

        [Fact]
        public void Registration_WhenTheClientIsDisposed_DoesNotThrowWhenEventsAreRaised()
        {
            var client = CapturingClient.Create(out _);
            var registration = UnhandledExceptionIntegration.Register(client);
            var handler = GetHandler<UnhandledExceptionEventHandler>(registration);

            client.Dispose();
            registration.Dispose();

            // The registration unsubscribed during Dispose; invoking the captured
            // delegate directly still must not throw.
            handler(new object(), new UnhandledExceptionEventArgs(new InvalidOperationException("after dispose"), true));
        }

        private static TDelegate GetHandler<TDelegate>(IDisposable registration)
            where TDelegate : Delegate
        {
            var field = registration.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .SingleOrDefault(candidate => candidate.FieldType == typeof(TDelegate));

            Assert.NotNull(field);
            return (TDelegate)field!.GetValue(registration)!;
        }

        private static int CountUnhandledExceptionHandlers()
        {
            return CountHandlers(FindStaticEventHandler(typeof(AppContext), typeof(UnhandledExceptionEventHandler)));
        }

        private static int CountUnobservedTaskExceptionHandlers()
        {
            return CountHandlers(
                FindStaticEventHandler(
                    typeof(TaskScheduler),
                    typeof(EventHandler<UnobservedTaskExceptionEventArgs>)));
        }

        private static Delegate? FindStaticEventHandler(Type type, Type delegateType)
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.FieldType == delegateType)
                {
                    return (Delegate?)field.GetValue(null);
                }
            }

            return null;
        }

        private static int CountHandlers(Delegate? handlers)
        {
            return handlers?.GetInvocationList().Length ?? 0;
        }
    }
}
