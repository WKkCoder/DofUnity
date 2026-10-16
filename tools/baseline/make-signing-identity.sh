#!/usr/bin/env bash
# 为 DnfBaselineTool 建一个"稳定的本地签名身份",让重新构建后系统里的屏幕录制/辅助功能授权不再失效。
#
# 背景:ad-hoc 签名(codesign --sign -)的授权记的是代码哈希,每次重建都变,授权要重新勾。
# 用固定证书签名后,授权记的是"标识符 + 证书",重建不影响。
#
# 做法(全部在 re/baseline/signing/ 下,已 gitignore,不动用户的登录钥匙串,也不改系统的信任设置):
#   1. 自签一张只用于代码签名的证书(10 年有效);
#   2. 建一个专用钥匙串文件 dnfbaselinetool.keychain-db,随机口令存在同目录(权限 600);
#   3. 把证书与私钥导入这个钥匙串,并允许 codesign 无提示使用。
# 证书与私钥只用来签这个小工具,不对外分发,没有任何其他用途。
#
# 用法: tools/baseline/make-signing-identity.sh   (已存在则什么都不做)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIR="$ROOT/re/baseline/signing"
KC="$DIR/dnfbaselinetool.keychain-db"
NAME="DnfBaselineTool Local Signing"

if [[ -f "$KC" && -f "$DIR/keychain.pass" ]]; then
  echo "已存在: $KC"
  exit 0
fi

mkdir -p "$DIR"
chmod 700 "$DIR"
cd "$DIR"

PASS="$(openssl rand -hex 16)"
P12PASS="$(openssl rand -hex 16)"
umask 077
printf '%s' "$PASS" > keychain.pass

cat > openssl.cnf <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF

# 用系统自带的 LibreSSL 导出 PKCS12(macOS 的 security import 读不了 OpenSSL 3 默认的新格式)
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config openssl.cnf -keyout key.pem -out cert.pem
/usr/bin/openssl pkcs12 -export -inkey key.pem -in cert.pem -name "$NAME" -out identity.p12 -passout "pass:$P12PASS"

security create-keychain -p "$PASS" "$KC"
security set-keychain-settings "$KC"          # 不自动锁定、不超时
security unlock-keychain -p "$PASS" "$KC"
security import identity.p12 -k "$KC" -P "$P12PASS" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple: -s -k "$PASS" "$KC" >/dev/null

rm -f key.pem identity.p12 openssl.cnf
echo "已建立: $KC(身份名 \"$NAME\")"
security find-identity -p codesigning "$KC" || true
