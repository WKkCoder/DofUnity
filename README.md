# dnf-unity

完整逆向《地下城与勇士》(DNF)客户端,并用 Unity 6 LTS 重新实现,目标平台 Windows、macOS、Android、iOS。
运行时读取**用户自备**的原版数据(`Script.pvf`、`ImagePacks2/`),本仓库不包含、不分发任何原版内容。

## 现状

| 阶段 | 状态 |
|---|---|
| P1 数据层 | **C# 库与命令行已完成,全库对拍零差异**(PVF 334,998 个文件哈希逐一一致;147,273 个标记流脚本;187,365 个 `.ani`;80,328 个 IMG、9,860,711 个帧解码并校验 Adler-32;随机 1000 帧逐像素一致)。Unity 6 里编译并运行该包已验证(9 个 EditMode 测试通过,含读取真实 PVF 全部文件并校验目录树与每个文件的 CRC-32)。**像素规则已用原版客户端的屏幕输出校验**:在一张城镇截图里识别出 33 个精灵,解码结果与屏幕逐像素一致;校验同时更正了 1555 的 5 位通道展开(`round(v*255/31)`,不是位复制),见证据 E-023 |
| P2 语义目录 | 脚本语法已破解,tag 目录已生成(3,408 种);语义待标注(`.atk` 的 40 个顶层标签已从服务端加载函数读出,并有两份独立实现在全库 6,394 个文件上逐字段一致,E-047) |
| P3 动画与渲染 | 进行中:位置模型(E-024)与像素规则(E-023)已对上原版的一个城镇场景;纯 C# 合成器 `Dnf.Render` 与 `dnf render-town` 复现了同样的数字(E-027);**Unity 的 GPU 渲染路径(`Dnf.Client`)与 CPU 合成器逐字节一致**(Metal,编辑器与 macOS 播放器,含真实城镇 NPC,E-028);**NPC 朝向与帧缩放导致的水平镜像已被原版截图证实**(露娜 2,519 / 2,519 个不透明像素逐像素一致,E-029);**地图图层与叠加混合已被原版截图证实**:网关地图的背景(`[closeback]`)、光效(叠加混合,染色 alpha 随 `.ani` 帧线性插值)、近景柱子(`[close]`)与 NPC 一起合成,光效区域 39,000 / 39,000 个像素逐字节一致,整屏 65.0% 精确一致(其余是主角、界面、聊天框与两个外观未解的 NPC,E-030);GPU 叠加着色器与 CPU 逐字节一致(Metal,编辑器 20 / 20、macOS 播放器 26 / 26,E-031);其余三个平台的 GPU 未验证;**可运行的城镇演示播放器**(`tools/unity-build-town.sh`,macOS 播放器离屏渲染与 CPU 逐字节一致,E-032);下一步是主角、界面与更多场景元素 |
| P4 脚本引擎 | **基础层、对象模型与接口扩展已完成,战斗相关的内置行为未做**:Squirrel 3.2(未修改)编成原生插件(macOS、Android 已构建,Windows 与 iOS 待前置条件)+ C# 封装;212 个原版 `.nut` 全部能编译(E-033);脚本需要的 445 个原生接口、5,704 次调用的静态清单(E-034);脚本宿主按原版的加载约定跑通整条加载链(169 个脚本、0 个错误,E-035)。纯 C# 的 `Dnf.Game` 最小世界(状态机、计时事件、动画与循环标记、命中、依附体、被动对象与二进制消息)+ **510/704 个接口名字已登记(72.4%),按调用点覆盖 97.7%**(`sq_` 301/446;登记表 520 项:Verified 67、Inferred 299、Trace 154,其中 Verified 的 67 项读过客户端实现并核对,逐项依据见 [`docs/native-audit.tsv`](docs/native-audit.tsv)、E-046)。**逐技能扫描:58 个登记了技能的状态里 52 个原版脚本不经修改跑完,另有 2 个强制进入后跑完,脚本错误 0 条**(E-036 到 E-039);但跑完的 54 个里 50 个用到了只占位的接口、11 个用到了没登记的接口,所以这是'脚本不崩、流程走到头',不是与原版行为一致。未做:内置状态(跳跃落地、投掷)的物理、占位接口的真实效果、伤害公式。304 个合成数据测试通过(Game 162、Data 76、Render 37、Squirrel 22、Script 7);Unity 编辑器里 5 个真实数据测试与 .NET 的结果逐毫秒一致(只在编辑器里验证)。下一步:P5 战斗切片 |
| P6 逆向 | Ghidra 对 `DNF.exe` 的只读静态分析已完成;已定位 PVF 解密与校验例程,并发现每个文件的 `crc` 同时是 CRC-32 校验值(全库 334,998 个文件与目录树通过,E-011);**客户端的文字块已静态解码**(18,055 个,E-041),由此读出原生接口注册表(699 个 `sq_` 名字,脚本调用的 445 个里 442 个有登记,E-042)与各脚本类型的标签词表(全库标签按出现次数 99.83% 在内,E-043);客户端约 13 MB 变形或加密的代码没有碰(E-044);**接口与脚本规则深挖**:`APID_*` 常量与对象字段偏移已读出(E-045);对 67 个原生接口读了客户端实现,改正了数学插值、变量表、二进制缓冲、状态请求参数等与客户端不符之处(E-046);`.atk` 加载规则已读出(E-047) |

