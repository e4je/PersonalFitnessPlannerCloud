namespace PersonalFitnessPlanner.Core;

public enum RecommendedSession
{
    A,
    B,
    Recovery,
    Cardio,
    Rest,
}

public enum RecommendationReason
{
    ManualOverride,
    HighFatigue,
    WeeklyLimitReached,
    ConsecutiveFullBodyProtection,
    FirstStrengthSession,
    AlternateAfterA,
    AlternateAfterB,
}

public sealed record CompletedWorkout(
    DateOnly LocalDate,
    PlanDayCode PlanCode,
    bool IsFullBody = true);

public sealed record TodayRecommendationInput(
    DateOnly Today,
    IReadOnlyList<CompletedWorkout> CompletedWorkouts,
    int? FatigueScore = null,
    int WeeklyLimit = 3,
    RecommendedSession? ManualOverride = null,
    int MinimumRestDays = 1,
    int FatigueThreshold = 8);

public sealed record TodayRecommendation(
    RecommendedSession Session,
    RecommendationReason Reason,
    PlanDayCode NextStrengthDay);

/// <summary>
/// Pure A/B recommendation logic shared with Android. Strength sessions
/// alternate by the last completed A/B workout. The active plan supplies the
/// fatigue threshold, weekly cap and minimum rest interval.
/// </summary>
public sealed class TodayRecommendationService
{
    public TodayRecommendation Recommend(TodayRecommendationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.WeeklyLimit is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.WeeklyLimit,
                "WeeklyLimit must be between 1 and 7.");
        }

        if (input.MinimumRestDays is < 0 or > 14)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.MinimumRestDays,
                "MinimumRestDays must be between 0 and 14.");
        }

        if (input.FatigueThreshold is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.FatigueThreshold,
                "FatigueThreshold must be between 1 and 10.");
        }

        if (input.FatigueScore is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.FatigueScore,
                "FatigueScore must be between 0 and 10.");
        }

        var completed = (input.CompletedWorkouts ?? Array.Empty<CompletedWorkout>())
            .Where(workout => workout.LocalDate <= input.Today)
            .OrderByDescending(workout => workout.LocalDate)
            .ToArray();

        var lastStrength = completed.FirstOrDefault();
        var nextStrengthDay = lastStrength?.PlanCode switch
        {
            PlanDayCode.A => PlanDayCode.B,
            PlanDayCode.B => PlanDayCode.A,
            _ => PlanDayCode.A,
        };

        if (input.ManualOverride is { } manualOverride)
        {
            return new TodayRecommendation(
                manualOverride,
                RecommendationReason.ManualOverride,
                nextStrengthDay);
        }

        if (input.FatigueScore is { } fatigueScore && fatigueScore >= input.FatigueThreshold)
        {
            return Recovery(RecommendationReason.HighFatigue, nextStrengthDay);
        }

        var daysSinceMonday = ((int)input.Today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var weekStart = input.Today.AddDays(-daysSinceMonday);
        var completedThisWeek = completed.Count(workout => workout.LocalDate >= weekStart);
        if (completedThisWeek >= input.WeeklyLimit)
        {
            return Recovery(RecommendationReason.WeeklyLimitReached, nextStrengthDay);
        }

        if (lastStrength is { IsFullBody: true } &&
            (input.Today.DayNumber - lastStrength.LocalDate.DayNumber) <= input.MinimumRestDays)
        {
            return Recovery(
                RecommendationReason.ConsecutiveFullBodyProtection,
                nextStrengthDay);
        }

        return lastStrength?.PlanCode switch
        {
            null => new TodayRecommendation(
                RecommendedSession.A,
                RecommendationReason.FirstStrengthSession,
                PlanDayCode.A),
            PlanDayCode.A => new TodayRecommendation(
                RecommendedSession.B,
                RecommendationReason.AlternateAfterA,
                PlanDayCode.B),
            PlanDayCode.B => new TodayRecommendation(
                RecommendedSession.A,
                RecommendationReason.AlternateAfterB,
                PlanDayCode.A),
            _ => throw new InvalidOperationException("Unknown plan day."),
        };
    }

    public TodayRecommendation Recommend(
        DateOnly today,
        IEnumerable<WorkoutSession> sessions,
        Readiness? readiness = null,
        int weeklyLimit = 3,
        RecommendedSession? manualOverride = null,
        int minimumRestDays = 1,
        int fatigueThreshold = 8)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var completed = sessions
            .Where(session =>
                session.Status == WorkoutStatus.Completed &&
                Enum.TryParse<PlanDayCode>(session.PlanDayCode, true, out _))
            .Select(session => new CompletedWorkout(
                session.LocalDate,
                Enum.Parse<PlanDayCode>(session.PlanDayCode!, true),
                session.IsFullBody))
            .ToArray();

        return Recommend(new TodayRecommendationInput(
            today,
            completed,
            readiness?.FatigueScore,
            weeklyLimit,
            manualOverride,
            minimumRestDays,
            fatigueThreshold));
    }

    private static TodayRecommendation Recovery(
        RecommendationReason reason,
        PlanDayCode nextStrengthDay) =>
        new(RecommendedSession.Recovery, reason, nextStrengthDay);
}

/// <summary>Static compatibility facade for callers that prefer a pure-function API.</summary>
public static class TrainingRecommendationEngine
{
    public static TodayRecommendation Recommend(TodayRecommendationInput input) =>
        new TodayRecommendationService().Recommend(input);
}
