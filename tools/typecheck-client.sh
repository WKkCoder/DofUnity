#!/usr/bin/env bash
# 宿主(packages/com.dnfunity.client)的编译检查:不启动 Unity 编辑器、不依赖它的授权,几秒出结果,适合改动中途反复跑。
# 做法:把宿主源码编进一个 netstandard2.1 / C# 9 的临时程序集(tools/typecheck/Dnf.Client.TypeCheck.csproj),
#       引用 Unity 安装里的 UnityEngine*.dll,再引用 Dnf.Data / Dnf.Render / Dnf.Play / Dnf.Net 四个库的工程(与 Dnf.Client.asmdef 一致)。
# 用法: tools/typecheck-client.sh [--selftest] [-h|--help]
#   (无参数)    检查宿主源码。0 个编译错误退出 0;有错误退出 1,并打印错误行(最多前 50 行)。警告不算失败。
#   --selftest   自测("负对照"):把宿主源码复制到临时目录,分别验证:
#                1. 注入 UnityEngine 类型错误(CS0029),检查必须失败;
#                2. 注入跨程序集间接引用(Dnf.Play.RoomCombatSession.Create),非严格模式能过,默认严格模式必须报 CS0012 失败。
#                然后清理临时目录。通过退出 0,不通过退出 1。改了本工具之后、或怀疑它"没真检查"时跑。
# 环境变量:
#   UNITY_MANAGED   含 UnityEngine.CoreModule.dll 的目录;设了就只用它(优先),不对就报错,不会悄悄改用别的。
#   UNITY_EDITOR    Unity 可执行文件(默认 Hub 的 6000.3.26f1 路径,与 tools/unity-test.sh、tools/unity-build-town.sh 同一约定)。
#                   没设 UNITY_MANAGED 时,由它推出安装根(去掉末尾的 /Unity.app/Contents/MacOS/Unity),
#                   在 <安装根>/PlaybackEngines/*/Variations/mono/Managed 里找含 UnityEngine.CoreModule.dll 的目录;
#                   候选不止一个时取 UnityEngine*.dll 最多的(模块越全,越不会因缺模块而误报);
#                   这里一个都没有,再找 <安装根>/Unity.app/Contents/PlaybackEngines/*/Variations/mono/Managed(应用包内的 Mac 播放器模块)。
# 退出码: 0 通过 | 1 有编译错误(或自测不通过) | 2 环境或用法问题(找不到 Unity 引擎程序集、没有 dotnet、参数不认识、源码目录为空)
# 覆盖范围: 不定义 Unity 的编译符号,检查的是"播放器"一侧;#if UNITY_EDITOR 里的代码(目前 TownDemo.cs 两处)不会被编译,
#           那一部分仍要靠 Unity 编辑器(tools/unity-test.sh)确认。
# 其他: 不进 DnfUnity.slnx、不进 tools/verify-all.sh(没有 Unity 引擎程序集的机器上会失败)。
#       打印的路径里,仓库根目录前缀会被去掉、主目录写成 ~,输出可以直接贴进笔记。
set -euo pipefail

SELF="$(cd "$(dirname "$0")" && pwd -P)/$(basename "$0")"
ORIG_PWD="$(pwd)"
cd "$(dirname "$0")/.."
ROOT="$(pwd -P)"
. tools/env.sh

usage() {
  awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$SELF"
}

mode=check
for arg in "$@"; do
  case "$arg" in
    --selftest) mode=selftest ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数: $arg(用 --help 看用法)" >&2; exit 2 ;;
  esac
done

