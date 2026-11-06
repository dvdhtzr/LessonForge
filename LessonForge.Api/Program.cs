// Copyright (c) 2026 — LessonForge. Original work.

using System.Text.Json;
using LessonForge.Api.Export;
using LessonForge.Api.Persistence;
using LessonForge.Api.Runs;
using LessonForge.Core.Configuration;
using LessonForge.Core.Dto;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// QuestPDF Community license (free for this use).
QuestPDF.Settings.License = LicenseType.Community;

const string CorsPolicy = "frontend";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins(
        builder.Configuration.GetSection("LessonForge:Cors:Origins").Get<string[]>()
            ?? ["http://localhost:5173", "http://127.0.0.1:5173"])
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Use only the repository-shipped LLM configuration, rather than depending on
// machine-specific or stale session environment variables for model selection.
var llmConfiguration = new ConfigurationBuilder()
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();
var llmOptions = llmConfiguration.GetSection(LlmOptions.SectionName).Get<LlmOptions>()
    ?? throw new InvalidOperationException("Missing LessonForge:Llm in appsettings.json.");
builder.Services.AddSingleton(new ClientFactory(llmOptions));

// SQLite persistence for finished lessons (the vector index is never persisted).
var dbPath = builder.Configuration["LessonForge:DbPath"] ?? "lessonforge.db";
builder.Services.AddDbContext<LessonDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<LessonStore>();

builder.Services.AddSingleton<LessonRunService>();

var app = builder.Build();
app.Logger.LogInformation(
    "LessonForge Ollama configuration: chat={ChatModel}, embeddings={EmbeddingModel}, endpoint={Endpoint}, maxOutputTokens={MaxOutputTokens}",
    llmOptions.ChatModel, llmOptions.EmbeddingModel, llmOptions.Endpoint, llmOptions.MaxOutputTokens);
app.UseCors(CorsPolicy);

// Ensure the database exists.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<LessonDbContext>().Database.EnsureCreated();
}

var api = app.MapGroup("/api/lessons");

// Start a generation run and stream Server-Sent Events: run → stage* → gate* → complete → done.
api.MapPost("/generate", async (HttpContext ctx, LessonRequest request, bool? interactive, LessonRunService runs) =>
{
    var session = runs.Start(request, interactive ?? true);

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    try
    {
        await foreach (var frame in session.Frames.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync(frame, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    finally
    {
        // Do not leave an Ollama workflow running after its browser stream disconnects.
        if (ctx.RequestAborted.IsCancellationRequested)
            runs.TryCancel(session.RunId);
    }
});

// Deliver a review-gate decision (approve / edit / abort) back to a waiting run.
api.MapPost("/gate/{runId}", (string runId, GateRequest body, LessonRunService runs) =>
{
    var delivered = runs.SubmitGate(runId, body.RequestId, new GateDecision(body.Approved, body.Payload));
    return delivered ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, message = "No gate is waiting for this run." });
});

api.MapPost("/{runId}/cancel", (string runId, LessonRunService runs) =>
    runs.TryCancel(runId) ? Results.Ok(new { ok = true }) : Results.NotFound());

// List persisted lessons (most recent first).
api.MapGet("/", async (LessonStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));

// Fetch one persisted lesson (request + verified result).
api.MapGet("/{id:int}", async (int id, LessonStore store, CancellationToken ct) =>
{
    var lesson = await store.GetAsync(id, ct);
    return lesson is null ? Results.NotFound() : Results.Ok(lesson);
});

// Export a lesson as md / json / pdf, in a student or teacher variant.
api.MapGet("/{id:int}/export", async (int id, string? format, string? audience, LessonStore store, CancellationToken ct) =>
{
    var lesson = await store.GetAsync(id, ct);
    if (lesson is null) return Results.NotFound();

    var fmt = (format ?? "md").ToLowerInvariant() switch
    {
        "json" => ExportFormat.Json,
        "pdf" => ExportFormat.Pdf,
        _ => ExportFormat.Markdown
    };
    var who = (audience ?? "teacher").Equals("student", StringComparison.OrdinalIgnoreCase)
        ? Audience.Student : Audience.Teacher;

    var result = LessonExporter.Export(StoredLessonView.From(lesson), fmt, who);
    return Results.File(result.Content, result.ContentType, result.FileName);
});

app.MapGet("/", () => Results.Ok(new { service = "LessonForge API", status = "ok" }));

app.Run();

/// <summary>Body of a gate decision POST.</summary>
public sealed record GateRequest(string RequestId, bool Approved, JsonElement? Payload);
