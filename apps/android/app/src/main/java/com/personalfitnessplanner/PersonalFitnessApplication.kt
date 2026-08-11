package com.personalfitnessplanner

import android.app.Application
import com.personalfitnessplanner.sync.SyncCoordinator
import com.personalfitnessplanner.sync.SyncDependenciesProvider

class PersonalFitnessApplication : Application(), SyncDependenciesProvider {
    lateinit var container: AppContainer
        private set

    override val syncCoordinator: SyncCoordinator
        get() = container.syncCoordinator

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this).also(AppContainer::start)
    }
}
