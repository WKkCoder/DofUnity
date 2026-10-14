#!/usr/bin/env bash
# 服务器只读勘察:通过 tools/server-ssh.sh 'bash -s' < tools/server-recon.sh 执行。
# 只读:不写文件、不改配置、不起停服务、不提权;不打印进程命令行参数(避免带出口令)。
set +e

echo "== host"
hostname
uname -srm
( . /etc/os-release 2>/dev/null && echo "$PRETTY_NAME" )
uptime

echo "== user"
id

echo "== disk / mem"
df -h / 2>/dev/null | tail -n 2
free -m 2>/dev/null | head -n 2

echo "== listening sockets (tcp/udp)"
ss -lntu 2>/dev/null | awk 'NR==1 || /LISTEN|UNCONN/' | head -n 80

echo "== process names (comm only, counted)"
ps -eo user:14,comm --no-headers 2>/dev/null | sort | uniq -c | sort -rn | head -n 50

echo "== home dir"
ls -la ~ 2>/dev/null | head -n 40

echo "== candidate install dirs"
ls -d /home/* /opt/* /data* /srv/* /root 2>/dev/null

echo "== tools available"
for t in tcpdump tshark python3 gdb strace objdump nm file readelf ldd; do
  printf '%s: ' "$t"
  command -v "$t" || echo "-"
done

echo "== passwordless sudo? (only checks, does not escalate)"
sudo -n true 2>&1 | head -n 1
if sudo -n true 2>/dev/null; then echo "sudo: yes"; else echo "sudo: no"; fi
