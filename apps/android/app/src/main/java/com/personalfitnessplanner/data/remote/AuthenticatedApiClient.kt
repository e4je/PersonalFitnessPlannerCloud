package com.personalfitnessplanner.data.remote

import com.personalfitnessplanner.data.security.AuthTokens
import com.personalfitnessplanner.data.security.TokenStore
import com.squareup.moshi.Moshi
import com.squareup.moshi.kotlin.reflect.KotlinJsonAdapterFactory
import okhttp3.Authenticator
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.Route
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.moshi.MoshiConverterFactory

class BearerTokenInterceptor(
    private val tokenStore: TokenStore,
) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        if (UNAUTHENTICATED_PATHS.any { request.url.encodedPath.endsWith(it) }) {
            return chain.proceed(request)
        }
        val tokens = tokenStore.read() ?: return chain.proceed(request)
        return chain.proceed(request.withBearer(tokens))
    }

    private fun Request.withBearer(tokens: AuthTokens): Request = newBuilder()
        .header(AUTHORIZATION, "${tokens.tokenType} ${tokens.accessToken}")
        .build()

    private companion object {
        val UNAUTHENTICATED_PATHS = setOf("/api/v1/auth/login", "/api/v1/auth/refresh")
    }
}

/** Refreshes once for an authenticated 401 and never recursively retries a second 401. */
class TokenRefreshAuthenticator(
    private val tokenStore: TokenStore,
    private val refreshApi: RefreshTokenApi,
    private val nowEpochSeconds: () -> Long = { System.currentTimeMillis() / 1_000L },
) : Authenticator {
    private val refreshLock = Any()

    override fun authenticate(route: Route?, response: Response): Request? {
        if (responseCount(response) >= MAX_RESPONSE_COUNT) {
            tokenStore.clear()
            return null
        }
        val failedToken = response.request.header(AUTHORIZATION)
            ?.substringAfter(' ', missingDelimiterValue = "")
            ?.takeIf(String::isNotBlank)
            ?: return null

        synchronized(refreshLock) {
            val latest = tokenStore.read() ?: return null
            if (latest.accessToken != failedToken) {
                return response.request.withBearer(latest)
            }

            val refreshResponse = try {
                refreshApi.refresh(RefreshTokenRequestDto(latest.refreshToken)).execute()
            } catch (_: Exception) {
                tokenStore.clear()
                return null
            }

            try {
                val result = refreshResponse
                if (!result.isSuccessful) {
                    tokenStore.clear()
                    return null
                }
                val body = result.body() ?: run {
                    tokenStore.clear()
                    return null
                }
                if (body.accessToken.isBlank()) {
                    tokenStore.clear()
                    return null
                }
                val refreshed = AuthTokens(
                    accessToken = body.accessToken,
                    refreshToken = body.refreshToken?.takeIf(String::isNotBlank) ?: latest.refreshToken,
                    expiresAtEpochSeconds = body.expiresAtEpochSeconds
                        ?: body.expiresInSeconds?.let { nowEpochSeconds() + it },
                    tokenType = body.tokenType.ifBlank { "Bearer" },
                )
                try {
                    tokenStore.write(refreshed)
                } catch (_: Exception) {
                    tokenStore.clear()
                    return null
                }
                return response.request.withBearer(refreshed)
            } finally {
                refreshResponse.errorBody()?.close()
            }
        }
    }

    private fun responseCount(response: Response): Int {
        var count = 1
        var prior = response.priorResponse
        while (prior != null) {
            count++
            prior = prior.priorResponse
        }
        return count
    }

    private fun Request.withBearer(tokens: AuthTokens): Request = newBuilder()
        .header(AUTHORIZATION, "${tokens.tokenType} ${tokens.accessToken}")
        .build()

    private companion object {
        const val MAX_RESPONSE_COUNT = 2
    }
}

