#!/usr/bin/env python3
"""
release_v17.py:
Comprehensive release builder and deployment script for Enightx POS v1.0.19:
1. Updates .csproj versions to 1.0.17
2. Compiles Windows desktop application (folder + single-file) via Wine dotnet
3. Packages EnightxPos-Setup.zip, EnightxPos-Update.zip
4. Builds and cryptographically signs version-v2.json using release_private_key.pem
5. Builds version.json for legacy v1 clients
6. SFTP deploys all artifacts to VPS /srv/enightx/downloads/
7. Deploys updated API code to /srv/enightx/production/releases/v1.0.19 and restarts systemd service
8. Broadcasts live WebSocket notification to all active POS terminals
"""

import os
import sys
import re
import time
import shutil
import hashlib
import json
import zipfile
import subprocess
import base64
from datetime import datetime, timezone
import paramiko
import urllib.request
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"
TARGET_DIR = "/srv/enightx/downloads"

WORKSPACE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CSPROJ_WPF = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/Enightx.Pos.Wpf.csproj")
CSPROJ_CORE = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos/Enightx.Pos.csproj")
SIGNING_KEY_PATH = os.path.join(WORKSPACE, "infra/.secrets/release_private_key.pem")

OUTPUT_DIR = "/tmp/enightx-release-1.0.19"
FOLDER_DIR = os.path.join(OUTPUT_DIR, "folder")
SINGLE_DIR = os.path.join(OUTPUT_DIR, "single")

VER = "1.0.19"
NOTES = "Enightx POS v1.0.19: Role-Based Professional UI Overhaul, Adaptive Dual Theme (Light/Dark), Touch-First Ergonomics, Manager PIN Guardrails & Audit, Multi-Branch Transfers & Shift Control."

SUPPORTED_FROM = [
    "1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5",
    "1.0.6", "1.0.7", "1.0.8", "1.0.9", "1.0.10", "1.0.11",
    "1.0.12", "1.0.13", "1.0.14", "1.0.15", "1.0.16", "1.0.17", "1.0.18"
]

def update_version(ver: str):
    print(f"--> Updating versions to {ver}...")
    for p in [CSPROJ_WPF, CSPROJ_CORE]:
        with open(p, "r", encoding="utf-8") as f:
            content = f.read()
        content = re.sub(r"<Version>.*?</Version>", f"<Version>{ver}</Version>", content)
        content = re.sub(r"<AssemblyVersion>.*?</AssemblyVersion>", f"<AssemblyVersion>{ver}.0</AssemblyVersion>", content)
        content = re.sub(r"<FileVersion>.*?</FileVersion>", f"<FileVersion>{ver}.0</FileVersion>", content)
        with open(p, "w", encoding="utf-8") as f:
            f.write(content)
    print("    Versions updated.")

def compute_sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()

def sftp_mkdir_p(sftp, remote_dir):
    dirs = []
    d = remote_dir
    while d and d != "/":
        dirs.append(d)
        d = os.path.dirname(d)
    for d in reversed(dirs):
        try:
            sftp.mkdir(d)
        except IOError:
            pass

def sftp_upload_dir(sftp, local_dir, remote_dir):
    sftp_mkdir_p(sftp, remote_dir)
    for root, dirs, files in os.walk(local_dir):
        if "__pycache__" in root or ".venv" in root or ".git" in root or ".pytest_cache" in root:
            continue
        rel_root = os.path.relpath(root, local_dir)
        target_root = remote_dir if rel_root == "." else os.path.join(remote_dir, rel_root)
        sftp_mkdir_p(sftp, target_root)
        for f in files:
            if f.endswith(".pyc") or f == ".DS_Store":
                continue
            local_path = os.path.join(root, f)
            remote_path = os.path.join(target_root, f)
            sftp.put(local_path, remote_path)

def run_remote(client, cmd):
    chan = client.get_transport().open_session()
    chan.exec_command(cmd)
    out = []
    while not chan.exit_status_ready():
        if chan.recv_ready():
            out.append(chan.recv(4096).decode("utf-8", errors="replace"))
        time.sleep(0.1)
    while chan.recv_ready():
        out.append(chan.recv(4096).decode("utf-8", errors="replace"))
    return chan.recv_exit_status(), "".join(out)

