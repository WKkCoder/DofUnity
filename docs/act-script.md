# `.act` 动作脚本

本文只记录**格式与结构**(证据 E-050)和**已读出的语义**(证据 E-051 起)。不含任何原版脚本内容或数值;标签名、类型号、结构规则可以写,具体脚本里的数值与路径不写。

## 它是什么

`.act` 是客户端用来描述"一个对象有哪些动作、每个动作播什么动画、在哪一帧触发什么行为"的声明式脚本。全库 19,087 个:`monster` 13,115、`passiveobject` 5,633、`aicharacter` 173、`ui` 131(界面用的是另一个加载函数,下面单说)、`worldmap` 10、`npc` 9、`creature` 7、`character` 1。

- 服务端 `df_game_r` **没有** `.act` 的加载函数(符号表里只有 `importActionMailScript` 这类无关的名字;`ImportMonsterScript`、`importAIInfoScript`、`ImportPassiveObjectScript`、`ImportAttackInfoScript` 等才是服务端读的脚本)。所以 `.act` 是客户端逻辑:怪物与被动物体的动作、投射物、特效触发都在客户端算。
- 8 个 `.act` 不是记号流(格式见 `docs/formats.md`),没有检查过。

## 顶层结构

按块计,顶层只有三种常见块和几种罕见块:

| 顶层块 | 个数 | 作用(部分是推断,见证据) |
| --- | --- | --- |
| `[MOTION]` | 18,776 | 动作的动画:`[BASE ANI]` 给主动画,`[SUB ANI]` 等给叠加的子动画(装备、武器);也可含 `[SPEECH]`、`[SOUND]` 等随帧事件 |
| `[TRIGGER]` | 33,877 | 一个触发器:若干"条件"记录 + 若干 `[DO BEHAVIOR]` 记录;条件全部成立时执行行为 |
| `[BEHAVIOR]` | 32,017 | 一组行为记录;`[DO BEHAVIOR]` 里的编号 `k` 指向它(从 0 起,按出现顺序) |
| `[HOLD POSITION]` `[DEFAULT ATTACKINFO]` `[AUTOEND]` | 56、55、12 | 罕见的单项设置 |

`[TRIGGER]` 里最常见的子标签(按次数):`[DO BEHAVIOR]` 36,472、`[ME]` 33,705、`[FRAME]` 26,884、`[WHICH]` 10,386、`[CHECKUP]` 8,761、`[CHECKED NO]` 4,877、`[CHECKUP OBJECT]` 4,263、`[ENABLE]` 2,676、`[LIMIT]` 1,876、`[IF]` 1,785、`[ON SET ACTION]` 1,115、`[CHECK TIME]` 1,099、`[ON ATTACKSUCCESS]` 922。

`[DO BEHAVIOR]` 后面跟一个"对象选择"和一个行为编号:`[ME] k` 最常见(30,581 个,另有 707 个不带编号),其次 `[CHECKUP OBJECT] k`(4,024)、`[TARGET] k`(420)、`[LAST ATTACKSUCCESS] k`(354)、`[CHARACTER ATTACKSUCCESS] k`(87)、`[SUMMON MASTER] k`(63)、`[LOAD TARGET] a b`(39)等。核对:30,581 个 `[ME] k` 里 99.7% 满足 `k` 小于该文件的 `[BEHAVIOR]` 块数(即 `k` 从 0 起编号);其余 95 个正好等于或大于块数,推测是脚本自己的笔误,客户端对它们的处理没有读过。

`[BEHAVIOR]` 里最常见的子标签前 15 个合计占 79.5%:`[CREATE PASSIVEOBJECT]` 13,349、`[TRIGGER]`(嵌套触发器)6,438、`[SET ACTION]` 4,674、`[SET SPEED]` 4,356、`[DESTROY]` 2,801、`[ATTACK]` 2,484、`[CUSTOM]` 2,280、`[DIM]` 1,667、`[PARTICLE]` 1,495、`[CHANGE ACTION FILE]` 1,216、`[ATTACKRECT]` 1,110(前 15 里另有 `[NOW]`、`[OFF]`、`[ON]`、`[RESET]` 四个是别的标签的取值关键字);再往后有 `[SET DIRECTION]` 859、`[SUMMON MONSTER]` 856。

## 文法(已验证)

文件是记号流(`docs/formats.md`),标签是类型 5 的记号,方括号内的文字区分大小写。

1. 有 68 种标签**可以是块**(`Dnf.Game.Act.ActParser.BlockTags`),块以 `[/名字]` 结束;其余标签是叶子,后面跟它的值(整数、浮点、字符串、括号词、关键字)直到下一个标签。同一个标签有时是块有时是叶子(例如 `[DAMAGE]`、`[TRIGGER]` 既能是顶层块也能是行为里的嵌套块),判据只有一条:**后面有没有配得上的 `[/名字]`**。
2. 配对规则:遇到 `[/X]`,向前找最近的、尚未配对的 `[X]`;找到就配成块。找不到时:如果 `X` 不在 68 个块标签里,就当作"笔误闭合"配给最内层尚未配对的块标签(全库 31 处);否则记为多余闭合(全库 17 处),忽略。这条"笔误闭合"规则是按数据归纳的,客户端怎么处理这些笔误没有读过。
3. 嵌套最深 5 层。全库 19,079 个记号流文件共 461,931 个节点、141,168 个块;19,039 个文件完全干净,40 个含笔误闭合或多余闭合。
4. 以上结构由两份互相独立的实现得出且逐项一致:C# 的 `ActParser`(`packages/com.dnfunity.game/Runtime/Act/`,有 11 个单元测试,用合成数据)和 Python 的 `tools/py/act_tree.py`;命令 `dnf act-check --json out/act_cs.json` 与 `python3 tools/py/act_tree.py` 的"父标签→子标签→次数"表逐项相同。

## 加载函数与记录表(来自客户端 `DNF.exe` 的静态读取)

下面的名字是我们自己起的;地址是 `DNF.exe` 的虚拟地址。

