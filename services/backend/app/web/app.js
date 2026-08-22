const API_ROOT = "/api/v1";
const TOKEN_KEY = "pfpc.access_token";
const REFRESH_KEY = "pfpc.refresh_token";

const state = {
  accessToken: sessionStorage.getItem(TOKEN_KEY),
  refreshToken: sessionStorage.getItem(REFRESH_KEY),
  me: null,
  bootstrap: null,
  users: [],
  selectedUser: null,
  plans: [],
  selectedPlan: null,
  registrationEnabled: true,
  setup: null,
  refreshInFlight: null,
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function formatDate(value) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? String(value) : date.toLocaleString("zh-CN", { dateStyle: "medium", timeStyle: "short" });
}

function todayIso() {
  const date = new Date();
  const pad = (value) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function isAdmin() {
  return Boolean(state.me && (state.me.is_superuser || (state.me.roles || []).some((role) => role.toLowerCase() === "admin")));
}

function isSuperuser() {
  return Boolean(state.me?.is_superuser);
}

function setTokens(tokens) {
  if (!tokens) {
    state.accessToken = null;
    state.refreshToken = null;
    sessionStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(REFRESH_KEY);
    return;
  }
  state.accessToken = tokens?.access_token || null;
  state.refreshToken = tokens?.refresh_token || state.refreshToken || null;
  if (state.accessToken) sessionStorage.setItem(TOKEN_KEY, state.accessToken);
  else sessionStorage.removeItem(TOKEN_KEY);
  if (state.refreshToken) sessionStorage.setItem(REFRESH_KEY, state.refreshToken);
  else sessionStorage.removeItem(REFRESH_KEY);
}

function clearSession() {
  setTokens(null);
  state.me = null;
  state.bootstrap = null;
  state.selectedUser = null;
  state.selectedPlan = null;
  showAuth();
  $("#logout-button").classList.add("hidden");
  $("#connection-state").textContent = "未连接";
  $("#connection-state").classList.remove("connected", "error");
  $$(".admin-only").forEach((element) => element.classList.add("hidden"));
}

function showAuth() {
  $("#setup-view").classList.add("hidden");
  $("#dashboard-view").classList.add("hidden");
  $("#auth-view").classList.remove("hidden");
}

function showSetup() {
  $("#auth-view").classList.add("hidden");
  $("#dashboard-view").classList.add("hidden");
  $("#setup-view").classList.remove("hidden");
  $("#logout-button").classList.add("hidden");
  const connection = $("#connection-state");
  connection.textContent = "等待初始化";
  connection.classList.remove("connected", "error");
}

function showToast(message, isError = false) {
  const toast = $("#toast");
  toast.textContent = message;
  toast.classList.toggle("toast-danger", isError);
  toast.classList.add("show");
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => toast.classList.remove("show"), 3600);
}

function errorMessage(error) {
  if (error?.data?.detail) {
    const detail = error.data.detail;
    if (typeof detail === "string") return detail;
    if (detail.message) return detail.message;
  }
  return error?.message || "请求失败，请稍后重试";
}

function setupErrorMessage(error) {
  const code = error?.data?.detail?.code;
  const messages = {
    invalid_setup_token: "一次性初始化码不正确，请从后端启动日志复制。",
    setup_rate_limited: "尝试次数过多，请稍后再试。",
    database_password_invalid: "数据库密码必须为 1–1024 个字符。",
    database_connection_failed: "无法连接 MySQL，或该账号没有创建/访问 fitness 数据库的权限。",
    database_initialization_failed: "已经连接 MySQL，但表结构升级或默认数据初始化失败。请查看后端日志。",
    setup_persistence_failed: "数据库已初始化，但后端无法保存私有配置。请检查 /app-data 的写入权限。",
    setup_storage_unavailable: "后端运行配置目录不可写，请检查 /app-data 的挂载和权限。",
    setup_in_progress: "另一个初始化请求正在执行，请稍后再试。",
    setup_complete: "数据库已经配置完成，请刷新页面。",
  };
  return messages[code] || errorMessage(error);
}

