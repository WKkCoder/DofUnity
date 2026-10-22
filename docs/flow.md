# 游戏流程骨架(M9b,证据 E-093;M9d 起城镇可走、阶段之间有加载画面,证据 E-095;M9e 起从选角界面开始,证据 E-096;M10 起流程发出声音提示,证据 E-097)

目标:把"选角 → 登录进城 → 选副本 → 副本 → 结算 → 再来一次 / 换副本 / 回城"这条主线连成一个能自己跑完的循环,
并且能在没有人的环境里整条走一遍(命令行 `flow-check`、播放器 `-dnfflow -dnfbot`)。
这一步只做**骨架**:阶段怎么切、每个阶段的内容由谁提供、结算菜单的三个选项各去哪里。
副本选择界面已经换成原版的世界地图画法(M9c,见 [dungeon-select.md](dungeon-select.md));城镇是可走的区域图、阶段之间有加载画面(M9d,见 [town.md](town.md));起点是原版的选角界面(M9e,见 [char-select.md](char-select.md));登录是占位(名册在进程内,没有账号与网络)。

## 1 阶段与转移

| 阶段 | 内容 | 出口 |
|---|---|---|
| `CharacterSelect` 选角 | 没有主角的整屏界面(`CharacterSelectScreen`,M9e):原版的选角画面(底座、发光框、头像、按钮,见 [char-select.md](char-select.md)),没给素材时是占位画面 | 确认(回车、空格、点"游戏开始")→ 登录:先经过加载画面进城镇世界的第一站;"结束游戏"只是界面的结果,流程不动,由宿主退出 |
| `Loading` 加载 | 整屏加载画面(`SplashScreen`:过场图 + 上下边条 + 标题,M9d);停够时间后切到目标阶段 | 进城镇(登录、换城镇、进出网关区域、副本回城)或进副本 |
| `Town` 城镇 | 有主角的场景会话(城镇图册里的一块区域,M9d,见 [town.md](town.md));走进地图边缘的出口换区域 | 地下城入口区域里通往世界地图的出口 → 打开副本选择;其他出口 → 换区域(必要时先加载);老式的单会话城镇(`--legacy-town`)没有出口,由宿主调 `EnterSelect`(播放器里按回车)占位 |
| `DungeonSelect` 副本选择 | 没有主角的整屏界面(`DungeonSelectScreen`):有世界地图数据时画原版的世界地图(M9c);没有时是占位(深色底、像素字列表、选中行高亮、底栏提示) | 确认 → 进选中的副本;返回 → 回城镇 |
| `Dungeon` 副本 | 整座副本会话(`DungeonSession`);首领清空 3 秒后出结算菜单(E-091) | 结算菜单上选:再次挑战 → 同一座副本重开一局;选择其它地下城 → 回副本选择,选中刚打过的那座;返回城镇 → 回城镇 |

结算菜单里的"快速组队"在原版没有队伍时是灰的,流程里没有对应的去向,不会被选中。
加载画面:登录、换城镇、进出网关区域、进副本都先停在 `Loading`(规则与时长见 [town.md](town.md) §2.3,是推断与占位);没给加载画面素材的流程(`--legacy-town`、测试里的假世界)瞬间切换,与 M9b 一致。

## 2 代码

