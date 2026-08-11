using PersonalFitnessPlanner.Infrastructure.Data;

namespace PersonalFitnessPlanner.Tests;

public sealed class DefaultPlanTests
{
    private static readonly IReadOnlyDictionary<(string Day, int Position), string[]> ExpectedExerciseNames =
        new Dictionary<(string, int), string[]>
        {
            [("A", 1)] = ["杠铃平板卧推", "史密斯平板卧推", "哑铃平板卧推", "坐姿推胸"],
            [("A", 2)] = ["高位下拉", "辅助引体向上", "对握高位下拉", "自重引体向上"],
            [("A", 3)] = ["坐姿腿举", "哈克深蹲", "史密斯深蹲", "高脚杯深蹲", "杠铃深蹲"],
            [("A", 4)] = ["坐姿腿弯举", "俯卧腿弯举", "站姿单腿弯举", "哑铃罗马尼亚硬拉", "史密斯罗马尼亚硬拉"],
            [("A", 5)] = ["哑铃侧平举", "器械侧平举", "单臂绳索侧平举"],
            [("A", 6)] = ["绳索下压", "直杆下压", "单臂绳索下压", "绳索过头臂屈伸", "三头下压"],
            [("A", 7)] = ["站姿提踵", "腿举机提踵", "史密斯站姿提踵", "单腿站姿提踵"],
            [("A", 8)] = ["绳索卷腹", "器械卷腹", "悬垂屈膝举腿", "反向卷腹", "平板支撑"],
            [("B", 1)] = ["胸托划船", "坐姿绳索划船", "坐姿器械划船", "胸托哑铃划船", "单臂哑铃划船", "杠铃划船"],
            [("B", 2)] = ["上斜哑铃卧推", "史密斯上斜卧推", "上斜杠铃卧推", "上斜器械推胸", "低位绳索夹胸"],
            [("B", 3)] = ["杠铃罗马尼亚硬拉", "史密斯罗马尼亚硬拉", "哑铃罗马尼亚硬拉", "杠铃臀推", "史密斯臀推", "器械臀推", "坐姿腿弯举"],
            [("B", 4)] = ["腿屈伸", "坐姿腿举", "哈克深蹲", "史密斯深蹲", "保加利亚分腿蹲"],
            [("B", 5)] = ["反向蝴蝶机飞鸟", "绳索面拉", "绳索反向飞鸟", "俯身哑铃反向飞鸟", "胸托反向飞鸟"],
            [("B", 6)] = ["哑铃弯举", "锤式弯举", "EZ 杠弯举", "绳索弯举", "牧师凳弯举", "器械二头弯举"],
            [("B", 7)] = ["坐姿提踵", "哑铃坐姿提踵", "腿举机提踵", "单腿站姿提踵"],
            [("B", 8)] = ["器械卷腹", "绳索卷腹", "悬垂屈膝举腿", "反向卷腹", "死虫式", "平板支撑"],
        };

