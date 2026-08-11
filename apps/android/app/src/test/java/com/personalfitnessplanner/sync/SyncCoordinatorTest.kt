package com.personalfitnessplanner.sync

import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.remote.BootstrapDto
import com.personalfitnessplanner.data.remote.SyncBatchItemResultDto
import com.personalfitnessplanner.data.remote.SyncBatchRequestDto
import com.personalfitnessplanner.data.remote.SyncBatchResponseDto
import com.personalfitnessplanner.data.remote.SyncChangesDto
import java.io.IOException
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset
import kotlinx.coroutines.test.runTest
import org.junit.Test

class SyncCoordinatorTest {
    @Test
    fun offlineFailure_keepsOutboxAndEnqueuesConnectedRetry() = runTest {
        val item = outboxItem()
        val local = FakeLocalStore(mutableListOf(item))
        val remote = FakeRemote().apply { failPushes = 1 }
        val retries = FakeRetryEnqueuer()
        val coordinator = coordinator(remote, local, retries)

        val result = coordinator.manualSync()

        assertThat(result).isInstanceOf(SyncResult.RetryableFailure::class.java)
        assertThat(local.items).containsExactly(item)
        assertThat(retries.calls).isEqualTo(1)
        assertThat(remote.requests).hasSize(1)
    }

    @Test
    fun retryUsesSameBatchAndOperationIdempotencyKeys_thenAcceptsDuplicate() = runTest {
        val item = outboxItem()
        val local = FakeLocalStore(mutableListOf(item))
        val remote = FakeRemote().apply { failPushes = 1 }
        val coordinator = coordinator(remote, local, FakeRetryEnqueuer())

        assertThat(coordinator.manualSync()).isInstanceOf(SyncResult.RetryableFailure::class.java)
        remote.batchStatus = "duplicate"
        val second = coordinator.manualSync()

        assertThat(second).isInstanceOf(SyncResult.Success::class.java)
        assertThat(local.items).isEmpty()
        assertThat(remote.headerKeys).hasSize(2)
        assertThat(remote.headerKeys[0]).isEqualTo(remote.headerKeys[1])
        assertThat(remote.requests[0].batchId).isEqualTo(remote.requests[1].batchId)
        assertThat(remote.requests[1].operations.single().idempotencyKey)
            .isEqualTo(item.idempotencyKey)
    }

    @Test
    fun fullResync_pushesOutboxThenAtomicallyReplacesServerDataAndCursor() = runTest {
        val local = FakeLocalStore(mutableListOf())
        val remote = FakeRemote().apply {
            bootstrapResponse = BootstrapDto(syncCursor = "bootstrap-cursor")
        }
        val result = coordinator(remote, local, FakeRetryEnqueuer()).fullResync()

        assertThat(result).isEqualTo(
            SyncResult.Success(0, 0, "bootstrap-cursor", fullResync = true),
        )
        assertThat(local.replacedBootstraps).containsExactly(remote.bootstrapResponse)
        assertThat(local.cursor).isEqualTo("bootstrap-cursor")
    }

    @Test
    fun incrementalSync_appliesAllPagesAndPersistsEachCursor() = runTest {
        val local = FakeLocalStore(mutableListOf()).apply { cursor = "c0" }
        val remote = FakeRemote().apply {
            changePages += SyncChangesDto(cursor = "c1", hasMore = true)
            changePages += SyncChangesDto(cursor = "c2", hasMore = false)
        }

        val result = coordinator(remote, local, FakeRetryEnqueuer()).backgroundSync()

        assertThat(result).isEqualTo(SyncResult.Success(0, 0, "c2", fullResync = false))
        assertThat(local.appliedChanges).hasSize(2)
        assertThat(local.writtenCursors).containsExactly("c1", "c2").inOrder()
    }

    @Test
    fun retentionGapBootstrapsWithoutApplyingPageOrAdvancingItsCursor() = runTest {
        val local = FakeLocalStore(mutableListOf()).apply { cursor = "c0" }
        val remote = FakeRemote().apply {
            changePages += SyncChangesDto(
                nextCursor = "expired-page-cursor",
                fullResyncRequired = true,
            )
            bootstrapResponse = BootstrapDto(syncCursor = "bootstrap-cursor")
        }

        val result = coordinator(remote, local, FakeRetryEnqueuer()).backgroundSync()

        assertThat(result).isEqualTo(
            SyncResult.Success(0, 0, "bootstrap-cursor", fullResync = true),
        )
        assertThat(local.appliedChanges).isEmpty()
        assertThat(local.replacedBootstraps).containsExactly(remote.bootstrapResponse)
        assertThat(local.writtenCursors).containsExactly("bootstrap-cursor")
        assertThat(remote.bootstrapCalls).isEqualTo(1)
    }

