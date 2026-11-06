// Copyright (c) 2026 — LessonForge. Original work.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using LessonForge.Api.Persistence;
using LessonForge.Core.Configuration;
using LessonForge.Core.Dto;
using LessonForge.Core.Workflow;
using Microsoft.Agents.AI.Workflows;

namespace LessonForge.Api.Runs;

/// <summary>A client's decision at a review gate: approve (optionally with an edited payload) or abort.</summary>
public sealed record GateDecision(bool Approved, JsonElement? Payload);

/// <summary>
/// One in-flight generation run. Holds the queue of Server-Sent-Event frames the HTTP stream drains,
/// and a rendezvous point per pending gate so the gate endpoint can hand an approval back to the
/// workflow driver. Single-user v1: runs live in memory only.
/// </summary>
public sealed class RunSession
{
    public required string RunId { get; init; }
    public Channel<string> Frames { get; } = Channel.CreateUnbounded<string>();
    public ConcurrentDictionary<string, TaskCompletionSource<GateDecision>> Gates { get; } = new();
    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Starts and drives lesson-generation workflow runs, translating MAF workflow events into SSE
/// frames and coordinating the human-in-the-loop review gates.
/// </summary>
public sealed class LessonRunService(
    ClientFactory clients,
    IServiceScopeFactory scopeFactory,
    ILogger<LessonRunService> logger)
{
    private readonly ConcurrentDictionary<string, RunSession> _runs = new();

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public RunSession Start(LessonRequest request, bool interactive)
    {
        var session = new RunSession { RunId = Guid.NewGuid().ToString("n")[..12] };
        _runs[session.RunId] = session;
        logger.LogInformation("Starting lesson run {RunId} for {Topic} (age {AudienceAge}, interactive: {Interactive})",
            session.RunId, request.Topic, request.AudienceAge, interactive);
        _ = Task.Run(() => DriveAsync(session, request, interactive));
        return session;
    }

    /// <summary>Delivers a client's gate decision to a waiting run. Returns false if nothing was waiting.</summary>
    public bool SubmitGate(string runId, string requestId, GateDecision decision)
    {
        if (_runs.TryGetValue(runId, out var session)
            && session.Gates.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(decision);
            return true;
        }
        return false;
    }

    public bool TryCancel(string runId)
    {
        if (_runs.TryGetValue(runId, out var session))
        {
            session.Cts.Cancel();
            return true;
        }
        return false;
    }

    private async Task DriveAsync(RunSession session, LessonRequest request, bool interactive)
    {
        var ct = session.Cts.Token;
        try
        {
            await WriteAsync(session, "run", new { runId = session.RunId, interactive });

            var retrieval = LessonWorkflow.CreateRetrievalService(clients);
            var progress = new Progress<string>(message =>
            {
                logger.LogInformation("Lesson run {RunId}: research: {Message}", session.RunId, message);
                var payload = JsonSerializer.Serialize(new { stage = "Research", message }, s_json);
                session.Frames.Writer.TryWrite($"event: progress\ndata: {payload}\n\n");
            });
            var (workflow, _) = LessonWorkflow.Build(clients, retrieval, interactive, progress);

            await using var run = await InProcessExecution.RunStreamingAsync(workflow, request);

            VerifiedLessonOutput? result = null;

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(ct))
            {
                switch (evt)
                {
                    case ExecutorInvokedEvent invoked when LessonWorkflow.StageOrder.Contains(invoked.ExecutorId):
                        logger.LogInformation("Lesson run {RunId}: stage {Stage} started", session.RunId, invoked.ExecutorId);
                        await WriteAsync(session, "stage", new { name = invoked.ExecutorId, status = "started" });
                        break;

                    case ExecutorCompletedEvent completed when LessonWorkflow.StageOrder.Contains(completed.ExecutorId):
                        logger.LogInformation("Lesson run {RunId}: stage {Stage} completed", session.RunId, completed.ExecutorId);
                        await WriteAsync(session, "stage", new { name = completed.ExecutorId, status = "completed" });
                        break;

                    case ExecutorFailedEvent failed:
                        logger.LogError("Lesson run {RunId}: stage {Stage} failed: {Message}",
                            session.RunId, failed.ExecutorId, failed.Data?.Message);
                        await WriteAsync(session, "stage", new { name = failed.ExecutorId, status = "failed", message = failed.Data?.Message });
                        break;

                    case WorkflowOutputEvent output when output.Data is VerifiedLessonOutput verified:
                        result = verified;
                        break;

                    case RequestInfoEvent requestInfo:
                        await HandleGateAsync(session, run, requestInfo.Request, ct);
                        break;
                }
            }

            if (result is not null)
            {
                var id = await PersistAsync(request, result, ct);
                logger.LogInformation("Lesson run {RunId} completed; saved lesson {LessonId}", session.RunId, id);
                await WriteAsync(session, "complete", new { id, result });
            }
            else if (!ct.IsCancellationRequested)
            {
                await WriteAsync(session, "error", new { message = "The run finished without producing a lesson." });
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Lesson run {RunId} cancelled", session.RunId);
            await WriteAsync(session, "aborted", new { message = "Run cancelled." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lesson run {RunId} failed", session.RunId);
            await WriteAsync(session, "error", new { message = ex.Message });
        }
        finally
        {
            await WriteAsync(session, "done", new { });
            session.Frames.Writer.TryComplete();
            // Keep the session briefly so a late gate POST gets a clean "not waiting" answer, then drop it.
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _runs.TryRemove(session.RunId, out RunSession? _));
        }
    }

    // Surfaces a review gate to the client and waits for its decision, then forwards it to the workflow.
    private async Task HandleGateAsync(RunSession session, StreamingRun run, ExternalRequest request, CancellationToken ct)
    {
        string stage;
        object payload;
        Func<JsonElement, ExternalResponse> approve;

        if (request.TryGetDataAs<ResearchOutput>(out var research))
        {
            stage = "research";
            payload = research!;
            approve = edited => request.CreateResponse(Deserialize(edited, research!));
        }
        else if (request.TryGetDataAs<LessonPlanOutput>(out var plan))
        {
            stage = "plan";
            payload = plan!;
            approve = edited => request.CreateResponse(Deserialize(edited, plan!));
        }
        else
        {
            // Unknown gate type — approve with whatever came through, to avoid stalling.
            await run.SendResponseAsync(request.CreateResponse(request.Data));
            return;
        }

        var tcs = new TaskCompletionSource<GateDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Gates[request.RequestId] = tcs;

        logger.LogInformation("Lesson run {RunId}: awaiting {Stage} approval", session.RunId, stage);
        await WriteAsync(session, "gate", new { requestId = request.RequestId, stage, payload });

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var decision = await tcs.Task;
        logger.LogInformation("Lesson run {RunId}: {Stage} gate {Decision}",
            session.RunId, stage, decision.Approved ? "approved" : "rejected");

        if (!decision.Approved)
        {
            await run.CancelRunAsync();
            session.Cts.Cancel();
            return;
        }

        var response = decision.Payload is { } edited ? approve(edited) : approve(ToElement(payload));
        await run.SendResponseAsync(response);
    }

    private async Task<int> PersistAsync(LessonRequest request, VerifiedLessonOutput result, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<LessonStore>();
        return await store.SaveAsync(request, result, ct);
    }

    private static T Deserialize<T>(JsonElement element, T fallback)
        => element.Deserialize<T>(s_json) ?? fallback;

    private static JsonElement ToElement(object value)
        => JsonSerializer.SerializeToElement(value, s_json);

    private async Task WriteAsync(RunSession session, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data, s_json);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        await session.Frames.Writer.WriteAsync(frame);
    }
}
