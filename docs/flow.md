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
| `Dungeon` 副本 | 整座副本会话(`DungeonSession`);首领清空 3 秒后出结算菜单(E-091) | 结算菜单上选:再次挑战 → 同一座副本重开一局;选择其它地下城 → 回副本选择,选中刚打过的那座;返回城镇 → 回城镇;主角死后没有复活、10 秒窗口走完 → 副本失败回城镇(不算结算,§8);Esc 系统菜单里选"放弃回城"也回城镇(§8) |

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
- ~~结算上的金币掉落与战果小结~~:M24 已做(本文 §9);物品掉落、评分、原版结算页的内容与版式、德利拉的对话与修理仍没做(见 `docs/settlement.md`)。

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
- **扫描结果**(`tools/py/dungeon_sweep.py`,非门槛项):346 个定义里通关 297 个(M22 起怪物按走位大脑走,E-114;此前 293);其余:进阶祭坛类 13(祭坛交互没做)、卡住 28(原因同下:出口落在地图外、起点区域与出口不连通、门体楔形盖住怪物、障碍物夹缝;M22 之后比此前多出的几个没有逐个归类)、没有迷宫的塔与样例 4、没有登记房间的 12、战争屋 5(PvP 地图,不在范围内)。

```
. ./local.env && python3 tools/py/dungeon_sweep.py --seconds 600 --real-timeout 180 --jobs 8     # 全库扫描(约半分钟),明细在 out/dungeon-sweep.tsv
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll dungeon-check --dungeon <副本定义> --bot --explore --seconds 600 --no-damage   # 单座副本
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --scene --room --map <地图> --cam-x 0 --cam-y <镜头 y> --png <输出.png>   # 合成带扩展瓦片的房间
```

## 8 死亡、复活与副本失败;Esc 系统菜单(M23,证据 E-115)

**主角死了怎么办**(副本里;规则来自服务端静态读码,地址见账本 E-115):

- **复活币规则**读副本定义里的三项(`[coin limit]`、`[party member coin limit]`、`[coin info]`,`DungeonCoinRules`):先看这一难度的免费复活,再依次看身上有没有币、队伍上限、难度上限、总上限;总上限没写是不限,队伍上限没写是不设;"上限 ≤ 已用次数"就算到顶。
- **10 秒窗口**(`HeroRevive`):主角躺平的那一刻打开(倒地飞行与落地不算,和服务端"躺平之后才通知死亡"一致,E-084),窗口里按攻击键使用一枚复活币——满血满蓝起身、短暂无敌、身上放一个使用复活币的动画;没有币、到上限、这一难度不能花币时窗口里换一句提示,窗口走完副本失败。躺着的主角走 `KnockdownFlight.Resurrect` 的"躺着保持一会儿再起身"路径。
- **副本失败**:`IRunSession.Failed` → `GameFlow` 回城镇(`Failures` 加一,不算结算,不动 `Settlements`);下一局照常开始。原版失败还要扣疲劳(设为 10),这一条没做。
- **复活币库存**跨整个流程(`ReviveCoins`,占位起始 5 枚——没有背包,等 M25 并入),每一局副本的"已用次数"重新数。
- **画面**(`ReviveArt`):倒计时用客户端的 `coin_countdown` 大数字、库存用 `coin_stock`、币图标用 `countdown` 的第 0 帧;原版文字(`ui.kor.str` 的 `use_coin_*`)没有 CJK 字体系统可用,提示是占位英文。
- **测试旋钮**(不是原版规则):命令行 `--coins N`(0 到 99)、`--monster-damage-scale N`,播放器 `-dnfcoins N`、`-dnfmonsterdamage N`——把怪物伤害放大到一击必杀,才能无人值守地走到死亡。