- `Dnf.Render.ISceneSource`:能给出某一时刻整屏绘制列表的东西(`Build(timeMs)`);`IHeroSession` 继承它。视图层只依赖它,所以有主角的场景与没有主角的整屏界面走同一条画图管线(Unity `TownView`、CPU 合成器)。
- `Dnf.Play.GameFlow`:流程本体,纯逻辑。构造时给城镇世界(`ITownWorld`;老式的单会话工厂用 `LegacyTownWorld` 包一层)、副本工厂(按副本路径与局序号造一局)、副本列表,可选世界地图与副本加载画面素材;`EnterTown` / `EnterSelect` / `StartDungeon` 进入某个阶段;`Poll` 看当前阶段有没有出结果(结算菜单选定、副本选择确认或返回、城镇里走进出口)并切到下一个阶段(需要加载画面的先进 `Loading`);`StageSerial` 每次切换加一,宿主靠它换视图;`History` 记录每次切换(从哪到哪、为什么);`Place` 是主角所在的城镇区域,`Splash` 是加载阶段的画面。
- `Dnf.Play.GameFlow` 也是声音源(`ISoundSource`,M10,证据 E-097):进选角界面发选角音乐,进城镇区域发那块地图 `[sound]` 里的音乐与环境声,副本与结算转发当前会话发出的提示(结算开始一声 `M_RESULT`);换阶段时上一阶段没取走的提示作废,宿主换阶段先把旧声音全停(城镇 → 城镇除外,`LastSwitchStaysInTown`)。细节见 [audio.md](audio.md) §7。
- `Dnf.Play.IRunSession`:流程对"一局副本"的全部要求——一个 `Choice`。`DungeonSession` 实现它,所以流程的逻辑可以用不带原版数据的假会话来测。
- `Dnf.Play.DungeonSelectScreen`:副本选择界面的逻辑(上下移动不循环、确认、返回、结果只认第一次、鼠标点击)、世界地图画面(M9c,`docs/dungeon-select.md`)与没有地图数据时的占位画面;`GameFlow` 构造时可以带地图数据(`WorldMapScene`)。
- `Dnf.Play.CharacterSelectScreen`(M9e):选角界面的逻辑(方向键在 6×2 的网格里移动不循环、确认、返回、鼠标点格子与按钮、结果只认第一次)、原版画面(`CharacterSelectArt`,头像 `ICharacterAvatars`,背景光束 `UiAnimation`)与没有素材时的占位画面;`GameFlow` 构造时带一个选角界面工厂才有这个阶段,`EnterCharacterSelect` 进入,`Poll` 看到 `Start` 就 `GoTown(Start, null, "login")`。
- `Dnf.Play.FlowBot`:无人值守的流程自动机。城镇里(有出口的世界)沿 `ITownWorld.TryRoute` 的路线走到地下城入口、走进通往世界地图的出口(两分钟走不到就报超时),没有出口的老式城镇站 1.5 秒再进选择;选角界面停 1.5 秒再确认选中的角色;加载阶段只推进画面时间;选择界面停 2.5 秒再确认(要比世界地图的开场动画长);副本里交给 `DungeonBot`;结算菜单出现后等 1.5 秒,按清单依次选;清单用完就算走完(以返回城镇收尾则停在城镇(地下城入口区域),否则停在最后一局的结算菜单上)。
- 播放器 `TownDemo`:`-dnfflow`(从 `-dnfflowfrom char|town|select|dungeon` 开始,默认 town;`char` 从选角界面开始,键盘方向键/回车/Esc、鼠标点格子与按钮)玩整条流程(城镇走城镇图册;`-dnflegacytown` 退回只有赛丽亚房间一张图的老行为,`-dnfnosplash` 去掉加载画面,`-dnftownarea 城镇/区域` 直接看某块区域);副本取 `-dnfdungeon`(默认洛兰);再带 `-dnfbot`(`-dnfexplore`)由 `FlowBot` 走完,结算选择按 `-dnfflowchoices`(默认 `retry,select,town`);`-dnftimescale N`(1 到 6)放大时间步,无人值守的检查用它缩短墙钟时间。每次阶段切换在日志里留一行 `DNF_FLOW`;切换时视图换新、声音全停(新副本的第一个房间提示重新起音乐)。
- 命令行 `dnf flow-check`:真实数据、固定 1/60 秒步长走完整条流程,打印每次切换与每局战绩;`--choices`、`--from char|town|select|dungeon`、`--town N`(起点城镇,默认第一座)、`--legacy-town`、`--worldmap <路径|none>`、`--explore`、`--seed`、`--no-damage`、`--shots-at … --png …`(输出当时阶段的画面)。
- 城镇可走地图与加载画面的代码(`TownAtlas`、`TownExitTracker`、`TownWorld`、`SplashScreen`、`AtlasTownWorld`、`TownBot` 等)见 [town.md](town.md) §5。

## 3 验证

