#!/bin/sh
# 安装 pre-commit 钩子:每次提交前运行 tools/guard/precommit.py
set -e
ROOT="$(git rev-parse --show-toplevel)"
HOOK="$ROOT/.git/hooks/pre-commit"
cat > "$HOOK" <<'EOF'
#!/bin/sh
exec python3 "$(git rev-parse --show-toplevel)/tools/guard/precommit.py"
EOF
chmod +x "$HOOK"
echo "已安装 $HOOK"
