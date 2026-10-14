#!/usr/bin/env bash
# 服务器只读勘察(第三轮):数据目录布局、compose 配置(口令类字段脱敏)、服务端二进制的符号情况、PVF 哈希。
# 通过 tools/server-ssh.sh 'bash -s' < tools/server-recon3.sh 执行。只读。
set +e

echo "== /opt/dnf/data (names, sizes)"
sudo -n ls -la /opt/dnf/data 2>&1 | head -n 40

echo "== /opt/dnf/log (names)"
sudo -n ls -la /opt/dnf/log 2>&1 | head -n 20

echo "== compose.yaml (secret-like lines redacted)"
sudo -n sed -E 's/((PASS|PWD|KEY|SECRET|TOKEN)[A-Za-z_]*[:=] *).*/\1<redacted>/I' /opt/dnf/compose.yaml 2>&1 | head -n 60

echo "== df_game_r binary facts"
B=/opt/dnf/data/df_game_r
sudo -n ls -l "$B" 2>&1
file "$B" 2>&1 | cut -c1-300
readelf -h "$B" 2>/dev/null | sed -n '1,12p'
echo "section names:"
readelf -S -W "$B" 2>/dev/null | awk '{print $2}' | tr '\n' ' ' | cut -c1-600
echo
echo "symtab entries: $(readelf -s -W "$B" 2>/dev/null | wc -l)"
echo "dynsym entries: $(nm -D "$B" 2>/dev/null | wc -l)"
echo "debug sections: $(readelf -S -W "$B" 2>/dev/null | grep -c debug)"
echo "sample function-like symbols:"
nm -C "$B" 2>/dev/null | awk '$2 ~ /^[Tt]$/ {print $3}' | head -n 12

echo "== sha256 of server Script.pvf"
sha256sum /opt/dnf/data/Script.pvf 2>&1 | cut -c1-80
