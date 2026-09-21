#!/usr/bin/env python3
"""
inspect_vps.py: Read-only inspection of target VPS.
Connects via Paramiko SSH and queries OS, CPU, RAM, disk, firewall, open ports, and installed packages.
Uses channel exit_status_ready() to handle AlmaLinux / systemd session behavior properly.
"""
import os
import sys
import time
import paramiko

TARGET_HOST = os.getenv("VPS_HOST", "5.189.170.180")
TARGET_USER = os.getenv("VPS_USER", "root")
TARGET_PASS = os.getenv("VPS_PASS", "")

if not TARGET_PASS:
    print("ERROR: VPS_PASS environment variable is required.", file=sys.stderr)
    sys.exit(1)

def run_cmd(client, cmd, timeout=25):
    channel = client.get_transport().open_session()
    channel.exec_command(cmd)
    out = []
    err = []
    start = time.time()
    while not channel.exit_status_ready():
        while channel.recv_ready():
            out.append(channel.recv(4096))
        while channel.recv_stderr_ready():
            err.append(channel.recv_stderr(4096))
        if time.time() - start > timeout:
            channel.close()
            return -1, b"".join(out).decode("utf-8", errors="replace"), f"Command timed out after {timeout}s: {cmd}"
        time.sleep(0.1)
    while channel.recv_ready():
        out.append(channel.recv(4096))
    while channel.recv_stderr_ready():
        err.append(channel.recv_stderr(4096))
    exit_status = channel.recv_exit_status()
    channel.close()
    return exit_status, b"".join(out).decode("utf-8", errors="replace"), b"".join(err).decode("utf-8", errors="replace")

client = paramiko.SSHClient()
client.set_missing_host_key_policy(paramiko.AutoAddPolicy())

try:
    print(f"Connecting to {TARGET_USER}@{TARGET_HOST}...")
    client.connect(hostname=TARGET_HOST, username=TARGET_USER, password=TARGET_PASS, timeout=10)
    print("SSH connection established successfully.")

    commands = {
        "OS Release": "cat /etc/os-release",
        "Kernel": "uname -a",
        "Uptime": "uptime",
        "CPU & Architecture": "lscpu | grep -E 'Model name|Architecture|CPU\\(s\\):'",
        "Memory (free -m)": "free -m",
        "Disk Space (df -h)": "df -h",
        "Listening Ports": "ss -tulpn || netstat -tulpn",
        "Firewall Status": "systemctl status firewalld || ufw status || iptables -L -n -v",
        "SELinux Status": "sestatus || getenforce",
        "Installed Python": "python3 --version || python --version",
        "Installed Dotnet": "dotnet --version || echo 'dotnet not installed'",
        "Installed Git": "git --version || echo 'git not installed'",
        "Installed Docker": "docker --version || echo 'docker not installed'",
        "Package Manager": "which dnf yum apt-get 2>/dev/null",
        "Existing Users": "cut -d: -f1,3,7 /etc/passwd | grep -E '(enightx|teammate|root)'",
        "Existing /srv": "ls -la /srv"
    }

    results = {}
    for label, cmd in commands.items():
        print(f"\n{'='*20} {label} ({cmd}) {'='*20}")
        status, stdout, stderr = run_cmd(client, cmd)
        if stdout.strip():
            print(stdout.strip())
        if stderr.strip():
            print(f"[STDERR]: {stderr.strip()}")
        print(f"Exit code: {status}")
        results[label] = {"stdout": stdout.strip(), "stderr": stderr.strip(), "status": status}

finally:
    client.close()

