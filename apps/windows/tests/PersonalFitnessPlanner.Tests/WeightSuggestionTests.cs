using PersonalFitnessPlanner.Core;

namespace PersonalFitnessPlanner.Tests;

public sealed class WeightSuggestionTests
{
    private static readonly Guid ExerciseId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public static TheoryData<WeightSuggestionInput, ProgressionAction, double, ProgressionReason> SharedVectors => new()
    {
        {
            Input(60, [Good(12, 2), Good(12, 1), Good(12, 2)]),
            ProgressionAction.Increase,
            62.5,
            ProgressionReason.AllWorkingSetsAtUpperBound
        },
        {
            Input(60, [Good(12, 2), Good(12, 2) with { Pain = true }]),
            ProgressionAction.Hold,
            60,
            ProgressionReason.PainReported
        },
        {
            Input(60, [Good(7, 2), Good(7, 2), Good(10, 2)]),
            ProgressionAction.Decrease,
            57.5,
            ProgressionReason.MoreThanHalfBelowLowerBound
        },
        {
            Input(60, [Good(9, 0), Good(9, 0), Good(9, 0)], failedSessions: 2),
            ProgressionAction.Decrease,
            57.5,
            ProgressionReason.TwoConsecutiveFailures
        },
        {
            Input(60, [Good(10, 2), Good(11, 2), Good(12, 2)]),
            ProgressionAction.Hold,
            60,
            ProgressionReason.KeepBuildingReps
        },
        {
            Input(60, [Good(12, 2) with { IsWarmup = true }]),
            ProgressionAction.Hold,
            60,
            ProgressionReason.NoCompletedWorkingSets
        },
    };

    [Theory]
    [MemberData(nameof(SharedVectors))]
    public void Suggest_MatchesSharedDoubleProgressionVectors(
        WeightSuggestionInput input,
        ProgressionAction expectedAction,
        double expectedWeight,
        ProgressionReason expectedReason)
    {
        var result = new WeightSuggestionService().Suggest(input);

        Assert.Equal(expectedAction, result.Action);
        Assert.Equal(expectedWeight, result.NextWeightKg, precision: 6);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(input.ExerciseId, result.ExerciseId);
    }

    [Fact]
    public void LatestWeightHistory_IsScopedToExactExercise_NotItsAlternative()
    {
        var alternativeId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var records = new[]
        {
            new ExerciseWeightRecord(ExerciseId, DateTimeOffset.Parse("2026-08-01T10:00:00Z"), 60, 10),
            new ExerciseWeightRecord(alternativeId, DateTimeOffset.Parse("2026-08-03T10:00:00Z"), 80, 10),
            new ExerciseWeightRecord(ExerciseId, DateTimeOffset.Parse("2026-08-02T10:00:00Z"), 62.5, 9),
        };

        var result = ExerciseWeightHistory.LatestForExercise(ExerciseId, records);

        Assert.NotNull(result);
        Assert.Equal(62.5, result.WeightKg);
        Assert.Equal(ExerciseId, result.ExerciseId);
    }

    [Fact]
    public void UnitConversion_RoundTripsAndRoundsToAvailableIncrement()
    {
        var pounds = UnitConverter.KilogramsToPounds(100);
        var kilograms = UnitConverter.PoundsToKilograms(pounds);

        Assert.Equal(220.46226218487757, pounds, precision: 10);
        Assert.Equal(100, kilograms, precision: 10);
        Assert.Equal(62.5, UnitConverter.RoundToIncrement(61.4, 2.5));
    }

    private static WeightSuggestionInput Input(
        double weight,
        IReadOnlyList<ProgressionSet> sets,
        int failedSessions = 0) =>
        new(ExerciseId, weight, 2.5, 8, 12, sets, failedSessions);

    private static ProgressionSet Good(int reps, int rir) =>
        new(reps, rir, MovementQuality.Good, Pain: false);
}
