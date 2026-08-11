package com.personalfitnessplanner.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Bedtime
import androidx.compose.material.icons.rounded.CalendarToday
import androidx.compose.material.icons.rounded.DirectionsRun
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material.icons.rounded.TrendingUp
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.personalfitnessplanner.ui.HomeCallbacks
import com.personalfitnessplanner.ui.components.LabelPill
import com.personalfitnessplanner.ui.components.MetricCard
import com.personalfitnessplanner.ui.components.PlanProgress
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.components.SectionTitle
import com.personalfitnessplanner.ui.components.SyncStatusPill
import com.personalfitnessplanner.ui.model.HomeUiState

@Composable
fun HomeScreen(
    state: HomeUiState,
    callbacks: HomeCallbacks,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 28.dp),
    ) {
        item {
            ScreenHeader(
                title = state.greeting,
                subtitle = state.dateText,
                trailing = { SyncStatusPill(state.syncStatus, state.syncMessage) },
            )
        }
        item {
            RecommendationCard(
                state = state,
                onStartWorkout = callbacks.onStartWorkout,
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
        item {
            Column(
                Modifier.padding(horizontal = 16.dp, vertical = 20.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                SectionTitle("本周概览")
                PlanProgress(state.completedThisWeek, state.weeklyLimit)
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    MetricCard(
                        label = "距离上次",
                        value = "${state.daysSinceLastWorkout} 天",
                        icon = Icons.Rounded.Schedule,
                        supportingText = "已满足恢复间隔",
                        modifier = Modifier.weight(1f),
                    )
                    MetricCard(
                        label = "下一次",
                        value = state.nextWorkout,
                        icon = Icons.Rounded.TrendingUp,
                        supportingText = "按完成记录交替",
                        modifier = Modifier.weight(1f),
                    )
                }
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    MetricCard(
                        label = "疲劳评分",
                        value = "${state.fatigueScore} / 10",
                        icon = Icons.Rounded.Bedtime,
                        supportingText = if (state.fatigueScore >= 8) "建议恢复" else "状态正常",
                        modifier = Modifier.weight(1f),
                    )
                    MetricCard(
                        label = "本周完成",
                        value = "${state.completedThisWeek} 次",
                        icon = Icons.Rounded.CalendarToday,
                        supportingText = "上限 ${state.weeklyLimit} 次",
                        modifier = Modifier.weight(1f),
                    )
                }
            }
        }
        item {
            Column(
                Modifier.padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                SectionTitle("调整今天", supportingText = "手动选择不会修改你的长期计划")
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(
                        onClick = callbacks.onMarkRest,
                        modifier = Modifier
                            .weight(1f)
                            .height(52.dp),
                    ) {
                        Icon(Icons.Rounded.Bedtime, contentDescription = null)
                        Text("  今天休息")
                    }
                    OutlinedButton(
                        onClick = callbacks.onSwitchToCardio,
                        modifier = Modifier
                            .weight(1f)
                            .height(52.dp),
                    ) {
                        Icon(Icons.Rounded.DirectionsRun, contentDescription = null)
                        Text("  改为有氧")
                    }
                }
                TextButton(
                    onClick = callbacks.onSync,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Icon(Icons.Rounded.Sync, contentDescription = null)
                    Text("  立即同步")
                }
            }
        }
    }
}

@Composable
private fun RecommendationCard(
    state: HomeUiState,
    onStartWorkout: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.primaryContainer,
            contentColor = MaterialTheme.colorScheme.onPrimaryContainer,
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(
                    Modifier
                        .size(48.dp)
                        .background(MaterialTheme.colorScheme.primary, CircleShape)
                        .semantics { contentDescription = "今日训练建议" },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        Icons.Rounded.FitnessCenter,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onPrimary,
                    )
                }
                LabelPill("今日建议", emphasized = true)
            }
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(state.recommendation, style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.Bold)
                Text(state.recommendationReason, style = MaterialTheme.typography.bodyLarge)
            }
            Column {
                Text(state.planName, style = MaterialTheme.typography.titleMedium)
                Text(
                    state.planVersion,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.78f),
                )
            }
            Button(
                onClick = onStartWorkout,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(54.dp),
            ) {
                Text(if (state.hasActiveWorkout) "继续训练" else "开始训练")
            }
        }
    }
}
