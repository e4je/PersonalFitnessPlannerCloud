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
import androidx.compose.material.icons.rounded.CloudOff
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import com.personalfitnessplanner.ui.OnboardingCallbacks
import com.personalfitnessplanner.ui.components.FitnessCard
import com.personalfitnessplanner.ui.components.ScreenHeader
import com.personalfitnessplanner.ui.components.SectionTitle
import com.personalfitnessplanner.ui.model.OnboardingUiState
import com.personalfitnessplanner.ui.model.WeightUnit

@Composable
fun OnboardingScreen(
    state: OnboardingUiState,
    callbacks: OnboardingCallbacks,
    modifier: Modifier = Modifier,
) {
    val config = state.config
    var passwordVisible by remember { mutableStateOf(false) }

    LazyColumn(
        modifier = modifier.fillMaxSize().testTag("onboarding_list"),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 32.dp),
    ) {
        item {
            ScreenHeader(
                title = "欢迎开始训练",
                subtitle = "一次设置，之后离线也能记录",
            )
        }
        item {
            Column(
                Modifier.padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text("连接与偏好", style = MaterialTheme.typography.labelLarge)
                    Text("${state.step} / ${state.totalSteps}", style = MaterialTheme.typography.labelLarge)
                }
                LinearProgressIndicator(
                    progress = { state.step.toFloat() / state.totalSteps.coerceAtLeast(1) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(8.dp),
                )
                Spacer(Modifier.height(8.dp))
            }
        }
        item {
            FitnessCard(
                Modifier
                    .padding(horizontal = 16.dp)
                    .fillMaxWidth(),
            ) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    SectionTitle("连接云端", supportingText = "只连接 HTTPS REST API，不会直连数据库")
                    OutlinedTextField(
                        value = config.apiBaseUrl,
                        onValueChange = { callbacks.onConfigChanged(config.copy(apiBaseUrl = it)) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("后端 API 地址") },
                        supportingText = { Text("示例：https://fitness.example.com/") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    )
                    OutlinedTextField(
                        value = config.account,
                        onValueChange = { callbacks.onConfigChanged(config.copy(account = it)) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("账号") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
                        leadingIcon = { Icon(Icons.Rounded.Person, contentDescription = null) },
                    )
                    OutlinedTextField(
                        value = config.password,
                        onValueChange = { callbacks.onConfigChanged(config.copy(password = it)) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("密码") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                        visualTransformation = if (passwordVisible) VisualTransformation.None else PasswordVisualTransformation(),
                        leadingIcon = { Icon(Icons.Rounded.Lock, contentDescription = null) },
                        trailingIcon = {
                            IconButton(
                                onClick = { passwordVisible = !passwordVisible },
                                modifier = Modifier.semantics {
                                    contentDescription = if (passwordVisible) "隐藏密码" else "显示密码"
                                },
                            ) {
                                Icon(
                                    if (passwordVisible) Icons.Rounded.VisibilityOff else Icons.Rounded.Visibility,
                                    contentDescription = null,
                                )
                            }
                        },
                    )
                    if (state.errorMessage != null) {
                        Text(
                            state.errorMessage,
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                }
            }
        }
        item {
            FitnessCard(
                Modifier
                    .padding(start = 16.dp, end = 16.dp, top = 16.dp)
                    .fillMaxWidth(),
            ) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    SectionTitle("训练偏好", supportingText = "以后可在设置中修改")
                    Text("重量单位", style = MaterialTheme.typography.labelLarge)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        WeightUnit.entries.forEach { unit ->
                            FilterChip(
                                selected = config.weightUnit == unit,
                                onClick = { callbacks.onConfigChanged(config.copy(weightUnit = unit)) },
                                label = { Text(unit.label) },
                                modifier = Modifier.weight(1f),
                            )
                        }
                    }
                    OutlinedTextField(
                        value = config.timezone,
                        onValueChange = { callbacks.onConfigChanged(config.copy(timezone = it)) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text("时区") },
                        supportingText = { Text("使用 IANA 名称，例如 Asia/Shanghai") },
                        singleLine = true,
                    )
                    Text("训练日", style = MaterialTheme.typography.labelLarge)
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(7) { index ->
                            val day = listOf("周一", "周二", "周三", "周四", "周五", "周六", "周日")[index]
                            FilterChip(
                                selected = day in config.trainingDays,
                                onClick = {
                                    val days = config.trainingDays.toMutableSet().apply {
                                        if (!add(day)) remove(day)
                                    }
                                    callbacks.onConfigChanged(config.copy(trainingDays = days))
                                },
                                label = { Text(day) },
                            )
                        }
                    }
                }
            }
        }
        item {
            Column(
                Modifier.padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Button(
                    onClick = { callbacks.onSubmit(config) },
                    enabled = !state.isSubmitting,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Text(if (state.isSubmitting) "正在登录…" else "登录并继续")
                }
                OutlinedButton(
                    onClick = callbacks.onDownloadPlan,
                    enabled = !state.isSubmitting,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Text("下载云端计划")
                }
                TextButton(
                    onClick = callbacks.onUseLocalMode,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Icon(Icons.Rounded.CloudOff, contentDescription = null)
                    Spacer(Modifier.height(0.dp))
                    Text(" 后端不可达？使用内置计划进入本地模式")
                }
                Text(
                    "登录令牌将由 Android Keystore 保护",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}