    private static readonly IReadOnlyDictionary<(string Day, int Position), string[]> ExpectedCueFragments =
        new Dictionary<(string, int), string[]>
        {
            [("A", 1)] = ["肩胛后缩", "胸口打开", "手腕中立", "30～60°", "耸肩", "肩膀向前顶"],
            [("A", 2)] = ["胸口微抬", "先沉肩", "髋部", "颈后", "大幅后仰"],
            [("A", 3)] = ["脚掌踩稳", "膝盖跟随脚尖", "腰臀稳定", "骨盆卷起", "猛烈锁膝"],
            [("A", 4)] = ["转轴对齐膝关节", "臀部稳定", "控制回放", "臀部后移", "腰背中立"],
            [("A", 5)] = ["肘部带动", "身体侧前方", "耸肩", "摆动", "夹死肩胛"],
            [("A", 6)] = ["手肘固定", "肩膀下沉", "手腕中立", "躯干压重量"],
            [("A", 7)] = ["前脚掌踩稳", "充分下降和抬起", "顶端停顿", "内外翻"],
            [("A", 8)] = ["肋骨向骨盆靠近", "骨盆稳定", "手臂", "髋屈肌", "塌腰"],
            [("B", 1)] = ["脊柱中立", "肩胛自然前伸和后缩", "肘部向后", "耸肩", "摆动躯干"],
            [("B", 2)] = ["15～30°", "肩胛后缩下沉", "胸口打开", "手腕中立", "肩膀前顶"],
            [("B", 3)] = ["核心收紧", "膝盖微屈", "臀部向后", "重量贴近腿", "腰背中立", "过度后仰"],
            [("B", 4)] = ["膝关节对齐转轴", "腰臀稳定", "控制回放", "猛烈锁膝", "膝盖跟随脚尖"],
            [("B", 5)] = ["躯干稳定", "手肘微屈", "后肩带动", "耸肩", "腰摆动"],
            [("B", 6)] = ["肩膀下沉", "手肘固定", "手腕中立", "后仰", "耸肩", "摆动"],
            [("B", 7)] = ["脚掌稳定", "充分下降和抬高", "脚踝正直", "快速弹动"],
            [("B", 8)] = ["腹部主动收缩", "肋骨下沉", "骨盆稳定", "甩腿", "过度反弓"],
        };

    private static readonly IReadOnlyDictionary<(string Day, int Position), string> ExpectedPrescriptionVectors =
        new Dictionary<(string, int), string>
        {
            [("A", 1)] = "3|8|10|reps;3|8|12|reps;3|8|12|reps;3|8|12|reps",
            [("A", 2)] = "3|8|12|reps;3|8|12|reps;3|8|12|reps;3|1|20|reps_rir_1_2",
            [("A", 3)] = "3|8|12|reps;3|8|12|reps;3|8|12|reps;3|10|15|reps;3|6|10|reps",
            [("A", 4)] = "2|10|15|reps;2|10|15|reps;2|10|15|reps_per_side;2|8|12|reps;2|8|12|reps",
            [("A", 5)] = "2|12|20|reps;2|12|20|reps;2|12|20|reps_per_side",
            [("A", 6)] = "2|10|15|reps;2|10|15|reps;2|10|15|reps_per_side;2|10|15|reps;2|10|15|reps",
            [("A", 7)] = "2|10|15|reps;2|12|20|reps;2|10|15|reps;2|12|20|reps_per_side",
            [("A", 8)] = "2|10|15|reps;2|10|15|reps;2|8|15|reps;2|12|20|reps;2|30|60|seconds",
            [("B", 1)] = "3|8|12|reps;3|8|12|reps;3|8|12|reps;3|8|12|reps;3|8|12|reps_per_side;3|6|10|reps",
            [("B", 2)] = "3|8|12|reps;3|8|12|reps;3|6|10|reps;3|8|12|reps;3|10|15|reps",
            [("B", 3)] = "3|8|10|reps;3|8|12|reps;3|8|12|reps;3|8|12|reps;3|8|12|reps;3|8|12|reps;3|10|15|reps",
            [("B", 4)] = "2|10|15|reps;2|8|12|reps;2|8|12|reps;2|8|12|reps;2|8|12|reps_per_side",
            [("B", 5)] = "2|12|20|reps;2|12|20|reps;2|12|20|reps;2|12|20|reps;2|12|20|reps",
            [("B", 6)] = "2|10|15|reps;2|10|15|reps;2|8|12|reps;2|10|15|reps;2|10|15|reps;2|10|15|reps",
            [("B", 7)] = "2|12|20|reps;2|12|20|reps;2|12|20|reps;2|12|20|reps_per_side",
            [("B", 8)] = "2|10|15|reps;2|10|15|reps;2|8|15|reps;2|12|20|reps;2|8|12|reps_per_side;2|30|60|seconds",
        };

