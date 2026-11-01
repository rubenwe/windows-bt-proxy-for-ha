"""The real aioesphomeapi client against the real exe: what Home Assistant does, end to end."""

from __future__ import annotations

import asyncio
import base64
import os
import socket
import subprocess

import pytest
from aioesphomeapi import (
    ZERO_NOISE_PSK,
    APIClient,
    InvalidEncryptionKeyAPIError,
    RequiresEncryptionAPIError,
)

BTHOME_ADDRESS = 0xA4C138000001
WAIT = 10  # seconds; every network wait in this file is bounded


def new_client(proxy, noise_psk=None, **kwargs) -> APIClient:
    host, port, _ = proxy
    return APIClient(host, port, None, noise_psk=noise_psk, **kwargs)


async def connected(client: APIClient) -> APIClient:
    await asyncio.wait_for(client.connect(login=False), WAIT)
    return client


async def disconnect(client: APIClient) -> None:
    try:
        await asyncio.wait_for(client.disconnect(), WAIT)
    except Exception:
        pass


def mac_hex(device_info) -> str:
    return device_info.mac_address.replace(":", "").lower()


async def provision(proxy) -> str:
    """Does what Home Assistant does on adoption; returns the new key as base64 text."""
    key_b64 = base64.b64encode(os.urandom(32)).decode()
    client = new_client(proxy, noise_psk=ZERO_NOISE_PSK)
    try:
        await connected(client)
        assert await asyncio.wait_for(client.noise_encryption_set_key(key_b64.encode()), WAIT) is True
    finally:
        await disconnect(client)
    return key_b64


async def first_advertisement(client: APIClient, address: int, timeout: float):
    """The first raw advertisement from `address`, or None after `timeout` seconds."""
    found: asyncio.Future = asyncio.get_running_loop().create_future()

    def on_ads(response) -> None:
        for ad in response.advertisements:
            if ad.address == address and not found.done():
                found.set_result(ad)

    unsubscribe = client.subscribe_bluetooth_le_raw_advertisements(on_ads)
    try:
        return await asyncio.wait_for(found, timeout)
    except TimeoutError:
        return None
    finally:
        unsubscribe()


async def test_plaintext_device_info(proxy):
    client = new_client(proxy)
    try:
        await connected(client)
        info = await asyncio.wait_for(client.device_info(), WAIT)
        api_version = client.api_version
        assert (api_version.major, api_version.minor) == (1, 13)
        assert info.bluetooth_proxy_feature_flags_compat(api_version) == 33
        assert info.api_encryption_supported is True
        assert info.api_encryption_provisionable is True
        assert info.manufacturer == "Windows"
        assert info.esphome_version == "2025.6.0"
        assert info.name
        assert info.mac_address == info.bluetooth_mac_address
        assert len(mac_hex(info)) == 12
    finally:
        await disconnect(client)


async def test_raw_advertisements_flow(proxy):
    client = new_client(proxy)
    try:
        await connected(client)
        ad = await first_advertisement(client, BTHOME_ADDRESS, 2)
        assert ad is not None, f"no advertisement from {BTHOME_ADDRESS:#x} within 2 s\n{proxy.output}"
        assert ad.rssi == -60
        assert bytes(ad.data)[:3] == b"\x02\x01\x06"
    finally:
        await disconnect(client)


async def test_ha_style_provisioning(proxy):
    # HA connects with the zero PSK and sends the new key; afterwards only that key works.
    key_b64 = await provision(proxy)

    plain = new_client(proxy)
    with pytest.raises(RequiresEncryptionAPIError):
        await asyncio.wait_for(plain.connect(login=False), WAIT)
    await disconnect(plain)

    zero = new_client(proxy, noise_psk=ZERO_NOISE_PSK)
    with pytest.raises(InvalidEncryptionKeyAPIError):
        await asyncio.wait_for(zero.connect(login=False), WAIT)
    await disconnect(zero)

    wrong = new_client(proxy, noise_psk=base64.b64encode(os.urandom(32)).decode())
    with pytest.raises(InvalidEncryptionKeyAPIError):
        await asyncio.wait_for(wrong.connect(login=False), WAIT)
    await disconnect(wrong)

    keyed = new_client(proxy, noise_psk=key_b64)
    try:
        await connected(keyed)
        info = await asyncio.wait_for(keyed.device_info(), WAIT)
        assert info.api_encryption_supported is True
        assert info.api_encryption_provisionable is False
        assert await first_advertisement(keyed, BTHOME_ADDRESS, WAIT) is not None
    finally:
        await disconnect(keyed)


async def test_expected_name_and_mac_accepted(proxy):
    probe = new_client(proxy)
    try:
        await connected(probe)
        info = await asyncio.wait_for(probe.device_info(), WAIT)
    finally:
        await disconnect(probe)
    name, mac = info.name, mac_hex(info)

    key_b64 = await provision(proxy)
    client = new_client(proxy, noise_psk=key_b64, expected_name=name, expected_mac=mac)
    try:
        await connected(client)
        again = await asyncio.wait_for(client.device_info(), WAIT)
        assert again.name == name
        assert mac_hex(again) == mac
    finally:
        await disconnect(client)


async def test_key_survives_restart(proxy_restartable):
    proxy = proxy_restartable
    key_b64 = await provision(proxy)

    code = proxy.stop()
    if not proxy.forced:  # a CI runner without a console cannot deliver Ctrl+Break; then the process was killed and the code means nothing
        assert code == 0, f"graceful stop should exit 0\n{proxy.output}"
    proxy.start()

    plain = new_client(proxy)
    with pytest.raises(RequiresEncryptionAPIError):
        await asyncio.wait_for(plain.connect(login=False), WAIT)
    await disconnect(plain)

    keyed = new_client(proxy, noise_psk=key_b64)
    try:
        await connected(keyed)
        assert await first_advertisement(keyed, BTHOME_ADDRESS, WAIT) is not None
    finally:
        await disconnect(keyed)


def test_corrupt_key_file_exits_2(proxy_factory):
    proxy = proxy_factory()
    (proxy.data_dir / "noise.key").write_bytes(b"this is not a key\r\n")
    with pytest.raises(RuntimeError, match="exited early") as excinfo:
        proxy.start()
    assert proxy.exit_code == 2, str(excinfo.value)
    with pytest.raises(OSError), socket.create_connection((proxy.host, proxy.port), timeout=1):
        pass  # the port never opened


def test_port_in_use_exits_3(proxy_factory):
    proxy = proxy_factory()
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as blocker:
        blocker.bind(("0.0.0.0", proxy.port))
        blocker.listen()
        process = proxy.spawn()  # start() would see the blocker accepting connections
        assert process.wait(timeout=30) == 3, proxy.output


def test_unknown_command_prints_usage_and_exits_1(proxy_exe):
    result = subprocess.run([str(proxy_exe), "frobnicate"], capture_output=True, text=True, timeout=30, stdin=subprocess.DEVNULL)
    assert result.returncode == 1
    assert "Usage" in result.stdout + result.stderr
