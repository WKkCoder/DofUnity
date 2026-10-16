# 怪物数值的计算(服务端 `set_monster_ability` 的移植)

怪物的血量、攻防、抗性、速度这些数是怎么来的:**服务端按怪物编号、等级、种类和副本参数现算出来**(客户端怎样拿到这些数——收包还是本地也算——没有读)。
我们要在 Unity 里复现单机战斗,就必须自己算出同样的数。本文记**结构事实**:函数地址、结构偏移、代码里的立即数、计算顺序、精度模型、已知怪癖与验证方法。
证据编号见 `docs/evidence/ledger.md` 的 E-062(规则)与 E-063(验证方法);**不含反编译文本,也不含任何游戏数据文件里的具体数值**(表格、`.mob`、`.tbl` 的内容只在本机运行时读取)。

约定:**观察** = 在服务端带符号的反汇编里读到并核对过;**推断** = 由观察推出,名字也是我们按用途起的;**未知** = 没读到。
实现:`packages/com.dnfunity.game/Runtime/Defs/` 的 `MonsterDef`(读 `.mob`)、`MonsterBaseTable`(读 `.tbl`)、`MonsterAbility`(计算)与 `Numerics/X87.cs`(扩展精度模型)。

## 1. 总览

```
set_monster_ability(怪物脚本, 等级, 种类, 副本参数, …)
 ├ 清零 ActiveStaticInfo(188 个 int,带缺省值)           ← MonsterStats.Clear
 ├ setBasicAbilityByLevel      0x088B9603                 ← MonsterAbility.BasicByLevel
 │    选表 → 按等级查行 → 基础数值落进数值块;.mob 的"最小/最大"对按等级线性插值;派生量(回复、浮点加成)
 ├ upgradeAbilityByMonsterParameterCategory 0x088B7F36    ← MonsterAbility.ApplyCategories
 │    按 .mob 的 [ability category] 乘或加(14 个类别)
 ├ upgradeAbilityByDifficultyOfDungeon                    ← 未实现(按副本难度)
 ├ 首领特性(服务端里是空函数)/ 精英词缀(common / super champion)  ← 未实现
 └ 队伍人数、死亡塔、血系统等其他层                        ← 未实现
```

`MonsterAbility.Compute` 目前覆盖前两步(加首领判定);其余层有各自的服务端函数,按 P5 的需要逐个补。

## 2. 输入

**基础属性表**(`monster/{common,named,boss,summon,warroom}monsterbaseparameter.tbl`,路径取自服务端的文件名表):每个文件是一串整数,**每 24 个一行**,
末尾不足一行的整数丢弃。服务端按"文件列 → 结构偏移"存放,偏移是代码里的立即数(下表列号从 1 数):

| 文件列 | 结构偏移 | 去向 |
|---|---|---|
| 1 | +0x00 | 等级(查表时按它匹配,**线性查找,取第一个匹配**,行不要求连续) |
| 2 至 5 | +0x0C、+0x10、+0x14、+0x18 | 物理攻击、物理防御、魔法攻击、魔法防御(基础) |
| 6、7 | +0x04、+0x08 | 生命值基数、魔法值基数 |
| 8 至 11 | +0x1C、+0x20、+0x24、+0x28 | "装备"四项(同样的物理/魔法攻防) |
| 12 | +0x2C | 除以 100 加进数值块的一个浮点字段(去向含义未确认) |
| 13 | +0x30 | 除以 10 取整,加进硬直抗性 |
| 14 至 17 | +0x34..+0x40 | 加到火、水、暗、光元素抗性 |
| 18 至 21 | +0x44..+0x50 | 加到火、水、暗、光元素攻击 |
| 22 | +0x54 | 加到受击恢复 |
| 23 | +0x58 | 加到硬直度(`[rigidity]` 那一项) |
| 24 | +0x5C | 加到除硬直抗性外的 16 项异常状态抗性 |

**`.mob` 里参与计算的标签**(服务端加载函数 `ImportMonsterScript` `0x08a12827` 把它们存进脚本结构,偏移是代码立即数;全表见 `out/srv_schema/mob.tsv`,不入库):
`[level]`(+0x3EC,最小/最大一对)、火水暗光四个 `[… resistance]`(+0x3F4 起)、17 个异常状态抗性(+0x414 起,顺序见 `MonsterDef.StatusResistanceTags`)、四个 `[… attack]`(+0x508 起)、
`[HP regen speed]` +0x49C、`[MP regen speed]` +0x4A4、`[move speed]` +0x4AC、`[attack speed]` +0x4B4、`[cast speed]` +0x4BC、`[hit recovery]` +0x4C4、`[jump power]` +0x4CC、
`[jump speed]` +0x4D4、`[weight]` +0x4EC、`[rigidity]` +0x528;`[ability category]`(向量,+0x7C)、`[base table]`(怪物自带的表,+0x12C)、`[ability table]`(+0x3BC)、`[life max]`(+0x3B8,取低字节)。
成对标签只写一个整数时,最大值取最小值;标签缺省时各字段取服务端 `Clear` 里的立即数(例如 `[jump speed]` 100 100、`[width]` 40 20、`[sight]` 200、`[warlike]` 50、`[attack delay]` 1000,其余成对数值为 0)。

