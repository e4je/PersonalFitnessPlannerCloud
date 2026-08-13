using System.Globalization;
using System.Text;
using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Infrastructure.Export;

public sealed class ExportService
{
    public const long MaxImportFileBytes = 64L * 1024 * 1024;
    public const int MaxImportedPlans = 1_000;
    public const int MaxImportedWorkoutSessions = 100_000;
    public const int MaxImportedSets = 1_000_000;

    private static readonly UTF8Encoding Utf8WithBom = new(true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    private readonly FitnessRepository _repository;
    private readonly SettingsStore _settings;

    public ExportService(FitnessRepository repository, SettingsStore settings)
    {
        _repository = repository;
        _settings = settings;
    }

    public async Task<string> ExportHistoryCsvAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = PrepareTargetDirectory(targetDirectory);
        var rows = await _repository.GetHistoryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var target = Path.Combine(directory, $"训练历史-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var temporary = target + ".tmp";
        await using (var writer = new StreamWriter(temporary, false, Utf8WithBom))
        {
            await writer.WriteLineAsync("日期,A/B,来源,状态,组数,最高重量(kg),总次数,总容量(kg),同步状态,计划版本,动作").ConfigureAwait(false);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new[]
                {
                    row.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.DayCode,
                    row.Source, row.Status, row.SetCount.ToString(CultureInfo.InvariantCulture),
                    row.PeakWeightKg.ToString("0.###", CultureInfo.InvariantCulture),
                    row.TotalReps.ToString(CultureInfo.InvariantCulture),
                    row.VolumeKg.ToString("0.###", CultureInfo.InvariantCulture), row.SyncStatus,
                    row.PlanVersion, row.ExerciseNames
                };
                await writer.WriteLineAsync(string.Join(',', values.Select(EscapeCsv))).ConfigureAwait(false);
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, target, true);
        return target;
    }

    public async Task<string> ExportDataJsonAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = PrepareTargetDirectory(targetDirectory);
        var plans = await _repository.GetPlanVersionsAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await _repository.GetWorkoutExportSessionsAsync(cancellationToken).ConfigureAwait(false);
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var export = new HistoryExport(1, DateTimeOffset.UtcNow, plans, sessions, settings);
        var target = Path.Combine(directory, $"健身数据-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var temporary = target + ".tmp";
        await using (var stream = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, target, true);
        return target;
    }

    public async Task ImportDataJsonAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("导入 JSON 文件不存在。", fullPath);
        if (file.Length == 0) throw new InvalidDataException("导入 JSON 为空或格式不正确。");
        if (file.Length > MaxImportFileBytes)
        {
            throw new InvalidDataException($"导入 JSON 不能超过 {MaxImportFileBytes / 1024 / 1024} MiB。");
        }

        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxImportFileBytes)
        {
            throw new InvalidDataException($"导入 JSON 不能超过 {MaxImportFileBytes / 1024 / 1024} MiB。");
        }

        HistoryExport export;
        try
        {
            export = await JsonSerializer.DeserializeAsync<HistoryExport>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("导入 JSON 为空或格式不正确。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("导入 JSON 格式不正确或嵌套过深。", exception);
        }

        if (export.SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持的数据导出版本 {export.SchemaVersion}。");
        }
        ValidateImportStructure(export);

        // Server identity, storage paths and build version belong to this
        // installation. An imported backup may restore only portable choices.
        var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var portableSettings = current with
        {
            TimeZone = export.Settings.TimeZone,
            UnitSystem = export.Settings.UnitSystem,
            TrainingDays = export.Settings.TrainingDays,
            Theme = export.Settings.Theme,
            AutomaticSync = export.Settings.AutomaticSync
        };
        SettingsStore.Validate(portableSettings);

        await _repository.ImportSnapshotAsync(export, cancellationToken).ConfigureAwait(false);
        await _settings.SaveAsync(portableSettings, cancellationToken).ConfigureAwait(false);
    }

    private static string PrepareTargetDirectory(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetDirectory));
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string EscapeCsv(string value)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < value.Length && char.IsWhiteSpace(value[firstNonWhitespace]))
        {
            firstNonWhitespace++;
        }
        if (firstNonWhitespace < value.Length && value[firstNonWhitespace] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        if (value.ContainsAny([',', '"', '\r', '\n']))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static void ValidateImportStructure(HistoryExport export)
    {
        if (export.Plans is null || export.WorkoutSessions is null || export.Settings is null)
        {
            throw new InvalidDataException("导入 JSON 缺少 plans、workoutSessions 或 settings。");
        }
        if (export.Plans.Count > MaxImportedPlans || export.WorkoutSessions.Count > MaxImportedWorkoutSessions)
        {
            throw new InvalidDataException("导入 JSON 中的计划或训练记录数量超过安全上限。");
        }
        if (export.Plans.Any(plan => plan is null || !HasSafePlanShape(plan)))
        {
            throw new InvalidDataException("导入 JSON 中的训练计划结构无效或超过安全上限。");
        }

        long setCount = 0;
        foreach (var session in export.WorkoutSessions)
        {
            if (session is null || session.PlanSnapshot is null || !HasSafePlanShape(session.PlanSnapshot) || session.Sets is null)
            {
                throw new InvalidDataException("导入 JSON 中的训练记录结构无效。");
            }
            setCount += session.Sets.Count;
            if (setCount > MaxImportedSets)
            {
                throw new InvalidDataException("导入 JSON 中的训练组数超过安全上限。");
            }
        }
    }

    private static bool HasSafePlanShape(PlanData plan) =>
        plan.Days is not null && plan.Days.Count <= 31 &&
        plan.Days.All(day => day is not null && day.Items is not null && day.Items.Count <= 256 &&
            day.Items.All(item => item is not null && item.Options is not null && item.Options.Count <= 32));
}
