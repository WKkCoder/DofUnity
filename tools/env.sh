# 用法:  . tools/env.sh
# 把用户级 .NET SDK(~/.dotnet)加入当前 shell 的 PATH;不修改任何 shell 配置文件。
export DOTNET_ROOT="$HOME/.dotnet"
case ":$PATH:" in
  *":$DOTNET_ROOT:"*) ;;
  *) export PATH="$DOTNET_ROOT:$PATH" ;;
esac
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# 本机游戏目录(local.env 已被 .gitignore 排除)
if [ -f "$(dirname "${BASH_SOURCE[0]:-$0}")/../local.env" ]; then
  set -a
  . "$(dirname "${BASH_SOURCE[0]:-$0}")/../local.env"
  set +a
fi