- 加载函数 `importActionScript`(`0x01333530`,102 KB)。日志字符串里带源文件名 `DNFShared\GameScript\RDARScriptAction.cpp`。
- 它读出的每一条"条件"和"行为"都是一个 0x40 字节的**记录**:第一个字段是类型号;参数是记录里的一个整数向量(+0x30 起点、+0x34 终点),读取函数 `0x0107b2e0(记录, 下标)` 越界返回 -1。
- 标签→类型号有两种来源:(a) 21 个"首次使用时才建立"的静态标签字典(合计 280 个标签,其中最大的一个有 174 个标签,是行为记录的类型表);(b) 加载函数里直接的字符串比较链。两者都已整理进 [`docs/act-tags.tsv`](act-tags.tsv):
  - `behavior` 段:行为记录的类型号(例:`[SET SPEED]`=0x6b、`[SET ACTION]`=0x77、`[CREATE PASSIVEOBJECT]`=0x62、`[SUMMON MONSTER]`=0x63、`[DESTROY]`=0x73、`[ATTACKRECT]`=0x75、`[SET DIRECTION]`=0x76、`[PLAY SOUND]`=0x70、`[PARTICLE]`=0xaa、`[CHANGE ACTION FILE]`=0xb5)。
  - `trigger` 段:条件与事件的类型号(例:`[FRAME]`=0x1、`[IS INDEX]`=0x19、`[HP]`=0x1d、`[WHICH]`=0x1f、`[DISTANCE]`=0x21、`[IS IN POS]`=0x4b、`[CHECK TIME]`=0x53)。0x7 到 0xf 与 0x11 到 0x13 是事件(`[ON DAMAGE]`、`[ON ATTACKSUCCESS]`、`[ON SET ACTION]` 等),不由条件求值函数处理。
  - `selector` 段:`[WHICH]` 之后的目标选择关键字及其编号(`[ME]`=0、`[LAST ATTACKER]`=1、`[LAST ACTIVE ATTACKER]`=2、`[LAST ATTACKSUCCESS]`=3、`[CHARACTER ATTACKSUCCESS]`=4、`[LAST ATTACKSUCCESSES]`=5、`[LAST ACTIVE ATTACKSUCCESS]`=6、`[LAST ATTACKERS]`=7、`[TARGET]`=8、`[SUMMON MASTER]`=9、`[ALL OUR TEAM]`=0xb、`[ALL ENEMY]`=0xc、`[ALL CHARACTER TEAM]`=0xd、`[ALL MONSTER TEAM]`=0xe、`[CHARACTER]`=0xf、`[MONSTER]`=0x10、`[AI CHARACTER]`=0x11、`[CREATE OBJECT]`=0x12、`[DESTROY OBJECT]`=0x13、`[PASSIVE]`=0x14、`[LOAD TARGET]`=0x15、`[PARTY TARGET]`=0x16、`[PLAYER]`=0x17、`[SUMMON OBJECT]`=0x18)。
  - `object-type` 段:`[IS OBJECT TYPE]` 之后的对象类型关键字(`[COLLISION OBJECT]`=0、`[ACTIVE OBJECT]`=1、`[CHARACTER]`=2、`[MONSTER]`=3、`[AI CHARACTER]`=4、`[PASSIVE OBJECT]`=5、`[TRAP]`=6、`[BREAKABLE]`=7)。
  - `keyword` 段:其余字典里的标签与关键字(多半是某个行为块的子标签或取值),`owner` 列是按"全库里该块的子标签集合与字典的重合度"推断的,不是读出来的,可能不准。
- 条件求值函数 `CNRDAction::checkTriggerEvent`(`0x010d3630`,日志字符串里带源文件名 `CNRDActionTrigger.cpp`)与行为执行函数 `CNRDAction::doBehavior`(`0x010b56c0`,源文件名 `CNRDActionBehavior.cpp`)的名字都来自函数里的日志字符串。两者都是"记录类型号的 if 链"。
- 比较符号码(`[>=]` 等)由 `0x0132fb50` 解析,执行函数里验证过的取值:0 `==`、1 `>`、2 `<`、3 `>=`、4 `<=`、5 `!=`。
- 界面用的 `.act`(131 个)由另一组函数读:`0x01080500` 调用 `0x0107e240` 与 `0x0107f4a0`,标签有 `[IS MOUSE OVER]`、`[IS MOUSE LBCLICK]`、`[MY CONTROL]`、`[ALL CONTROLS]`、`[IS VISIBLE]`、`[IS ENABLE]` 等,与游戏对象的 `.act` 是两套记录。

## 条件的求值模型(已读)

`checkTriggerEvent(触发器记录, 目标集合)` 对"目标集合"逐个对象过滤:每条条件记录对集合里的每个对象做检查,**不满足的对象被从集合里删除**;所有条件都处理完后,集合非空才算触发成立(集合怎么产生:`[WHICH]` 选择器、`[LOAD TARGET]`、事件带来的对象)。因此 `[CHECKUP]` 不是"整体真假",而是"留下满足条件的对象"。

## 已读出的语义

逐条的"类型号 → 标签 → 一句话含义 → 置信度 → 没读通的地方"在 [`docs/act-semantics.tsv`](act-semantics.tsv)(157 行:10 个运行器、9 个共用函数、24 个目标选择器、81 个条件/事件、33 个行为),由 `tools/py/merge_cards.py` 从本机的阅读卡片索引合并而来;卡片正文只在本机(`re/`,不入库)。读到的范围按"出现次数"算:33 个行为(35 个标签)覆盖全库行为标签出现次数的 97.8%,条件与事件 79 个标签(共 91 个)覆盖 99.9%;还没读的 139 个行为标签合计只占 2.2%,多是只出现几次的特效。下面是把它们串起来的**运行模型**(证据 E-054,全部是静态读取的观察,置信度见 tsv)。

### 谁来跑触发器

动作对象(`CNRDAction`)身上有一个触发器列表(`+0xf8..+0xfc`,元素 0x24 字节;另有一个次级列表 `+0x108`,只在销毁与切换动作两类事件里扫)。**10 个运行器**各管一类事件,由事件源直接调用,没有统一的虚表:

| 运行器 | 事件 | 事件源 |
| --- | --- | --- |
| `0x010a8c30` | `[ON DAMAGE]`(7),并写"最近攻击者" `+0x98` | 受击处理 `0x0087a580` |
| `0x010aefc0` | `[ON ATTACKSUCCESS]`(9),并写"最近命中者" `+0x9c` | 命中处理 `0x008774e0` |
| `0x010aa920` | `[ON CREATE OBJECT]`(0xa),参数是新对象 | 创建对象 `0x010acdf0` |
| `0x010a9eb0` | `[ON DESTROY OBJECT]`(0xb)、`[ON DESTROY ME]`(0xc) | 销毁 `0x010ac190` |
| `0x010ab080` | `[ON SET ACTION]`(0xd);先处理次级列表里延后的 `[WHICH]` | 切换动作 `0x010aed60` |
| `0x010aba90` | `[ON END ACTION]`(0xe) | 动作结束(帧末) `0x010ace00` |
| `0x010a59e0` | `[ON SHAKE INPUT]`(0xf) | 小表 `0x01566d7c` 的槽 0 |
| `0x010a5ec0` | `[ON MOVE COLLISION]`(0x11),参数是轴下标,`[AXIS INDEX]`(0x3c)按轴掩码过滤 | 同一张小表的槽 1 |
| `0x010a97b0` | `[ON CHANGE LIFE]`(0x12) | 生命变化 `0x008202f0` |
| `0x010ad580` | 每帧的追帧(事件 0x10),内联处理 `[CHECK TIME]`(0x53,用毫秒时钟);没有"本运行器事件已出现"的标志 | `0x010ade50` |

