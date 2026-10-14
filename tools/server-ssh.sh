#!/usr/bin/env bash
# 在用户自己的服务器上执行一条远程命令(或通过 stdin 传入脚本)。
#
# 用法:
#   tools/server-ssh.sh 'uname -a'
#   tools/server-ssh.sh 'bash -s' < some-readonly-script.sh
#
# 凭据来自 local.secrets.env(已 gitignore,权限 600),经 sshpass -e 通过环境变量传递,
# 不会出现在进程命令行里。host key 记在 re/ssh_known_hosts(已 gitignore),不改动 ~/.ssh。
# 约定:本脚本只用于只读勘察与用户已批准的操作;不往服务器写公钥,不改服务器配置。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS="$ROOT/local.secrets.env"
if [[ ! -f "$SECRETS" ]]; then
  echo "缺少 $SECRETS" >&2
  exit 2
fi
if [[ "$(stat -f '%Lp' "$SECRETS" 2>/dev/null || stat -c '%a' "$SECRETS")" != "600" ]]; then
  echo "拒绝执行:$SECRETS 的权限必须是 600" >&2
  exit 2
fi

# shellcheck disable=SC1090
source "$SECRETS"
: "${DNF_SERVER_IP:?}" "${DNF_SERVER_SSH_USER:?}" "${DNF_SERVER_SSH_PASSWORD:?}"

mkdir -p "$ROOT/re"
export SSHPASS="$DNF_SERVER_SSH_PASSWORD"
exec sshpass -e ssh \
  -o StrictHostKeyChecking=accept-new \
  -o UserKnownHostsFile="$ROOT/re/ssh_known_hosts" \
  -o ConnectTimeout=10 \
  -o ServerAliveInterval=15 \
  -o PreferredAuthentications=password \
  -o PubkeyAuthentication=no \
  "$DNF_SERVER_SSH_USER@$DNF_SERVER_IP" "$@"
