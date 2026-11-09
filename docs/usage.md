# 使用手册

> 原 README 的「使用」与「测试策略」两节,2026-11-09 整理时迁到这里,**原文保留**。
> 仓库概览与最短的上手步骤见 [README](../README.md)。下面的命令都在**仓库根目录**执行。

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

全部对拍(P1、`.atk`、`.act`、怪物数值、伤害公式、Squirrel 加载链、整条流程)一条命令:`tools/verify-all.sh`(约 65 秒,结果写到 `out/verify-all.json`);再 `python3 tools/py/coverage.py --write-doc docs/coverage.md` 重写覆盖率表(M11,E-098)。

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

   不带参数启动时窗口里有输入框,路径会记在 PlayerPrefs 里。按键:方向键 让赛丽亚房间里的鬼剑士走动(按住)、空格 暂停/继续(暂停时左右方向键按 50 毫秒步进动画时间)、H 隐藏文字、Esc 退出;画面按整数倍放大并居中。`windows`、`android` 也能构建(没有运行过);Android 把数据放在应用的持久数据目录下的 `DNF/` 文件夹。目前只有赛丽亚房间(`map/elvengard/(r)gate.map`)有主角,没有名牌与界面。
   触屏:`-dnftouch` 打开触屏操作层(鼠标左键模拟一根手指),`-dnftouchscript "0.5:stick=left+run,3:stick=none"` 按时间表替手指操作,详见 [docs/mobile.md](mobile.md)。
   无人值守的开关:`-dnfautowalk 0.5:left,3:none`(按"秒:方向"的时间表替主角按键)、`-dnfhero x,y,left|right,rest|move,毫秒`(离屏渲染一帧时摆放主角)、`-dnfnohero`。
   副本房间与整座副本(证据 E-067 到 E-074;奔跑与跳跃的数值来自客户端代码,E-073;进门的落点规则也来自客户端代码,E-074):`-dnfmap map/lorien/s2.map` 播单个房间(X 键普攻);`-dnfdungeon dungeon/act1/lorien.dgn` 玩整座洛兰(构建好之后直接运行 `tools/play-dungeon.sh [explore|bot]` 就行):方向键走(左或右连按两下 = 奔跑,奔跑中按 X = 奔跑攻击)、X 键普攻、C 键跳跃(空中按 X = 跳跃攻击),怪物清完出口门洞打开,朝打开的门洞顶进去(门体是实心的,关着的门和墙只挡路)换到相邻房间,画面先淡出变黑再淡入(新房间里主角落在进来那一侧出口的识别矩形里:按客户端的规则在矩形里取随机点,种子 = 主角对象编号 × 地图编号,所以同一个人进同一张图落点固定,站不下就挪到最近的可站点;主角对象编号的来源没有查明,用了一个拟合值,`dnf arrival-check` 能列出每个出口的落点),右上角是占位小地图,首领房清空后出 CLEAR。A、S、D 三个技能快捷键能放了(E-076;对应关系是把基线 HUD 的图标逐像素比对出来的,更正了 E-075 里把 S、D 当成刀魂之卡赞与上挑的错):A = 鬼斩(原地出剑,有自己的攻击框、击倒与伤害数字、两层斩击光影)、S = 上挑(连招第三段整段重打)、D = 后跳(向后一跳,不能打人),各有冷却,屏幕上有技能冷却的文字;命中停顿也做了(E-077,静态读码,没有运行时验证:一次命中里攻击方与被打的怪物各定住同样的毫秒数,毫秒数取 `.atk` 的 `[hit info]`、没写就取角色 `.chr` 里这件武器的 `[weapon hit info]`;受击方的硬直时长仍是占位);魔法值(放技能耗蓝、不够放不出来、按 `.chr` 的速度回蓝,E-087)与原版底栏(E-088,见 [docs/hud.md](hud.md))也做了;没做:音效与语音、↓+C 与 ↑+技能键 的组合键。Z 与空格在原版热键表里是技能槽、技能栏上却没有对应的格子,不确定绑什么,所以按了没反应(空格是暂停);鬼剑士的技能没有 Squirrel 脚本,行为在客户端内置代码里(E-075、E-076)。`-dnfexplore`(逛遍所有房间再打首领)或 `-dnfbot`(直奔首领)让自动机替你打,`-dnfseed <整数>` 与 `-dnfrandomlayout` 指定战斗种子与随机选房。时间表也能按键:`-dnfautowalk "0.2:left,0.3:none,0.4:left,0.9:left+attack,2:jump"`(`attack`、`jump` 各按一下)。伤害是占位值;怪物会还手(E-086:主角有生命条与魔法条、受击硬直与击退,血量归零显示 DEAD、没有复活;`tools/play-dungeon.sh nodamage` 可以关掉还手,关掉后也就没有魔法值与底栏),没有结算界面。

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
