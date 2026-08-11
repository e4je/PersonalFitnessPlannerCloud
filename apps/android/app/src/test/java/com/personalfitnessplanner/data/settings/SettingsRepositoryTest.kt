package com.personalfitnessplanner.data.settings

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import com.google.common.truth.Truth.assertThat
import java.io.File
import kotlinx.coroutines.test.runTest
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class SettingsRepositoryTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    @Test
    fun exerciseNotePersistsUnicodeAndBlankValueRemovesIt() = runTest {
        val repository = SettingsRepository(
            dataStore = PreferenceDataStoreFactory.create(
                scope = backgroundScope,
                produceFile = { File(temporaryFolder.root, "settings.preferences_pb") },
            ),
            defaults = AppSettings(),
        )

        repository.setExerciseNote("bench-press", "  4 号架\n安全杆 6 档  ")

        assertThat(repository.current().exerciseNotes)
            .containsExactly("bench-press", "4 号架\n安全杆 6 档")

        repository.setExerciseNote("bench-press", "   ")
        assertThat(repository.current().exerciseNotes).isEmpty()
    }
}
