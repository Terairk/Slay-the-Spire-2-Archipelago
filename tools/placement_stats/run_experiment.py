#!/usr/bin/env python3
"""Prepare and run the fixed-config Slay the Spire II placement experiment."""

from __future__ import annotations

import argparse
import hashlib
import os
import shlex
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Sequence


SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent

FUZZER_REVISION = "ebe01d5523f04a2a0a1de5eb7229d10ef12b8fc2"
FUZZER_URL = (
    "https://raw.githubusercontent.com/ionium-ap/Archipelago-fuzzer/"
    f"{FUZZER_REVISION}/fuzz.py"
)
FUZZER_SHA256 = "4f65c12813b06e19046d9aa2397083cb14977592acee1cf8d40544307e405694"


class RunnerError(RuntimeError):
    """A user-actionable experiment setup error."""


def _positive_integer(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return parsed


def _non_negative_integer(value: str) -> int:
    parsed = int(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("must be a non-negative integer")
    return parsed


def _utc_timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _build_parser() -> argparse.ArgumentParser:
    default_label = f"sts2-v053-{_utc_timestamp()}"
    parser = argparse.ArgumentParser(
        description=(
            "Prepare the APWorld and run the fixed-config placement experiment. "
            "No environment variables or manual file copies are required."
        ),
    )
    parser.add_argument("--runs", type=_positive_integer, default=10_000,
                        help="generations to run (default: 10000)")
    parser.add_argument("--jobs", type=_positive_integer, default=8,
                        help="parallel workers (default: 8)")
    parser.add_argument("--timeout", type=_non_negative_integer, default=60,
                        help="per-generation timeout in seconds; 0 disables it (default: 60)")
    parser.add_argument("--output", type=Path,
                        help="dataset directory (default: artifacts/placement_stats/LABEL)")
    parser.add_argument("--label", default=default_label,
                        help="dataset label stored in every row (default: timestamped)")
    parser.add_argument("--yaml-dir", type=Path, default=SCRIPT_DIR / "sample-yamls",
                        help="fixed sample YAML directory (default: tools/placement_stats/sample-yamls)")
    parser.add_argument("--archipelago-root", type=Path, default=REPO_ROOT.parent / "Archipelago",
                        help="source Archipelago checkout (default: sibling ../Archipelago)")
    parser.add_argument("--python", dest="python_command",
                        help="existing Archipelago virtualenv Python executable")
    parser.add_argument("--fuzzer", type=Path,
                        help="fuzz.py to install in the Archipelago checkout")
    parser.add_argument("--skip-prepare", action="store_true",
                        help="skip APWorld sync/build and fuzzer installation")
    return parser


def _run(command: Sequence[str | os.PathLike[str]], *, cwd: Path | None = None,
         env: dict[str, str] | None = None) -> None:
    sys.stdout.flush()
    sys.stderr.flush()
    subprocess.run([str(part) for part in command], cwd=cwd, env=env, check=True)


def _resolve_command(command: str) -> Path:
    candidate = Path(command).expanduser()
    if candidate.parent != Path(".") or candidate.exists():
        return candidate.resolve()

    resolved = shutil.which(command)
    if resolved is None:
        raise RunnerError(f"executable not found: {command}")
    return Path(resolved).resolve()


def _venv_python(archipelago_root: Path) -> Path | None:
    candidates = (
        archipelago_root / ".venv" / "Scripts" / "python.exe",
        archipelago_root / ".venv" / "bin" / "python",
    )
    return next((candidate for candidate in candidates if candidate.is_file()), None)


def _find_python(archipelago_root: Path, explicit: str | None, *, allow_create: bool) -> Path:
    if explicit:
        python = _resolve_command(explicit)
    else:
        python = _venv_python(archipelago_root)
        if python is None:
            if not allow_create:
                raise RunnerError(
                    f"Archipelago virtualenv not found under {archipelago_root / '.venv'}; "
                    "rerun without --skip-prepare or pass --python"
                )

            uv = shutil.which("uv")
            if uv is None:
                raise RunnerError(
                    "Archipelago/.venv does not exist and uv is unavailable; "
                    "install uv or pass --python"
                )

            base_python = next(
                (path for name in ("python3.13", "python3.12", "python3.11")
                 if (path := shutil.which(name))),
                "3.13",
            )
            print(f"Creating isolated Archipelago environment at {archipelago_root / '.venv'}")
            _run((uv, "venv", "--seed", "--python", base_python, archipelago_root / ".venv"))
            python = _venv_python(archipelago_root)

    if python is None or not python.is_file():
        raise RunnerError(f"Python executable is not available: {python}")

    version_check = (
        "import sys; "
        "raise SystemExit(0 if (3, 11) <= sys.version_info[:2] < (3, 14) else 1)"
    )
    try:
        _run((python, "-c", version_check))
    except subprocess.CalledProcessError as error:
        raise RunnerError(f"Archipelago requires Python 3.11-3.13: {python}") from error
    return python


def _file_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as input_file:
        for chunk in iter(lambda: input_file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _tree_fingerprint(root: Path) -> dict[str, tuple[str, str]]:
    """Describe a tree while ignoring Python caches created by imports."""
    fingerprint: dict[str, tuple[str, str]] = {}
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root)
        if "__pycache__" in relative.parts or path.suffix == ".pyc":
            continue
        key = relative.as_posix()
        if path.is_symlink():
            fingerprint[key] = ("symlink", os.readlink(path))
        elif path.is_dir():
            fingerprint[key] = ("directory", "")
        elif path.is_file():
            fingerprint[key] = ("file", _file_digest(path))
    return fingerprint


def _remove_path(path: Path) -> None:
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.exists():
        shutil.rmtree(path)


def _sync_world(local_world: Path, archipelago_root: Path) -> Path | None:
    worlds_dir = archipelago_root / "worlds"
    target_world = worlds_dir / "spire2"
    staging = worlds_dir / f".spire2.sync.{os.getpid()}"
    if staging.exists():
        raise RunnerError(f"temporary sync path already exists: {staging}")

    print(f"Syncing world/spire2 into {archipelago_root}")
    shutil.copytree(local_world, staging)
    moved_target: Path | None = None
    keep_moved_target = False

    try:
        if target_world.exists() or target_world.is_symlink():
            target_matches = _tree_fingerprint(local_world) == _tree_fingerprint(target_world)
            if target_matches:
                moved_target = worlds_dir / f".spire2.previous.{os.getpid()}"
            else:
                moved_target = worlds_dir / f".spire2.backup-{_utc_timestamp()}-{os.getpid()}"
                keep_moved_target = True
            if moved_target.exists():
                raise RunnerError(f"world preservation path already exists: {moved_target}")
            target_world.rename(moved_target)

        try:
            staging.rename(target_world)
        except BaseException:
            if moved_target is not None and not target_world.exists():
                moved_target.rename(target_world)
            raise

        if moved_target is not None:
            if keep_moved_target:
                print(f"Preserved differing target world at {moved_target}")
                return moved_target
            _remove_path(moved_target)
        return None
    finally:
        if staging.exists():
            _remove_path(staging)


def _prepare_apworld(archipelago_root: Path, python: Path) -> Path:
    local_world = REPO_ROOT / "world" / "spire2"
    launcher = archipelago_root / "Launcher.py"
    module_update = archipelago_root / "ModuleUpdate.py"
    worlds_dir = archipelago_root / "worlds"
    for required in (local_world, worlds_dir):
        if not required.is_dir():
            raise RunnerError(f"required directory not found: {required}")
    for required in (launcher, module_update):
        if not required.is_file():
            raise RunnerError(f"required file not found: {required}")

    _sync_world(local_world, archipelago_root)

    print(f"Checking Archipelago requirements inside {python}")
    _run((python, module_update, "--yes"), cwd=archipelago_root)

    # World-specific requirements can have broader transitive ranges than the
    # core checkout. Reapply core requirements without dependency resolution
    # so a later world install cannot loosen an exact core pin (notably the
    # typing_extensions pin in Archipelago 0.6.7).
    core_requirements = archipelago_root / "requirements.txt"
    if core_requirements.is_file():
        print(f"Reapplying Archipelago core requirement pins from {core_requirements}")
        _run(
            (python, "-m", "pip", "install", "--disable-pip-version-check", "--no-deps",
             "-r", core_requirements),
            cwd=archipelago_root,
        )

    print(f"Building Slay the Spire II APWorld with {python}")
    build_environment = os.environ.copy()
    build_environment["SKIP_REQUIREMENTS_UPDATE"] = "1"

    # Archipelago 0.6.8 added this component-specific option. The 0.6.7
    # builder accepts only world names and always opens its output directory.
    launcher_components = archipelago_root / "worlds" / "LauncherComponents.py"
    supports_skip_open_folder = (
        launcher_components.is_file()
        and "--skip_open_folder" in launcher_components.read_text(encoding="utf-8")
    )
    build_command: list[str | os.PathLike[str]] = [
        python,
        launcher,
        "Build APWorlds",
        "--",
        "Slay the Spire II",
    ]
    if supports_skip_open_folder:
        build_command.append("--skip_open_folder")
    _run(
        build_command,
        cwd=archipelago_root,
        env=build_environment,
    )

    built_apworld = archipelago_root / "build" / "apworlds" / "spire2.apworld"
    if not built_apworld.is_file():
        raise RunnerError(f"build succeeded but output was not found: {built_apworld}")

    destination = REPO_ROOT / "dist" / "spire2.apworld"
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(built_apworld, destination)
    print(f"Built {destination}")
    return destination


def _install_fuzzer(archipelago_root: Path, explicit_source: Path | None) -> Path:
    target = archipelago_root / "fuzz.py"
    if explicit_source is not None:
        source = explicit_source.expanduser().resolve()
        if not source.is_file():
            raise RunnerError(f"fuzzer not found: {source}")
        if source != target.resolve():
            shutil.copy2(source, target)
        print(f"Installed fuzzer from {source}")
        return target

    if target.is_file():
        print(f"Using existing fuzzer: {target}")
        return target

    sibling_fuzzer = REPO_ROOT.parent / "Archipelago-fuzzer" / "fuzz.py"
    if sibling_fuzzer.is_file():
        shutil.copy2(sibling_fuzzer, target)
        print(f"Installed fuzzer from sibling checkout: {sibling_fuzzer}")
        return target

    temporary = archipelago_root / f".fuzz.py.download.{os.getpid()}"
    print(f"Downloading pinned Archipelago-fuzzer {FUZZER_REVISION}")
    try:
        request = urllib.request.Request(FUZZER_URL, headers={"User-Agent": "sts2-placement-runner"})
        with urllib.request.urlopen(request) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        if _file_digest(temporary) != FUZZER_SHA256:
            raise RunnerError("downloaded fuzz.py failed SHA-256 verification")
        temporary.replace(target)
    except (OSError, urllib.error.URLError) as error:
        raise RunnerError(
            "failed to download fuzz.py; pass --fuzzer /path/to/fuzz.py to use a local copy"
        ) from error
    finally:
        temporary.unlink(missing_ok=True)

    print(f"Installed pinned fuzzer: {target}")
    return target


def _display_command(command: Sequence[str | os.PathLike[str]]) -> str:
    parts = [str(part) for part in command]
    return subprocess.list2cmdline(parts) if os.name == "nt" else shlex.join(parts)


def _run_experiment(args: argparse.Namespace) -> None:
    if not args.label:
        raise RunnerError("--label cannot be empty")

    archipelago_root = args.archipelago_root.expanduser().resolve()
    yaml_dir = args.yaml_dir.expanduser().resolve()
    if not archipelago_root.is_dir():
        raise RunnerError(f"Archipelago checkout not found: {archipelago_root}")
    if not yaml_dir.is_dir():
        raise RunnerError(f"sample YAML directory not found: {yaml_dir}")
    if not any(yaml_dir.glob("*.yaml")):
        raise RunnerError(f"no .yaml files found in: {yaml_dir}")

    python = _find_python(
        archipelago_root,
        args.python_command,
        allow_create=not args.skip_prepare,
    )
    if not args.skip_prepare:
        _prepare_apworld(archipelago_root, python)
        _install_fuzzer(archipelago_root, args.fuzzer)

    fuzzer = archipelago_root / "fuzz.py"
    if not fuzzer.is_file():
        raise RunnerError(
            f"fuzzer not found at {fuzzer}; rerun without --skip-prepare or pass --fuzzer"
        )

    output_dir = (
        args.output.expanduser().resolve()
        if args.output is not None
        else REPO_ROOT / "artifacts" / "placement_stats" / args.label
    )
    output_dir.mkdir(parents=True, exist_ok=True)

    print(f"\nRunning {args.runs} generations with {args.jobs} workers")
    print(f"YAMLs:  {yaml_dir}")
    print(f"Output: {output_dir}")
    print(f"Label:  {args.label}\n")

    environment = os.environ.copy()
    environment["AP_PLACEMENT_STATS_DIR"] = str(output_dir)
    environment["AP_PLACEMENT_STATS_LABEL"] = args.label
    environment["PYTHONPATH"] = os.pathsep.join(
        part for part in (str(SCRIPT_DIR), environment.get("PYTHONPATH")) if part
    )
    _run(
        (
            python,
            "-O",
            fuzzer,
            "-r", str(args.runs),
            "-j", str(args.jobs),
            "-n", "1",
            "-t", str(args.timeout),
            "--sample-from", yaml_dir,
            "--skip-output",
            "--hook", "placement_stats_hook:Hook",
        ),
        cwd=archipelago_root,
        env=environment,
    )

    analysis_command = (
        "uv", "run", "--with", "duckdb", "python", SCRIPT_DIR / "analyze.py",
        "item-nth-sphere", output_dir, "--item", "Ironclad Relic", "--count", "1",
    )
    print("\nExperiment complete. Analyze it with:")
    print(f"  {_display_command(analysis_command)}")


def main(argv: Sequence[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        _run_experiment(args)
    except (RunnerError, OSError, subprocess.CalledProcessError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