**Esc 系统菜单**(`SystemMenu`、`SystemMenuArt`、`GameFlow.ToggleMenu`):流程模式下在城镇与副本里按 Esc 打开,游戏暂停(宿主 `TownDemo` 负责暂停,菜单本身是纯逻辑);上下键或鼠标悬停选行,回车、空格、数字键或点击确定,再按 Esc 关上。副本里三项:继续 / 放弃回城 / 退出游戏;城镇里三项:继续 / 回选角 / 退出游戏。选定的项在下一次 `Poll` 里执行:放弃回城计入 `GiveUps`(不是结算也不是失败),回选角进选角界面,退出只置 `QuitRequested` 让宿主去退。换阶段时菜单自动关上;加载、选角、副本选择阶段没有系统菜单,Esc 照旧(选择界面里是返回城镇)。**版式与条目是本项目自己的占位设计**——原版 Esc 菜单的界面脚本还没读,英文文字也是占位。

**验证**:

- 合成数据单元测试:`DungeonCoinRulesTests`(判定顺序与默认值)、`HeroReviveTests`(窗口、币、上限、倒计时)、`HeroReviveSessionTests`(房间里死、复活、无敌、跨房间共用)、`ReviveArtTests`、`HeroKnockdownTests` 的复活路径、`GameFlowTests` 的失败回城、`SystemMenuTests`(菜单状态、点击判定、画法、在流程里的执行)。
- `verify-all` 的 flow 项加了两条:一击必杀且币给足时自动机死后用币复活、打完整座副本;没有币时倒下后窗口走完副本失败(退出码 2 是预期)。
- 播放器实机三段(`tools/unity-build-town.sh macos`,无人值守):无币一击必杀 → 日志里死一次、没复活、`reason="dungeon fail"`、`failures=1 settlements=0`;币给足一击必杀 → 日志里有 `DNF_REVIVE` 复活行、不失败;`-dnfmenuat 3 -dnfmenuchoose 2` → 探针打开菜单(`DNF_MENU open`)、选"放弃回城"、`reason="menu return"`、`giveups=1`。

**推断与占位(非 REA)**:复活满血满蓝、无敌 3 秒外加躺着保持的一段、倒计时 10 到 0 的显示、窗口位置、起始库存 5 枚、菜单版式与文字都是我的取值;客户端收到复活之后的处理(回血比例、无敌时长)没读。

**没做**:队伍里别人复活你、复活币从背包扣、放弃与失败的疲劳惩罚、原版系统菜单的样子与中文文字、结算页上失败后的任何提示。

## 9 金币掉落、拾取与结算战果小结(M24,证据 E-116)

**怪物死了掉不掉金币、掉多少**(规则来自服务端静态读码,函数地址与标签见账本 E-116;`Dnf.Game` 的 `MonsterDropTable`、`GoldGenTable`、`GoldDrop`):

- 数据读用户自己的 PVF:`Etc/ItemDropInfo_Monseter.etc`(概率行、难度/种类/队伍三张加成表、首领分堆的三个标签)与 `Etc/ItemDropInfo_Common.etc`(按怪物等级的基础金额与浮动);`MonsterDrops.Load` 读一次并缓存。数据缺失或读不通时什么都不掉,流程照常走。
- 击杀时掷一次:选行看**副本标准等级**(副本定义的 `[basis level]`,`GoldDrop.StandardLevel`,来源见 E-116 第 3 条),概率 = 难度加成 × 种类加成 × 队伍加成 × 行里的金币概率;随机数取 0 到 10000(含)、比概率小才掉;金额 = 基础金额 ± 浮动百分比;首领按 `[condition rate]`/`[gold quantity]`/`[gold volume]` 分成几堆。怪物种类:首领取 3,其余取 0(与经验的做法一致,推断)。
- 随机数走 `IDropRandom`(`DropRandom` 是 xorshift32,按局种子播种):只复刻范围与取法,不是服务端的随机数序列(它与客户端流程无关)。
- 没做的修正:拜金率、行情调节、队伍人数大于 1;物品掉落不在这一版(`[item drop ref table]` 只读不用)。

**掉在地上、捡起来**(`Dnf.Play` 的 `FieldDrop`、`GainPopup`、`RoomCombatSession`):