/** Owns one Retrofit graph; changing the base URL affects subsequent calls without rebuilding it. */
class ApiClientFactory private constructor(
    initialBaseUrl: String,
    private val tokenStore: TokenStore,
    isDebug: Boolean,
    baseClient: OkHttpClient,
    moshi: Moshi,
    private val allowInsecureForTests: Boolean,
) {
    constructor(
        initialBaseUrl: String,
        tokenStore: TokenStore,
        isDebug: Boolean,
        baseClient: OkHttpClient = OkHttpClient(),
        moshi: Moshi = defaultMoshi(),
    ) : this(
        initialBaseUrl = initialBaseUrl,
        tokenStore = tokenStore,
        isDebug = isDebug,
        baseClient = baseClient,
        moshi = moshi,
        allowInsecureForTests = false,
    )

    private val dynamicBaseUrl = if (allowInsecureForTests) {
        DynamicBaseUrlInterceptor.createForInsecureTest(initialBaseUrl)
    } else {
        DynamicBaseUrlInterceptor(initialBaseUrl)
    }
    private val converterFactory = MoshiConverterFactory.create(moshi)

    private val refreshClient: OkHttpClient = baseClient.newBuilder()
        .addInterceptor(dynamicBaseUrl)
        .apply {
            if (isDebug) {
                addInterceptor(HttpLoggingInterceptor().apply {
                    level = HttpLoggingInterceptor.Level.BASIC
                    redactHeader(AUTHORIZATION)
                })
            }
        }
        .build()

    private val refreshApi: RefreshTokenApi = retrofit(refreshClient, initialBaseUrl)
        .create(RefreshTokenApi::class.java)
    private val bootstrapIdentityApi: BootstrapIdentityApi = retrofit(refreshClient, initialBaseUrl)
        .create(BootstrapIdentityApi::class.java)

    val httpClient: OkHttpClient = refreshClient.newBuilder()
        .addInterceptor(BearerTokenInterceptor(tokenStore))
        .authenticator(TokenRefreshAuthenticator(tokenStore, refreshApi))
        .build()

    val apiService: ApiService = retrofit(httpClient, initialBaseUrl)
        .create(ApiService::class.java)

    /**
     * Authenticates a bootstrap with an explicit, not-yet-persisted token. This lets Room verify
     * and atomically switch account scope before background workers can observe the new identity.
     */
    suspend fun preflightBootstrap(tokens: AuthTokens): BootstrapDto =
        bootstrapIdentityApi.bootstrap("${tokens.tokenType.ifBlank { "Bearer" }} ${tokens.accessToken}")

    /** Returns true when the scheme/host/port changed. Tokens never cross that boundary. */
    fun updateBaseUrl(
        value: String,
        clearAuthenticationOnOriginChange: Boolean = true,
    ): Boolean {
        val current = DynamicBaseUrlInterceptor.parseBaseUrl(
            dynamicBaseUrl.currentBaseUrl(),
            allowInsecureForTests,
        )
        val next = DynamicBaseUrlInterceptor.parseBaseUrl(value, allowInsecureForTests)
        val originChanged = current.scheme != next.scheme ||
            current.host != next.host ||
            current.port != next.port
        if (originChanged && clearAuthenticationOnOriginChange) tokenStore.clear()
        dynamicBaseUrl.updateBaseUrl(value)
        return originChanged
    }

    fun currentBaseUrl(): String = dynamicBaseUrl.currentBaseUrl()

    private fun retrofit(client: OkHttpClient, baseUrl: String): Retrofit = Retrofit.Builder()
        .baseUrl(DynamicBaseUrlInterceptor.parseBaseUrl(baseUrl, allowInsecureForTests))
        .client(client)
        .addConverterFactory(converterFactory)
        .build()

    companion object {
        /** HTTP is available only to same-module MockWebServer tests, never to an app build type. */
        internal fun createForInsecureTest(
            initialBaseUrl: String,
            tokenStore: TokenStore,
            isDebug: Boolean,
            baseClient: OkHttpClient = OkHttpClient(),
            moshi: Moshi = defaultMoshi(),
        ): ApiClientFactory = ApiClientFactory(
            initialBaseUrl = initialBaseUrl,
            tokenStore = tokenStore,
            isDebug = isDebug,
            baseClient = baseClient,
            moshi = moshi,
            allowInsecureForTests = true,
        )

        private fun defaultMoshi(): Moshi = Moshi.Builder()
            .addLast(KotlinJsonAdapterFactory())
            .build()
    }
}

private const val AUTHORIZATION = "Authorization"
