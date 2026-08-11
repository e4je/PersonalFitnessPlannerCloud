package com.personalfitnessplanner.data.remote

import retrofit2.Call
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.PATCH
import retrofit2.http.POST
import retrofit2.http.Path
import retrofit2.http.Query

interface ApiService {
    @POST("/api/v1/auth/login")
    suspend fun login(@Body request: LoginRequestDto): AuthTokensDto

    @POST("/api/v1/auth/refresh")
    suspend fun refresh(@Body request: RefreshTokenRequestDto): AuthTokensDto

    @POST("/api/v1/auth/logout")
    suspend fun logout(@Body request: LogoutRequestDto = LogoutRequestDto()): ApiMessageDto

    @GET("/api/v1/me")
    suspend fun me(): UserDto

    @GET("/api/v1/bootstrap")
    suspend fun bootstrap(): BootstrapDto

    @GET("/api/v1/plans/current")
    suspend fun currentPlan(): PlanVersionDto

    @GET("/api/v1/plans/{plan_version_id}")
    suspend fun plan(@Path("plan_version_id") planVersionId: String): PlanVersionDto

    @GET("/api/v1/exercises")
    suspend fun exercises(
        @Query("cursor") cursor: String? = null,
        @Query("limit") limit: Int = DEFAULT_PAGE_SIZE,
    ): ExercisePageDto

    @GET("/api/v1/equipment")
    suspend fun equipment(
        @Query("cursor") cursor: String? = null,
        @Query("limit") limit: Int = DEFAULT_PAGE_SIZE,
    ): EquipmentPageDto

    @GET("/api/v1/workout-sessions")
    suspend fun workoutSessions(
        @Query("cursor") cursor: String? = null,
        @Query("limit") limit: Int = DEFAULT_PAGE_SIZE,
        @Query("local_date_from") localDateFrom: String? = null,
        @Query("local_date_to") localDateTo: String? = null,
    ): WorkoutSessionPageDto

    @POST("/api/v1/workout-sessions")
    suspend fun createWorkoutSession(
        @Header(IDEMPOTENCY_HEADER) idempotencyKey: String,
        @Body request: WorkoutSessionUpsertDto,
    ): WorkoutSessionDto

    @PATCH("/api/v1/workout-sessions/{id}")
    suspend fun patchWorkoutSession(
        @Path("id") id: String,
        @Header(IDEMPOTENCY_HEADER) idempotencyKey: String,
        @Body request: WorkoutSessionUpsertDto,
    ): WorkoutSessionDto

    @POST("/api/v1/readiness")
    suspend fun createReadiness(
        @Header(IDEMPOTENCY_HEADER) idempotencyKey: String,
        @Body request: ReadinessUpsertDto,
    ): ReadinessDto

    @GET("/api/v1/sync/changes")
    suspend fun syncChanges(
        @Query("cursor") cursor: String? = null,
        @Query("limit") limit: Int = DEFAULT_PAGE_SIZE,
    ): SyncChangesDto

    @POST("/api/v1/sync/batch")
    suspend fun syncBatch(
        @Header(IDEMPOTENCY_HEADER) idempotencyKey: String,
        @Body request: SyncBatchRequestDto,
    ): SyncBatchResponseDto

    companion object {
        const val IDEMPOTENCY_HEADER = "Idempotency-Key"
        const val DEFAULT_PAGE_SIZE = 200
    }
}

/** A synchronous refresh endpoint is required by OkHttp's blocking Authenticator contract. */
interface RefreshTokenApi {
    @POST("/api/v1/auth/refresh")
    fun refresh(@Body request: RefreshTokenRequestDto): Call<AuthTokensDto>
}

/** Used before committing a newly authenticated identity to the process-wide token store. */
internal interface BootstrapIdentityApi {
    @GET("/api/v1/bootstrap")
    suspend fun bootstrap(@Header("Authorization") authorization: String): BootstrapDto
}
