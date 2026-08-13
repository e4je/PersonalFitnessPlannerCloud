from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path


SKIP_DIRECTORIES = {
    ".git",
    ".gradle",
    ".gradle-user-home",
    ".kotlin",
    ".packages",
    ".venv",
    "__pycache__",
    "artifacts",
    "bin",
    "build",
    "obj",
    "TestResults",
}

SECRET_FILE_SUFFIXES = {".jks", ".keystore", ".p12", ".pfx"}
FORBIDDEN_TEXT = {
    "-----BEGIN PRIVATE KEY-----": "private key",
    "-----BEGIN RSA PRIVATE KEY-----": "RSA private key",
    "-----BEGIN EC PRIVATE KEY-----": "EC private key",
    "development-only-change-me-32-characters": "known default JWT secret",
    "fitness-local-password": "known default MySQL password",
    "root-local-password": "known default MySQL root password",
    "mysql+pymysql://fitness:fitness@": "embedded MySQL credentials",
}


def is_skipped(path: Path) -> bool:
    return any(part in SKIP_DIRECTORIES for part in path.parts)


def candidate_files(root: Path) -> list[Path]:
    """Return tracked and non-ignored untracked files when Git is available.

    A developer's ignored ``.env`` is expected to contain real credentials and
    must not make the repository scanner unusable. Non-ignored new files remain
    in scope so this still works as a useful pre-commit gate.
    """
    try:
        completed = subprocess.run(
            [
                "git",
                "-C",
                str(root),
                "ls-files",
                "-z",
                "--cached",
                "--others",
                "--exclude-standard",
            ],
            check=True,
            capture_output=True,
        )
    except (FileNotFoundError, subprocess.CalledProcessError):
        return [path for path in root.rglob("*") if path.is_file()]

    return [root / Path(raw.decode("utf-8")) for raw in completed.stdout.split(b"\0") if raw]


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    findings: list[str] = []
    for path in candidate_files(root):
        relative = path.relative_to(root)
        if is_skipped(relative) or not path.is_file():
            continue
        if relative == Path("scripts/scan-secrets.py"):
            continue
        if path.name == ".env" or (path.name.startswith(".env.") and path.name != ".env.example"):
            findings.append(f"secret environment file: {relative}")
            continue
        if path.suffix.casefold() in SECRET_FILE_SUFFIXES:
            findings.append(f"key/certificate container: {relative}")
            continue
        if path.stat().st_size > 2_000_000:
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        for needle, label in FORBIDDEN_TEXT.items():
            if needle in text:
                findings.append(f"{label}: {relative}")
        if re.search(r"(?i)authorization\s*[:=]\s*['\"]bearer\s+[A-Za-z0-9._-]{24,}", text):
            findings.append(f"hard-coded bearer token: {relative}")

    if findings:
        print("secret scan failed:", file=sys.stderr)
        for finding in sorted(set(findings)):
            print(f"- {finding}", file=sys.stderr)
        return 1
    print("secret scan passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