### 一个运行器怎样跑

1. 检查拥有者有效;逐个触发器看三道门:`[ENABLE]`(`+0x1c`;`[TRIGGER] n [ON]/[OFF]` 写的就是这一字节,帧 0 复位不碰它,E-134)为 0 跳过、“已完成”位(`+0x1d`,帧 0 复位时清零)非 0 跳过、`[LIMIT]`(`+0x18`)大于 0 且已触发次数(`+0x14`)达到上限跳过。
2. 目标集合初始为 {拥有者};依次处理触发器里的每条记录(记录是 0x40 字节的堆对象,类型号在 +0,参数向量在 +0x30..+0x34):
   - `[WHICH]`(0x1f)用选择器函数 `0x010d25e0` **重新选择**目标集合;
   - `[FRAME]`(1)用 `0x010d1320`;`[CHECKED NO]`(0x27)用比较函数 `0x00977450` 比集合大小;`[SAVE TARGET OBJECT]`(0x3d)用 `0x010d1f40` 把选择结果的第一个对象存进拥有者的槽位(空选择不写,E-157);
   - `[DO BEHAVIOR]`(0)与 `[DIRECT DO BEHAVIOR]`(0x2b):本运行器的事件已经出现且集合非空(或之前 `[CHECKED NO]` 放过了空集合)时,触发次数加一,调用 `0x010d1850` 去执行第 k 个 `[BEHAVIOR]`;
   - `[DELAY DO BEHAVIOR]`(0x5c)把行为排进延迟队列(`0x006c8420`,调用点 `0x010adbc5`):判空与次数规则同 `[DO BEHAVIOR]`,行为组号或延迟为负数就不排队,排队后同样“动作状态没变就置完成字节(`+0x1d`)”;这个字节只被 `[FRAME]` 与事件触发器检查(M39,E-132);
   - 其它条件交给 `checkTriggerEvent`(`0x010d3630`)逐对象过滤。别的运行器负责的事件记录(7、8、9、0xa 到 0x12、0x58)会让这条触发器在本运行器里直接跳过。
3. 选择器共 24 个(`[ME]`=0、`[LAST ATTACKER]`=1 … `[SUMMON OBJECT]`=0x18,完整表见 `docs/act-tags.tsv` 与 tsv 的 `sel` 行);`[LAST ATTACKERS]` 全库一次也没出现。

### 条件与行为

- **条件**分两种:逐对象过滤(`[IS INDEX]`、`[IS OBJECT TYPE]`、`[HP]`、`[DISTANCE]`、`[IS IN POS]`、`[AREA]`、`[IS TEAM]` 等,不满足的对象被删出集合)和整体真假(`[IF]`、`[CHECK DISTANCE]`、`[CHECK PLAYER NUM]`、`[REALDUNGEON CHECKUP]` 等,不改集合;`[TRIGGER CHECK]` 没写族时是整体真假、写了族是逐对象过滤;`[GET TARGET]` 整理集合,工作集为空时整个不通过)。`[IS OBJECT TYPE]` 等判断用对象的种类码,是相等比较不是继承(见 `docs/engine-objects.md`)。
- **行为**由 `doBehavior` 对目标集合执行;大多数行为遍历集合,对每个对象按它的类型调不同的虚槽(例如 `[ATTACKRECT]` 开关攻击判定矩形、`[SET DIRECTION]` 设朝向、`[SET ACTION]` 按"动作族码"换内置状态)。`[SET ACTION]`(0x77)把 `[STAND]`、`[ATTACK] n`、`[CUSTOM] n`、`[DAMAGE]` 等写成内置状态号,再写进拥有者的"待切换状态"(`+0x400/+0x404`),实际切换发生在对象自己的状态机里——(推断)所以 `.act` 主要是把事件接到状态切换与对象创建上,具体的战斗逻辑在对象自己的状态机、Squirrel 脚本和成百个 C++ 子类里。
- 行为里有些参数是**值表达式**(可带随机、运算,由 `valueOf` 求值)和**符号码**(`[>=]` 之类的比较码,见上)。

### 没读通的地方

- 触发器 0x24 字节里 `+0x00..+0x0b`(记录向量)与 `+0x0c..+0x13`、`+0x1e..+0x23` 的含义;
- 受保护区域里的几个调用(行为 `0x6e`、`0xee`、`0xdf`、`0x8d` 的一部分,以及评估函数的两处),按约定当黑盒;
- 值表达式 `valueOf` 的完整语法、延迟队列的消费端(`0x006c8420` 内部与每帧出队)与取消语义、`[SET ACTION]` 的完整"关键字 → 族码 → 内置状态号"表(`docs/act-semantics.tsv` 的 `open` 列逐条列出)。

### 现状:动画层(M36,E-128)与触发器运行期(M36-4,E-130)

怪物 `.mob` 里的 `[waiting action]`、`[move action]`、`[damage action]`、`[down action]`、`[overturn action]`、`[attack action]`、`[action list]` 指向的 `.act` 现在会被读:取 `[MOTION]` 里的 `[BASE ANI]` 当作该动作的动画(`MonsterModel`),`[TRIGGER]` 的 `[ENABLE]` 初值给 `.ai` 的 `is attack action enable()` 用(运行期的开关跨出招保留,见文末 M41 一段);旧的 `[waiting motion]` 一类写法优先于新写法。触发器运行期(`[SET ACTION]`、`[CREATE PASSIVEOBJECT]`、`[SET SPEED]`、触发器开关等)在 M36-4 做了(见下),`[SUB ANI]` 叠加层与 `[SOUND]` 声音事件还没做;脚本怪物出场之后全库副本扫描里新增的未通关副本(放大 5000 倍时约 8 到 9 座)卡在这里(变身循环、隐藏与出现状态、阶段分身、走路动画没有受击框的首领)。


