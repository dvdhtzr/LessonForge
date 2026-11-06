// Copyright (c) 2026 — LessonForge. Original work.

using System.Text.Json;
using LessonForge.Core.Dto;
using Microsoft.EntityFrameworkCore;

namespace LessonForge.Api.Persistence;

/// <summary>A persisted lesson generation: the request plus the final verified result.</summary>
public sealed class LessonRecord
{
    public int Id { get; set; }
    public string Topic { get; set; } = "";
    public int AudienceAge { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The original <see cref="LessonRequest"/> as JSON.</summary>
    public string RequestJson { get; set; } = "{}";

    /// <summary>The final <see cref="VerifiedLessonOutput"/> as JSON.</summary>
    public string ResultJson { get; set; } = "{}";
}

public sealed class LessonDbContext(DbContextOptions<LessonDbContext> options) : DbContext(options)
{
    public DbSet<LessonRecord> Lessons => Set<LessonRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<LessonRecord>().HasKey(l => l.Id);
}

/// <summary>Thin persistence facade over EF Core + SQLite. The vector index is never persisted.</summary>
public sealed class LessonStore(LessonDbContext db)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<int> SaveAsync(LessonRequest request, VerifiedLessonOutput result, CancellationToken ct = default)
    {
        var record = new LessonRecord
        {
            Topic = request.Topic,
            AudienceAge = request.AudienceAge,
            CreatedAt = DateTime.UtcNow,
            RequestJson = JsonSerializer.Serialize(request, s_json),
            ResultJson = JsonSerializer.Serialize(result, s_json)
        };
        db.Lessons.Add(record);
        await db.SaveChangesAsync(ct);
        return record.Id;
    }

    public async Task<IReadOnlyList<LessonSummary>> ListAsync(CancellationToken ct = default)
        => await db.Lessons
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new LessonSummary(l.Id, l.Topic, l.AudienceAge, l.CreatedAt))
            .ToListAsync(ct);

    public async Task<StoredLesson?> GetAsync(int id, CancellationToken ct = default)
    {
        var record = await db.Lessons.FindAsync([id], ct);
        if (record is null) return null;

        var request = JsonSerializer.Deserialize<LessonRequest>(record.RequestJson, s_json) ?? new LessonRequest();
        var result = JsonSerializer.Deserialize<VerifiedLessonOutput>(record.ResultJson, s_json) ?? new VerifiedLessonOutput();
        return new StoredLesson(record.Id, record.Topic, record.AudienceAge, record.CreatedAt, request, result);
    }
}

public sealed record LessonSummary(int Id, string Topic, int AudienceAge, DateTime CreatedAt);

public sealed record StoredLesson(
    int Id, string Topic, int AudienceAge, DateTime CreatedAt, LessonRequest Request, VerifiedLessonOutput Result);