- 每堆金币从怪物身上抛出,落到周围随机的可站点(`CanStandAt` 过滤障碍与悬崖),按抛物线落地、小弹几下停住;画面按金额的四个门槛选 `Item/FieldImage.img` 的帧(客户端 0x007ad760),落在地上不会消失(没有存活时间上限,推断)。
- 主角走进范围(横向、进深各一个半宽,离地不高)就捡起:放拾取声(`GOLD_DROP1`/`GOLD_DROP2`,在捡起那一刻放,推断)、头顶飘出金额字(`Common/Etc/MoneyUp.img` 逐位拼,淡出上升)、钱进 `HeroProgress.Gold`(整个流程共用一份,`TownDemo` 的 `_progress`)。
- 整局汇总在 `DungeonSession`:`GoldDropped`、`GoldPicked`、`GoldEarned`、`ExpEarned`;首领清空后主角仍可走动,把房里剩下的金币捡完再选结算菜单。

**结算页战果小结**(`DungeonSession.AddRewardPanel`):结算菜单出现时左上角画四行占位文字(英文、像素字库,补了冒号与加号):通关用时、击杀数、经验、金币。版式是自己的设计;原版结算页的内容与版式没有读(见 [settlement.md](settlement.md))。

**自动机与验证**:

- `DungeonBot`、`FlowBot`:房里还有没捡的金币时自动机先走过去捡(含首领清空之后),捡完才去选结算菜单;这是测试工具,不代表原版行为。
- 合成数据单元测试(新增 100 条,全套 1746 条):`MonsterDropTableTests`(标签读取、选行、概率与舍入、金额浮动、首领分堆、标准等级)、`FieldDropTests`(抛物线、落点、弹跳、拾取范围、选帧与声音)、`GainPopupTests`、`HeroGoldTests`、`GoldDropSessionTests`(房间里击杀掷骰、拾取、统计、确定性)、`GoldCollectTests`(自动机捡金币、结算战果小结、字库)。
- `dungeon-check --bot` 与 `flow-check` 的末尾各打印一行金币(掉出与捡起的堆数、总额、身上有多少);`verify-all` 的流程项(选角起、副本起)断言"至少掉出一堆、掉出的都被捡起"。具体数值是用户数据,只在终端显示。
- 播放器日志有 `DNF_GOLD dropped=… picked=… earned=… purse=…` 一行(每捡一堆打一次)。
- 测试旋钮(不是原版规则):`RoomCombatSession.DropRate`、`DropDungeonLevel`、`UseDrops`、`UseDropArt`。

**推断与占位(非 REA)**:抛出的初速、重力、弹跳与静止判定、拾取范围、金币没有存活时限、拾取声音时刻、飘字的位置与动画、每只怪只掷一次、首领以外一律按种类 0、清空后可继续走动捡金币、结算战果小结的内容与版式、自动机捡金币的路线。

**没做**:物品掉落与背包(M25)、评分、结算页的原版内容、原版是否自动吸附金币、放弃回城的惩罚、金币上限与存档(M26)、中文文字。

## 10 物品掉落、地面拾取与背包(M25a,证据 E-117)

**怪物死了掉不掉物品、掉什么**(规则来自服务端静态读码,函数地址见账本 E-117;`Dnf.Game` 的 `ItemInfo`/`ItemScript`、`ItemCatalog`、`ItemPool`、`ItemDrop`、`MonsterDropTable`):

- 物品数据读用户自己的 PVF:`equipment.lst`、`stackable.lst`(编号 → 脚本路径)指到的每个 `.equ`/`.stk` 只读头部的几个标签(名字键、等级、稀有度、创建率、特殊怪物标志、类型、地上图、图标、移动音、最低等级、可用职业、价格、叠加上限);整库扫一遍约一秒,只留创建率不为 0 且不是特殊怪物才掉的,第一次用到时才扫(`MonsterDrops.Pool`,每份素材只扫一次)。
- 池按(类型 1 到 4、稀有度 0 到 5、怪物等级)分档:等级 L 的池收 `[grade]` 落在窗口 [L − 向下, L + 向上 − 1] 里的物品(窗口读掉落脚本的 `[item drop ref table]`),类型 1 = 全部可叠加物品、2 = 全部装备、3 = 设计图、4 = 宠物与圣物,以创建率为权重抽。
- 击杀时在金币之后掷:类型 1 到 4 各一遍,概率 = 队伍加成 × 种类加成 × 难度加成 × 行内基础概率(取整)× 掉落率;随机数取 0 到 10000(含)、**不大于**概率才掉(金币是"小于");掉了再掷稀有度(六个分界),最后按怪物自己的等级抽一件。每只怪一次最多四件。
- 物品用自己的随机数流(`RoomCombatSession` 的 `_itemRandom`),装上物品池不改变金币的金额与落点序列(最初共用一个流时,全库副本扫描有 4 个副本因金币落点变化翻转了通关状态)。

