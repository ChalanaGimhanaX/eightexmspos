#!/usr/bin/env python3
"""Build an immutable signed v2 release from a complete Windows folder publish.

Does not deploy, edit source versions, or broadcast. Requires cryptography.
Private signing key stays in the release environment, never in the public output.
"""
import argparse
import base64
import hashlib
import json
import re
from pathlib import Path
from urllib.parse import urlparse

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa


def build_release(publish: Path, output: Path, version: str, supported_from: list[str],
                  base_url: str, signing_key: Path, notes: str = "") -> Path:
    publish, output, signing_key = publish.resolve(), output.resolve(), signing_key.resolve()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version) or not supported_from:
        raise ValueError("Specify major.minor.patch and explicitly certified source versions")
    if any(not re.fullmatch(r"\d+\.\d+\.\d+", v) or tuple(map(int, v.split('.'))) >= tuple(map(int, version.split('.'))) for v in supported_from):
        raise ValueError("Source versions must precede the target version")
    uri = urlparse(base_url)
    if uri.scheme != "https" or not uri.netloc or uri.username or uri.query or uri.fragment:
        raise ValueError("Public base URL must use HTTPS without credentials/query")
    if output == publish or output.is_relative_to(publish) or signing_key.is_relative_to(publish) or signing_key.is_relative_to(output):
        raise ValueError("Keep output and signing keys outside the publish tree; keys outside public output")
    for name in ("Enightx.Pos.Wpf.exe", "Enightx.Pos.Wpf.dll", "ApplyUpdate.ps1"):
        if not (publish / name).is_file():
            raise ValueError(f"Missing {name}; provide a complete folder publish")
    deps = json.loads((publish / "Enightx.Pos.Wpf.deps.json").read_text())
    if f"Enightx.Pos.Wpf/{version}" not in deps.get("libraries", {}) or f"Enightx.Pos/{version}" not in deps.get("libraries", {}):
        raise ValueError("Published dependency metadata does not match the target version. Rebuild both projects with explicit version properties.")
    key = serialization.load_pem_private_key(signing_key.read_bytes(), password=None)
    if not isinstance(key, rsa.RSAPrivateKey) or key.key_size < 3072:
        raise ValueError("Use an RSA release signing key of at least 3072 bits")
    destination = output / "releases" / version
    destination.mkdir(parents=True, exist_ok=False)  # Never overwrite a published release identity.
    files = []
    for source in sorted(publish.rglob("*")):
        if source.is_symlink():
            raise ValueError("Publish tree must not contain symlinks")
        if not source.is_file():
            continue
        relative = source.relative_to(publish).as_posix()
        if relative == "release-public-key.pem":
            continue  # Provision the trust anchor with the initial installation, not by remote replacement.
        if any(part.startswith('.') for part in relative.split('/')) or source.suffix.lower() in {".db", ".sqlite", ".pem", ".key"} or relative in {"release.json", "installed-release.json"}:
            raise ValueError(f"Unexpected private/runtime file in publish tree: {relative}")
        content = source.read_bytes()
        digest = hashlib.sha256(content).hexdigest()
        blob = output / "objects" / digest
        blob.parent.mkdir(parents=True, exist_ok=True)
        if blob.exists():
            if hashlib.sha256(blob.read_bytes()).hexdigest() != digest:
                raise ValueError("Existing object has incorrect contents")
        else:
            blob.write_bytes(content)
        files.append({"path": relative, "sha256": digest, "size": len(content), "url": f"{base_url.rstrip('/')}/objects/{digest}"})
    manifest = {"protocolVersion": 2, "releaseId": version, "version": version,
                "releaseNotes": notes, "supportedFrom": supported_from, "databaseSchema": 1, "files": files}
    payload = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
    signature = key.sign(payload, padding.PKCS1v15(), hashes.SHA256())
    envelope = {"payload": base64.b64encode(payload).decode(), "signature": base64.b64encode(signature).decode()}
    manifest_path = destination / "version-v2.json"
    manifest_path.write_text(json.dumps(envelope), encoding="utf-8")
    # Public key is supplied to bootstrap installer separately; never a private key.
    (destination / "release-public-key.pem").write_bytes(key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo))
    return manifest_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--supported-from", nargs="+", required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--signing-key", type=Path, required=True)
    parser.add_argument("--notes", default="")
    args = parser.parse_args()
    result = build_release(args.publish, args.output, args.version, args.supported_from, args.base_url, args.signing_key, args.notes)
    print(f"Prepared {result}. Upload objects and immutable release first; atomically promote version-v2.json last.")


if __name__ == "__main__":
    main()
