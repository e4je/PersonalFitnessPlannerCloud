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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.ChevronRight
import androidx.compose.material.icons.rounded.CloudOff
import androidx.compose.material.icons.rounded.DeleteOutline
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.FileDownload
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.Schedule
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
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
import com.personalfitnessplanner.ui.HistoryCallbacks
import com.personalfitnessplanner.ui.components.IconLabel
import com.personalfitnessplanner.ui.components.LabelPill
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.components.SectionTitle
import com.personalfitnessplanner.ui.model.ExportFormat
import com.personalfitnessplanner.ui.model.HistorySessionUi
import com.personalfitnessplanner.ui.model.HistoryUiState
import com.personalfitnessplanner.ui.model.TrendPointUi

@Composable
fun HistoryScreen(
    state: HistoryUiState,
    callbacks: HistoryCallbacks,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 32.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item { ScreenHeader(title = "训练历史", subtitle = state.summary) }
        item {
            HistoryFilters(
                state = state,
                onPeriod = { callbacks.onFilterChanged(state.filter.copy(period = it)) },
                onWorkoutType = { callbacks.onFilterChanged(state.filter.copy(workoutType = it)) },
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
        item {
            TrendCard(
                title = state.trendExercise,
                points = state.trend,
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
        item {
            Row(
                Modifier.padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                OutlinedButton(
                    onClick = { callbacks.onExport(ExportFormat.CSV) },
                    modifier = Modifier
                        .weight(1f)
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.FileDownload, contentDescription = null)
                    Text("  导出 CSV")
                }
                OutlinedButton(
                    onClick = { callbacks.onExport(ExportFormat.JSON) },
                    modifier = Modifier
                        .weight(1f)
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.FileDownload, contentDescription = null)
                    Text("  导出 JSON")
                }
            }
        }
        item {
            SectionTitle(
                title = "训练记录",
                supportingText = "点击记录查看动作、重量与每组详情",
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
            )
        }
        items(state.sessions.size, key = { state.sessions[it].id }) { index ->
            val session = state.sessions[index]
            HistorySessionCard(
                session = session,
                onOpen = { callbacks.onOpen(session.id) },
                onEdit = { callbacks.onEdit(session.id) },
                onDelete = { callbacks.onDelete(session.id) },
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
    }
}

@Composable
private fun HistoryFilters(
    state: HistoryUiState,
    onPeriod: (String) -> Unit,
    onWorkoutType: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("筛选时间", style = MaterialTheme.typography.labelLarge)
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            val periods = listOf("近 7 天", "近 30 天", "近 90 天", "全部")
            items(periods.size) { index ->
                FilterChip(
                    selected = state.filter.period == periods[index],
                    onClick = { onPeriod(periods[index]) },
                    label = { Text(periods[index]) },
                )
            }
        }
        Text("训练类型", style = MaterialTheme.typography.labelLarge)
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            val types = listOf("全部", "A", "B", "有氧", "恢复")
            items(types.size) { index ->
                FilterChip(
                    selected = state.filter.workoutType == types[index],
                    onClick = { onWorkoutType(types[index]) },
                    label = { Text(types[index]) },
                )
            }
        }
        Text(
            "动作筛选：${state.filter.exercise}",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun TrendCard(
    title: String,
    points: List<TrendPointUi>,
    modifier: Modifier = Modifier,
) {
    val maxValue = points.maxOfOrNull { it.value }?.coerceAtLeast(1f) ?: 1f
    Card(
        modifier = modifier
            .fillMaxWidth()
            .semantics(mergeDescendants = true) {
                contentDescription = "$title，${points.joinToString("，") { "${it.label} ${it.displayValue}" }}"
            },
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            SectionTitle("重量与次数趋势", supportingText = title)
            Row(
                Modifier
                    .fillMaxWidth()
                    .height(150.dp),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.Bottom,
            ) {
                points.forEach { point ->
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Bottom,
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(point.value.toString().removeSuffix(".0"), style = MaterialTheme.typography.labelLarge)
                        Spacer(Modifier.height(4.dp))
                        Box(
                            Modifier
                                .width(28.dp)
                                .height((88f * point.value / maxValue).coerceAtLeast(8f).dp)
                                .background(MaterialTheme.colorScheme.primary, RoundedCornerShape(topStart = 8.dp, topEnd = 8.dp)),
                        )
                        Spacer(Modifier.height(6.dp))
                        Text(point.label, style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
        }
    }
}

@Composable
private fun HistorySessionCard(
    session: HistorySessionUi,
    onOpen: () -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(session.date, style = MaterialTheme.typography.titleMedium)
                    Text(session.workoutType, style = MaterialTheme.typography.headlineMedium)
                }
                LabelPill(session.status, emphasized = session.status == "待同步")
            }
            Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                IconLabel(Icons.Rounded.Schedule, session.duration)
                IconLabel(Icons.Rounded.FitnessCenter, "${session.completedSets} 组")
            }
            Text("总训练量 ${session.totalVolume}", style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
            if (session.syncDetail != null) {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
                    IconLabel(
                        Icons.Rounded.CloudOff,
                        session.syncDetail,
                        modifier = Modifier.padding(12.dp),
                        color = MaterialTheme.colorScheme.onSecondaryContainer,
                    )
                }
            }
            OutlinedButton(
                onClick = onOpen,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(50.dp),
            ) {
                Text("查看训练详情")
                Spacer(Modifier.weight(1f))
                Icon(Icons.Rounded.ChevronRight, contentDescription = null)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = onEdit, modifier = Modifier.weight(1f).height(48.dp)) {
                    Icon(Icons.Rounded.Edit, contentDescription = null)
                    Text("  编辑")
                }
                TextButton(onClick = onDelete, modifier = Modifier.weight(1f).height(48.dp)) {
                    Icon(Icons.Rounded.DeleteOutline, contentDescription = null, tint = MaterialTheme.colorScheme.error)
                    Text("  软删除", color = MaterialTheme.colorScheme.error)
                }
            }
        }
    }
}
