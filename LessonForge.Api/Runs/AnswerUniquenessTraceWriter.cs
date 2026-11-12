// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LessonForge.Core.Agents;
using LessonForge.Core.Dto;

namespace LessonForge.Api.Runs;

/// <summary>
/// Opt-in, local-only evaluation trace. Each event is a bounded, durable JSONL
/// append, including when the model or the workflow later fails. Never write
/// these records to ILogger, SSE, persisted lessons, or normal API responses.
/// </summary>
public sealed class AnswerUniquenessTraceWriter : IDisposable
{
    public const string DirectoryVariable = "LESSONFORGE_EVAL_DIAGNOSTICS_DIR";
    private const int MaxRecords = 256;
    private const int MaxTotalBytes = 1_500_000;
    private const int MaxEventBytes = 16_384;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FileStream _stream;
    private readonly string _runId;
    private readonly object _sync = new();
    private int _sequence;
    private int _writtenBytes;

    public string FilePath { get; }

    public static AnswerUniquenessTraceWriter? OpenIfEnabled(string runId)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        return string.IsNullOrWhiteSpace(directory) ? null : new AnswerUniquenessTraceWriter(directory, runId);
    }

    public static string FileName(string runId)
    {
        if (runId.Length != 12 || runId.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Expected a host-generated 12-character lowercase hex run ID.", nameof(runId));
        return runId + ".answer-uniqueness.jsonl";
    }

    // Public constructor makes durable storage testable without changing process environment.
    public AnswerUniquenessTraceWriter(string directory, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _runId = runId;
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, FileName(runId));
        _stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
    }

    public void Record(AnswerUniquenessTraceEvent incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.QuestionNumber is < 1 or > 30)
            throw new InvalidOperationException("Diagnostic question number outside supported bounds.");

        // Only fixed, host-owned vocabulary can become record metadata.
        if (incoming.Kind is not ("review_started" or "review_evidence" or "review_candidate"
            or "review_result" or "repair_started" or "repair_candidate"
            or "repair_output" or "outcome" or "failure"))
            throw new InvalidOperationException("Unknown diagnostic event kind.");
        if (incoming.Attempt is not ("original" or "replacement"))
            throw new InvalidOperationException("Unknown diagnostic attempt.");

        var bounded = incoming with
        {
            Question = SnapshotQuestion(incoming.Question),
            Review = SnapshotReview(incoming.Review),
            Evidence = Clip(incoming.Evidence, 4096),
            Reason = Clip(incoming.Reason, 150),
            FailureKind = Clip(incoming.FailureKind, 60)
        };

        lock (_sync)
        {
            if (_sequence >= MaxRecords)
                throw new InvalidOperationException("Evaluation trace record limit exceeded.");
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                runId = _runId,
                sequence = _sequence + 1,
                recordedAtUtc = DateTimeOffset.UtcNow,
                diagnostic = bounded
            }, s_json);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            if (bytes.Length > MaxEventBytes || _writtenBytes + bytes.Length > MaxTotalBytes)
                throw new InvalidOperationException("Evaluation trace size limit exceeded.");
            _stream.Write(bytes);
            _stream.Flush(flushToDisk: true); // Preserve preceding decisions on later failure.
            _writtenBytes += bytes.Length;
            _sequence++;
        }
    }

    private static QuizQuestion? SnapshotQuestion(QuizQuestion? q) => q is null ? null : new QuizQuestion
    {
        Stem = Clip(q.Stem, 512) ?? "",
        Options = q.Options?.Take(6).Select(s => Clip(s, 256) ?? "").ToList() ?? [],
        CorrectIndex = q.CorrectIndex,
        Explanation = Clip(q.Explanation, 600) ?? "",
        Citations = q.Citations?.Take(6).Select(c => new CitationRef
        {
            Title = Clip(c?.Title, 160) ?? "",
            Url = Clip(c?.Url, 400) ?? ""
        }).ToList() ?? []
    };

    private static AnswerUniquenessReview? SnapshotReview(AnswerUniquenessReview? r)
        => r is null ? null : new AnswerUniquenessReview
        {
            StemClear = r.StemClear,
            Notes = Clip(r.Notes, 600) ?? "",
            OptionVerdicts = r.OptionVerdicts?.Take(6).Select(v => v is null ? null! : new AnswerOptionVerdict
            {
                Index = v.Index,
                Verdict = Clip(v.Verdict, 40) ?? "",
                Reason = Clip(v.Reason, 400) ?? ""
            }).ToList() ?? []
        };

    private static string? Clip(string? value, int maxChars)
        => value is null ? null : value.Length <= maxChars ? value : value[..maxChars];

    public void Dispose() => _stream.Dispose();
}
