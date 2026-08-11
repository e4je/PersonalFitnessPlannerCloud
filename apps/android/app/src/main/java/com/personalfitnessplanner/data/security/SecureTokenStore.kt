package com.personalfitnessplanner.data.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class AuthTokens(
    val accessToken: String,
    val refreshToken: String,
    val expiresAtEpochSeconds: Long? = null,
    val tokenType: String = "Bearer",
)

interface TokenStore {
    val tokens: StateFlow<AuthTokens?>
    fun read(): AuthTokens?
    fun write(tokens: AuthTokens)
    fun clear()
}

/**
 * Persists only AES/GCM ciphertext. The non-exportable AES key lives in Android Keystore.
 * Decryption failure (for example after restoring data without its hardware key) fails closed.
 */
class SecureTokenStore(
    context: Context,
    private val keyAlias: String = DEFAULT_KEY_ALIAS,
    preferencesName: String = DEFAULT_PREFERENCES_NAME,
) : TokenStore {
    private val preferences = context.applicationContext.getSharedPreferences(
        preferencesName,
        Context.MODE_PRIVATE,
    )
    private val lock = Any()
    private val mutableTokens = MutableStateFlow(readPersisted())
    override val tokens: StateFlow<AuthTokens?> = mutableTokens

    override fun read(): AuthTokens? = mutableTokens.value

    private fun readPersisted(): AuthTokens? = synchronized(lock) {
        val encoded = preferences.getString(KEY_ENCRYPTED_TOKENS, null) ?: return@synchronized null
        try {
            val pieces = encoded.split('.', limit = 2)
            require(pieces.size == 2)
            val iv = Base64.decode(pieces[0], Base64.NO_WRAP)
            val ciphertext = Base64.decode(pieces[1], Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(GCM_TAG_BITS, iv))
            val json = JSONObject(String(cipher.doFinal(ciphertext), StandardCharsets.UTF_8))
            AuthTokens(
                accessToken = json.getString(JSON_ACCESS_TOKEN),
                refreshToken = json.getString(JSON_REFRESH_TOKEN),
                expiresAtEpochSeconds = if (json.isNull(JSON_EXPIRES_AT)) {
                    null
                } else {
                    json.getLong(JSON_EXPIRES_AT)
                },
                tokenType = json.optString(JSON_TOKEN_TYPE, "Bearer"),
            )
        } catch (_: Exception) {
            preferences.edit().remove(KEY_ENCRYPTED_TOKENS).commit()
            null
        }
    }

    override fun write(tokens: AuthTokens) = synchronized(lock) {
        require(tokens.accessToken.isNotBlank()) { "Access token must not be blank" }
        require(tokens.refreshToken.isNotBlank()) { "Refresh token must not be blank" }
        val plaintext = JSONObject()
            .put(JSON_ACCESS_TOKEN, tokens.accessToken)
            .put(JSON_REFRESH_TOKEN, tokens.refreshToken)
            .put(JSON_EXPIRES_AT, tokens.expiresAtEpochSeconds ?: JSONObject.NULL)
            .put(JSON_TOKEN_TYPE, tokens.tokenType)
            .toString()
            .toByteArray(StandardCharsets.UTF_8)

        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val encoded = listOf(cipher.iv, cipher.doFinal(plaintext))
            .joinToString(".") { Base64.encodeToString(it, Base64.NO_WRAP) }
        check(preferences.edit().putString(KEY_ENCRYPTED_TOKENS, encoded).commit()) {
            "Unable to persist encrypted authentication tokens"
        }
        mutableTokens.value = tokens
    }

    override fun clear() = synchronized(lock) {
        preferences.edit().remove(KEY_ENCRYPTED_TOKENS).commit()
        mutableTokens.value = null
        Unit
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getKey(keyAlias, null) as? SecretKey)?.let { return it }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                keyAlias,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(AES_KEY_BITS)
                .setRandomizedEncryptionRequired(true)
                .build(),
        )
        return generator.generateKey()
    }

    companion object {
        const val DEFAULT_KEY_ALIAS = "personal_fitness_auth_tokens_v1"
        const val DEFAULT_PREFERENCES_NAME = "encrypted_auth_tokens"

        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val AES_KEY_BITS = 256
        private const val GCM_TAG_BITS = 128
        private const val KEY_ENCRYPTED_TOKENS = "ciphertext_v1"
        private const val JSON_ACCESS_TOKEN = "access_token"
        private const val JSON_REFRESH_TOKEN = "refresh_token"
        private const val JSON_EXPIRES_AT = "expires_at"
        private const val JSON_TOKEN_TYPE = "token_type"
    }
}
