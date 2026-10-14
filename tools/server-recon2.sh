#!/usr/bin/env bash
# 服务器只读勘察(第二轮):部署布局、容器、游戏进程的可执行文件路径与 PVF 位置。
# 通过 tools/server-ssh.sh 'bash -s' < tools/server-recon2.sh 执行。
# 只读:只用 ls / readlink / sha256sum / docker ps,不用 docker inspect(避免带出环境变量里的口令),
# 不打印进程命令行与环境变量,不写文件,不起停服务。sudo 仅用于读取 root 拥有的 /proc 与目录。
set +e

echo "== /opt/dnf"
ls -la /opt/dnf 2>&1 | head -n 40
echo "== /data"
ls -la /data 2>&1 | head -n 40

echo "== docker containers (name, image, status, ports)"
if docker ps >/dev/null 2>&1; then
  docker ps --format '{{.Names}} | {{.Image}} | {{.Status}} | {{.Ports}}' 2>&1 | head -n 30
else
  sudo -n docker ps --format '{{.Names}} | {{.Image}} | {{.Status}} | {{.Ports}}' 2>&1 | head -n 30
fi

echo "== game/db processes: exe + cwd (no args)"
for name in df_game_r df_dbmw_r zergsvr; do
  for p in $(pgrep -x "$name"); do
    exe=$(sudo -n readlink "/proc/$p/exe" 2>/dev/null)
    cwd=$(sudo -n readlink "/proc/$p/cwd" 2>/dev/null)
    echo "$name pid=$p exe=$exe cwd=$cwd"
  done
done

echo "== open pvf/npk files of df_game_r"
for p in $(pgrep -x df_game_r); do
  echo "-- pid $p"
  sudo -n ls -l "/proc/$p/fd" 2>/dev/null | grep -i -E 'pvf|npk' | head -n 10
  sudo -n grep -i -E 'pvf|npk' "/proc/$p/maps" 2>/dev/null | awk '{print $6}' | sort -u | head -n 10
done

echo "== find Script.pvf under likely roots (names and sizes only)"
sudo -n find /opt/dnf /data /var/lib/docker/overlay2 -maxdepth 8 -iname 'Script.pvf' 2>/dev/null | head -n 10 | while read -r f; do
  ls -l "$f"
done