详见 [路线图](docs/roadmap.md)、[格式规格](docs/formats.md)、[证据台账](docs/evidence/ledger.md)、[范围与边界](docs/boundaries.md)、[逆向工作流](docs/reverse-workflow.md)。

## 目录

```
docs/        路线图、格式规格、边界、证据台账
tools/py/    Python 参考实现与扫描脚本(只读,标准库)——C# 库的对照标准
tools/guard/ 提交前守卫:挡住原版内容与反编译输出
native/      Squirrel 3.2 源码(逐字节未修改,MIT)与我们的薄封装 dnfsq(原生插件的源码)
packages/    com.dnfunity.data:Unity 包(数据层库的唯一源码);com.dnfunity.squirrel、com.dnfunity.script:Squirrel 封装与脚本宿主;com.dnfunity.game:纯 C# 的最小游戏世界与原生接口;com.dnfunity.render:纯 C# 的 CPU 合成器与城镇场景(Unity GPU 路径的参照实现);com.dnfunity.client:Unity 层(800×600 像素对齐的舞台、GPU 绘制、帧读回);com.dnfunity.squirrel:Squirrel 虚拟机的 C# 封装与各平台预编译插件;com.dnfunity.script:脚本宿主(按原版的加载约定从 PVF 运行脚本)
src/         Dnf.Data、Dnf.Render、Dnf.Squirrel、Dnf.Script、Dnf.Game(netstandard2.1 工程,链接上面的源码)、各自的 *.Tests(合成数据单元测试)、Dnf.Cli(全库对拍、画面对照与脚本检查命令行)
unity/       Unity 工程 DnfUnity(引用 packages/com.dnfunity.data;目前只有 EditMode 测试)
re/  out/    逆向产物与生成物,已被 .gitignore 排除
```

## 使用

1. 复制 `local.env.example` 为 `local.env`,把 `DNF_DIR` 改成你自己的游戏目录(里面应有 `Script.pvf` 与 `ImagePacks2/`)。
2. 安装提交守卫:`sh tools/guard/install-hooks.sh`
3. 跑参考实现(只读):

```sh
python3 tools/py/manifest.py    # PVF 文件清单与聚合哈希,期望 cb377e11…1154
python3 tools/py/scan_imgs.py   # 全量扫描 IMG v2
python3 tools/py/tag_catalog.py # tag 目录与覆盖率
python3 tools/py/ani_spec.py    # 二进制 .ani 全库验证
python3 tools/py/pe_probe.py    # DNF.exe 静态探查
python3 tools/py/pvf_crc_probe.py # PVF 每个文件与目录树的 CRC-32 校验(客户端自己的规则,E-011)
```

