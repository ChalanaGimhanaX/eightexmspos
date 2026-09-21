#!/usr/bin/env python3
"""
release_desktop.py:
Automates building and deploying desktop updates for Enightx POS.
1. Updates version in Enightx.Pos.Wpf.csproj
2. Builds modular folder distribution (win-x64)
3. Packages lightweight modular update zip (~1.2 MB)
4. Packages full setup zip (~67 MB)
5. Builds standalone single-file executable fallback (163 MB)
6. Generates version.json manifest with dual-channel metadata
7. Deploys all packages to VPS /srv/enightx/downloads/ via SFTP
8. Broadcasts real-time WebSocket update push to live terminals
9. Verifies public download & manifest URLs
"""
import os
import sys
import re
import time
import hashlib
import json
import zipfile
import subprocess
from datetime import datetime, timezone

TARGET_HOST = "5.189.170.180"
TARGET_DIR = "/srv/enightx/downloads"
WORKSPACE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CSPROJ_PATH = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/Enightx.Pos.Wpf.csproj")
CORE_CSPROJ_PATH = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos/Enightx.Pos.csproj")
OUTPUT_DIR = "/tmp/enightx-desktop-release"
FOLDER_PUBLISH_DIR = os.path.join(OUTPUT_DIR, "folder")
SINGLE_PUBLISH_DIR = os.path.join(OUTPUT_DIR, "single")

def update_csproj_version(version: str):
    for path in [CSPROJ_PATH, CORE_CSPROJ_PATH]:
        print(f"--> Updating {path} to version {version}...")
        with open(path, "r", encoding="utf-8") as f:
            content = f.read()

        content = re.sub(r"<Version>.*?</Version>", f"<Version>{version}</Version>", content)
        content = re.sub(r"<AssemblyVersion>.*?</AssemblyVersion>", f"<AssemblyVersion>{version}.0</AssemblyVersion>", content)
        content = re.sub(r"<FileVersion>.*?</FileVersion>", f"<FileVersion>{version}.0</FileVersion>", content)

        with open(path, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"    {os.path.basename(path)} updated.")

def build_folder_release():
    print(f"--> Building modular folder publish to {FOLDER_PUBLISH_DIR}...")
    os.makedirs(FOLDER_PUBLISH_DIR, exist_ok=True)
    env = os.environ.copy()
    env["DOTNET_ReadyToRun"] = "0"
    
    cmd = [
        "dotnet", "publish", CSPROJ_PATH,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-o", FOLDER_PUBLISH_DIR
    ]
    res = subprocess.run(cmd, env=env, cwd=WORKSPACE, capture_output=True, text=True)
    if res.returncode != 0:
        print("ERROR: Folder build failed!")
        print(res.stderr)
        print(res.stdout)
        sys.exit(1)
    print("    Folder build completed.")

def package_update_zip(version: str) -> str:
    zip_path = os.path.join(OUTPUT_DIR, "EnightxPos-Update.zip")
    print(f"--> Packaging modular update zip to {zip_path}...")
    
    # We include all application assemblies, app configs, and custom libraries,
    # skipping bulky static runtime DLLs (coreclr, clrjit, system runtime libs)
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for f in os.listdir(FOLDER_PUBLISH_DIR):
            p = os.path.join(FOLDER_PUBLISH_DIR, f)
            if not os.path.isfile(p):
                continue
            # Include Enightx binaries, dependencies, and sqlite native dlls
            if f.startswith("Enightx.") or f.endswith(".json") or "sqlite" in f.lower():
                z.write(p, f)
                
    size_mb = os.path.getsize(zip_path) / (1024 * 1024)
    print(f"    Modular update zip created! Size: {size_mb:.2f} MB (~{os.path.getsize(zip_path) // 1024} KB)")
    return zip_path

def package_setup_zip(version: str) -> str:
    zip_path = os.path.join(OUTPUT_DIR, "EnightxPos-Setup.zip")
    print(f"--> Packaging full setup zip to {zip_path}...")
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(FOLDER_PUBLISH_DIR):
            for f in files:
                full_p = os.path.join(root, f)
                rel_p = os.path.relpath(full_p, FOLDER_PUBLISH_DIR)
                z.write(full_p, rel_p)
    size_mb = os.path.getsize(zip_path) / (1024 * 1024)
    print(f"    Full setup zip created! Size: {size_mb:.2f} MB")
    return zip_path

