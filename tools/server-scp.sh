#!/usr/bin/env bash
# 从用户自己的服务器下载文件(只读方向:服务器 -> 本机)。凭据处理同 tools/server-ssh.sh。
#
# 用法: tools/server-scp.sh <远程绝对路径> <本地目标路径>
# 约定:不上传任何东西到服务器;不下载 .env、privatekey.pem、数据库文件等口令/密钥/用户数据
#       (本脚本会拒绝这些路径)。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS="$ROOT/local.secrets.env"
[[ -f "$SECRETS" ]] || { echo "缺少 $SECRETS" >&2; exit 2; }
if [[ "$(stat -f '%Lp' "$SECRETS" 2>/dev/null || stat -c '%a' "$SECRETS")" != "600" ]]; then
  echo "拒绝执行:$SECRETS 的权限必须是 600" >&2
  exit 2
fi
[[ $# -eq 2 ]] || { echo "用法: $0 <远程路径> <本地路径>" >&2; exit 2; }

REMOTE="$1"
LOCAL="$2"
case "$REMOTE" in
  *privatekey*|*.env*|*/mysql/*|*swapfile*|*id_rsa*|*.pem)
    echo "拒绝:不下载密钥、口令或数据库类文件: $REMOTE" >&2
    exit 2
    ;;
esac

# shellcheck disable=SC1090
source "$SECRETS"
: "${DNF_SERVER_IP:?}" "${DNF_SERVER_SSH_USER:?}" "${DNF_SERVER_SSH_PASSWORD:?}"

mkdir -p "$ROOT/re"
export SSHPASS="$DNF_SERVER_SSH_PASSWORD"
exec sshpass -e scp \
  -o StrictHostKeyChecking=accept-new \
  -o UserKnownHostsFile="$ROOT/re/ssh_known_hosts" \
  -o ConnectTimeout=10 \
  -o PreferredAuthentications=password \
  -o PubkeyAuthentication=no \
  "$DNF_SERVER_SSH_USER@$DNF_SERVER_IP:$REMOTE" "$LOCAL"
