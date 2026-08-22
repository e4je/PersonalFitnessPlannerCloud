using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFitnessPlanner.App.Services;

namespace PersonalFitnessPlanner.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "就绪";

    protected async Task RunAsync(Func<Task> operation, string successMessage)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            await operation();
            StatusMessage = successMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            OnError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event EventHandler<Exception>? Error;
    protected void OnError(Exception exception) => Error?.Invoke(this, exception);
}

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    private readonly AppRuntimeOptions _runtime;

    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private string globalStatus = "本地模式";
    [ObservableProperty] private string footerStatus = "正在初始化…";
    [ObservableProperty] private bool initializationSucceeded;
    [ObservableProperty] private bool personalDataAvailable;

    public string DataDirectory => _runtime.DataDirectory;
    public DashboardViewModel Dashboard { get; }
    public WorkoutViewModel Workout { get; }
    public HistoryViewModel History { get; }
    public ExerciseLibraryViewModel Exercises { get; }
    public PlanEditorViewModel PlanEditor { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(IAppDataService data, AppRuntimeOptions runtime)
    {
        _data = data;
        _runtime = runtime;
        Dashboard = new DashboardViewModel(data, OpenWorkoutAsync);
        Workout = new WorkoutViewModel(data);
        History = new HistoryViewModel(data, runtime.DataDirectory);
        Exercises = new ExerciseLibraryViewModel(data);
        PlanEditor = new PlanEditorViewModel(data);
        Settings = new SettingsViewModel(data, runtime.DataDirectory);
        Settings.AuthenticationChanged += HandleAuthenticationChangedAsync;

        foreach (var child in new ViewModelBase[] { Dashboard, Workout, History, Exercises, PlanEditor, Settings })
        {
            child.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(StatusMessage)) FooterStatus = child.StatusMessage;
            };
            child.Error += (_, ex) => _data.LogError(ex, child.GetType().Name);
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            await _data.InitializeAsync();
            await Settings.LoadAsync();
            if (!CanShowPersonalData)
            {
                ClearPersonalData();
                GlobalStatus = "未登录";
                FooterStatus = "请先登录；个人健康数据已隐藏";
                InitializationSucceeded = true;
                return;
            }

            if (!_runtime.Offline && Settings.AutomaticSync && Settings.IsAuthenticated)
            {
                var sync = await _data.SynchronizeAsync();
                GlobalStatus = sync.Message;
            }
            await RefreshPersonalDataAsync();
            PersonalDataAvailable = true;
            GlobalStatus = _runtime.Offline ? "离线模式" : GlobalStatus == "本地模式" ? Dashboard.SyncStatus : GlobalStatus;
            FooterStatus = "初始化完成";
            InitializationSucceeded = true;
        }
        catch (Exception ex)
        {
            FooterStatus = $"初始化失败：{ex.Message}";
            InitializationSucceeded = false;
            _data.LogError(ex, "ApplicationInitialization");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenWorkoutAsync(string dayCode)
    {
        if (!CanShowPersonalData)
        {
            ClearPersonalData();
            FooterStatus = "请先登录后开始训练";
            return;
        }
        SelectedTabIndex = 1;
        await Workout.StartOrResumeAsync(dayCode);
    }

    private bool CanShowPersonalData => _runtime.Offline || Settings.IsAuthenticated;

    private async Task HandleAuthenticationChangedAsync()
    {
        // Authentication callbacks are awaited by SettingsViewModel. Clearing before
        // the first await prevents the previous subject from remaining visible while
        // the new subject's bootstrap is in flight.
        PersonalDataAvailable = false;
        ClearPersonalData();
        if (!CanShowPersonalData)
        {
            GlobalStatus = "未登录";
            FooterStatus = "已退出登录；个人健康数据已隐藏";
            return;
        }

        try
        {
            IsBusy = true;
            if (!_runtime.Offline)
            {
                var sync = await _data.SynchronizeAsync();
                GlobalStatus = sync.Message;
            }
            await RefreshPersonalDataAsync();
            PersonalDataAvailable = true;
            FooterStatus = _runtime.Offline ? "离线数据已加载" : "账户数据已安全加载";
        }
        catch (Exception ex)
        {
            ClearPersonalData();
            FooterStatus = $"账户数据加载失败：{ex.Message}";
            _data.LogError(ex, "AuthenticationDataRefresh");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task RefreshPersonalDataAsync() => Task.WhenAll(
        Dashboard.RefreshAsync(),
        Workout.LoadAsync(),
        History.RefreshAsync(),
        Exercises.RefreshAsync(),
        PlanEditor.LoadAsync());

    private void ClearPersonalData()
    {
        Dashboard.ClearForAuthenticationLoss();
        Workout.ClearForAuthenticationLoss();
        History.ClearForAuthenticationLoss();
        Exercises.ClearForAuthenticationLoss();
        PlanEditor.ClearForAuthenticationLoss();
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (!PersonalDataAvailable)
        {
            ClearPersonalData();
            GlobalStatus = "未登录";
            FooterStatus = "请先登录后同步";
            return;
        }
        await RunAsync(async () =>
        {
            var result = await _data.SynchronizeAsync();
            GlobalStatus = result.Message;
            await RefreshPersonalDataAsync();
        }, "同步完成");
    }

    [RelayCommand]
    private async Task RefreshDashboardAsync()
    {
        if (!PersonalDataAvailable)
        {
            ClearPersonalData();
            FooterStatus = "请先登录后刷新";
            return;
        }
        await Dashboard.RefreshAsync();
    }

    [RelayCommand]
    private void ShowShortcuts()
    {
        FooterStatus = "快捷键：Ctrl+Enter/Ctrl+S 保存组；Ctrl+Z 修改上一组；Space 计时；Ctrl+R 休息；Ctrl+Shift+C 有氧；Ctrl+Shift+S 同步；Ctrl+E 提前结束；Esc 返回首页（输入框内保留编辑按键）";
    }

    public async Task HandleClosingAsync()
    {
        if (Settings.HasUnsavedChanges) await Settings.SaveAsync();
    }
}

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    private readonly Func<string, Task> _openWorkout;
    private DateOnly _currentLocalDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty] private string dateText = "";
    [ObservableProperty] private string weekdayText = "";
    [ObservableProperty] private string recommendation = "加载中";
    [ObservableProperty] private string planName = "—";
    [ObservableProperty] private string planVersionText = "版本 —";
    [ObservableProperty] private string progressText = "0 / 3";
    [ObservableProperty] private double progressPercent;
    [ObservableProperty] private string nextDay = "A";
    [ObservableProperty] private string syncStatus = "未同步";
    [ObservableProperty] private string fatigueScoreText = "5";
    [ObservableProperty] private string sleepQualityText = "3";
    [ObservableProperty] private string painNotes = "";
    public ObservableCollection<WorkoutHistoryRow> RecentWorkouts { get; } = [];

    public DashboardViewModel(IAppDataService data, Func<string, Task> openWorkout)
    {
        _data = data;
        _openWorkout = openWorkout;
    }

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(LoadDashboardDataAsync, "首页已刷新");

    public void ClearForAuthenticationLoss()
    {
        Recommendation = "登录后查看今日建议";
        PlanName = "—";
        PlanVersionText = "版本 —";
        ProgressText = "0 / 0";
        ProgressPercent = 0;
        NextDay = "A";
        SyncStatus = "未登录";
        FatigueScoreText = "";
        SleepQualityText = "";
        PainNotes = "";
        RecentWorkouts.Clear();
        StatusMessage = "个人健康数据已隐藏";
    }

    private async Task LoadDashboardDataAsync()
    {
        var settings = await _data.GetSettingsAsync();
        var today = DateTimeOffset.Now;
        try
        {
            today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone));
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        DateText = today.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"));
        WeekdayText = today.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"));
        _currentLocalDate = DateOnly.FromDateTime(today.DateTime);
        var dashboard = await _data.GetDashboardAsync();
        Recommendation = dashboard.Recommendation;
        PlanName = dashboard.PlanName;
        PlanVersionText = $"版本 {dashboard.PlanVersion}";
        ProgressText = $"{dashboard.CompletedThisWeek} / {dashboard.WeeklyTarget}";
        ProgressPercent = dashboard.WeeklyTarget == 0 ? 0 : dashboard.CompletedThisWeek * 100d / dashboard.WeeklyTarget;
        NextDay = dashboard.NextDay;
        SyncStatus = dashboard.SyncStatus;
        RecentWorkouts.ReplaceWith(dashboard.RecentWorkouts);
    }

    [RelayCommand]
    private Task StartWorkoutAsync() => _openWorkout(NextDay is "A" or "B" ? NextDay : "A");

    [RelayCommand]
    private async Task MarkRestAsync()
    {
        await RunAsync(async () =>
        {
            await _data.MarkTodayAsync("REST");
            await LoadDashboardDataAsync();
        }, "今天已标记为休息");
    }

    [RelayCommand]
    private async Task MarkCardioAsync()
    {
        await RunAsync(async () =>
        {
            await _data.MarkTodayAsync("CARDIO");
            await LoadDashboardDataAsync();
        }, "今天已改为有氧");
    }

    [RelayCommand]
    private async Task SaveReadinessAsync()
    {
        if (!int.TryParse(FatigueScoreText, out var fatigue) || fatigue is < 1 or > 10)
        {
            StatusMessage = "疲劳分数必须是 1～10";
            return;
        }
        int? sleep = int.TryParse(SleepQualityText, out var parsedSleep) ? parsedSleep : null;
        if (sleep is < 1 or > 5)
        {
            StatusMessage = "睡眠质量必须是 1～5";
            return;
        }
        await RunAsync(async () =>
        {
            await _data.SaveReadinessAsync(new DailyReadinessData(
                Guid.NewGuid(), _currentLocalDate, fatigue, sleep, PainNotes, "Windows 首页状态"));
            await LoadDashboardDataAsync();
        }, "今日状态已保存，建议已重新计算");
    }
}