    @Test
    fun emptyBatchAcknowledgements_keepOutboxAndScheduleRetry() = runTest {
        val item = outboxItem()
        val local = FakeLocalStore(mutableListOf(item))
        val remote = FakeRemote().apply { omitResults = true }
        val retries = FakeRetryEnqueuer()

        val result = coordinator(remote, local, retries).manualSync()

        assertThat(result).isInstanceOf(SyncResult.RetryableFailure::class.java)
        assertThat(local.items).containsExactly(item)
        assertThat(local.failedIds).containsExactly(item.id)
        assertThat(retries.calls).isEqualTo(1)
    }

    private fun coordinator(
        remote: SyncRemoteDataSource,
        local: SyncLocalStore,
        retry: SyncRetryEnqueuer,
    ) = SyncCoordinator(
        remote = remote,
        localStore = local,
        retryEnqueuer = retry,
        clock = Clock.fixed(Instant.parse("2026-08-09T00:00:00Z"), ZoneOffset.UTC),
    )

    private fun outboxItem() = OutboxItem(
        id = "outbox-1",
        entityType = "workout_session",
        entityId = "session-1",
        operation = OutboxOperation.UPSERT,
        payload = mapOf("id" to "session-1", "status" to "COMPLETED"),
        idempotencyKey = IdempotencyKeys.forOperation(
            "workout_session",
            "session-1",
            "mutation-1",
        ),
    )

    private class FakeRetryEnqueuer : SyncRetryEnqueuer {
        var calls = 0
        override fun enqueueRetry() {
            calls++
        }
    }

    private class FakeRemote : SyncRemoteDataSource {
        var failPushes = 0
        var omitResults = false
        var batchStatus = "accepted"
        var bootstrapResponse = BootstrapDto()
        val requests = mutableListOf<SyncBatchRequestDto>()
        val headerKeys = mutableListOf<String>()
        val changePages = ArrayDeque<SyncChangesDto>()
        var bootstrapCalls = 0

        override suspend fun bootstrap(): BootstrapDto {
            bootstrapCalls++
            return bootstrapResponse
        }

        override suspend fun changes(cursor: String?, limit: Int): SyncChangesDto =
            if (changePages.isEmpty()) SyncChangesDto(cursor = cursor) else changePages.removeFirst()

        override suspend fun pushBatch(
            idempotencyKey: String,
            request: SyncBatchRequestDto,
        ): SyncBatchResponseDto {
            headerKeys += idempotencyKey
            requests += request
            if (failPushes-- > 0) throw IOException("offline")
            return SyncBatchResponseDto(
                batchId = request.batchId,
                results = if (omitResults) emptyList() else request.operations.map {
                    SyncBatchItemResultDto(id = it.id, status = batchStatus)
                },
            )
        }
    }

    private class FakeLocalStore(
        val items: MutableList<OutboxItem>,
    ) : SyncLocalStore {
        var cursor: String? = null
        val appliedChanges = mutableListOf<SyncChangesDto>()
        val replacedBootstraps = mutableListOf<BootstrapDto>()
        val writtenCursors = mutableListOf<String?>()
        val failedIds = mutableListOf<String>()

        override suspend fun pendingOutbox(limit: Int): List<OutboxItem> = items.take(limit)

        override suspend fun markOutboxSynced(ids: List<String>) {
            items.removeAll { it.id in ids }
        }

        override suspend fun markOutboxFailed(id: String, message: String, retryable: Boolean) {
            failedIds += id
        }

        override suspend fun applyIncrementalChanges(changes: SyncChangesDto) {
            appliedChanges += changes
        }

        override suspend fun replaceServerOwnedData(bootstrap: BootstrapDto) {
            replacedBootstraps += bootstrap
        }

        override suspend fun readCursor(): String? = cursor

        override suspend fun writeCursor(cursor: String?) {
            this.cursor = cursor
            writtenCursors += cursor
        }
    }
}