4. 一条命令验收 P1(单元测试 + C# 全库对拍 + 客户端校验规则,约 30 秒;参考输出缺失时会先用上面的脚本生成):

```sh
tools/verify-p1.sh
```

需要用户级 .NET SDK(`tools/env.sh` 会把 `~/.dotnet` 加进当前 shell 的 PATH)。报告写到 `out/p1_verification_all.json`。

5. 在 Unity 里跑测试(批处理模式,不需要登录):`tools/unity-test.sh`。结果在 `out/unity-EditMode-results.xml`。
   加 `UNITY_GRAPHICS=1` 会去掉 `-nographics`,让 GPU 与 CPU 合成器的对照真正运行(需要 GPU);`UNITY_GRAPHICS=1 tools/unity-build-smoke.sh macos` 在 macOS 播放器里再做一遍;`UNITY_TEST_TARGET=Android` 让编辑器按移动端精度编译着色器再做一遍(默认目标是 StandaloneOSX,E-031)。

6. 对照原版画面(需要一张原版客户端的 800×600 截图,窗口内容 1:1,放在 `re/` 下;截图不入库):

```sh
# 在全部 IMG 帧里找"不透明像素与截图完全相同"的帧,并给出它们在截图里的位置(约 20 秒)
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll locate --shot <截图.png> --min-ratio 0.99 --json out/locate.json
# 用 Python 参考实现独立复核,同时比较 1555 的两种展开规则
python3 tools/py/screen_match_check.py --shot <截图.png> --locate out/locate.json
```

7. 用我们自己的合成器画城镇里的 NPC,并与同一张原版截图逐像素对照(约 1 秒;`--png` 输出 左=我们、中=原版、右=对照 的三联图):

```sh
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --shot <截图.png> --best-frame --png re/notes/town/render_town.png
```

8. 合成整个城镇场景(地图图层、叠加光效、NPC)并与同一张截图整屏对照(光效区域逐字节一致,整屏约 65%;`--eval` 统计任意矩形;`--time` 是动画时间,光效的相位 545 与 1455 毫秒对得上,证据 E-030):

```sh
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --scene --shot <截图.png> --time 545 --eval 340,30,600,180 --png re/notes/town/scene.png
# 不带截图:只输出 800×600 的 CPU 合成结果
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --scene --png out/town-cpu-545.png --time 545
```

9. 在 Unity 里实时看这个城镇(运行时读取你自备的数据,不复制、不分发任何原版内容):

```sh
tools/unity-build-town.sh macos      # 构建 macOS 播放器;DNF_DIR 有效时还会离屏渲染一帧并与 CPU 合成器逐字节比较,再实时跑 4 秒
open unity/DnfUnity/Builds/macos/DnfTownDemo.app --args -dnfdir "<含 Script.pvf 与 ImagePacks2 的目录>"
```

   不带参数启动时窗口里有输入框,路径会记在 PlayerPrefs 里。按键:空格 暂停/继续、左右方向键 暂停时按 50 毫秒步进、H 隐藏文字、Esc 退出;画面按整数倍放大并居中。`windows`、`android` 也能构建(没有运行过);Android 把数据放在应用的持久数据目录下的 `DNF/` 文件夹。目前没有主角、名牌与界面。

10. 检查脚本引擎(需要 DNF_DIR;预编译的 macOS 与 Android 插件已在 `packages/com.dnfunity.squirrel/Plugins/`,别的平台或改了 `native/` 后用 `tools/build-squirrel.sh` 重建):

```sh
# 212 个 .nut 逐个编译,再按原版的加载约定运行入口脚本,统计登记的状态、回调与缺少的接口(约 0.3 秒;证据 E-033、E-035)
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll sq-check --json out/sq_check.json
# 用上游 Squirrel 3.2 命令行独立复核语法(第一次会检出并编译上游源码)
tools/sq-compile-check.sh
# 脚本需要哪些原生接口、各被调用多少次(证据 E-034)
python3 tools/py/sq_inventory.py --top 40
```

11. 在最小游戏世界里跑原版技能脚本(需要 DNF_DIR;约 1 到 10 秒;证据 E-037、E-038):

```sh
# IceCrash 切片:回调序列、接口调用序列、没实现的接口与接口覆盖率
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll game-check
# 逐技能扫描:对每个登记了技能的状态在假人前跑一遍,列出结果、缺的接口与只占位的接口
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll game-check --sweep --json out/game_check.json
# 只看某一个技能(状态后缀,不分大小写)的接口调用序列与回调序列
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll game-check --sweep --only frozenland
```

## 测试策略

- **合成数据测试**(可提交):测试里自己构造最小的 PVF、NPK、IMG,验证解析器。
- **全库对拍**(只在本机跑,数据不提交):C# 实现与 Python 参考实现、PVF 文件清单逐项比较;随机抽 1000 帧逐像素比较。
- **差分测试**(P3 以后):同样输入在原版客户端(Wine)与 Unity 里各跑一遍再比较。
