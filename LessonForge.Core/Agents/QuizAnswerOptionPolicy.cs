// Copyright (c) 2026 — LessonForge. Original work.

using LessonForge.Core.Dto;

namespace LessonForge.Core.Agents;

/// <summary>
/// Host-owned, model-independent quiz-option checks. These rules catch invalid
/// answer indices and identical answer choices, but do not pretend to decide
/// whether two different wordings are semantically equivalent. That requires
/// the separate evidence-aware Accuracy Editor and an educator's judgment.
/// </summary>
public static class QuizAnswerOptionPolicy
{
    public static IReadOnlyList<string> InvalidFields(QuizOutput quiz)
    {
        ArgumentNullException.ThrowIfNull(quiz);
        var invalid = new List<string>();

        if (quiz.Questions is null) return ["questions"];

        for (var index = 0; index < quiz.Questions.Count; index++)
        {
            var question = quiz.Questions[index];
            var options = question?.Options;
            if (options is null || options.Count == 0) continue;

            if (question.CorrectIndex < 0 || question.CorrectIndex >= options.Count)
                invalid.Add($"questions[{index}].correct_index");

            // Compare only genuinely identical choices under harmless casing and
            // whitespace variation. Do NOT infer that different place names,
            // synonyms, or overlapping categories must be equivalent.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
            {
                var choice = options[optionIndex];
                if (string.IsNullOrWhiteSpace(choice)) continue;
                var normalized = string.Join(" ",
                    choice.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (!seen.Add(normalized))
                    invalid.Add($"questions[{index}].options[{optionIndex}] (duplicate choice)");
            }
        }
        return invalid;
    }

    public static void RequireValid(QuizOutput quiz)
    {
        var errors = InvalidFields(quiz);
        if (errors.Count != 0)
            throw new InvalidOperationException(
                "Quiz has invalid answer options: " + string.Join(", ", errors) + ".");
    }
}