public sealed partial class WorkoutOptionViewModel : ObservableObject
{
    public ExerciseOptionData Data { get; }
    public Guid Id => Data.Id;
    public string ExerciseName => Data.ExerciseName;
    public string Equipment => Data.Equipment;
    public string DisplayName => $"{(Data.IsPreferred ? "首选" : "替代")}：{Data.ExerciseName}｜{Data.Equipment}";
    public string Prescription => $"{Data.Sets}×{Data.RepMin}–{Data.RepMax}{(Data.RepUnit == "seconds" ? "秒" : "")}";
    public WorkoutOptionViewModel(ExerciseOptionData data) => Data = data;
}

public sealed partial class WorkoutItemViewModel : ObservableObject
{
    public Guid Id { get; }
    public int Position { get; }
    public string BodyPart { get; }
    public string Cues { get; }
    public string CommonMistakes { get; }
    public ObservableCollection<WorkoutOptionViewModel> Options { get; }

    [ObservableProperty] private WorkoutOptionViewModel? selectedOption;
    [ObservableProperty] private int completedSets;
    [ObservableProperty] private string seatPosition;
    [ObservableProperty] private string benchAngle;
    [ObservableProperty] private string machineNumber;
    [ObservableProperty] private string lastRecord = "上次记录：暂无";
    [ObservableProperty] private string suggestion = "本次建议：从舒适重量开始，保留 2～3 次余力";
    public event EventHandler? SelectedOptionChanged;

    public string Prescription => SelectedOption?.Prescription ?? "—";

