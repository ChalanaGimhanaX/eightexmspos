import base64
import importlib.util
import json
from pathlib import Path

import pytest
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

spec = importlib.util.spec_from_file_location("release_desktop", Path(__file__).resolve().parents[2] / "infra/release_desktop.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


@pytest.fixture
def release_inputs(tmp_path):
    publish = tmp_path / "publish"
    publish.mkdir()
    for name in ["Enightx.Pos.Wpf.exe", "Enightx.Pos.Wpf.dll", "ApplyUpdate.ps1", "new-third-party.dll", "coreclr.dll"]:
        (publish / name).write_bytes(name.encode())
    (publish / "Enightx.Pos.Wpf.deps.json").write_text(json.dumps({"libraries": {"Enightx.Pos.Wpf/1.0.4": {}, "Enightx.Pos/1.0.4": {}}}))
    key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    key_path = tmp_path / "private.pem"
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    return publish, tmp_path / "output", key_path, key


def test_complete_manifest_signed_immutable_and_reuses_objects(release_inputs):
    publish, output, key_path, key = release_inputs
    result = builder.build_release(publish, output, "1.0.4", ["1.0.1"], "https://example.test/downloads", key_path)
    envelope = json.loads(result.read_text())
    payload = base64.b64decode(envelope["payload"])
    key.public_key().verify(base64.b64decode(envelope["signature"]), payload, padding.PKCS1v15(), hashes.SHA256())
    manifest = json.loads(payload)
    assert {f["path"] for f in manifest["files"]} == {p.name for p in publish.iterdir()}
    assert manifest["supportedFrom"] == ["1.0.1"]
    assert len(list((output / "objects").iterdir())) == len(manifest["files"])
    with pytest.raises(FileExistsError):
        builder.build_release(publish, output, "1.0.4", ["1.0.1"], "https://example.test/downloads", key_path)


def test_wrong_build_version_and_key_inside_publish_rejected(release_inputs):
    publish, output, key_path, _ = release_inputs
    with pytest.raises(ValueError, match="metadata"):
        builder.build_release(publish, output, "1.0.5", ["1.0.1"], "https://example.test/downloads", key_path)
    unsafe = publish / "private.pem"
    unsafe.write_bytes(key_path.read_bytes())
    with pytest.raises(ValueError, match="signing keys"):
        builder.build_release(publish, output, "1.0.4", ["1.0.1"], "https://example.test/downloads", unsafe)
