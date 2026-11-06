// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Reviews every lesson section and quiz question in small, independently validated
/// scopes. A length-exhausted multi-item response is retried as smaller scopes;
/// an incomplete single-item review still fails the entire stage.
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
                ex.Message.Contains("output was truncated", StringComparison.OrdinalIgnoreCase))
            {
                // Retry only complete re-generations of smaller scopes.
                // Never parse, append, or salvage a truncated response.
                var (left, right) = scope.Split();
                var a = await RunScopeAsync(left, token);
                a.AddRange(await RunScopeAsync(right, token));
                return a;
            }

            var invalid = AccuracyReviewPolicy.InvalidFields(
                review, plan.Sections.Count, quiz.Questions.Count).ToList();
            if (review.Flags is not null)
                for (var i = 0; i < review.Flags.Count; i++)
                    if (review.Flags[i] is { } flag && !scope.ContainsLocation(flag.Location))
                        invalid.Add($"flags[{i}].location (outside {scope.Label})");

            if (invalid.Count > 0)
                throw new InvalidOperationException(
                    "Accuracy Editor returned invalid scoped review fields for " +
                    $"{scope.Label}: {string.Join(", ", invalid)}.");

            return [(scope, review)];
        }
    }
}