    public WorkoutItemViewModel(PlanItemData item)
    {
        Id = item.Id;
        Position = item.Position;
        BodyPart = item.BodyPart;
        Cues = item.Cues;
        CommonMistakes = item.CommonMistakes;
        SeatPosition = item.SeatPosition;
        BenchAngle = item.BenchAngle;
        MachineNumber = item.MachineNumber;
        Options = new ObservableCollection<WorkoutOptionViewModel>(item.Options.Select(x => new WorkoutOptionViewModel(x)));
        SelectedOption = Options.FirstOrDefault(x => x.Data.IsPreferred) ?? Options.FirstOrDefault();
    }

    partial void OnSelectedOptionChanged(WorkoutOptionViewModel? value)
    {
        OnPropertyChanged(nameof(Prescription));
        SelectedOptionChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class WorkoutViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    private readonly DispatcherTimer _timer;
    private Guid? _sessionId;
    private int _elapsedSeconds;

    [ObservableProperty] private string sessionTitle = "尚未开始训练";
    [ObservableProperty] private string resumeText = "";
    [ObservableProperty] private WorkoutItemViewModel? selectedItem;
    [ObservableProperty] private string weightText = "";
    [ObservableProperty] private string repsText = "";
    [ObservableProperty] private string rirText = "2";
    [ObservableProperty] private bool pain;
    [ObservableProperty] private string setNotes = "";
    [ObservableProperty] private string timerText = "00:00";
    [ObservableProperty] private bool timerRunning;
    [ObservableProperty] private string unitLabel = "kg";
    private bool _usePounds;
    public ObservableCollection<WorkoutItemViewModel> Items { get; } = [];

    public WorkoutViewModel(IAppDataService data)
    {
        _data = data;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _elapsedSeconds++;
            TimerText = TimeSpan.FromSeconds(_elapsedSeconds).ToString(@"mm\:ss");
        }, Dispatcher.CurrentDispatcher);
        _timer.Stop();
    }

    public async Task LoadAsync()
    {
        var settings = await _data.GetSettingsAsync();
        _usePounds = settings.UnitSystem.Equals("LB", StringComparison.OrdinalIgnoreCase);
        UnitLabel = _usePounds ? "lb" : "kg";
        var active = await _data.GetActiveWorkoutAsync();
        if (active is not null) LoadActive(active, true);
        else
        {
            var plan = await _data.GetCurrentPlanAsync();
            LoadPreview(plan, "A");
        }
        await RefreshAllItemContextAsync();
    }

    public void ClearForAuthenticationLoss()
    {
        _timer.Stop();
        _elapsedSeconds = 0;
        _sessionId = null;
        Items.Clear();
        SelectedItem = null;
        SessionTitle = "请先登录";
        ResumeText = "登录后加载训练";
        WeightText = "";
        RepsText = "";
        RirText = "2";
        Pain = false;
        SetNotes = "";
        TimerText = "00:00";
        TimerRunning = false;
        StatusMessage = "训练数据已隐藏";
    }

    [RelayCommand]
    private Task StartOrResumeAsync() => StartOrResumeAsync("A");

    public async Task StartOrResumeAsync(string dayCode)
    {
        await RunAsync(async () =>
        {
            var active = await _data.GetActiveWorkoutAsync();
            if (active is null) active = await _data.StartWorkoutAsync(dayCode);
            LoadActive(active, active.SavedSets.Count > 0);
            await RefreshAllItemContextAsync();
        }, "训练已开始，所有记录将即时保存");
    }

    private void LoadPreview(PlanData plan, string dayCode)
    {
        var day = plan.Days.FirstOrDefault(x => x.Code.Equals(dayCode, StringComparison.OrdinalIgnoreCase)) ?? plan.Days.First();
        ReplaceWorkoutItems(day.Items);
        SelectedItem = Items.FirstOrDefault();
        SessionTitle = $"{plan.Name} · {day.Code}（预览）";
        ResumeText = "点击“开始/恢复”创建训练";
    }

    private void LoadActive(ActiveWorkoutData active, bool resumed)
    {
        _sessionId = active.SessionId;
        var day = active.Snapshot.Days.First(x => x.Code.Equals(active.DayCode, StringComparison.OrdinalIgnoreCase));
        ReplaceWorkoutItems(day.Items);
        foreach (var item in Items)
        {
            var sets = active.SavedSets.Where(x => x.PlanItemId == item.Id).OrderBy(x => x.SetNumber).ToArray();
            item.CompletedSets = sets.Length;
            var last = sets.LastOrDefault();
            if (last is not null) item.LastRecord = $"本次上一组：{FromKilograms(last.WeightKg):0.##} {UnitLabel} × {last.Reps ?? last.DurationSeconds}";
        }
        SelectedItem = Items.FirstOrDefault(x => x.CompletedSets < (x.SelectedOption?.Data.Sets ?? 0)) ?? Items.LastOrDefault();
        SessionTitle = $"{active.Snapshot.Name} v{active.Snapshot.Version} · {active.DayCode} · {active.LocalDate}";
        ResumeText = resumed ? "已恢复未完成训练" : "自动保存已开启";
    }

    [RelayCommand]
    private async Task SaveSetAsync()
    {
        if (_sessionId is null || SelectedItem?.SelectedOption is null)
        {
            StatusMessage = "请先开始训练并选择动作";
            return;
        }

        await RunAsync(async () =>
        {
            var option = SelectedItem.SelectedOption.Data;
            var setNumber = SelectedItem.CompletedSets + 1;
            var displayWeight = ParseDecimal(WeightText);
            var weight = ToKilograms(displayWeight);
            var value = ParseInt(RepsText);
            var request = new SaveSetRequest(
                _sessionId.Value,
                SelectedItem.Id,
                option,
                setNumber,
                option.RepUnit == "seconds" ? null : weight,
                option.RepUnit == "seconds" ? null : value,
                option.RepUnit == "seconds" ? value : null,
                ParseInt(RirText),
                Pain,
                SetNotes,
                $"{_sessionId:D}:{SelectedItem.Id:D}:{option.Id:D}:{setNumber}");
            var inserted = await _data.SaveSetAsync(request);
            if (!inserted)
            {
                StatusMessage = "该组已保存，已阻止重复记录";
                return;
            }
            await _data.SaveExerciseSetupPreferenceAsync(new ExerciseSetupPreferenceData(
                option.ExerciseId,
                option.Equipment,
                SelectedItem.SeatPosition,
                SelectedItem.BenchAngle,
                SelectedItem.MachineNumber,
                "Windows 训练执行页自动保存"));
            SelectedItem.CompletedSets++;
            SelectedItem.LastRecord = $"本次上一组：{displayWeight:0.##} {UnitLabel} × {value}";
            SetNotes = "";
            Pain = false;
            ResetTimer();
            ToggleTimer();
            MoveToNextCompletedTarget();
        }, "本组已自动保存并加入同步队列");
    }

    [RelayCommand]
    private async Task UpdatePreviousSetAsync()
    {
        if (_sessionId is null || SelectedItem is null) return;
        await RunAsync(async () =>
        {
            var updated = await _data.UpdatePreviousSetAsync(_sessionId.Value, SelectedItem.Id, ToKilograms(ParseDecimal(WeightText)), ParseInt(RepsText), ParseInt(RirText), Pain);
            if (updated is null) throw new InvalidOperationException("没有可修改的上一组记录");
            SelectedItem.LastRecord = $"本次上一组：{FromKilograms(updated.WeightKg):0.##} {UnitLabel} × {updated.Reps ?? updated.DurationSeconds}";
        }, "上一组已修改并重新排队同步");
    }

    private void MoveToNextCompletedTarget()
    {
        if (SelectedItem is null || SelectedItem.CompletedSets < (SelectedItem.SelectedOption?.Data.Sets ?? int.MaxValue)) return;
        var next = Items.FirstOrDefault(x => x.Position > SelectedItem.Position && x.CompletedSets < (x.SelectedOption?.Data.Sets ?? 0));
        if (next is not null) SelectedItem = next;
    }

    [RelayCommand]
    private async Task CompleteWorkoutAsync() => await FinishWorkoutAsync(false);

    [RelayCommand]
    private async Task EndEarlyAsync() => await FinishWorkoutAsync(true);

    private async Task FinishWorkoutAsync(bool early)
    {
        if (_sessionId is null) return;
        await RunAsync(async () =>
        {
            await _data.CompleteWorkoutAsync(_sessionId.Value, early);
            _sessionId = null;
            _timer.Stop();
            SessionTitle = early ? "训练已提前结束" : "训练已完成";
            ResumeText = "记录已保存，等待同步";
        }, early ? "训练已安全中断" : "训练完成");
    }

    [RelayCommand]
    private void ToggleTimer()
    {
        TimerRunning = !TimerRunning;
        if (TimerRunning) _timer.Start(); else _timer.Stop();
    }

    [RelayCommand]
    private void ResetTimer()
    {
        _timer.Stop();
        TimerRunning = false;
        _elapsedSeconds = 0;
        TimerText = "00:00";
    }

    private static decimal? ParseDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var local) ||
               decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out local) ? local : null;
    }

    private static int? ParseInt(string value) => int.TryParse(value, out var parsed) ? parsed : null;

    private decimal? ToKilograms(decimal? value) => value is null ? null : _usePounds ? value / 2.2046226218m : value;
    private decimal? FromKilograms(decimal? value) => value is null ? null : _usePounds ? value * 2.2046226218m : value;

    partial void OnSelectedItemChanged(WorkoutItemViewModel? value)
    {
        WeightText = "";
        RepsText = "";
        RirText = "2";
        Pain = false;
        if (value is not null) _ = LoadItemContextSafelyAsync(value, true);
    }

    private void ReplaceWorkoutItems(IEnumerable<PlanItemData> items)
    {
        Items.Clear();
        foreach (var data in items)
        {
            var item = new WorkoutItemViewModel(data);
            item.SelectedOptionChanged += OnWorkoutOptionChanged;
            Items.Add(item);
        }
    }

    private async void OnWorkoutOptionChanged(object? sender, EventArgs e)
    {
        if (sender is WorkoutItemViewModel item)
        {
            var selected = ReferenceEquals(item, SelectedItem);
            if (selected) WeightText = "";
            await LoadItemContextSafelyAsync(item, selected);
        }
    }

    private Task RefreshAllItemContextAsync() =>
        Task.WhenAll(Items.Select(item => LoadItemContextSafelyAsync(item, ReferenceEquals(item, SelectedItem))));

    private async Task LoadItemContextSafelyAsync(WorkoutItemViewModel item, bool updateInput)
    {
        try
        {
            var option = item.SelectedOption?.Data;
            if (option is null) return;
            var suggestionTask = _data.GetWeightSuggestionAsync(option);
            var preferenceTask = _data.GetExerciseSetupPreferenceAsync(option.ExerciseId, option.Equipment);
            await Task.WhenAll(suggestionTask, preferenceTask);
            if (item.SelectedOption?.Id != option.Id) return;

            var suggestion = await suggestionTask;
            var preference = await preferenceTask;
            if (preference is not null)
            {
                item.SeatPosition = preference.SeatPosition;
                item.BenchAngle = preference.BenchAngle;
                item.MachineNumber = preference.MachineNumber;
            }
            if (item.CompletedSets == 0)
            {
                item.LastRecord = suggestion.LastWeightKg is null
                    ? "上次记录：暂无（替代动作与器械独立计算）"
                    : $"上次记录：{FromKilograms(suggestion.LastWeightKg):0.##} {UnitLabel} × {suggestion.LastReps}";
            }
            item.Suggestion = suggestion.LastWeightKg is null
                ? "本次建议：从舒适重量开始，保留 2～3 次余力"
                : suggestion.Action.ToLowerInvariant() switch
                {
                    "increase" => $"本次建议：增加到 {FromKilograms(suggestion.SuggestedWeightKg):0.##} {UnitLabel}",
                    "decrease" => $"本次建议：降低到 {FromKilograms(suggestion.SuggestedWeightKg):0.##} {UnitLabel}",
                    _ when suggestion.PainReported => $"本次建议：疼痛记录存在，不加重；保持 {FromKilograms(suggestion.SuggestedWeightKg):0.##} {UnitLabel}",
                    _ => $"本次建议：保持 {FromKilograms(suggestion.SuggestedWeightKg):0.##} {UnitLabel}，继续积累次数"
                };
            if (updateInput && suggestion.LastWeightKg is not null && string.IsNullOrWhiteSpace(WeightText))
                WeightText = FromKilograms(suggestion.LastWeightKg)?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        }
        catch (Exception ex)
        {
            _data.LogError(ex, "LoadWeightSuggestionAndSetup");
            StatusMessage = $"无法加载动作历史：{ex.Message}";
        }
    }
}

