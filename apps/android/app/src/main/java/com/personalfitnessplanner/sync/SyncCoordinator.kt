package com.personalfitnessplanner.sync

import com.personalfitnessplanner.data.remote.ApiService
import com.personalfitnessplanner.data.remote.BootstrapDto
import com.personalfitnessplanner.data.remote.SyncBatchRequestDto
import com.personalfitnessplanner.data.remote.SyncOperationDto
import java.io.IOException
import java.time.Clock
import java.time.Instant
import kotlinx.coroutines.sync.Mutex
import retrofit2.HttpException

class SyncCoordinator(
    private val remote: SyncRemoteDataSource,
    private val localStore: SyncLocalStore,
    private val retryEnqueuer: SyncRetryEnqueuer? = null,
    private val batchSize: Int = DEFAULT_BATCH_SIZE,
    private val pageSize: Int = DEFAULT_PAGE_SIZE,
    private val clock: Clock = Clock.systemUTC(),
) {
    constructor(
        apiService: ApiService,
        localStore: SyncLocalStore,
        retryEnqueuer: SyncRetryEnqueuer? = null,
        batchSize: Int = DEFAULT_BATCH_SIZE,
        pageSize: Int = DEFAULT_PAGE_SIZE,
        clock: Clock = Clock.systemUTC(),
    ) : this(
        remote = RetrofitSyncRemoteDataSource(apiService),
        localStore = localStore,
        retryEnqueuer = retryEnqueuer,
        batchSize = batchSize,
        pageSize = pageSize,
        clock = clock,
    )

    private val mutex = Mutex()

    init {
        require(batchSize in 1..MAX_BATCH_SIZE) { "batchSize must be in 1..$MAX_BATCH_SIZE" }
        require(pageSize > 0) { "pageSize must be positive" }
    }

    suspend fun manualSync(): SyncResult = sync(SyncTrigger.MANUAL)

    suspend fun backgroundSync(): SyncResult = sync(SyncTrigger.BACKGROUND)

    suspend fun fullResync(): SyncResult = sync(SyncTrigger.FULL_RESYNC, fullResync = true)

    suspend fun sync(
        trigger: SyncTrigger = SyncTrigger.MANUAL,
        fullResync: Boolean = trigger == SyncTrigger.FULL_RESYNC,
    ): SyncResult {
        if (!mutex.tryLock()) return SyncResult.AlreadyRunning
        return try {
            val pushed = pushOutbox()
            val pull = if (fullResync) pullBootstrap() else pullIncremental()
            SyncResult.Success(
                pushedCount = pushed,
                pulledCount = pull.count,
                cursor = pull.cursor,
                fullResync = pull.fullResync,
            )
        } catch (error: IOException) {
            retryable(error.message ?: "Network unavailable", error)
        } catch (error: HttpException) {
            if (error.code().isRetryableHttpCode()) {
                retryable("Server temporarily unavailable (${error.code()})", error)
            } else {
                SyncResult.PermanentFailure(
                    message = "Sync request failed (${error.code()})",
                    httpCode = error.code(),
                    cause = error,
                )
            }
        } catch (error: Exception) {
            SyncResult.PermanentFailure(
                message = error.message ?: "Unable to synchronize local data",
                cause = error,
            )
        } finally {
            mutex.unlock()
        }
    }

    private suspend fun pushOutbox(): Int {
        var pushed = 0
        val handled = mutableSetOf<String>()
        while (true) {
            val pending = localStore.pendingOutbox(batchSize)
                .filterNot { it.id in handled }
                .take(batchSize)
            if (pending.isEmpty()) break
            pending.forEach { handled += it.id }

            val idempotencyKey = IdempotencyKeys.forBatch(pending)
            val request = SyncBatchRequestDto(
                batchId = idempotencyKey,
                sentAt = Instant.now(clock).toString(),
                operations = pending.map { item ->
                    SyncOperationDto(
                        id = item.id,
                        idempotencyKey = item.idempotencyKey,
                        entityType = item.entityType,
                        entityId = item.entityId,
                        operation = item.operation.wireValue,
                        payload = item.payload,
                    )
                },
            )
            val response = remote.pushBatch(idempotencyKey, request)

            if (response.results.isEmpty()) {
                val message = "Batch response contained no per-operation acknowledgements"
                pending.forEach { item ->
                    localStore.markOutboxFailed(item.id, message, retryable = true)
                }
                throw RetryableSyncException(message)
            }

            val results = response.results.associateBy { it.id }
            val acknowledged = mutableListOf<String>()
            var retryableMessage: String? = null
            pending.forEach { item ->
                val itemResult = results[item.id]
                when (itemResult?.status?.lowercase()) {
                    "accepted", "applied", "success", "duplicate" -> {
                        acknowledged += item.id
                        pushed++
                    }

                    "rejected", "invalid", "conflict" -> localStore.markOutboxFailed(
                        id = item.id,
                        message = itemResult.error ?: "Server rejected the operation",
                        retryable = false,
                    )

                    "retry", "retryable", "temporary_error" -> {
                        val message = itemResult.error ?: "Server deferred the operation"
                        localStore.markOutboxFailed(item.id, message, retryable = true)
                        retryableMessage = retryableMessage ?: message
                    }

                    else -> {
                        val message = itemResult?.error ?: "Batch response omitted operation ${item.id}"
                        localStore.markOutboxFailed(item.id, message, retryable = true)
                        retryableMessage = retryableMessage ?: message
                    }
                }
            }
            if (acknowledged.isNotEmpty()) localStore.markOutboxSynced(acknowledged)
            retryableMessage?.let { throw RetryableSyncException(it) }
        }
        return pushed
    }

    private suspend fun pullBootstrap(): PullResult {
        val bootstrap = remote.bootstrap()
        localStore.replaceServerOwnedData(bootstrap)
        val cursor = bootstrap.syncCursor ?: bootstrap.cursor
        localStore.writeCursor(cursor)
        return PullResult(bootstrap.itemCount(), cursor, fullResync = true)
    }

    private suspend fun pullIncremental(): PullResult {
        var cursor = localStore.readCursor()
        var pulled = 0
        repeat(MAX_CHANGE_PAGES) {
            val previousCursor = cursor
            val page = remote.changes(cursor, pageSize)
            if (page.fullResyncRequired) {
                // A retention gap invalidates this incremental page and its cursor. Bootstrap
                // before applying or persisting anything from the gap response.
                return pullBootstrap()
            }
            localStore.applyIncrementalChanges(page)
            pulled += page.changes.size
            val next = page.nextCursor ?: page.cursor
            if (next != cursor) {
                localStore.writeCursor(next)
                cursor = next
            }
            if (!page.hasMore) return PullResult(pulled, cursor, fullResync = false)
            if (next == null || next == previousCursor) {
                throw IllegalStateException("Sync API reported more changes without advancing its cursor")
            }
        }
        throw RetryableSyncException("Incremental synchronization exceeded $MAX_CHANGE_PAGES pages")
    }

    private fun retryable(message: String, error: Throwable): SyncResult.RetryableFailure {
        retryEnqueuer?.enqueueRetry()
        return SyncResult.RetryableFailure(message, error)
    }

    private data class PullResult(
        val count: Int,
        val cursor: String?,
        val fullResync: Boolean,
    )

    private class RetryableSyncException(message: String) : IOException(message)

    companion object {
        const val DEFAULT_BATCH_SIZE = 50
        const val DEFAULT_PAGE_SIZE = 200
        const val MAX_BATCH_SIZE = 100
        const val MAX_CHANGE_PAGES = 1_000
    }
}

private fun Int.isRetryableHttpCode(): Boolean = this == 408 || this == 425 || this == 429 || this >= 500

private fun BootstrapDto.itemCount(): Int =
    exercises.size + equipment.size + planVersions.size + assignments.size + workoutSessions.size + readiness.size +
        cardioSessions.size +
        (if (user != null) 1 else 0) +
        (if (currentPlan != null && planVersions.none { it.id == currentPlan.id }) 1 else 0)