- 合成数据单元测试(`GameFlowTests`、`FlowBotTests`,共 23 条):阶段转移表、三个选项各去哪里、局序号、记录次序、没选时不动、到头不循环、空列表、自动机的停止条件与确定性(不依赖帧长);M9d 又加了 `TownFlowTests` 19 条(出口换区域、加载画面规则、自动机沿出口走完整条、超时等,见 [town.md](town.md) §6);M9e 又加了 `CharacterSelectTests` 47 条,其中流程与自动机 9 条(从选角起步、确认后经登录加载画面进城、没有加载画面直接进城、结束游戏不动流程、缺工厂时报错、每次进来是新界面、登录后的城镇落脚点、自动机停留后确认并等加载画面,见 [char-select.md](char-select.md) §9)。
- 命令行 `flow-check`(真实数据,默认清单 `retry,select,town`):登录加载 → 城镇(走出口换区域、走进地下城入口)→ 选择 → 副本加载 → 副本(首领清空)→ 再次挑战 → 副本 → 选择其它地下城 → 选择 → 副本 → 返回城镇(加载),十五次阶段切换、三局副本、三次菜单选择,每局首领清空,墙钟约 1 秒;`--explore`(逛遍 6 个房间)同样走完(十五次切换);`--from select`(十次)、`--from dungeon`(九次)同样走完;`--legacy-town` 仍是 M9b 的七次切换;M9e:`--from char` 从选角界面开始(进入选角记一次、停 1.5 秒后确认、登录经加载画面进城),十六次切换,`--explore` 同样十六次,`--legacy-town --from char` 八次。
- 全套测试 1129 条通过(Squirrel 22、Script 7、Data 92、Game 369、Render 267、Play 372);Unity EditMode 44 条(34 通过、10 条要显卡的跳过、0 失败)。
- macOS 播放器(`tools/unity-build-town.sh macos`):副本选择界面离屏一帧与 CPU 合成逐字节一致(M9b),M9d 起又加了加载画面两帧与城镇图册三块区域各一帧,全部 45 帧对拍零差异(M9e 又加了选角界面 0、0.7、1.4 秒三帧);无人值守实时从选角界面开始走完整条流程(时间步 ×4,墙钟 32.9 秒),日志里十六行 `DNF_FLOW`(第一行进入选角界面、第二行登录进加载画面)、末尾 `flow=Town serial=16 runs=3 settlements=3 botDone=True choicesMade=3`、声音计数没有读失败。
- 场景音乐(M10,证据 E-097):命令行 `flow-check` 逐行打印背景音乐开始与结算音(见 [audio.md](audio.md) §7.3),任何一首读不到文件、从选角开始时第一首不是选角音乐、结算音次数不等于结算界面出现的次数都退出码 2(`--from char|town|select|dungeon`、`--explore`、`--legacy-town` 都过);播放器实时整局(静音)日志里第一首 `DNF_MUSIC` 是选角音乐、背景音乐至少开始七次、三个结算界面各一行 `DNF_RESULT_SOUND`,汇总计数与日志行数一致、读文件失败 0。对拍帧不变(45 帧零差异):离屏渲染不开声音。

## 4 推断与占位

- 城镇停留 1.5 秒、选择界面停留 2.5 秒(等开场动画放完)、结算菜单停留 1.5 秒都是自动机的占位,不是原版时间。
- 再次挑战的局序号加到战斗种子上(`种子 + 局序号`),这样每一局的随机性不同;原版怎么选下一局的随机数没有读。
- "再次挑战 = 同一座副本重开""选择其它地下城 = 回选择界面""返回城镇 = 回城镇"是按菜单文字与热键写的,没有读客户端里这三个按钮的处理函数;重开时是否重新随机选房间、保不保留怪物、冷却是否重置,没确认,目前是整局重来(新的会话、满血、冷却清零)。
- 城镇里出口的触发规则、非网关区域的落脚点、要不要加载画面的规则、加载画面停留时长都是推断或占位,见 [town.md](town.md) §2、§7;老式的单会话城镇(`--legacy-town`)进选择界面仍用回车占位。

## 5 没做

