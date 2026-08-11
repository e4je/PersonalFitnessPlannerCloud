using System.Globalization;
using System.Text;
using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Infrastructure.Export;

public sealed class ExportService
{
    private static readonly UTF8Encoding Utf8WithBom = new(true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
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
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var export = await JsonSerializer.DeserializeAsync<HistoryExport>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("导入 JSON 为空或格式不正确。");
        if (export.SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持的数据导出版本 {export.SchemaVersion}。");
        }
        await _repository.ImportSnapshotAsync(export, cancellationToken).ConfigureAwait(false);
        await _settings.SaveAsync(export.Settings, cancellationToken).ConfigureAwait(false);
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
        if (value.ContainsAny([',', '"', '\r', '\n']))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
