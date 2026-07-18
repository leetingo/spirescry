#!/usr/bin/env python3
"""Process-level regression checks for the host's last-gasp exit trail."""

import os
import signal
import subprocess
import sys
import tempfile
import time


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOST_DLL = os.path.join(
    REPO, "headless", "Host", "bin", "Release", "spirescry_host.dll"
)


def launch(exit_mode):
    env = os.environ.copy()
    env["STS2_HOST_EXIT_TRAIL_TEST"] = exit_mode
    log = tempfile.TemporaryFile(mode="w+")
    proc = subprocess.Popen(
        ["dotnet", HOST_DLL],
        cwd=REPO,
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=log,
        text=True,
    )
    return proc, log


def log_text(log):
    log.seek(0)
    return log.read()


def wait_for_line(proc, log, expected, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        text = log_text(log)
        if expected in text:
            return text
        if proc.poll() is not None:
            break
        time.sleep(0.05)
    raise AssertionError(
        f"host never logged {expected!r}; rc={proc.poll()}, stderr={log_text(log)!r}"
    )


def clean_shutdown():
    proc, log = launch("clean")
    try:
        proc.wait(timeout=5)
        text = log_text(log)
        assert proc.returncode == 0, (proc.returncode, text)
        assert "shutdown: clean self-test" in text, text
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        log.close()


def process_exit_shutdown():
    proc, log = launch("process-exit")
    try:
        proc.wait(timeout=5)
        text = log_text(log)
        assert proc.returncode == 0, (proc.returncode, text)
        assert "shutdown: process exit" in text, text
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        log.close()


def signal_shutdown(sig):
    proc, log = launch("wait")
    try:
        wait_for_line(proc, log, "exit-trail test ready")
        proc.send_signal(sig)
        proc.wait(timeout=5)
        text = log_text(log)
        assert proc.returncode == 0, (proc.returncode, text)
        expected = {
            signal.SIGINT: ("shutdown: signal SIGINT", "shutdown: console ControlC"),
            signal.SIGQUIT: ("shutdown: signal SIGQUIT", "shutdown: console ControlBreak"),
        }.get(sig, (f"shutdown: signal {signal.Signals(sig).name}",))
        assert any(line in text for line in expected), text
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        log.close()


def unhandled_exception():
    proc, log = launch("unhandled-thread")
    try:
        proc.wait(timeout=5)
        text = log_text(log)
        assert proc.returncode != 0, (proc.returncode, text)
        assert "unhandled exception" in text, text
        assert "exit-trail test exception" in text, text
        assert "InvalidOperationException" in text, text
        assert " at " in text, text
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        log.close()


def main():
    if not os.path.isfile(HOST_DLL):
        sys.exit(f"host not built ({HOST_DLL}) — run: ./build.sh headless-setup")

    checks = [
        ("clean_shutdown", clean_shutdown),
        ("process_exit_shutdown", process_exit_shutdown),
    ]
    checks.extend(
        (
            f"signal_shutdown_{signal.Signals(sig).name}",
            lambda sig=sig: signal_shutdown(sig),
        )
        for sig in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP, signal.SIGQUIT)
    )
    checks.append(("unhandled_exception", unhandled_exception))
    failures = 0
    for name, check in checks:
        try:
            check()
            print(f"ok - {name}")
        except Exception as ex:
            failures += 1
            print(f"not ok - {name}: {ex}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
