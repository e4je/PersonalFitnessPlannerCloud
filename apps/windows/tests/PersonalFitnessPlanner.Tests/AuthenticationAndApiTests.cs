using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PersonalFitnessPlanner.Contracts;
using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Data;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Network;
using PersonalFitnessPlanner.Infrastructure.Persistence;
using PersonalFitnessPlanner.Infrastructure.Security;

namespace PersonalFitnessPlanner.Tests;

public sealed class AuthenticationAndApiTests
{
    [Fact]
    public void JwtRoleParser_RecognizesSupportedBackendClaims_AndFailsClosed()
    {
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var role = JwtRoleParser.Parse(CreateJwt(new Dictionary<string, object>
        {
            ["sub"] = "user-1",
            ["name"] = "管理员",
            ["role"] = "admin",
            ["exp"] = future,
        }));
        var roles = JwtRoleParser.Parse(CreateJwt(new Dictionary<string, object>
        {
            ["roles"] = new[] { "user", "administrator" },
            ["exp"] = future,
        }));
        var realm = JwtRoleParser.Parse(CreateJwt(new Dictionary<string, object>
        {
            ["realm_access"] = new { roles = new[] { "user", "admin" } },
            ["exp"] = future,
        }));
        var expiredToken = CreateJwt(new Dictionary<string, object>
        {
            ["role"] = "admin",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
        });
        var expired = JwtRoleParser.ToAuthenticationState(
            new StoredTokens(expiredToken, "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.True(role.IsValid);
        Assert.True(role.IsAdmin);
        Assert.Equal("user-1", role.Subject);
        Assert.Equal("管理员", role.DisplayName);
        Assert.True(roles.IsAdmin);
        Assert.True(realm.IsAdmin);
        Assert.False(JwtRoleParser.Parse("not-a-jwt").IsValid);
        Assert.False(expired.IsAuthenticated);
        Assert.False(expired.IsAdmin);
    }

    [Fact]
    public void ConfigureBaseAddress_RequiresHttpsExceptForLoopback()
    {
        using var temporary = new TemporaryDirectory("API 地址 安全");
        using var http = new HttpClient(new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, "{}")));
        var client = new FitnessApiClient(http, new DpapiTokenStore(new AppPaths(temporary.Path)));

        Assert.Throws<ArgumentException>(() => client.ConfigureBaseAddress("http://fitness.example.com"));
        Assert.Throws<ArgumentException>(() => client.ConfigureBaseAddress("https://user:pass@fitness.example.com"));
        Assert.Throws<ArgumentException>(() => client.ConfigureBaseAddress("https://fitness.example.com?next=https://attacker.invalid"));
        Assert.Throws<ArgumentException>(() => client.ConfigureBaseAddress("https://fitness.example.com/#fragment"));
        client.ConfigureBaseAddress("http://localhost:8000/base/");
        Assert.Equal(new Uri("http://localhost:8000/base/"), client.BaseAddress);
        client.ConfigureBaseAddress("https://fitness.example.com/api-root");
        Assert.Equal(new Uri("https://fitness.example.com/api-root/"), client.BaseAddress);
    }