def main():
    print(f"==================================================")
    print(f" Building & Deploying Enightx POS v{VER}")
    print(f" Notes: {NOTES}")
    print(f"==================================================")

    shutil.rmtree(OUTPUT_DIR, ignore_errors=True)
    os.makedirs(FOLDER_DIR, exist_ok=True)
    os.makedirs(SINGLE_DIR, exist_ok=True)

    update_version(VER)

    # 1. Restore dependencies
    print("--> Restoring dependencies...")
    subprocess.run(["dotnet", "restore", CSPROJ_WPF, "-r", "win-x64"], cwd=WORKSPACE, check=True)

    # 2. Folder Publish (win-x64)
    print(f"--> Publishing folder distribution to {FOLDER_DIR} via Wine...")
    env = os.environ.copy()
    env["WINEDEBUG"] = "-all"
    rel_csproj = "apps/desktop/src/Enightx.Pos.Wpf/Enightx.Pos.Wpf.csproj"
    cmd_folder = [
        "wine", "/opt/dotnet-win/dotnet.exe", "publish", rel_csproj,
        "-c", "Release", "-r", "win-x64", "--no-restore",
        "--self-contained", "true", "-p:PublishSingleFile=false",
        "-o", FOLDER_DIR
    ]
    subprocess.run(cmd_folder, cwd=WORKSPACE, env=env, check=True)

    # Add version file & alias
    version_file_path = os.path.join(FOLDER_DIR, "enightx_version.txt")
    with open(version_file_path, "w", encoding="utf-8") as f:
        f.write(VER)

    folder_exe = os.path.join(FOLDER_DIR, "Enightx.Pos.Wpf.exe")
    folder_app_exe = os.path.join(FOLDER_DIR, "Enightx.Pos.Wpf.app.exe")
    shutil.copy2(folder_exe, folder_app_exe)
    shutil.copy2(folder_exe, os.path.join(FOLDER_DIR, "EnightxPos.exe"))

    # Copy ApplyUpdate.ps1 and release-public-key.pem
    ps1_src = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/ApplyUpdate.ps1")
    if os.path.isfile(ps1_src):
        shutil.copy2(ps1_src, os.path.join(FOLDER_DIR, "ApplyUpdate.ps1"))
    key_src = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/release-public-key.pem")
    if os.path.isfile(key_src):
        shutil.copy2(key_src, os.path.join(FOLDER_DIR, "release-public-key.pem"))

    # 3. Package Setup Zip
    setup_zip = os.path.join(OUTPUT_DIR, "EnightxPos-Setup.zip")
    print(f"--> Packaging {setup_zip}...")
    with zipfile.ZipFile(setup_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(FOLDER_DIR):
            for file in files:
                fp = os.path.join(root, file)
                rel = os.path.relpath(fp, FOLDER_DIR)
                z.write(fp, rel)
    print(f"    Setup zip size: {os.path.getsize(setup_zip) / (1024*1024):.2f} MB")

    # 4. Package Modular Update Zip
    update_zip = os.path.join(OUTPUT_DIR, "EnightxPos-Update.zip")
    print(f"--> Packaging {update_zip}...")
    with zipfile.ZipFile(update_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(FOLDER_DIR):
            for file in files:
                fp = os.path.join(root, file)
                rel = os.path.relpath(fp, FOLDER_DIR)
                if file.startswith("Enightx.") or file.endswith(".json") or file == "enightx_version.txt" or file == "ApplyUpdate.ps1" or "sqlite" in file.lower() or "resources.dll" in file.lower():
                    z.write(fp, rel)
    print(f"    Update zip size: {os.path.getsize(update_zip) / (1024*1024):.2f} MB")

    # 5. Standalone Single-File Build
    print(f"--> Publishing standalone single-file executable to {SINGLE_DIR}...")
    cmd_single = [
        "wine", "/opt/dotnet-win/dotnet.exe", "publish", rel_csproj,
        "-c", "Release", "-r", "win-x64", "--no-restore",
        "--self-contained", "true", "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", SINGLE_DIR
    ]
    subprocess.run(cmd_single, cwd=WORKSPACE, env=env, check=True)
    exe_path = os.path.join(SINGLE_DIR, "Enightx.Pos.Wpf.exe")
    shutil.copy2(exe_path, os.path.join(SINGLE_DIR, "EnightxPos.exe"))
    print(f"    Single-file size: {os.path.getsize(exe_path) / (1024*1024):.2f} MB")

    # 6. Build version.json (v1)
    exe_sha = compute_sha256(exe_path)
    zip_sha = compute_sha256(update_zip)
    manifest_v1 = {
        "version": VER,
        "Version": VER,
        "releaseNotes": NOTES,
        "ReleaseNotes": NOTES,
        "release_notes": NOTES,
        "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
        "DownloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
        "download_url": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
        "updateZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
        "UpdateZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
        "update_zip_url": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
        "setupZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
        "SetupZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
        "setup_zip_url": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
        "sha256": exe_sha,
        "Sha256": exe_sha,
        "zipSha256": zip_sha,
        "ZipSha256": zip_sha,
        "zipSizeBytes": os.path.getsize(update_zip),
        "ZipSizeBytes": os.path.getsize(update_zip),
        "publishedAtUtc": datetime.now(timezone.utc).isoformat(),
        "PublishedAtUtc": datetime.now(timezone.utc).isoformat(),
        "mandatory": True,
        "Mandatory": True
    }
    manifest_v1_path = os.path.join(OUTPUT_DIR, "version.json")
    with open(manifest_v1_path, "w", encoding="utf-8") as f:
        json.dump(manifest_v1, f, indent=2)

    # 7. Build and Sign version-v2.json (v2 Updater with differential files)
    print("--> Generating cryptographically signed version-v2.json...")
    with open(SIGNING_KEY_PATH, "rb") as kf:
        signing_key = serialization.load_pem_private_key(kf.read(), password=None)

    wpf_dll_path = os.path.join(FOLDER_DIR, "Enightx.Pos.Wpf.dll")
    pos_dll_path = os.path.join(FOLDER_DIR, "Enightx.Pos.dll")

    v2_files = [
        {
            "path": "Enightx.Pos.Wpf.exe",
            "sha256": compute_sha256(folder_exe),
            "size": os.path.getsize(folder_exe),
            "url": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.app.exe"
        },
        {
            "path": "Enightx.Pos.Wpf.dll",
            "sha256": compute_sha256(wpf_dll_path),
            "size": os.path.getsize(wpf_dll_path),
            "url": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.dll"
        },
        {
            "path": "Enightx.Pos.dll",
            "sha256": compute_sha256(pos_dll_path),
            "size": os.path.getsize(pos_dll_path),
            "url": "https://posapi.eightexms.site/downloads/Enightx.Pos.dll"
        },
        {
            "path": "enightx_version.txt",
            "sha256": compute_sha256(version_file_path),
            "size": os.path.getsize(version_file_path),
            "url": "https://posapi.eightexms.site/downloads/enightx_version.txt"
        }
    ]

    manifest_v2_core = {
        "protocolVersion": 2,
        "releaseId": VER,
        "version": VER,
        "releaseNotes": NOTES,
        "supportedFrom": SUPPORTED_FROM,
        "databaseSchema": 1,
        "files": v2_files
    }

    payload_bytes = json.dumps(manifest_v2_core, sort_keys=True, separators=(",", ":")).encode("utf-8")
    signature = signing_key.sign(payload_bytes, padding.PKCS1v15(), hashes.SHA256())

    envelope = {
        "payload": base64.b64encode(payload_bytes).decode("utf-8"),
        "signature": base64.b64encode(signature).decode("utf-8"),
        "version": VER,
        "releaseNotes": NOTES,
        "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
        "updateZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
        "setupZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip"
    }

    manifest_v2_path = os.path.join(OUTPUT_DIR, "version-v2.json")
    with open(manifest_v2_path, "w", encoding="utf-8") as f:
        json.dump(envelope, f, indent=2)
    print("    version-v2.json signed successfully.")

    # 8. Deploy to VPS
    print(f"--> Connecting to root@{TARGET_HOST} via Paramiko...")
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=15)
    sftp = c.open_sftp()

    print("--> Uploading desktop release packages...")
    sftp.put(update_zip, f"{TARGET_DIR}/EnightxPos-Update.zip")
    sftp.put(setup_zip, f"{TARGET_DIR}/EnightxPos-Setup.zip")
    sftp.put(exe_path, f"{TARGET_DIR}/Enightx.Pos.Wpf.exe")
    sftp.put(os.path.join(SINGLE_DIR, "EnightxPos.exe"), f"{TARGET_DIR}/EnightxPos.exe")
    sftp.put(folder_app_exe, f"{TARGET_DIR}/Enightx.Pos.Wpf.app.exe")
    sftp.put(wpf_dll_path, f"{TARGET_DIR}/Enightx.Pos.Wpf.dll")
    sftp.put(pos_dll_path, f"{TARGET_DIR}/Enightx.Pos.dll")
    sftp.put(version_file_path, f"{TARGET_DIR}/enightx_version.txt")
    sftp.put(manifest_v1_path, f"{TARGET_DIR}/version.json")
    sftp.put(manifest_v2_path, f"{TARGET_DIR}/version-v2.json")
    print("    Desktop packages uploaded.")

    # 9. Deploy API code to /srv/enightx/production/releases/v1.0.19
    target_api_rel = "/srv/enightx/production/releases/v1.0.19"
    print(f"--> Deploying API to {target_api_rel}...")
    sftp_upload_dir(sftp, os.path.join(WORKSPACE, "apps/api/src"), os.path.join(target_api_rel, "apps/api/src"))
    sftp_mkdir_p(sftp, os.path.join(target_api_rel, "apps/api"))
    sftp.put(os.path.join(WORKSPACE, "apps/api/pyproject.toml"), os.path.join(target_api_rel, "apps/api/pyproject.toml"))
    sftp_upload_dir(sftp, os.path.join(WORKSPACE, "contracts"), os.path.join(target_api_rel, "contracts"))
    sftp.close()

    setup_api_cmd = f"""
cat <<'EOF' > {target_api_rel}/apps/api/.env
ENVIRONMENT=production
DEBUG=false
HOST=127.0.0.1
PORT=8010
SECRET_KEY="production_enightx_pos_api_key_secure_v1"
ALLOWED_ORIGINS=https://posapi.eightexms.site,https://pos.eightexms.site,http://localhost:3000
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_DB=enightx_pos
POSTGRES_USER=enightx_user
POSTGRES_PASSWORD=dev_password
LICENCE_SIGNING_PUBLIC_KEY="PLACEHOLDER_ED25519_PUBLIC_KEY"
ENIGHTX_RELEASE_TOKEN="secure_release_token_enightx_2026"
ENIGHTX_RELEASE_MANIFEST="/srv/enightx/downloads/version-v2.json"
EOF
cp -f {target_api_rel}/apps/api/.env {target_api_rel}/.env
chown -R enightx-srv:enightx-deploy {target_api_rel}
chmod 600 {target_api_rel}/apps/api/.env {target_api_rel}/.env
ln -sfn {target_api_rel} /srv/enightx/production/current
chmod 644 {TARGET_DIR}/*
systemctl restart enightx-pos-api
systemctl status enightx-pos-api --no-pager
"""
    print("--> Restarting API service on remote server...")
    st, out = run_remote(c, setup_api_cmd)
    print(out)

    c.close()

    # 10. Broadcast WebSocket update to live terminals
    time.sleep(2)
    print("--> Triggering live WebSocket broadcast...")
    try:
        req = urllib.request.Request(
            "https://posapi.eightexms.site/api/v1/updates/broadcast",
            data=json.dumps({"version": VER, "releaseNotes": NOTES}).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "Authorization": "Bearer secure_release_token_enightx_2026"
            }
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            print(f"    Broadcast response: {data}")
    except Exception as ex:
        print(f"    Broadcast notice: {ex}")

    print("\n✅ Enightx POS v1.0.19 successfully built and deployed to production!")

if __name__ == "__main__":
    main()
