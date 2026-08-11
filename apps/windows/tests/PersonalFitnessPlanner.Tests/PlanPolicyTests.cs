using System.Security.Claims;
using PersonalFitnessPlanner.Core;

namespace PersonalFitnessPlanner.Tests;

public sealed class PlanPolicyTests
{
    [Fact]
    public void PlanVersionPolicy_PublishedVersionIsImmutable_AndNextDraftGetsNewNestedIds()
    {
        var published = CreateValidPlan(PlanStatus.Published) with
        {
            VersionNumber = 4,
            PublishedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        };
        var policy = new PlanVersionPolicy();

        Assert.Throws<InvalidOperationException>(() => policy.EnsureEditable(published));
        var draft = policy.CreateNextDraft(published);

        Assert.Equal(PlanStatus.Draft, draft.Status);
        Assert.Equal(5, draft.VersionNumber);
        Assert.Equal(published.PlanId, draft.PlanId);
        Assert.NotEqual(published.Id, draft.Id);
        Assert.Null(draft.PublishedAt);
        Assert.Empty(
            published.Days.Select(day => day.Id)
                .Intersect(draft.Days.Select(day => day.Id)));
        Assert.Empty(
            published.Days.SelectMany(day => day.Items).Select(item => item.Id)
                .Intersect(draft.Days.SelectMany(day => day.Items).Select(item => item.Id)));
        Assert.Empty(
            published.Days.SelectMany(day => day.Items).SelectMany(item => item.Options).Select(option => option.Id)
                .Intersect(draft.Days.SelectMany(day => day.Items).SelectMany(item => item.Options).Select(option => option.Id)));

        var republished = policy.Publish(draft, DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        Assert.Equal(PlanStatus.Published, republished.Status);
        Assert.True(policy.CanAssign(republished));
        Assert.False(policy.CanEdit(republished));
    }

    [Fact]
    public void PlanValidator_RejectsDuplicatePositionsAndInvalidSetPrescription()
    {
        var valid = CreateValidPlan();
        var firstDay = valid.Days[0];
        var invalidOption = firstDay.Items[0].Options[0] with { SetCount = 0 };
        var invalidItem = firstDay.Items[0] with { Options = [invalidOption] };
        var duplicate = firstDay.Items[0] with { Id = Guid.NewGuid() };
        var invalid = valid with
        {
            Days = [firstDay with { Items = [invalidItem, duplicate] }, valid.Days[1]],
        };

        var result = new PlanValidator().Validate(invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "plan.position_duplicate");
        Assert.Contains(result.Errors, issue => issue.Code == "plan.set_count_invalid");
    }

    [Fact]
    public void ExerciseOptionSelector_UsesPreferredThenAvailableAlternative_AndRejectsForeignOption()
    {
        var item = CreateValidPlan().Days[0].Items[0];
        var preferred = item.Options.Single(option => option.IsPreferred);
        var alternative = item.Options.Single(option => !option.IsPreferred);
        var selector = new ExerciseOptionSelector();

        Assert.Equal(preferred.Id, selector.Select(item).Id);
        Assert.Equal(
            alternative.Id,
            selector.Select(item, unavailableExerciseIds: [preferred.ExerciseId]).Id);
        Assert.Equal(alternative.Id, selector.Select(item, requestedOptionId: alternative.Id).Id);
        Assert.Throws<ArgumentException>(() => selector.Select(item, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => selector.Select(
            item,
            unavailableExerciseIds: item.Options.Select(option => option.ExerciseId)));
    }

    [Fact]
    public void IntroSetPolicy_UsesTwoSetsForFirstTwoWeeks_ThenFullPrescription()
    {
        var plan = CreateValidPlan();
        var option = plan.Days[0].Items[0].Options[0];
        var assigned = new DateOnly(2026, 8, 3);

        Assert.Equal(2, IntroSetPolicy.GetEffectiveSetCount(plan, option, assigned, assigned));
        Assert.Equal(2, IntroSetPolicy.GetEffectiveSetCount(plan, option, assigned, assigned.AddDays(13)));
        Assert.Equal(3, IntroSetPolicy.GetEffectiveSetCount(plan, option, assigned, assigned.AddDays(14)));
    }

    [Fact]
    public void Authorization_RequiresAuthenticatedAdministratorRoleClaim()
    {
        var adminIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "admin")],
            authenticationType: "Bearer");
        var unauthenticatedAdminIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "admin")]);
        var normalUserIdentity = new ClaimsIdentity(
            [new Claim("roles", "[\"user\",\"athlete\"]")],
            authenticationType: "Bearer");

        Assert.True(AuthorizationPolicy.IsAdministrator(new ClaimsPrincipal(adminIdentity)));
        Assert.False(AuthorizationPolicy.IsAdministrator(new ClaimsPrincipal(unauthenticatedAdminIdentity)));
        Assert.False(AuthorizationPolicy.IsAdministrator(new ClaimsPrincipal(normalUserIdentity)));
        Assert.False(AuthorizationPolicy.IsAdministrator(["user", "management-ui-enabled"]));
        Assert.Throws<UnauthorizedAccessException>(
            () => AuthorizationPolicy.DemandAdministrator(new ClaimsPrincipal(normalUserIdentity)));
    }

    [Fact]
    public void WorkoutSnapshotPolicy_PreventsChangingHistoricalPlanSnapshot()
    {
        var original = CreateWorkout("{\"planVersion\":1,\"exercise\":\"卧推\"}");
        var changed = original with { PlanSnapshotJson = "{\"planVersion\":2}" };

        Assert.True(WorkoutSnapshotPolicy.HasValidSnapshot(original));
        WorkoutSnapshotPolicy.EnsureSnapshotUnchanged(original, original with { Notes = "补充备注" });
        Assert.Throws<InvalidOperationException>(
            () => WorkoutSnapshotPolicy.EnsureSnapshotUnchanged(original, changed));
    }

    private static TrainingPlan CreateValidPlan(PlanStatus status = PlanStatus.Draft)
    {
        var aPreferred = CreateOption("杠铃平板卧推", preferred: true, sortOrder: 0);
        var aAlternative = CreateOption("哑铃平板卧推", preferred: false, sortOrder: 1);
        var bPreferred = CreateOption("胸托划船", preferred: true, sortOrder: 0);
        var days = new[]
        {
            new PlanDay(
                Guid.NewGuid(),
                "A",
                "胸部优先",
                [new PlanItem(Guid.NewGuid(), 1, "胸部", "肩胛稳定", [aPreferred, aAlternative])]),
            new PlanDay(
                Guid.NewGuid(),
                "B",
                "背部优先",
                [new PlanItem(Guid.NewGuid(), 1, "背部", "脊柱中立", [bPreferred])]),
        };
        return new TrainingPlan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "测试 A/B 计划",
            1,
            status,
            days,
            IntroWeeks: 2,
            IntroMaxSets: 2,
            PublishedAt: status == PlanStatus.Published ? DateTimeOffset.UtcNow : null);
    }

    private static ExerciseOption CreateOption(string name, bool preferred, int sortOrder) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            name,
            "测试器械",
            preferred,
            SetCount: 3,
            RepMin: 8,
            RepMax: 12,
            SortOrder: sortOrder,
            IntroSetCount: 2,
            IntroWeeks: 2);

    private static WorkoutSession CreateWorkout(string snapshot) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 8, 9),
            "Asia/Shanghai",
            DateTimeOffset.Parse("2026-08-09T02:00:00Z"),
            WorkoutStatus.Completed,
            [],
            PlanVersionId: Guid.NewGuid(),
            PlanDayCode: "A",
            CompletedAt: DateTimeOffset.Parse("2026-08-09T03:00:00Z"),
            PlanSnapshotJson: snapshot);
}