public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    private readonly string _dataDirectory;
    private bool _usePounds;
    [ObservableProperty] private DateTime? fromDate = DateTime.Today.AddMonths(-3);
    [ObservableProperty] private DateTime? toDate = DateTime.Today;
    [ObservableProperty] private string selectedDayFilter = "全部";
    [ObservableProperty] private string exerciseFilter = "";
    [ObservableProperty] private WorkoutHistoryRow? selectedRow;
    [ObservableProperty] private string editWeightText = "";
    [ObservableProperty] private string editRepsText = "";
    [ObservableProperty] private string editRirText = "2";
    [ObservableProperty] private bool editPain;
    [ObservableProperty] private string weightTrendText = "重量趋势：暂无数据";
    [ObservableProperty] private string repsTrendText = "次数趋势：暂无数据";
    [ObservableProperty] private string volumeTrendText = "容量趋势：暂无数据";
    [ObservableProperty] private string unitLabel = "kg";
    public ObservableCollection<WorkoutHistoryRow> Rows { get; } = [];
    public IReadOnlyList<string> DayFilters { get; } = ["全部", "A", "B", "有氧", "休息"];

    public HistoryViewModel(IAppDataService data, string dataDirectory)
    {
        _data = data;
        _dataDirectory = dataDirectory;
    }

    public void ClearForAuthenticationLoss()
    {
        Rows.Clear();
        SelectedRow = null;
        ExerciseFilter = "";
        EditWeightText = "";
        EditRepsText = "";
        EditRirText = "2";
        EditPain = false;
        WeightTrendText = "重量趋势：暂无数据";
        RepsTrendText = "次数趋势：暂无数据";
        VolumeTrendText = "容量趋势：暂无数据";
        StatusMessage = "历史数据已隐藏";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            var settings = await _data.GetSettingsAsync();
            _usePounds = settings.UnitSystem.Equals("LB", StringComparison.OrdinalIgnoreCase);
            UnitLabel = _usePounds ? "lb" : "kg";
            var rows = await _data.GetHistoryAsync(
                FromDate is null ? null : DateOnly.FromDateTime(FromDate.Value),
                ToDate is null ? null : DateOnly.FromDateTime(ToDate.Value),
                SelectedDayFilter == "全部" ? null : SelectedDayFilter,
                string.IsNullOrWhiteSpace(ExerciseFilter) ? null : ExerciseFilter);
            Rows.ReplaceWith(_usePounds
                ? rows.Select(x => x with { PeakWeightKg = x.PeakWeightKg * 2.2046226218m, VolumeKg = x.VolumeKg * 2.2046226218m })
                : rows);
            UpdateTrends();
        }, "历史已刷新");
    }

    private void UpdateTrends()
    {
        var ordered = Rows.Where(x => x.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("完成", StringComparison.Ordinal)).OrderBy(x => x.LocalDate).ToArray();
        if (ordered.Length == 0)
        {
            WeightTrendText = "重量趋势：暂无数据";
            RepsTrendText = "次数趋势：暂无数据";
            VolumeTrendText = "容量趋势：暂无数据";
            return;
        }
        var first = ordered[0];
        var last = ordered[^1];
        WeightTrendText = $"重量趋势：{first.PeakWeightKg:0.##} → {last.PeakWeightKg:0.##} {UnitLabel}";
        RepsTrendText = $"次数趋势：{first.TotalReps} → {last.TotalReps}";
        VolumeTrendText = $"容量趋势：{first.VolumeKg:0} → {last.VolumeKg:0} {UnitLabel}";
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (SelectedRow is null) return;
        await RunAsync(async () =>
        {
            var updated = await _data.UpdateHistoricalLastSetAsync(
                SelectedRow.Id,
                ToKilograms(ParseOptionalDecimal(EditWeightText)),
                int.TryParse(EditRepsText, out var reps) ? reps : null,
                int.TryParse(EditRirText, out var rir) ? rir : null,
                EditPain);
            if (updated is null) throw new InvalidOperationException("所选训练没有可编辑的正式组");
            await RefreshAsync();
        }, "历史最后一组已编辑并加入同步队列");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedRow is null) return;
        await RunAsync(async () =>
        {
            await _data.SoftDeleteWorkoutAsync(SelectedRow.Id);
            await RefreshAsync();
        }, "训练已软删除，可由同步保留审计记录");
    }

    [RelayCommand]
    private async Task ExportCsvAsync() => await RunAsync(async () => StatusMessage = $"已导出：{await _data.ExportHistoryCsvAsync(Path.Combine(_dataDirectory, "exports"))}", "CSV 导出完成");

    [RelayCommand]
    private async Task ExportJsonAsync() => await RunAsync(async () => StatusMessage = $"已导出：{await _data.ExportDataJsonAsync(Path.Combine(_dataDirectory, "exports"))}", "JSON 导出完成");

    private static decimal? ParseOptionalDecimal(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value) ? value : null;

    private decimal? ToKilograms(decimal? value) => value is null ? null : _usePounds ? value / 2.2046226218m : value;
}

