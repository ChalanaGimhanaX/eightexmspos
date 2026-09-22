#!/usr/bin/env python3
import os
import sys
import re
import time
import shutil
import hashlib
import json
import zipfile
import subprocess
from datetime import datetime, timezone
import paramiko
import urllib.request

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"
TARGET_DIR = "/srv/enightx/downloads"

WORKSPACE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CSPROJ_WPF = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/Enightx.Pos.Wpf.csproj")
CSPROJ_CORE = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos/Enightx.Pos.csproj")

OUTPUT_DIR = "/tmp/enightx-release-1.0.12"
FOLDER_DIR = os.path.join(OUTPUT_DIR, "folder")
SINGLE_DIR = os.path.join(OUTPUT_DIR, "single")

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

def main():
    ver = "1.0.16"
    notes = "Enightx POS v1.0.16: Full Phase 2 & 3 UI Update - Customer Credit (Naya Ledger), Held Bills, Shift Control, Returns & Refunds, Goods Receiving, Reports, and Manager PIN Gate."

    print(f"==================================================")
    print(f" Building & Deploying Enightx POS v{ver}")
    print(f"==================================================")

    shutil.rmtree(OUTPUT_DIR, ignore_errors=True)
    os.makedirs(FOLDER_DIR, exist_ok=True)
    os.makedirs(SINGLE_DIR, exist_ok=True)

    update_version(ver)

    # Restore dependencies with native dotnet
    print("--> Restoring dependencies...")
    subprocess.run(["dotnet", "restore", CSPROJ_WPF, "-r", "win-x64"], cwd=WORKSPACE, check=True)

    # 1. Folder Publish (win-x64)
    print(f"--> Publishing folder distribution to {FOLDER_DIR} via wine...")
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
    with open(os.path.join(FOLDER_DIR, "enightx_version.txt"), "w", encoding="utf-8") as f:
        f.write(ver)
    shutil.copy2(os.path.join(FOLDER_DIR, "Enightx.Pos.Wpf.exe"), os.path.join(FOLDER_DIR, "EnightxPos.exe"))
    # Copy ApplyUpdate.ps1
    ps1_src = os.path.join(WORKSPACE, "apps/desktop/src/Enightx.Pos.Wpf/ApplyUpdate.ps1")
    if os.path.isfile(ps1_src):
        shutil.copy2(ps1_src, os.path.join(FOLDER_DIR, "ApplyUpdate.ps1"))

    # 2. Package Setup Zip
    setup_zip = os.path.join(OUTPUT_DIR, "EnightxPos-Setup.zip")
    print(f"--> Packaging {setup_zip}...")
    with zipfile.ZipFile(setup_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(FOLDER_DIR):
            for file in files:
                fp = os.path.join(root, file)
                rel = os.path.relpath(fp, FOLDER_DIR)
                z.write(fp, rel)
    print(f"    Setup zip size: {os.path.getsize(setup_zip) / (1024*1024):.2f} MB")

    # 3. Package Modular Update Zip
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

    # 4. Standalone Single-File Build
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

    # 5. Create Manifest
    exe_sha = compute_sha256(exe_path)
    zip_sha = compute_sha256(update_zip)
    manifest = {
        "version": ver,
        "Version": ver,
        "releaseNotes": notes,
        "ReleaseNotes": notes,
        "release_notes": notes,
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
    manifest_path = os.path.join(OUTPUT_DIR, "version.json")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print(f"    Manifest created with version {ver}")

    # 6. Deploy to VPS
    print(f"--> Connecting to root@{TARGET_HOST}...")
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=15)
    sftp = c.open_sftp()

    print("    Uploading EnightxPos-Update.zip...")
    sftp.put(update_zip, f"{TARGET_DIR}/EnightxPos-Update.zip")
    print("    Uploading EnightxPos-Setup.zip...")
    sftp.put(setup_zip, f"{TARGET_DIR}/EnightxPos-Setup.zip")
    print("    Uploading Enightx.Pos.Wpf.exe...")
    sftp.put(exe_path, f"{TARGET_DIR}/Enightx.Pos.Wpf.exe")
    print("    Uploading EnightxPos.exe...")
    sftp.put(os.path.join(SINGLE_DIR, "EnightxPos.exe"), f"{TARGET_DIR}/EnightxPos.exe")
    print("    Uploading enightx_version.txt...")
    sftp.put(os.path.join(FOLDER_DIR, "enightx_version.txt"), f"{TARGET_DIR}/enightx_version.txt")
    print("    Uploading version.json...")
    sftp.put(manifest_path, f"{TARGET_DIR}/version.json")
    sftp.close()

    chan = c.get_transport().open_session()
    chan.exec_command(f"chmod 644 {TARGET_DIR}/* && ls -lh {TARGET_DIR}/")
    while not chan.exit_status_ready():
        time.sleep(0.2)
    print(chan.recv(4096).decode("utf-8", errors="replace"))
    c.close()

    # 7. Broadcast via WebSocket API
    print("--> Triggering real-time broadcast via API...")
    try:
        req = urllib.request.Request(
            "https://posapi.eightexms.site/api/v1/updates/broadcast",
            data=json.dumps({"version": ver, "releaseNotes": notes, "downloadUrl": "https://posapi.eightexms.site/downloads/Enightx.Pos.Wpf.exe"}).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            print(f"    Broadcast sent! Active terminals notified: {data.get('recipient_count', 0)}")
    except Exception as ex:
        print(f"    Broadcast warning: {ex}")

    print("\n✅ Release v1.0.12 successfully built and deployed!")

if __name__ == "__main__":
    main()
