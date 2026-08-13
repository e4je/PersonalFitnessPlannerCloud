using Microsoft.Extensions.Logging;
using PersonalFitnessPlanner.Core;
using PersonalFitnessPlanner.Infrastructure.Backup;
using PersonalFitnessPlanner.Infrastructure.Data;
using PersonalFitnessPlanner.Infrastructure.Export;
using PersonalFitnessPlanner.Infrastructure.Logging;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Network;
using PersonalFitnessPlanner.Infrastructure.Persistence;
using PersonalFitnessPlanner.Infrastructure.Security;

namespace PersonalFitnessPlanner.Infrastructure;

/// <summary>
/// UI-oriented facade over local persistence, exports, credentials and REST
/// synchronization. The App project can implement its own interface with a
/// mechanical DTO adapter without reversing the project dependency.
/// </summary>
public sealed class AppDataService : IDisposable
{
    private readonly bool _ownsHttpClient;
    private readonly HttpClient _httpClient;
    private readonly FileLoggerProvider _loggerProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public AppDataService(
        AppPaths? paths = null,
        HttpClient? httpClient = null,
        FileLoggerProvider? loggerProvider = null)
    {
        Paths = paths ?? new AppPaths();
        Paths.EnsureCreated();
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _loggerProvider = loggerProvider ?? new FileLoggerProvider(Paths);
        _logger = _loggerProvider.CreateLogger(typeof(AppDataService).FullName!);

        Database = new SqliteDatabase(Paths);
        Repository = new FitnessRepository(Database, new DefaultPlanLoader());
        Settings = new SettingsStore(Paths);
        Tokens = new DpapiTokenStore(Paths);
        ApiClient = new FitnessApiClient(_httpClient, Tokens);
        Sync = new SyncService(Repository, ApiClient);
        Exporter = new ExportService(Repository, Settings);
        Backups = new BackupService(Database, Paths);
    }

