using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat.Prototype;

public sealed class ChatPrototypeQueuedCancellationTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("history")]
    [InlineData("submit")]
    public async Task Cancelled_gate_waiter_cannot_release_another_requests_gate_or_poison_later_requests(string operation)
    {
        // Existing tests cancel during evidence reads and before append. Here a
        // different request owns the gate: the cancelled waiter must never own it.
        var queued = new QueuedRequestObserver();
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social,
            services => services.AddSingleton<IStartupFilter>(queued));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Scenario.BeforeAppend = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var ownerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var owner = host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages",
            new ChatPrototypeSubmitDto("gate owner"), ownerCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, host.Scenario.Operations.CurrentCount);
            var readsBeforeWaiter = host.Evidence.Reads;
            var path = operation switch
            {
                "list" => ChatPrototypeApiTests.Root() + "?pageSize=1",
                "detail" => ChatPrototypeApiTests.Target(),
                "history" => ChatPrototypeApiTests.Target() + "/messages?pageSize=1",
                _ => ChatPrototypeApiTests.Target() + "/messages"
            };
            using var request = new HttpRequestMessage(operation == "submit" ? HttpMethod.Post : HttpMethod.Get, path);
            request.Headers.Add(QueuedRequestObserver.Marker, "1");
            if (operation == "submit") request.Content = JsonContent.Create(new ChatPrototypeSubmitDto("cancelled waiter"));
            using var cancellation = new CancellationTokenSource();
            var waiter = host.Client.SendAsync(request, cancellation.Token);
            await queued.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(waiter.IsCompleted);
            Assert.Equal(readsBeforeWaiter, host.Evidence.Reads);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            // Client cancellation can complete before server unwind. Observe both
            // before checking that the owner still exclusively holds the gate.
            await queued.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(owner.IsCompleted);
            Assert.Equal(0, host.Scenario.Operations.CurrentCount);
            Assert.Equal(readsBeforeWaiter, host.Evidence.Reads);
            Assert.Equal(3, host.Scenario.MessageCount);
            Assert.Equal(0, host.Scenario.CursorCount);

            release.TrySetResult();
            using var ownerResponse = await owner.WaitAsync(TimeSpan.FromSeconds(10));
            await ChatPrototypeApiTests.Read<ChatPrototypeMessageDto>(ownerResponse, HttpStatusCode.Created);
            using var recovered = await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages",
                new ChatPrototypeSubmitDto("after cancellation"), ownerCancellation.Token);
            await ChatPrototypeApiTests.Read<ChatPrototypeMessageDto>(recovered, HttpStatusCode.Created);
            var history = host.Scenario.History(ChatPrototypeTestHost.Conversation);
            Assert.Equal(5, history.Length);
            Assert.Single(history, message => message.Text == "gate owner");
            Assert.Single(history, message => message.Text == "after cancellation");
            Assert.DoesNotContain(history, message => message.Text == "cancelled waiter");
            Assert.Equal(1, host.Scenario.Operations.CurrentCount);
        }
        finally
        {
            release.TrySetResult();
            // Drain the request before the host-owned semaphore is disposed,
            // including when an assertion above fails.
            try { using var response = await owner.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (ownerCancellation.IsCancellationRequested) { }
        }
    }

    private sealed class QueuedRequestObserver : IStartupFilter
    {
        internal const string Marker = "X-Test-Queued-Cancellation";
        internal TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (!context.Request.Headers.ContainsKey(Marker))
                {
                    await continuation(context);
                    return;
                }
                try
                {
                    // Fixed authentication completes synchronously. Invoke the
                    // real pipeline through its first incomplete await (the held
                    // gate) before signalling; no sleeps or replacement handlers.
                    var pending = continuation(context);
                    Dispatched.TrySetResult();
                    await pending;
                }
                finally { Finished.TrySetResult(); }
            });
            next(app);
        };
    }
}
