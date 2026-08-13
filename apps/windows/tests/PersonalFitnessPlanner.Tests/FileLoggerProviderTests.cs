using Microsoft.Extensions.Logging;
using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Logging;

namespace PersonalFitnessPlanner.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void ConstructorAndWrite_DeleteOnlyExpiredStrictlyNamedApplicationLogs()
    {
        using var temporary = new TemporaryDirectory("日志保留轮转");
        var paths = new AppPaths(temporary.Path);
        paths.EnsureCreated();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var expiredAtConstruction = LogPath(paths, today.AddDays(-FileLoggerProvider.RetentionDays));
        var retentionBoundary = LogPath(paths, today.AddDays(-(FileLoggerProvider.RetentionDays - 1)));
        var futureLog = LogPath(paths, today.AddDays(1));
        var malformed = Path.Combine(paths.LogsDirectory, "app-2000010x.log");
        var unrelated = Path.Combine(paths.LogsDirectory, "notes.log");
        foreach (var path in new[] { expiredAtConstruction, retentionBoundary, futureLog, malformed, unrelated })
        {
            File.WriteAllText(path, "existing");
        }

        using var provider = new FileLoggerProvider(paths);

        Assert.False(File.Exists(expiredAtConstruction));
        Assert.True(File.Exists(retentionBoundary));
        Assert.True(File.Exists(futureLog));
        Assert.True(File.Exists(malformed));
        Assert.True(File.Exists(unrelated));

        var expiredBeforeWrite = LogPath(paths, today.AddDays(-(FileLoggerProvider.RetentionDays + 1)));
        File.WriteAllText(expiredBeforeWrite, "expired after construction");
        provider.CreateLogger("retention-test").LogInformation("write triggers cleanup");

        Assert.False(File.Exists(expiredBeforeWrite));
        Assert.Contains(
            "write triggers cleanup",
            File.ReadAllText(LogPath(paths, today)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LockedExpiredLog_DoesNotBreakStartupOrLoggingAndIsRetriedLater()
    {
        using var temporary = new TemporaryDirectory("锁定旧日志安全清理");
        var paths = new AppPaths(temporary.Path);
        paths.EnsureCreated();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var lockedPath = LogPath(paths, today.AddDays(-(FileLoggerProvider.RetentionDays + 10)));
        File.WriteAllText(lockedPath, "locked");

        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var provider = new FileLoggerProvider(paths))
        {
            provider.CreateLogger("locked-retention-test").LogWarning("logging remains available");
            Assert.True(File.Exists(lockedPath));
            Assert.True(File.Exists(LogPath(paths, today)));
        }

        using (var provider = new FileLoggerProvider(paths))
        {
            provider.CreateLogger("retry-retention-test").LogInformation("cleanup retry");
        }
        Assert.False(File.Exists(lockedPath));
    }

    private static string LogPath(AppPaths paths, DateOnly date) =>
        Path.Combine(paths.LogsDirectory, $"app-{date:yyyyMMdd}.log");
}
