package com.personalfitnessplanner.data.remote

import okhttp3.Interceptor
import okhttp3.Response
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.util.concurrent.atomic.AtomicReference

/** Rewrites only the origin/base path while retaining Retrofit's endpoint and query. */
class DynamicBaseUrlInterceptor private constructor(
    initialBaseUrl: String,
    private val allowInsecureForTests: Boolean,
) : Interceptor {
    constructor(initialBaseUrl: String) : this(initialBaseUrl, allowInsecureForTests = false)

    private val baseUrl = AtomicReference(parseBaseUrl(initialBaseUrl, allowInsecureForTests))

    fun updateBaseUrl(value: String) {
        baseUrl.set(parseBaseUrl(value, allowInsecureForTests))
    }

    fun currentBaseUrl(): String = baseUrl.get().toString()

    override fun intercept(chain: Interceptor.Chain): Response {
        val original = chain.request()
        val target = baseUrl.get()
        val combinedPath = buildString {
            append(target.encodedPath.trimEnd('/'))
            append('/')
            append(original.url.encodedPath.trimStart('/'))
        }.replace(Regex("/{2,}"), "/")

        val rewritten = original.url.newBuilder()
            .scheme(target.scheme)
            .host(target.host)
            .port(target.port)
            .encodedPath(combinedPath)
            .build()

        return chain.proceed(original.newBuilder().url(rewritten).build())
    }

    companion object {
        fun parseBaseUrl(value: String): HttpUrl = parseBaseUrl(
            value = value,
            allowInsecureForTests = false,
        )

        internal fun createForInsecureTest(initialBaseUrl: String): DynamicBaseUrlInterceptor =
            DynamicBaseUrlInterceptor(initialBaseUrl, allowInsecureForTests = true)

        internal fun parseBaseUrl(value: String, allowInsecureForTests: Boolean): HttpUrl {
            val normalized = if (value.endsWith('/')) value else "$value/"
            val parsed = normalized.toHttpUrlOrNull()
                ?: throw IllegalArgumentException("API base URL is invalid")
            require(parsed.scheme == "https" || (allowInsecureForTests && parsed.scheme == "http")) {
                "API base URL must use HTTPS"
            }
            require(parsed.username.isEmpty() && parsed.password.isEmpty()) {
                "API base URL must not contain credentials"
            }
            require(parsed.query == null && parsed.fragment == null) {
                "API base URL must not contain a query or fragment"
            }
            return parsed
        }
    }
}
