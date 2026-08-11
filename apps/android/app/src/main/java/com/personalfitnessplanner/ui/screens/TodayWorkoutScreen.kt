package com.personalfitnessplanner.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.AccessTime
import androidx.compose.material.icons.rounded.ChangeCircle
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.Info
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Divider
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
import com.personalfitnessplanner.ui.TodayCallbacks
import com.personalfitnessplanner.ui.components.IconLabel
import com.personalfitnessplanner.ui.components.KeyValueRow
import com.personalfitnessplanner.ui.components.LabelPill
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.components.SectionTitle
import com.personalfitnessplanner.ui.model.ExerciseSlotUi
import com.personalfitnessplanner.ui.model.TodayWorkoutUiState

@Composable
fun TodayWorkoutScreen(
    state: TodayWorkoutUiState,
    callbacks: TodayCallbacks,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 32.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item {
            ScreenHeader(title = "今日训练", subtitle = state.dateText)
        }
        item {
            PlanSummaryCard(
                state = state,
                onStartWorkout = callbacks.onStartWorkout,
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
        item {
            SectionTitle(
                title = "动作顺序",
                supportingText = "每个位置选择首选动作或一个替代动作",
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
            )
        }
        items(state.exercises.size, key = { state.exercises[it].id }) { index ->
            ExerciseSlotCard(
                slot = state.exercises[index],
                onStart = { callbacks.onExerciseStart(state.exercises[index].id) },
                onSkip = { callbacks.onExerciseSkip(state.exercises[index].id) },
                onSwap = { callbacks.onExerciseSwap(state.exercises[index].id) },
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
    }
}

@Composable
private fun PlanSummaryCard(
    state: TodayWorkoutUiState,
    onStartWorkout: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text(state.workoutLabel, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
                    Text("${state.planName} · ${state.planVersion}", style = MaterialTheme.typography.bodyMedium)
                }
                LabelPill("${state.exercises.size} 个动作", emphasized = true)
            }
            Text(state.weekNote, style = MaterialTheme.typography.bodyLarge)
            IconLabel(Icons.Rounded.AccessTime, "预计 ${state.estimatedMinutes} 分钟")
            Button(
                onClick = onStartWorkout,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(54.dp),
            ) {
                Icon(Icons.Rounded.PlayArrow, contentDescription = null)
                Text("  开始整套训练")
            }
        }
    }
}

@Composable
fun ExerciseSlotCard(
    slot: ExerciseSlotUi,
    onStart: () -> Unit,
    onSkip: () -> Unit,
    onSwap: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Card(
        modifier = modifier
            .fillMaxWidth()
            .semantics(mergeDescendants = true) {
                contentDescription = "第 ${slot.order} 个动作，${slot.bodyPart}，${slot.exerciseName}，${slot.sets} 组，每组 ${slot.reps}"
            },
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = androidx.compose.foundation.BorderStroke(
            1.dp,
            MaterialTheme.colorScheme.outline.copy(alpha = 0.2f),
        ),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Card(
                    modifier = Modifier.size(48.dp),
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
                    elevation = CardDefaults.cardElevation(0.dp),
                ) {
                    androidx.compose.foundation.layout.Box(
                        Modifier.fillMaxSize(),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text("${slot.order}", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                    }
                }
                Column(Modifier.weight(1f)) {
                    Text(slot.bodyPart, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                    Text(slot.exerciseName, style = MaterialTheme.typography.titleLarge)
                }
                LabelPill(slot.status)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                IconLabel(Icons.Rounded.FitnessCenter, slot.equipment, Modifier.weight(1f))
                Text("${slot.sets} × ${slot.reps}", style = MaterialTheme.typography.titleMedium)
            }
            Divider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f))
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                IconLabel(Icons.Rounded.Info, "动作要点", color = MaterialTheme.colorScheme.primary)
                Text(slot.cue, style = MaterialTheme.typography.bodyLarge)
            }
            KeyValueRow("上次表现", slot.previousPerformance)
            KeyValueRow("本次重量", slot.suggestedWeight)
            KeyValueRow("器械设置", slot.setupNote)
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("可选替代动作", style = MaterialTheme.typography.labelLarge)
                slot.alternatives.forEach { alternative ->
                    Text(
                        "${alternative.name} · ${alternative.equipment}",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Text(
                    "替代动作使用独立重量记录",
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.secondary,
                )
            }
            Button(
                onClick = onStart,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(50.dp),
            ) {
                Icon(Icons.Rounded.PlayArrow, contentDescription = null)
                Text("  开始 ${slot.exerciseName}")
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(
                    onClick = onSwap,
                    modifier = Modifier
                        .weight(1f)
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.ChangeCircle, contentDescription = null)
                    Spacer(Modifier.width(6.dp))
                    Text("更换动作")
                }
                TextButton(
                    onClick = onSkip,
                    modifier = Modifier
                        .weight(1f)
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.SkipNext, contentDescription = null)
                    Spacer(Modifier.width(6.dp))
                    Text("跳过")
                }
            }
        }
    }
}
