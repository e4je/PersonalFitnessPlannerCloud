package com.personalfitnessplanner.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Backup
import androidx.compose.material.icons.rounded.CloudDone
import androidx.compose.material.icons.rounded.DataObject
import androidx.compose.material.icons.rounded.DeleteSweep
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.Logout
import androidx.compose.material.icons.rounded.Security
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import com.personalfitnessplanner.ui.SettingsCallbacks
import com.personalfitnessplanner.ui.components.IconLabel
import com.personalfitnessplanner.ui.components.KeyValueRow
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.components.SectionTitle
import com.personalfitnessplanner.ui.components.SyncStatusPill
import com.personalfitnessplanner.ui.model.ExportFormat
import com.personalfitnessplanner.ui.model.SettingsKey
import com.personalfitnessplanner.ui.model.SettingsUiState
import com.personalfitnessplanner.ui.model.ThemeMode
import com.personalfitnessplanner.ui.model.WeightUnit

@Composable
fun SettingsScreen(
    state: SettingsUiState,
    callbacks: SettingsCallbacks,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize().testTag("settings_list"),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 40.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item { ScreenHeader(title = "设置", subtitle = "账号、同步与训练偏好") }
        item {
            SettingsSection("连接与账号", Modifier.padding(horizontal = 16.dp)) {
                OutlinedTextField(
                    value = state.apiBaseUrl,
                    onValueChange = { callbacks.onSettingChanged(SettingsKey.ApiBaseUrl, it) },
                    label = { Text("API 地址") },
                    supportingText = { Text("修改后将在下次同步时生效") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                KeyValueRow("账号", state.accountName)
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("同步状态", style = MaterialTheme.typography.bodyMedium)
                        Text("上次同步：${state.lastSync}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    SyncStatusPill(state.syncStatus)
                }
                OutlinedButton(
                    onClick = callbacks.onSync,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.Sync, contentDescription = null)
                    Text("  立即同步")
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    OutlinedButton(
                        onClick = callbacks.onUploadLocal,
                        modifier = Modifier.weight(1f).height(50.dp),
                    ) {
                        Text("上传本地")
                    }
                    OutlinedButton(
                        onClick = callbacks.onDownloadCloudOverwrite,
                        modifier = Modifier.weight(1f).height(50.dp),
                    ) {
                        Text("云端覆盖")
                    }
                }
                Text(
                    "上传本地只推送待同步记录；云端覆盖只下载服务器权威计划。存在未上传记录时会自动阻止覆盖。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("后台自动同步", style = MaterialTheme.typography.bodyLarge)
                        Text("断网后将自动重试", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    Switch(
                        checked = state.autoSync,
                        onCheckedChange = { callbacks.onSettingChanged(SettingsKey.AutoSync, it.toString()) },
                    )
                }
            }
        }
        item {
            SettingsSection("训练偏好", Modifier.padding(horizontal = 16.dp)) {
                OutlinedTextField(
                    value = state.timezone,
                    onValueChange = { callbacks.onSettingChanged(SettingsKey.Timezone, it) },
                    label = { Text("时区") },
                    supportingText = { Text("IANA 时区名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Text("重量单位", style = MaterialTheme.typography.labelLarge)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    WeightUnit.entries.forEach { unit ->
                        FilterChip(
                            selected = state.weightUnit == unit,
                            onClick = { callbacks.onSettingChanged(SettingsKey.WeightUnit, unit.name) },
                            label = { Text(unit.label) },
                            modifier = Modifier.weight(1f),
                        )
                    }
                }
                Text("训练日（可多选，至少保留一天）", style = MaterialTheme.typography.labelLarge)
                val selectedDayNumbers = selectedTrainingDays(state.trainingDays)
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(TrainingDayLabels.size) { index ->
                        val day = index + 1
                        val selected = day in selectedDayNumbers
                        FilterChip(
                            selected = selected,
                            onClick = {
                                if (!(selected && selectedDayNumbers.size == 1)) {
                                    val updated = selectedDayNumbers.toMutableSet().apply {
                                        if (!add(day)) remove(day)
                                    }
                                    callbacks.onSettingChanged(
                                        SettingsKey.TrainingDays,
                                        updated.sorted().joinToString(","),
                                    )
                                }
                            },
                            label = { Text(TrainingDayLabels[index]) },
                            modifier = Modifier.testTag("training_day_$day"),
                        )
                    }
                }
                Text("组间休息", style = MaterialTheme.typography.labelLarge)
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    val options = listOf(60, 90, 120, 180)
                    items(options.size) { index ->
                        val seconds = options[index]
                        FilterChip(
                            selected = state.restSeconds == seconds,
                            onClick = { callbacks.onSettingChanged(SettingsKey.RestSeconds, seconds.toString()) },
                            label = { Text("${seconds} 秒") },
                        )
                    }
                }
            }
        }
        item {
            SettingsSection("外观", Modifier.padding(horizontal = 16.dp)) {
                Text("深色模式", style = MaterialTheme.typography.labelLarge)
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    val modes = ThemeMode.entries
                    items(modes.size) { index ->
                        val mode = modes[index]
                        FilterChip(
                            selected = state.themeMode == mode,
                            onClick = { callbacks.onSettingChanged(SettingsKey.ThemeMode, mode.name) },
                            label = { Text(mode.label) },
                        )
                    }
                }
            }
        }
        item {
            SettingsSection("数据管理", Modifier.padding(horizontal = 16.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = { callbacks.onExport(ExportFormat.CSV) },
                        modifier = Modifier
                            .weight(1f)
                            .height(50.dp),
                    ) {
                        Icon(Icons.Rounded.Download, contentDescription = null)
                        Text("  CSV")
                    }
                    OutlinedButton(
                        onClick = { callbacks.onExport(ExportFormat.JSON) },
                        modifier = Modifier
                            .weight(1f)
                            .height(50.dp),
                    ) {
                        Icon(Icons.Rounded.DataObject, contentDescription = null)
                        Text("  JSON")
                    }
                }
                OutlinedButton(
                    onClick = callbacks.onLocalBackup,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.Backup, contentDescription = null)
                    Text("  创建本地备份")
                }
                OutlinedButton(
                    onClick = callbacks.onClearCache,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                ) {
                    Icon(Icons.Rounded.DeleteSweep, contentDescription = null)
                    Text("  清理缓存（${state.cacheSize}）")
                }
            }
        }
        item {
            SettingsSection("安全与版本", Modifier.padding(horizontal = 16.dp)) {
                IconLabel(Icons.Rounded.Security, "登录令牌由 Android Keystore 保护")
                IconLabel(Icons.Rounded.CloudDone, "Release 版本不输出敏感网络日志")
                KeyValueRow("应用版本", state.appVersion, showDivider = false)
                TextButton(
                    onClick = callbacks.onLogout,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Icon(Icons.Rounded.Logout, contentDescription = null, tint = MaterialTheme.colorScheme.error)
                    Text("  退出登录", color = MaterialTheme.colorScheme.error)
                }
            }
        }
    }
}

private val TrainingDayLabels = listOf("周一", "周二", "周三", "周四", "周五", "周六", "周日")

private fun selectedTrainingDays(value: String): Set<Int> = value
    .split('、', ',')
    .mapNotNull { token ->
        token.trim().toIntOrNull()?.takeIf { it in 1..7 }
            ?: TrainingDayLabels.indexOf(token.trim()).takeIf { it >= 0 }?.plus(1)
    }
    .toSet()
    .ifEmpty { setOf(1) }

@Composable
private fun SettingsSection(
    title: String,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionTitle(title)
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),
        ) {
            Column(
                Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                content = content,
            )
        }
    }
}
