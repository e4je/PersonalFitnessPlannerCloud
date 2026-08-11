using PersonalFitnessPlanner.Core;

namespace PersonalFitnessPlanner.Tests;

public sealed class RecommendationTests
{
    private static readonly DateOnly Monday = new(2026, 8, 10);

    [Fact]
    public void Recommend_FirstSessionStartsWithA_ThenAlternatesAAndB()
    {
        var service = new TodayRecommendationService();

        var first = service.Recommend(new TodayRecommendationInput(Monday, []));
        var afterA = service.Recommend(new TodayRecommendationInput(
            Monday,
            [new CompletedWorkout(Monday.AddDays(-3), PlanDayCode.A)]));
        var afterB = service.Recommend(new TodayRecommendationInput(
            Monday,
            [new CompletedWorkout(Monday.AddDays(-3), PlanDayCode.B)]));

        Assert.Equal(RecommendedSession.A, first.Session);
        Assert.Equal(RecommendationReason.FirstStrengthSession, first.Reason);
        Assert.Equal(RecommendedSession.B, afterA.Session);
        Assert.Equal(PlanDayCode.B, afterA.NextStrengthDay);
        Assert.Equal(RecommendedSession.A, afterB.Session);
        Assert.Equal(PlanDayCode.A, afterB.NextStrengthDay);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Recommend_HighFatigueChoosesRecovery_WithoutLosingNextABDay(int fatigue)
    {
        var input = new TodayRecommendationInput(
            Monday,
            [new CompletedWorkout(Monday.AddDays(-3), PlanDayCode.A)],
            FatigueScore: fatigue);

        var result = new TodayRecommendationService().Recommend(input);

        Assert.Equal(RecommendedSession.Recovery, result.Session);
        Assert.Equal(RecommendationReason.HighFatigue, result.Reason);
        Assert.Equal(PlanDayCode.B, result.NextStrengthDay);
    }

    [Fact]
    public void Recommend_WhenFatigueRecoversBelowThreshold_ResumesExpectedStrengthDay()
    {
        var history = new[] { new CompletedWorkout(Monday.AddDays(-3), PlanDayCode.B) };
        var service = new TodayRecommendationService();

        var tired = service.Recommend(new TodayRecommendationInput(Monday, history, FatigueScore: 8));
        var recovered = service.Recommend(new TodayRecommendationInput(Monday, history, FatigueScore: 7));

        Assert.Equal(RecommendedSession.Recovery, tired.Session);
        Assert.Equal(RecommendedSession.A, recovered.Session);
        Assert.Equal(RecommendationReason.AlternateAfterB, recovered.Reason);
    }

    [Fact]
    public void Recommend_ProtectsRecoveryDayAndWeeklyLimit_ButAllowsManualCardioOverride()
    {
        var service = new TodayRecommendationService();
        var yesterday = service.Recommend(new TodayRecommendationInput(
            Monday,
            [new CompletedWorkout(Monday.AddDays(-1), PlanDayCode.A)]));
        var weeklyLimit = service.Recommend(new TodayRecommendationInput(
            new DateOnly(2026, 8, 14),
            [
                new CompletedWorkout(new DateOnly(2026, 8, 10), PlanDayCode.A),
                new CompletedWorkout(new DateOnly(2026, 8, 12), PlanDayCode.B),
                new CompletedWorkout(new DateOnly(2026, 8, 14), PlanDayCode.A),
            ]));
        var manual = service.Recommend(new TodayRecommendationInput(
            Monday,
            [],
            ManualOverride: RecommendedSession.Cardio));

        Assert.Equal(RecommendationReason.ConsecutiveFullBodyProtection, yesterday.Reason);
        Assert.Equal(RecommendedSession.Recovery, yesterday.Session);
        Assert.Equal(RecommendationReason.WeeklyLimitReached, weeklyLimit.Reason);
        Assert.Equal(RecommendedSession.Recovery, weeklyLimit.Session);
        Assert.Equal(RecommendedSession.Cardio, manual.Session);
        Assert.Equal(RecommendationReason.ManualOverride, manual.Reason);
    }

    [Fact]
    public void Recommend_UsesPlanSpecificFatigueAndMinimumRestRules()
    {
        var service = new TodayRecommendationService();
        var history = new[] { new CompletedWorkout(Monday.AddDays(-2), PlanDayCode.A) };

        var belowPlanThreshold = service.Recommend(new TodayRecommendationInput(
            Monday,
            history,
            FatigueScore: 8,
            MinimumRestDays: 1,
            FatigueThreshold: 9));
        var planThresholdReached = service.Recommend(new TodayRecommendationInput(
            Monday,
            history,
            FatigueScore: 8,
            MinimumRestDays: 1,
            FatigueThreshold: 8));
        var extendedRest = service.Recommend(new TodayRecommendationInput(
            Monday,
            history,
            FatigueScore: 4,
            MinimumRestDays: 2,
            FatigueThreshold: 9));

        Assert.Equal(RecommendedSession.B, belowPlanThreshold.Session);
        Assert.Equal(RecommendationReason.HighFatigue, planThresholdReached.Reason);
        Assert.Equal(RecommendationReason.ConsecutiveFullBodyProtection, extendedRest.Reason);
    }
}