# ---------- 小工具 ----------
# 主目录写成 ~(只用于打印)
tilde() {
  case "$1" in
    "$HOME"/*) printf '~%s' "${1#"$HOME"}" ;;
    *) printf '%s' "$1" ;;
  esac
}
# 把一段路径转义成能放进 sed 正则的写法
esc_sed() { printf '%s' "$1" | sed -e 's/[][\\.*^$|]/\\&/g'; }
ROOT_SED="$(esc_sed "$ROOT/")"

count_engine_dlls() {
  local n=0 f
  for f in "$1"/UnityEngine*.dll; do
    if [ -f "$f" ]; then n=$((n + 1)); fi
  done
  echo "$n"
}

# 在安装根下找引擎程序集目录,找到就打印(找不到什么都不打印,也总是返回 0)
find_managed_dir() {
  local root="$1" d n best="" best_n=0
  for d in "$root"/PlaybackEngines/*/Variations/mono/Managed; do
    [ -f "$d/UnityEngine.CoreModule.dll" ] || continue
    n="$(count_engine_dlls "$d")"
    if [ "$n" -gt "$best_n" ]; then best="$d"; best_n="$n"; fi
  done
  if [ -z "$best" ]; then
    for d in "$root"/Unity.app/Contents/PlaybackEngines/*/Variations/mono/Managed; do
      [ -f "$d/UnityEngine.CoreModule.dll" ] || continue
      n="$(count_engine_dlls "$d")"
      if [ "$n" -gt "$best_n" ]; then best="$d"; best_n="$n"; fi
    done
  fi
  if [ -n "$best" ]; then printf '%s\n' "$best"; fi
  return 0
}

unity_help() {
  cat <<'EOF'
怎么设(二选一):
  export UNITY_MANAGED=<含 UnityEngine.CoreModule.dll 的目录>
      例:<Unity 安装目录>/PlaybackEngines/WindowsStandaloneSupport/Variations/mono/Managed
  export UNITY_EDITOR=<Unity 可执行文件>
      与 tools/unity-test.sh 同一约定(默认 /Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity);
      本工具去掉末尾的 /Unity.app/Contents/MacOS/Unity 得到安装根,再找 PlaybackEngines/*/Variations/mono/Managed。
本工具不启动 Unity,也不需要编辑器授权,只读取引擎程序集(UnityEngine*.dll)。
EOF
}

# ---------- 定位 Unity 引擎程序集 ----------
if [ -n "${UNITY_MANAGED:-}" ]; then
  MANAGED="$UNITY_MANAGED"
  case "$MANAGED" in /*) ;; *) MANAGED="$ORIG_PWD/$MANAGED" ;; esac
  if [ ! -f "$MANAGED/UnityEngine.CoreModule.dll" ]; then
    echo "错误:UNITY_MANAGED 指向的目录里没有 UnityEngine.CoreModule.dll: $(tilde "$UNITY_MANAGED")" >&2
    unity_help >&2
    exit 2
  fi
else
  UNITY="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity}"
  UNITY_ROOT="${UNITY%/Unity.app/Contents/MacOS/Unity}"
  MANAGED="$(find_managed_dir "$UNITY_ROOT")"
  if [ -z "$MANAGED" ]; then
    echo "错误:找不到 Unity 引擎程序集(UnityEngine.CoreModule.dll)。按 UNITY_EDITOR 推出的安装根是: $(tilde "$UNITY_ROOT")" >&2
    unity_help >&2
    exit 2
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "错误:找不到 dotnet,需要 .NET SDK(用户级安装见 tools/env.sh)。" >&2
  exit 2
fi

PROJ="tools/typecheck/Dnf.Client.TypeCheck.csproj"
CLIENT_SRC="$ROOT/packages/com.dnfunity.client/Runtime"
N_CS="$(find "$CLIENT_SRC" -name '*.cs' -type f 2>/dev/null | wc -l | tr -d ' ')"
if [ "$N_CS" -eq 0 ]; then
  echo "错误:宿主源码目录里一个 .cs 文件都没有(packages/com.dnfunity.client/Runtime);编了 0 个文件就报通过,没有意义。" >&2
  exit 2
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/dnf-typecheck.XXXXXX" 2>/dev/null)" || {
  echo "错误:无法在 $(tilde "${TMPDIR:-/tmp}") 下创建临时目录(检查环境变量 TMPDIR 与磁盘空间)。" >&2
  exit 2
}
TMP="$(cd "$TMP" && pwd -P)"
# 只清理自己建的临时目录:路径必须是上面 mktemp 按 dnf-typecheck.XXXXXX 建出来的样子,别的一概不碰
# (rm -rf 一个"理论上不会为空、不会指到别处"的变量,出过事就是删整棵树,不值得赌)
cleanup() {
  case "$TMP" in
    */dnf-typecheck.??????) rm -rf "$TMP" ;;
  esac
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
LOG="$TMP/build.log"
ERRS="$TMP/errors.txt"

