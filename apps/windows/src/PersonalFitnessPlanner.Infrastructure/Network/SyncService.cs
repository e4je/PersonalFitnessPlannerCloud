using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Infrastructure.Network;

public sealed class SyncService
{
    private readonly FitnessRepository _repository;
    private readonly FitnessApiClient _apiClient;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public SyncService(FitnessRepository repository, FitnessApiClient apiClient)
    {
        _repository = repository;
        _apiClient = apiClient;
    }

    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new SyncResult(false, 0, 0, "同步已在进行中。");
        }

        IReadOnlyList<OutboxItem> pending = [];
        try
        {
            pending = await _repository.ClaimPendingOutboxAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var uploaded = 0;
            var failedUploads = 0;
            var firstUploadError = string.Empty;
            if (pending.Count > 0)
            {
                var batch = await _apiClient.SendBatchAsync(pending, cancellationToken).ConfigureAwait(false);
                await _repository.MarkOutboxSucceededAsync(batch.AcceptedOutboxIds, cancellationToken).ConfigureAwait(false);
                await _repository.RecordSyncBatchFailuresAsync(batch.Failures, cancellationToken).ConfigureAwait(false);
                uploaded = batch.AcceptedOutboxIds.Count;
                failedUploads = batch.Failures.Count;
                firstUploadError = batch.Failures.FirstOrDefault() is { } failure
                    ? string.IsNullOrWhiteSpace(failure.Error) ? failure.Status : $"{failure.Status}: {failure.Error}"
                    : string.Empty;
                var acceptedIds = batch.AcceptedOutboxIds.ToHashSet();
                var detailedFailureIds = batch.Failures.Select(item => item.OutboxId).ToHashSet();
                var rejectedIds = pending
                    .Where(x => !acceptedIds.Contains(x.Id) && !detailedFailureIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToArray();
                if (rejectedIds.Length > 0)
                {
                    await _repository.MarkOutboxFailedAsync(rejectedIds, "服务器未接受该同步操作。", cancellationToken).ConfigureAwait(false);
                    failedUploads += rejectedIds.Length;
                    if (string.IsNullOrWhiteSpace(firstUploadError)) firstUploadError = "服务器未返回该操作的确认结果。";
                }
            }

            var cursor = await _repository.GetSyncCursorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var downloaded = 0;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                var bootstrap = await _apiClient.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
                cursor = ReadCursor(bootstrap);
                if (string.IsNullOrWhiteSpace(cursor))
                    throw new IOException("服务器 bootstrap 未返回同步游标，未修改本地缓存。");
                downloaded += await _repository.ApplyBootstrapAsync(bootstrap.GetRawText(), cancellationToken).ConfigureAwait(false);
                await _repository.SetSyncCursorAsync(cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            var fullResyncAttempted = false;
            for (var pageNumber = 0; pageNumber < 100; pageNumber++)
            {
                var page = await _apiClient.GetChangesAsync(cursor, cancellationToken).ConfigureAwait(false);
                if (page.FullResyncRequired)
                {
                    if (fullResyncAttempted)
                    {
                        throw new IOException("服务器在完整 bootstrap 后仍要求重新同步，未推进本地游标。");
                    }

                    fullResyncAttempted = true;
                    var bootstrap = await _apiClient.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
                    var bootstrapCursor = ReadCursor(bootstrap);
                    if (string.IsNullOrWhiteSpace(bootstrapCursor))
                    {
                        throw new IOException("完整 bootstrap 未返回同步游标，未推进本地游标。");
                    }

                    downloaded += await _repository.ApplyFullBootstrapAsync(
                        bootstrap.GetRawText(), cancellationToken).ConfigureAwait(false);
                    cursor = bootstrapCursor;
                    await _repository.SetSyncCursorAsync(cursor, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await _repository.ApplyServerChangesAsync(page.Changes, cancellationToken).ConfigureAwait(false);
                var nextCursor = string.IsNullOrWhiteSpace(page.Cursor) ? cursor : page.Cursor;
                await _repository.SetSyncCursorAsync(nextCursor, cancellationToken: cancellationToken).ConfigureAwait(false);
                downloaded += page.Changes.Count;
                if (!page.HasMore || string.Equals(nextCursor, cursor, StringComparison.Ordinal)) break;
                cursor = nextCursor;
            }
            var success = failedUploads == 0;
            var message = success
                ? $"同步完成：上传 {uploaded}，下载 {downloaded}。"
                : $"同步完成但有 {failedUploads} 个上传操作失败，本地 Outbox 已保留：{firstUploadError}";
            return new SyncResult(success, uploaded, downloaded, message);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            if (pending.Count > 0)
            {
                await _repository.MarkOutboxFailedAsync(pending.Select(x => x.Id), exception.Message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return new SyncResult(false, 0, 0, $"暂时无法同步，本地记录已保留：{exception.Message}");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>Uploads pending local mutations and deliberately does not pull cloud data.</summary>
    public async Task<SyncResult> UploadLocalAsync(CancellationToken cancellationToken = default)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new SyncResult(false, 0, 0, "同步已在进行中。");

        IReadOnlyList<OutboxItem> pending = [];
        try
        {
            pending = await _repository.ClaimPendingOutboxAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
                return new SyncResult(true, 0, 0, "没有待上传的本地记录。");

            var batch = await _apiClient.SendBatchAsync(pending, cancellationToken).ConfigureAwait(false);
            await _repository.MarkOutboxSucceededAsync(batch.AcceptedOutboxIds, cancellationToken).ConfigureAwait(false);
            await _repository.RecordSyncBatchFailuresAsync(batch.Failures, cancellationToken).ConfigureAwait(false);
            var accepted = batch.AcceptedOutboxIds.Count;
            var detailedFailureIds = batch.Failures.Select(item => item.OutboxId).ToHashSet();
            var missing = pending
                .Where(item => !batch.AcceptedOutboxIds.Contains(item.Id) && !detailedFailureIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToArray();
            if (missing.Length > 0)
                await _repository.MarkOutboxFailedAsync(missing, "服务器未返回该操作的确认结果。", cancellationToken).ConfigureAwait(false);

            var failed = batch.Failures.Count + missing.Length;
            var detail = batch.Failures.FirstOrDefault() is { } failure
                ? (string.IsNullOrWhiteSpace(failure.Error) ? failure.Status : $"{failure.Status}: {failure.Error}")
                : "";
            return failed == 0
                ? new SyncResult(true, accepted, 0, $"本地记录上传完成：{accepted} 项。")
                : new SyncResult(false, accepted, 0, $"上传完成但有 {failed} 项失败，本地 Outbox 已保留：{detail}");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            if (pending.Count > 0)
                await _repository.MarkOutboxFailedAsync(pending.Select(item => item.Id), exception.Message, CancellationToken.None)
                    .ConfigureAwait(false);
            return new SyncResult(false, 0, 0, $"暂时无法上传，本地记录已保留：{exception.Message}");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>Replaces server-owned local caches from bootstrap without uploading local changes.</summary>
    public async Task<SyncResult> DownloadCloudOverwriteAsync(CancellationToken cancellationToken = default)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new SyncResult(false, 0, 0, "同步已在进行中。");
        try
        {
            var outbox = await _repository.GetOutboxStatusAsync(cancellationToken).ConfigureAwait(false);
            if (outbox.Pending > 0)
                return new SyncResult(false, 0, 0, $"云端覆盖已阻止：本地仍有 {outbox.Pending} 项待上传，请先上传或备份。");

            var bootstrap = await _apiClient.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            var cursor = ReadCursor(bootstrap);
            if (string.IsNullOrWhiteSpace(cursor))
                throw new IOException("云端 bootstrap 未返回同步游标。");
            var downloaded = await _repository.ApplyFullBootstrapAsync(bootstrap.GetRawText(), cancellationToken).ConfigureAwait(false);
            await _repository.SetSyncCursorAsync(cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SyncResult(true, 0, downloaded, $"云端数据已覆盖本地缓存：下载 {downloaded} 项。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return new SyncResult(false, 0, 0, $"云端覆盖失败，本地数据未丢失：{exception.Message}");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<SyncResult> FullResynchronizeAsync(CancellationToken cancellationToken = default)
    {
        return await DownloadCloudOverwriteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadCursor(System.Text.Json.JsonElement bootstrap)
    {
        foreach (var name in new[] { "sync_cursor", "syncCursor", "cursor" })
        {
            if (bootstrap.ValueKind == System.Text.Json.JsonValueKind.Object &&
                bootstrap.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