    [Fact]
    public async Task EmbeddedPlan_ContainsCompleteAlternatingAAndBDays()
    {
        var plan = await new DefaultPlanLoader().LoadAsync();

        Assert.Equal(2, plan.DeloadWeeks);
        Assert.Equal(2, plan.DeloadMaxSets);
        Assert.Equal(3, plan.WeeklyStrengthTarget);
        Assert.Equal(1, plan.MinimumRestDays);
        Assert.Equal(8, plan.FatigueThreshold);
        Assert.Equal(new[] { "A", "B" }, plan.Days.Select(day => day.Code).Order().ToArray());
        Assert.All(plan.Days, day =>
        {
            Assert.Equal(8, day.Items.Count);
            Assert.Equal(Enumerable.Range(1, 8), day.Items.OrderBy(item => item.Position).Select(item => item.Position));
            Assert.All(day.Items, item =>
            {
                Assert.Single(item.Options, option => option.IsPreferred);
                Assert.Contains(item.Options, option => !option.IsPreferred);
                Assert.All(item.Options, option =>
                {
                    Assert.True(option.Sets > 0);
                    Assert.InRange(option.RepMin, 1, option.RepMax);
                });
            });
        });
    }

    [Fact]
    public async Task EmbeddedPlan_HasStablePublishedVersionIdentity()
    {
        var loader = new DefaultPlanLoader();

        var first = await loader.LoadAsync();
        var second = await loader.LoadAsync();

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(Guid.Empty, first.PlanId);
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), first.Id);
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000000"), first.PlanId);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal("published", first.Status, ignoreCase: true);
        Assert.Equal(DateTimeOffset.Parse("2026-08-09T00:00:00Z"), first.PublishedAt);
    }

    [Fact]
    public async Task EmbeddedPlan_MatchesEveryRequiredPreferredAndAlternativeExercise()
    {
        var plan = await new DefaultPlanLoader().LoadAsync();

        foreach (var expected in ExpectedExerciseNames)
        {
            var day = plan.Days.Single(day => day.Code == expected.Key.Day);
            var item = day.Items.Single(item => item.Position == expected.Key.Position);
            Assert.Equal(expected.Value, item.Options.Select(option => option.ExerciseName));
            Assert.True(item.Options[0].IsPreferred);
            Assert.All(item.Options.Skip(1), option => Assert.False(option.IsPreferred));
        }

        var allOptions = plan.Days.SelectMany(day => day.Items).SelectMany(item => item.Options).ToArray();
        Assert.Equal(ExpectedExerciseNames.Values.Sum(names => names.Length), allOptions.Length);
        Assert.Equal(allOptions.Length, allOptions.Select(option => option.Id).Distinct().Count());
        Assert.Equal(66, allOptions.Select(option => option.ExerciseId).Distinct().Count());
        Assert.All(
            allOptions.GroupBy(option => option.ExerciseName),
            group => Assert.Single(group.Select(option => option.ExerciseId).Distinct()));
        Assert.All(allOptions, option => Assert.True(option.EquipmentId is { } id && id != Guid.Empty));
        Assert.All(allOptions, option => Assert.False(string.IsNullOrWhiteSpace(option.Equipment)));

        foreach (var expected in ExpectedPrescriptionVectors)
        {
            var options = plan.Days.Single(day => day.Code == expected.Key.Day)
                .Items.Single(item => item.Position == expected.Key.Position)
                .Options;
            Assert.Equal(
                expected.Value.Split(';'),
                options.Select(option => $"{option.Sets}|{option.RepMin}|{option.RepMax}|{option.RepUnit}"));
        }
    }

    [Fact]
    public async Task EmbeddedPlan_PreservesAllRequiredTechniqueAndSafetyCues()
    {
        var plan = await new DefaultPlanLoader().LoadAsync();

        foreach (var expected in ExpectedCueFragments)
        {
            var day = plan.Days.Single(day => day.Code == expected.Key.Day);
            var item = day.Items.Single(item => item.Position == expected.Key.Position);
            var combinedGuidance = item.Cues + "；" + item.CommonMistakes;
            Assert.All(expected.Value, fragment => Assert.Contains(fragment, combinedGuidance));
        }
    }
}