# ---------- 编译与错误行 ----------
# build_check <源码目录> [额外 MSBuild 参数...]:编译,全部输出落到 $LOG,dotnet 的退出码放进 BUILD_CODE(本函数总是返回 0,免得 set -e 中途退出)
BUILD_CODE=0
build_check() {
  local src_dir="$1"
  shift
  BUILD_CODE=0
  dotnet build "$PROJ" -c Release -nologo -v:q -clp:NoSummary -p:NuGetAudit=false \
    -p:UnityManaged="$MANAGED" -p:ClientSourceDir="$src_dir" "$@" >"$LOG" 2>&1 || BUILD_CODE=$?
  return 0
}

# extract_errors:从 $LOG 抽出错误行(含 CS、MSB、NU 等一切 "error 代码:" 形式),按首次出现的顺序去重,去掉仓库根目录前缀,写到 $ERRS。
# 警告行同样的处理,放进 $WARNS。诊断行里的 "error"/"warning" 是 MSBuild 的固定写法,不随系统语言变化。
WARNS="$TMP/warnings.txt"
extract_errors() {
  grep -E ' error [A-Za-z]+[0-9]+: ' "$LOG" | sed -e "s|$ROOT_SED||g" | awk '!seen[$0]++' >"$ERRS" || true
  grep -E ' warning [A-Za-z]+[0-9]+: ' "$LOG" | sed -e "s|$ROOT_SED||g" | awk '!seen[$0]++' >"$WARNS" || true
}

line_count() { wc -l <"$1" | tr -d ' '; }

echo "Unity 引擎程序集: $(tilde "$MANAGED")($(count_engine_dlls "$MANAGED") 个 UnityEngine*.dll)"

# ---------- 自测:注入故意的类型错误与严格模式间接引用,检查必须失败 ----------
if [ "$mode" = selftest ]; then
  echo "== 自测 1/2:注入一个故意的类型错误,检查必须失败且含 CS0029 =="
  SRC="$TMP/src"
  mkdir -p "$SRC"
  cp -R "$CLIENT_SRC/." "$SRC/"
  # 用 UnityEngine 的类型来造错:UnityEngine 引用若没解析成功,报的会是"找不到类型"(CS0246/CS0234)而不是 CS0029,
  # 所以拿到 CS0029 同时说明了"引擎程序集真的被引用了、编译器真的在做类型检查"。
  cat >"$SRC/TypeCheckSelfTestInjection.cs" <<'EOF'
namespace Dnf.Client
{
    internal static class TypeCheckSelfTestInjection
    {
        internal static int Bad()
        {
            UnityEngine.Vector3 v = default(UnityEngine.Vector3);
            int n = v; // 故意的类型错误:Vector3 不能隐式转成 int(CS0029)
            return n;
        }
    }
}
EOF
  echo "已复制宿主源码($N_CS 个 .cs)到临时目录并注入 TypeCheckSelfTestInjection.cs"
  build_check "$SRC"
  extract_errors
  N_ERR="$(line_count "$ERRS")"
  HIT="$(grep -E 'TypeCheckSelfTestInjection\.cs\([0-9]+,[0-9]+\): error CS0029: ' "$ERRS" | head -n 1 || true)"
  if [ "$BUILD_CODE" -eq 0 ] || [ -z "$HIT" ]; then
    echo "自测不通过:注入了类型错误,检查却没有按预期失败。" >&2
    echo "  dotnet 退出码: $BUILD_CODE;错误行 $N_ERR 条;其中指向注入文件的 CS0029: $([ -n "$HIT" ] && echo 有 || echo 没有)" >&2
    if [ "$N_ERR" -gt 0 ]; then
      echo "  实际的错误行(前 10 行):" >&2
      head -n 10 "$ERRS" | sed -e "s|$(esc_sed "$TMP/src/")|<临时目录>/|g" -e 's/^/  /' >&2
    else
      echo "  日志末尾 10 行:" >&2
      tail -n 10 "$LOG" | sed -e "s|$ROOT_SED||g" -e 's/^/  /' >&2
    fi
    exit 1
  fi
  echo "检出:退出码 $BUILD_CODE,共 $N_ERR 个错误行,其中注入的 CS0029 为:"
  printf '  %s\n' "$(printf '%s' "$HIT" | sed -e "s|$(esc_sed "$TMP/src/")|<临时目录>/|g")"

  echo "== 自测 2/2:注入跨程序集间接引用(CS0012 严格模式负对照) =="
  SRC_STRICT="$TMP/src-strict"
  mkdir -p "$SRC_STRICT"
  cp -R "$CLIENT_SRC/." "$SRC_STRICT/"
  cat >"$SRC_STRICT/TypeCheckStrictSelfTestInjection.cs" <<'EOF'
