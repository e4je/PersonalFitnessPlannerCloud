using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task SaveAsync_RequiresValidIanaTimeZoneAndIsoTrainingDays()
    {
        using var temporary = new TemporaryDirectory("设置 校验");
        var paths = new AppPaths(temporary.Path);
        var store = new SettingsStore(paths);
        var valid = AppSettingsData.Default(paths.DataDirectory) with
        {
            TimeZone = "Asia/Shanghai",
            TrainingDays = "1,3,5",
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(valid with { TrainingDays = "Mon,Wed,Fri" }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(valid with { TrainingDays = "0,3,8" }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(valid with { TimeZone = "Invalid/TimeZone" }));

        await store.SaveAsync(valid);
        var loaded = await store.GetAsync();
        Assert.Equal("Asia/Shanghai", loaded.TimeZone);
        Assert.Equal("1,3,5", loaded.TrainingDays);
    }
}