**掉在地上、捡进背包**(`FieldDrop`、`RoomCombatSession`、`Inventory`、`HeroProgress.Bag`):

- 物品与金币共用 `FieldDrop`(抛出、落地、弹跳、拾取范围全沿用金币的推断值);画面用物品脚本 `[field image]` 的那一帧(真实数据里都在图包里);还没有中文文字,所以地上不画名字。
- 走到就捡(**推断**:原版可能要按键),放进 `HeroProgress.Bag`:四页(装备、消耗品、材料、任务)、每页 56 格、可叠加物品同编号叠起来(缺省上限 1000)、装备一件一格——页签划分、格数、叠加上限都是推断;捡起时放物品脚本 `[move wav]` 的声音(推断)。背包放不下就留在地上(`FieldDrop.Blocked`,`ItemsBagFull` 每件只计一次),腾出地方后再走过去还能捡。
- 整局汇总在 `DungeonSession`:`ItemsDropped`、`ItemsPicked`、`ItemsBagFull`;结算页的战果小结多一行 `ITEMS n`(现在五行:用时、击杀、经验、金币、物品)。

**自动机与验证**:

- `DungeonBot` 捡完金币也捡物品(背包放不下的不追);`FlowBot` 沿用。
- 合成数据单元测试(新增 109 条,全套 1855 条):`ItemDropTests`(92 条:标签读取、类型对照、目录、掉落表新读法、概率与舍入、稀有度、池窗口、权重取法、掷骰顺序、背包)、`ItemDropSessionTests`(17 条:`FieldDrop` 装物品、房间里击杀 → 抛出 → 捡进背包、背包满、自动机、结算行、`MonsterDrops` 构造)。
- `dnf item-check [--level N] [--samples N]`:整库扫描物品脚本头、检查类型与稀有度的范围、地上图与图标的帧是否都在图包里、建池后按怪物等级抽样(数值与物品名只打印在终端);`dungeon-check --bot` 与 `flow-check` 的末尾各多一行物品(掉出、捡起、背包里几件占几格);`verify-all` 的流程项断言"物品掉出的都被捡起"并新增物品池整库扫描一项。
- 播放器日志 `DNF_ITEM dropped=… picked=… full=… bag=… slots=…`(每捡一件打一次);`unity-build-town.sh` 在有这类日志时要求最后一行掉出与捡起一样多、没有因背包满留下的。

**推断与占位(非 REA)**:拾取要不要按键、地上物品的物理与拾取范围、拾取声音、每只怪只掷一次、首领取种类 3 其余 0、背包的页签与格数与叠加上限、结算物品行的版式、自动机的捡法。

**没做**:物品名与中文文字(装备栏与背包窗口见 §11)、装备品级与强化(服务端 `generateEquipGrade`/`generateUpgrade`)、幸运点、随机选项物品、特殊怪物掉落、世界掉落、通关奖励、行情因子、使用消耗品、出售与存档(M26)。

## 11 装备栏、穿脱与背包窗口(M25b,证据 E-118)

**穿得上穿不上**(规则来自服务端静态读码,函数地址见账本 E-118;`Dnf.Game` 的 `EquipRules`、`EquipmentSlots`、`EquipActions`):

- `EquipRules.Check` 按服务端的次序判断,结果是 `EquipResult`:没有物品 → 不是装备 → 这一类没有格子(时装与宠物类,**自己加的**)→ 类型与槽不符 → 职业不符 → 等级不够。
- 职业:装备脚本的 `[usable job]` 里每个名字对应十一个职业之一(认不出的算全职业,没写的谁也穿不了);主角现在固定是剑魂(职业 0)。等级:物品最低等级不大于主角等级(溢出等级按 0)。
- 装备栏 12 格,对应部位 10 到 21(武器、称号、上衣、肩甲、下装、鞋、腰带、项链、手镯、戒指、辅助装备、魔法石)。穿上时背包里那件与装备栏里原来那件在背包原位互换;脱下要求背包装备页有空格。
- 穿上武器还多一道自己加的保护 `NotShowable`:画面上要穿得出来——能造出剑魂的外观图层,攻击 1 到 3、冲刺攻击、跳跃攻击五个动作在至少一个武器图层里有动画(真实数据里剑魂能穿的武器都满足,所以这道保护只是兜底)。

