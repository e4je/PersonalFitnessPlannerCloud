package com.personalfitnessplanner.ui

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import com.personalfitnessplanner.ui.model.AppDestination
import com.personalfitnessplanner.ui.model.FitnessAppUiState
import org.junit.Rule
import org.junit.Test

class KeyScreensUiTest {
    @get:Rule
    val composeRule = createComposeRule()

    @Test
    fun bottomNavigationOpensHistoryAndReadOnlyExerciseLibrary() {
        composeRule.setContent {
            FitnessApp(state = FitnessAppUiState.preview(AppDestination.Home))
        }

        composeRule.onNodeWithText("历史").performClick()
        composeRule.onNodeWithText("训练历史").assertIsDisplayed()
        composeRule.onNodeWithText("近 30 天完成 9 次 · 训练一致性 82%").assertIsDisplayed()

        composeRule.onNodeWithText("动作库").performClick()
        composeRule.onNodeWithText("动作定义由云端维护，本机可离线查看").assertIsDisplayed()
        composeRule.onNodeWithText("服务器动作定义为只读；个人器械备注会单独保存")
            .performScrollTo()
            .assertIsDisplayed()
    }
}
