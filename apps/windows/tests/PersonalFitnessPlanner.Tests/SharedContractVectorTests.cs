using System.Text.Json;
using PersonalFitnessPlanner.Core;

namespace PersonalFitnessPlanner.Tests;

public sealed class SharedContractVectorTests
{
    public static IEnumerable<object[]> RecommendationCaseIds =>
        CaseIds("recommendation-cases.json", item => item.TryGetProperty("today", out _));

    public static IEnumerable<object[]> AdaptationCaseIds =>
        CaseIds("recommendation-cases.json", item => item.TryGetProperty("training_week", out _));

    public static IEnumerable<object[]> PlanVersionPinningCaseIds =>
        CaseIds("recommendation-cases.json", item => item.TryGetProperty("existing_workout", out _));

    public static IEnumerable<object[]> ProgressionRuleCaseIds =>
        CaseIds("progression-cases.json", item => item.TryGetProperty("input", out _));

    public static IEnumerable<object[]> ProgressionHistoryCaseIds =>
        CaseIds("progression-cases.json", item => item.TryGetProperty("history", out _));

    [Theory]
    [MemberData(nameof(RecommendationCaseIds))]
    public void RecommendationRules_MatchSharedCanonicalVectors(string caseId)
    {
        var vector = FindCase("recommendation-cases.json", caseId);
        var completed = vector.GetProperty("completed_workouts").EnumerateArray()
            .Select(item => new CompletedWorkout(
                DateOnly.ParseExact(item.GetProperty("local_date").GetString()!, "yyyy-MM-dd"),
                ParseEnum<PlanDayCode>(item.GetProperty("plan_code").GetString()!),
                !item.TryGetProperty("is_full_body", out var fullBody) || fullBody.GetBoolean()))
            .ToArray();
        var input = new TodayRecommendationInput(
            DateOnly.ParseExact(vector.GetProperty("today").GetString()!, "yyyy-MM-dd"),
            completed,
            vector.TryGetProperty("fatigue_score", out var fatigue) ? fatigue.GetInt32() : null,
            vector.GetProperty("weekly_limit").GetInt32(),
            MinimumRestDays: vector.TryGetProperty("minimum_rest_days", out var minimumRestDays)
                ? minimumRestDays.GetInt32()
                : 1,
            FatigueThreshold: vector.TryGetProperty("fatigue_threshold", out var fatigueThreshold)
                ? fatigueThreshold.GetInt32()
                : 8);

        var result = new TodayRecommendationService().Recommend(input);
        var expected = vector.GetProperty("expected");

        Assert.Equal(ParseEnum<RecommendedSession>(expected.GetProperty("session").GetString()!), result.Session);
        Assert.Equal(ParseEnum<PlanDayCode>(expected.GetProperty("next_strength_day").GetString()!), result.NextStrengthDay);
        Assert.Equal(ParseEnum<RecommendationReason>(expected.GetProperty("reason").GetString()!), result.Reason);
    }

    [Theory]
    [MemberData(nameof(AdaptationCaseIds))]
    public void AdaptationSetRules_MatchSharedCanonicalVectors(string caseId)
    {
        var vector = FindCase("recommendation-cases.json", caseId);
        var assignmentStart = new DateOnly(2026, 8, 3);
        var workoutDate = assignmentStart.AddDays((vector.GetProperty("training_week").GetInt32() - 1) * 7);

        var result = IntroSetPolicy.GetEffectiveSetCount(
            vector.GetProperty("prescribed_sets").GetInt32(),
            vector.GetProperty("adaptation_sets").GetInt32(),
            vector.GetProperty("adaptation_weeks").GetInt32(),
            assignmentStart,
            workoutDate);

        Assert.Equal(vector.GetProperty("expected").GetProperty("effective_sets").GetInt32(), result);
    }

