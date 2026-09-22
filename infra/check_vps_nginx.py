import paramiko
import time
import sys

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"

client = paramiko.SSHClient()
client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
client.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=10)

def run_cmd(cmd):
    stdin, stdout, stderr = client.exec_command(cmd)
    out = stdout.read().decode('utf-8')
    err = stderr.read().decode('utf-8')
    code = stdout.channel.recv_exit_status()
    return code, out, err

print("=== Checking Nginx posapi config ===")
code, out, err = run_cmd("cat /etc/nginx/conf.d/posapi.eightexms.site.conf")
print(out)

print("=== Checking /srv/enightx ===")
code, out, err = run_cmd("ls -la /srv/enightx")
print(out)

client.close()