namespace Dnf.Client
{
    internal static class TypeCheckStrictSelfTestInjection
    {
        internal static object TransitiveRefLeak()
        {
            return Dnf.Play.RoomCombatSession.Create(null, null);
        }
    }
}
EOF
  build_check "$SRC_STRICT" -p:DisableTransitiveProjectReferences=false
  extract_errors
  if [ "$BUILD_CODE" -ne 0 ] || [ "$(line_count "$ERRS")" -ne 0 ]; then
    echo "自测不通过:非严格模式下(DisableTransitiveProjectReferences=false)注入的间接引用应当能编过,实际退出码 ${BUILD_CODE}。" >&2
    head -n 10 "$ERRS" | sed -e "s|$(esc_sed "$TMP/src-strict/")|<临时目录>/|g" -e 's/^/  /' >&2
    exit 1
  fi
  build_check "$SRC_STRICT"
  extract_errors
  N_STRICT_ERR="$(line_count "$ERRS")"
  HIT_STRICT="$(grep -E 'TypeCheckStrictSelfTestInjection\.cs\([0-9]+,[0-9]+\): error CS0012: ' "$ERRS" | head -n 1 || true)"
  if [ "$BUILD_CODE" -eq 0 ] || [ -z "$HIT_STRICT" ]; then
    echo "自测不通过:默认严格模式下注入的间接引用应当报 CS0012 失败。" >&2
    echo "  dotnet 退出码: $BUILD_CODE;错误行 $N_STRICT_ERR 条;CS0012: $([ -n "$HIT_STRICT" ] && echo 有 || echo 没有)" >&2
    if [ "$N_STRICT_ERR" -gt 0 ]; then
      head -n 10 "$ERRS" | sed -e "s|$(esc_sed "$TMP/src-strict/")|<临时目录>/|g" -e 's/^/  /' >&2
    else
      tail -n 10 "$LOG" | sed -e "s|$ROOT_SED||g" -e 's/^/  /' >&2
    fi
    exit 1
  fi
  echo "检出(仅严格模式):退出码 $BUILD_CODE,共 $N_STRICT_ERR 个错误行,其中 CS0012 为:"
  printf '  %s\n' "$(printf '%s' "$HIT_STRICT" | sed -e "s|$(esc_sed "$TMP/src-strict/")|<临时目录>/|g")"
  echo "自测通过:普通类型错误(CS0029)与严格模式间接引用(CS0012)均被检出。(临时目录已清理)"
  exit 0
fi

# ---------- 正式检查 ----------
echo "检查宿主源码: packages/com.dnfunity.client/Runtime($N_CS 个 .cs)"
build_check "$CLIENT_SRC"
extract_errors
N_ERR="$(line_count "$ERRS")"
N_WARN="$(line_count "$WARNS")"

if [ "$BUILD_CODE" -eq 0 ] && [ "$N_ERR" -eq 0 ]; then
  echo "通过:0 个编译错误(警告 $N_WARN 条,不算失败)"
  exit 0
fi

if [ "$N_ERR" -gt 0 ]; then
  head -n 50 "$ERRS"
  if [ "$N_ERR" -gt 50 ]; then
    echo "…… 还有 $((N_ERR - 50)) 行错误没有显示(共 $N_ERR 行)"
  fi
  if ! grep -qE ' error CS[0-9]+: ' "$ERRS"; then
    echo "提示:错误里没有 CSxxxx 编译错误,更像是还原、工程或环境问题,不是宿主代码本身。"
  fi
else
  echo "构建失败(dotnet 退出码 $BUILD_CODE),但日志里没有可识别的错误行;日志末尾 20 行:"
  tail -n 20 "$LOG" | sed -e "s|$ROOT_SED||g"
fi
echo "失败:$N_ERR 个错误行(警告 $N_WARN 条,不算失败)"
exit 1