public sealed partial class ExerciseLibraryViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    [ObservableProperty] private ExerciseLibraryItem? selectedItem;
    [ObservableProperty] private bool isAdmin;
    [ObservableProperty] private string draftName = "";
    [ObservableProperty] private string draftBodyPart = "";
    [ObservableProperty] private string draftEquipment = "";
    [ObservableProperty] private string draftCues = "";
    public ObservableCollection<ExerciseLibraryItem> Items { get; } = [];

    public ExerciseLibraryViewModel(IAppDataService data) => _data = data;

    public void ClearForAuthenticationLoss()
    {
        Items.Clear();
        SelectedItem = null;
        IsAdmin = false;
        DraftName = "";
        DraftBodyPart = "";
        DraftEquipment = "";
        DraftCues = "";
        StatusMessage = "账户动作数据已隐藏";
    }

    public async Task RefreshAsync()
    {
        Items.ReplaceWith(await _data.GetExercisesAsync());
        SelectedItem ??= Items.FirstOrDefault();
        IsAdmin = (await _data.GetAuthenticationStateAsync()).IsAdmin;
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        if (!IsAdmin) { StatusMessage = "管理权限必须来自后端令牌角色声明"; return; }
        var draft = new ExerciseLibraryItem(Guid.NewGuid(), DraftName, DraftBodyPart, DraftEquipment, "3×8–12", DraftCues, "", "", 0);
        await RunAsync(async () =>
        {
            await _data.SaveExerciseDraftAsync(draft);
            await RefreshAsync();
            SelectedItem = Items.FirstOrDefault(x => x.Id == draft.Id);
        }, "动作草稿已保存");
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (!IsAdmin) { StatusMessage = "管理权限必须来自后端令牌角色声明"; return; }
        if (SelectedItem is null) return;
        await RunAsync(() => _data.PublishExerciseAsync(SelectedItem.Id), "动作已发布到云端");
    }
}