def build_standalone_executable() -> str:
    print(f"--> Compiling standalone Windows single-file executable to {SINGLE_PUBLISH_DIR}...")
    os.makedirs(SINGLE_PUBLISH_DIR, exist_ok=True)
    env = os.environ.copy()
    env["DOTNET_ReadyToRun"] = "0"
    
    cmd = [
        "dotnet", "publish", CSPROJ_PATH,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", SINGLE_PUBLISH_DIR
    ]
    res = subprocess.run(cmd, env=env, cwd=WORKSPACE, capture_output=True, text=True)
    if res.returncode != 0:
        print("ERROR: Standalone single-file build failed!")
        print(res.stderr)
        print(res.stdout)
        sys.exit(1)
    
    exe_path = os.path.join(SINGLE_PUBLISH_DIR, "Enightx.Pos.Wpf.exe")
    size_mb = os.path.getsize(exe_path) / (1024 * 1024)
    print(f"    Standalone build succeeded! Executable size: {size_mb:.2f} MB")
    return exe_path

def compute_sha256(file_path: str) -> str:
    h = hashlib.sha256()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()

def create_manifest(version: str, notes: str, exe_sha256: str, update_zip_path: str) -> str:
    manifest_path = os.path.join(OUTPUT_DIR, "version.json")
    zip_sha256 = compute_sha256(update_zip_path)
    zip_size = os.path.getsize(update_zip_path)

    manifest_data = {
        "version": version,
        "releaseNotes": notes,
        "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe",
        "updateZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
        "setupZipUrl": "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
        "sha256": exe_sha256,
        "zipSha256": zip_sha256,
        "zipSizeBytes": zip_size,
        "publishedAtUtc": datetime.now(timezone.utc).isoformat(),
        "mandatory": False
    }
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest_data, f, indent=2)
    print(f"    Generated manifest version.json at {manifest_path}")
    return manifest_path

def deploy_to_vps(exe_path: str, update_zip_path: str, setup_zip_path: str, manifest_path: str):
    print(f"--> Uploading packages to {TARGET_HOST}:{TARGET_DIR} via Paramiko SFTP...")
    import paramiko
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(TARGET_HOST, username="root", password="@0517eighte@ft0517", timeout=20)

    sftp = c.open_sftp()
    print("    Uploading EnightxPos-Update.zip (~1.2 MB)...")
    sftp.put(update_zip_path, f"{TARGET_DIR}/EnightxPos-Update.zip")
    print("    Uploading EnightxPos-Setup.zip (~67 MB)...")
    sftp.put(setup_zip_path, f"{TARGET_DIR}/EnightxPos-Setup.zip")
    print("    Uploading Enightx.Pos.Wpf.exe (~160 MB)...")
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
        "https://posapi.eightexms.site/downloads/EnightxPos-Update.zip",
        "https://posapi.eightexms.site/downloads/EnightxPos-Setup.zip",
        "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe"
    ]
    for url in urls:
        res = subprocess.run(["curl", "-s", "-I", url], capture_output=True, text=True)
        first_line = res.stdout.splitlines()[0] if res.stdout else "No response"
        print(f"    {url} -> {first_line}")

def main():
    version = sys.argv[1] if len(sys.argv) > 1 else "1.0.3"
    notes = sys.argv[2] if len(sys.argv) > 2 else "Added lightweight modular zip updates (~1.2 MB) and performance improvements."

    print(f"==================================================")
    print(f" Enightx POS Desktop Release - Version {version}")
    print(f" Release Notes: {notes}")
    print(f"==================================================")

    os.makedirs(OUTPUT_DIR, exist_ok=True)
    update_csproj_version(version)
    
    # 1. Build modular folder and packages
    build_folder_release()
    update_zip_path = package_update_zip(version)
    setup_zip_path = package_setup_zip(version)

    # 2. Build standalone single-file executable fallback
    exe_path = build_standalone_executable()
    exe_sha256 = compute_sha256(exe_path)

    # 3. Create manifest
    manifest_path = create_manifest(version, notes, exe_sha256, update_zip_path)
    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest_data = json.load(f)

    # 4. Deploy to VPS
    deploy_to_vps(exe_path, update_zip_path, setup_zip_path, manifest_path)

    # 5. Broadcast to connected terminals
    broadcast_realtime_update(manifest_data)

    # 6. Verify endpoints
    verify_public_urls()
    print("\n✅ Desktop modular release completed successfully!")

if __name__ == "__main__":
    main()
