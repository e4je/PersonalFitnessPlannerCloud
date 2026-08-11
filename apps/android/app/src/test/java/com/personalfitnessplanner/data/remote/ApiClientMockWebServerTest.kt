package com.personalfitnessplanner.data.remote

import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.security.AuthTokens
import com.personalfitnessplanner.data.security.TokenStore
import com.personalfitnessplanner.data.settings.normalizedBaseUrl
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.test.runTest
import okhttp3.Request
import okhttp3.logging.HttpLoggingInterceptor
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Before
import org.junit.Test

class ApiClientMockWebServerTest {
    private lateinit var server: MockWebServer
    private lateinit var tokenStore: FakeTokenStore

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
        tokenStore = FakeTokenStore()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    @Test
    fun loginAndExplicitRefresh_useTheMinimumAuthContract() = runTest {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"access_token":"access-1","refresh_token":"refresh-1","token_type":"Bearer","expires_in":3600}""",
            ),
        )
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"access_token":"access-2","refresh_token":"refresh-2","token_type":"Bearer"}""",
            ),
        )
        val api = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        ).apiService

        val login = api.login(LoginRequestDto("athlete@example.com", "secret", "Pixel"))
        val refresh = api.refresh(RefreshTokenRequestDto("refresh-1"))

        assertThat(login.accessToken).isEqualTo("access-1")
        assertThat(refresh.refreshToken).isEqualTo("refresh-2")
        server.takeRequest().also { request ->
            assertThat(request.path).isEqualTo("/api/v1/auth/login")
            assertThat(request.body.readUtf8()).contains("\"email\":\"athlete@example.com\"")
            assertThat(request.getHeader("Authorization")).isNull()
        }
        server.takeRequest().also { request ->
            assertThat(request.path).isEqualTo("/api/v1/auth/refresh")
            assertThat(request.body.readUtf8()).contains("\"refresh_token\":\"refresh-1\"")
            assertThat(request.getHeader("Authorization")).isNull()
        }
    }

    @Test
    fun authenticated401_refreshesTokenAndRetriesExactlyOnce() {
        tokenStore.write(AuthTokens("expired-access", "refresh-1"))
        val protectedCalls = AtomicInteger()
        val refreshCalls = AtomicInteger()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: okhttp3.mockwebserver.RecordedRequest): MockResponse =
                when (request.requestUrl?.encodedPath) {
                    "/api/v1/auth/refresh" -> {
                        refreshCalls.incrementAndGet()
                        MockResponse().setResponseCode(200).setBody(
                            """{"access_token":"fresh-access","refresh_token":"refresh-2","token_type":"Bearer"}""",
                        )
                    }

                    "/protected" -> {
                        protectedCalls.incrementAndGet()
                        if (request.getHeader("Authorization") == "Bearer fresh-access") {
                            MockResponse().setResponseCode(200).setBody("ok")
                        } else {
                            MockResponse().setResponseCode(401)
                        }
                    }

                    else -> MockResponse().setResponseCode(404)
                }
        }
        val client = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        )
            .httpClient

        client.newCall(Request.Builder().url(server.url("/protected")).build()).execute().use { response ->
            assertThat(response.code).isEqualTo(200)
        }

        assertThat(protectedCalls.get()).isEqualTo(2)
        assertThat(refreshCalls.get()).isEqualTo(1)
        assertThat(tokenStore.read()?.accessToken).isEqualTo("fresh-access")
        assertThat(tokenStore.read()?.refreshToken).isEqualTo("refresh-2")
    }

    @Test
    fun second401_isReturnedWithoutRefreshLoop() {
        tokenStore.write(AuthTokens("expired-access", "refresh-1"))
        val protectedCalls = AtomicInteger()
        val refreshCalls = AtomicInteger()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: okhttp3.mockwebserver.RecordedRequest): MockResponse =
                when (request.requestUrl?.encodedPath) {
                    "/api/v1/auth/refresh" -> {
                        refreshCalls.incrementAndGet()
                        MockResponse().setResponseCode(200).setBody(
                            """{"access_token":"still-rejected","refresh_token":"refresh-1"}""",
                        )
                    }

                    "/protected" -> {
                        protectedCalls.incrementAndGet()
                        MockResponse().setResponseCode(401)
                    }

                    else -> MockResponse().setResponseCode(404)
                }
        }
        val client = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        )
            .httpClient

        client.newCall(Request.Builder().url(server.url("/protected")).build()).execute().use { response ->
            assertThat(response.code).isEqualTo(401)
        }

        assertThat(protectedCalls.get()).isEqualTo(2)
        assertThat(refreshCalls.get()).isEqualTo(1)
        assertThat(tokenStore.read()).isNull()
    }

    @Test
    fun failedRefresh_clearsTokens() {
        tokenStore.write(AuthTokens("expired-access", "refresh-1"))
        server.enqueue(MockResponse().setResponseCode(401))
        server.enqueue(MockResponse().setResponseCode(503))
        val client = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        ).httpClient

        client.newCall(Request.Builder().url(server.url("/protected")).build()).execute().close()

        assertThat(tokenStore.read()).isNull()
    }

    @Test
    fun dynamicBaseUrl_movesSubsequentCallsAndReleaseHasNoNetworkLogger() = runTest {
        val secondServer = MockWebServer().also { it.start() }
        try {
            server.enqueue(MockResponse().setBody(authBody("first")))
            secondServer.enqueue(MockResponse().setBody(authBody("second")))
            val factory = ApiClientFactory.createForInsecureTest(
                server.url("/").toString(),
                tokenStore,
                isDebug = false,
            )

            assertThat(factory.apiService.login(LoginRequestDto("a@b.test", "pw")).accessToken)
                .isEqualTo("first")
            factory.updateBaseUrl(secondServer.url("tenant/").toString())
            assertThat(factory.apiService.login(LoginRequestDto("a@b.test", "pw")).accessToken)
                .isEqualTo("second")

            assertThat(server.takeRequest().path).isEqualTo("/api/v1/auth/login")
            assertThat(secondServer.takeRequest().path).isEqualTo("/tenant/api/v1/auth/login")
            assertThat(factory.httpClient.interceptors.filterIsInstance<HttpLoggingInterceptor>()).isEmpty()
        } finally {
            secondServer.shutdown()
        }
    }

    @Test
    fun changingOriginClearsTokens_butChangingOnlyBasePathDoesNot() {
        tokenStore.write(AuthTokens("access", "refresh"))
        val factory = ApiClientFactory.createForInsecureTest(
            server.url("one/").toString(),
            tokenStore,
            isDebug = false,
        )

        assertThat(factory.updateBaseUrl(server.url("two/").toString())).isFalse()
        assertThat(tokenStore.read()).isNotNull()

        val secondServer = MockWebServer().also { it.start() }
        try {
            assertThat(factory.updateBaseUrl(secondServer.url("/").toString())).isTrue()
            assertThat(tokenStore.read()).isNull()
        } finally {
            secondServer.shutdown()
        }
    }

    @Test
    fun preflightBootstrapUsesTransientNewIdentityWithoutOverwritingStoredTokens() = runTest {
        tokenStore.write(AuthTokens("old-access", "old-refresh"))
        server.enqueue(MockResponse().setBody("{}"))
        val factory = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        )

        factory.preflightBootstrap(AuthTokens("new-access", "new-refresh"))

        assertThat(server.takeRequest().getHeader("Authorization")).isEqualTo("Bearer new-access")
        assertThat(tokenStore.read()?.accessToken).isEqualTo("old-access")
        assertThat(tokenStore.read()?.refreshToken).isEqualTo("old-refresh")
    }

    @Test
    fun logoutSendsCurrentRefreshToken() = runTest {
        server.enqueue(MockResponse().setBody("{\"message\":\"ok\"}"))
        tokenStore.write(AuthTokens("access", "refresh-current"))
        val api = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        ).apiService

        api.logout(LogoutRequestDto(tokenStore.read()!!.refreshToken))

        assertThat(server.takeRequest().body.readUtf8())
            .contains("\"refresh_token\":\"refresh-current\"")
    }

    @Test
    fun syncChangesDecodesFullResyncRequiredRetentionSignal() = runTest {
        server.enqueue(
            MockResponse().setBody(
                """{"changes":[],"next_cursor":"expired","full_resync_required":true}""",
            ),
        )
        val api = ApiClientFactory.createForInsecureTest(
            server.url("/").toString(),
            tokenStore,
            isDebug = false,
        ).apiService

        val page = api.syncChanges(cursor = "old")

        assertThat(page.fullResyncRequired).isTrue()
        assertThat(page.nextCursor).isEqualTo("expired")
    }

    @Test
    fun productionAndSettingsBaseUrls_rejectHttpIncludingLocalhost() {
        listOf(
            "http://localhost/",
            "http://127.0.0.1:8080/",
            "http://fitness.example.com/",
        ).forEach { value ->
            val failure = runCatching {
                DynamicBaseUrlInterceptor.parseBaseUrl(value)
            }.exceptionOrNull()

            assertThat(failure).isInstanceOf(IllegalArgumentException::class.java)
            assertThat(failure).hasMessageThat().contains("HTTPS")
        }

        assertThat(DynamicBaseUrlInterceptor.parseBaseUrl("https://localhost/").scheme)
            .isEqualTo("https")

        val settingsFailure = runCatching {
            normalizedBaseUrl("http://localhost/")
        }.exceptionOrNull()
        assertThat(settingsFailure).isInstanceOf(IllegalArgumentException::class.java)
    }

    private fun authBody(access: String): String =
        """{"access_token":"$access","refresh_token":"refresh"}"""

    private class FakeTokenStore : TokenStore {
        private val mutableTokens = MutableStateFlow<AuthTokens?>(null)
        override val tokens: StateFlow<AuthTokens?> = mutableTokens
        override fun read(): AuthTokens? = mutableTokens.value
        override fun write(tokens: AuthTokens) {
            mutableTokens.value = tokens
        }

        override fun clear() {
            mutableTokens.value = null
        }
    }
}