- ~~副本选择界面的原版样子~~:M9c 已做(`docs/dungeon-select.md`);选难度、中文文字、气泡样式的其余几何仍没做。
- ~~可走的艾尔文防线、副本入口触发、进城加载画面~~:M9d 已做(`docs/town.md`);加载画面里的文字(提示带、队伍加载条)与淡入淡出、小地图仍没做;~~城镇音乐~~M10 已做(见 [audio.md](audio.md) §7)。
- ~~选角界面与登录占位(基线 S02)~~:M9e 已做(`docs/char-select.md`);账号、频道、创建与删除角色、中文文字、职业头像与光环仍没做,登录要等 P7。
- 加载画面之外的过渡(淡入淡出);选了菜单之后的音乐与音效衔接(M10 起每个界面有自己的音乐,但换阶段仍是瞬间全停再起,城镇换区域除外)。
- 结算上的掉落物、评分、经验、德利拉的对话与修理(见 `docs/settlement.md`)。

## 6 复现

```
dotnet build DnfUnity.slnx -c Release
dotnet test DnfUnity.slnx -c Release
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --from char            # 从选角界面开始(登录加载 → 城镇 → … → 回城)
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check                       # 默认清单 retry,select,town(城镇走图册,有加载画面)
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --legacy-town        # 老行为:城镇只有赛丽亚房间一张图,阶段瞬间切换
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --explore --seed 2    # 自动机逛遍所有房间
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll flow-check --from select --choices select,town --shots-at 0,35 --png out/m9/flow
tools/unity-build-town.sh macos                                                 # 构建 + 对拍 + 实时走完整条流程
tools/play-dungeon.sh flow [bot|explore] [nodamage] [nosound]                   # 自己玩:走出口换区域、走进地下城入口选副本;bot 让自动机按 retry,select,town 走一遍
```

## 7 副本泛化与全库扫描(M21,证据 E-113)

流程骨架以前只在洛兰这一座副本上验证过。M21 把"一座副本怎么排布、每个房间怎么读"换成服务端读码得来的规则,并用自动机把全部 346 个副本定义各跑一局:

- **迷宫与房间池**(`DungeonMaze`、`DungeonLayout`):一份副本定义可以有多个 `[maze info]` 段落(各带可选的 `[quest connection]`),`[start map]`、`[boss map]` 是坐标对列表,`[map specification]` 指定某格能用的房间,出口码 A 的格是空格,房间地图靠自己的 `[dungeon]` 归池,`[type]` 的 normal、boss、dummy 是三个候选表,进阶祭坛类把地图编号钉在定义里,起点格兼首领格的单格副本没有首领。
- **扩展瓦片**(`RoomMap`、`RoomGrid`、`RoomScene`、`TownCamera`):约三成的地图带 `[extended tile]`,接在基础瓦片下面,房间因此比 480 像素高。可走区域、图像摆放和纵向镜头(死区 [160, 240)、载入时镜头在最下面)都按客户端的规则做;各图层纵向的滚动百分数沿用横向的(推断)。不带它的地图镜头 y 恒为 0,投影与以前相同。
- **出口触发**(`DungeonSession.HeroTriggers`):门体盖着出口那一行的左右出口看"顶进门体",其余的左右出口看主角 y 与离出口的距离(或顶着可走区域的边缘),上下出口看"向上(下)走"加占位盒压到识别矩形。**这些都是推断**,客户端里"碰到门就换房间"的代码没读到。
- **单格无首领副本**:没有首领格、也没有能通行的出口的单格副本,房间清空就算通关(推断)。
- **自动机**(`DungeonBot`、`WalkPathfinder`):目标走不近时改走格子寻路(绕障碍与悬崖),背对怪物且离得不足一步时先退再转。这是测试工具,不代表原版的移动规则。
- **扫描结果**(`tools/py/dungeon_sweep.py`,非门槛项):346 个定义里通关 293 个;其余:进阶祭坛类 13(祭坛交互没做)、卡住 19(出口落在地图外、起点区域与出口不连通、门体楔形盖住怪物、障碍物夹缝)、没有迷宫的塔与样例 4、没有登记房间的 12、战争屋 5(PvP 地图,不在范围内)。

```
. ./local.env && python3 tools/py/dungeon_sweep.py --seconds 600 --real-timeout 180 --jobs 8     # 全库扫描(约半分钟),明细在 out/dungeon-sweep.tsv
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll dungeon-check --dungeon <副本定义> --bot --explore --seconds 600 --no-damage   # 单座副本
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --scene --room --map <地图> --cam-x 0 --cam-y <镜头 y> --png <输出.png>   # 合成带扩展瓦片的房间
```