**触发器运行期(M36-4,E-130)**:`Dnf.Game.Act.ActCompiler` 把语法树编成类型化的 `ActProgram`,`ActRunner` 按上面的模型运行——第 0 帧复位是**边沿触发**(`0x010ade50` 每次更新读当前帧,帧不为 0 清标志 `+0x11c`,帧为 0 且标志未置位才调 `0x010a3fe0` 复位各条目的“已完成”字节;回到第 0 帧只复位一次),跳过的帧逐帧补跑,`[FRAME]` 先看一次性位再看区间,`[DO BEHAVIOR]` 在目标集合非空时计数并跑第 k 组行为、行为里拥有者换了动作则不置一次性位。`[SET DIRECTION]` 取值编号:0 左、1 右、2 朝目标、3 反向、4 随机、7 本体朝向、8 本体反向;`[SET SPEED]` 的 X 在被设对象朝左时取反;`[CREATE PASSIVEOBJECT]` 的基点、X 取反(朝向不是 1)、朝向来源(`[FIX DIRECTION]` 0 左/1 右/4 中立/5 随机,再看 `[USE MY DIRECTION]`)与 Z 绝对值的规则都读通了。宿主在 `Dnf.Play`(`ActLibrary`、`PassiveObject`、`MonsterActor`、`RoomCombat`),整库审计命令是 `dnf act-run-check`;整个里程碑见 [`docs/flow.md`](flow.md) §24,证据 E-130。

**语言补全(M39,E-132)**:`ActCompiler`/`ActRunner` 又认得 `[DIM]` 变量(整数、按名字、没赋值读 0,右边可以是数、变量、`[GET TIME]` 与加减)、`[IF]` 整体条件、`[DELAY DO BEHAVIOR]` 延迟队列、`[HP]`(绝对值或百分比,百分比阈值是 32 位的“最大生命乘原始值再除以 100”)、`[FRONTSIDE]`、`[IS IN POS]` 与 `[DO PROC BEHAVIOR]`(与 `[DO BEHAVIOR]` 同一个分支)。`[GET TIME]` 当毫秒时钟(整库 394 处“变量加常数与取时间比较”的常数中位数 3000,量级上是毫秒,单位仍是推断)。整库审计(汇总数,本机,非 REA):怪物出招的 `.act` 里认得的触发器 96.4%,被动对象自己的 92.2%,`[proc action]` 83.6%。还没做:`[TRIGGER CHECK]` 与按动作槽选容器的 `[TRIGGER]`(跨动作的触发器池)、`[BACKSIDE]`、`[IS GRABABLE]`、非出招动作的脚本宿主,以及 `[SET POS FORCE]`、`[CHANGE ATTACKINFO]`、`[SET HP]`、`[SUMMON MONSTER]`、`[CHANGE ACTION FILE]` 等行为(见 `dnf act-run-check` 的汇总;`[TELEPORT]`、`[CHANGE FLOATING HEIGHT]`、`[SET DAMAGE BOX]`、`[SHAKING]` 见下一段)。

**怪物行为补全(二)(M40,E-133)**:又认得四个行为。`[SET DAMAGE BOX]`:开、关、复位三档(不带模式标签就是关),关着的时候攻击打不到目标,出招结束自动恢复。`[TELEPORT]`:一个块,`[POS]` 给 X、Y、Z 三个偏移(可带随机值);默认以目标为基准、按目标朝向镜像 X(朝左取反)后移动目标,`[USE MY BASEPOS]` 以拥有者为基准,`[USE MY DIRECTION]` 用拥有者的朝向,`[USE MAP POS]` 当绝对坐标,`[NOT USE OBJECT DIRECTION]` 不镜像、也不再默认以目标为基准,`[NOT USE OBJECT ZPOS]` 让 Z 不加基准的 Z,`[MOVE ME]`/`[MOVE TARGET]` 选被移动的一方,`[ALLOW FIXTURE]` 当不动;落点不合法时在半径 200 内找最近的合法点。`[CHANGE FLOATING HEIGHT]`:不小于 1 就升降到该高度,否则关闭浮空;升降初速是差值的符号乘 sqrt(2×重力×|差值|),出招结束回到基准高度(自己的推断)。`[SHAKING]`:振幅与持续毫秒,持续为负数不震;每条行为只震一次,不按目标重复;场景每帧随机偏移,没有衰减。`IActEntity` 多 `SetDamageBox`、`SetFloatingHeight`、`Teleport`,`IActHost` 多 `Shake`。整库审计(汇总数,本机,非 REA):怪物出招里含读不懂标签的行为组 3290 → 2551、不支持的行为 5036 → 4202。还没做:`.mob` 的静态基准浮空高度(接上后地面攻击够不着,自动机清不了房间)、`[SET POS FORCE]`、`[FLASH SCREEN]`、`[CHANGE ATTACKINFO]`、`[SET HP]`、`[SUMMON MONSTER]`、`[CHANGE ACTION FILE]`;整个里程碑见 [`docs/flow.md`](flow.md) §27,证据 E-133。

**怪物召唤与触发器开关(M41,E-134)**:又认得几个标签。`[SUMMON MONSTER]`:`[INDEX]` 编号、`[LEVEL]` 等级、`[POS]` 三个偏移(可带随机值)与标志(`[USE MY DIRECTION]`、`[USE MY BASEPOS]`、`[NOT USE OBJECT DIRECTION]`、`[USE MAP POS]`、`[USE OBJECT ZPOS]`、`[SUMMON TIME]` 毫秒;`[USE TARGET TEAM]`、`[TEAM]`、`[SUMMON MARK]`、`[COLLISION GROUP]` 读掉不用;出场效果 `[NO EFFECT]`/`[JUMP OUT]`/`[MAGIC CIRCLE]` 只记模式);五个值在目标循环之前各求一次,等级 = 等级值(-1 取目标的)加目标的等级(被动对象目标不加),位置规则与 `[CREATE PASSIVEOBJECT]` 同款(X 偏移朝左取反,`[USE MAP POS]` 当绝对坐标)。`[SET HP]`:一个值,对活着的目标设血量,不大于 0 就死。`[RESTORE]`:`[HP]` 数量与可选的 `[%]`,血量按点数或最大生命的百分比增减(向零截断),夹在 0 到最大生命,0 就是死;`[MP]` 当空操作。条件 `[IS CHILD]` 参数 1 / 0:留下父对象是拥有者的 / 不是的;选择器 `SUMMON OBJECT`:创建者是拥有者的对象。**触发器开关跨出招保留**:`[TRIGGER] n [ON]/[OFF]` 写的是条目的 `[ENABLE]` 字节(`+0x1c`),帧 0 复位只清“已完成”字节(`+0x1d`)与另一张表的 `+0x18`,所以关掉的触发器下一次出同一招时仍是关的;宿主为每只怪物每个出招槽留一份开关数组交给运行器,`.ai` 的 `is attack action enable()` 先读这份跑着的开关,这一招没有触发器程序才回到 `[ENABLE]` 初值。第一版每次出招都复位开关,首领的“每个血量段只召唤一次”招就变成每次出招都召唤、清不了房间,全库扫描因此掉到 294/346;改正后 297,再接上 `[RESTORE]` 是 298。整库审计(汇总数,本机,非 REA):怪物出招里含读不懂标签的行为组 2551 → 2173、不支持的行为 4202 → 3644。召唤物的存活时间、同时在场上限与奖励都是推断(服务端的处理没读到)。还没做:触发次数的跨出招保留、`[TRIGGER CHECK]`(读的就是这个开关字节)、`[SET POS FORCE]`、`[FLASH SCREEN]`、`[CHANGE ATTACKINFO]`、`[CHANGE ACTION FILE]`;整个里程碑见 [`docs/flow.md`](flow.md) §28,证据 E-134。