## 3. 选表(`getMonsterTableIndex`)

后面的条件覆盖前面的:`[ability table]` 为 1 或强制召唤 → 3(召唤表);种类为首领(3)→ 2(首领表);怪物编号在 50000 到 51000 或 56000 到 60000(`isNamedMonster`)→ 1(有名表);
副本参数在 2000 到 2002 或强制战争房 → 4(战争房表)。都不是 → 0(普通表)。**战争房表的行用副本参数匹配,而不是怪物等级**(数值块里记的等级仍是调用者给的等级)。

怪物自带的 `[base table]`(可有多个,路径相对 `monster/` 目录,后一张覆盖前一张的同行号)按**同一个行号**取代公共表的那一行;
**服务端不检查越界**:自带表比公共表短而怪物等级落在后面的行上时,原版读到数组之外(未定义行为)。我们沿用公共表的行,并在测试里写明。

## 4. 按等级插值与派生量

令 `cur = 怪物等级 - [level]最小值`、`total = [level]最大值 - [level]最小值`。成对标签的取值(`GetLinearDataForMonsterAbility`):
`cur >= total` 取最大值;`cur <= 0` 取最小值;否则 `(最大 - 最小) × cur / total + 最小`——**乘法在 32 位里回绕,除法是有符号整除**(与原版一致,有专门的单元测试)。

派生量(顺序与原版一致,凡标"x87"的都用扩展精度模型,见第 6 节):
- 生命、魔法回复:`速度 / 1000` 先**落地成 float**,再乘生命/魔法上限(上限 = 基数 × 倍率 截断),乘积截断为整数。回复的单位与周期没有读出,不要当作每秒。
- 文件第 12 列:`第 12 列 / 100` 加到浮点字段 0x6A(x87,结果落成 float)。第 13 列:`第 13 列 / 10`(x87)截断后加到硬直抗性。
- 元素、受击恢复、硬直度、17 项异常状态抗性:先插值,再加表里对应列(硬直抗性这一项除外,它只加第 13 列)。

## 5. `[ability category]`

14 个类别名与它们落到数值块的下标(服务端跳转表;`[HP MAX]` 与 `[HP MAX BOSS]` 都落在生命值基数上):

`[ATTACK_SPEED]` 0x48、`[MOVE_SPEED]` 0x46、`[PHYSICAL_ATTACK]` 6、`[PHYSICAL_DEFENSE]` 8、`[MAGICAL_ATTACK]` 10、`[MAGICAL_DEFENSE]` 12、
`[EQUIPMENT_PHYSICAL_ATTACK]` 0x56、`[EQUIPMENT_PHYSICAL_DEFENSE]` 0x58、`[EQUIPMENT_MAGICAL_ATTACK]` 0x5A、`[EQUIPMENT_MAGICAL_DEFENSE]` 0x5C、`[HP MAX]` 1、`[HIT_RECOVERY]` 0x4C、`[STUCK]` 0x2B、`[HP MAX BOSS]` 1。

每一项是 (类别名, 运算符, 数值) 三个一组:
- 运算符为 `+`:`字段 += 数值 / 100`(整数除法,向零取整);
- 其它(`*`):`字段 = 截断(数值 / 100 × (float)字段)`——字段先落成 float,`数值 / 100` 留在 80 位寄存器里不落地(x87);
- 列表里**有** `[HP MAX BOSS]` 时:首领只用它、跳过 `[HP MAX]`;非首领只用 `[HP MAX]`、跳过它。没有 `[HP MAX BOSS]` 时 `[HP MAX]` 对所有怪物生效;
- 类别名不认识的项被跳过(与服务端字符串比较一致,区分大小写)。

## 6. 精度:x87 不能用 double 代替

服务端是 32 位 x86 程序,浮点走 x87:默认控制字 `0x37F`(64 位有效位、就近舍入偶),中间结果留在 80 位寄存器里,`FSTP` 到 float 时才舍入到 24 位,`FISTP` 取整时临时改成截断
(溢出得到 `INT_MIN`)。用 `double` 朴素算会在整数边界上差一:例如 **100 的 53% 原版得 52,double 得 53**;**100 的 29% 原版得 29,double 得 28**
(这两个是合成算例,不是游戏数据)。`Numerics/X87.cs` 用大整数尾数加指数精确复现这条路径(加、乘、除、舍入到 float、截断取整),`tools/py/x87.py` 是它的独立 Fraction 实现。

