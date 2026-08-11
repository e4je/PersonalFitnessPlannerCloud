package com.personalfitnessplanner.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.Clear
import androidx.compose.material.icons.rounded.CloudDone
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.personalfitnessplanner.ui.ExerciseLibraryCallbacks
import com.personalfitnessplanner.ui.components.IconLabel
import com.personalfitnessplanner.ui.components.KeyValueRow
import com.personalfitnessplanner.ui.components.LabelPill
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.model.ExerciseLibraryUiState
import com.personalfitnessplanner.ui.model.LibraryExerciseUi

@Composable
fun ExerciseLibraryScreen(
    state: ExerciseLibraryUiState,
    callbacks: ExerciseLibraryCallbacks,
    modifier: Modifier = Modifier,
) {
    val visibleExercises = state.exercises.filter {
        (state.selectedBodyPart == "全部" || it.bodyPart == state.selectedBodyPart) &&
            (state.query.isBlank() || listOf(it.name, it.bodyPart, it.equipment).any { value ->
                value.contains(state.query, ignoreCase = true)
            })
    }
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 32.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item { ScreenHeader(title = "动作库", subtitle = "动作定义由云端维护，本机可离线查看") }
        item {
            Column(
                Modifier.padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                OutlinedTextField(
                    value = state.query,
                    onValueChange = callbacks.onSearch,
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("搜索动作、部位或器械") },
                    singleLine = true,
                    leadingIcon = { Icon(Icons.Rounded.Search, contentDescription = null) },
                    trailingIcon = if (state.query.isNotEmpty()) {
                        {
                            IconButton(
                                onClick = { callbacks.onSearch("") },
                                modifier = Modifier.semantics { contentDescription = "清空搜索" },
                            ) {
                                Icon(Icons.Rounded.Clear, contentDescription = null)
                            }
                        }
                    } else null,
                )
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    val parts = listOf("全部", "胸部", "背部", "腿部", "肩部", "手臂", "核心")
                    items(parts.size) { index ->
                        FilterChip(
                            selected = state.selectedBodyPart == parts[index],
                            onClick = { callbacks.onBodyPartChanged(parts[index]) },
                            label = { Text(parts[index]) },
                        )
                    }
                }
            }
        }
        item {
            Card(
                modifier = Modifier.padding(horizontal = 16.dp),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
            ) {
                IconLabel(
                    Icons.Rounded.CloudDone,
                    "服务器动作定义为只读；个人器械备注会单独保存",
                    modifier = Modifier.padding(14.dp),
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
        }
        if (visibleExercises.isEmpty()) {
            item {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .padding(32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Icon(Icons.Rounded.ErrorOutline, contentDescription = null)
                    Text("没有找到匹配动作", style = MaterialTheme.typography.titleMedium)
                    Text("尝试更换关键词或部位筛选", style = MaterialTheme.typography.bodyMedium)
                }
            }
        } else {
            items(visibleExercises.size, key = { visibleExercises[it].id }) { index ->
                ExerciseLibraryCard(
                    exercise = visibleExercises[index],
                    onOpen = { callbacks.onOpen(visibleExercises[index].id) },
                    onNoteSave = { callbacks.onNoteSave(visibleExercises[index].id, it) },
                    modifier = Modifier.padding(horizontal = 16.dp),
                )
            }
        }
    }
}

@Composable
private fun ExerciseLibraryCard(
    exercise: LibraryExerciseUi,
    onOpen: () -> Unit,
    onNoteSave: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    var noteDraft by rememberSaveable(exercise.id, exercise.personalEquipmentNote) {
        mutableStateOf(exercise.personalEquipmentNote)
    }
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(exercise.bodyPart, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                    Text(exercise.name, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                }
                LabelPill(exercise.version)
            }
            IconLabel(Icons.Rounded.FitnessCenter, exercise.equipment)
            KeyValueRow("默认组次", exercise.defaultPrescription)
            KeyValueRow("动作提示", exercise.cue)
            KeyValueRow("常见错误", exercise.commonMistakes)
            KeyValueRow("替代动作", exercise.alternatives, showDivider = false)
            OutlinedTextField(
                value = noteDraft,
                onValueChange = { noteDraft = it },
                modifier = Modifier
                    .fillMaxWidth()
                    .semantics { contentDescription = "${exercise.name}的个人器械备注" },
                label = { Text("个人器械备注") },
                placeholder = { Text("例如：座椅 4 档、安全杆 6 档") },
                supportingText = { Text("仅保存在本机；服务器动作定义保持只读") },
                minLines = 1,
                maxLines = 3,
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(
                    onClick = onOpen,
                    modifier = Modifier
                        .weight(1f)
                        .height(50.dp),
                ) {
                    Text("动作定义说明")
                    androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
                    Icon(Icons.Rounded.ChevronRight, contentDescription = null)
                }
                Button(
                    onClick = { onNoteSave(noteDraft) },
                    enabled = noteDraft.trim() != exercise.personalEquipmentNote,
                    modifier = Modifier
                        .weight(1f)
                        .height(50.dp)
                        .semantics { contentDescription = "保存${exercise.name}的个人器械备注" },
                ) {
                    Text("保存个人备注")
                }
            }
        }
    }
}