public sealed partial class EditablePlanItemViewModel : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private int position;
    [ObservableProperty] private string bodyPart = "新位置";
    [ObservableProperty] private string preferredExercise = "请选择动作";
    [ObservableProperty] private string equipment = "";
    [ObservableProperty] private int sets = 2;
    [ObservableProperty] private int repMin = 8;
    [ObservableProperty] private int repMax = 12;
    [ObservableProperty] private int restSeconds = 90;
    [ObservableProperty] private string alternatives = "";
    [ObservableProperty] private string cues = "";
    [ObservableProperty] private string commonMistakes = "";
    public string Prescription => $"{Sets}×{RepMin}–{RepMax}";

    partial void OnSetsChanged(int value) => OnPropertyChanged(nameof(Prescription));
    partial void OnRepMinChanged(int value) => OnPropertyChanged(nameof(Prescription));
    partial void OnRepMaxChanged(int value) => OnPropertyChanged(nameof(Prescription));
}

public sealed class EditablePlanDayViewModel
{
    public string Code { get; }
    public ObservableCollection<EditablePlanItemViewModel> Items { get; } = [];
    public EditablePlanDayViewModel(string code) => Code = code;
}

public sealed record PlanVersionChoice(PlanData Data)
{
    public string DisplayName => $"v{Data.Version} · {Data.Status} · {(Data.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未发布")}";
}

