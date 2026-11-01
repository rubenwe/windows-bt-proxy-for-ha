"""The `status` command against the real exe, over the status pipe (each proxy has its own pipe name, never the default)."""

from __future__ import annotations

import asyncio
import time

from aioesphomeapi import APIClient

WAIT = 10  # seconds; every wait in this file is bounded


async def test_status_reports_running_proxy(proxy_factory):
    proxy = proxy_factory()
    proxy.start()
    client = APIClient(proxy.host, proxy.port, None, client_info="interop-status-test")
    try:
        await asyncio.wait_for(client.connect(login=False), WAIT)
        unsubscribe = client.subscribe_bluetooth_le_raw_advertisements(lambda _: None)
        try:
            # The subscription is a message the proxy reads after connect() returns; ask until the status shows it.
            deadline = time.monotonic() + WAIT
            while True:
                result = await asyncio.to_thread(proxy.status)
                if "subscribed" in result.stdout and "not subscribed" not in result.stdout or time.monotonic() > deadline:
                    break
                await asyncio.sleep(0.2)
        finally:
            unsubscribe()
    finally:
        await asyncio.wait_for(client.disconnect(), WAIT)

    assert result.returncode == 0, result.stdout
    lines = result.stdout.splitlines()
    assert lines[1].startswith("Bluetooth:      Running, passive scan, adapter "), result.stdout
    assert f"API:            port {proxy.port}" in lines, result.stdout
    assert "Encryption:     no key yet; Home Assistant provisions one when it adds the proxy" in lines, result.stdout
    assert "Clients:        127.0.0.1  interop-status-test  subscribed, not encrypted" in lines, result.stdout


def test_status_without_service_exits_1(proxy_factory):
    proxy = proxy_factory()  # never started: nothing serves its pipe

    result = proxy.status()

    assert result.returncode == 1, result.stdout
    assert result.stdout.strip() == (
        f"The Windows BT Proxy service is not running (or not installed). (pipe {proxy.pipe_name})"
    )
