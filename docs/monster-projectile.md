# 怪物投射物(M15,E-103)

地精投掷手这类怪物的出招不是近战,而是"扔东西":`.mob` 里写一个 `[throw attack]` 块,出招动画播到某一帧时放出一个被动对象(石头、冰弹之类),它按 `.ptl` 的定义飞,碰到主角就造成伤害。本文只记**机制、字段布局与实现结构**;`.mob`、`.obj`、`.ptl` 里的具体数值(发射帧、起点偏移、速度、寿命、攻击框等)是原版数据,不进仓库。

证据等级:块的布局、投掷的发射条件、创建位置的算法与 `.ptl` 的枚举来自静态读码(高到中);粒子被创建之后"怎么飞"落在客户端的受保护区域里,没有读到,**飞行速度的单位、寿命的单位、命中后消失都是推断**(低),实现里单独标出。没有运行客户端。

## 1. `[throw attack]` 块(E-103)

`.mob` 里每个 `[throw attack]` 到 `[/throw attack]` 是一条记录(加载函数 0x012abfd0 里 0x012b1094 到 0x012b17ea 一段)。子标签与缺省值:

| 子标签 | 含义 | 缺省 |
|---|---|---|
| `[attack index]` | 哪一招(`[attack motion]` 的下标)出招时投 | -1(不投) |
| `[passive object number]` | 一次放几个被动对象(记作 N);下面带"每个"的表都按 N 个取 | -1 |
| `[passive object filename]` | N 个 `.ptl` 文件名 | 空 |
| `[passive object index]` | 被动对象编号(经 `passiveobject.lst` 找 `.obj`);0 表示不放 | 0 |
| `[throw frame]` | 发射帧:动画当前帧序号**大于**它才发射 | -1 |
| `[passive object start x cood]`、`... y ...`、`... z ...` | 每个对象的起点偏移 | 空 |
| `[attack number]` | 一次出招里投几次 | -1 |
| `[throw sound]` | 发射时播放的音效名(空串不播) | 空 |
| `[direction]` | 每个对象的朝向:空或 0 = 与怪物同向,非 0 = 反向 | 空 |
| `[power]`、`[object type]`、`[start kind]` | 含义没读通(`[power]` 几乎都是 0) | -1 / 0 / 0 |

## 2. 什么时候扔(E-103)

状态更新(0x00896780)在"出招"状态里每帧调投掷函数 0x00890550。对每条记录,下面全成立才发射:

1. `[attack index]` 等于当前出招号;
2. 该记录这次出招里还没投过(已投标志在**进入出招状态**时清空);
3. 动画当前帧序号 > `[throw frame]`;
4. `[passive object index]` 非 0;
5. 剩余次数非 0。

发射时按 N 个创建被动对象,置已投标志,播发射音效;剩余次数减 1,若还大于 1 就再请求一次出招状态(下一轮动画重播、再投)。**待机时每次更新把剩余次数重置成 `[attack number]`**,所以被打断的出招不会"欠"次数。

进入出招状态时还有一次 15% 的出招语音掷骰(类别 = 出招号);当前实现每次出招都放语音,与此不符,下一步修正。

## 3. 扔在哪(E-103)

创建位置 = 怪物位置 + 起点偏移,**x 偏移随朝向镜像**(朝右加、朝左减),y、z 不镜像;朝向见上表 `[direction]`。创建之后的行为在客户端的受保护区域里(蹦床 0x00877f50 与 0x00877bc0,读取时按 E-059 的授权设置了 `DNF_ALLOW_PROTECTED=1`,结果没读懂),所以怎么飞全靠 `.ptl` 的字段加推断。

## 4. `.ptl`(E-103)

加载函数 0x0131c0e0。只列我们读的几项:

- `[x axis]`、`[y axis]`、`[z axis]`:后面的 `[move type]`、`[move variable 1/2]`、`[changing type]`、`[changing term]` 都归到这个轴(没写轴时是 x)。
- `[move type]`:`[stop]`(0)、`[straight line]`(1)、`[sin]`(2)、`[cos]`(3);`[move variable 1]`、`[move variable 2]` 每轴各两个整数。
- `[changing type]`:`[keep changing]`(0)、`[keep last value]`(1)、`[ping pong]`(2)。
- `[landing type]`:`[pierce]`(0)、`[slide]`(1)、`[stop]`(2)、`[vanish]`(3),全局一个。
- `[life time]`、`[gravity]`、`[creation pos]`;另有 `[rotation angle]`、`[transparent value]`、`[object]` 发射器块,我们不解释。

