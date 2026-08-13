using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Network;

namespace PersonalFitnessPlanner.Infrastructure;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(AppPaths paths) => _paths = paths;

    public async Task<AppSettingsData> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            if (!File.Exists(_paths.SettingsPath))
            {
                return AppSettingsData.Default(_paths.DataDirectory);
            }

            await using var stream = new FileStream(
                _paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<AppSettingsData>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                   ?? AppSettingsData.Default(_paths.DataDirectory);
        }
        catch (JsonException)
        {
            // A partially written legacy settings file must not prevent startup.
            return AppSettingsData.Default(_paths.DataDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettingsData settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var temporaryPath = _paths.SettingsPath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _paths.SettingsPath, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static void Validate(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!FitnessApiClient.TryValidateBaseAddress(settings.ApiBaseUrl, out var apiUri))
        {
            throw new ArgumentException(
                "API 地址必须是无账号、查询参数和片段的有效 HTTP(S) 绝对地址。",
                nameof(settings));
        }
        if (apiUri.Scheme == "http" && !apiUri.IsLoopback &&
            !string.Equals(apiUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("非本机 API 必须使用 HTTPS。", nameof(settings));
        }
        if (settings.UnitSystem is not ("kg" or "lb"))
        {
            throw new ArgumentException("重量单位只能是 kg 或 lb。", nameof(settings));
        }
        if (string.IsNullOrWhiteSpace(settings.TimeZone) || string.IsNullOrWhiteSpace(settings.DataDirectory))
        {
            throw new ArgumentException("时区和数据目录不能为空。", nameof(settings));
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("时区必须是本机 .NET 可识别的 IANA 时区，例如 Asia/Shanghai。", nameof(settings), exception);
        }

        var trainingDays = settings.TrainingDays.Split(
            [',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (trainingDays.Length == 0 || trainingDays.Any(value => !int.TryParse(value, out var day) || day is < 1 or > 7))
        {
            throw new ArgumentException("训练日必须使用 1～7 的 ISO 星期数字，例如 1,3,5。", nameof(settings));
        }
    }
}