    [Theory]
    [MemberData(nameof(PlanVersionPinningCaseIds))]
    public void WorkoutPlanVersionPinning_MatchesSharedCanonicalVectors(string caseId)
    {
        var vector = FindCase("recommendation-cases.json", caseId);
        var existing = vector.GetProperty("existing_workout").GetProperty("plan_version_id").GetGuid();
        var assigned = vector.GetProperty("new_assignment").GetProperty("plan_version_id").GetGuid();
        var expected = vector.GetProperty("expected");

        Assert.Equal(
            expected.GetProperty("existing_workout_plan_version_id").GetGuid(),
            WorkoutSnapshotPolicy.ResolvePlanVersion(existing, assigned));
        Assert.Equal(
            expected.GetProperty("next_workout_plan_version_id").GetGuid(),
            WorkoutSnapshotPolicy.ResolvePlanVersion(null, assigned));
    }

    [Theory]
    [MemberData(nameof(ProgressionRuleCaseIds))]
    public void DoubleProgressionRules_MatchSharedCanonicalVectors(string caseId)
    {
        var vector = FindCase("progression-cases.json", caseId);
        var source = vector.GetProperty("input");
        var sets = source.GetProperty("sets").EnumerateArray().Select(item => new ProgressionSet(
            item.GetProperty("reps").GetInt32(),
            item.TryGetProperty("rir", out var rir) && rir.ValueKind != JsonValueKind.Null ? rir.GetInt32() : null,
            item.TryGetProperty("quality", out var quality) && quality.ValueKind != JsonValueKind.Null
                ? ParseEnum<MovementQuality>(quality.GetString()!)
                : null,
            item.GetProperty("pain").GetBoolean())).ToArray();
        var input = new ProgressionInput(
            source.GetProperty("exercise_id").GetGuid(),
            source.GetProperty("current_weight_kg").GetDouble(),
            source.GetProperty("minimum_increment_kg").GetDouble(),
            source.GetProperty("rep_min").GetInt32(),
            source.GetProperty("rep_max").GetInt32(),
            sets,
            source.GetProperty("consecutive_failed_sessions").GetInt32());

        var result = DoubleProgressionEngine.Recommend(input);
        var expected = vector.GetProperty("expected");

        Assert.Equal(ParseEnum<ProgressionAction>(expected.GetProperty("action").GetString()!), result.Action);
        Assert.Equal(expected.GetProperty("next_weight_kg").GetDouble(), result.NextWeightKg, precision: 6);
        Assert.Equal(ParseEnum<ProgressionReason>(expected.GetProperty("reason").GetString()!), result.Reason);
    }

    [Theory]
    [MemberData(nameof(ProgressionHistoryCaseIds))]
    public void ExerciseHistoryIdentity_MatchesSharedCanonicalVectors(string caseId)
    {
        var vector = FindCase("progression-cases.json", caseId);
        var history = vector.GetProperty("history").EnumerateArray().Select((item, index) => new ExerciseWeightRecord(
            item.GetProperty("exercise_id").GetGuid(),
            DateTimeOffset.UtcNow.AddMinutes(index),
            item.GetProperty("weight_kg").GetDouble(),
            1,
            SourceOptionId: item.TryGetProperty("source_option_id", out var optionId) ? optionId.GetGuid() : null)).ToArray();
        var query = vector.GetProperty("query");

        var result = ExerciseWeightHistory.LatestForExercise(
            query.GetProperty("exercise_id").GetGuid(),
            history,
            sourceOptionId: query.TryGetProperty("source_option_id", out var sourceOptionId)
                ? sourceOptionId.GetGuid()
                : null);

        var expected = vector.GetProperty("expected").GetProperty("latest_weight_kg");
        if (expected.ValueKind == JsonValueKind.Null) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.Equal(expected.GetDouble(), result.WeightKg, precision: 6);
        }
    }

    private static IEnumerable<object[]> CaseIds(string fileName, Func<JsonElement, bool> predicate)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractPath(fileName)));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(predicate)
            .Select(item => new object[] { item.GetProperty("id").GetString()! })
            .ToArray();
    }

    private static JsonElement FindCase(string fileName, string id)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractPath(fileName)));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Single(item => string.Equals(item.GetProperty("id").GetString(), id, StringComparison.Ordinal))
            .Clone();
    }

    private static string ContractPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", fileName);

    private static T ParseEnum<T>(string canonical) where T : struct, Enum
    {
        var normalized = canonical.Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.GetValues<T>().Single(value =>
            string.Equals(value.ToString(), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