`.mob` 里写的 `.ptl` 路径有两种写法:相对 `.mob` 所在目录(约五分之四),或从 PVF 根目录起的完整路径(约五分之一);同一个引用只会符合其中一种。实现先按 `.mob` 目录找、再按根目录找。写成空串的块,放出的对象不带粒子定义。

## 5. 我们的实现

| 部分 | 位置 | 内容 |
|---|---|---|
| 记录 | `Dnf.Game`:`ThrowAttackRecord` | 块的解析与缺省值;`ShotCount`(N,限制在 0 到 64)、`StartOffset(k)`、`IsReversed(k)`、`ParticleFile(k)` |
| 状态机 | `Dnf.Game`:`ThrowAttackRunner` | 进出招清标志、待机重置剩余次数、帧序号 > 发射帧才发射、同一次出招里重复投 |
| 粒子 | `Dnf.Game`:`ParticleDef`、`GameData.GetParticle`/`GetParticleFor` | `.ptl` 的轴运动、`[life time]`、`[landing type]`、`[gravity]`;两种路径写法 |
| 投射物 | `Dnf.Play`:`ProjectileTemplate`、`MonsterThrows`、`MonsterProjectile` | 模板 = `.obj` 的基础动画与 `[attack info]` + 粒子定义;投射物沿朝向直线飞、按动画帧画、当前帧的攻击框(类型 15) |
| 接线 | `MonsterActor`(`SetThrows`、`TakeThrown`)、`RoomCombatSession` | 出招与待机喂给状态机;发射的投射物进会话;每步移动并与主角受击框求交;命中走怪物还手同一条路(`StrikeHero`:发射者的等级与攻击力 + 投射物 `.obj` 的 `[attack info]`);发射音效记为效果音;画面里画出投射物 |

**推断与假设(代码里单独标出,不要当成原版行为):**

- 速度 = x 轴直线运动 `[move variable 1]` 的第一个整数,单位像素每秒;y、z 轴的直线运动同理;sin、cos、重力与落地处理**不模拟**(这些投射物原地不动或只按直线分量飞)。
- 寿命(毫秒)= `.ptl` 的 `[life time]` 与 `.obj` 的 `[time limite]` 里写了的较短者;都没写时,"动画播完就消失"的取动画时长,否则封顶 3000 毫秒。
- 打中主角就消失(`[pierce]` 是否真的穿透没读到);`[power]` 忽略;投射物没有影子。
- `[passive object filename]` 为空串的块:对象不带粒子定义,原地不动到寿命结束;它们靠自己的 `.obj` 脚本动,这条路没接。

## 6. 全库结构检查(`dnf mob-check`,门槛)

不到百分之三的怪物脚本带 `[throw attack]`(51 个文件、85 个块)。门槛要求:招号都落在 `[attack motion]` 的范围里、都有发射帧与非 0 的被动对象编号、编号都能找到 `.obj` 且读到基础动画、写了路径的 `.ptl` 都读得到;不符合就退出码非 0。只报告不设门槛的:各个表与 `[passive object number]` 个数对不上的块(个别)、`[attack number]` 大于 1 的块、写成空串的 `.ptl`、只在 x 轴直线飞的 `.ptl` 占多少(约四成)。

真实数据里,`combat-check` 在一间含投掷手的房间里看到它按间隔扔出石头、石头朝主角飞;画面核对过起点在怪物手边、朝主角方向、有动画。

## 7. 没读通 / 没做

1. 粒子运行时:`[move variable 1]` 两个数的确切含义、sin/cos 的振幅与周期、重力、落地处理、`[pierce]`。
2. `[power]`、`[start kind]` 取 1、`[object type]` 的作用。
3. `this+0x670` 守卫与创建函数里调用的虚函数槽 0x738 的对象。
4. 空串 `.ptl` 的被动对象自己怎么动(`.obj` 脚本)。
5. 投射物的影子与绘制层次、投射物能否被打掉、与主角技能的相互作用。
6. 出招语音 15% 概率(下一步)。

## 8. 复现

```
dotnet test DnfUnity.slnx -c Release                       # ThrowAttackTests、MonsterThrowTests
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll mob-check   # [throw attack] 结构检查(门槛)
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map <含投掷手的房间> --seconds 14
```
