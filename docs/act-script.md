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

(随证据补充;每一条给出地址与依据。)