    [Fact]
    public async Task ConfigureBaseAddress_DeletesLegacyAndCrossOriginCredentialsBeforeAnyRequest()
    {
        using var temporary = new TemporaryDirectory("API 源站令牌绑定");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, "{\"changes\":[],\"next_cursor\":\"\"}"));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);

        await tokenStore.SaveAsync(TokenForRole("user") with { ApiOrigin = "" });
        await client.ConfigureBaseAddressAsync("https://fitness.example.com/api/");
        Assert.Null(await tokenStore.LoadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetChangesAsync(string.Empty));
        Assert.Empty(handler.Requests);

        await tokenStore.SaveAsync(TokenForRole("user"));
        await client.ConfigureBaseAddressAsync("https://attacker.example.net/");
        Assert.Null(await tokenStore.LoadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetChangesAsync(string.Empty));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConfigureBaseAddress_PreservesCredentialAcrossPathsOnSameNormalizedOrigin()
    {
        using var temporary = new TemporaryDirectory("API 同源路径切换");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        using var http = new HttpClient(new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, "{}")));
        var client = new FitnessApiClient(http, tokenStore);
        await client.ConfigureBaseAddressAsync("https://FITNESS.example.com:443/api-one/");
        await tokenStore.SaveAsync(TokenForRole("user"));

        await client.ConfigureBaseAddressAsync("https://fitness.example.com/api-two/");

        Assert.NotNull(await tokenStore.LoadAsync());
        Assert.Equal(new Uri("https://fitness.example.com/api-two/"), client.BaseAddress);
    }

    [Fact]
    public async Task AuthorizedRequest_CannotDeadlockWhenConfigurationChangeWaitsForIt()
    {
        using var temporary = new TemporaryDirectory("API 配置并发切换");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        await tokenStore.SaveAsync(TokenForRole("user"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new BlockingHandler(entered, release));
        var client = new FitnessApiClient(http, tokenStore);
        await client.ConfigureBaseAddressAsync("https://fitness.example.com/");

        var requestTask = client.GetChangesAsync(string.Empty);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var configureTask = client.ConfigureBaseAddressAsync("https://attacker.example.net/");
        Assert.False(configureTask.IsCompleted);
        release.SetResult();

        await requestTask.WaitAsync(TimeSpan.FromSeconds(5));
        await configureTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await tokenStore.LoadAsync());
    }

    [Fact]
    public async Task AdminRequest_RejectsNonAdminWithoutNetwork_AndClearsStaleTokenOnForbidden()
    {
        using var temporary = new TemporaryDirectory("管理权限 JWT");
        var paths = new AppPaths(temporary.Path);
        var tokenStore = new DpapiTokenStore(paths);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Forbidden, "{\"detail\":\"forbidden\"}"));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");
        await tokenStore.SaveAsync(TokenForRole("user"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.SendAdminAsync(HttpMethod.Post, "api/v1/admin/plans"));
        Assert.Empty(handler.Requests);

        await tokenStore.SaveAsync(TokenForRole("admin"));
        var forbidden = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAdminAsync(HttpMethod.Post, "api/v1/admin/plans", new { name = "测试" }));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Null(await tokenStore.LoadAsync());
    }

    [Fact]
    public async Task ApiError_ReportsOnlyBoundedWhitelistedStringsAndNeverEchoesSensitiveBodyData()
    {
        using var temporary = new TemporaryDirectory("API 错误脱敏");
        var sensitiveBody = JsonSerializer.Serialize(new
        {
            code = "invalid_request",
            message = "Request could not be processed",
            detail = "Validation failed",
            password = "password-value-should-never-appear",
            access_token = "access-value-should-never-appear",
            validation = new[] { new { input = "private-input-should-never-appear" } },
            unexpected = "whole-body-marker-should-never-appear"
        });
        using var http = new HttpClient(new RecordingHandler(_ =>
            JsonResponse(HttpStatusCode.UnprocessableEntity, sensitiveBody)));
        var client = new FitnessApiClient(http, new DpapiTokenStore(new AppPaths(temporary.Path)));
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.LoginAsync("private-login@example.com", "private-login-password"));

        Assert.Contains("422", exception.Message);
        Assert.Contains("code=invalid_request", exception.Message);
        Assert.Contains("message=Request could not be processed", exception.Message);
        Assert.Contains("detail=Validation failed", exception.Message);
        Assert.DoesNotContain("password-value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-input", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("whole-body-marker", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiError_RedactsSensitiveWhitelistedTextAndIgnoresStructuredValidationDetail()
    {
        using var temporary = new TemporaryDirectory("API 错误敏感白名单字段");
        var call = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            call++;
            return call == 1
                ? JsonResponse(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new
                {
                    code = "bad_credentials",
                    message = "password=hunter2",
                    detail = "access_token=eyJ-secret-token"
                }))
                : JsonResponse(HttpStatusCode.UnprocessableEntity, JsonSerializer.Serialize(new
                {
                    code = 422,
                    message = new { text = "not-a-string-marker" },
                    detail = new[] { new { input = "validation-input-marker" } }
                }));
        }));
        var client = new FitnessApiClient(http, new DpapiTokenStore(new AppPaths(temporary.Path)));
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var sensitive = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.LoginAsync("user@example.com", "password"));
        var structured = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.LoginAsync("user@example.com", "password"));

        Assert.Contains("message=[REDACTED]", sensitive.Message);
        Assert.Contains("detail=[REDACTED]", sensitive.Message);
        Assert.DoesNotContain("hunter2", sensitive.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ-secret-token", sensitive.Message, StringComparison.Ordinal);
        Assert.Equal("API 请求失败 (422)。", structured.Message);
        Assert.DoesNotContain("validation-input-marker", structured.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-string-marker", structured.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiError_DoesNotEchoPlainTextOrOversizedBodyOrReasonPhrase()
    {
        using var temporary = new TemporaryDirectory("API 非 JSON 错误不回显");
        var call = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            call++;
            var response = call == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("plain-body-secret-marker", Encoding.UTF8, "text/plain")
                }
                : JsonResponse(HttpStatusCode.BadGateway, "{\"message\":\"" + new string('x', 33 * 1024) + "\"}");
            response.ReasonPhrase = "reason-phrase-secret-marker";
            return response;
        }));
        var client = new FitnessApiClient(http, new DpapiTokenStore(new AppPaths(temporary.Path)));
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var plain = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.LoginAsync("user@example.com", "password"));
        var oversized = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.LoginAsync("user@example.com", "password"));

        Assert.Equal("API 请求失败 (502)。", plain.Message);
        Assert.Equal("API 请求失败 (502)。", oversized.Message);
        Assert.DoesNotContain("plain-body-secret-marker", plain.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reason-phrase-secret-marker", plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedRequest_RefreshesTokenOnce_AndRetriesWithNewBearerToken()
    {
        using var temporary = new TemporaryDirectory("令牌 刷新");
        var paths = new AppPaths(temporary.Path);
        var tokenStore = new DpapiTokenStore(paths);
        var oldToken = CreateJwt(new { sub = "u", roles = new[] { "user" }, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
        var newToken = CreateJwt(new { sub = "u", roles = new[] { "user" }, exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() });
        await tokenStore.SaveAsync(new StoredTokens(
            oldToken,
            "refresh-old",
            DateTimeOffset.UtcNow.AddHours(1),
            ApiOrigin: "https://fitness.example.com:443"));

        var changesAttempts = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.PathAndQuery.StartsWith("/api/v1/auth/refresh", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = newToken,
                    refresh_token = "refresh-new",
                    expires_in = 7200,
                }));
            }

            if (request.PathAndQuery.StartsWith("/api/v1/sync/changes", StringComparison.Ordinal))
            {
                changesAttempts++;
                return changesAttempts == 1
                    ? JsonResponse(HttpStatusCode.Unauthorized, "{\"detail\":\"expired\"}")
                    : JsonResponse(HttpStatusCode.OK, "{\"changes\":[],\"next_cursor\":\"cursor-2\"}");
            }

            return JsonResponse(HttpStatusCode.NotFound, "{}");
        });
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var page = await client.GetChangesAsync("cursor 1");

        Assert.Equal("cursor-2", page.Cursor);
        Assert.Empty(page.Changes);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("cursor=cursor%201", handler.Requests[0].PathAndQuery);
        Assert.Equal("Bearer " + oldToken, handler.Requests[0].Authorization);
        Assert.Equal("/api/v1/auth/refresh", handler.Requests[1].PathAndQuery);
        Assert.Contains("refresh-old", handler.Requests[1].Body);
        Assert.Equal("Bearer " + newToken, handler.Requests[2].Authorization);
        Assert.Equal(newToken, (await tokenStore.LoadAsync())!.AccessToken);
    }

    [Fact]
    public async Task Login_ParsesNumericEpochExpiration()
    {
        using var temporary = new TemporaryDirectory("数字 epoch 令牌");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        var accessToken = CreateJwt(new
        {
            sub = "epoch-user",
            role = "user",
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        });
        var expectedExpiration = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            refresh_token = "refresh-epoch",
            expires_at = expectedExpiration.ToUnixTimeSeconds()
        })));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        await client.LoginAsync("epoch@example.com", "password");

        Assert.Equal(expectedExpiration, (await tokenStore.LoadAsync())!.ExpiresAt);
        Assert.Equal("https://fitness.example.com:443", (await tokenStore.LoadAsync())!.ApiOrigin);
    }

    [Fact]
    public async Task UnauthorizedRequest_ClearsTokenWhenRefreshFails()
    {
        using var temporary = new TemporaryDirectory("刷新失败清除令牌");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        await tokenStore.SaveAsync(TokenForRole("user"));
        var handler = new RecordingHandler(request =>
            request.PathAndQuery == "/api/v1/auth/refresh"
                ? JsonResponse(HttpStatusCode.Unauthorized, "{\"detail\":\"refresh expired\"}")
                : JsonResponse(HttpStatusCode.Unauthorized, "{\"detail\":\"expired\"}"));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetChangesAsync(string.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(await tokenStore.LoadAsync());
    }

    [Fact]
    public async Task UnauthorizedRequest_ClearsRefreshedTokenWhenRetryIsStillUnauthorized()
    {
        using var temporary = new TemporaryDirectory("刷新后 401 清除令牌");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        await tokenStore.SaveAsync(TokenForRole("user"));
        var refreshedToken = CreateJwt(new
        {
            sub = "test-user",
            role = "user",
            exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        });
        var handler = new RecordingHandler(request =>
            request.PathAndQuery == "/api/v1/auth/refresh"
                ? JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = refreshedToken,
                    refresh_token = "refresh-new",
                    expires_in = 7200
                }))
                : JsonResponse(HttpStatusCode.Unauthorized, "{\"detail\":\"unauthorized\"}"));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetChangesAsync(string.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Null(await tokenStore.LoadAsync());
    }

    [Fact]
    public async Task SendBatch_OnlyAcknowledgesExplicitAcceptedOrSuccessfulResults()
    {
        using var temporary = new TemporaryDirectory("同步 接收列表");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        await tokenStore.SaveAsync(TokenForRole("user"));
        var first = CreateOutbox("first");
        var second = CreateOutbox("second");
        var unrelated = Guid.NewGuid();
        var call = 0;
        var handler = new RecordingHandler(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse(HttpStatusCode.OK, "{\"accepted\":[]}"),
                2 => JsonResponse(HttpStatusCode.OK,
                    $"{{\"accepted_outbox_ids\":[\"{second.Id:D}\",\"{unrelated:D}\"]}}"),
                3 => JsonResponse(HttpStatusCode.OK, "{}"),
                4 => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => JsonResponse(HttpStatusCode.OK,
                    $"{{\"results\":[" +
                    $"{{\"client_outbox_id\":\"{first.Id:D}\",\"status\":\"duplicate\"}}," +
                    $"{{\"client_outbox_id\":\"{second.Id:D}\",\"status\":\"conflict\"," +
                    $"\"error\":\"version_conflict\",\"server_version\":9," +
                    $"\"server_copy\":{{\"id\":\"{second.EntityId:D}\",\"weight_kg\":45}}}}," +
                    $"{{\"client_outbox_id\":\"{unrelated:D}\",\"status\":\"accepted\"}}]}}")
            };
        });
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var empty = await client.SendBatchAsync([first, second]);
        var subset = await client.SendBatchAsync([first, second]);
        var unknown = await client.SendBatchAsync([first, second]);
        var noContent = await client.SendBatchAsync([first, second]);
        var resultStatuses = await client.SendBatchAsync([first, second]);

        Assert.Empty(empty.AcceptedOutboxIds);
        Assert.Equal(new[] { second.Id }, subset.AcceptedOutboxIds);
        Assert.Empty(unknown.AcceptedOutboxIds);
        Assert.Empty(noContent.AcceptedOutboxIds);
        Assert.Equal(new[] { first.Id }, resultStatuses.AcceptedOutboxIds);
        var failure = Assert.Single(resultStatuses.Failures);
        Assert.Equal(second.Id, failure.OutboxId);
        Assert.Equal("conflict", failure.Status);
        Assert.Equal("version_conflict", failure.Error);
        Assert.Equal(9, failure.ServerVersion);
        Assert.Contains("\"weight_kg\":45", failure.ServerCopyJson);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("/api/v1/sync/batch", request.PathAndQuery);
            Assert.StartsWith("sync-batch:", request.IdempotencyKey);
            Assert.Contains(first.IdempotencyKey, request.Body);
        });
    }

    [Fact]
    public async Task GetChanges_ParsesCanonicalChangedAtAndEntityVersion()
    {
        using var temporary = new TemporaryDirectory("增量 changed_at 解析");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        await tokenStore.SaveAsync(TokenForRole("user"));
        var entityId = Guid.NewGuid();
        var changedAt = new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.Zero);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            changes = new[]
            {
                new
                {
                    entity_type = "user",
                    entity_id = entityId,
                    operation = "UPSERT",
                    version = 17,
                    changed_at = changedAt,
                    payload = new { id = entityId }
                }
            },
            next_cursor = "cursor-17",
            full_resync_required = true
        })));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var page = await client.GetChangesAsync(string.Empty);

        var change = Assert.Single(page.Changes);
        Assert.Equal(changedAt, change.UpdatedAt);
        Assert.Equal(17, change.Version);
        Assert.Equal(entityId, change.EntityId);
        Assert.True(page.FullResyncRequired);
    }

    [Fact]
    public async Task SyncBatchFailure_PersistsServerDetailAndKeepsOutboxUnprocessed()
    {
        using var temporary = new TemporaryDirectory("同步逐项失败详情");
        var paths = new AppPaths(temporary.Path);
        var database = new SqliteDatabase(paths);
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        await repository.StartWorkoutAsync("A", new DateOnly(2026, 8, 9));
        var outbox = Assert.Single(await repository.GetPendingOutboxAsync());
        var tokenStore = new DpapiTokenStore(paths);
        await tokenStore.SaveAsync(TokenForRole("user"));
        var handler = new RecordingHandler(request => request.PathAndQuery switch
        {
            "/api/v1/sync/batch" => JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                results = new[]
                {
                    new
                    {
                        client_outbox_id = outbox.Id,
                        status = "conflict",
                        error = "expected_version_mismatch",
                        server_version = 12,
                        server_copy = new { id = outbox.EntityId, status = "COMPLETED" }
                    }
                },
                accepted_outbox_ids = Array.Empty<Guid>()
            })),
            "/api/v1/bootstrap" => JsonResponse(HttpStatusCode.OK,
                "{\"exercises\":[],\"equipment\":[],\"assignments\":[],\"workout_sessions\":[]," +
                "\"readiness\":[],\"cardio_sessions\":[],\"sync_cursor\":\"failure-cursor\"}"),
            "/api/v1/sync/changes?cursor=failure-cursor" => JsonResponse(HttpStatusCode.OK,
                "{\"changes\":[],\"next_cursor\":\"failure-cursor\"}"),
            _ => JsonResponse(HttpStatusCode.NotFound, "{}")
        });
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var result = await new SyncService(repository, client).SynchronizeAsync();

        Assert.False(result.Success);
        Assert.Contains("expected_version_mismatch", result.Message);
        var status = await repository.GetOutboxStatusAsync();
        Assert.Equal(1, status.Failed);
        Assert.Contains("expected_version_mismatch", status.LastError);
        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT o.status, o.processed_at IS NULL, o.last_error,
                c.resolution, c.resolved_at IS NULL, c.server_json
            FROM outbox o
            JOIN sync_conflicts c ON c.entity_type=o.entity_type AND c.entity_id=o.entity_id
            WHERE o.id=$id;
            """;
        verify.Parameters.AddWithValue("$id", outbox.Id.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Contains("server_version=12", reader.GetString(2));
        Assert.Equal("outbox_failed", reader.GetString(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Contains("COMPLETED", reader.GetString(5));
    }

    [Fact]
    public async Task ManagementMethods_CallConcreteAdminEndpointsWithIdempotencyKeys()
    {
        using var temporary = new TemporaryDirectory("管理 API 端点");
        var tokenStore = new DpapiTokenStore(new AppPaths(temporary.Path));
        await tokenStore.SaveAsync(TokenForRole("admin"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");
        var exercise = new ExerciseLibraryItem(
            Guid.NewGuid(), "测试动作", "胸部", "器械", "3×8～12", "要点", "错误", "替代", 1, "draft");
        var plan = await new DefaultPlanLoader().LoadAsync();

        await client.PublishExerciseAsync(exercise);
        await client.PublishPlanAsync(plan);
        await client.AssignPlanAsync(plan.Id);

        Assert.Equal(
            new[]
            {
                "/api/v1/admin/exercises",
                "/api/v1/admin/plans",
                $"/api/v1/admin/plans/{plan.PlanId:D}/versions",
                $"/api/v1/admin/plan-versions/{plan.Id:D}/publish",
                "/api/v1/admin/assignments",
            },
            handler.Requests.Select(request => request.PathAndQuery));
        Assert.All(handler.Requests, request => Assert.False(string.IsNullOrWhiteSpace(request.IdempotencyKey)));
        var versionRequest = Assert.Single(handler.Requests, request =>
            request.PathAndQuery == $"/api/v1/admin/plans/{plan.PlanId:D}/versions");
        using var versionBody = JsonDocument.Parse(versionRequest.Body);
        Assert.Equal(plan.WeeklyStrengthTarget, versionBody.RootElement.GetProperty("weekly_frequency").GetInt32());
        Assert.Equal(plan.MinimumRestDays, versionBody.RootElement.GetProperty("min_rest_days").GetInt32());
        Assert.Equal(plan.FatigueThreshold, versionBody.RootElement.GetProperty("fatigue_threshold").GetInt32());
    }

    [Fact]
    public async Task FirstIncrementalSync_AppliesBootstrapThenChanges_AndAdvancesCursor()
    {
        using var temporary = new TemporaryDirectory("首次 bootstrap 同步");
        var paths = new AppPaths(temporary.Path);
        var repository = new FitnessRepository(new SqliteDatabase(paths));
        await repository.InitializeAsync();
        var tokenStore = new DpapiTokenStore(paths);
        await tokenStore.SaveAsync(TokenForRole("user"));
        var handler = new RecordingHandler(request => request.PathAndQuery switch
        {
            "/api/v1/bootstrap" => JsonResponse(HttpStatusCode.OK,
                "{\"exercises\":[],\"equipment\":[],\"assignments\":[],\"workoutSessions\":[],\"readiness\":[],\"syncCursor\":\"bootstrap-1\"}"),
            "/api/v1/sync/changes?cursor=bootstrap-1" => JsonResponse(HttpStatusCode.OK,
                "{\"changes\":[],\"next_cursor\":\"changes-2\"}"),
            _ => JsonResponse(HttpStatusCode.NotFound, "{}"),
        });
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var result = await new SyncService(repository, client).SynchronizeAsync();

        Assert.True(result.Success);
        Assert.Equal("changes-2", await repository.GetSyncCursorAsync());
        Assert.Equal(
            new[] { "/api/v1/bootstrap", "/api/v1/sync/changes?cursor=bootstrap-1" },
            handler.Requests.Select(request => request.PathAndQuery));
    }

    [Fact]
    public async Task RetentionGap_AppliesFullBootstrapBeforeContinuingFromBootstrapCursor()
    {
        using var temporary = new TemporaryDirectory("增量保留窗口缺口");
        var paths = new AppPaths(temporary.Path);
        var repository = new FitnessRepository(new SqliteDatabase(paths));
        await repository.InitializeAsync();
        await repository.SetSyncCursorAsync("stale-cursor");
        var tokenStore = new DpapiTokenStore(paths);
        await tokenStore.SaveAsync(TokenForRole("user"));
        var handler = new RecordingHandler(request => request.PathAndQuery switch
        {
            "/api/v1/sync/changes?cursor=stale-cursor" => JsonResponse(HttpStatusCode.OK,
                "{\"changes\":[],\"next_cursor\":\"must-not-commit\",\"full_resync_required\":true}"),
            "/api/v1/bootstrap" => JsonResponse(HttpStatusCode.OK,
                "{\"exercises\":[],\"equipment\":[],\"assignments\":[],\"workout_sessions\":[],\"readiness\":[],\"cardio_sessions\":[],\"sync_cursor\":\"bootstrap-recovered\"}"),
            "/api/v1/sync/changes?cursor=bootstrap-recovered" => JsonResponse(HttpStatusCode.OK,
                "{\"changes\":[],\"next_cursor\":\"after-bootstrap\"}"),
            _ => JsonResponse(HttpStatusCode.NotFound, "{}"),
        });
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var result = await new SyncService(repository, client).SynchronizeAsync();

        Assert.True(result.Success);
        Assert.Equal("after-bootstrap", await repository.GetSyncCursorAsync());
        Assert.Equal(
            new[]
            {
                "/api/v1/sync/changes?cursor=stale-cursor",
                "/api/v1/bootstrap",
                "/api/v1/sync/changes?cursor=bootstrap-recovered"
            },
            handler.Requests.Select(request => request.PathAndQuery));
        Assert.DoesNotContain(handler.Requests, request =>
            request.PathAndQuery.Contains("must-not-commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullResync_PreservesLocalDraftAndOutbox_WhileReplacingServerCaches()
    {
        using var temporary = new TemporaryDirectory("完整重新同步 保留本地");
        var paths = new AppPaths(temporary.Path);
        var repository = new FitnessRepository(new SqliteDatabase(paths));
        await repository.InitializeAsync();
        var draft = await repository.CreatePlanDraftAsync();
        await repository.StartWorkoutAsync("A", new DateOnly(2026, 8, 9));
        var pendingBefore = (await repository.GetOutboxStatusAsync()).Pending;
        var tokenStore = new DpapiTokenStore(paths);
        await tokenStore.SaveAsync(TokenForRole("user"));
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK,
            "{\"exercises\":[],\"equipment\":[],\"assignments\":[],\"workoutSessions\":[],\"readiness\":[],\"sync_cursor\":\"full-9\"}"));
        using var http = new HttpClient(handler);
        var client = new FitnessApiClient(http, tokenStore);
        client.ConfigureBaseAddress("https://fitness.example.com/");

        var result = await new SyncService(repository, client).FullResynchronizeAsync();

        Assert.True(result.Success);
        Assert.Equal("full-9", await repository.GetSyncCursorAsync());
        Assert.Contains(await repository.GetPlanVersionsAsync(), plan => plan.Id == draft.Id && plan.Status == "draft");
        Assert.Equal(pendingBefore, (await repository.GetOutboxStatusAsync()).Pending);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/bootstrap", handler.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task AccountSwitch_ClearsPreviousHealthAndCursor_BeforeBootstrappingNewSubject()
    {
        using var temporary = new TemporaryDirectory("账号 A 到 B 缓存隔离");
        var paths = new AppPaths(temporary.Path);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var readinessA = Guid.NewGuid();
        var readinessB = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(request =>
        {
            if (request.PathAndQuery == "/api/v1/auth/login")
            {
                var isB = request.Body.Contains("b@example.com", StringComparison.OrdinalIgnoreCase);
                var subject = isB ? userB : userA;
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = CreateJwt(new
                    {
                        sub = subject.ToString("D"),
                        role = "user",
                        exp = expiry.ToUnixTimeSeconds()
                    }),
                    refresh_token = $"refresh-{subject:D}",
                    expires_in = 3600
                }));
            }

            if (request.PathAndQuery == "/api/v1/bootstrap")
            {
                return JsonResponse(HttpStatusCode.OK, ContractJson.Serialize(new BootstrapDto
                {
                    User = new UserDto
                    {
                        Id = userB,
                        Email = "b@example.com",
                        DisplayName = "用户 B",
                        Timezone = "Asia/Shanghai",
                        WeightUnit = "KG",
                        UpdatedAt = now
                    },
                    Readiness =
                    [
                        new ReadinessDto
                        {
                            Id = readinessB,
                            UserId = userB,
                            LocalDate = new DateOnly(2026, 8, 11),
                            FatigueScore = 3,
                            SleepQuality = 5,
                            UpdatedAt = now
                        }
                    ],
                    SyncCursor = "b-bootstrap-cursor"
                }));
            }

            if (request.PathAndQuery.StartsWith("/api/v1/sync/changes", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK,
                    "{\"changes\":[],\"next_cursor\":\"b-bootstrap-cursor\",\"has_more\":false}");
            return JsonResponse(HttpStatusCode.NotFound, "{}");
        });
        using var http = new HttpClient(handler);
        using var service = new AppDataService(paths, http);
        await service.InitializeAsync();

        await service.LoginAsync("a@example.com", "password");
        await service.Repository.ApplyBootstrapAsync(ContractJson.Serialize(new BootstrapDto
        {
            User = new UserDto
            {
                Id = userA,
                Email = "a@example.com",
                DisplayName = "用户 A",
                Timezone = "Asia/Shanghai",
                WeightUnit = "KG",
                UpdatedAt = now
            },
            Readiness =
            [
                new ReadinessDto
                {
                    Id = readinessA,
                    UserId = userA,
                    LocalDate = new DateOnly(2026, 8, 10),
                    FatigueScore = 9,
                    SleepQuality = 2,
                    UpdatedAt = now
                }
            ]
        }));
        await service.Repository.SetSyncCursorAsync("a-cursor");
        Assert.Equal(readinessA, (await service.Repository.GetLatestReadinessAsync())!.Id);

        await service.LoginAsync("b@example.com", "password");

        Assert.Equal(string.Empty, await service.Repository.GetSyncCursorAsync());
        Assert.Null(await service.Repository.GetLatestReadinessAsync());
        Assert.Equal(userB.ToString("D"), JwtRoleParser.Parse((await service.Tokens.LoadAsync())!.AccessToken).Subject);

        var sync = await service.SynchronizeAsync();

        Assert.True(sync.Success);
        Assert.Equal(readinessB, (await service.Repository.GetLatestReadinessAsync())!.Id);
        Assert.Equal("b-bootstrap-cursor", await service.Repository.GetSyncCursorAsync());
        Assert.Contains(handler.Requests, request => request.PathAndQuery == "/api/v1/bootstrap");
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.Contains("a-cursor", StringComparison.Ordinal));
        await using var connection = await service.Database.OpenConnectionAsync();
        await using var users = connection.CreateCommand();
        users.CommandText = "SELECT id FROM user_cache WHERE deleted_at IS NULL;";
        Assert.Equal(userB.ToString("D"), await users.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AccountSwitch_WithPendingOutbox_IsBlockedAndClearsNewCredential()
    {
        using var temporary = new TemporaryDirectory("账号切换 待上传保护");
        var paths = new AppPaths(temporary.Path);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var handler = new RecordingHandler(request =>
        {
            if (request.PathAndQuery != "/api/v1/auth/login")
                return JsonResponse(HttpStatusCode.NotFound, "{}");
            var subject = request.Body.Contains("b@example.com", StringComparison.OrdinalIgnoreCase) ? userB : userA;
            return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                access_token = CreateJwt(new
                {
                    sub = subject.ToString("D"),
                    role = "user",
                    exp = expiry.ToUnixTimeSeconds()
                }),
                refresh_token = $"refresh-{subject:D}",
                expires_in = 3600
            }));
        });
        using var http = new HttpClient(handler);
        using var service = new AppDataService(paths, http);
        await service.InitializeAsync();
        await service.LoginAsync("a@example.com", "password");
        var readinessId = Guid.NewGuid();
        await service.Repository.SaveReadinessAsync(new DailyReadinessData(
            readinessId,
            new DateOnly(2026, 8, 11),
            8,
            3,
            "",
            "等待账号 A 上传"));
        await service.Repository.SetSyncCursorAsync("a-cursor");

        var blocked = await Assert.ThrowsAsync<AccountSwitchBlockedException>(
            () => service.LoginAsync("b@example.com", "password"));

        Assert.Equal(1, blocked.PendingOutboxCount);
        Assert.Null(await service.Tokens.LoadAsync());
        Assert.Equal("a-cursor", await service.Repository.GetSyncCursorAsync());
        Assert.Equal(readinessId, (await service.Repository.GetLatestReadinessAsync())!.Id);
        Assert.Equal(1, (await service.Repository.GetOutboxStatusAsync()).Pending);
    }

    private static StoredTokens TokenForRole(string role)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        return new StoredTokens(
            CreateJwt(new { sub = "test-user", role, exp = expiry.ToUnixTimeSeconds() }),
            "refresh-token",
            expiry,
            "测试用户",
            "https://fitness.example.com:443");
    }

    private static OutboxItem CreateOutbox(string key) => new(
        Guid.NewGuid(),
        "workout_set",
        Guid.NewGuid(),
        "upsert",
        "idempotency:" + key,
        "{\"value\":1}",
        0,
        DateTimeOffset.UtcNow);

    private static string CreateJwt(object payload) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" })) + "." +
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload)) + ".signature";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedRequest(
        string Method,
        string PathAndQuery,
        string Authorization,
        string IdempotencyKey,
        string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _response;

        public RecordingHandler(Func<CapturedRequest, HttpResponseMessage> response) => _response = response;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var capture = new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : string.Empty,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(capture);
            return _response(capture);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;

        public BlockingHandler(TaskCompletionSource entered, TaskCompletionSource release)
        {
            _entered = entered;
            _release = release;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _entered.SetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{\"changes\":[],\"next_cursor\":\"\"}");
        }
    }
}
