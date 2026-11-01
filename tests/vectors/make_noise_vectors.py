"""Generate Noise_NNpsk0_25519_ChaChaPoly_SHA256 test vectors for the ESPHome API transport.

Uses the reference `noiseprotocol` library (the one aioesphomeapi uses) with fixed ephemeral keys so the
C# implementation can be checked byte for byte. Run with an interpreter that has `noiseprotocol` installed:

    python -I tests/vectors/make_noise_vectors.py

Writes noise_nnpsk0.json next to this script.
"""

import json
import pathlib

from noise.connection import Keypair, NoiseConnection

PROTOCOL = b"Noise_NNpsk0_25519_ChaChaPoly_SHA256"
PROLOGUE = b"NoiseAPIInit\x00\x00"
PSK = bytes(range(32))
# Fixed ephemeral private keys (arbitrary, test-only): initiator = 0x20..0x3f, responder = 0x40..0x5f.
INITIATOR_EPHEMERAL = bytes(range(0x20, 0x40))
RESPONDER_EPHEMERAL = bytes(range(0x40, 0x60))
INITIATOR_TO_RESPONDER = [b"\x00\x01\x00\x00", b"\x00\x09\x00\x05hello"]
RESPONDER_TO_INITIATOR = [b"\x00\x02\x00\x00", b"\x00\x0a\x00\x0bbt-proxy-x!"]


def make(initiator: bool, ephemeral: bytes) -> NoiseConnection:
    conn = NoiseConnection.from_name(PROTOCOL)
    if initiator:
        conn.set_as_initiator()
    else:
        conn.set_as_responder()
    conn.set_psks(PSK)
    conn.set_prologue(PROLOGUE)
    conn.set_keypair_from_private_bytes(Keypair.EPHEMERAL, ephemeral)
    conn.start_handshake()
    return conn


def main() -> None:
    initiator = make(True, INITIATOR_EPHEMERAL)
    responder = make(False, RESPONDER_EPHEMERAL)

    message_a = bytes(initiator.write_message())
    assert responder.read_message(message_a) == b""
    message_b = bytes(responder.write_message())
    assert initiator.read_message(message_b) == b""
    assert initiator.handshake_finished and responder.handshake_finished
    assert initiator.get_handshake_hash() == responder.get_handshake_hash()

    i2r = []
    for plaintext in INITIATOR_TO_RESPONDER:
        ciphertext = initiator.encrypt(plaintext)
        assert responder.decrypt(ciphertext) == plaintext
        i2r.append({"plaintext": plaintext.hex(), "ciphertext": ciphertext.hex()})
    r2i = []
    for plaintext in RESPONDER_TO_INITIATOR:
        ciphertext = responder.encrypt(plaintext)
        assert initiator.decrypt(ciphertext) == plaintext
        r2i.append({"plaintext": plaintext.hex(), "ciphertext": ciphertext.hex()})

    vectors = {
        "protocol": PROTOCOL.decode(),
        "generator": "noiseprotocol 0.3.1 via tests/vectors/make_noise_vectors.py",
        "prologue": PROLOGUE.hex(),
        "psk": PSK.hex(),
        "initiator_ephemeral_private": INITIATOR_EPHEMERAL.hex(),
        "responder_ephemeral_private": RESPONDER_EPHEMERAL.hex(),
        "message_a": message_a.hex(),
        "message_b": message_b.hex(),
        "handshake_hash": initiator.get_handshake_hash().hex(),
        "initiator_to_responder": i2r,
        "responder_to_initiator": r2i,
    }
    out = pathlib.Path(__file__).with_name("noise_nnpsk0.json")
    out.write_text(json.dumps(vectors, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