**怪物对主角挂异常状态(M51,E-144)**:又认得 `[ACTIVE STATUS]`。写法就是 `.atk` 状态槽的写法:一个种类(小写开标签,或带引号的字符串)、概率(浮点)、状态等级、持续毫秒,再按种类跟 0、1、2 或 4 个附加整数(种类名表与个数表和 `.atk` 共用);数不够的当 0。执行:对目标集合里每个活着的、不是被动对象的目标调一次它的挂状态函数,施加者是行为拥有者——客户端调的是受击处理对每个 `.atk` 状态槽调的同一个受害者一侧函数,所以主角身上走 E-136 的整套规则(概率与等级差修正、时长、持续伤害、失去控制),区别只有没有“这一击”(睡眠不被叫醒、冰冻不被打碎)。主角由 `HeroActEntity` 接给会话的状态系统;怪物与被动对象这一版不接状态,收到请求什么都不做。整库审计(汇总数,本机,非 REA):`.act` 里 211 处,怪物招式里 87 条读通,整数个数与 `.atk` 的读法完全一致;引用它的触发器目标落到主角的 65 条、落到怪物的 16 条。还没做:怪物自己身上的状态、元素种类、`[REMOVE ACTIVE STATUS]`;整个里程碑见 [`docs/flow.md`](flow.md) §38,证据 E-144。

**脚本开关别的招的触发器(M52,E-145)**:`[TRIGGER]` 的按动作族写法也认得了。写法有两种:`[TRIGGER] [MOVE]/[STAY]/[SIT]/[DOWN]/[DEFAULT]/[PROC] n 命令`(这几族动作每族只有一个,直接写触发器序号)和 `[TRIGGER] [ATTACK]/[CUSTOM] a n 命令`(先写动作序号 a,再写触发器序号 n),命令是 `[ON]`/`[OFF]`/`[RESET]`;没写种类的老写法仍是当前这个 `.act` 自己。客户端的执行块只看行为拥有者,不循环 `[DO BEHAVIOR]` 的目标集合:从拥有者取那一族动作的触发器池,池不存在或触发器个数不大于序号就跳过,命令 0、1 写条目上的开关字节(就是 `[ENABLE]`,E-134)。`ActRunner` 因此不自己处理按动作族的写法,交给行为拥有者(`IActEntity.SetActionTrigger`,默认空实现),每条行为一次;怪物把出招族直接写进每招一份的开关数组(跨出招保留,`.ai` 的 `is attack action enable()` 读同一份),写到正在跑的这一招时经运行器;其它动作族怪物还没有 `.act` 触发器宿主,只记数。整库审计(汇总数,本机,非 REA):94 只怪物带这类开关,指向出招的 237 条中 90.7% 落到存在的招与触发器(是对“动作序号就是 `.mob` 招式列表下标”的交叉验证),指向其它动作族的 412 条。还没做:其它动作族的落实(M39-2b)、被动对象的 `[CUSTOM]`、`[TRIGGER CHECK]`;整个里程碑见 [`docs/flow.md`](flow.md) §39,证据 E-145。

**怪物非出招动作族的 .act(M53,E-146)**:怪物的待机、行走、坐下、受击一与二、倒地、翻倒、过程、最后一幕各有自己的 `.act`(`.mob` 里 `[waiting action]`、`[move action]`、`[sit action]`、`[damage action 1]`、`[damage action 2]`、`[down action]`、`[proc action]`、`[last action]` 等标签;`[etc action]` 的列表读进来但没有宿主),触发器与出招的一样,只是不属于任何一招:怪物宿主为每一族建一个运行器,站立、行走、受击、倒地只在这一族是当前动作时跑(每次进入这一族重新建运行器,次数与单次位清零,开关数组跨进入保留),过程动作从出生跑到死亡(没有 BASE ANI 时帧号固定 0),最后一幕从血量归零起跑到动画播完再多 200 毫秒(没有 BASE ANI 给 1 秒;身体消失之后继续跑,脚本放出的对象由会话接管)。客户端把 `[ON DAMAGE]` 与 `[ON ATTACKSUCCESS]` 发给“当前动作”与“过程动作”两个对象(静态读码),所以过程动作随时都收得到,别的族只有它是当前动作时才收得到。非出招族里的行为:`[SET SPEED]` 与 `[SET ACTION]` 照出招的接法(速度离开这一动作就清零;`[SET ACTION]` 只在怪物待机或行走时接 `[ATTACK] n`、`[STAND]`/`[DEFAULT]`、`[MOVE]`,而且 `[ATTACK]` 要等出招间隔过了——这条是推断,不加的话“离目标近就出招”的行走脚本会在出完招的第 7 毫秒又出一招、而出招动画多半没有受击框,主角永远打不到它);Z 方向的速度受重力,非出招相位里把 Z 速度设成 0 只是“不再上升”,动作被换掉时在半空的怪物落回地面。`[TRIGGER]` 的按族写法(E-145)现在能开关站立、行走、坐下、倒地、过程这几族的触发器;`[DEFAULT]`、`[CUSTOM]` 池仍只记数。整库审计(汇总数,本机,非 REA):带触发器的非出招族 `.act` 的怪物 648 个,指向这些族的 `[TRIGGER]` 开关 443 条中 249 条落到宿主、194 条落空(多数是序号越界,与“池里个数不大于序号就跳过”一致)。后来做了的:`[TRIGGER CHECK]`(M61)与 `[etc action]` 自定义动作、`[SET ACTION] [CUSTOM]`(M62-b,见本文最后一段);还没做:`[DEFAULT]` 池、跳跃类动作族、最后一幕的画面;整个里程碑见 [`docs/flow.md`](flow.md) §40,证据 E-146。

