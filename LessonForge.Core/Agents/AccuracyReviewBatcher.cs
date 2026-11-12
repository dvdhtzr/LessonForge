// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Reviews every lesson section and quiz question in small, independently validated
/// scopes. A truncated multi-item response or one with incomplete review fields
/// is regenerated as independent smaller scopes. A one-item scope gets at most
/// one full retry for missing/invalid required review fields; persistent failures
/// fail closed. Only missing locations may be restored from host-owned context.
/// </summary>
public static class AccuracyReviewBatcher
{
    public const int ItemsPerScope = 2;

    public sealed record Scope(IReadOnlyList<int> Sections, IReadOnlyList<int> Questions)
    {
        public int ItemCount => Sections.Count + Questions.Count;

        public string Label => string.Join(", ",
            Sections.Select(i => $"S{i + 1}").Concat(Questions.Select(i => $"Q{i + 1}")));

        public bool ContainsLocation(string location)
            => location == "GENERAL"
               || Sections.Any(i => location == $"S{i + 1}")
               || Questions.Any(i => location == $"Q{i + 1}");

        public (Scope Left, Scope Right) Split()
        {
            if (ItemCount < 2) throw new InvalidOperationException("Cannot split one review item.");
            var midpoint = ItemCount / 2;
            var ids = Sections.Select(i => (Kind: 'S', Index: i))
                .Concat(Questions.Select(i => (Kind: 'Q', Index: i))).ToArray();
            static Scope Make(IEnumerable<(char Kind, int Index)> items)
            {
                var selected = items.ToArray();
                return new Scope(
                    selected.Where(item => item.Kind == 'S').Select(item => item.Index).ToArray(),
                    selected.Where(item => item.Kind == 'Q').Select(item => item.Index).ToArray());
            }
            return (Make(ids.Take(midpoint)), Make(ids.Skip(midpoint)));
        }
    }

    public static IReadOnlyList<Scope> Plan(LessonPlanOutput plan, QuizOutput quiz)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(quiz);
        if (plan.Sections is not { Count: > 0 } || quiz.Questions is not { Count: > 0 })
            throw new InvalidOperationException("Accuracy review requires sections and quiz questions.");

        return Enumerable.Range(0, plan.Sections.Count)
            .Chunk(ItemsPerScope)
            .Select(indices => new Scope(indices, Array.Empty<int>()))
            .Concat(Enumerable.Range(0, quiz.Questions.Count)
                .Chunk(ItemsPerScope)
                .Select(indices => new Scope(Array.Empty<int>(), indices)))
            .ToArray();
    }

    // Only errors from the strict structured-review parser are eligible.
    // This handles severity/category/recommendation/etc. as well as location;
    // it never assigns a missing value or accepts a partial model response.
    private static bool IsIncompleteReviewFailure(InvalidOperationException ex)
        => ex.Message.Contains("omitted required review fields", StringComparison.OrdinalIgnoreCase) &&
           ex.Message.Contains("Missing or invalid fields:", StringComparison.OrdinalIgnoreCase);

    public static async Task<FactCheckReviewOutput> RunAsync(
        LessonPlanOutput plan, QuizOutput quiz,
        Func<Scope, CancellationToken, Task<FactCheckReviewOutput>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var scopes = Plan(plan, quiz);
        var completed = new List<(Scope Scope, FactCheckReviewOutput Review)>();

        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed.AddRange(await RunScopeAsync(scope, cancellationToken));
        }

        var itemCount = completed.Sum(item => item.Scope.ItemCount);
        return new FactCheckReviewOutput
        {
            Flags = completed.SelectMany(item => item.Review.Flags).ToList(),
            // AI estimates are not objective measurements. Weight by number of
            // independently reviewed items instead of averaging unequal scopes.
            CitationCoverage = completed.Sum(item =>
                item.Review.CitationCoverage * item.Scope.ItemCount) / itemCount,
            Notes = "AI-estimated, scoped review (not an exhaustive factual guarantee). " +
                string.Join(" ", completed.Select(item =>
                    $"[{item.Scope.Label}] {item.Review.Notes}"))
        };

        async Task<List<(Scope Scope, FactCheckReviewOutput Review)>> RunScopeAsync(
            Scope scope, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            FactCheckReviewOutput review;
            try
            {
                review = await run(scope, token);
            }
            catch (InvalidOperationException ex) when (
                scope.ItemCount > 1 &&
                (ex.Message.Contains("output was truncated", StringComparison.OrdinalIgnoreCase) ||
                 IsIncompleteReviewFailure(ex)))
            {
                // An incomplete two-item audit cannot be trusted or repaired by
                // guessing severity/category. Regenerate each one-item scope.
                // Never salvage a truncated or partially invalid response.
                var (left, right) = scope.Split();
                var a = await RunScopeAsync(left, token);
                a.AddRange(await RunScopeAsync(right, token));
                return a;
            }
            catch (InvalidOperationException ex) when (
                scope.ItemCount == 1 && IsIncompleteReviewFailure(ex))
            {
                // Regenerate this one scope ONCE from the same evidence. The
                // model must choose a real rubric severity for each finding;
                // the host cannot invent it. An invalid second response throws,
                // and never becomes an empty or successful fact-check.
                review = await run(scope, token);
            }

            var invalid = AccuracyReviewPolicy.InvalidFields(
                review, plan.Sections.Count, quiz.Questions.Count).ToList();
            if (review.Flags is not null)
                for (var i = 0; i < review.Flags.Count; i++)
                    if (review.Flags[i] is { } flag && !scope.ContainsLocation(flag.Location))
                        invalid.Add($"flags[{i}].location (outside {scope.Label})");

            if (invalid.Count > 0)
            {
                // A local model sometimes assigns a finding to Q3 while reviewing
                // only Q1, Q2. Never re-label the finding or accept the batch:
                // regenerate each target independently using host-owned IDs.
                // Other invalid fields still fail closed, and a single target is
                // never split/retried for an explicitly wrong location.
                if (scope.ItemCount > 1 &&
                    invalid.All(field => field.Contains("(outside ", StringComparison.Ordinal)))
                {
                    var (left, right) = scope.Split();
                    var a = await RunScopeAsync(left, token);
                    a.AddRange(await RunScopeAsync(right, token));
                    return a;
                }

                throw new InvalidOperationException(
                    "Accuracy Editor returned invalid scoped review fields for " +
                    $"{scope.Label}: {string.Join(", ", invalid)}.");
            }

            return [(scope, review)];
        }
    }
}
