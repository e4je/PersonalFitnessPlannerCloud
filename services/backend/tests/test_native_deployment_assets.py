from __future__ import annotations

from pathlib import Path


BACKEND_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = BACKEND_ROOT.parents[1]


def _read_repository_file(relative_path: str) -> str:
    return (REPOSITORY_ROOT / relative_path).read_text(encoding="utf-8")


def test_ubuntu_native_deployment_is_loopback_only_and_hardened() -> None:
    installer = _read_repository_file("scripts/deploy-backend-ubuntu-native.sh")

    assert "--require-hashes" in installer
    assert "--no-deps" in installer
    assert "--host 127.0.0.1 --port 8000 --workers 1" in installer
    assert "User=$SERVICE_USER" in installer
    assert "NoNewPrivileges=true" in installer
    assert "ProtectSystem=strict" in installer
    assert "ReadWritePaths=$DATA_DIR" in installer
    assert "RUNTIME_CONFIG_PATH=$DATA_DIR/backend-config.json" in installer
    assert "DATABASE_BACKEND=sqlite" in installer
    assert "SQLITE_DATABASE_PATH=$DATA_DIR/fitness.db" in installer
    assert "fitness.db" in installer
    assert "jwt-secret" in installer
    assert "ExecStartPre=$VENV_DIR/bin/python -m alembic upgrade head" in installer
    assert "ExecStartPre=$VENV_DIR/bin/python -m scripts.seed_default_plan" in installer
    assert "--host 0.0.0.0" not in installer


def test_windows_native_deployment_uses_a_restricted_startup_task() -> None:
    installer = _read_repository_file("scripts/deploy-backend-windows.ps1")
    runner = _read_repository_file("scripts/run-backend-windows-service.ps1")

    assert "--require-hashes" in installer
    assert "--no-deps" in installer
    assert "print(sys.version_info[0], sys.version_info[1], sep=chr(46))" in installer
    assert 'print(".".join' not in installer
    assert "-UserId 'S-1-5-19'" in installer
    assert "*S-1-5-19:(OI)(CI)RX" in installer
    assert "*S-1-5-19:(OI)(CI)M" in installer
    assert "@($dataPath, $logsPath)" in installer
    assert "$installerFullControl" in installer
    assert "if (-not (Test-Path -LiteralPath $installMarkerPath -PathType Leaf))" in installer
    assert "Assert-BackendPortAvailable -BackendPort $Port" in installer
    assert "Get-NetTCPConnection -State Listen" in installer
    assert "[IO.FileAttributes]::ReparsePoint" in installer
    assert "takeown.exe /F $ManagedRoot /A /R /D Y /SKIPSL" in installer
    assert "$childrenPattern /reset /T /Q" in installer
    assert "后端在 90 秒内没有通过 liveness 检查" in installer
    assert "Write-BackendTaskDiagnostics" in installer
    assert "-UserId 'S-1-5-18'" not in installer
    assert "127.0.0.1" in runner
    assert "--workers', '1'" in runner
    assert "RUNTIME_CONFIG_PATH" in runner
    assert "DATABASE_BACKEND" in runner
    assert "SQLITE_DATABASE_PATH" in runner
    assert "backend-config.json" in installer
    assert "fitness.db" in installer
    assert "jwt-secret" in installer
    assert "-m alembic upgrade head" in runner
    assert "-m scripts.seed_default_plan" in runner
    assert "0.0.0.0" not in runner


def test_first_run_web_hint_is_deployment_method_neutral() -> None:
    page = (BACKEND_ROOT / "app" / "web" / "index.html").read_text(encoding="utf-8")

    assert "从部署脚本的完成信息或后端服务日志中复制" in page
    assert "docker compose logs backend" not in page
