using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Security;

namespace PersonalFitnessPlanner.Infrastructure.Network;

public sealed record SyncBatchResult(
    IReadOnlyList<Guid> AcceptedOutboxIds,
    IReadOnlyList<SyncBatchFailure> Failures);

public sealed class FitnessApiClient
{
    private const int MaxErrorBodyBytes = 32 * 1024;
    private const int MaxSafeErrorFieldLength = 300;
    private static readonly string[] SafeErrorFieldNames = ["code", "message", "detail"];
    private static readonly string[] SensitiveErrorTerms =
    [
        "authorization", "password", "passwd", "secret", "token", "credential", "input",
        "api_key", "apikey", "access_key", "refresh", "bearer", "cookie", "session"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly DpapiTokenStore _tokens;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Uri? _baseAddress;
    private string? _apiOrigin;

    public FitnessApiClient(HttpClient httpClient, DpapiTokenStore tokens)
    {
        _httpClient = httpClient;
        _tokens = tokens;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public Uri? BaseAddress => _baseAddress;

    public void ConfigureBaseAddress(string apiBaseUrl) =>
        ConfigureBaseAddressAsync(apiBaseUrl).GetAwaiter().GetResult();

    public async Task ConfigureBaseAddressAsync(
        string apiBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateBaseAddress(apiBaseUrl, out var address))
        {
            throw new ArgumentException(
                "API 地址必须是无账号、查询参数和片段的 HTTP(S) 绝对地址。",
                nameof(apiBaseUrl));
        }
        if (address.Scheme == "http" && !IsLoopbackHost(address))
        {
            throw new ArgumentException("非本机 API 必须使用 HTTPS。", nameof(apiBaseUrl));
        }

        var baseAddress = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
        var origin = NormalizeOrigin(baseAddress);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Credentials are valid only for the exact scheme/host/effective-port
            // that issued them. Legacy credentials have no origin and fail closed.
            var stored = await _tokens.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (stored is not null && !IsBoundToOrigin(stored, origin))
            {
                // Remove the old credential before exposing the new base address to
                // callers, so a concurrent request cannot send it to the new host.
                await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _baseAddress = baseAddress;
            _apiOrigin = origin;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task<AuthenticationState> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConfigured();
            using var response = await _httpClient.PostAsJsonAsync(
                ResolveUri("api/v1/auth/login"), new { email = email.Trim(), password }, JsonOptions, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var tokens = await ReadTokensAsync(response, null, CurrentOrigin(), cancellationToken).ConfigureAwait(false);
            await _tokens.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
            return JwtRoleParser.ToAuthenticationState(tokens);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_baseAddress is not null &&
                (await GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false)).IsAuthenticated)
            {
                using var response = await SendAuthorizedAsync(
                    stored =>
                    {
                        var body = JsonSerializer.SerializeToUtf8Bytes(
                            new { refresh_token = stored.RefreshToken }, JsonOptions);
                        return JsonRequest(HttpMethod.Post, "api/v1/auth/logout", body);
                    }, cancellationToken).ConfigureAwait(false);
                // Logout is best effort; local credentials are always removed.
            }
        }
        finally
        {
            await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default) =>
        JwtRoleParser.ToAuthenticationState(await LoadCurrentTokensAsync(cancellationToken).ConfigureAwait(false));

    internal async Task<StoredTokens?> LoadCurrentTokensAsync(CancellationToken cancellationToken = default)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConfigured();
            return await LoadBoundTokensUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task<SyncBatchResult> SendBatchAsync(
        IReadOnlyList<OutboxItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) return new SyncBatchResult([], []);
        EnsureConfigured();
        var operations = items.Select(item => new
        {
            id = item.Id,
            client_outbox_id = item.Id,
            entity_type = item.EntityType,
            entity_id = item.EntityId,
            operation = item.Operation,
            idempotency_key = item.IdempotencyKey,
            payload = ParsePayload(item.PayloadJson)
        }).ToArray();
        var batchId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            batch_id = batchId,
            sent_at = DateTimeOffset.UtcNow,
            operations
        }, JsonOptions);
        using var response = await SendAuthorizedAsync(_ =>
            {
                var request = JsonRequest(HttpMethod.Post, "api/v1/sync/batch", body);
                request.Headers.TryAddWithoutValidation("Idempotency-Key", $"sync-batch:{batchId:D}");
                return request;
            }, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return new SyncBatchResult([], []);
        }

        using var document = JsonDocument.Parse(responseJson);
        var requestedIds = items.Select(item => item.Id).ToHashSet();
        var accepted = new HashSet<Guid>();
        var failures = new List<SyncBatchFailure>();
        if (TryGetProperty(document.RootElement, out var values, "accepted_outbox_ids", "acceptedOutboxIds", "accepted"))
        {
            if (values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) &&
                        requestedIds.Contains(id)) accepted.Add(id);
                    else if (value.ValueKind == JsonValueKind.Object &&
                             TryGetProperty(value, out var idNode, "client_outbox_id", "clientOutboxId", "id") &&
                             Guid.TryParse(idNode.GetString(), out id) && requestedIds.Contains(id)) accepted.Add(id);
                }
            }
        }

        if (TryGetProperty(document.RootElement, out var results, "results") && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                var status = GetString(result, "status").ToLowerInvariant();
                if (!TryGetProperty(result, out var idNode, "client_outbox_id", "clientOutboxId", "id") ||
                    !Guid.TryParse(idNode.ValueKind == JsonValueKind.String ? idNode.GetString() : idNode.ToString(), out var id) ||
                    !requestedIds.Contains(id)) continue;

                if (status is "accepted" or "applied" or "success" or "duplicate")
                {
                    accepted.Add(id);
                    continue;
                }

                var error = GetString(result, "error", "message", "detail");
                var serverCopy = TryGetProperty(result, out var serverCopyNode, "server_copy", "serverCopy") &&
                                 serverCopyNode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? serverCopyNode.GetRawText()
                    : string.Empty;
                long? serverVersion = TryGetProperty(result, out var versionNode, "server_version", "serverVersion") &&
                                      versionNode.TryGetInt64(out var parsedVersion)
                    ? parsedVersion
                    : null;
                failures.Add(new SyncBatchFailure(
                    id,
                    string.IsNullOrWhiteSpace(status) ? "unknown" : status,
                    error,
                    serverCopy,
                    serverVersion));
            }
        }

        return new SyncBatchResult(
            accepted.ToArray(),
            failures.Where(failure => !accepted.Contains(failure.OutboxId)).ToArray());
    }

    public async Task<SyncChangesPage> GetChangesAsync(string cursor, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var relative = string.IsNullOrWhiteSpace(cursor)
            ? "api/v1/sync/changes"
            : $"api/v1/sync/changes?cursor={Uri.EscapeDataString(cursor)}";
        using var response = await SendAuthorizedAsync(_ => new HttpRequestMessage(HttpMethod.Get, relative), cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var changes = new List<SyncChange>();
        if (TryGetProperty(root, out var items, "changes", "items") && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var entityType = GetString(item, "entity_type", "entityType", "type");
                var operation = GetString(item, "operation", "op");
                var entityIdText = GetString(item, "entity_id", "entityId", "id");
                if (string.IsNullOrWhiteSpace(entityType) || !Guid.TryParse(entityIdText, out var entityId)) continue;
                var updatedAtText = GetString(item, "changed_at", "changedAt", "updated_at", "updatedAt");
                var updatedAt = DateTimeOffset.TryParse(updatedAtText, out var parsed) ? parsed : DateTimeOffset.UtcNow;
                var version = TryGetProperty(item, out var versionNode, "version") && versionNode.TryGetInt64(out var parsedVersion)
                    ? parsedVersion
                    : 0;
                var payload = TryGetProperty(item, out var payloadNode, "payload", "data")
                    ? payloadNode.GetRawText()
                    : item.GetRawText();
                changes.Add(new SyncChange(entityType, entityId, operation, payload, updatedAt, version));
            }
        }
        var nextCursor = GetString(root, "next_cursor", "nextCursor", "cursor");
        var hasMore = TryGetProperty(root, out var hasMoreNode, "has_more", "hasMore") &&
                      hasMoreNode.ValueKind is JsonValueKind.True;
        var fullResyncRequired = TryGetProperty(
                                     root,
                                     out var fullResyncNode,
                                     "full_resync_required",
                                     "fullResyncRequired") &&
                                 fullResyncNode.ValueKind is JsonValueKind.True;
        return new SyncChangesPage(changes, nextCursor, hasMore, fullResyncRequired);
    }

    public async Task<JsonElement> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var response = await SendAuthorizedAsync(
            _ => new HttpRequestMessage(HttpMethod.Get, "api/v1/bootstrap"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (TryGetProperty(root, out var data, "data", "bootstrap") && data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }
        return root.Clone();
    }

    public async Task<JsonElement> SendAdminAsync(
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!(await GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false)).IsAdmin)
        {
            throw new UnauthorizedAccessException("管理操作要求后端 JWT 中的 admin 角色声明。");
        }
        var bytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        using var response = await SendAuthorizedAsync(
            _ =>
            {
                var request = bytes is null ? new HttpRequestMessage(method, relativeUrl) : JsonRequest(method, relativeUrl, bytes);
                if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // A stale local admin claim must not continue enabling management UI.
            await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength == 0) return default;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    public async Task PublishExerciseAsync(ExerciseLibraryItem exercise, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            id = exercise.Id,
            name = exercise.Name,
            body_part = exercise.BodyPart,
            equipment_name = exercise.Equipment,
            prescription = exercise.Prescription,
            cues = exercise.Cues,
            common_mistakes = exercise.CommonMistakes,
            alternatives = exercise.Alternatives,
            version = exercise.Version
        };
        try
        {
            await SendAdminAsync(HttpMethod.Post, "api/v1/admin/exercises", body,
                $"exercise:{exercise.Id:D}:publish", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            await SendAdminAsync(HttpMethod.Patch, $"api/v1/admin/exercises/{exercise.Id:D}", body,
                $"exercise:{exercise.Id:D}:publish", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishPlanAsync(PlanData plan, CancellationToken cancellationToken = default)
    {
        var planBody = new { id = plan.PlanId, name = plan.Name };
        try
        {
            await SendAdminAsync(HttpMethod.Post, "api/v1/admin/plans", planBody,
                $"plan:{plan.PlanId:D}:create", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // Existing plan identity is expected when publishing a new immutable version.
        }

        var versionBody = new
        {
            id = plan.Id,
            plan_id = plan.PlanId,
            plan_name = plan.Name,
            version_number = plan.Version,
            status = "draft",
            weekly_frequency = plan.WeeklyStrengthTarget,
            min_rest_days = plan.MinimumRestDays,
            fatigue_threshold = plan.FatigueThreshold,
            intro_weeks = plan.DeloadWeeks,
            intro_max_sets = plan.DeloadMaxSets,
            days = plan.Days.Select(day => new
            {
                code = day.Code,
                name = day.Name,
                items = day.Items.Select(item => new
                {
                    id = item.Id,
                    position = item.Position,
                    body_part = item.BodyPart,
                    cues = item.Cues,
                    common_mistakes = item.CommonMistakes,
                    seat_position = item.SeatPosition,
                    bench_angle = item.BenchAngle,
                    machine_number = item.MachineNumber,
                    options = item.Options.Select(option => new
                    {
                        id = option.Id,
                        exercise_id = option.ExerciseId,
                        equipment_id = option.EquipmentId,
                        exercise_name = option.ExerciseName,
                        equipment = option.Equipment,
                        is_preferred = option.IsPreferred,
                        set_count = option.Sets,
                        rep_min = option.RepMin,
                        rep_max = option.RepMax,
                        rep_unit = option.RepUnit,
                        rest_seconds = option.RestSeconds
                    })
                })
            })
        };
        try
        {
            await SendAdminAsync(HttpMethod.Post, $"api/v1/admin/plans/{plan.PlanId:D}/versions", versionBody,
                $"plan-version:{plan.Id:D}:create", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // A retried publish may find the same client UUID already created.
        }
        await SendAdminAsync(HttpMethod.Post, $"api/v1/admin/plan-versions/{plan.Id:D}/publish", null,
            $"plan-version:{plan.Id:D}:publish", cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> AssignPlanAsync(Guid planVersionId, CancellationToken cancellationToken = default)
    {
        var assignmentId = Guid.NewGuid();
        await SendAdminAsync(HttpMethod.Post, "api/v1/admin/assignments", new
        {
            id = assignmentId,
            plan_version_id = planVersionId,
            start_local_date = DateOnly.FromDateTime(DateTime.Today),
            is_active = true
        }, $"assignment:{assignmentId:D}", cancellationToken).ConfigureAwait(false);
        return assignmentId;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<StoredTokens, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConfigured();
            var stored = await LoadBoundTokensUnsafeAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("请先登录后再同步。");
            using var firstRequest = requestFactory(stored);
            PrepareRequestUri(firstRequest);
            firstRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stored.AccessToken);
            var response = await _httpClient.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            if (string.IsNullOrWhiteSpace(stored.RefreshToken))
            {
                await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                return response;
            }

            response.Dispose();
            StoredTokens refreshed;
            try
            {
                refreshed = await RefreshAsync(stored, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            using var retry = requestFactory(refreshed);
            PrepareRequestUri(retry);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
            var retryResponse = await _httpClient.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (retryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return retryResponse;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private async Task<StoredTokens> RefreshAsync(StoredTokens previous, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadBoundTokensUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (current is not null && current.AccessToken != previous.AccessToken)
            {
                return current;
            }
            using var response = await _httpClient.PostAsJsonAsync(
                ResolveUri("api/v1/auth/refresh"), new { refresh_token = previous.RefreshToken }, JsonOptions, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var refreshed = await ReadTokensAsync(response, previous, CurrentOrigin(), cancellationToken).ConfigureAwait(false);
            await _tokens.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<StoredTokens> ReadTokensAsync(
        HttpResponseMessage response,
        StoredTokens? previous,
        string apiOrigin,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (TryGetProperty(root, out var data, "data", "tokens") && data.ValueKind == JsonValueKind.Object) root = data;
        var access = GetString(root, "access_token", "accessToken");
        var refresh = GetString(root, "refresh_token", "refreshToken");
        if (string.IsNullOrWhiteSpace(refresh)) refresh = previous?.RefreshToken ?? string.Empty;
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("登录响应缺少 access_token。");
        DateTimeOffset? expiresAt = null;
        if (TryGetProperty(root, out var expiresAtNode, "expires_at", "expiresAt"))
        {
            expiresAt = ParseExpiration(expiresAtNode);
        }
        if (expiresAt is null && TryGetProperty(root, out var expiresIn, "expires_in", "expiresIn") &&
            expiresIn.TryGetInt32(out var seconds))
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        var displayName = GetString(root, "display_name", "displayName", "name");
        if (string.IsNullOrWhiteSpace(displayName)) displayName = previous?.DisplayName ?? string.Empty;
        return new StoredTokens(access, refresh, expiresAt, displayName, apiOrigin);
    }

    private static DateTimeOffset? ParseExpiration(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericEpoch))
        {
            return FromUnixEpoch(numericEpoch);
        }

        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (long.TryParse(text, out numericEpoch)) return FromUnixEpoch(numericEpoch);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset FromUnixEpoch(long value) =>
        value is >= 100_000_000_000 or <= -100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);

    private static HttpRequestMessage JsonRequest(HttpMethod method, string relativeUrl, byte[] body) =>
        new(method, relativeUrl) { Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } };

    private static JsonElement ParsePayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var summary = await ReadSafeErrorSummaryAsync(response, cancellationToken).ConfigureAwait(false);
        var status = $"API 请求失败 ({(int)response.StatusCode})";
        throw new HttpRequestException(
            string.IsNullOrEmpty(summary) ? status + "。" : status + $"：{summary}",
            null,
            response.StatusCode);
    }

    private static async Task<string> ReadSafeErrorSummaryAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > MaxErrorBodyBytes) return string.Empty;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[4 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxErrorBodyBytes) return string.Empty;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false },
                cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;

            var fields = new List<string>(SafeErrorFieldNames.Length);
            foreach (var fieldName in SafeErrorFieldNames)
            {
                if (!TryGetDirectProperty(document.RootElement, fieldName, out var value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var safeValue = SanitizeErrorField(value.GetString());
                if (!string.IsNullOrEmpty(safeValue)) fields.Add($"{fieldName}={safeValue}");
            }
            return string.Join("；", fields);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            // Non-JSON and malformed bodies are intentionally never echoed.
            return string.Empty;
        }
    }

    private static bool TryGetDirectProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string SanitizeErrorField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (SensitiveErrorTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return "[REDACTED]";
        }
        return normalized.Length <= MaxSafeErrorFieldLength
            ? normalized
            : normalized[..MaxSafeErrorFieldLength] + "…";
    }

    private void EnsureConfigured()
    {
        if (_baseAddress is null || _apiOrigin is null)
        {
            throw new InvalidOperationException("尚未配置 API 地址。");
        }
    }

    private Uri ResolveUri(string relativeUrl)
    {
        EnsureConfigured();
        return new Uri(_baseAddress!, relativeUrl);
    }

    private void PrepareRequestUri(HttpRequestMessage request)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("API 请求缺少地址。");
        }

        var target = request.RequestUri.IsAbsoluteUri
            ? request.RequestUri
            : ResolveUri(request.RequestUri.OriginalString);
        if (!string.Equals(NormalizeOrigin(target), CurrentOrigin(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝向已登录 API 之外的源站发送授权请求。");
        }
        request.RequestUri = target;
    }

    private async Task<StoredTokens?> LoadBoundTokensUnsafeAsync(CancellationToken cancellationToken)
    {
        var stored = await _tokens.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null || IsBoundToOrigin(stored, CurrentOrigin()))
        {
            return stored;
        }

        await _tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
        return null;
    }

    private string CurrentOrigin()
    {
        EnsureConfigured();
        return _apiOrigin!;
    }

    private static bool IsBoundToOrigin(StoredTokens tokens, string origin) =>
        !string.IsNullOrWhiteSpace(tokens.ApiOrigin) &&
        string.Equals(tokens.ApiOrigin, origin, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeOrigin(Uri address)
    {
        var host = address.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Contains(':', StringComparison.Ordinal)) host = $"[{host}]";
        return $"{address.Scheme.ToLowerInvariant()}://{host}:{address.Port}";
    }

    private static bool IsLoopbackHost(Uri address) =>
        address.IsLoopback ||
        string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(address.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(address.Host, "::1", StringComparison.OrdinalIgnoreCase);

    internal static bool TryValidateBaseAddress(string apiBaseUrl, out Uri address)
    {
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            address = null!;
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        }
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }
}
