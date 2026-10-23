"""CLI parsing, environment fallbacks and token redaction."""

from __future__ import annotations

import io
import logging
import subprocess
import sys
from collections.abc import Iterator
from pathlib import Path

import pytest

from clipforge_companion import cli
from clipforge_companion.config import Config, ConfigError
from clipforge_companion.logsetup import Redactor, setup_logging
from clipforge_companion.status import StatusReporter
from tests.fake_api import TOKEN, FakeLiveApi

ENV_VARS = (
    "CLIPFORGE_API_URL",
    "CLIPFORGE_DEVICE_TOKEN",
    "OBS_WEBSOCKET_URL",
    "OBS_WEBSOCKET_PASSWORD",
    "FFMPEG_PATH",
)


@pytest.fixture(autouse=True)
def clean_env(monkeypatch: pytest.MonkeyPatch) -> Iterator[None]:
    for name in ENV_VARS:
        monkeypatch.delenv(name, raising=False)
    yield
    # main() installs a root handler bound to the (captured) stderr: remove it afterwards.
    root = logging.getLogger()
    for handler in list(root.handlers):
        if getattr(handler, "_clipforge_companion_handler", False):
            root.removeHandler(handler)
    root.setLevel(logging.WARNING)


def config_from(argv: list[str]) -> Config:
    return cli.build_config(cli.build_parser().parse_args(argv))


def test_run_flags_obs_mode(tmp_path: Path) -> None:
    config = config_from(
        [
            "run", "--api", "https://api.example.com/", "--token", TOKEN,
            "--obs", "ws://192.168.0.10:4455", "--obs-password", "segredo", "--auto-record",
            "--segment-seconds", "45", "--title", "Live de sexta", "--work-dir", str(tmp_path),
            "--ffmpeg", "C:/ffmpeg/bin/ffmpeg.exe", "--max-upload-mbps", "20", "--keep-segments",
        ]
    )  # fmt: skip
    assert config.mode == "obs"
    assert config.api_url == "https://api.example.com"
    assert config.token == TOKEN
    assert config.obs_url == "ws://192.168.0.10:4455"
    assert config.obs_password == "segredo"
    assert config.auto_record and config.keep_segments
    assert config.segment_seconds == 45
    assert config.title == "Live de sexta"
    assert config.work_dir == tmp_path
    assert config.ffmpeg == "C:/ffmpeg/bin/ffmpeg.exe"
    assert config.max_upload_mbps == 20


def test_run_file_mode_defaults(tmp_path: Path) -> None:
    recording = tmp_path / "2026-10-23 20-00-00.mkv"
    config = config_from(
        ["run", "--api", "http://localhost:5080/api", "--token", TOKEN, "--file", str(recording)]
    )
    assert config.mode == "file"
    assert config.file == recording
    assert config.api_url == "http://localhost:5080"  # trailing /api removed
    assert config.segment_seconds == 60
    assert config.obs_url == "ws://127.0.0.1:4455"
    assert config.max_upload_mbps is None and not config.keep_segments


