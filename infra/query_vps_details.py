#!/usr/bin/env python3
"""
query_vps_details.py: Deep inspection of existing services on ports 8000, 5432, and package availability.
"""
import os, sys, time, paramiko

TARGET_HOST = os.getenv("VPS_HOST", "5.189.170.180")
TARGET_USER = os.getenv("VPS_USER", "root")
TARGET_PASS = os.getenv("VPS_PASS", "")

def run_cmd(client, cmd, timeout=30):
    channel = client.get_transport().open_session()
    channel.exec_command(cmd)
    out, err = [], []
    start = time.time()
    while not channel.exit_status_ready():
        while channel.recv_ready():
            out.append(channel.recv(4096))
        while channel.recv_stderr_ready():
            err.append(channel.recv_stderr(4096))
        if time.time() - start > timeout:
            channel.close()
            return -1, b"".join(out).decode("utf-8", errors="replace"), f"Timed out: {cmd}"
        time.sleep(0.1)
    while channel.recv_ready():
        out.append(channel.recv(4096))
    while channel.recv_stderr_ready():
        err.append(channel.recv_stderr(4096))
    status = channel.recv_exit_status()
    channel.close()
    return status, b"".join(out).decode("utf-8", errors="replace"), b"".join(err).decode("utf-8", errors="replace")

client = paramiko.SSHClient()
client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
client.connect(hostname=TARGET_HOST, username=TARGET_USER, password=TARGET_PASS, timeout=10)

queries = {
    "Port 8000 Process": "ps -fp 1750 || ss -tlpn | grep :8000",
    "Port 5432 Postgres": "ps -fp 1151 || ss -tlpn | grep :5432",
    "Python 3.11/3.12 in DNF": "dnf list available 'python3.1*' 2>/dev/null | head -n 30",
    "Dotnet in DNF": "dnf list available '*dotnet-sdk*' 2>/dev/null | head -n 30",
    "Postgres CLI": "which psql || echo 'psql not found'",
    "Firewall Active Zones": "firewall-cmd --get-active-zones && firewall-cmd --list-all"
}

for label, cmd in queries.items():
    print(f"\n--- {label} ---")
    st, o, e = run_cmd(client, cmd)
    if o.strip():
        print(o.strip())
    if e.strip():
        print(f"[ERR]: {e.strip()}")
    print(f"Status: {st}")

client.close()

