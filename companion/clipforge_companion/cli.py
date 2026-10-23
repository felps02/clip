"""Command line interface (``python -m clipforge_companion``).

Subcommands:

* ``run``: captures the OBS recording (OBS mode) or a file (``--file``) and uploads segments;
* ``check``: validates the device token against ``GET /api/live/companion/me`` and prints the
  device name and plan (plus the FFmpeg version, and the OBS connection with ``--obs``).

Every flag has an environment variable fallback. The token and the OBS password are registered
with the log redactor before anything is printed, so they never show up in the output.
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import sys
from collections.abc import Sequence
from pathlib import Path

from clipforge_companion import __version__
from clipforge_companion.api import ApiError, AuthError, CompanionApi, RetryableError
from clipforge_companion.app import (
    EXIT_ABORTED,
    EXIT_AUTH,
    EXIT_ERROR,
    EXIT_OK,
    EXIT_USAGE,
    TOKEN_REJECTED,
    CompanionApp,
)
from clipforge_companion.config import (
    DEFAULT_OBS_URL,
    DEFAULT_SEGMENT_SECONDS,
    ENV_API_URL,
    ENV_FFMPEG,
    ENV_OBS_PASSWORD,
    ENV_OBS_URL,
    ENV_TOKEN,
    MAX_SEGMENT_SECONDS,
    MIN_SEGMENT_SECONDS,
    Config,
    ConfigError,
    default_ffmpeg_path,
    default_work_dir,
    env,
    is_insecure_remote,
    looks_like_device_token,
    normalize_api_url,
    normalize_obs_url,
    normalize_token,
)
from clipforge_companion.logsetup import Redactor, setup_logging
from clipforge_companion.obs import ObsClient, ObsError
from clipforge_companion.status import StatusReporter

DESCRIPTION = (
    "Companion local do OBS: acompanha a SUA gravação enquanto ela é escrita, corta em "
    "segmentos (sem recodificar) e envia para a detecção de destaques ao vivo. Nada é "
    "capturado da Twitch, do YouTube ou de outras plataformas."
)

EPILOG = (
    "Variáveis de ambiente: "
    f"{ENV_API_URL}, {ENV_TOKEN}, {ENV_OBS_URL}, {ENV_OBS_PASSWORD}, {ENV_FFMPEG}. "
    "Prefira variáveis para o token e a senha: argumentos ficam no histórico do terminal."
)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="python -m clipforge_companion", description=DESCRIPTION, epilog=EPILOG
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    commands = parser.add_subparsers(dest="command", metavar="{run,check}")
    commands.required = True

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--api", metavar="URL", help=f"endereço da API (ou {ENV_API_URL})")
    common.add_argument(
        "--token",
        metavar="TOKEN",
        help=f"token do dispositivo criado na página Lives (ou {ENV_TOKEN}, preferível)",
    )
    common.add_argument(
        "-v", "--verbose", action="store_true", help="mostra logs de diagnóstico (sem segredos)"
    )

    run = commands.add_parser(
        "run",
        parents=[common],
        help="captura a gravação e envia os segmentos",
        description=DESCRIPTION,
        epilog=EPILOG,
    )
    obs = run.add_argument_group("modo OBS (padrão)")
    obs.add_argument(
        "--obs", metavar="URL", help=f"endereço do obs-websocket (padrão {DEFAULT_OBS_URL})"
    )
    obs.add_argument(
        "--obs-password",
        metavar="SENHA",
        help=f"senha do obs-websocket (ou {ENV_OBS_PASSWORD}, preferível)",
    )
    obs.add_argument(
        "--auto-record",
        action="store_true",
        help="inicia a gravação no OBS quando ele transmitir sem gravar",
    )
    files = run.add_argument_group("modo arquivo (sem OBS)")
    files.add_argument(
        "--file",
        metavar="ARQUIVO",
        type=Path,
        help="captura este arquivo (sendo gravado ou já pronto) em vez de conversar com o OBS",
    )
    options = run.add_argument_group("opções")
    options.add_argument(
        "--segment-seconds",
        type=int,
        default=DEFAULT_SEGMENT_SECONDS,
        metavar="N",
        help=(
            f"duração alvo de cada segmento, de {MIN_SEGMENT_SECONDS} a {MAX_SEGMENT_SECONDS} s "
            f"(padrão {DEFAULT_SEGMENT_SECONDS}); os cortes caem em quadros-chave. Vale para "
            "sessões novas: uma sessão retomada continua com a duração com que foi criada"
        ),
    )
    options.add_argument(
        "--title", metavar="TÍTULO", help="título da sessão (padrão: Live de dd/MM HH:mm)"
    )
    options.add_argument(
        "--work-dir",
        type=Path,
        metavar="DIR",
        help=f"pasta dos segmentos e da fila de envio (padrão {default_work_dir()})",
    )
    options.add_argument(
        "--ffmpeg", metavar="CAMINHO", help=f"executável do FFmpeg (ou {ENV_FFMPEG})"
    )
    options.add_argument(
        "--max-upload-mbps",
        type=float,
        metavar="MBPS",
        help="limite de banda do envio, em megabits por segundo (padrão: sem limite)",
    )
    options.add_argument(
        "--keep-segments", action="store_true", help="não apaga os segmentos depois de enviados"
    )

    check = commands.add_parser(
        "check",
        parents=[common],
        help="valida o token e o acesso à API",
        description="Valida o token do dispositivo e mostra o dispositivo e o plano.",
        epilog=EPILOG,
    )
    check.add_argument(
        "--ffmpeg", metavar="CAMINHO", help=f"executável do FFmpeg (ou {ENV_FFMPEG})"
    )
    check.add_argument("--obs", metavar="URL", help="também testa a conexão com o obs-websocket")
    check.add_argument(
        "--obs-password", metavar="SENHA", help=f"senha do obs-websocket (ou {ENV_OBS_PASSWORD})"
    )
    return parser


def collect_secrets(args: argparse.Namespace) -> list[str]:
    values = [
        args.token,
        env(ENV_TOKEN),
        getattr(args, "obs_password", None),
        env(ENV_OBS_PASSWORD),
    ]
    return [v for v in values if v]


def build_config(args: argparse.Namespace) -> Config:
    """Flags first, then environment variables, then defaults. Raises :class:`ConfigError`."""
    api = args.api or env(ENV_API_URL)
    if not api:
        raise ConfigError(f"Informe o endereço da API com --api ou {ENV_API_URL}.")
    token = args.token or env(ENV_TOKEN)
    if not token:
        raise ConfigError(
            f"Informe o token do dispositivo com {ENV_TOKEN} (ou --token). Crie o token na "
            "página Lives, em Dispositivos."
        )
    command = args.command
    file = getattr(args, "file", None)
    obs_flag = getattr(args, "obs", None)
    password = getattr(args, "obs_password", None) or env(ENV_OBS_PASSWORD)
    if (
        command == "run"
        and file is not None
        and (obs_flag or getattr(args, "obs_password", None) or args.auto_record)
    ):
        raise ConfigError(
            "--file não pode ser combinado com --obs, --obs-password ou --auto-record."
        )
    obs_url = normalize_obs_url(obs_flag or env(ENV_OBS_URL) or DEFAULT_OBS_URL)
    segment_seconds = getattr(args, "segment_seconds", DEFAULT_SEGMENT_SECONDS)
    if not MIN_SEGMENT_SECONDS <= segment_seconds <= MAX_SEGMENT_SECONDS:
        raise ConfigError(
            f"--segment-seconds deve ficar entre {MIN_SEGMENT_SECONDS} e {MAX_SEGMENT_SECONDS}."
        )
    max_upload = getattr(args, "max_upload_mbps", None)
    if max_upload is not None and max_upload <= 0:
        raise ConfigError("--max-upload-mbps deve ser maior que zero.")
    title = (getattr(args, "title", None) or "").strip() or None
    if title and len(title) > 200:
        raise ConfigError("O título deve ter no máximo 200 caracteres.")
    work_dir = getattr(args, "work_dir", None) or default_work_dir()
    return Config(
        api_url=normalize_api_url(api),
        token=normalize_token(token),
        obs_url=obs_url,
        obs_password=password,
        auto_record=bool(getattr(args, "auto_record", False)),
        file=file,
        segment_seconds=segment_seconds,
        title=title,
        work_dir=Path(work_dir).expanduser(),
        ffmpeg=getattr(args, "ffmpeg", None) or default_ffmpeg_path(),
        max_upload_mbps=max_upload,
        keep_segments=bool(getattr(args, "keep_segments", False)),
        verbose=bool(args.verbose),
    )


def warn_about_config(config: Config, status: StatusReporter) -> None:
    if is_insecure_remote(config.api_url):
        status.warn(
            "A API está em http:// fora desta máquina: o token trafega sem criptografia. "
            "Use https://."
        )
    if not looks_like_device_token(config.token):
        status.warn(
            "O token não tem o formato esperado (cfd_ seguido de 40 letras e números). Confira "
            "se ele foi copiado inteiro."
        )


async def ffmpeg_version(ffmpeg: str, timeout: float = 15.0) -> str | None:
    """First line of ``ffmpeg -version`` or None when it cannot be run."""
    try:
        proc = await asyncio.create_subprocess_exec(
            ffmpeg,
            "-hide_banner",
            "-version",
            stdin=asyncio.subprocess.DEVNULL,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL,
        )
    except OSError:
        return None
    try:
        out, _ = await asyncio.wait_for(proc.communicate(), timeout)
    except TimeoutError:
        with contextlib.suppress(ProcessLookupError):
            proc.kill()
        await proc.wait()
        return None
    if proc.returncode != 0:
        return None
    lines = out.decode("utf-8", "replace").splitlines()
    return lines[0].strip() if lines else None


async def run_check(
    config: Config,
    status: StatusReporter,
    *,
    obs_url: str | None = None,
    api: CompanionApi | None = None,
) -> int:
    owns_api = api is None
    api = api or CompanionApi(config.api_url, config.token)
    try:
        try:
            me = await api.me()
        except AuthError:
            status.error(TOKEN_REJECTED)
            return EXIT_AUTH
        except RetryableError as exc:
            status.error(f"Não foi possível contatar a API em {config.api_url}: {exc.message}")
            return EXIT_ERROR
        except ApiError as exc:
            status.error(
                f"A API respondeu {exc.message} em /api/live/companion/me. Confira se --api "
                "aponta para a API."
            )
            return EXIT_ERROR
    finally:
        if owns_api:
            await api.aclose()
    device = me.get("device") or {}
    plan = me.get("plan") or {}
    user = me.get("user") or {}
    status.info(f"Token válido. Dispositivo: {device.get('name') or '?'}")
    plan_name = plan.get("name") or plan.get("code") or "?"
    plan_code = f" ({plan['code']})" if plan.get("code") and plan.get("name") else ""
    status.info(f"Plano: {plan_name}{plan_code}")
    if user.get("displayName"):
        status.info(f"Conta: {user['displayName']}")
    version = await ffmpeg_version(config.ffmpeg)
    if version:
        status.info(f"FFmpeg: {version}")
    else:
        status.warn(
            f"FFmpeg não encontrado ou não executável ({config.ffmpeg}). Use --ffmpeg ou "
            f"{ENV_FFMPEG}; ele é necessário para o comando run."
        )
    if obs_url:
        exit_code = await check_obs(obs_url, config.obs_password, status)
        if exit_code != EXIT_OK:
            return exit_code
    return EXIT_OK


async def check_obs(url: str, password: str | None, status: StatusReporter) -> int:
    client = ObsClient(url, password, backoff_initial=0.5)
    task = asyncio.create_task(client.run())
    try:
        connected = await client.wait_connected(8.0)
        if task.done() and task.exception() is not None:
            status.error(str(task.exception()))
            return EXIT_ERROR
        if not connected:
            status.error(f"OBS indisponível em {url}: {client.last_error or 'sem resposta'}")
            return EXIT_ERROR
        version = await client.get_version()
        record = await client.get_record_status()
        directory = await client.get_record_directory()
        status.info(
            f"OBS conectado: OBS {version.get('obsVersion', '?')}, obs-websocket "
            f"{version.get('obsWebSocketVersion', '?')}; "
            f"{'gravando' if record.get('outputActive') else 'sem gravação no momento'}; "
            f"pasta de gravação: {directory or '?'}"
        )
        return EXIT_OK
    except ObsError as exc:
        status.error(str(exc))
        return EXIT_ERROR
    finally:
        await client.close()
        if not task.done():
            task.cancel()
        with contextlib.suppress(BaseException):
            await task


def _configure_stdout() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            with contextlib.suppress(Exception):
                reconfigure(errors="replace")


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    _configure_stdout()
    redactor = Redactor(collect_secrets(args))
    setup_logging(verbose=args.verbose, redactor=redactor)
    status = StatusReporter(redactor=redactor)
    try:
        config = build_config(args)
    except ConfigError as exc:
        status.error(str(exc))
        return EXIT_USAGE
    redactor.add(config.token)
    redactor.add(config.obs_password)
    warn_about_config(config, status)
    try:
        if args.command == "check":
            obs_url = normalize_obs_url(args.obs) if args.obs else None
            return asyncio.run(run_check(config, status, obs_url=obs_url))
        return asyncio.run(CompanionApp(config, status=status).run())
    except ConfigError as exc:
        status.error(str(exc))
        return EXIT_USAGE
    except KeyboardInterrupt:
        status.warn("Interrompido.")
        return EXIT_ABORTED