**没落实的行为与触发器的运行期埋点(M54a,E-147)**:`IActHost` 有两个带默认空实现的通知 `NoteUnsupportedBehavior(标签)` 与 `NoteUnsupportedTrigger(标签)`;`ActRunner` 在行为分发的缺省分支(编译器认得、运行器这一版没落实的行为)与每次 `Begin()` 时读不懂的触发器标签上各调一次,会话汇总成两张表,`dungeon-check` 打印,`dungeon_sweep.py` 汇总成“没做的行为”“读不懂的触发器标签”两行(另有“怪物构成”一行,数脚本指挥、默认 AI 大脑、占位各多少只)。数的是执行次数——持续成立的条件触发器每步都会再触发——不是独立事件数;全库默认扫描里没落实的行为 50 种、共 419150 次,读不懂的触发器标签 34 种、共 974 次(汇总数,本机脚本扫描,非 REA)。`dungeon-check --trace` 每秒多打一行活着的怪物(序号、位置、相位首字母、大脑模式),查“怪物为什么没动”时用。

**怪物脚本换动作文件(M57,E-151)**:又认得 `[CHANGE ACTION FILE]`。写法:标签后跟一个族关键字(与 `[SET ACTION]` 同一套;受击、出招、自定义带序号,序号可省当 0)和新 `.act` 的路径字符串(相对执行它的 `.act` 所在目录);没有族关键字或没有路径的写法(如“最后一幕”)客户端的族码表里没有,按没做记数。客户端的行为(静态读码):对每个怪物目标按族号(0 到 13)新建动作对象换进它的动作表;被换的若正是正在跑的动作,旧动作被标成可丢弃并挂替换指针,下一次设当前动作时才改取新的——**正在跑的继续跑,新动作从这一族下一次被进入(或脚本再次 `[SET ACTION]`)起生效**。我们:`ActRunner` 对怪物目标调 `IActHost.ChangeActionFile`(默认空实现);`MonsterActor.ApplyActionFile` 换这一族的动画与触发器程序,没在跑的马上换,正在跑的记成待换;`ActLibrary.SwapAction` 缓存换进来的动作(程序与主动画),`Preload` 提前读并跟着读它的被动对象(深度 3)。后退、冲刺、冲刺攻击、跳跃、跳击、自定义六个族这版怪物没有动作,只记数。真实数据:含换文件记录的怪物 200 个、记录 1343 条,换进来的文件读得到 1327 条(98.8%),序号越界 0,没有对应动作的族 46,写法读不通 21(汇总数,本机脚本扫描,非 REA)。

**怪物脚本强制位移(M58,E-152)**:又认得 `[SET POS FORCE]`。写法:块里按出现顺序排下的记号——三个轴的 `[X START]`、`[Y START]`、`[Z START]` 与 `[X END]`、`[Y END]`、`[Z END]`(后面各跟一个值表达式)、基准点 `[USE TARGET POS]`(缺省)、`[USE MY BASEPOS]`、`[USE MAP POS]`,朝向来源 `[USE MY DIRECTION]`、`[NOT USE OBJECT DIRECTION]`(缺省取目标的),插值 `[MOVE UNIFORM]`、`[MOVE REDUCE]`(线性)与 `[MOVE ACCELATE]`(没读),被移动者 `[MOVE TARGET]`(缺省)、`[MOVE ME]`。客户端的行为(静态读码):执行时先清空这个动作的强制位移表,再给目标集合里每个活动对象登记一条记录;此后**每次更新**重算落点:基准加偏移,每个轴写 0 个值取目标自己的坐标、1 个值是基准加起点、2 个值是基准加起点到终点的线性插值(进度 = 动作已过时间 / 动作全程,单精度、向零截断)、更多个值落 0,朝左时只有 X 的偏移取反;落点能站才整个摆,不能就只改高度。我们:`ActCompiler.ParseSetPosForce` 按序留下全部记号(读不出值或带 `[MOVE ACCELATE]` 的块整个记成不支持);`ActRunner` 的强制位移表与 `ApplyForcedMoves`;`IActEntity` 的 `MotionDurationMs` 与 `ForcePosition`(怪物、被动对象、主角各自实现)。没做:加速插值、`[LASTATTACK POSITION]`、“可抓取”谓词、主角的高度。真实数据:怪物出招的 `.act` 里 149 块,读通 146 块,其余动作与被动对象全部读通(汇总数,本机脚本扫描,非 REA)。

**挑目标、距离、可抓取、触发器开关读取与保存目标槽(M61,E-155、E-156、E-157)**:又认得六个条件写法。`[GET TARGET] n 键 [HIGH]/[LOW]`:键 `[RANDOM]`、`[DISTANCE]`、`[LEVEL]`、`[HP]`、`[LAST ATTACKSUCCESS]`(`[MP]` 不认),随机没有方向;工作集为空整个触发器不通过,否则按键整理再留前 n 个(n 小于个数才删;距离按整数距离,`[HIGH]` 从尾取;生命 `[HIGH]` 是原版笔误,不排序)。`[CHECK DISTANCE] x y`:整体真假,拥有者是生物类且场上有敌对、活着的角色在横向与纵深范围内(位置取整数,含等号)。`[IS GRABABLE] n`:逐对象过滤,被动对象剔除,n 为 1 留能抓的、别的值留不能抓的。`[TRIGGER CHECK] [种类 [动作序号]] 序号 [ON]/[OFF]`:没写种类是自己这一动作的第 n 个触发器开关的整体真假(越界不通过),写了族是逐对象过滤(`IActEntity.GetActionTrigger`;`[DEFAULT]` 不认;怪物上 `[CUSTOM]` 与 `[DEFAULT]` 池没有宿主,回答没有,被动对象见 M62)。`[SAVE TARGET OBJECT] [选择器] 槽号` 与 `[LOAD TARGET] 槽号`:槽表在行为拥有者对象上(`IActEntity.SetSavedTarget`/`GetSavedTarget`),只存选择结果的第一个对象,空选择不动槽,不随运行器与这一招结束而清掉;取出时已死的对象当没有。没做:`[GET TARGET]` 的 `[MP]` 键、`[CHECK DISTANCE]` 里两个没读出语义的虚槽(按成立、按假)。

