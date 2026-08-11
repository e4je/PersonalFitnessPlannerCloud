package com.personalfitnessplanner.ui

import androidx.compose.ui.test.assertHasClickAction
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performScrollToNode
import com.personalfitnessplanner.ui.model.AppDestination
import com.personalfitnessplanner.ui.model.FitnessAppUiState
import com.personalfitnessplanner.ui.model.WorkoutExecutionUiState
import com.personalfitnessplanner.ui.screens.WorkoutExecutionScreen
import com.personalfitnessplanner.ui.theme.FitnessPlannerTheme
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class FitnessAppUiTest {
    @get:Rule
    val composeRule = createComposeRule()

    @Test
    fun onboarding_showsRequiredConnectionAndOfflineActions() {
        composeRule.setContent {
            FitnessApp(state = FitnessAppUiState.preview(AppDestination.Onboarding))
        }

        composeRule.onNodeWithText("后端 API 地址").assertIsDisplayed()
        composeRule.onNodeWithText("账号").assertIsDisplayed()

        composeRule.onNodeWithTag("onboarding_list")
            .performScrollToNode(hasText("登录并继续"))
        composeRule.onNodeWithText("登录并继续").assertHasClickAction()
        composeRule.onNodeWithText("下载云端计划").assertHasClickAction()

        composeRule.onNodeWithTag("onboarding_list")
            .performScrollToNode(hasText(" 后端不可达？使用内置计划进入本地模式"))
        composeRule.onNodeWithText(" 后端不可达？使用内置计划进入本地模式").assertHasClickAction()

        composeRule.onNodeWithTag("onboarding_list")
            .performScrollToNode(hasText("账号"))
        composeRule.onNodeWithContentDescription("显示密码").assertHasClickAction()
    }

    @Test
    fun home_startWorkout_opensTodayPlan() {
        composeRule.setContent {
            FitnessApp(state = FitnessAppUiState.preview(AppDestination.Home))
        }

        composeRule.onNodeWithText("A · 胸部优先").assertIsDisplayed()
        composeRule.onNodeWithText("开始训练").performClick()
        composeRule.onNodeWithText("动作顺序").assertIsDisplayed()
        composeRule.onNodeWithText("杠铃平板卧推").assertIsDisplayed()
    }

    @Test
    fun workoutExecution_completeSet_emitsSetId() {
        var completedSetId: String? = null
        composeRule.setContent {
            FitnessPlannerTheme {
                WorkoutExecutionScreen(
                    state = WorkoutExecutionUiState(),
                    callbacks = WorkoutExecutionCallbacks(
                        onSetComplete = { completedSetId = it },
                    ),
                )
            }
        }

        composeRule.onNodeWithText("完成第 2 组")
            .performScrollTo()
            .performClick()
        composeRule.runOnIdle { assertEquals("set-2", completedSetId) }
    }

    @Test
    fun darkTheme_settingsKeepsCoreActionsVisible() {
        composeRule.setContent {
            FitnessPlannerTheme(darkTheme = true) {
                FitnessAppContent(
                    state = FitnessAppUiState.preview(AppDestination.Settings),
                    callbacks = FitnessAppCallbacks(),
                )
            }
        }

        composeRule.onNodeWithText("账号、同步与训练偏好").assertIsDisplayed()
        composeRule.onNodeWithTag("settings_list")
            .performScrollToNode(hasText("  立即同步"))
        composeRule.onNodeWithText("  立即同步").assertHasClickAction()
        composeRule.onNodeWithTag("settings_list")
            .performScrollToNode(hasText("  退出登录"))
        composeRule.onNodeWithText("  退出登录").assertHasClickAction()
    }
}
