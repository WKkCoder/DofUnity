# 游戏流程骨架(M9b,证据 E-093)

目标:把"城镇 → 选副本 → 副本 → 结算 → 再来一次 / 换副本 / 回城"这条主线连成一个能自己跑完的循环,
并且能在没有人的环境里整条走一遍(命令行 `flow-check`、播放器 `-dnfflow -dnfbot`)。
这一步只做**骨架**:阶段怎么切、每个阶段的内容由谁提供、结算菜单的三个选项各去哪里。
每个阶段自己的画面(副本选择、城镇可走地图、选角)与原版的样子见后面"没做"一节(M9c 起)。

## 1 阶段与转移

| 阶段 | 内容 | 出口 |
|---|---|---|
| `Town` 城镇 | 有主角的场景会话(目前只有量过出生点与镜头范围的赛丽亚房间) | 打开副本选择:目前由宿主调 `EnterSelect`(播放器里按回车)占位;真实入口触发(走到副本入口)没做 |
| `DungeonSelect` 副本选择 | 没有主角的整屏界面(`DungeonSelectScreen`,目前是占位:深色底、像素字列表、选中行高亮、底栏提示) | 确认 → 进选中的副本;返回 → 回城镇 |
| `Dungeon` 副本 | 整座副本会话(`DungeonSession`);首领清空 3 秒后出结算菜单(E-091) | 结算菜单上选:再次挑战 → 同一座副本重开一局;选择其它地下城 → 回副本选择,选中刚打过的那座;返回城镇 → 回城镇 |

结算菜单里的"快速组队"在原版没有队伍时是灰的,流程里没有对应的去向,不会被选中。
阶段之间原版有加载画面(基线 S06 进图的过渡),这里先瞬间切换(没做)。

## 2 代码

- `Dnf.Render.ISceneSource`:能给出某一时刻整屏绘制列表的东西(`Build(timeMs)`);`IHeroSession` 继承它。视图层只依赖它,所以有主角的场景与没有主角的整屏界面走同一条画图管线(Unity `TownView`、CPU 合成器)。
- `Dnf.Play.GameFlow`:流程本体,纯逻辑。构造时给城镇会话工厂、副本工厂(按副本路径与局序号造一局)、副本列表;`EnterTown` / `EnterSelect` / `StartDungeon` 进入某个阶段;`Poll` 看当前阶段有没有出结果(结算菜单选定、副本选择确认或返回)并切到下一个阶段;`StageSerial` 每次切换加一,宿主靠它换视图;`History` 记录每次切换(从哪到哪、为什么)。
- `Dnf.Play.IRunSession`:流程对"一局副本"的全部要求——一个 `Choice`。`DungeonSession` 实现它,所以流程的逻辑可以用不带原版数据的假会话来测。
- `Dnf.Play.DungeonSelectScreen`:副本选择界面的逻辑(上下移动不循环、确认、返回、结果只认第一次)与占位画面。
- `Dnf.Play.FlowBot`:无人值守的流程自动机。城镇里站 1.5 秒再进选择;选择界面停 1 秒再确认;副本里交给 `DungeonBot`;结算菜单出现后等 1.5 秒,按清单依次选;清单用完就算走完(以返回城镇收尾则停在城镇,否则停在最后一局的结算菜单上)。
- 播放器 `TownDemo`:`-dnfflow`(从 `-dnfflowfrom town|select|dungeon` 开始,默认 town)玩整条流程;副本取 `-dnfdungeon`(默认洛兰);再带 `-dnfbot`(`-dnfexplore`)由 `FlowBot` 走完,结算选择按 `-dnfflowchoices`(默认 `retry,select,town`);`-dnftimescale N`(1 到 6)放大时间步,无人值守的检查用它缩短墙钟时间。每次阶段切换在日志里留一行 `DNF_FLOW`;切换时视图换新、声音全停(新副本的第一个房间提示重新起音乐)。
- 命令行 `dnf flow-check`:真实数据、固定 1/60 秒步长走完整条流程,打印每次切换与每局战绩;`--choices`、`--from`、`--explore`、`--seed`、`--no-damage`、`--shots-at … --png …`(输出当时阶段的画面)。

## 3 验证

- 合成数据单元测试(`GameFlowTests`、`FlowBotTests`,共 23 条):阶段转移表、三个选项各去哪里、局序号、记录次序、没选时不动、到头不循环、空列表、自动机的停止条件与确定性(不依赖帧长)。
- 命令行 `flow-check`(真实数据,默认清单 `retry,select,town`):城镇 → 选择 → 副本(首领清空)→ 再次挑战 → 副本 → 选择其它地下城 → 选择 → 副本 → 返回城镇,七次阶段切换、三局副本、三次菜单选择,每局首领清空,墙钟 0.7 秒;`--explore`(逛遍 6 个房间)与 `--from dungeon|select` 同样走完。
- 全套测试 999 条通过(Squirrel 22、Script 7、Data 92、Game 369、Render 236、Play 273);Unity EditMode 44 条(34 通过、10 条要显卡的跳过、0 失败)。
- macOS 播放器(`tools/unity-build-town.sh macos`):副本选择界面离屏一帧与 CPU 合成逐字节一致,全部 36 帧对拍零差异(此前 35 帧,新增这一帧);无人值守实时走完整条流程(时间步 ×4,墙钟 26.8 秒),日志里七行 `DNF_FLOW`、末尾 `flow=Town serial=7 runs=3 settlements=3 botDone=True choicesMade=3`、声音计数没有读失败。

## 4 推断与占位

- 城镇停留 1.5 秒、选择界面停留 1 秒、结算菜单停留 1.5 秒都是自动机的占位,不是原版时间。
- 再次挑战的局序号加到战斗种子上(`种子 + 局序号`),这样每一局的随机性不同;原版怎么选下一局的随机数没有读。
- "再次挑战 = 同一座副本重开""选择其它地下城 = 回选择界面""返回城镇 = 回城镇"是按菜单文字与热键写的,没有读客户端里这三个按钮的处理函数;重开时是否重新随机选房间、保不保留怪物、冷却是否重置,没确认,目前是整局重来(新的会话、满血、冷却清零)。
- 城镇进选择界面用回车占位,原版是走到艾尔文防线的副本入口(基线 S05 的行走连拍里能看到那段路)。

## 5 没做

- 副本选择界面的原版样子(大地图、副本卡片、难度、底栏、按钮;基线 S05)。→ M9c
- 可走的艾尔文防线、副本入口触发、进城加载画面(基线 S03、S04、S05 行走)。→ M9d
- 选角界面与登录占位(基线 S02)。→ M9e
- 阶段之间的加载画面与过渡;选了菜单之后的音乐与音效衔接(目前是瞬间全停再起)。
- 结算上的掉落物、评分、经验、德利拉的对话与修理(见 `docs/settlement.md`)。

## 6 复现

```
dotnet build DnfUnity.slnx -c Release
dotnet test DnfUnity.slnx -c Release
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check                       # 默认清单 retry,select,town
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --explore --seed 2    # 自动机逛遍所有房间
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --from select --choices select,town --shots-at 0,35 --png out/m9/flow
tools/unity-build-town.sh macos                                                 # 构建 + 对拍 + 实时走完整条流程
tools/play-dungeon.sh flow [bot|explore] [nodamage] [nosound]                   # 自己玩:城镇里按回车打开选择界面;bot 让自动机按 retry,select,town 走一遍
```
