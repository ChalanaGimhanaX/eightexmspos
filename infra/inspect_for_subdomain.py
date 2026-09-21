#!/usr/bin/env python3
import sys
import paramiko

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"

def run():
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=15)

    commands = [
        ("Nginx Version & Config layout", "nginx -v && ls -la /etc/nginx/ && ls -la /etc/nginx/conf.d/"),
        ("Certbot check", "which certbot; certbot --version 2>&1; certbot certificates 2>&1"),
        ("Listening ports", "ss -tulpn"),
        ("PM2 status", "pm2 list 2>&1 || true"),
        ("Systemd services for enightx", "systemctl list-units 'enightx*' --all; systemctl list-unit-files 'enightx*'"),
        ("Directory /srv/enightx", "ls -la /srv/enightx/ && ls -la /srv/enightx/workspaces/teammate/enightx-pos/apps/api/"),
        ("Firewall status", "firewall-cmd --list-all 2>&1 || true"),
    ]

    for title, cmd in commands:
        print(f"=== {title} ===")
        stdin, stdout, stderr = client.exec_command(cmd)
        out = stdout.read().decode(errors='replace')
        err = stderr.read().decode(errors='replace')
        if out:
            print(out)
        if err:
            print("STDERR:", err)
        print()

    client.close()

if __name__ == "__main__":
    run()