def test_environment_fallbacks_and_flag_precedence(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("CLIPFORGE_API_URL", "https://env.example.com")
    monkeypatch.setenv("CLIPFORGE_DEVICE_TOKEN", TOKEN)
    monkeypatch.setenv("OBS_WEBSOCKET_URL", "ws://10.0.0.2:4455")
    monkeypatch.setenv("OBS_WEBSOCKET_PASSWORD", "env-pass")
    monkeypatch.setenv("FFMPEG_PATH", "/opt/ffmpeg/ffmpeg")
    config = config_from(["run"])
    assert config.api_url == "https://env.example.com"
    assert config.token == TOKEN
    assert config.obs_url == "ws://10.0.0.2:4455"
    assert config.obs_password == "env-pass"
    assert config.ffmpeg == "/opt/ffmpeg/ffmpeg"
    flagged = config_from(
        ["run", "--api", "https://flag.example.com", "--obs", "ws://127.0.0.1:4460"]
    )
    assert flagged.api_url == "https://flag.example.com"
    assert flagged.obs_url == "ws://127.0.0.1:4460"


@pytest.mark.parametrize(
    ("argv", "message"),
    [
        (["run", "--token", TOKEN], "--api"),
        (["run", "--api", "https://a.b"], "token"),
        (["run", "--api", "ftp://a.b", "--token", TOKEN], "URL da API"),
        (
            ["run", "--api", "https://a.b", "--token", TOKEN, "--segment-seconds", "10"],
            "entre 20 e 120",
        ),
        (
            ["run", "--api", "https://a.b", "--token", TOKEN, "--segment-seconds", "121"],
            "entre 20 e 120",
        ),
        (
            [
                "run",
                "--api",
                "https://a.b",
                "--token",
                TOKEN,
                "--file",
                "x.mkv",
                "--obs",
                "ws://h:1",
            ],
            "--file",
        ),
        (
            ["run", "--api", "https://a.b", "--token", TOKEN, "--file", "x.mkv", "--auto-record"],
            "--file",
        ),
        (
            ["run", "--api", "https://a.b", "--token", TOKEN, "--obs", "http://h:4455"],
            "obs-websocket",
        ),
        (
            ["run", "--api", "https://a.b", "--token", TOKEN, "--max-upload-mbps", "0"],
            "maior que zero",
        ),
        (["run", "--api", "https://a.b", "--token", "cfd_ x"], "token"),
    ],
)
def test_invalid_configuration(argv: list[str], message: str) -> None:
    with pytest.raises(ConfigError, match=message):
        config_from(argv)


def test_check_subcommand_parses() -> None:
    args = cli.build_parser().parse_args(["check", "--api", "https://a.b", "--token", TOKEN])
    assert args.command == "check"
    assert cli.build_config(args).api_url == "https://a.b"


def test_main_returns_usage_error_without_api(capsys: pytest.CaptureFixture[str]) -> None:
    assert cli.main(["check", "--token", TOKEN]) == 2
    out = capsys.readouterr()
    assert "--api" in out.out
    assert TOKEN not in out.out + out.err


@pytest.mark.parametrize("argv", [["--help"], ["run", "--help"], ["check", "--help"]])
def test_module_help(argv: list[str]) -> None:
    result = subprocess.run(
        [sys.executable, "-m", "clipforge_companion", *argv],
        capture_output=True,
        timeout=60,
        check=False,
        cwd=Path(__file__).resolve().parents[1],
    )
    assert result.returncode == 0
    text = result.stdout.decode(errors="replace")
    assert "usage:" in text
    if argv == ["run", "--help"]:
        for flag in (
            "--obs",
            "--obs-password",
            "--auto-record",
            "--file",
            "--segment-seconds",
            "--title",
            "--work-dir",
            "--ffmpeg",
            "--max-upload-mbps",
            "--keep-segments",
        ):
            assert flag in text


def test_config_repr_hides_secrets() -> None:
    config = Config(api_url="https://a.b", token=TOKEN, obs_password="obs-secret-pass")
    assert TOKEN not in repr(config)
    assert "obs-secret-pass" not in repr(config)


def test_token_is_redacted_from_logs() -> None:
    stream = io.StringIO()
    redactor = Redactor([TOKEN, "obs-secret-pass"])
    setup_logging(verbose=True, redactor=redactor, stream=stream)
    logger = logging.getLogger("clipforge_companion.test")
    try:
        logger.info("using token %s", TOKEN)
        logger.debug("Authorization: Bearer %s", TOKEN)
        logger.warning("password was obs-secret-pass")
        logger.info("unknown token cfd_ZZZZZZZZZZZZZZZZ appeared")
        try:
            raise RuntimeError(f"request failed with {TOKEN}")
        except RuntimeError:
            logger.exception("boom")
    finally:
        setup_logging(verbose=False, redactor=Redactor(), stream=io.StringIO())
    output = stream.getvalue()
    assert TOKEN not in output
    assert "obs-secret-pass" not in output
    assert "cfd_ZZZZ" not in output
    assert output.count("***") >= 5
    assert "RuntimeError" in output  # the traceback is still logged, just redacted


def test_status_lines_are_redacted() -> None:
    stream = io.StringIO()
    status = StatusReporter(stream, redactor=Redactor([TOKEN]))
    status.error(f"falhou com {TOKEN}")
    assert TOKEN not in stream.getvalue()
    assert "falhou com ***" in stream.getvalue()


def test_check_prints_device_and_plan_without_token(capsys: pytest.CaptureFixture[str]) -> None:
    import asyncio

    fake = FakeLiveApi()
    config = Config(api_url="http://api.test", token=TOKEN, ffmpeg="definitely-not-ffmpeg-xyz")
    stream = io.StringIO()
    status = StatusReporter(stream, redactor=Redactor([TOKEN]))
    code = asyncio.run(cli.run_check(config, status, api=fake.client()))
    output = stream.getvalue()
    assert code == 0
    assert "PC do estúdio" in output
    assert "Pro (pro)" in output
    assert "FFmpeg não encontrado" in output
    assert TOKEN not in output
    assert fake.auth_headers == [f"Bearer {TOKEN}"]


def test_check_with_revoked_token_exits_3() -> None:
    import asyncio

    fake = FakeLiveApi()
    fake.revoked = True
    stream = io.StringIO()
    code = asyncio.run(
        cli.run_check(
            Config(api_url="http://api.test", token=TOKEN),
            StatusReporter(stream, redactor=Redactor([TOKEN])),
            api=fake.client(),
        )
    )
    assert code == 3
    assert "revogado" in stream.getvalue()


def test_check_against_unreachable_api_never_prints_token(
    capsys: pytest.CaptureFixture[str],
) -> None:
    code = cli.main(["check", "--api", "http://127.0.0.1:9", "--token", TOKEN, "-v"])
    out = capsys.readouterr()
    assert code == 1
    assert "Não foi possível contatar a API" in out.out
    assert TOKEN not in out.out
    assert TOKEN not in out.err