**被动对象各动作的触发器开关板(M62,E-158)**:被动对象每个动作一份触发器开关(第一次用到时取 `[ENABLE]` 初值,换动作时交给新运行器共用,一生保留,不随再次进入同一动作而复位)。`[TRIGGER CHECK] [CUSTOM] a i [ON]/[OFF]` 逐对象过滤,读 `[etc action]` 第 a 项的第 i 个开关(读不出动画的动作也有池);`[TRIGGER] [CUSTOM] a i`、`[TRIGGER] [DEFAULT] i`(`[DEFAULT]` 是基础动作)写开关,写正在跑的动作走运行器(复位清它的触发次数),写没在跑的动作只改开关。被动对象没有 `[ATTACK]`、`[MOVE]`、`[STAY]`、`[SIT]`、`[DOWN]`、`[PROC]` 的池:读侧对象被剔除,写侧落空(有计数)。运行器里 `[DO BEHAVIOR]` 的目标写 `[CHECKUP OBJECT]` 时才用过滤后的工作集,写 `[ME]` 等选择器则重新选目标、过滤的结果不起作用。怪物的自定义动作宿主见下一段(M62-b)。
**怪物的自定义动作(M62-b,E-159)**:怪物 `.mob` 的 `[etc action]` 列表(下标从 0 起,空位也占序号)是 `[SET ACTION] [CUSTOM] n` 的目标(全库扫描:怪物自己 `.act` 里 230 条这样的写法有 97.0% 的 n 落在本怪物列表范围内)。怪物装上列表(`MonsterDef.EtcActions`、`MonsterFamilyActs.Customs`),切进去后放它自己的 BASE ANI、整个动作一个运行器,`[ENABLE]` 开关板按动作各存一份、进出保留;`[TRIGGER] [CUSTOM] a t` 与 `[TRIGGER CHECK] [CUSTOM] a t` 在怪物上有了宿主。运行模型是推断(怪物状态机一侧自定义状态的处理没读):住在出招阶段(不算一次出招,不涨出招次数、不通知 `.ai`),动画不吃出招速率,攻击信息沿用最近一次出招,每次进入重置判定窗口;出招、待机、行走里都能切进(待机与行走里不看出招间隔,结束回待机、不动冷却;出招里切进来的整串结束按出招结束算),不循环的播完结束,循环动画只有脚本 `[STAND]`/`[ATTACK]`/`[CUSTOM]` 或挨打能结束(带 `[END]` 时放完这一遍),没有动画的跑一步就结束;序号越界、空位、没写列表落空并计数。没做:怪物上的 `[DEFAULT]` 池;`.ai` 的 is custom action enable()(数据里没有使用者,仍返回假)。
**属性改变附加物(M63,E-161)**:`[APPENDAGE] 持续毫秒 { [属性标签] 操作符 数值 }… [/APPENDAGE]` 对目标集合里每个活着的非被动对象各挂一个附加物(一项一个,来源是行为拥有者);`[DELETE APPENDAGE] 开头数 { … }` 写法相同,删来源等于行为拥有者、与所列项相同(类型、比例标志、数值都同)的附加物。属性标签(43 个,类型号 0 到 63,不认得的得 −1)的字典在 `StatChangeTypes`;操作符只有 `[%]` 算比例,`[+]`、`[-]` 都是点数且数值照写的符号用;项后多出来的数读掉不用;缺操作符或数值的写法读到此为止、已读的项照用。同类型、同比例标志、同数值的项被新挂的顶替(不分来源),所以同一脚本反复挂不叠加;持续毫秒不大于 0 当没有时限(推断);被动对象放出的 `[APPENDAGE]` 来源是那个被动对象。宿主接口:`IActEntity.AddStatAppendage(StatChange, int durationMs, IActEntity source)`、`RemoveStatAppendage(StatChange, IActEntity source)`(缺省实现什么都不做);怪物落生命上限、走路与出招速率、攻击力、防御面板,主角只落移动速度与攻击速度(见 `docs/flow.md` §51)。没做:`[NO BUFF APPENDAGE]`、`[MASTER APPENDAGE]`、`[HP/ICE/GUARD/PULL/REFLECTBUFF/VIRTUALHP/BOOSTER APPENDAGE]`(别的加载块,在"读不懂的行为标签"里计数)。

**被动对象追踪与 `[SET USE HOMING]`(M67,E-165)**:`[SET USE HOMING] n` 现在认得:写法一个整数(整库 50 处都是整数写法),非 0 开、0 关目标身上的追踪,由对象自己落实(`IActEntity.SetUseHoming`,默认什么都不做);被动对象的 `[homing]`…`[/homing]` 块读成 `HomingDef`(`[homing use]`、`[homing follow]`、`[homing velocity]`、`[homing check gap]`、`[max rotation]`、`[init rotation]`、`[diff rotation]`、`[homing time]`、`[homing direction]`、`[homing keep distance]`、`[sync animation rotation]`、`[sync target move]`、`[straight homing]`、`[homing z axis]`),`[homing use]` 为非 0 的对象出生时配上追踪控制器,`[homing use]` 为 0 的对象没有控制器,开关对它没有效果(整库里所有会开关追踪的 `.act` 都属于启用追踪的对象)。控制器每帧在对象按旧速度走完之后转向,规则(静态读码,非 REA)见 `docs/evidence/ledger.md` E-165 与 `docs/flow.md` §55。

**条件批一(M68,E-166)**:十七个条件标签现在认得并在运行器里落实:`[RANDOM CHECK] 分子 [分母]`(逐对象掷一次,余数不大于分子留下,分母缺或 0 当 100)、`[BACKSIDE]`、`[FACE TO FACE]`、`[WITHIN SIGHT]`、`[OUT OF SIGHT]`(与 `[FRONTSIDE]` 同一套整数 X 与朝向的真值表)、`[IS TEAM]` 子标签 `[MY TEAM]`、`[ENEMY TEAM]`、`[CHARCTER TEAM]`、`[MONSTER TEAM]` 至 `[/IS TEAM]`、`[ZSPEED] 符号 n`(对象速度的高度轴浮点比较)、`[IS END MAP]`、`[IS END MAP VERTICALLY]`(整体条件:拥有者在舞台端头且速度朝外或为 0)、`[IS TARGETACTIVESTATUS] n`(对象带着第 n 号异常状态)、`[IS FIRE DAMAGE]`、`[IS WATER DAMAGE]`、`[IS DARK DAMAGE]`、`[IS LIGHT DAMAGE]`、`[IS NONE DAMAGE]`(`[ON DAMAGE]` 唤醒时看命中它的那一击的元素标志,别的唤醒恒通过)、`[IS FRONTPOS]`、`[IS BACKPOS]`。宿主要给的新东西:`IActEntity.Team/SpeedX/SpeedY/SpeedZ/HasActiveStatus`(默认实现:按类型给队号,速度与状态恒空)、`IActHost.MapWidth/MapHeight`(默认 0 = 不知道,端头条件失败)。同时更正 M61:`[CHECK DISTANCE]` 的拥有者与 `[IS GRABABLE]` 的对象转的是碰撞对象类,被动对象照样评估(`[IS GRABABLE]` 对被动对象恒为不能抓)。规则(静态读码,非 REA)见 `docs/evidence/ledger.md` E-166 与 `docs/flow.md` §56。

