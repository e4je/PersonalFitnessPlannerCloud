namespace PersonalFitnessPlanner.Core;

public enum MovementQuality
{
    Poor,
    Fair,
    Good,
}

public enum ProgressionAction
{
    Increase,
    Hold,
    Decrease,
}

public enum ProgressionReason
{
    PainReported,
    AllWorkingSetsAtUpperBound,
    MoreThanHalfBelowLowerBound,
    TwoConsecutiveFailures,
    KeepBuildingReps,
    NoCompletedWorkingSets,
}

public sealed record ProgressionSet(
    int Reps,
    int? Rir,
    MovementQuality? Quality,
    bool Pain,
    bool IsWarmup = false,
    bool Completed = true);

public sealed record WeightSuggestionInput(
    Guid ExerciseId,
    double CurrentWeightKg,
    double MinimumIncrementKg,
    int RepMin,
    int RepMax,
    IReadOnlyList<ProgressionSet> Sets,
    int ConsecutiveFailedSessions = 0);

public sealed record WeightSuggestion(
    Guid ExerciseId,
    ProgressionAction Action,
    double NextWeightKg,
    ProgressionReason Reason);

/// <summary>
/// Android-compatible double-progression algorithm. Records must belong to the
/// exact exercise UUID; alternatives never share weight history implicitly.
/// </summary>
public sealed class WeightSuggestionService
{
    public WeightSuggestion Suggest(WeightSuggestionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var workingSets = (input.Sets ?? Array.Empty<ProgressionSet>())
            .Where(set => !set.IsWarmup && set.Completed)
            .ToArray();

        if (workingSets.Length == 0)
        {
            return Result(
                input,
                ProgressionAction.Hold,
                input.CurrentWeightKg,
                ProgressionReason.NoCompletedWorkingSets);
        }

        if (workingSets.Any(set => set.Pain))
        {
            return Result(
                input,
                ProgressionAction.Hold,
                input.CurrentWeightKg,
                ProgressionReason.PainReported);
        }

        var belowMinimum = workingSets.Count(set => set.Reps < input.RepMin);
        if (belowMinimum * 2 > workingSets.Length)
        {
            return Decrease(input, ProgressionReason.MoreThanHalfBelowLowerBound);
        }

        if (input.ConsecutiveFailedSessions >= 2)
        {
            return Decrease(input, ProgressionReason.TwoConsecutiveFailures);
        }

        var earnedIncrease = workingSets.All(set =>
            set.Reps >= input.RepMax &&
            set.Quality == MovementQuality.Good &&
            set.Rir is >= 1);

        if (earnedIncrease)
        {
            return Result(
                input,
                ProgressionAction.Increase,
                input.CurrentWeightKg + input.MinimumIncrementKg,
                ProgressionReason.AllWorkingSetsAtUpperBound);
        }

        return Result(
            input,
            ProgressionAction.Hold,
            input.CurrentWeightKg,
            ProgressionReason.KeepBuildingReps);
    }

    private static void Validate(WeightSuggestionInput input)
    {
        if (input.ExerciseId == Guid.Empty)
        {
            throw new ArgumentException("ExerciseId is required.", nameof(input));
        }

        if (!double.IsFinite(input.CurrentWeightKg) || input.CurrentWeightKg < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.CurrentWeightKg,
                "CurrentWeightKg cannot be negative and must be finite.");
        }

        if (!double.IsFinite(input.MinimumIncrementKg) || input.MinimumIncrementKg <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.MinimumIncrementKg,
                "MinimumIncrementKg must be positive and finite.");
        }

        if (input.RepMin <= 0 || input.RepMax < input.RepMin)
        {
            throw new ArgumentException("Rep range is invalid.", nameof(input));
        }

        if (input.ConsecutiveFailedSessions < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.ConsecutiveFailedSessions,
                "ConsecutiveFailedSessions cannot be negative.");
        }
    }

    private static WeightSuggestion Decrease(
        WeightSuggestionInput input,
        ProgressionReason reason) =>
        Result(
            input,
            ProgressionAction.Decrease,
            Math.Max(0, input.CurrentWeightKg - input.MinimumIncrementKg),
            reason);

    private static WeightSuggestion Result(
        WeightSuggestionInput input,
        ProgressionAction action,
        double nextWeightKg,
        ProgressionReason reason) =>
        new(input.ExerciseId, action, nextWeightKg, reason);
}

public sealed record ProgressionInput(
    Guid ExerciseId,
    double CurrentWeightKg,
    double MinimumIncrementKg,
    int RepMin,
    int RepMax,
    IReadOnlyList<ProgressionSet> Sets,
    int ConsecutiveFailedSessions = 0);

public sealed record ProgressionRecommendation(
    Guid ExerciseId,
    ProgressionAction Action,
    double NextWeightKg,
    ProgressionReason Reason);

/// <summary>Android naming-compatible pure-function facade.</summary>
public static class DoubleProgressionEngine
{
    public static ProgressionRecommendation Recommend(ProgressionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var suggestion = new WeightSuggestionService().Suggest(new WeightSuggestionInput(
            input.ExerciseId,
            input.CurrentWeightKg,
            input.MinimumIncrementKg,
            input.RepMin,
            input.RepMax,
            input.Sets,
            input.ConsecutiveFailedSessions));

        return new ProgressionRecommendation(
            suggestion.ExerciseId,
            suggestion.Action,
            suggestion.NextWeightKg,
            suggestion.Reason);
    }
}

public sealed record ExerciseWeightRecord(
    Guid ExerciseId,
    DateTimeOffset CompletedAt,
    double WeightKg,
    int Reps,
    Guid? EquipmentId = null,
    Guid? SourceOptionId = null);

public static class ExerciseWeightHistory
{
    /// <summary>
    /// Finds history for the exact movement configuration. When a source option
    /// UUID is available it is the strongest identity; otherwise both exercise
    /// and equipment UUID must match. This prevents alternatives and different
    /// machines from inheriting one another's working weight.
    /// </summary>
    public static ExerciseWeightRecord? LatestForExercise(
        Guid exerciseId,
        IEnumerable<ExerciseWeightRecord> records,
        Guid? equipmentId = null,
        Guid? sourceOptionId = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (exerciseId == Guid.Empty)
        {
            throw new ArgumentException("Exercise UUID is required.", nameof(exerciseId));
        }

        return records
            .Where(record =>
                record.ExerciseId == exerciseId &&
                (sourceOptionId is not null
                    ? record.SourceOptionId == sourceOptionId
                    : record.EquipmentId == equipmentId))
            .OrderByDescending(record => record.CompletedAt)
            .FirstOrDefault();
    }

    public static ExerciseWeightRecord? LatestForOption(
        ExerciseOption option,
        IEnumerable<ExerciseWeightRecord> records)
    {
        ArgumentNullException.ThrowIfNull(option);
        return LatestForExercise(option.ExerciseId, records, option.EquipmentId, option.Id);
    }
}
