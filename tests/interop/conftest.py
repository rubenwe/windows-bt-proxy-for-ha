"""Fixtures that run the real windows-bt-proxy.exe (Debug build, fake scanner, no mDNS) for aioesphomeapi to talk to."""

from __future__ import annotations

import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
PROJECT = REPO_ROOT / "src" / "WindowsBtProxy" / "WindowsBtProxy.csproj"
EXE_NAME = "windows-bt-proxy.exe"

STARTUP_TIMEOUT = 30.0
STOP_TIMEOUT = 10.0
STATUS_TIMEOUT = 15.0  # the status command itself gives up after a few seconds


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _find_exe() -> Path:
    """The exe the Debug build just produced, asked of MSBuild rather than guessed from the bin folder."""
    result = subprocess.run(
        ["dotnet", "msbuild", str(PROJECT), "-p:Configuration=Debug", "-getProperty:TargetDir", "-nologo"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
    )
    exe = Path(result.stdout.strip()) / EXE_NAME
    if result.returncode != 0 or not exe.is_file():
        raise FileNotFoundError(f"{EXE_NAME} not found at {exe}\n{result.stdout}\n{result.stderr}")
    return exe


@pytest.fixture(scope="session")
def proxy_exe() -> Path:
    """Builds the Debug configuration once per test session (a quick no-op build when nothing changed)."""
    result = subprocess.run(
        ["dotnet", "build", str(PROJECT), "-c", "Debug", "--nologo", "-v", "q"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        pytest.fail(f"dotnet build failed:\n{result.stdout}\n{result.stderr}", pytrace=False)
    return _find_exe()


class ProxyProcess:
    """
    One proxy exe on a fixed port, data dir and status pipe that can be stopped and started again. Always call stop() (the
    fixture does). The pipe name is unique: the installed service owns the default one, which tests must never touch.
    """

    host = "127.0.0.1"

    def __init__(self, exe: Path, data_dir: Path, port: int, *extra_args: str) -> None:
        self.exe = exe
        self.data_dir = data_dir
        self.port = port
        self.pipe_name = f"wbp-test-{uuid.uuid4().hex}"
        self.extra_args = extra_args
        self.process: subprocess.Popen | None = None
        self.exit_code: int | None = None  # of the most recent run that ended
        self.forced = False  # the most recent stop() had to kill the process (no console to send Ctrl+Break to, or it hung)
        self._stdout = data_dir.parent / f"{data_dir.name}-stdout.txt"

    def __iter__(self):
        # Lets tests write `host, port, data_dir = proxy`.
        return iter((self.host, self.port, self.data_dir))

    @property
    def output(self) -> str:
        try:
            return self._stdout.read_text(errors="replace")
        except OSError:
            return ""

    def command(self) -> list[str]:
        return [
            str(self.exe), "run", "--fake-scanner", "--no-mdns",
            "--port", str(self.port), "--data-dir", str(self.data_dir),
            "--no-key-acl",  # the key file's SYSTEM/Administrators-only ACL would lock out a test run by a normal user
            "--pipe-name", self.pipe_name,
            *self.extra_args,
        ]

    def status(self) -> subprocess.CompletedProcess:
        """Runs `status` against this proxy's pipe (whether or not the proxy is running); stdout and stderr together."""
        return subprocess.run(
            [str(self.exe), "status", "--pipe-name", self.pipe_name],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            timeout=STATUS_TIMEOUT,
        )

    def spawn(self) -> subprocess.Popen:
        """Starts the process without waiting for anything."""
        assert self.process is None, "already started"
        with self._stdout.open("ab") as out:
            process = subprocess.Popen(
                self.command(),
                stdin=subprocess.DEVNULL,
                stdout=out,
                stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if sys.platform == "win32" else 0,
            )
        self.process = process
        return process

    def start(self) -> None:
        """Starts the process and waits until it accepts connections; raises if it exits first."""
        process = self.spawn()
        deadline = time.monotonic() + STARTUP_TIMEOUT
        while time.monotonic() < deadline:
            code = process.poll()
            if code is not None:
                self.process = None
                self.exit_code = code
                raise RuntimeError(f"proxy exited early with code {code}:\n{self.output}")
            try:
                with socket.create_connection((self.host, self.port), timeout=0.5):
                    return
            except OSError:
                time.sleep(0.1)
        self.stop()
        raise RuntimeError(f"proxy did not open port {self.port} within {STARTUP_TIMEOUT} s:\n{self.output}")

    def stop(self) -> int | None:
        """Asks the process to stop (Ctrl+Break), kills it if it ignores that, and returns its exit code."""
        process, self.process = self.process, None
        if process is None:
            return None
        self.forced = False
        try:
            if process.poll() is None and sys.platform == "win32":
                try:
                    process.send_signal(signal.CTRL_BREAK_EVENT)
                except OSError:
                    pass
            try:
                process.wait(timeout=STOP_TIMEOUT)
            except subprocess.TimeoutExpired:
                self.forced = True
                process.kill()
                process.wait(timeout=STOP_TIMEOUT)
        finally:
            if process.poll() is None:
                process.kill()
        self.exit_code = process.returncode
        return process.returncode


@pytest.fixture
def proxy_factory(proxy_exe: Path):
    """Creates ProxyProcess objects on free ports with fresh data dirs; whatever is still running at the end is killed."""
    root = Path(tempfile.mkdtemp(prefix="wbp-interop-"))
    created: list[ProxyProcess] = []

    def make(*extra_args: str) -> ProxyProcess:
        data_dir = root / f"data{len(created)}"
        data_dir.mkdir()
        p = ProxyProcess(proxy_exe, data_dir, free_port(), *extra_args)
        created.append(p)
        return p

    try:
        yield make
    finally:
        for p in created:
            try:
                p.stop()
            except Exception:
                pass
        shutil.rmtree(root, ignore_errors=True)


@pytest.fixture
def proxy(proxy_factory):
    """A running proxy: unpack as (host, port, data_dir)."""
    p = proxy_factory()
    p.start()
    return p


@pytest.fixture
def proxy_restartable(proxy):
    """Same proxy; the test calls stop() and start() itself."""
    return proxy