**脚本换攻击信息(M69a,E-167)**:`[CHANGE ATTACKINFO] "路径"`(路径相对 `.act` 所在目录)与 `[CHANGE ATTACKINFO] [DEFAULT]` 现在认得并落实:只作用在动作拥有者自己身上,不看目标集合;`[DEFAULT]` 取程序一级顶层 `[DEFAULT ATTACKINFO] "路径"`(`ActProgram.DefaultAttackInfo`,多条取第一条非空的),没有程序默认就回到对象本来的(怪物 `.mob` 表里的、被动对象 `.obj` 里的)。宿主要给的新东西:`IActHost.ChangeAttackInfo(owner, path, fromProgram)`(默认空实现;`path` 为 null 即 `[DEFAULT]`,`fromProgram` 是写这条行为的 `.act` 的路径,用来解析相对路径)。怪物这一侧换上的在下一次正常出招开始时清掉,被动对象这一侧跨动作保留、进入自带默认的动作时被盖掉(都是推断)。规则(静态读码,非 REA)见 `docs/evidence/ledger.md` E-167 与 `docs/flow.md` §57。

**脚本读条(M69b,E-168)**:`[CASTING] 毫秒 行为组号`(两个值表达式,排上时各取一次)现在认得并落实:读条记在运行器上(单槽,新的顶掉旧的),到期时刻 = 运行器自己累加的时间 + 毫秒,每次 `Update` 的末尾检查到期,到期就对排上时的目标集合(不在场的剔除)执行指定的行为组(从 1 数起的组号,小于 1 或越界不执行);毫秒小于 0 或组号小于 1 不排,但目标列表已经换成此刻的;到期先清读条再执行,所以行为组里可以再排一条;`Begin()` 作废排着的读条。宿主要给的新东西:`IActHost.CastArmed(owner, milliseconds, group)` 与 `CastFired(owner, group)`(默认空实现,只做统计,读条条的画法留给宿主)。出招结束或被打断时怪物换新的运行器,排着的读条随之作废;被动对象的读条同理。规则(静态读码,非 REA)见 `docs/evidence/ledger.md` E-168 与 `docs/flow.md` §58。

**朝目标的移动与转向、循环出招的收尾(M70、M70b,E-169、E-170)**:`[MOVE TO TARGET] n`、`[CHANGE DIRECTION BY TARGET] n`、`[IS DIRECTION TO MOVE] n`、`[SET SAVED TARGET] 槽号`、`[CHANGE MY TARGET]` 五个行为现在认得并落实:`[MOVE TO TARGET]` 对集合里每个在场的对象按 `ActGeometry` 的取整算法(角度不还原纵深、距离还原)设平面速度,计时记在运行器上(单槽,重启顶掉旧的,动作重来作废),每次更新里在触发器之后检查到点、把还停在当初速度上的对象的平面速度清零;两个朝向开关与 AI 目标交给拥有者,没有目标的 `[MOVE TO TARGET]` 什么都不做。宿主要给的新东西:`IActEntity.SetFacingFollowsTarget/SetAttackFacing/SetAiTarget`,`IActHost.MoveToTargetStarted/MoveToTargetStopped`(默认空实现,只做统计)。`ActProgram.CanEndAction` 记“程序里有能离开出招的 `[SET ACTION]`(站立、缺省、出招、自定义四族)”,宿主用它决定动画带循环属性的出招要不要由脚本收尾(动画循环又能由脚本收尾、没有排着的切招时,出招阶段不设上限)。规则(静态读码加本机统计与实跑,非 REA)见 `docs/evidence/ledger.md` E-169、E-170 与 `docs/flow.md` §59。

**脚本类别、目标状态与副本队条件(M71–M73,E-171、E-172、E-173)**:又认得六个标签。`[ADD CATEGORY] 名字` 与 `[REMOVE CATEGORY] 名字` 对目标集合里每个活着的对象改类别(名字按客户端 44 项类别名表查编号,查不到的名字整项跳过、不影响后面的行为;加入锚点类别时还记下当前坐标作锚点,目前没有消费者);`[IS CATEGORY] 名字…` 是逐对象过滤,名字之间是“或”,没写名字时没有对象能留下;怪物 `.mob` 的 `[category]` 在出生时被播进同一个类别集合,主角与被动对象没有类别集合。`[IS TARGET STATE] n` 与 `[IS NOT TARGET STATE] n` 是逐对象过滤,拿对象的状态值(怪物取 AI 的当前操作值,主角取会话给的值)与 n 比较(前者要求相等、后者要求不等);n 为 -1 且对象是主角时看主角的 `+0x36b0` 字段,不等于 -1 就无条件留下(字段含义没读通,Play 层默认 -1);拥有者是被动对象时所有对象都被剔除。`[REALDUNGEON CHECKUP] mode key third` 是整体真假、`[IS OURTEAMATTACKED]` 是逐对象过滤,运行器已落实,Play 层的副本单例与受击登记没接,没接时前者按查表得 0、后者按不匹配走。

**脚本设队与两个条件(M74,E-221 到 E-223)**:`[SET TEAM] 值`(行为码 0xc4)对目标集合里每个活着的非被动对象写队号,写入的队号在 `[IS TEAM]` 里优先于对象自带队号;`[IS VISIBLE] n`(0x3b)按"可见性字节"的低 8 位与参数相等过滤;`[IS DAMAGEBOX ENABLE] n`(0x3a)按"伤害盒是否开启"与参数过滤,被动对象剔除。三者的写入点与读取点由对象自己提供(`IActEntity` 的队号覆盖、可见性字节、伤害盒开关,默认不覆盖、0、开启),Play 层的实体还没有接线,所以真实房间里它们不改变结果。
