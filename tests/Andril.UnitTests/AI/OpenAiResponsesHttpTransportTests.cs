using System.Net;
using System.Text;
using System.Text.Json;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Simulated handlers only, synthetic credentials/counts. No network-backed handler,
/// real tokenizer, database, dispatch permission or financial effects.</summary>
public sealed class OpenAiResponsesHttpTransportTests
{
    private const string Secret = "synthetic-credential-never-real";
    private const string Answer = "Synthetic answer தமிழ் සිංහල";
    // Technical test setting only, not an approved product timeout.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static AiProviderExecutionSettings Settings() => new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false);
    private static AiTokenCountRequest Request() => new(OpenAiResponsesRequestSerializer.ApprovedModel,
        OpenAiResponsesRequestSerializer.SerializationRevision, "Private synthetic system",
        new[] { new AiPromptMessage(AiStoredMessageRole.User, "Earlier தமிழ்"),
            new AiPromptMessage(AiStoredMessageRole.Assistant, "Earlier සිංහල") }, "Current 👩🏽‍💻 question");
    private static AiProviderRequestCandidate Candidate(AiTokenCountRequest request) => AiProviderRequestCandidate.FromMeasuredRequest(request, Settings());
    private static OpenAiPreparedResponsesRequest Prepare(AiTokenCountRequest request) => OpenAiResponsesRequestSerializer.Prepare(request, Candidate(request));
    private static AiTokenCountResult Measurement(AiTokenCountRequest request, OpenAiPreparedResponsesRequest payload) =>
        new(AiTokenCountState.Measured, new(request.Model, request.Fingerprint, 400, 400, 400, 400, 400, 2000, 500, true, true),
            new("synthetic-counter", "fixture-v1", request.FramingRevision), payload.MeasurementPayload.Binding);
    private static string Json(string state = "completed", string kind = "output_text", string model = "gpt-6-luna") =>
        JsonSerializer.Serialize(new
        {
            id = "resp_synthetic", @object = "response", model, status = state,
            incomplete_details = state == "incomplete" ? new { reason = "max_output_tokens" } : null,
            output = new[] { new { id = "msg_synthetic", type = "message", role = "assistant", status = state == "incomplete" ? "incomplete" : "completed",
                content = new[] { new Dictionary<string, object> { ["type"] = kind, [kind == "refusal" ? "refusal" : "text"] = Answer } } } },
            usage = new { input_tokens = 100, output_tokens = 20, total_tokens = 120, output_tokens_details = new { reasoning_tokens = 5 } }
        });
    private static HttpResponseMessage Response(int status = 200, string? body = null) =>
        new((HttpStatusCode)status) { Content = new StringContent(body ?? Json(), Encoding.UTF8, "application/json") };
    private static Task<OpenAiTransportResult> Send(OpenAiResponsesHttpTransport transport, CancellationToken token = default)
    {
        var request = Request();
        var prepared = Prepare(request);
        return transport.SendAsync(request, Candidate(request), Measurement(request, prepared), prepared, new(Secret), token);
    }