**穿上以后画面与数值怎么变**(`HeroProgress`、`HeroLook`、`HeroPanel.WornEquipment`、`RoomCombatSession`):

- 面板的装备加成 = 外观里的穿着加装备栏里每件装备的物攻、魔攻、物理暴击、魔法暴击;装备栏里有武器时,外观里默认那件武器不再计入(换的是同一个槽)。
- 外观:`AvatarOutfit.WithWeapon` 只换武器图层、保留皮肤与其它部件。战斗会话在下一步更新里按装备版本号同步:换主角的武器外观、重取跟武器走的命中停顿、命中延迟倍率与挥砍、命中声音,重算攻击面板;新进的房间、新开的副本与新换的城镇区域一开始就按装备栏造外观,城镇里穿脱由宿主直接换。

**背包窗口(`I` 键)**(`BagWindow`、`BagWindowArt`、`GameFlow.ToggleBag`):

- 只在城镇与副本里开;开着时游戏暂停(与 Esc 系统菜单共用暂停,开一个会先关另一个,阶段切换时一起关掉)。窗口里是 8 列 7 行的背包格子、四个页签,装备页旁边是 12 个装备栏格子;下方显示选中物品的说明、稀有度颜色、装备加成与结果提示。
- 键位:`I`、`Esc` 关闭,方向键移动,`Q`/`E`/`Tab` 换页签,回车或 `X` 穿脱,鼠标点一次选中、再点一次执行。
- **版式、键位与文字都是自己的占位设计**(没有读原版背包界面,没有中文文字,物品只显示图标、稀有度色与件数)。

**自动机与验证**:

- 合成数据单元测试(新增 73 条,全套 1928 条):`EquipTests`(18 条:职业与类型判断、槽位、穿脱与互换、背包满)、`BagWindowTests`(35 条:格子与页签的命中、移动与换页、点击与键位、画法)、`EquipSessionTests`(19 条:进度、面板加成、会话同步与外观切换、副本与城镇的外观)、`AvatarTests` 里 `WithWeapon` 1 条。
- `dnf equip-check [--level N] [--seed N]`:整库扫描能掉落的装备,检查类型与部位的分类、可用职业的写法、剑魂 N 级逐件的穿戴结果、剑魂能穿的武器的外观图层与五个动作的动画覆盖(`HeroLook.CanShow` 与逐动作检查对照),再按部位挑一件穿得上的全穿上、全脱下,核对装备栏、背包与面板加成的账(数值与物品名只打印在终端);`verify-all` 新增一项。
- `dnf bag-shot --png … [--level N] [--tab 0..3] [--select N] [--panel]`:把背包窗口画成一张 800×600 的图给人看(穿满装备的剑魂加一批背包物品,查版式有没有叠字、挤出格子;图里是用户自己的物品图标,只在本机看,不入库)。
- 播放器参数 `-dnfbagat S`:副本或城镇阶段开始 S 秒后探针往背包放一件剑魂穿得上的武器、像按 `I` 一样打开背包、再过一秒按回车试穿、再过一秒关上、再过一秒核对战斗会话的武器外观;日志 `DNF_BAG`(开关、变化、探针各步);`unity-build-town.sh` 多一趟"背包与装备栏"实机运行,要求这些行齐全。

**推断与占位(非 REA)**:装备栏范围限于部位 10 到 21、时装与宠物类没有格子、窗口版式与键位与英文占位文字、换外观的时机、面板加总规则、`NotShowable` 保护、脱下要求背包有空格。

**没做**:防滥用检查、溢出等级、最高可用等级、需要技能、专业职业、装备品级与强化、套装与称号效果、物品名与说明的中文、使用消耗品、出售、商店与存档(M26)。