public sealed partial class PlanEditorViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    private PlanData? _source;
    [ObservableProperty] private EditablePlanDayViewModel? selectedDay;
    [ObservableProperty] private EditablePlanItemViewModel? selectedItem;
    [ObservableProperty] private string planTitle = "训练计划";
    [ObservableProperty] private string validationSummary = "";
    [ObservableProperty] private bool isPublished;
    [ObservableProperty] private PlanVersionChoice? selectedVersion;
    public ObservableCollection<EditablePlanDayViewModel> Days { get; } = [];
    public ObservableCollection<PlanVersionChoice> Versions { get; } = [];

    public PlanEditorViewModel(IAppDataService data) => _data = data;

    public void ClearForAuthenticationLoss()
    {
        _source = null;
        Days.Clear();
        Versions.Clear();
        SelectedDay = null;
        SelectedItem = null;
        SelectedVersion = null;
        PlanTitle = "请先登录";
        ValidationSummary = "登录后加载计划";
        IsPublished = false;
        StatusMessage = "计划数据已隐藏";
    }

    public async Task LoadAsync()
    {
        var current = await _data.GetCurrentPlanAsync();
        await RefreshVersionsAsync();
        LoadPlan(current);
        SelectedVersion = Versions.FirstOrDefault(x => x.Data.Id == current.Id);
    }

    [RelayCommand]
    private async Task CreateDraftAsync()
    {
        await RunAsync(async () =>
        {
            var draft = await _data.CreatePlanDraftAsync();
            await RefreshVersionsAsync();
            LoadPlan(draft);
            SelectedVersion = Versions.FirstOrDefault(x => x.Data.Id == draft.Id);
        }, "已从当前版本创建新草稿");
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        var plan = BuildPlan();
        Validate(plan);
        if (!string.IsNullOrEmpty(ValidationSummary)) return;
        await RunAsync(async () =>
        {
            await _data.SavePlanDraftAsync(plan);
            await RefreshVersionsAsync();
        }, "草稿已保存");
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        var auth = await _data.GetAuthenticationStateAsync();
        if (!auth.IsAdmin) { StatusMessage = "发布权限由后端令牌角色声明决定"; return; }
        var plan = BuildPlan();
        Validate(plan);
        if (!string.IsNullOrEmpty(ValidationSummary)) return;
        await RunAsync(async () =>
        {
            var published = await _data.PublishPlanAsync(plan);
            await RefreshVersionsAsync();
            LoadPlan(published);
            SelectedVersion = Versions.FirstOrDefault(x => x.Data.Id == published.Id);
        }, "新版本已发布；已发布内容不可原地修改");
    }

    [RelayCommand]
    private async Task AssignAsync()
    {
        if (_source is null) return;
        await RunAsync(() => _data.AssignPlanAsync(_source.Id), "已分配给当前用户");
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        var versions = await _data.GetPlanVersionsAsync();
        var selectedPublished = SelectedVersion?.Data.Status.Equals("published", StringComparison.OrdinalIgnoreCase) == true
            ? SelectedVersion.Data
            : null;
        var oldVersion = selectedPublished ?? versions.Where(x => x.Status.Equals("published", StringComparison.OrdinalIgnoreCase) && x.Version < (_source?.Version ?? int.MaxValue)).OrderByDescending(x => x.Version).FirstOrDefault();
        if (oldVersion is null) { StatusMessage = "没有可回滚的已发布旧版本"; return; }
        await RunAsync(() => _data.RollbackAssignmentAsync(oldVersion.Id), $"已回滚分配到 v{oldVersion.Version}");
    }

    [RelayCommand]
    private void ViewVersion()
    {
        if (SelectedVersion is not null) LoadPlan(SelectedVersion.Data);
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (IsPublished) { StatusMessage = "已发布版本不可修改，请先新建草稿"; return; }
        if (SelectedDay is null || SelectedItem is null) return;
        var index = SelectedDay.Items.IndexOf(SelectedItem);
        if (index <= 0) return;
        SelectedDay.Items.Move(index, index - 1);
        Renumber();
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (IsPublished) { StatusMessage = "已发布版本不可修改，请先新建草稿"; return; }
        if (SelectedDay is null || SelectedItem is null) return;
        var index = SelectedDay.Items.IndexOf(SelectedItem);
        if (index < 0 || index >= SelectedDay.Items.Count - 1) return;
        SelectedDay.Items.Move(index, index + 1);
        Renumber();
    }

    [RelayCommand]
    private void AddItem()
    {
        if (IsPublished) { StatusMessage = "已发布版本不可修改，请先新建草稿"; return; }
        SelectedDay ??= Days.FirstOrDefault();
        if (SelectedDay is null) return;
        var item = new EditablePlanItemViewModel { Position = SelectedDay.Items.Count + 1 };
        SelectedDay.Items.Add(item);
        SelectedItem = item;
    }

    [RelayCommand]
    private void RemoveItem()
    {
        if (IsPublished) { StatusMessage = "已发布版本不可修改，请先新建草稿"; return; }
        if (SelectedDay is null || SelectedItem is null) return;
        SelectedDay.Items.Remove(SelectedItem);
        SelectedItem = SelectedDay.Items.FirstOrDefault();
        Renumber();
    }

    private void Renumber()
    {
        if (SelectedDay is null) return;
        for (var i = 0; i < SelectedDay.Items.Count; i++) SelectedDay.Items[i].Position = i + 1;
    }

    private void LoadPlan(PlanData plan)
    {
        _source = plan;
        IsPublished = plan.Status.Equals("published", StringComparison.OrdinalIgnoreCase);
        PlanTitle = $"{plan.Name} v{plan.Version} · {plan.Status}";
        Days.Clear();
        foreach (var day in plan.Days)
        {
            var editable = new EditablePlanDayViewModel(day.Code);
            foreach (var item in day.Items)
            {
                var preferred = item.Options.FirstOrDefault(x => x.IsPreferred) ?? item.Options.First();
                editable.Items.Add(new EditablePlanItemViewModel
                {
                    Id = item.Id,
                    Position = item.Position,
                    BodyPart = item.BodyPart,
                    PreferredExercise = preferred.ExerciseName,
                    Equipment = preferred.Equipment,
                    Sets = preferred.Sets,
                    RepMin = preferred.RepMin,
                    RepMax = preferred.RepMax,
                    RestSeconds = preferred.RestSeconds,
                    Alternatives = string.Join("，", item.Options.Where(x => !x.IsPreferred).Select(x => x.ExerciseName)),
                    Cues = item.Cues,
                    CommonMistakes = item.CommonMistakes
                });
            }
            Days.Add(editable);
        }
        SelectedDay = Days.FirstOrDefault();
        SelectedItem = SelectedDay?.Items.FirstOrDefault();
        ValidationSummary = IsPublished ? "已发布版本不可原地修改；请先新建草稿" : "";
    }

    private async Task RefreshVersionsAsync()
    {
        var versions = await _data.GetPlanVersionsAsync();
        Versions.ReplaceWith(versions.OrderByDescending(x => x.Version).Select(x => new PlanVersionChoice(x)));
    }

    private PlanData BuildPlan()
    {
        if (_source is null) throw new InvalidOperationException("没有计划可保存");
        var days = Days.Select(day => new PlanDayData(day.Code, $"{day.Code} 训练日", day.Items.Select(item =>
        {
            var preferred = new ExerciseOptionData(Guid.NewGuid(), Guid.NewGuid(), item.PreferredExercise, item.Equipment, true, item.Sets, item.RepMin, item.RepMax, "reps", item.RestSeconds);
            var alternatives = item.Alternatives.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((name, index) => new ExerciseOptionData(Guid.NewGuid(), Guid.NewGuid(), name, item.Equipment, false, item.Sets, item.RepMin, item.RepMax, "reps", item.RestSeconds));
            return new PlanItemData(item.Id, item.Position, item.BodyPart, item.Cues, item.CommonMistakes, new[] { preferred }.Concat(alternatives).ToArray());
        }).ToArray())).ToArray();
        return _source with { Days = days };
    }

    private void Validate(PlanData plan)
    {
        var errors = new List<string>();
        if (plan.Status.Equals("published", StringComparison.OrdinalIgnoreCase)) errors.Add("已发布版本不可编辑");
        if (plan.Days.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 || !plan.Days.Any(x => x.Code == "A") || !plan.Days.Any(x => x.Code == "B")) errors.Add("必须包含唯一的 A/B 训练日");
        foreach (var day in plan.Days)
        {
            if (day.Items.GroupBy(x => x.Position).Any(x => x.Count() > 1)) errors.Add($"{day.Code} 存在重复位置");
            if (day.Items.Any(x => x.Options.Count == 0 || x.Options.Count(y => y.IsPreferred) != 1)) errors.Add($"{day.Code} 每个位置必须有且仅有一个首选动作");
            if (day.Items.SelectMany(x => x.Options).Any(x => x.Sets <= 0 || x.RepMin <= 0 || x.RepMax < x.RepMin || x.RestSeconds < 0)) errors.Add($"{day.Code} 存在无效组次或休息时间");
        }
        ValidationSummary = string.Join("；", errors.Distinct());
    }
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppDataService _data;
    private readonly string _runtimeDataDirectory;
    [ObservableProperty] private string apiBaseUrl = "https://localhost:8000";
    [ObservableProperty] private string loginEmail = "";
    [ObservableProperty] private string authenticationText = "未登录（普通用户）";
    [ObservableProperty] private string timeZone = "Asia/Shanghai";
    [ObservableProperty] private string unitSystem = "KG";
    [ObservableProperty] private string trainingDays = "1,3,5";
    [ObservableProperty] private string theme = "System";
    [ObservableProperty] private bool automaticSync = true;
    [ObservableProperty] private string dataDirectory = "";
    [ObservableProperty] private string versionText = "";
    [ObservableProperty] private bool isAuthenticated;
    public IReadOnlyList<string> UnitSystems { get; } = ["KG", "LB"];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public bool HasUnsavedChanges { get; private set; }
    public event Func<Task>? AuthenticationChanged;

    public SettingsViewModel(IAppDataService data, string runtimeDataDirectory)
    {
        _data = data;
        _runtimeDataDirectory = runtimeDataDirectory;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not nameof(StatusMessage) and not nameof(IsBusy) and not nameof(AuthenticationText) and not nameof(VersionText)) HasUnsavedChanges = true;
        };
    }

    public async Task LoadAsync()
    {
        var settings = await _data.GetSettingsAsync();
        ApiBaseUrl = settings.ApiBaseUrl;
        TimeZone = settings.TimeZone;
        UnitSystem = settings.UnitSystem;
        TrainingDays = settings.TrainingDays;
        Theme = settings.Theme;
        ThemeService.Apply(Theme);
        AutomaticSync = settings.AutomaticSync;
        DataDirectory = settings.DataDirectory;
        VersionText = $"Personal Fitness Planner {settings.Version} · .NET {Environment.Version} · Windows x64";
        UpdateAuthentication(await _data.GetAuthenticationStateAsync());
        HasUnsavedChanges = false;
    }

    public async Task LoginAsync(string password)
    {
        await RunAsync(async () =>
        {
            try
            {
                UpdateAuthentication(await _data.LoginAsync(LoginEmail, password));
            }
            catch
            {
                // Account-switch safety can reject a new credential when the old
                // subject owns pending local changes. Reflect the cleared token in
                // the UI before surfacing the error so cached health data is hidden.
                UpdateAuthentication(await _data.GetAuthenticationStateAsync());
                await NotifyAuthenticationChangedAsync();
                throw;
            }
            await NotifyAuthenticationChangedAsync();
        }, "登录成功");
    }

    [RelayCommand]
    private async Task LogoutAsync() => await RunAsync(async () =>
    {
        await _data.LogoutAsync();
        UpdateAuthentication(await _data.GetAuthenticationStateAsync());
        await NotifyAuthenticationChangedAsync();
    }, "已退出登录");

    [RelayCommand]
    public async Task SaveAsync()
    {
        await RunAsync(async () =>
        {
            await _data.SaveSettingsAsync(new AppSettingsData(ApiBaseUrl, TimeZone, UnitSystem, TrainingDays, Theme, DataDirectory, AutomaticSync, Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"));
            ThemeService.Apply(Theme);
            HasUnsavedChanges = false;
        }, "设置已保存");
    }

    [RelayCommand]
    private async Task SyncAsync() => await RunAsync(async () =>
    {
        var result = await _data.SynchronizeAsync();
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }, "同步完成");

    [RelayCommand]
    private async Task FullSyncAsync() => await RunAsync(async () =>
    {
        var result = await _data.FullResynchronizeAsync();
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }, "完整重新同步完成");

    [RelayCommand]
    private async Task UploadLocalAsync() => await RunAsync(async () =>
    {
        var result = await _data.UploadLocalAsync();
        if (!result.Success) throw new InvalidOperationException(result.Message);
        StatusMessage = result.Message;
    }, "本地记录上传完成");

    [RelayCommand]
    private async Task DownloadCloudOverwriteAsync() => await RunAsync(async () =>
    {
        var result = await _data.DownloadCloudOverwriteAsync();
        if (!result.Success) throw new InvalidOperationException(result.Message);
        StatusMessage = result.Message;
    }, "云端数据覆盖完成");

    [RelayCommand]
    private async Task BackupAsync() => await RunAsync(async () => StatusMessage = $"备份：{await _data.CreateBackupAsync()}", "备份完成");

    [RelayCommand]
    private async Task ExportAsync() => await RunAsync(async () => StatusMessage = $"导出：{await _data.ExportDataJsonAsync(Path.Combine(_runtimeDataDirectory, "exports"))}", "导出完成");

    public async Task ImportAsync(string path) => await RunAsync(() => _data.ImportDataJsonAsync(path), "导入完成；已在导入前创建备份");

    private void UpdateAuthentication(AuthenticationState auth)
    {
        IsAuthenticated = auth.IsAuthenticated;
        AuthenticationText = auth.IsAuthenticated
            ? $"{auth.DisplayName} · {(auth.IsAdmin ? "管理员" : "普通用户")}\n权限来源：{auth.RoleSource}"
            : "未登录（普通用户）";
    }

    private async Task NotifyAuthenticationChangedAsync()
    {
        if (AuthenticationChanged is null) return;
        foreach (Func<Task> callback in AuthenticationChanged.GetInvocationList()) await callback();
    }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }
}