    [Fact]
    public async Task SendsRetainedBytesToFixedDestinationAndDisposesRequestButNotSharedClient()
    {
        var request = Request();
        var prepared = Prepare(request);
        var bytes = prepared.CopyUtf8Payload();
        HttpRequestMessage? captured = null;
        var stream = new TrackingStream(Encoding.UTF8.GetBytes(Json()));
        var handler = new Handler(async (message, token) =>
        {
            captured = message;
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.openai.com/v1/responses", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal(Secret, message.Headers.Authorization.Parameter);
            Assert.Single(message.Headers);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await message.Content.ReadAsByteArrayAsync(token));
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(request.Model, json.RootElement.GetProperty("model").GetString());
            Assert.Equal(request.Messages[^1].Text, json.RootElement.GetProperty("input")[3].GetProperty("content").GetString());
            var response = StreamResponse(stream);
            response.Headers.Add("x-request-id", "req_synthetic-123");
            return response;
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://ignored.example.test") };
        var transport = new OpenAiResponsesHttpTransport(client, Deadline);
        var result = await transport.SendAsync(request, Candidate(request), Measurement(request, prepared), prepared, new(Secret));
        Assert.Equal(OpenAiTransportOutcome.ResponseReceived, result.Outcome);
        Assert.Equal(OpenAiTransportPhase.Complete, result.Phase);
        Assert.Equal(Answer, result.Response!.AssistantText);
        Assert.Equal("req_synthetic-123", result.ProviderRequestId);
        Assert.True(result.ExecutionMayHaveOccurred);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(bytes, prepared.CopyUtf8Payload());
        Assert.True(stream.Disposed);
        Assert.NotNull(captured);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => captured!.Content!.ReadAsByteArrayAsync());
        Assert.Empty(client.DefaultRequestHeaders);
        Assert.False(handler.Disposed);
        // Reuse confirms that the injected client remains usable; still one send per invocation.
        await Send(transport);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("candidate")]
    [InlineData("payload")]
    [InlineData("measurement")]
    [InlineData("binding")]
    [InlineData("digest")]
    [InlineData("model")]
    [InlineData("measurement-request")]
    [InlineData("unverified")]
    [InlineData("settings")]
    [InlineData("credential")]
    [InlineData("default-header")]
    public async Task InvalidEvidenceInvokesHandlerZeroTimes(string defect)
    {
        AiTokenCountRequest? request = Request();
        AiProviderRequestCandidate? candidate = Candidate(request);
        OpenAiPreparedResponsesRequest? prepared = Prepare(request);
        AiTokenCountResult? measured = Measurement(request, prepared);
        OpenAiTransportCredential? credential = new(Secret);
        if (defect == "request") request = null;
        if (defect == "candidate") candidate = null;
        if (defect == "payload") prepared = null;
        if (defect == "measurement") measured = null;
        if (defect == "binding") measured = measured! with { PayloadBinding = null };
        if (defect == "digest") measured = measured! with { PayloadBinding = measured.PayloadBinding! with { PayloadSha256 = "invalid" } };
        if (defect == "model") measured = measured! with { PayloadBinding = measured.PayloadBinding! with { Model = "other-model" } };
        if (defect == "measurement-request") measured = measured! with { Evidence = measured.Evidence! with { RequestFingerprint = "other-request" } };
        if (defect == "unverified") measured = measured! with { Evidence = measured.Evidence! with { FullRequestVerified = false } };
        if (defect == "settings") candidate = AiProviderRequestCandidate.FromMeasuredRequest(request!, Settings() with { Store = true });
        if (defect == "credential") credential = null;
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not invoke"));
        using var client = new HttpClient(handler);
        if (defect == "default-header") client.DefaultRequestHeaders.Add("X-Private", "private-value");
        var result = await new OpenAiResponsesHttpTransport(client, Deadline).SendAsync(request, candidate, measured, prepared, credential);
        Assert.Equal(OpenAiTransportOutcome.InvalidPreparedRequest, result.Outcome);
        Assert.False(result.HttpInvocationStarted);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(400, "RequestRejected")]
    [InlineData(401, "AuthenticationRejected")]
    [InlineData(403, "AccessRejected")]
    [InlineData(404, "RequestRejected")]
    [InlineData(408, "Timeout")]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "ProviderUnavailable")]
    [InlineData(502, "ProviderUnavailable")]
    [InlineData(503, "ProviderUnavailable")]
    [InlineData(504, "Timeout")]
    [InlineData(302, "UncertainExecution")]
    [InlineData(307, "UncertainExecution")]
    [InlineData(204, "UncertainExecution")]
    [InlineData(599, "UncertainExecution")]
    public async Task HttpClassificationNeverGrantsRetryOrZeroLiability(int status, string outcome)
    {
        var stream = new TrackingStream(Encoding.UTF8.GetBytes("private error " + Secret));
        var response = StreamResponse(stream, status);
        response.Headers.Location = new Uri("https://unapproved.example.test/redirect");
        var handler = new Handler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(Enum.Parse<OpenAiTransportOutcome>(outcome), result.Outcome);
        Assert.Equal(status, result.HttpStatusCode);
        Assert.Null(result.Response);
        Assert.True(result.ExecutionMayHaveOccurred);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Secret, result.ToString());
        Assert.DoesNotContain("private error", result.ToString());
    }

    [Theory]
    [InlineData("completed", "output_text", AiTextCompletionState.Completed)]
    [InlineData("completed", "refusal", AiTextCompletionState.Refused)]
    [InlineData("incomplete", "output_text", AiTextCompletionState.Incomplete)]
    [InlineData("future_status", "output_text", AiTextCompletionState.Uncertain)]
    public async Task HttpSuccessIsSeparateFromModelCompletionAndAnswerAcceptance(string state, string kind, AiTextCompletionState completion)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(body: Json(state, kind))));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(OpenAiTransportOutcome.ResponseReceived, result.Outcome);
        Assert.Equal(completion, result.Response!.Completion);
        if (completion != AiTextCompletionState.Completed) Assert.Null(result.Response.AssistantText);
        Assert.NotEqual(AiTextValidationOutcome.ValidText, result.Response.ValidateUnverifiedText(new(
            OpenAiResponsesRequestSerializer.ApprovedModel, "local-context", false)).Outcome);
        Assert.DoesNotContain(Answer, result.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PreCancelledRequestNeverInvokesHttp()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not invoke"));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await Send(new(client, Deadline), cancellation.Token);
        Assert.Equal(OpenAiTransportOutcome.CancelledBeforeInvocation, result.Outcome);
        Assert.False(result.ExecutionMayHaveOccurred);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAndDeadlineCoverHeadersAndBody(bool reading, bool timeout)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ControlledClock();
        using var cancellation = new CancellationTokenSource();
        var stream = new BlockingStream(entered);
        using var response = StreamResponse(stream);
        var handler = new Handler(async (_, token) =>
        {
            if (reading) return response;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var pending = Send(new(client, Deadline, clock), cancellation.Token);
        // Hang guards only; test behavior uses explicit entry/cancellation/timer signals.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout) clock.Expire(); else cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(timeout ? OpenAiTransportOutcome.Timeout : OpenAiTransportOutcome.CancelledDuringExecution, result.Outcome);
        Assert.Equal(reading ? OpenAiTransportPhase.ReadingBody : OpenAiTransportPhase.WaitingForHeaders, result.Phase);
        Assert.Equal(reading ? 200 : (int?)null, result.HttpStatusCode);
        Assert.True(result.ExecutionMayHaveOccurred);
        Assert.Equal(1, handler.Calls);
        if (reading) Assert.True(stream.Disposed);
        Assert.True(clock.TimerDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionAndInterruptedReadingFailuresDoNotRetry(bool reading)
    {
        var stream = new BrokenStream();
        var handler = new Handler((_, _) =>
        {
            if (!reading) throw new HttpRequestException("private connection " + Secret);
            return Task.FromResult(StreamResponse(stream));
        });
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(reading ? OpenAiTransportOutcome.UncertainExecution : OpenAiTransportOutcome.TransportFailure, result.Outcome);
        Assert.True(result.ExecutionMayHaveOccurred);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Secret, result.ToString());
        if (reading) Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ private malformed")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task EmptyAndMalformedBodiesAreNotSuccessfulAnswers(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(body: body)));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(OpenAiTransportOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, result.Response!.Outcome);
        Assert.True(result.ExecutionMayHaveOccurred);
        Assert.DoesNotContain("private malformed", result.ToString());
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("model")]
    [InlineData("missing-content")]
    [InlineData("content-type")]
    [InlineData("encoding")]
    [InlineData("invalid-utf8")]
    [InlineData("redirected-destination")]
    public async Task UnsupportedAndInconsistentResponsesFailClosed(string defect)
    {
        var response = Response(body: defect == "unsupported" ? Json(kind: "output_audio") : defect == "model" ? Json(model: "other-model") : Json());
        if (defect == "missing-content") response.Content = null;
        if (defect == "content-type") response.Content.Headers.ContentType = new("text/html");
        if (defect == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
        if (defect == "invalid-utf8")
        {
            response.Content = new ByteArrayContent(new byte[] { 0xff, 0xfe });
            response.Content.Headers.ContentType = new("application/json");
        }
        using var redirected = new HttpRequestMessage(HttpMethod.Post, "https://unapproved.example.test");
        if (defect == "redirected-destination") response.RequestMessage = redirected;
        var handler = new Handler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(OpenAiTransportOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.True(result.ExecutionMayHaveOccurred);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("underreported")]
    [InlineData("over-limit")]
    public async Task ActualByteBoundIsEnforcedWithoutTrustingContentLength(string length)
    {
        var stream = new TrackingStream(new byte[OpenAiResponsesResponseParser.ProposedMaxJsonBytes + 100]) { HideLength = length == "absent" };
        var response = StreamResponse(stream);
        if (length == "underreported") response.Content.Headers.ContentLength = 1;
        if (length == "over-limit") response.Content.Headers.ContentLength = OpenAiResponsesResponseParser.ProposedMaxJsonBytes + 100;
        var handler = new Handler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(OpenAiTransportOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(length == "over-limit" ? 0 : OpenAiResponsesResponseParser.ProposedMaxJsonBytes + 1, stream.BytesRead);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task IncorrectDeclaredLengthCannotBecomeACompleteResponse(int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(Json());
        var stream = new TrackingStream(bytes);
        var response = StreamResponse(stream);
        response.Content.Headers.ContentLength = bytes.Length + difference;
        var handler = new Handler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(OpenAiTransportOutcome.UncertainExecution, result.Outcome);
        Assert.Equal(OpenAiTransportPhase.ReadingBody, result.Phase);
        Assert.True(result.ExecutionMayHaveOccurred);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("req_valid-123", true)]
    [InlineData("private error with spaces", false)]
    [InlineData("req_one,req_two", false)]
    [InlineData("தமிழ்", false)]
    [InlineData("", false)]
    public async Task CorrelationHeaderIsMinimizedAndExcludedFromDiagnostics(string value, bool valid)
    {
        var response = Response();
        response.Headers.TryAddWithoutValidation("x-request-id", value);
        var handler = new Handler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        var result = await Send(new(client, Deadline));
        Assert.Equal(valid ? value : null, result.ProviderRequestId);
        Assert.DoesNotContain("req_valid", result.ToString());
        Assert.DoesNotContain("private error", result.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("private secret")]
    [InlineData("private\r\nX-Injected: true")]
    [InlineData("தமிழ்")]
    public void InvalidCredentialsHaveSafeExceptions(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => new OpenAiTransportCredential(value));
        Assert.DoesNotContain(value == "" ? "private" : value, error.Message);
        Assert.DoesNotContain(Secret, new OpenAiTransportCredential(Secret).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.MaxValue)]
    public void InvalidDeadlinesNeverInvokeHttp(double milliseconds)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not invoke"));
        using var client = new HttpClient(handler);
        var timeout = milliseconds == double.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiResponsesHttpTransport(client, timeout));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void FriendAccessIsLimitedToActualTestAssemblyAndTransportRemainsInternal()
    {
        var assembly = typeof(OpenAiResponsesHttpTransport).Assembly;
        Assert.Equal("Andril.UnitTests", typeof(OpenAiResponsesHttpTransportTests).Assembly.GetName().Name);
        Assert.False(typeof(OpenAiResponsesHttpTransport).IsPublic);
        var friends = assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false)
            .Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>();
        Assert.Equal("Andril.UnitTests", Assert.Single(friends).AssemblyName);
    }

    private static HttpResponseMessage StreamResponse(Stream stream, int status = 200)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request, token); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int BytesRead { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool HideLength { get; set; }
        public override bool CanSeek => !HideLength && base.CanSeek;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await base.ReadAsync(buffer, token); BytesRead += count; return count; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private abstract class TestReadStream : Stream
    {
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class BlockingStream(TaskCompletionSource entered) : TestReadStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    }
    private sealed class BrokenStream : TestReadStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException<int>(new IOException("private interrupted body " + Secret));
    }
    private sealed class ControlledClock : TimeProvider
    {
        private ControlledTimer? _timer;
        internal bool TimerDisposed => _timer?.Disposed == true;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _timer = new(callback, state); return _timer; }
        internal void Expire() => _timer!.Fire();
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed { get; private set; }
            internal void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
