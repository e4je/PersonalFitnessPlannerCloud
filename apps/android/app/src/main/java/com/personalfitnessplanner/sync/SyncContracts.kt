package com.personalfitnessplanner.sync

import com.personalfitnessplanner.data.remote.ApiService
import com.personalfitnessplanner.data.remote.BootstrapDto
import com.personalfitnessplanner.data.remote.SyncBatchRequestDto
import com.personalfitnessplanner.data.remote.SyncBatchResponseDto
import com.personalfitnessplanner.data.remote.SyncChangesDto
import java.nio.charset.StandardCharsets
import java.security.MessageDigest

enum class OutboxOperation(val wireValue: String) {
    UPSERT("UPSERT"),
    DELETE("DELETE"),
}

data class OutboxItem(
    val id: String,
    val entityType: String,
    val entityId: String,
    val operation: OutboxOperation,
    val payload: Map<String, Any?>? = null,
    /** Generated once when the local mutation is enqueued and persisted with it. */
    val idempotencyKey: String,
    val attemptCount: Int = 0,
)

/**
 * Room adapter boundary. Implementations must make each apply/replace operation transactional,
 * keep unsent client-owned records, and use versions/soft deletes when applying changes.
 */
interface SyncLocalStore {
    suspend fun pendingOutbox(limit: Int): List<OutboxItem>
    /** Number of local mutations that have not been acknowledged by the server. */
    suspend fun pendingOutboxCount(): Int = pendingOutbox(1).size
    suspend fun markOutboxSynced(ids: List<String>)
    suspend fun markOutboxFailed(id: String, message: String, retryable: Boolean)
    suspend fun applyIncrementalChanges(changes: SyncChangesDto)
    suspend fun replaceServerOwnedData(bootstrap: BootstrapDto)
    suspend fun readCursor(): String?
    suspend fun writeCursor(cursor: String?)
}

interface SyncRetryEnqueuer {
    fun enqueueRetry()
}

interface SyncRemoteDataSource {
    suspend fun bootstrap(): BootstrapDto
    suspend fun changes(cursor: String?, limit: Int): SyncChangesDto
    suspend fun pushBatch(
        idempotencyKey: String,
        request: SyncBatchRequestDto,
    ): SyncBatchResponseDto
}

class RetrofitSyncRemoteDataSource(
    private val apiService: ApiService,
) : SyncRemoteDataSource {
    override suspend fun bootstrap(): BootstrapDto = apiService.bootstrap()

    override suspend fun changes(cursor: String?, limit: Int): SyncChangesDto =
        apiService.syncChanges(cursor, limit)

    override suspend fun pushBatch(
        idempotencyKey: String,
        request: SyncBatchRequestDto,
    ): SyncBatchResponseDto = apiService.syncBatch(idempotencyKey, request)
}

object IdempotencyKeys {
    /** Stable for a persisted client mutation; caller supplies its stable local mutation id. */
    fun forOperation(entityType: String, entityId: String, mutationId: String): String =
        sha256("operation|$entityType|$entityId|$mutationId")

    /** Stable across retries even when outbox row ordering differs. */
    fun forBatch(items: List<OutboxItem>): String = sha256(
        items.sortedBy(OutboxItem::idempotencyKey)
            .joinToString(separator = "|") { "${it.id}:${it.idempotencyKey}" },
    )

    private fun sha256(value: String): String = MessageDigest.getInstance("SHA-256")
        .digest(value.toByteArray(StandardCharsets.UTF_8))
        .joinToString(separator = "") { byte -> "%02x".format(byte) }
}

enum class SyncTrigger { MANUAL, BACKGROUND, RETRY, FULL_RESYNC }

sealed interface SyncResult {
    data class Success(
        val pushedCount: Int,
        val pulledCount: Int,
        val cursor: String?,
        val fullResync: Boolean,
    ) : SyncResult

    data class RetryableFailure(
        val message: String,
        val cause: Throwable? = null,
    ) : SyncResult

    data class PermanentFailure(
        val message: String,
        val httpCode: Int? = null,
        val cause: Throwable? = null,
    ) : SyncResult

    /** Cloud overwrite is intentionally blocked while local mutations are pending. */
    data class LocalChangesPending(val count: Int) : SyncResult

    data object AlreadyRunning : SyncResult
}
