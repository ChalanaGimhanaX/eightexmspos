#!/usr/bin/env python3
"""
release_desktop.py:
Automates building and deploying desktop updates for Enightx POS.
1. Updates version in Enightx.Pos.Wpf.csproj
2. Compiles standalone single-file Windows executable (win-x64) on Linux
3. Computes SHA-256 hash
4. Generates version.json manifest
5. Deploys executable and version.json to VPS /srv/enightx/downloads/
6. Verifies public download & manifest URLs
"""
import os
import sys
import re
import hashlib
import json
import subprocess
from datetime import datetime, timezone

TARGET_HOST = "5.189.170.180"
TARGET_DIR = "/srv/enightx/downloads"
WORKSPACE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CSPROJ_PATH = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/Enightx.Pos.Wpf.csproj")
PUBLISH_DIR = "/tmp/wpf-release"

def update_csproj_version(version: str):
    print(f"--> Updating {CSPROJ_PATH} to version {version}...")
    with open(CSPROJ_PATH, "r", encoding="utf-8") as f:
        content = f.read()

    content = re.sub(r"<Version>.*?</Version>", f"<Version>{version}</Version>", content)
    content = re.sub(r"<AssemblyVersion>.*?</AssemblyVersion>", f"<AssemblyVersion>{version}.0</AssemblyVersion>", content)
    content = re.sub(r"<FileVersion>.*?</FileVersion>", f"<FileVersion>{version}.0</FileVersion>", content)

    with open(CSPROJ_PATH, "w", encoding="utf-8") as f:
        f.write(content)
    print("    Csproj updated.")

def build_standalone_executable():
    print(f"--> Compiling standalone Windows executable to {PUBLISH_DIR}...")
    env = os.environ.copy()
    env["DOTNET_ReadyToRun"] = "0"
    
    cmd = [
        "dotnet", "publish", CSPROJ_PATH,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", PUBLISH_DIR
    ]
    res = subprocess.run(cmd, env=env, cwd=WORKSPACE, capture_output=True, text=True)
    if res.returncode != 0:
        print("ERROR: Build failed!")
        print(res.stderr)
        print(res.stdout)
        sys.exit(1)
    
    exe_path = os.path.join(PUBLISH_DIR, "Enightx.Pos.Wpf.exe")
    if not os.path.exists(exe_path):
        print(f"ERROR: Executable not found at {exe_path}")
        sys.exit(1)

    size_mb = os.path.getsize(exe_path) / (1024 * 1024)
    print(f"    Build succeeded! Executable size: {size_mb:.2f} MB")
    return exe_path

def compute_sha256(file_path: str) -> str:
    h = hashlib.sha256()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()

def create_manifest(version: str, notes: str, sha256: str) -> str:
    manifest_path = os.path.join(PUBLISH_DIR, "version.json")
    manifest_data = {
        "version": version,
        "releaseNotes": notes,
        "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
        "sha256": sha256,
        "publishedAtUtc": datetime.now(timezone.utc).isoformat(),
        "mandatory": False
    }
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest_data, f, indent=2)
    print(f"    Generated manifest version.json at {manifest_path}")
    return manifest_path

def deploy_to_vps(exe_path: str, manifest_path: str):
    print(f"--> Uploading files to {TARGET_HOST}:{TARGET_DIR} via Paramiko SFTP...")
    import paramiko
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(TARGET_HOST, username="root", password="@0517eighte@ft0517", timeout=15)

    sftp = c.open_sftp()
    print("    Uploading Enightx.Pos.Wpf.exe...")
    sftp.put(exe_path, f"{TARGET_DIR}/Enightx.Pos.Wpf.exe")
    print("    Uploading version.json...")
    sftp.put(manifest_path, f"{TARGET_DIR}/version.json")
    sftp.close()

    # Copy alias & fix permissions
    chan = c.get_transport().open_session()
    chan.exec_command(f"cp -f {TARGET_DIR}/Enightx.Pos.Wpf.exe {TARGET_DIR}/EnightxPos.exe && chmod 644 {TARGET_DIR}/* && ls -lh {TARGET_DIR}/")
    while not chan.exit_status_ready():
        time.sleep(0.2)
    print(chan.recv(4096).decode("utf-8", errors="replace"))
    c.close()

def broadcast_realtime_update(manifest_data: dict):
    print("--> Triggering real-time WebSocket broadcast to live terminals...")
    broadcast_url = "https://posapi.eightexms.site/api/v1/updates/broadcast"
    try:
        import urllib.request
        req = urllib.request.Request(
            broadcast_url,
            data=json.dumps(manifest_data).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            print(f"    Broadcast sent! Active live terminals notified: {data.get('recipient_count', 0)}")
    except Exception as ex:
        print(f"    Notice: Broadcast trigger returned: {ex}")

def verify_public_urls():
    print("--> Verifying public HTTPS endpoints...")
    urls = [
        "https://posapi.eightexms.site/downloads/version.json",
        "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe"
    ]
    for url in urls:
        res = subprocess.run(["curl", "-s", "-I", url], capture_output=True, text=True)
        first_line = res.stdout.splitlines()[0] if res.stdout else "No response"
        print(f"    {url} -> {first_line}")

def main():
    version = sys.argv[1] if len(sys.argv) > 1 else "1.0.2"
    notes = sys.argv[2] if len(sys.argv) > 2 else "Added real-time WebSocket live updates and instant push alerts."

    print(f"==================================================")
    print(f" Enightx POS Desktop Release - Version {version}")
    print(f" Release Notes: {notes}")
    print(f"==================================================")

    update_csproj_version(version)
    exe_path = build_standalone_executable()
    sha256 = compute_sha256(exe_path)
    print(f"    SHA-256: {sha256}")
    manifest_path = create_manifest(version, notes, sha256)
    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest_data = json.load(f)

    deploy_to_vps(exe_path, manifest_path)
    broadcast_realtime_update(manifest_data)
    verify_public_urls()
    print("\n✅ Desktop release completed successfully!")

if __name__ == "__main__":
    main()