    public AppPaths Paths { get; }
    public SqliteDatabase Database { get; }
    public FitnessRepository Repository { get; }
    public SettingsStore Settings { get; }
    public DpapiTokenStore Tokens { get; }
    public FitnessApiClient ApiClient { get; }
    public SyncService Sync { get; }
    public ExportService Exporter { get; }
    public BackupService Backups { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var settings = await Settings.GetAsync(cancellationToken).ConfigureAwait(false);
            await ApiClient.ConfigureBaseAddressAsync(settings.ApiBaseUrl, cancellationToken).ConfigureAwait(false);
            await ReconcileStoredAccountAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            _logger.LogInformation("Infrastructure initialized at {DataDirectory}; schema v{SchemaVersion}.",
                Paths.DataDirectory, await Database.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<DashboardData> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var plan = await Repository.GetCurrentPlanAsync(cancellationToken).ConfigureAwait(false);
        var settings = await Settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var history = await Repository.GetHistoryAsync(limit: 12, cancellationToken: cancellationToken).ConfigureAwait(false);
        var completed = history
            .Where(x => string.Equals(x.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                        Enum.TryParse<PlanDayCode>(x.DayCode, true, out _))
            .Select(x => new CompletedWorkout(x.LocalDate, Enum.Parse<PlanDayCode>(x.DayCode, true)))
            .ToArray();
        var today = GetLocalToday(settings.TimeZone);
        var readiness = await Repository.GetLatestReadinessAsync(cancellationToken).ConfigureAwait(false);
        var weeklyTarget = plan.WeeklyStrengthTarget;
        var recommendation = new TodayRecommendationService().Recommend(new TodayRecommendationInput(
            today,
            completed,
            FatigueScore: readiness?.LocalDate == today ? readiness.FatigueScore : null,
            WeeklyLimit: weeklyTarget,
            MinimumRestDays: plan.MinimumRestDays,
            FatigueThreshold: plan.FatigueThreshold));
        var mondayOffset = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var weekStart = today.AddDays(-mondayOffset);
        var outbox = await Repository.GetOutboxStatusAsync(cancellationToken).ConfigureAwait(false);
        return new DashboardData(
            plan.Name,
            plan.Version,
            RecommendationText(recommendation),
            recommendation.NextStrengthDay.ToString(),
            completed.Count(x => x.LocalDate >= weekStart),
            weeklyTarget,
            outbox.Pending == 0
                ? "已同步"
                : outbox.Failed > 0 && !string.IsNullOrWhiteSpace(outbox.LastError)
                    ? $"同步失败 {outbox.Failed}：{outbox.LastError}"
                    : $"待同步 {outbox.Pending}",
            history.Take(5).ToArray(),
            settings.UnitSystem);
    }

    public async Task<PlanData> GetCurrentPlanAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetCurrentPlanAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActiveWorkoutData?> GetActiveWorkoutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetActiveWorkoutAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActiveWorkoutData> StartWorkoutAsync(string dayCode, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var settings = await Settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.StartWorkoutAsync(
            dayCode,
            GetLocalToday(settings.TimeZone),
            cancellationToken,
            settings.TimeZone).ConfigureAwait(false);
    }

    public async Task<bool> SaveSetAsync(SaveSetInput input, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.SaveSetAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedSetData?> UpdatePreviousSetAsync(
        Guid sessionId,
        Guid planItemId,
        decimal? weightKg,
        int? reps,
        int? rir,
        bool pain,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.UpdatePreviousSetAsync(sessionId, planItemId, weightKg, reps, rir, pain, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SavedSetData?> UpdateHistoricalLastSetAsync(
        Guid sessionId,
        decimal? weightKg,
        int? reps,
        int? rir,
        bool pain,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.UpdateHistoricalLastSetAsync(sessionId, weightKg, reps, rir, pain, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CompleteWorkoutAsync(Guid sessionId, bool endedEarly, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await Repository.CompleteWorkoutAsync(sessionId, endedEarly, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkTodayAsync(string kind, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var settings = await Settings.GetAsync(cancellationToken).ConfigureAwait(false);
        await Repository.MarkTodayAsync(kind, GetLocalToday(settings.TimeZone), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkoutHistoryRow>> GetHistoryAsync(
        DateOnly? from,
        DateOnly? to,
        string? dayCode,
        string? exercise,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetHistoryAsync(from, to, dayCode, exercise, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SoftDeleteWorkoutAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SoftDeleteWorkoutAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportHistoryCsvAsync(string targetDirectory, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Exporter.ExportHistoryCsvAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportDataJsonAsync(string targetDirectory, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Exporter.ExportDataJsonAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportDataJsonAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await Backups.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
        await Exporter.ImportDataJsonAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExerciseLibraryItem>> GetExercisesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetExercisesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveExerciseDraftAsync(ExerciseLibraryItem exercise, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SaveExerciseDraftAsync(exercise, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishExerciseAsync(Guid exerciseId, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        var exercise = (await Repository.GetExercisesAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(x => x.Id == exerciseId)
            ?? throw new InvalidOperationException("动作草稿不存在。");
        await ApiClient.PublishExerciseAsync(exercise, cancellationToken).ConfigureAwait(false);
        await Repository.PublishExerciseAsync(exerciseId, enqueueOutbox: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanData> CreatePlanDraftAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.CreatePlanDraftAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePlanDraftAsync(PlanData plan, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SavePlanDraftAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanData> PublishPlanAsync(PlanData plan, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        await ApiClient.PublishPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        return await Repository.PublishPlanAsync(plan, enqueueOutbox: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task AssignPlanAsync(Guid planVersionId, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        var assignmentId = await ApiClient.AssignPlanAsync(planVersionId, cancellationToken).ConfigureAwait(false);
        await Repository.AssignPlanAsync(planVersionId, assignmentId, enqueueOutbox: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlanData>> GetPlanVersionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetPlanVersionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RollbackAssignmentAsync(Guid planVersionId, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        var assignmentId = await ApiClient.AssignPlanAsync(planVersionId, cancellationToken).ConfigureAwait(false);
        await Repository.AssignPlanAsync(planVersionId, assignmentId, enqueueOutbox: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppSettingsData> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Settings.GetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(AppSettingsData settings, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        SettingsStore.Validate(settings);
        // Rebind (and, when the origin changes, delete credentials) before the
        // new address can be persisted and observed after a restart.
        await ApiClient.ConfigureBaseAddressAsync(settings.ApiBaseUrl, cancellationToken).ConfigureAwait(false);
        await Settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthenticationState> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var authentication = await ApiClient.LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareCurrentAccountScopeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Login has already replaced the credential file. Never leave the new
                // account authenticated while the cache is still owned by the old one.
                await Tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            return authentication;
        }
        finally
        {
            _accountGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApiClient.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountGate.Release();
        }
    }

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await ApiClient.GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareCurrentAccountScopeAsync(cancellationToken).ConfigureAwait(false);
            return await Sync.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountGate.Release();
        }
    }

    public async Task<SyncResult> FullResynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareCurrentAccountScopeAsync(cancellationToken).ConfigureAwait(false);
            return await Sync.FullResynchronizeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accountGate.Release();
        }
    }

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Backups.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxStatusData> GetOutboxStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetOutboxStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveReadinessAsync(DailyReadinessData readiness, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SaveReadinessAsync(readiness, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCardioSessionAsync(CardioSessionData cardio, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SaveCardioSessionAsync(cardio, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveExerciseSetupPreferenceAsync(
        ExerciseSetupPreferenceData preference,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SaveExerciseSetupPreferenceAsync(preference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExerciseSetupPreferenceData?> GetExerciseSetupPreferenceAsync(
        Guid exerciseId,
        string equipmentKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await Repository.GetExerciseSetupPreferenceAsync(exerciseId, equipmentKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WeightSuggestionData> GetWeightSuggestionAsync(
        ExerciseOptionData option,
        decimal minimumIncrementKg = 2.5m,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var history = await Repository.GetExactExerciseHistoryAsync(
            option.ExerciseId, option.Id, option.Equipment, cancellationToken).ConfigureAwait(false);
        var latestWithWeight = history.LastOrDefault(x => x.WeightKg is not null);
        if (latestWithWeight is null)
        {
            return new WeightSuggestionData(null, history.LastOrDefault()?.Reps, 0, "Hold", "NoHistory", history.Any(x => x.Pain));
        }

        var progressionSets = history.Select(set => new ProgressionSet(
            set.Reps ?? 0,
            set.Rir,
            set.Pain ? MovementQuality.Poor : set.Rir is >= 1 and <= 3 ? MovementQuality.Good : MovementQuality.Fair,
            set.Pain)).ToArray();
        var suggestion = new WeightSuggestionService().Suggest(new WeightSuggestionInput(
            option.ExerciseId,
            Convert.ToDouble(latestWithWeight.WeightKg!.Value),
            Convert.ToDouble(minimumIncrementKg),
            option.RepMin,
            option.RepMax,
            progressionSets));
        return new WeightSuggestionData(latestWithWeight.WeightKg, history.LastOrDefault()?.Reps,
            Convert.ToDecimal(suggestion.NextWeightKg), suggestion.Action.ToString(), suggestion.Reason.ToString(),
            history.Any(x => x.Pain));
    }

    public void LogError(Exception exception, string context) =>
        _logger.LogError(exception, "{Context}", context);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initializeGate.Dispose();
        _accountGate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
        _loggerProvider.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized) await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (!(await ApiClient.GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false)).IsAdmin)
        {
            throw new UnauthorizedAccessException("管理功能要求后端 JWT 中的 admin 角色声明。");
        }
    }

    private async Task ReconcileStoredAccountAsync(CancellationToken cancellationToken)
    {
        var stored = await ApiClient.LoadCurrentTokensAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null) return;
        var claims = JwtRoleParser.Parse(stored.AccessToken);
        if (!claims.IsValid || string.IsNullOrWhiteSpace(claims.Subject))
        {
            await Tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            await Repository.PrepareAccountScopeAsync(claims.Subject, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountSwitchBlockedException exception)
        {
            await Tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(exception, "Stored login does not match the owner of pending local data; credentials were cleared.");
        }
    }

    private async Task<AccountScopePreparation> PrepareCurrentAccountScopeAsync(
        CancellationToken cancellationToken)
    {
        var stored = await ApiClient.LoadCurrentTokensAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("请先登录后再同步。");
        var claims = JwtRoleParser.Parse(stored.AccessToken);
        if (!claims.IsValid || string.IsNullOrWhiteSpace(claims.Subject))
            throw new UnauthorizedAccessException("登录令牌缺少用户标识，已拒绝使用未绑定账号的本地缓存。");
        return await Repository.PrepareAccountScopeAsync(claims.Subject, cancellationToken).ConfigureAwait(false);
    }

    private static string RecommendationText(TodayRecommendation recommendation) => recommendation.Session switch
    {
        RecommendedSession.A => "建议训练 A（胸部优先）",
        RecommendedSession.B => "建议训练 B（背部优先）",
        RecommendedSession.Cardio => "建议进行有氧训练",
        RecommendedSession.Rest => "建议休息",
        _ => "建议恢复；避免连续两天全身力量训练"
    };

    private static DateOnly GetLocalToday(string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.Today);
        }
        catch (InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(DateTime.Today);
        }
    }
}