## 7. 数值块(`ActiveStaticInfo`,188 个 int)

下标 = 结构字节偏移 / 4;很多整数后面紧跟一个浮点倍率(缺省 1.0,位模式存在同一数组里)。`MonsterStats` 给出有名字的下标:
等级 0、生命基数 1(倍率 2)、魔法基数 3(倍率 4)、标志字节 5、物理攻击 6、物理防御 8、魔法攻击 10、魔法防御 12、元素抗性 0xE 起 4 项、元素攻击 0x16 起 4 项、
异常状态抗性 0x1E 起 17 项(硬直抗性 0x2B)、生命回复 0x42、魔法回复 0x44、移动速度 0x46、攻击速度 0x48、施法速度 0x4A、受击恢复 0x4C、硬直度 0x4E、跳跃力 0x50、
跳跃速度 0x52、重量 0x54、"装备"四项 0x56 至 0x5C。没认出含义的下标不给名字,保留在 `Raw` 里。缺省值取自服务端 `ActiveStaticInfo::clear`(`0x081509aa`)的立即数;`.mob` 各标签缺省时的取值来自 `STMonsterScript::Clear`(`0x08a398aa`)。

**怪癖(都已在原版机器码里确认)**:
- **槽 5 是字节**:`clear` 只写低字节(缺省 1,`BasicByLevel` 里取脚本 `[life max]` 的低字节),其余 3 个字节未初始化。我们把它当字节处理,比对时只比低字节。
- 生命上限 = 截断(基数 × float 倍率)(x87);基数很大且倍率大于 1 时 `FISTP` 溢出得到 `INT_MIN`,不会饱和。
- 自带表越界读(第 3 节);`[life max]` 全库只有 3 个怪物使用。

## 8. 验证

三份互相独立的实现逐位一致(E-063):C# `MonsterAbility`、Python 参考(`tools/py/mob_ability_ref.py`,精确 x87 模型)、**服务端原版机器码在 CPU 模拟器里执行**
(`tools/py/elf_emu.py`、`tools/py/mob_ability_oracle.py`;非 REA)。
- 全库:`monster.lst` 里每个 `.mob` 取等级范围的最小、中间、最大三个等级(重复的合并),普通与首领两种,共 8,050 组互不相同的 (怪物, 等级, 种类),三者零不一致;
- 随机合成:10 万个随机怪物(随机等级区间、随机类别运算符与数值、随机自带表)Python 与原版零不一致;另 3 万加 4 万个(含 `int` 全范围的极端数值)C# 与原版零不一致;
- 回归:`src/Dnf.Game.Tests/Golden/mob_ability_golden.txt` 是全合成的表加 250 个合成用例与原版函数的输出(可入库),`MonsterGoldenTests` 逐槽比对;
  把 `ApplyCategories` 的乘法换成 double 朴素算法,金标准测试出现 54 个不一致、53% 算例失败(变异检查,验证了测试有区分力);
- 命令:`dnf mob-check`(全库校验、`--dump`、`--json`)与 `dnf mob-check --cases <文件>`(读取合成用例并与期望槽值比对)。

**覆盖的边界**:模拟器验证的是**计算函数**——喂给它们的脚本结构由我们按加载函数 `ImportMonsterScript`(`0x08a12827`)的读法填写,加载函数本身没有在模拟器里跑;`.mob` → 结构偏移这一步依据静态阅读(E-055 的 schema)。
没有实现的层(副本难度、队伍、精英词缀、死亡塔、血系统)不在上面的数字里。

## 9. 未知与下一步

- 其余层的服务端函数:`upgradeAbilityByDifficultyOfDungeon`(三个重载:`0x088b8c37`、`0x088b85cf`、`0x088b813a`)、队伍人数、精英词缀、死亡塔、血系统;
- 回复的周期与单位;浮点字段 0x6A 的含义;`[attack kind]`、`[int data]` 的语义;
- **客户端自己的浮点模式**:客户端同样用 x87(例如 tryHit 的区间比较,见 `docs/hit-pipeline.md`),但它在 Windows 上跑:CRT 默认精度控制是 53 位,而 Direct3D 9 设备创建时若没带 `FPU_PRESERVE` 会把精度降到 24 位;
  客户端算出来的东西(伤害、几何)是否受影响要查设备创建标志(静态可查)。服务端的结果不受影响(Linux,`0x37F`,已对拍)。