async function refreshAccessToken() {
  if (!state.refreshToken) return false;
  if (state.refreshInFlight) return state.refreshInFlight;
  state.refreshInFlight = (async () => {
    try {
      const response = await fetch(`${API_ROOT}/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refresh_token: state.refreshToken }),
      });
      if (!response.ok) return false;
      setTokens(await response.json());
      return true;
    } catch {
      return false;
    } finally {
      state.refreshInFlight = null;
    }
  })();
  return state.refreshInFlight;
}

async function api(path, options = {}, retry = true) {
  const headers = new Headers(options.headers || {});
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  if (state.accessToken) headers.set("Authorization", `Bearer ${state.accessToken}`);
  let response;
  try {
    response = await fetch(`${API_ROOT}${path}`, { ...options, headers });
  } catch (cause) {
    const error = new Error("无法连接到服务器");
    error.cause = cause;
    throw error;
  }
  if (response.status === 401 && retry && path !== "/auth/refresh" && await refreshAccessToken()) {
    return api(path, options, false);
  }
  const contentType = response.headers.get("content-type") || "";
  const data = contentType.includes("json") ? await response.json().catch(() => null) : await response.text();
  if (!response.ok) {
    const error = new Error(`请求失败（${response.status}）`);
    error.status = response.status;
    error.data = data;
    if (response.status === 401) clearSession();
    throw error;
  }
  return data;
}

function setAuthError(message = "") {
  $("#auth-error").textContent = message;
}

function setConnection(connected, failed = false) {
  const element = $("#connection-state");
  element.textContent = connected ? "已连接" : failed ? "连接失败" : "未连接";
  element.classList.toggle("connected", connected);
  element.classList.toggle("error", failed);
}

function switchAuthTab(tab) {
  $$("[data-auth-tab]").forEach((button) => {
    const active = button.dataset.authTab === tab;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", String(active));
  });
  $("#login-form").classList.toggle("hidden", tab !== "login");
  $("#register-form").classList.toggle("hidden", tab !== "register");
  setAuthError();
  if (tab === "register") loadRegistrationStatus();
}

async function loadRegistrationStatus() {
  try {
    const result = await api("/auth/registration-status", {}, false);
    state.registrationEnabled = Boolean(result.enabled);
    const note = $("#registration-note");
    note.textContent = state.registrationEnabled ? "当前允许公开注册。" : "管理员已关闭公开注册，请联系管理员创建账号。";
    $("#register-form button[type=submit]").disabled = !state.registrationEnabled;
  } catch (error) {
    $("#registration-note").textContent = "暂时无法读取注册设置。";
    setConnection(false, true);
  }
}

function showDashboard() {
  $("#setup-view").classList.add("hidden");
  $("#auth-view").classList.add("hidden");
  $("#dashboard-view").classList.remove("hidden");
  $("#logout-button").classList.remove("hidden");
  $$(".superuser-only").forEach((element) => element.classList.toggle("hidden", !isSuperuser()));
  $$(".admin-only").forEach((element) => element.classList.toggle("hidden", !isAdmin()));
}

async function loadSetupStatus() {
  try {
    const result = await api("/setup/status", {}, false);
    state.setup = result;
    if (!result.setup_required) return false;
    showSetup();
    const form = $("#setup-form");
    if (!form.elements.host.value) form.elements.host.value = result.default_host || "127.0.0.1";
    form.elements.port.value = String(result.default_port || 3306);
    if (!form.elements.username.value) form.elements.username.value = result.default_username || "fitness";
    return true;
  } catch (error) {
    showSetup();
    $("#setup-form").classList.add("hidden");
    $("#setup-error").textContent = "无法读取后端初始化状态，请确认服务已正常启动。";
    setConnection(false, true);
    return true;
  }
}

async function initializeDatabase(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const submit = $("#setup-submit");
  const data = Object.fromEntries(new FormData(form));
  $("#setup-error").textContent = "";
  submit.disabled = true;
  submit.textContent = "正在检查并初始化…";
  try {
    const result = await api("/setup/database", {
      method: "POST",
      body: JSON.stringify({
        host: data.host,
        port: Number(data.port),
        username: data.username,
        password: data.password,
        setup_token: data.setup_token,
      }),
    }, false);
    form.reset();
    form.classList.add("hidden");
    $("#setup-result").classList.remove("hidden");
    $("#setup-result-summary").textContent = result.database_created
      ? "已创建固定数据库 fitness，并完成全部初始化。"
      : `已识别现有数据库 fitness（初始化前 ${result.existing_table_count} 张表），并完成兼容升级。`;
    $("#setup-mysql-version").textContent = result.mysql_version || "—";
    $("#setup-collation").textContent = result.database_collation || "—";
    $("#setup-table-count").textContent = `${result.table_count} 张`;
    $("#setup-revision").textContent = result.alembic_revision || "—";
    $("#setup-seed-status").textContent = result.seed_status === "created" ? "已写入" : "已存在";
    setConnection(true);
  } catch (error) {
    form.elements.password.value = "";
    form.elements.setup_token.value = "";
    $("#setup-error").textContent = setupErrorMessage(error);
    setConnection(false, true);
  } finally {
    submit.disabled = false;
    submit.textContent = "检查并初始化数据库";
  }
}

async function loadSession() {
  const me = await api("/me");
  state.me = me;
  showDashboard();
  $("#role-label").textContent = isAdmin() ? "ADMIN WORKSPACE" : "CLOUD WORKSPACE";
  $("#welcome-title").textContent = `欢迎回来，${me.display_name || me.username || me.email}`;
  $("#welcome-copy").textContent = `${me.email} · ${isAdmin() ? "管理员" : "普通用户"}`;
  $("#metric-role").textContent = isAdmin() ? "管理员" : "普通用户";
  $("#metric-email").textContent = me.email;
  setConnection(true);
  await loadOverview();
  if (isAdmin()) await Promise.all([loadAdminUsers(), loadAdminPlans(), loadRegistrationSetting()]);
}

async function loadOverview() {
  try {
    const bootstrap = await api("/bootstrap");
    state.bootstrap = bootstrap;
    const plan = bootstrap.current_plan || bootstrap.plan_version;
    $("#metric-plan").textContent = plan?.plan_name || "暂无计划";
    $("#metric-plan-version").textContent = plan ? `版本 ${plan.version_number ?? "—"} · ${plan.status || "—"}` : "请联系管理员分配计划";
    $("#metric-workouts").textContent = String((bootstrap.workout_sessions || []).length);
    $("#metric-sync").textContent = "云端已更新";
    $("#plan-status").textContent = plan?.status || "暂无";
    $("#plan-summary").classList.toggle("empty-state", !plan);
    $("#plan-summary").innerHTML = plan ? renderPlanSummary(plan) : "暂无已分配的训练计划";
    const workouts = (bootstrap.workout_sessions || []).slice(0, 8);
    $("#recent-workouts").classList.toggle("empty-state", workouts.length === 0);
    $("#recent-workouts").innerHTML = workouts.length ? workouts.map(renderWorkout).join("") : "暂无训练记录";
  } catch (error) {
    if (error.status === 401) return;
    showToast(errorMessage(error), true);
  }
}

function renderPlanSummary(plan) {
  const days = (plan.days || []).map((day) => `${escapeHtml(day.name || day.code)}（${(day.slots || []).length} 个位置）`).join(" · ");
  return `<strong>${escapeHtml(plan.plan_name || "未命名计划")}</strong><span>版本 ${escapeHtml(plan.version_number ?? "—")} · 每周 ${escapeHtml(plan.weekly_frequency ?? "—")} 次</span><p>${days || "暂无训练日"}</p>`;
}

function renderWorkout(workout) {
  const label = workout.local_date || workout.localDate || workout.started_at || workout.startedAt || "训练记录";
  const status = workout.status || "—";
  return `<div class="list-item"><div><strong>${escapeHtml(label)}</strong><small>${escapeHtml(workout.plan_day_code || workout.planDayCode || "自定义训练")}</small></div><span class="badge">${escapeHtml(status)}</span></div>`;
}

async function loadAdminUsers() {
  const query = $("#user-search").value.trim();
  try {
    const result = await api(`/admin/users?limit=200${query ? `&query=${encodeURIComponent(query)}` : ""}`);
    state.users = result.items || [];
    renderUserTable();
    $("#admin-user-count").textContent = `账号 ${state.users.length}`;
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

function renderUserTable() {
  const target = $("#user-table");
  if (!state.users.length) {
    target.innerHTML = `<p class="empty-state">没有匹配的账号。</p>`;
    return;
  }
  target.innerHTML = `<table><thead><tr><th>账号</th><th>角色</th><th>状态</th><th>创建时间</th></tr></thead><tbody>${state.users.map((user) => `<tr data-user-id="${escapeHtml(user.id)}"><td><strong>${escapeHtml(user.display_name)}</strong><small>${escapeHtml(user.email)} · @${escapeHtml(user.username)}</small></td><td>${escapeHtml((user.roles || []).join(", ") || "user")}</td><td><span class="badge ${user.is_active ? "" : "off"}">${user.is_active ? "启用" : "停用"}</span></td><td>${escapeHtml(formatDate(user.created_at))}</td></tr>`).join("")}</tbody></table>`;
  $$("[data-user-id]", target).forEach((row) => row.addEventListener("click", () => selectUser(row.dataset.userId)));
}

async function selectUser(userId) {
  try {
    const overview = await api(`/admin/users/${encodeURIComponent(userId)}/overview`);
    state.selectedUser = overview;
    const user = overview.user;
    $("#selected-user-title").textContent = user.display_name;
    $("#selected-user-content").innerHTML = `<p><strong>${escapeHtml(user.email)}</strong> · @${escapeHtml(user.username)}</p><p class="muted">计划分配 ${overview.assignments?.length || 0} 条 · 训练记录 ${overview.workout_sessions?.length || 0} 条 · 准备度 ${overview.readiness?.length || 0} 条 · 有氧 ${overview.cardio_sessions?.length || 0} 条</p><div class="list-stack">${(overview.plans || []).slice(0, 5).map((plan) => `<div class="list-item"><div><strong>${escapeHtml(plan.plan_name)}</strong><small>v${escapeHtml(plan.version_number)} · ${escapeHtml(plan.status)}</small></div><span>${escapeHtml(formatDate(plan.published_at))}</span></div>`).join("") || "<p class=\"empty-state\">暂无计划版本</p>"}</div>`;
    const form = $("#edit-user-form");
    form.classList.remove("hidden");
    form.elements.id.value = user.id;
    form.elements.expected_version.value = user.version;
    form.elements.display_name.value = user.display_name;
    form.elements.timezone.value = user.timezone;
    form.elements.password.value = "";
    form.elements.is_active.checked = user.is_active;
    form.elements.roles.value = (user.roles || []).includes("admin") ? "admin" : "user";
    $$(".superuser-only", form).forEach((element) => element.classList.toggle("hidden", !isSuperuser()));
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function createUser(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const data = Object.fromEntries(new FormData(form));
  try {
    const roles = data.roles === "admin" ? ["admin"] : ["user"];
    await api("/admin/users", { method: "POST", body: JSON.stringify({ ...data, roles }) });
    form.reset();
    $("#create-user-form").classList.add("hidden");
    showToast("账号已创建");
    await loadAdminUsers();
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function saveUser(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const data = Object.fromEntries(new FormData(form));
  const payload = {
    expected_version: Number(data.expected_version),
    display_name: data.display_name,
    timezone: data.timezone,
    is_active: form.elements.is_active.checked,
    roles: [data.roles === "admin" ? "admin" : "user"],
  };
  if (data.password) payload.password = data.password;
  try {
    await api(`/admin/users/${encodeURIComponent(data.id)}`, { method: "PATCH", body: JSON.stringify(payload) });
    showToast("账号设置已保存");
    await Promise.all([loadAdminUsers(), selectUser(data.id)]);
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function loadRegistrationSetting() {
  try {
    const result = await api("/admin/settings/registration");
    state.registrationEnabled = Boolean(result.enabled);
    $("#registration-toggle").checked = state.registrationEnabled;
    $("#registration-badge").textContent = state.registrationEnabled ? "已开启" : "已关闭";
    $("#registration-badge").classList.toggle("off", !state.registrationEnabled);
    $("#admin-registration-state").textContent = state.registrationEnabled ? "公开注册已开启" : "公开注册已关闭";
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function updateRegistrationSetting(event) {
  try {
    const result = await api("/admin/settings/registration", { method: "PATCH", body: JSON.stringify({ enabled: event.target.checked }) });
    state.registrationEnabled = Boolean(result.enabled);
    $("#registration-badge").textContent = state.registrationEnabled ? "已开启" : "已关闭";
    $("#registration-badge").classList.toggle("off", !state.registrationEnabled);
    $("#admin-registration-state").textContent = state.registrationEnabled ? "公开注册已开启" : "公开注册已关闭";
    $("#settings-updated").textContent = `最近更新：${formatDate(result.updated_at)}`;
    showToast("注册设置已更新");
  } catch (error) {
    event.target.checked = state.registrationEnabled;
    showToast(errorMessage(error), true);
  }
}

async function loadAdminPlans() {
  try {
    const result = await api("/admin/plans?limit=200");
    state.plans = result.items || [];
    renderPlanTable();
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

function renderPlanTable() {
  const target = $("#plan-table");
  if (!state.plans.length) {
    target.innerHTML = `<p class="empty-state">暂无计划版本。</p>`;
    return;
  }
  target.innerHTML = `<table><thead><tr><th>计划</th><th>版本</th><th>状态</th><th>更新</th></tr></thead><tbody>${state.plans.map((plan) => `<tr data-plan-id="${escapeHtml(plan.id)}"><td><strong>${escapeHtml(plan.plan_name)}</strong><small>每周 ${escapeHtml(plan.weekly_frequency)} 次</small></td><td>v${escapeHtml(plan.version_number)}</td><td><span class="badge ${plan.status === "published" ? "" : "warn"}">${escapeHtml(plan.status)}</span></td><td>${escapeHtml(formatDate(plan.updated_at))}</td></tr>`).join("")}</tbody></table>`;
  $$("[data-plan-id]", target).forEach((row) => row.addEventListener("click", () => selectPlan(row.dataset.planId)));
}

async function selectPlan(planId) {
  try {
    const detail = await api(`/admin/plan-versions/${encodeURIComponent(planId)}`);
    state.selectedPlan = detail;
    $("#selected-plan-title").textContent = `${detail.plan_name || "计划"} · v${detail.version_number || "—"}`;
    $("#selected-plan-status").textContent = detail.status || "—";
    $("#selected-plan-status").classList.toggle("warn", detail.status !== "published");
    $("#plan-editor").value = JSON.stringify(detail, null, 2);
    $("#plan-editor-error").textContent = "";
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

function editorValue() {
  try {
    const parsed = JSON.parse($("#plan-editor").value);
    if (!parsed || typeof parsed !== "object") throw new Error("JSON 顶层必须是对象");
    return parsed;
  } catch (error) {
    $("#plan-editor-error").textContent = `JSON 无效：${error.message}`;
    throw error;
  }
}

function toVersionPayload(detail, changelogSuffix = "") {
  const days = (detail.days || []).map((day, dayIndex) => ({
    day_code: day.day_code || day.code || `DAY${dayIndex + 1}`,
    name: day.name || day.body_part || "",
    sort_order: day.sort_order ?? dayIndex,
    notes: day.notes || day.cues || "",
    slots: (day.slots || day.items || []).map((slot, slotIndex) => ({
      name: slot.name || slot.body_part || `位置 ${slotIndex + 1}`,
      sort_order: slot.sort_order ?? slot.position ?? slotIndex,
      notes: slot.notes || slot.cues || "",
      selection_rule_json: slot.selection_rule_json || null,
      options: (slot.options || []).map((option, optionIndex) => ({
        exercise_id: option.exercise_id,
        is_preferred: Boolean(option.is_preferred),
        sort_order: option.sort_order ?? optionIndex,
        set_count: option.set_count ?? option.intro_set_count ?? 1,
        reps_min: option.reps_min ?? option.rep_min ?? 0,
        reps_max: option.reps_max ?? option.rep_max ?? 0,
        duration_seconds_min: option.duration_seconds_min ?? null,
        duration_seconds_max: option.duration_seconds_max ?? null,
        rir_min: option.rir_min ?? 2,
        rir_max: option.rir_max ?? 3,
        is_per_side: Boolean(option.is_per_side),
        prescription_json: option.prescription_json || (option.prescription_text ? { text: option.prescription_text } : null),
      })),
    })),
  }));
  return {
    plan_id: detail.plan_id,
    weekly_frequency: detail.weekly_frequency ?? 3,
    min_rest_days: detail.min_rest_days ?? 1,
    fatigue_threshold: detail.fatigue_threshold ?? 8,
    initial_reduced_weeks: detail.initial_reduced_weeks ?? detail.intro_weeks ?? 2,
    initial_set_count: detail.initial_set_count ?? detail.intro_max_sets ?? 2,
    config_json: detail.config_json ?? detail.rules ?? {},
    changelog: `${detail.changelog || ""}${changelogSuffix}`.trim(),
    days,
  };
}

async function createPlanDraft() {
  if (!state.selectedPlan) return showToast("请先选择一个计划版本", true);
  try {
    const detail = editorValue();
    const payload = toVersionPayload({ ...detail, plan_id: state.selectedPlan.plan_id }, "\nWeb 创建草稿");
    const created = await api(`/admin/plans/${encodeURIComponent(state.selectedPlan.plan_id)}/versions`, { method: "POST", body: JSON.stringify(payload) });
    await selectPlan(created.id);
    await loadAdminPlans();
    showToast("草稿已创建，现在可以编辑并保存");
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function savePlanDraft() {
  if (!state.selectedPlan) return showToast("请先选择一个计划版本", true);
  try {
    const detail = editorValue();
    if (state.selectedPlan.status === "published") throw new Error("已发布版本不可修改，请先创建草稿");
    const payload = toVersionPayload({ ...detail, plan_id: state.selectedPlan.plan_id });
    payload.expected_version = state.selectedPlan.version;
    const saved = await api(`/admin/plan-versions/${encodeURIComponent(state.selectedPlan.id)}`, { method: "PATCH", body: JSON.stringify(payload) });
    await selectPlan(saved.id);
    await loadAdminPlans();
    showToast("草稿已保存");
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function publishPlan() {
  if (!state.selectedPlan) return showToast("请先选择一个计划版本", true);
  try {
    const detail = editorValue();
    if (state.selectedPlan.status === "published") throw new Error("该版本已经发布");
    const result = await api(`/admin/plan-versions/${encodeURIComponent(state.selectedPlan.id)}/publish`, { method: "POST", body: JSON.stringify({ expected_version: state.selectedPlan.version }) });
    await selectPlan(result.id);
    await loadAdminPlans();
    showToast("计划版本已发布");
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

async function assignSelectedPlan() {
  if (!state.selectedUser) return showToast("请先在账号列表中选择用户", true);
  if (!state.selectedPlan || state.selectedPlan.status !== "published") return showToast("请先选择已发布的计划版本", true);
  try {
    await api("/admin/assignments", { method: "POST", body: JSON.stringify({ user_id: state.selectedUser.user.id, plan_version_id: state.selectedPlan.id, starts_on: todayIso(), status: "active" }) });
    await selectUser(state.selectedUser.user.id);
    showToast("计划已分配给用户");
  } catch (error) {
    showToast(errorMessage(error), true);
  }
}

function wireEvents() {
  $("#setup-form").addEventListener("submit", initializeDatabase);
  $("#setup-continue").addEventListener("click", async () => {
    showAuth();
    switchAuthTab("login");
    await loadRegistrationStatus();
  });
  $$("[data-auth-tab]").forEach((button) => button.addEventListener("click", () => switchAuthTab(button.dataset.authTab)));
  $("#login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(event.currentTarget));
    setAuthError();
    try {
      setTokens(await api("/auth/login", { method: "POST", body: JSON.stringify({ email: data.email, password: data.password, device_name: "Web" }) }, false));
      event.currentTarget.reset();
      await loadSession();
    } catch (error) {
      setAuthError(errorMessage(error));
      setConnection(false, true);
    }
  });
  $("#register-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!state.registrationEnabled) return setAuthError("当前已关闭公开注册，请联系管理员。");
    const data = Object.fromEntries(new FormData(event.currentTarget));
    try {
      setTokens(await api("/auth/register", { method: "POST", body: JSON.stringify(data) }, false));
      event.currentTarget.reset();
      await loadSession();
    } catch (error) {
      setAuthError(errorMessage(error));
      setConnection(false, true);
    }
  });
  $("#logout-button").addEventListener("click", async () => {
    try { if (state.accessToken) await api("/auth/logout", { method: "POST", body: JSON.stringify({ refresh_token: state.refreshToken }) }); } catch { /* token may already be expired */ }
    clearSession();
    showToast("已退出登录");
  });
  $("#refresh-button").addEventListener("click", async () => { await loadOverview(); if (isAdmin()) await Promise.all([loadAdminUsers(), loadAdminPlans(), loadRegistrationSetting()]); showToast("数据已刷新"); });
  $$("[data-panel]").forEach((button) => button.addEventListener("click", () => {
    $$("[data-panel]").forEach((item) => item.classList.toggle("active", item === button));
    $$("[data-panel-view]").forEach((panel) => panel.classList.toggle("hidden", panel.dataset.panelView !== button.dataset.panel));
  }));
  $("#reload-users").addEventListener("click", loadAdminUsers);
  $("#user-search").addEventListener("input", () => { window.clearTimeout(loadAdminUsers.timer); loadAdminUsers.timer = window.setTimeout(loadAdminUsers, 220); });
  $("#create-user-toggle").addEventListener("click", () => $("#create-user-form").classList.toggle("hidden"));
  $("#cancel-create-user").addEventListener("click", () => $("#create-user-form").classList.add("hidden"));
  $("#create-user-form").addEventListener("submit", createUser);
  $("#edit-user-form").addEventListener("submit", saveUser);
  $("#assign-selected-plan").addEventListener("click", assignSelectedPlan);
  $("#registration-toggle").addEventListener("change", updateRegistrationSetting);
  $("#reload-plans").addEventListener("click", loadAdminPlans);
  $("#draft-plan").addEventListener("click", createPlanDraft);
  $("#save-plan").addEventListener("click", savePlanDraft);
  $("#publish-plan").addEventListener("click", publishPlan);
}

async function start() {
  wireEvents();
  if (await loadSetupStatus()) return;
  showAuth();
  if (!state.accessToken) {
    switchAuthTab("login");
    await loadRegistrationStatus();
    return;
  }
  try {
    await loadSession();
  } catch {
    clearSession();
    await loadRegistrationStatus();
  }
}

start();
