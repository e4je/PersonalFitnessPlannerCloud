package com.personalfitnessplanner.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.AccessTime
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.CloudDone
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.Info
import androidx.compose.material.icons.rounded.PauseCircle
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.personalfitnessplanner.ui.WorkoutExecutionCallbacks
import com.personalfitnessplanner.ui.components.CompletionMark
import com.personalfitnessplanner.ui.components.IconLabel
import com.personalfitnessplanner.ui.components.LabelPill
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.model.WorkoutExecutionUiState
import com.personalfitnessplanner.ui.model.WorkoutSetUi

@Composable
fun WorkoutExecutionScreen(
    state: WorkoutExecutionUiState,
    callbacks: WorkoutExecutionCallbacks,
    modifier: Modifier = Modifier,
    onBack: () -> Unit = {},
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 36.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item {
            ScreenHeader(
                title = state.workoutLabel,
                subtitle = "${state.exercisePosition} · 已训练 ${state.elapsedTime}",
                onBack = onBack,
                trailing = { LabelPill("离线自动保存") },
            )
        }
        if (state.isResting) {
            item {
                RestTimerCard(
                    secondsRemaining = state.restSecondsRemaining,
                    modifier = Modifier.padding(horizontal = 16.dp),
                )
            }
        }
        item {
            ExerciseFocusCard(state, Modifier.padding(horizontal = 16.dp))
        }
        items(state.sets.size, key = { state.sets[it].id }) { index ->
            WorkoutSetCard(
                workoutSet = state.sets[index],
                onChanged = { callbacks.onSetChanged(state.sets[index].id, it) },
                onComplete = { callbacks.onSetComplete(state.sets[index].id) },
                onEdit = { callbacks.onEditPreviousSet(state.sets[index].id) },
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
        item {
            Column(
                Modifier.padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                IconLabel(Icons.Rounded.CloudDone, state.autosaveMessage)
                Button(
                    onClick = callbacks.onFinishWorkout,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(54.dp),
                ) {
                    Icon(Icons.Rounded.Check, contentDescription = null)
                    Text("  完成整次训练")
                }
                TextButton(
                    onClick = callbacks.onEndWorkoutEarly,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.PauseCircle, contentDescription = null)
                    Text("  中途结束并保存")
                }
            }
        }
    }
}

@Composable
private fun RestTimerCard(secondsRemaining: Int, modifier: Modifier = Modifier) {
    val minutes = secondsRemaining.coerceAtLeast(0) / 60
    val seconds = secondsRemaining.coerceAtLeast(0) % 60
    Card(
        modifier = modifier
            .fillMaxWidth()
            .semantics { contentDescription = "组间休息剩余 $minutes 分 $seconds 秒" },
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.secondaryContainer,
            contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
        ),
    ) {
        Row(
            Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Icon(Icons.Rounded.AccessTime, contentDescription = null)
            Column(Modifier.weight(1f)) {
                Text("组间休息", style = MaterialTheme.typography.labelLarge)
                Text(
                    "%d:%02d".format(minutes, seconds),
                    style = MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.Bold,
                )
            }
            Text("结束时会通知", style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
private fun ExerciseFocusCard(state: WorkoutExecutionUiState, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
    ) {
        Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(state.exerciseName, style = MaterialTheme.typography.headlineMedium)
                    Text(state.equipment, style = MaterialTheme.typography.bodyLarge)
                }
                LabelPill(state.target, emphasized = true)
            }
            IconLabel(Icons.Rounded.Info, state.cue, color = MaterialTheme.colorScheme.onPrimaryContainer)
            IconLabel(Icons.Rounded.FitnessCenter, state.setupNote, color = MaterialTheme.colorScheme.onPrimaryContainer)
        }
    }
}

@Composable
fun WorkoutSetCard(
    workoutSet: WorkoutSetUi,
    onChanged: (com.personalfitnessplanner.ui.model.WorkoutSetDraft) -> Unit,
    onComplete: () -> Unit,
    onEdit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val draft = workoutSet.draft
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (workoutSet.completed) {
                MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.55f)
            } else {
                MaterialTheme.colorScheme.surface
            },
        ),
        border = androidx.compose.foundation.BorderStroke(
            1.dp,
            MaterialTheme.colorScheme.outline.copy(alpha = 0.25f),
        ),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("第 ${workoutSet.number} 组", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                if (workoutSet.completed) {
                    CompletionMark(true)
                } else {
                    LabelPill(if (draft.isWarmup) "热身组" else "正式组", emphasized = !draft.isWarmup)
                }
            }
            if (workoutSet.completed) {
                Text(
                    "${draft.weight} kg × ${draft.reps} 次 · RIR ${draft.rir} · ${draft.quality} · 疼痛 ${draft.pain}/10",
                    style = MaterialTheme.typography.bodyLarge,
                )
                if (draft.note.isNotBlank()) Text("备注：${draft.note}", style = MaterialTheme.typography.bodyMedium)
                OutlinedButton(
                    onClick = onEdit,
                    enabled = workoutSet.isEditable,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.Edit, contentDescription = null)
                    Text("  修改上一组")
                }
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedTextField(
                        value = draft.weight,
                        onValueChange = { onChanged(draft.copy(weight = it)) },
                        label = { Text("重量 kg") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                    )
                    OutlinedTextField(
                        value = draft.reps,
                        onValueChange = { onChanged(draft.copy(reps = it)) },
                        label = { Text("次数") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                    )
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(
                        checked = draft.isWarmup,
                        onCheckedChange = { onChanged(draft.copy(isWarmup = it)) },
                    )
                    Text("这是热身组", style = MaterialTheme.typography.bodyLarge)
                }
                ChoiceRow(
                    label = "余力 RIR",
                    choices = listOf("0", "1", "2", "3", "4+"),
                    selected = if (draft.rir >= 4) "4+" else draft.rir.toString(),
                    onSelected = { onChanged(draft.copy(rir = it.removeSuffix("+").toInt())) },
                )
                ChoiceRow(
                    label = "动作质量",
                    choices = listOf("良好", "一般", "需改进"),
                    selected = draft.quality,
                    onSelected = { onChanged(draft.copy(quality = it)) },
                )
                ChoiceRow(
                    label = "疼痛反馈",
                    choices = listOf("0", "2", "5", "8"),
                    selected = draft.pain.toString(),
                    onSelected = { onChanged(draft.copy(pain = it.toInt())) },
                    supportingText = if (draft.pain > 0) "有疼痛时不会建议加重量" else null,
                )
                OutlinedTextField(
                    value = draft.note,
                    onValueChange = { onChanged(draft.copy(note = it)) },
                    label = { Text("备注（可选）") },
                    modifier = Modifier.fillMaxWidth(),
                    minLines = 2,
                    maxLines = 4,
                )
                Button(
                    onClick = onComplete,
                    enabled = draft.weight.isNotBlank() && draft.reps.isNotBlank(),
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Text("完成第 ${workoutSet.number} 组")
                }
            }
        }
    }
}

@Composable
private fun ChoiceRow(
    label: String,
    choices: List<String>,
    selected: String,
    onSelected: (String) -> Unit,
    supportingText: String? = null,
) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(label, style = MaterialTheme.typography.labelLarge)
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(choices.size) { index ->
                val choice = choices[index]
                FilterChip(
                    selected = selected == choice,
                    onClick = { onSelected(choice) },
                    label = { Text(choice) },
                )
            }
        }
        if (supportingText != null) {
            Text(supportingText, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
        }
    }
}
