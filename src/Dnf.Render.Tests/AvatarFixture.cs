using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;

namespace Dnf.Render.Tests;

/// <summary>
/// 在临时目录里造一个"迷你职业":一份角色脚本、几件装备脚本、站立与行走两个 .ani、几张 2×2 的 IMG 和一张带可走区域的地图。
/// 同一份数据里还有三段普攻(.chr 的 [attack motion] 与武器图层里带攻击框的动画)和一种"迷你怪物"(怪物目录、.mob、动作动画与 IMG)。
/// 全部由合成数据按规格构造(规格与命名规则见证据 E-065、E-069),不含任何原版内容。
/// </summary>
internal sealed class AvatarFixture : IDisposable
{
    public const string ChrPath = "character/test/test.chr";
    public const string NoAttackChrPath = "character/test/noattack.chr";

    /// <summary>有受击动作([damage motion 1]、[damage motion 2],同 <see cref="ActionChrPath"/>)但没有倒地动作的角色脚本:被打只走普通受击(证据 E-105)。</summary>
    public const string HurtOnlyChrPath = "character/test/hurtonly.chr";

    /// <summary>
    /// 带奔跑与跳跃的角色脚本:除了 <see cref="ChrPath"/> 有的之外,还有 [dash motion](四帧 × 100 毫秒、循环)、[dashattack motion](三帧 × 100 毫秒)、
    /// [jump motion](八帧:100、100、80、停留 10000、100、80、停留 10000、落地 50 毫秒)、[jumpattack motion](100、50、50 毫秒)、
    /// [jump power] <see cref="JumpPowerStat"/>、[weight] <see cref="WeightStat"/>、[jump speed] <see cref="JumpSpeedStat"/>
    /// 与 [width] <see cref="BodyWidthStat"/> <see cref="BodyDepthStat"/>(纯属测试用的数)。
    /// </summary>
    public const string ActionChrPath = "character/test/action.chr";
    public const int JumpPowerStat = 400;
    public const int WeightStat = 60000;
    public const int JumpSpeedStat = 90;
    public const int BodyWidthStat = 36;
    public const int BodyDepthStat = 12;

    /// <summary>主角脚本 [skill] 里的主动技能编号(与原版同号:鬼斩 5、上挑 46、后跳 169);上挑的等级是 3,冷却在 1 级与最高级(10 级)之间线性插值。</summary>
    public const int HardAttackSkill = 5;
    public const int UpperSlashSkill = 46;
    public const int BackStepSkill = 169;
    public const int UpperSlashLevel = 3;
    public const int UpperSlashCoolStart = 2000;
    public const int UpperSlashCoolEnd = 1000;
    public const int UpperSlashMpStart = 5;
    public const int UpperSlashMpEnd = 35;

    /// <summary>鬼斩的冷却(两端相同)与后跳的耗蓝:都是测试用的合成数,与原版数据无关。</summary>
    public const int HardAttackCool = 4500;
    public const int HardAttackMp = 13;
    public const int BackStepMp = 2;

    /// <summary>后跳 .skl 的 [static data] 第 0 个数(起跳力,纯属测试用的数)。</summary>
    public const int BackStepPowerStat = 270;

    /// <summary>
    /// 鬼斩:[etc motion] 的第 1 项是它的动作动画(第 0 项是别的动作,所以"在动作表里的下标"与"在攻击信息表里的下标"故意不同),
    /// [etc attack info] 的第 0 项是它的攻击信息;1 级的伤害两个数(百分比、固定值)是 .skl [level info] 第一行,击退像素是 .atk 的 [push aside](都是测试用的数)。
    /// 动作 600 毫秒,第 2、3 帧(200 到 400 毫秒)各有一个攻击框:x 从 +20 起宽 60,y 从 −10 起深 20,z 从 0 起高 50。
    /// </summary>
    public const string HardAttackAni = "character/test/animation/hardattack.ani";
    public const string HardAttackAtk = "character/test/attackinfo/hardattack.atk";
    public const string SoundAtk = "character/test/attackinfo/sound.atk";

    /// <summary>带三个(外加一个超出的)[active status] 槽、[human active status rate] 与火属性的攻击信息(M43,合成)。</summary>
    public const string StatusAtk = "character/test/attackinfo/status.atk";
    public const int HardAttackRate = 200;
    public const int HardAttackFixed = 40;
    public const int HardAttackPushAside = 150;
    public const int HardAttackMotionMs = 600;

    /// <summary>
    /// 多段技能(M37,证据 E-129)的合成数据:技能编号 <see cref="ChainSkill"/>,[etc motion] 第 2、3 项是两段动作(<see cref="ChainAAni"/> 400 毫秒、<see cref="ChainBAni"/> 300 毫秒),
    /// [etc attack info] 第 1、2 项是两段的攻击信息;.skl 的 [static data] 是 (200, −400, 100, −100),[level info] 四列 (第 1 级 150、250、30、50,第 2 级 180、300、40、60)。
    /// A 段第 2 帧(200 到 300 毫秒)有攻击框,B 段第 1 帧(100 到 200 毫秒)有;两段的特效(<see cref="ChainEffect1"/>、<see cref="ChainEffect2"/>)是 350 毫秒的合成动画,
    /// 技能预载脚本(<c>sqr/common.nut</c>)里按这个顺序登记。数全是测试用的。
    /// </summary>
    public const int ChainSkill = 901;
    public const string ChainAAni = "character/test/animation/chaina.ani";
    public const string ChainBAni = "character/test/animation/chainb.ani";
    public const string ChainAAtk = "character/test/attackinfo/chaina.atk";
    public const string ChainBAtk = "character/test/attackinfo/chainb.atk";
    public const string ChainEffect1 = "character/test/effect/animation/chain1.ani";
    public const string ChainEffect2 = "character/test/effect/animation/chain2.ani";
    public const int ChainAPushAside = 120;
    public const int ChainBPushAside = 200;
    public const int ChainMotionAMs = 400;
    public const int ChainMotionBMs = 300;

    /// <summary>
    /// 波系技能(M42,证据 E-135)的合成数据:技能编号 <see cref="WaveSkill"/>,[etc motion] 第 4 项是施法动作 <see cref="WaveCastAni"/>(六帧:第 0 帧停 10000 毫秒是蓄力占位,
    /// 第 1 到 5 帧各 50 毫秒;第 4 帧带一个攻击框,用来检查"施法动作自己不命中");.skl 的 [casting time] 是 <see cref="WaveCastMs"/>,
    /// [static data] 是 (250, 160, −320),[level info] 三列 (第 1 级 11、100、222,第 2 级 12、101、333),消耗魔法 15、冷却 2500。
    /// 预载特效 <see cref="WaveEffect"/> 登记在 <c>sqr/common.nut</c>。放出的被动对象 <see cref="WaveObjectId"/>(.obj <see cref="WaveObj"/>,老式:只有 [basic motion] 与 [attack info])
    /// 的动画 <see cref="WaveObjAni"/> 四帧各 50 毫秒、不循环,第 0、1 帧各有一个攻击框(x 从 +20 起宽 120,y 从 −20 起深 40,z 从 0 起高 100);攻击信息 <see cref="WaveObjAtk"/>:
    /// 击倒、击退 <see cref="WavePushAside"/>、命中声 <see cref="WaveHitSound"/>、魔法攻击。数全是测试用的。
    /// </summary>
    public const int WaveSkill = 903;
    public const string WaveCastAni = "character/test/animation/wavecast.ani";
    public const string WaveEffect = "character/test/effect/animation/waveslash.ani";

    /// <summary>
    /// 波系技能预载的第二个特效(M44,证据 E-137):四帧——空帧 50、图 50、图 50(带关键帧标志 <see cref="WaveEffect2Flag"/>)、空帧 100 毫秒,
    /// 标志帧从 <see cref="WaveEffect2FlagMs"/> 毫秒起。
    /// </summary>
    public const string WaveEffect2 = "character/test/effect/animation/waveslash2.ani";
    public const int WaveEffect2Flag = 7;
    public const int WaveEffect2FlagMs = 100;
    public const int WaveObjectId = 9301;
    public const string WaveObj = "passiveobject/hero/wave.obj";
    public const string WaveObjAni = "passiveobject/hero/animation/wave.ani";
    public const string WaveObjAtk = "passiveobject/hero/attackinfo/wave.atk";
    public const int WavePushAside = 260;
    public const int WaveCastMs = 300;
    public const string WaveHitSound = "TEST_WAVE_HIT";

    /// <summary>
    /// 鬼斩的两层特效(角色目录下 effect/animation/):都是 350 毫秒——空帧 150、图 50、图 50、空帧 100(原版的斩击光影也是先空一段再出图);
    /// 第二层每帧带图形特效 2(加法混合)。图用皮肤 IMG 的第 0、1 帧,帧偏移 (-5, -8)。
    /// </summary>
    public const string HardAttackEffect1 = "character/test/effect/animation/hardattack1.ani";

    /// <summary>升级光效的动画路径(与产品代码里 RoomCombatSession.LevelUpEffectPath 同一个字符串,证据 E-111);内容是一段合成的不循环动画。</summary>
    public const string LevelUpEffect = "Common/CommonEffect/Animation/LevelUp.ani";
    public const string HardAttackEffect2 = "character/test/effect/animation/hardattack2.ani";
    public const int EffectDurationMs = 350;

    /// <summary>
    /// 通关营地的合成数据(<c>ClearCamp</c>):营火动作脚本(一个 [MOTION],[BASE ANI] 是 200 毫秒不循环的木柴、[SUB ANI] 两层:偏移 (10, −5) 的循环光晕与偏移 (0, 0) 的同一块木柴)、
    /// NPC 目录(编号 <see cref="CampKeeperId"/>、<see cref="CampGuestId"/> 带 [visible on dungeon clear] 1,<see cref="CampPlainId"/> 没有)、一张 [NPC] 里依次写这三个的地图
    /// (守卫坐标 (0, 0, 0) 朝 [left],客人写明 (500, 60, 0) 朝 [right],普通 NPC 写 (0, 0, 0))。动画用皮肤 IMG 的第 0、1 帧(循环的各 100 毫秒)。
    /// </summary>
    public const string CampfireAct = "passiveobject/actionobject/common/action/campfire.act";
    public const string CampBaseAni = "passiveobject/actionobject/common/animation/campbase.ani";
    public const string CampGlowAni = "passiveobject/actionobject/common/animation/campglow.ani";
    public const string CampMapPath = "map/test/camp.map";
    public const int CampKeeperId = 7001;
    public const int CampGuestId = 7002;
    public const int CampPlainId = 7003;
    public const string CampKeeperScript = "npc/camp/keeper.npc";
    public const string CampKeeperAni = "npc/camp/animation/keeper.ani";
    public const string CampGuestAni = "npc/camp/animation/guest.ani";
    public const string CampPlainScript = "npc/camp/plain.npc";
    public const string DashAni = "character/test/animation/dash.ani";
    public const string DashAttackAni = "character/test/animation/dashattack.ani";
    public const string JumpAni = "character/test/animation/jump.ani";
    public const string JumpAttackAni = "character/test/animation/jumpattack.ani";

    /// <summary>主角脚本里合成的 [move speed](纯属测试用的数)。</summary>
    public const int MoveSpeedStat = 400;
    public const string JobTag = "[tester]";
    public const string EquipRoot = "equipment/character/test";
    public const string WeaponEqu = "weapon/sword/sw_start.equ";

    /// <summary>第二把夹具武器(只有一个图层 swordb1);换装备栏武器的测试用它。</summary>
    public const string SecondWeaponEqu = "weapon/sword/sw_digit.equ";

    /// <summary>两把夹具武器脚本里的 [equipment physical attack] / [equipment magical attack] 第一个数(合成的,供装备加成的测试用)。</summary>
    public const int WeaponStartPhysical = 10;
    public const int WeaponStartMagical = 4;
    public const int SecondWeaponPhysical = 25;
    public const int SecondWeaponMagical = 9;

    /// <summary>夹具武器的 [sub type](写在各职业的 [animation job] 块之后,证据 E-077)。</summary>
    public const int WeaponSubType = 4;
    public const string AvatarImageRoot = "sprite/character/test/equipment/avatar";
    public const string WeaponImageRoot = "sprite/character/test/equipment/weapon";
    public const string AreaMap = "map/test/area.map";
    public const string RestAni = "character/test/animation/rest.ani";
    public const string MoveAni = "character/test/animation/move.ani";
    public const string WaitAni = "character/test/animation/wait.ani";

    /// <summary>三段普攻的基础动画(各 400、300、400 毫秒);第三段自己带攻击框(帧 1)。</summary>
    public const string Attack1Ani = "character/test/animation/attack1.ani";
    public const string Attack2Ani = "character/test/animation/attack2.ani";
    public const string Attack3Ani = "character/test/animation/attack3.ani";

    /// <summary>
    /// 受击动作(<see cref="ActionChrPath"/> 的 [damage motion 1]、[damage motion 2]):各一帧、停留 10000 毫秒(与原版的受击动画同形,停多久由游戏逻辑决定);
    /// 皮肤 IMG 的帧号分别是 0 与 1(两段的路径不同,用路径分辨放的是哪一段)。
    /// </summary>
    public const string Damage1Ani = "character/test/animation/damage1.ani";
    public const string Damage2Ani = "character/test/animation/damage2.ani";

    /// <summary>
    /// 倒地动作(<see cref="ActionChrPath"/> 的 [down motion],证据 E-105):与原版的主角倒地动画同形的合成版——
    /// 一帧 80 毫秒的起手、两个长停留帧(各 10000 毫秒:第 2、3 帧,最高点与落地前的两个相位由游戏逻辑放行)、一帧 400 毫秒的躺着(最后一个长停留帧的下一帧);
    /// 前三帧带站立高度的受击框 <see cref="HeroHurtBox"/>,躺着那一帧的受击框是贴地的扁框 <see cref="HeroLyingBox"/>(z 从 0 起高 20)。
    /// </summary>
    public const string DownAni = "character/test/animation/down.ani";

    /// <summary>主角躺着时的受击框(类型 14):x 从 −30 起宽 60,y 从 −6 起深 12,z 从 0 起高 20(纯属测试用的数)。</summary>
    public static readonly (ushort Type, int[] Values) HeroLyingBox = ((ushort)14, new[] { -30, -6, 0, 60, 12, 20 });

    /// <summary>主角基础动作(站立、等待、行走、受击)每一帧的受击框(类型 14):x 从 −12 起宽 24,y 从 −6 起深 12,z 从 0 起高 80(纯属测试用的数)。</summary>
    public static readonly (ushort Type, int[] Values) HeroHurtBox = ((ushort)14, new[] { -12, -6, 0, 24, 12, 80 });

    /// <summary>武器图层 swordb、swordc 的动作动画所在目录(装备脚本所在目录下的 &lt;层名&gt;/)。</summary>
    public const string WeaponLayerB = EquipRoot + "/weapon/sword/swordb";
    public const string WeaponLayerC = EquipRoot + "/weapon/sword/swordc";

    /// <summary>怪物目录、怪物 .mob、怪物动画目录与 IMG。编号 1 是完整的迷你怪物,2 只有待机动作,3 的路径指向不存在的脚本。</summary>
    public const string MonsterListPath = "monster/monster.lst";
    public const string BlobMobPath = "monster/blob/blob.mob";

    /// <summary>
    /// 倒地动画每一帧都有受击框、有两个长停留帧的迷你怪物(与原版地精的倒地动画同形:两帧短、两帧长停留、一帧躺着;证据 E-083),
    /// 用来测空中还能不能被打到、颠一下换帧。其余动作与 <see cref="BlobMobPath"/> 相同。
    /// </summary>
    public const string BlobBoxedDownMobPath = "monster/blob/boxed.mob";

    /// <summary>倒地动作是一段没有长停留帧的短动画(三帧各 80 毫秒):倒地状态机只能靠时间走完。</summary>
    public const string BlobShortDownMobPath = "monster/blob/short.mob";

    /// <summary>
    /// 脚本怪物(M36,证据 E-128):不写 [waiting motion] 这类旧式标签,而写指向 .act 的 [waiting action] 等;[attack action] 三项依次是
    /// 正常的 Attack.act、不存在的 .act、没有 [BASE ANI] 的 .act;[attack info] 两项。.act 在 <see cref="BlobActionDir"/>,[BASE ANI] 写 "../Animation_Blob/…"。
    /// </summary>
    public const string BlobScriptedMobPath = "monster/blob/scripted.mob";
    public const string BlobActionDir = "monster/blob/action";

    /// <summary>旧式与 action 标签并存的怪物:待机两种标签都有(旧式优先)、行走只有 [move action]、攻击列表写成 [action list]。</summary>
    public const string BlobMixedMobPath = "monster/blob/mixed.mob";

    /// <summary>所有动画都没有受击框的怪物(占位、机关一类;主角永远打不到它,M36,证据 E-128):待机、行走各一段、没有别的动作。</summary>
    public const string BlobGhostMobPath = "monster/blob/ghost.mob";

    /// <summary>
    /// 有两个攻击动作的迷你怪物([attack motion] 依次是 Attack1.ani、<see cref="BlobStrikeAni"/>,[attack info] 依次是一个不存在的脚本与 <see cref="BlobStrikerAtk"/>):
    /// 测"决策挑了第几招就放第几招的动画与攻击信息"。其余动作与 <see cref="BlobMobPath"/> 相同。
    /// </summary>
    public const string BlobDuoMobPath = "monster/blob/duo.mob";

    /// <summary>
    /// 会伤人的迷你怪物:出招动画 <see cref="BlobStrikeAni"/> 四帧各 80 毫秒,第 2 帧(160 到 240 毫秒)带一个攻击框(类型 15):
    /// x 从 +10 起宽 40,y 从 −10 起深 20,z 从 0 起高 50(纯属测试用的数);出招的攻击信息 <see cref="BlobStrikerAtk"/> 是 [damage bonus]
    /// <see cref="StrikerDamageBonus"/>、普通受击([damage reaction] [damage])。其余动作与 <see cref="BlobMobPath"/> 相同。
    /// </summary>
    public const string BlobStrikerMobPath = "monster/blob/striker.mob";
    public const string BlobStrikerAtk = "monster/blob/attackinfo/striker.atk";
    public const string BlobStrikeAni = "monster/blob/animation_blob/strike.ani";
    public const int StrikerDamageBonus = 25;

    /// <summary>出招攻击框(类型 15)的数。</summary>
    public static readonly (ushort Type, int[] Values) BlobStrikeBox = ((ushort)15, new[] { 10, -10, 0, 40, 20, 50 });

    /// <summary>
    /// 会扔东西的迷你怪物(M15,证据 E-103):出招动画 <see cref="BlobThrowAni"/> 四帧各 80 毫秒(只有受击框,自己不伤人);.mob 里一个 [throw attack] 块:
    /// 第 0 招、放 1 个被动对象 <see cref="ShotObjectId"/>、发射帧 1(动画到第 2 帧、160 毫秒时发射)、起点偏移 (<see cref="ShotStartX"/>, 0, <see cref="ShotStartZ"/>)、
    /// 发射声音 <see cref="ShotThrowSound"/>。投射物:.ptl <see cref="ShotParticle"/> 沿 x 轴直线飞,速度 <see cref="ShotSpeed"/>(像素/秒)、[life time] <see cref="ShotLifeMs"/> 毫秒;
    /// .obj <see cref="ShotObj"/> 的 [basic motion] 是两帧循环(各 100 毫秒)的 <see cref="ShotAni"/>,带一个攻击框(类型 15):x 从 −8 起宽 16,y 从 −8 起深 16,z 从 0 起高 16;
    /// [attack info] 是 [damage bonus] <see cref="ShotDamageBonus"/> 的 <see cref="ShotAtk"/>。数全是测试用的。
    /// </summary>
    public const string BlobThrowerMobPath = "monster/blob/thrower.mob";
    public const string BlobThrowAni = "monster/blob/animation_blob/throw.ani";
    public const string ShotParticle = "monster/blob/particle/shot.ptl";

    /// <summary>另一个 .ptl,放在 PVF 根目录下的完整路径上(.mob 里写 "Common/Particle/Shot.ptl"),速度 <see cref="AbsShotSpeed"/>:用来证明读 .ptl 时先按 .mob 目录找、再按根目录找。</summary>
    public const string AbsShotParticle = "common/particle/shot.ptl";
    public const int AbsShotSpeed = 120;
    public const int ShotObjectId = 9001;
    public const string ShotObj = "passiveobject/blob/shot.obj";
    public const string ShotAni = "passiveobject/blob/animation/shot.ani";
    public const string ShotAtk = "passiveobject/blob/attackinfo/shot.atk";
    public const int ShotSpeed = 200;
    public const int ShotLifeMs = 1000;
    public const int ShotStartX = 30;
    public const int ShotStartZ = 40;
    public const int ShotDamageBonus = 25;
    public const string ShotThrowSound = "THROW_BLOB";
    public static readonly (ushort Type, int[] Values) ShotBox = ((ushort)15, new[] { -8, -8, 0, 16, 16, 16 });

    /// <summary>
    /// 出招靠 .act 触发器的迷你怪物与它们放出的被动对象(M36-4,证据 E-130)。怪物 <see cref="ActMobPath"/>(名字) 的出招动画是 <see cref="BlobThrowAni"/>
    /// (四帧各 80 毫秒、只有受击框,自己不伤人)或 <see cref="BlobStrikeAni"/>(第 2 帧、160 到 240 毫秒带攻击框),各招的 .act 在 <see cref="BlobActionDir"/> 下:
    /// plain(只有动画,没有触发器,对照)、shoot(出招开始起 X 速度 100、帧 1 放 <see cref="ActShotId"/>、帧 2 速度归零)、
    /// chain(帧 1 立即切第 1 招 shoot)、chainend(帧 1 [END] 切第 1 招 plain)、rectoff(出招开始关攻击框)、rectreset(帧 3 重置攻击框再拨回帧 2,限一次)、
    /// skip(帧 1 拨到帧 3,跳过攻击框那一帧)、stand(帧 1 立即回待机)、reverse(帧 1 反转朝向)、jump(出招开始起 Z 速度 300)、
    /// onhit(近战一击打中主角后起 X 速度 100)、dmgoff(出招开始关受击盒、帧 3 再开)、dmgkeep(出招开始关受击盒、不再开)、hover(出招开始浮到 60)、
    /// land(出招开始浮到 30、帧 3 取消浮空)、tele(出招开始瞬移到自己前方偏移 (50, 0, 30),落在半空)、shake(出招开始震屏,强度 6、200 毫秒)、
    /// 以及 spawn&lt;编号&gt;(帧 1 在偏移 (30, 0, 40) 放对应的被动对象)。
    /// 被动对象都在 <see cref="ActObjectDir"/> 下:<see cref="ActShotId"/> 出生后以 X 速度 200 飞,打中主角就切自定义动作 0(三帧、没有攻击框的 boom),播完消失,限时 1000 毫秒;
    /// <see cref="ActRepeatId"/> 不动、带攻击框,每次动画回到第 0 帧重置攻击框(所以隔一圈就能再打一次),限时 1000 毫秒;
    /// <see cref="ActGraveId"/> 不动、带攻击框,打中主角就销毁自己;<see cref="ActSpawnerId"/> 没有攻击框,出生就在自己的位置放一个 <see cref="ActGraveId"/>;
    /// <see cref="ActFallId"/> 没有攻击框,出生起 Z 速度 300(受重力落回地面);限时 1000 毫秒;<see cref="ActForeverId"/> 没有任何消失条件、没有触发器。数全是测试用的。
    /// M41(证据 E-134)再加四只召唤怪:summon(帧 1 召唤怪物 <see cref="SummonedCode"/>,等级 2、偏移 (30, 0, 0))、
    /// summontimed(同上,等级写 0,带 [SUMMON TIME] <see cref="SummonLifeMs"/>)、summonkill(帧 1 同 summon,帧 3 对 [SUMMON OBJECT] 执行 [SET HP] 0)、
    /// summononce(帧 1 同 summon,随后 [TRIGGER] 0 [OFF] 把自己的触发器关掉:开关跨出招保留,只召唤一次)、
    /// restore(帧 1 [RESTORE] [HP] -50 [%]:每次出招扣掉自己最大生命的一半)。
    /// </summary>
    public const int ActForeverId = 9206;

    /// <summary>召唤用的怪物编号(.act 里写的是编号,真正造出什么由场景的工厂决定)与召唤物的存活时间(毫秒)。</summary>
    public const int SummonedCode = 7;
    public const int SummonLifeMs = 300;
    public const int ActShotId = 9201;
    public const int ActRepeatId = 9202;
    public const int ActGraveId = 9203;
    public const int ActSpawnerId = 9204;
    public const int ActFallId = 9205;
    public const string ActObjectDir = "passiveobject/blob";
    public const string ActBoomAni = "passiveobject/blob/animation/boom.ani";
    public const string ActIdleAni = "passiveobject/blob/animation/idle.ani";

    public static string ActMobPath(string name) => "monster/blob/act_" + name + ".mob";
    public const string BlobAnimationDir = "monster/blob/animation_blob";
    public const string BlobImage = "sprite/monster/blob/blob.img";

    /// <summary>
    /// 可破坏物件(M34,证据 E-126):全放在 breakableobject 目录下,基础动画是一帧带受击框(<see cref="BlobHurtBox"/>)的循环动画。
    /// 木箱 <see cref="CrateObjectId"/>:[int data] 的第 1、2、3 个数是 1 2 1(打 2 下、打够消失),有一个两帧(各 80 毫秒)的受击动画与一个空槽,
    /// 受击声 <see cref="CrateHitSound"/>、破坏声 <see cref="CrateCrashSound"/>;木桶 <see cref="BarrelObjectId"/>:打 1 下就消失,没有受击动画;
    /// 石柱 <see cref="PillarObjectId"/>:打 2 下、不消失(只触发一次),有受击动画,受击声 <see cref="PillarHitSound"/>;
    /// 草丛 <see cref="GrassObjectId"/>:[int data] 只有两个数,打不坏;无框箱 <see cref="GhostObjectId"/>:基础动画没有受击框,战斗里创建不出来;
    /// 石头 <see cref="RockObjectId"/>:有 [int data] 与受击框,但不在 breakableobject 目录里,不算可破坏物件。
    /// </summary>
    public const int CrateObjectId = 9101;
    public const int BarrelObjectId = 9102;
    public const int PillarObjectId = 9103;
    public const int GrassObjectId = 9104;
    public const int GhostObjectId = 9105;
    public const int RockObjectId = 9106;
    public const string CrateHitSound = "CRATE_HIT";
    public const string CrateCrashSound = "CRATE_CRASH";
    public const string PillarHitSound = "PILLAR_HIT";
    public const string BreakableDir = "passiveobject/mapobject/breakableobject";

    /// <summary>受击框(类型 14):x 从 −10 起宽 40(故意不对称,朝向才看得出镜像),y 从 −6 起深 12,z 从 0 起高 60。</summary>
    public static readonly (ushort Type, int[] Values) BlobHurtBox = ((ushort)14, new[] { -10, -6, 0, 40, 12, 60 });

    /// <summary>波系对象每个攻击帧的攻击框:x 从 +20 起宽 120,y 从 −20 起深 40,z 从 0 起高 100(见 <see cref="WaveObjectId"/>)。</summary>
    public static readonly (ushort Type, int[] Values) WaveObjBox = ((ushort)15, new[] { 20, -20, 0, 120, 40, 100 });

    /// <summary>每个 IMG 的第 k 帧,红色通道 = 基色 + k,用来判断精灵取的是哪个 IMG 帧。</summary>
    public static readonly (int R, int G, int B) SkinColor = (200, 10, 10);
    public static readonly (int R, int G, int B) CoatAColor = (10, 200, 10);
    public static readonly (int R, int G, int B) CoatBColor = (10, 10, 200);
    public static readonly (int R, int G, int B) HairColor = (200, 200, 10);
    public static readonly (int R, int G, int B) SwordBColor = (60, 60, 60);
    public static readonly (int R, int G, int B) SwordCColor = (250, 250, 250);

    private const byte TagType = (byte)TokenType.Tag;
    private const byte IntType = (byte)TokenType.Int;
    private const byte StrType = (byte)TokenType.String;

    private readonly List<string> _pool = new();

    public string Root { get; }

    /// <summary>按"标记 / 字符串 / 整数"一个个拼出标记流脚本。</summary>
    private sealed class Script
    {
        private readonly AvatarFixture _owner;
        private readonly List<(byte Type, int Value)> _tokens = new();

        public Script(AvatarFixture owner)
        {
            _owner = owner;
        }

        public Script Tag(string tag)
        {
            _tokens.Add((TagType, _owner.Intern(tag)));
            return this;
        }

        public Script Str(params string[] texts)
        {
            foreach (string text in texts)
            {
                _tokens.Add((StrType, _owner.Intern(text)));
            }
            return this;
        }

        public Script Int(params int[] values)
        {
            foreach (int v in values)
            {
                _tokens.Add((IntType, v));
            }
            return this;
        }

        /// <summary>浮点标记(真实的 .chr 里 [move speed] 之类的属性就是浮点标记)。</summary>
        public Script Float(params float[] values)
        {
            foreach (float v in values)
            {
                _tokens.Add(((byte)TokenType.Float, BitConverter.SingleToInt32Bits(v)));
            }
            return this;
        }

        public byte[] Build()
        {
            return TestData.BuildTokenScript(_tokens);
        }

        /// <summary>一个 [TRIGGER] 块:<paramref name="records"/> 写条件记录,最后是 [DO BEHAVIOR] 目标 <paramref name="target"/> 与行为组编号 <paramref name="behavior"/>。</summary>
        public Script Trigger(Action<Script> records, int behavior, string target = "[ME]")
        {
            Tag("[TRIGGER]");
            records(this);
            return Tag("[DO BEHAVIOR]").Tag(target).Int(behavior).Tag("[/TRIGGER]");
        }

        /// <summary>一个 [BEHAVIOR] 块(行为组)。</summary>
        public Script Behavior(Action<Script> body)
        {
            Tag("[BEHAVIOR]");
            body(this);
            return Tag("[/BEHAVIOR]");
        }

        /// <summary>[SET SPEED] 某一轴的速度:<paramref name="axis"/> 是 "[X AXIS]"、"[Y AXIS]" 或 "[Z AXIS]"。</summary>
        public Script SetSpeed(string axis, int value)
        {
            return Tag("[SET SPEED]").Tag(axis).Int(value).Tag("[/SET SPEED]");
        }

        /// <summary>[SUMMON MONSTER],怪物编号 <paramref name="index"/>、等级 <paramref name="level"/>、偏移 (x, y, z),再接 <paramref name="flags"/> 里写的标志子标签。</summary>
        public Script Summon(int index, int level, int x, int y, int z, Action<Script> flags = null)
        {
            Tag("[SUMMON MONSTER]").Tag("[INDEX]").Int(index).Tag("[LEVEL]").Int(level).Tag("[POS]").Int(x, y, z);
            flags?.Invoke(this);
            return Tag("[/SUMMON MONSTER]");
        }

        /// <summary>[CREATE PASSIVEOBJECT],被动对象 <paramref name="index"/>,偏移 (x, y, z)。</summary>
        public Script Create(int index, int x, int y, int z)
        {
            return Tag("[CREATE PASSIVEOBJECT]").Tag("[INDEX]").Int(index).Tag("[POS]").Int(x, y, z).Tag("[/CREATE PASSIVEOBJECT]");
        }
    }

    private int Intern(string text)
    {
        int at = _pool.IndexOf(text);
        if (at < 0)
        {
            _pool.Add(text);
            at = _pool.Count - 1;
        }
        return at;
    }

    private Script NewScript()
    {
        return new Script(this);
    }

    /// <summary>只有一个 [MOTION]、里面一个 [BASE ANI] 的 .act(脚本怪物的待机、行走等动作最常见的写法)。</summary>
    private byte[] ActMotion(string baseAni)
    {
        return NewScript().Tag("[MOTION]").Tag("[BASE ANI]").Str(baseAni).Tag("[/MOTION]").Build();
    }

    /// <summary>以一个 [MOTION] 开头的 .act,后面接 <see cref="Script.Trigger"/> 与 <see cref="Script.Behavior"/>。</summary>
    private Script ActWith(string baseAni)
    {
        return NewScript().Tag("[MOTION]").Tag("[BASE ANI]").Str(baseAni).Tag("[/MOTION]");
    }

    private const string ThrowActAni = "../Animation_Blob/Throw.ani";
    private const string StrikeActAni = "../Animation_Blob/Strike.ani";

    /// <summary>每只 act_&lt;名字&gt; 怪物的出招 .act 文件名表(同一个 .act 可以出现在不同怪物里)。</summary>
    private static readonly (string Name, string[] Acts)[] ActMobs =
    {
        ("plain", new[] { "ActPlain" }),
        ("shoot", new[] { "ActShoot" }),
        ("chain", new[] { "ActChain", "ActShoot" }),
        ("chainend", new[] { "ActChainEnd", "ActPlain" }),
        ("rectoff", new[] { "ActRectOff" }),
        ("rectreset", new[] { "ActRectReset" }),
        ("skip", new[] { "ActSkip" }),
        ("stand", new[] { "ActStand" }),
        ("reverse", new[] { "ActReverse" }),
        ("jump", new[] { "ActJump" }),
        ("onhit", new[] { "ActOnHit" }),
        ("spawn9201", new[] { "ActSpawn9201" }),
        ("spawn9202", new[] { "ActSpawn9202" }),
        ("spawn9203", new[] { "ActSpawn9203" }),
        ("spawn9204", new[] { "ActSpawn9204" }),
        ("spawn9205", new[] { "ActSpawn9205" }),
        ("dmgoff", new[] { "ActDmgOff" }),
        ("dmgkeep", new[] { "ActDmgKeep" }),
        ("hover", new[] { "ActHover" }),
        ("land", new[] { "ActLand" }),
        ("tele", new[] { "ActTele" }),
        ("shake", new[] { "ActShake" }),
        ("summon", new[] { "ActSummon" }),
        ("summontimed", new[] { "ActSummonTimed" }),
        ("summonkill", new[] { "ActSummonKill" }),
        ("summononce", new[] { "ActSummonOnce" }),
        ("restore", new[] { "ActRestore" }),
    };

    private IEnumerable<(string Name, byte[] Data)> ActScripts()
    {
        foreach ((string name, string[] acts) in ActMobs)
        {
            Script mob = NewScript()
                .Tag("[waiting action]").Str("Action/Stay.act")
                .Tag("[move action]").Str("Action/Move.act")
                .Tag("[damage action 1]").Str("Action/Damage1.act")
                .Tag("[damage action 2]").Str("Action/Damage2.act")
                .Tag("[down action]").Str("Action/Down.act")
                .Tag("[attack action]");
            foreach (string act in acts)
            {
                mob.Str("Action/" + act + ".act");
            }
            mob.Tag("[/attack action]").Tag("[attack info]");
            foreach (string _ in acts)
            {
                mob.Str("");
            }
            yield return (ActMobPath(name), mob.Tag("[/attack info]").Build());
        }
        // ---- 怪物的出招 .act
        yield return (BlobActionDir + "/actplain.act", ActMotion(StrikeActAni));
        yield return (BlobActionDir + "/actshoot.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 1)
            .Trigger(r => r.Tag("[FRAME]").Int(2, 2), 2)
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Behavior(b => b.Create(ActShotId, ShotStartX, 0, ShotStartZ))
            .Behavior(b => b.SetSpeed("[X AXIS]", 0))
            .Build());
        yield return (BlobActionDir + "/actchain.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actchainend.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[END]"))
            .Build());
        yield return (BlobActionDir + "/actrectoff.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[ATTACKRECT]").Tag("[OFF]"))
            .Build());
        yield return (BlobActionDir + "/actrectreset.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(3, 3), 0)
            .Behavior(b => b.Tag("[ATTACKRECT]").Tag("[RESET]").Tag("[SET FRAME]").Int(2))
            .Build());
        yield return (BlobActionDir + "/actskip.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET FRAME]").Int(3))
            .Build());
        yield return (BlobActionDir + "/actstand.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[STAND]").Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actreverse.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET DIRECTION]").Tag("[REVERSE]"))
            .Build());
        yield return (BlobActionDir + "/actjump.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[Z AXIS]", 300))
            .Build());
        yield return (BlobActionDir + "/actonhit.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON ATTACKSUCCESS]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Build());
        // M40(证据 E-133):受击盒开关、浮空高度、瞬移、震屏
        yield return (BlobActionDir + "/actdmgoff.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3), 1)
            .Behavior(b => b.Tag("[SET DAMAGE BOX]").Tag("[OFF]"))
            .Behavior(b => b.Tag("[SET DAMAGE BOX]").Tag("[ON]"))
            .Build());
        yield return (BlobActionDir + "/actdmgkeep.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[SET DAMAGE BOX]").Tag("[OFF]"))
            .Build());
        yield return (BlobActionDir + "/acthover.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[CHANGE FLOATING HEIGHT]").Int(60))
            .Build());
        yield return (BlobActionDir + "/actland.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3), 1)
            .Behavior(b => b.Tag("[CHANGE FLOATING HEIGHT]").Int(30))
            .Behavior(b => b.Tag("[CHANGE FLOATING HEIGHT]").Int(0))
            .Build());
        yield return (BlobActionDir + "/acttele.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[TELEPORT]").Tag("[POS]").Int(50, 0, 30).Tag("[/TELEPORT]"))
            .Build());
        yield return (BlobActionDir + "/actshake.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[SHAKING]").Int(6, 200))
            .Build());
        // M41(证据 E-134):召唤怪物与按创建者清场
        yield return (BlobActionDir + "/actsummon.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Summon(SummonedCode, 2, 30, 0, 0))
            .Build());
        yield return (BlobActionDir + "/actsummontimed.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Summon(SummonedCode, 0, 30, 0, 0, f => f.Tag("[SUMMON TIME]").Int(SummonLifeMs)))
            .Build());
        yield return (BlobActionDir + "/actsummonkill.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3), 1, "[SUMMON OBJECT]")
            .Behavior(b => b.Summon(SummonedCode, 2, 30, 0, 0))
            .Behavior(b => b.Tag("[SET HP]").Int(0))
            .Build());
        // 首领"每个血量段只用一次"的写法:召唤完把自己的触发器关掉;开关属于怪物、跨出招保留(E-134)
        yield return (BlobActionDir + "/actsummononce.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Summon(SummonedCode, 2, 30, 0, 0).Tag("[TRIGGER]").Int(0).Tag("[OFF]"))
            .Build());
        // 自己扣血的招式:每次出招帧 1 把自己的生命减去最大生命的一半(召唤型首领"每召唤一次掉一截血、掉光了就死"的写法)
        yield return (BlobActionDir + "/actrestore.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[RESTORE]").Tag("[HP]").Int(-50).Tag("[%]"))
            .Build());
        foreach (int id in new[] { ActShotId, ActRepeatId, ActGraveId, ActSpawnerId, ActFallId })
        {
            int spawned = id;
            yield return (BlobActionDir + "/actspawn" + id + ".act", ActWith(ThrowActAni)
                .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
                .Behavior(b => b.Create(spawned, ShotStartX, 0, ShotStartZ))
                .Build());
        }
        // ---- 被动对象:.obj 的 [basic action]/[etc action] 相对 .obj 所在目录,.act 里的 [BASE ANI] 相对 .act 所在目录
        string shotDestroy = "[destroy condition]";
        yield return (ActObjectDir + "/actshot.obj", NewScript()
            .Tag("[basic action]").Str("action/actshot.act")
            .Tag("[etc action]").Str("action/actshot_boom.act")
            .Tag("[attack info]").Str("attackinfo/shot.atk")
            .Tag("[object destroy condition]").Str(shotDestroy, "[on end of animation]", "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actshot.act", ActWith("../animation/shot.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[ON ATTACKSUCCESS]"), 1)
            .Behavior(b => b.SetSpeed("[X AXIS]", 200))
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        yield return (ActObjectDir + "/action/actshot_boom.act", ActWith("../animation/boom.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 0))
            .Build());
        yield return (ActObjectDir + "/actrepeat.obj", NewScript()
            .Tag("[basic action]").Str("action/actrepeat.act")
            .Tag("[attack info]").Str("attackinfo/shot.atk")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actrepeat.act", ActWith("../animation/shot.ani")
            .Trigger(r => r.Tag("[FRAME]").Int(0, 0), 0)
            .Behavior(b => b.Tag("[ATTACKRECT]").Tag("[RESET]"))
            .Build());
        yield return (ActObjectDir + "/actgrave.obj", NewScript()
            .Tag("[basic action]").Str("action/actgrave.act")
            .Tag("[attack info]").Str("attackinfo/shot.atk")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actgrave.act", ActWith("../animation/shot.ani")
            .Trigger(r => r.Tag("[ON ATTACKSUCCESS]"), 0)
            .Behavior(b => b.Tag("[DESTROY]"))
            .Build());
        yield return (ActObjectDir + "/actspawner.obj", NewScript()
            .Tag("[basic action]").Str("action/actspawner.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actspawner.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActGraveId, 0, 0, 0))
            .Build());
        yield return (ActObjectDir + "/actfall.obj", NewScript()
            .Tag("[basic action]").Str("action/actfall.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actfall.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[Z AXIS]", 300))
            .Build());
        yield return (ActObjectDir + "/actforever.obj", NewScript().Tag("[basic action]").Str("action/actidle.act").Build());
        yield return (ActObjectDir + "/action/actidle.act", ActMotion("../animation/idle.ani"));
        yield return (ActBoomAni, BuildBlobBoxesAni(false, (0, 100, new (ushort Type, int[] Values)[0]), (1, 100, new (ushort Type, int[] Values)[0]),
            (2, 100, new (ushort Type, int[] Values)[0])));
        yield return (ActIdleAni, BuildBlobBoxesAni(true, (0, 100, new (ushort Type, int[] Values)[0])));
    }

    public AvatarFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "dnf-avatar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

        var scripts = new List<(string Name, byte[] Data)>
        {
            (ChrPath, NewScript()
                .Tag("[body image path]").Str("Character/Test/Equipment/Avatar/skin/tt_body%04d.img")
                .Tag("[rest motion]").Str("animation/rest.ani")
                .Tag("[waiting motion]").Str("animation/wait.ani")
                .Tag("[move motion]").Str("animation/move.ani")
                .Tag("[move speed]").Float(MoveSpeedStat)
                .Tag("[attack motion]").Str("animation/attack1.ani").Str("Animation/Attack2.ani").Str("animation/attack3.ani").Build()),
            // 没有 [attack motion] 的角色脚本:按攻击键什么也不发生
            (NoAttackChrPath, NewScript()
                .Tag("[body image path]").Str("Character/Test/Equipment/Avatar/skin/tt_body%04d.img")
                .Tag("[rest motion]").Str("animation/rest.ani")
                .Tag("[waiting motion]").Str("animation/wait.ani")
                .Tag("[move motion]").Str("animation/move.ani").Build()),
            // 有受击动作、没有倒地动作的角色脚本:被打只走普通受击
            (HurtOnlyChrPath, NewScript()
                .Tag("[body image path]").Str("Character/Test/Equipment/Avatar/skin/tt_body%04d.img")
                .Tag("[rest motion]").Str("animation/rest.ani")
                .Tag("[waiting motion]").Str("animation/wait.ani")
                .Tag("[move motion]").Str("animation/move.ani")
                .Tag("[damage motion 1]").Str("animation/damage1.ani")
                .Tag("[damage motion 2]").Str("Animation/Damage2.ani").Build()),
            (ActionChrPath, NewScript()
                .Tag("[body image path]").Str("Character/Test/Equipment/Avatar/skin/tt_body%04d.img")
                .Tag("[rest motion]").Str("animation/rest.ani")
                .Tag("[waiting motion]").Str("animation/wait.ani")
                .Tag("[move motion]").Str("animation/move.ani")
                .Tag("[move speed]").Float(MoveSpeedStat)
                .Tag("[damage motion 1]").Str("animation/damage1.ani")
                .Tag("[damage motion 2]").Str("Animation/Damage2.ani")
                .Tag("[down motion]").Str("animation/down.ani")
                .Tag("[dash motion]").Str("animation/dash.ani")
                .Tag("[dashattack motion]").Str("Animation/DashAttack.ani")
                .Tag("[jump motion]").Str("animation/jump.ani")
                .Tag("[jumpattack motion]").Str("animation/jumpattack.ani")
                .Tag("[jump power]").Int(JumpPowerStat)
                .Tag("[weight]").Int(WeightStat)
                .Tag("[jump speed]").Int(JumpSpeedStat)
                .Tag("[width]").Int(BodyWidthStat, BodyDepthStat)
                // 初始技能表("编号 等级"成对,在 [initial value] 块里,与原版一样):181 与 174 是被动,5、46、169 是带出招指令的主动技能,顺序与原版新建剑士一样
                .Tag("[initial value]")
                .Tag("[skill]").Int(181, 2, HardAttackSkill, 1, UpperSlashSkill, UpperSlashLevel, BackStepSkill, 1, 174, 1)
                // 技能动作与攻击信息(两个表的下标不对应:护身动作没有攻击信息,鬼斩在动作表里是第 1 项、在攻击信息表里是第 0 项)
                .Tag("[etc motion]").Str("Animation/Guard.ani").Str("Animation/HardAttack.ani").Str("Animation/ChainA.ani").Str("Animation/ChainB.ani").Str("Animation/WaveCast.ani")
                .Tag("[etc attack info]").Str("AttackInfo/HardAttack.atk").Str("AttackInfo/ChainA.atk").Str("AttackInfo/ChainB.atk")
                .Tag("[attack motion]").Str("animation/attack1.ani").Str("Animation/Attack2.ani").Str("animation/attack3.ani").Build()),
            // 技能目录:职业 0 → ActionChrPath;三个主动技能与一个被动技能的 .skl(数是合成的)
            ("character/character.lst", NewScript().Int(0).Str("test/action.chr").Build()),
            ("skill/skilllist.lst", NewScript().Int(0).Str("testskill.lst").Build()),
            ("skill/testskill.lst", NewScript()
                .Int(181).Str("test/passive.skl").Int(HardAttackSkill).Str("test/hardattack.skl").Int(UpperSlashSkill).Str("test/upperslash.skl")
                .Int(BackStepSkill).Str("test/backstep.skl").Int(174).Str("test/passive.skl").Int(ChainSkill).Str("test/chain.skl").Int(WaveSkill).Str("test/wave.skl").Build()),
            // 波系技能:三列 (固定值、大小比率、百分比),[casting time] 与三个 [static data]
            ("skill/test/wave.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Str("SKILL").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(15, 15).Tag("[cool time]").Int(2500, 2500).Tag("[casting time]").Int(WaveCastMs, WaveCastMs)
                .Tag("[static data]").Int(250, 160, -320)
                .Tag("[level info]").Int(3, 11, 100, 222, 12, 101, 333).Build()),
            (WaveObj, NewScript().Tag("[basic motion]").Str("animation/wave.ani").Tag("[attack info]").Str("attackinfo/wave.atk").Build()),
            (WaveObjAtk, NewScript()
                .Tag("[attack type]").Tag("[magic]").Tag("[damage reaction]").Tag("[down]").Tag("[push aside]").Int(WavePushAside)
                .Tag("[hit wav]").Str(WaveHitSound).Build()),
            (WaveObjAni, BuildBlobBoxesAni(false,
                (0, 50, new[] { WaveObjBox }), (1, 50, new[] { WaveObjBox }), (2, 50, new (ushort Type, int[] Values)[0]), (3, 50, new (ushort Type, int[] Values)[0]))),
            ("skill/test/chain.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Str("SKILL").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(20, 20).Tag("[cool time]").Int(3000, 3000).Tag("[static data]").Int(200, -400, 100, -100)
                .Tag("[level info]").Int(4, 150, 250, 30, 50, 180, 300, 40, 60).Build()),     // 第一个数是列数(4),后面按行排:第 1 级 (150, 250, 30, 50),第 2 级 (180, 300, 40, 60)
            // 多段技能两段的攻击信息:第一段击退 ChainAPushAside,第二段击倒并击退 ChainBPushAside
            (ChainAAtk, NewScript().Tag("[damage reaction]").Tag("[damage]").Tag("[push aside]").Int(ChainAPushAside).Build()),
            (ChainBAtk, NewScript().Tag("[damage reaction]").Tag("[down]").Tag("[push aside]").Int(ChainBPushAside).Build()),
            // 技能预载脚本(与原版 sqr/common.nut 同形):常量、职业函数、按技能编号分支、依次预载特效;前面放一个别的技能的分支做干扰
            ("sqr/common.nut", Encoding.ASCII.GetBytes(
                "SKILL_OTHER_TEST <- 902;\r\nSKILL_CHAIN_TEST <- 901;\r\nSKILL_WAVE_TEST <- 903;\r\n"
                + "function sq_LoadSkillEffect_Swordman(obj, skillIndex)\r\n{\r\n"
                + "\tif (skillIndex == SKILL_OTHER_TEST)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Other.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_CHAIN_TEST)\r\n\t{\r\n\t\t// comment\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain1.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain2.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_WAVE_TEST)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/WaveSlash.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/WaveSlash2.ani\");\r\n\t}\r\n}\r\n")),
            ("skill/test/passive.skl", NewScript().Tag("[type]").Str("[passive]").Build()),
            ("skill/test/hardattack.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Str("UP").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(HardAttackMp, 150).Tag("[cool time]").Int(HardAttackCool, HardAttackCool).Tag("[static data]").Int(120)
                // 两列的按等级取值表:第 1 级 (200, 40),第 2 级 (250, 60)
                .Tag("[level info]").Int(2, HardAttackRate, HardAttackFixed, 250, 60).Build()),
            // 鬼斩的攻击信息:击倒,水平击退 HardAttackPushAside 像素
            (HardAttackAtk, NewScript().Tag("[damage reaction]").Tag("[down]").Tag("[push aside]").Int(HardAttackPushAside).Build()),
            // 只带一个命中声标签的攻击信息(合成):读取与保留 [hit wav] 的测试用
            (SoundAtk, NewScript().Tag("[hit wav]").Str("TEST_HIT_WAV").Build()),
            // 带异常状态槽的攻击信息(合成,M43):眩晕 80%(1500 毫秒)、出血 100%(2000 毫秒,附加整数 300)、减速 30%(3000 毫秒,附加整数 40 与 20)、
            // 第四个槽(超出三个,计数后忽略)、[human active status rate] 50 和火属性
            (StatusAtk, NewScript()
                .Tag("[damage bonus]").Int(StrikerDamageBonus)
                .Tag("[elemental property]").Tag("[fire element]")
                .Tag("[active status]").Tag("[stun]").Float(80f).Int(0, 1500)
                .Tag("[active status]").Tag("[bleeding]").Float(100f).Int(0, 2000, 300)
                .Tag("[active status]").Tag("[slow]").Float(30f).Int(0, 3000, 40, 20)
                .Tag("[active status]").Tag("[freeze]").Float(100f).Int(0, 1000)
                .Tag("[human active status rate]").Float(50f).Build()),
            ("skill/test/upperslash.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Str("SKILL").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(UpperSlashMpStart, UpperSlashMpEnd).Tag("[cool time]").Int(UpperSlashCoolStart, UpperSlashCoolEnd).Tag("[static data]").Int(110, -200).Build()),
            ("skill/test/backstep.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Str("DOWN").Tag("[maximum level]").Int(1)
                .Tag("[consume MP]").Int(BackStepMp, BackStepMp).Tag("[static data]").Int(BackStepPowerStat).Build()),
            (EquipRoot + "/avatar/skin/askin_default.equ", NewScript()
                .Tag("[equipment type]").Str("[skin avatar]")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(0, 0).Build()),
            // 本职业的块在前,别的职业的块在后:读到下一个 [animation job] 必须停
            (EquipRoot + "/avatar/hair/ahair_default.equ", NewScript()
                .Tag("[equipment type]").Str("[hair avatar]")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(0, 0)
                .Tag("[layer variation]").Int(2222).Str("hair_a")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(7, 7)
                .Tag("[layer variation]").Int(102).Str("hair_q").Build()),
            // 别的职业的块在前,本职业的块在后:要跳过前一个块;一件装备可以有多个图层
            (EquipRoot + "/avatar/coat/acoat_default.equ", NewScript()
                .Tag("[equipment type]").Str("[coat avatar]")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(9, 9)
                .Tag("[layer variation]").Int(101).Str("coat_z")
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(1, 5)
                .Tag("[layer variation]").Int(1111).Str("coat_a").Int(700).Str("coat_b").Build()),
            // 武器:变体两个数里只有第二个(B = 2)进文件名;swordd 的图片不存在。别的职业的块之后才写 [sub type](与原版装备脚本同序)
            (EquipRoot + "/" + WeaponEqu, NewScript()
                .Tag("[equipment type]").Str("[weapon]")
                .Tag("[equipment physical attack]").Int(WeaponStartPhysical, 1)
                .Tag("[equipment magical attack]").Int(WeaponStartMagical, 0)
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(3, 2)
                .Tag("[layer variation]").Int(650).Str("swordb").Int(2999).Str("swordc").Int(3000).Str("swordd")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(8, 8)
                .Tag("[layer variation]").Int(1).Str("swordz")
                .Tag("[sub type]").Int(WeaponSubType).Build()),
            (EquipRoot + "/" + SecondWeaponEqu, NewScript()
                .Tag("[equipment type]").Str("[weapon]")
                .Tag("[equipment physical attack]").Int(SecondWeaponPhysical, 2)
                .Tag("[equipment magical attack]").Int(SecondWeaponMagical, 0)
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(3, 2)
                .Tag("[layer variation]").Int(654).Str("swordb1").Build()),
            // 本职业没有块的装备
            (EquipRoot + "/avatar/cap/ahat_other.equ", NewScript()
                .Tag("[equipment type]").Str("[cap avatar]")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(1, 1)
                .Tag("[layer variation]").Int(2150).Str("cap_a").Build()),
            (RestAni, BuildAni(new[] { (0, 100), (1, 300) }, true)),
            (MoveAni, BuildAni(new[] { (0, 100), (1, 100), (0, 100) }, true)),
            (WaitAni, BuildAni(new[] { (1, 100), (0, 100) }, true)),
            (Damage1Ani, BuildBoxAni((0, 10000, new[] { HeroHurtBox }))),
            (Damage2Ani, BuildBoxAni((1, 10000, new[] { HeroHurtBox }))),
            (DownAni, BuildBoxAni((0, 80, new[] { HeroHurtBox }), (1, 10000, new[] { HeroHurtBox }), (0, 10000, new[] { HeroHurtBox }), (1, 400, new[] { HeroLyingBox }))),
            // 奔跑(循环)、奔跑攻击;跳跃动画有两个停留帧(最高点与落地前,各 10000 毫秒),最后一帧是 50 毫秒的落地帧;空中攻击 200 毫秒
            (DashAni, BuildAni(new[] { (0, 100), (1, 100), (0, 100), (1, 100) })),
            // 奔跑攻击在第 1 帧(100 到 200 毫秒)、空中攻击在第 1 帧(100 到 150 毫秒)各有一个攻击框:x 从 +20 起宽 40,y 从 −10 起深 20,z 从 0 起高 50
            (DashAttackAni, BuildBoxAni((0, 100, null), (1, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }), (0, 100, null))),
            // 跳跃动画每一帧都带站立高度的受击框(原版的跳跃动画也有受击框),空中的主角才能被打中
            (JumpAni, BuildBoxAni((0, 100, new[] { HeroHurtBox }), (1, 100, new[] { HeroHurtBox }), (0, 80, new[] { HeroHurtBox }), (1, 10000, new[] { HeroHurtBox }),
                (0, 100, new[] { HeroHurtBox }), (1, 80, new[] { HeroHurtBox }), (0, 10000, new[] { HeroHurtBox }), (1, 50, new[] { HeroHurtBox }))),
            (JumpAttackAni, BuildBoxAni((0, 100, null), (1, 50, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }), (0, 50, null))),
            // 三段普攻:四帧 × 100、三帧 × 100、两帧 × 200 毫秒;攻击框(类型 15)在武器图层的动画里,第三段的基础动画自己带一个
            (Attack1Ani, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, null), (1, 100, null))),
            (Attack2Ani, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, null))),
            (Attack3Ani, BuildBoxAni((0, 200, null), (1, 200, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }))),
            // 技能动作:护身(占动作表的第 0 项)与鬼斩(600 毫秒,第 2、3 帧各带一个攻击框)
            ("character/test/animation/guard.ani", BuildBoxAni((0, 100, null))),
            (HardAttackEffect1, BuildEffectAni(false)),
            (HardAttackEffect2, BuildEffectAni(true)),
            (ChainEffect1, BuildEffectAni(false)),
            (ChainEffect2, BuildEffectAni(true)),
            (WaveEffect, BuildEffectAni(false)),
            (WaveEffect2, BuildKeyedEffectAni()),
            (WaveCastAni, BuildBoxAni((0, 10000, null), (1, 50, null), (0, 50, null), (1, 50, null),
                (0, 50, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (1, 50, null))),
            (ChainAAni, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (1, 100, null))),
            (ChainBAni, BuildBoxAni((0, 100, null), (1, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (0, 100, null))),
            (LevelUpEffect, BuildEffectAni(false)),
            // 通关营地:营火动作脚本(相对路径故意混着大小写)与两层动画、NPC 目录与三个 NPC、带 [NPC] 的地图
            (CampfireAct, NewScript()
                .Tag("[MOTION]")
                .Tag("[BASE ANI]").Str("../animation/CampBase.ani")
                .Tag("[SUB ANI]").Str("../animation/campglow.ani").Int(10, -5).Str("../animation/campbase.ani").Int(0, 0)
                .Tag("[/MOTION]").Build()),
            (CampBaseAni, BuildBoxAni((0, 100, null), (1, 100, null))),
            (CampGlowAni, BuildAni(new[] { (0, 100), (1, 100) })),
            ("npc/npc.lst", NewScript()
                .Int(CampKeeperId).Str("camp/keeper.npc").Int(CampGuestId).Str("camp\\guest.npc").Int(CampPlainId).Str("camp/plain.npc").Build()),
            (CampKeeperScript, NewScript().Tag("[field animation]").Str("animation/keeper.ani").Tag("[visible on dungeon clear]").Int(1).Build()),
            ("npc/camp/guest.npc", NewScript().Tag("[field animation]").Str("animation/guest.ani").Tag("[visible on dungeon clear]").Int(1).Build()),
            (CampPlainScript, NewScript().Tag("[field animation]").Str("animation/plain.ani").Tag("[visible on dungeon clear]").Int(0).Build()),
            (CampKeeperAni, BuildAni(new[] { (0, 100), (1, 100) })),
            (CampGuestAni, BuildAni(new[] { (1, 100), (0, 100) })),
            ("npc/camp/animation/plain.ani", BuildAni(new[] { (0, 100) })),
            (CampMapPath, NewScript()
                .Tag("[NPC]")
                .Int(CampKeeperId).Str("[left]").Int(0, 0, 0)
                .Int(CampGuestId).Str("[right]").Int(500, 60, 0)
                .Int(CampPlainId).Str("[right]").Int(0, 0, 0)
                .Tag("[/NPC]").Build()),
            (HardAttackAni, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }),
                (1, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (0, 200, null))),
            (WeaponLayerB + "/attack1.ani", BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, new[] { ((ushort)15, new[] { 10, -10, 0, 50, 20, 60 }) }), (1, 100, null))),
            (WeaponLayerB + "/attack2.ani", BuildBoxAni((0, 100, null), (1, 100, new[] { ((ushort)15, new[] { 10, -10, 0, 50, 20, 60 }) }), (0, 100, null))),
            (WeaponLayerC + "/attack1.ani", BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, new[] { ((ushort)15, new[] { 10, 30, 0, 10, 10, 10 }) }), (1, 100, null))),
            // 迷你怪物:目录(编号 3 指向不存在的脚本)、.mob(待机、行走、两种受击、倒地、一个攻击加一条空的攻击信息)与动画
            (MonsterListPath, NewScript()
                .Int(1).Str("blob/blob.mob").Int(2).Str("Blob\\Big.mob").Int(3).Str("blob/missing.mob").Build()),
            (BlobMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[move motion]").Str("Animation_Blob/Move.ani")
                .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
                .Tag("[damage motion 2]").Str("Animation_Blob/Damage2.ani")
                .Tag("[down motion]").Str("Animation_Blob/Down.ani")
                .Tag("[attack motion]").Str("Animation_Blob/Attack1.ani")
                .Tag("[attack info]").Str("AttackInfo/Attack1.atk").Str("").Build()),
            (BlobStrikerMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[move motion]").Str("Animation_Blob/Move.ani")
                .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
                .Tag("[damage motion 2]").Str("Animation_Blob/Damage2.ani")
                .Tag("[down motion]").Str("Animation_Blob/Down.ani")
                .Tag("[attack motion]").Str("Animation_Blob/Strike.ani")
                .Tag("[attack info]").Str("AttackInfo/Striker.atk").Build()),
            (BlobStrikerAtk, NewScript().Tag("[damage bonus]").Int(StrikerDamageBonus).Tag("[damage reaction]").Tag("[damage]").Build()),
            // 会扔东西的迷你怪物:出招动画 + 一个 [throw attack] 块;被动对象目录、.obj、.ptl、投射物的 .atk 与动画(证据 E-103)
            (BlobThrowerMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[move motion]").Str("Animation_Blob/Move.ani")
                .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
                .Tag("[damage motion 2]").Str("Animation_Blob/Damage2.ani")
                .Tag("[down motion]").Str("Animation_Blob/Down.ani")
                .Tag("[attack motion]").Str("Animation_Blob/Throw.ani")
                .Tag("[throw attack]")
                .Str("[attack index]").Int(0)
                .Str("[passive object number]").Int(1)
                .Str("[passive object filename]").Str("Particle/Shot.ptl")
                .Str("[object type]").Int(0)
                .Str("[passive object index]").Int(ShotObjectId)
                .Str("[power]").Int(0)
                .Str("[throw frame]").Int(1)
                .Str("[passive object start x cood]").Int(ShotStartX)
                .Str("[passive object start y cood]").Int(0)
                .Str("[passive object start z cood]").Int(ShotStartZ)
                .Str("[attack number]").Int(1)
                .Str("[throw sound]").Str(ShotThrowSound)
                .Tag("[/throw attack]").Build()),
            ("passiveobject/passiveobject.lst", NewScript()
                .Int(ShotObjectId).Str("blob/shot.obj")
                .Int(ActShotId).Str("blob/actshot.obj")
                .Int(ActRepeatId).Str("blob/actrepeat.obj")
                .Int(ActGraveId).Str("blob/actgrave.obj")
                .Int(ActSpawnerId).Str("blob/actspawner.obj")
                .Int(ActFallId).Str("blob/actfall.obj")
                .Int(ActForeverId).Str("blob/actforever.obj")
                .Int(CrateObjectId).Str("MapObject/BreakableObject/Crate.obj")
                .Int(BarrelObjectId).Str("MapObject/BreakableObject/Barrel.obj")
                .Int(PillarObjectId).Str("MapObject/BreakableObject/Pillar.obj")
                .Int(GrassObjectId).Str("MapObject/BreakableObject/Grass.obj")
                .Int(GhostObjectId).Str("MapObject/BreakableObject/Ghost.obj")
                .Int(RockObjectId).Str("MapObject/Obstacle/Rock.obj").Int(WaveObjectId).Str("hero/wave.obj").Build()),
            (BreakableDir + "/crate.obj", NewScript()
                .Tag("[width]").Int(30, 20).Tag("[layer]").Str("[normal]").Tag("[pass type]").Str("[do not pass]")
                .Tag("[basic motion]").Str("animation/stand.ani")
                .Tag("[etc motion]").Str("animation/hit.ani", "").Tag("[/etc motion]")
                .Tag("[int data]").Int(1, 2, 1, 0, 0, 30, 1).Tag("[/int data]")
                .Tag("[string data]").Str("", "", "Particle/Crate.ptl", "", CrateHitSound, CrateCrashSound).Tag("[/string data]").Build()),
            (BreakableDir + "/barrel.obj", NewScript()
                .Tag("[width]").Int(20, 20).Tag("[layer]").Str("[normal]").Tag("[pass type]").Str("[do not pass]")
                .Tag("[basic motion]").Str("animation/stand.ani")
                .Tag("[int data]").Int(1, 1, 1, 0, 0, 30, 1).Tag("[/int data]")
                .Tag("[string data]").Str("", "", "", "", CrateHitSound, CrateCrashSound).Tag("[/string data]").Build()),
            (BreakableDir + "/pillar.obj", NewScript()
                .Tag("[width]").Int(20, 20).Tag("[layer]").Str("[normal]").Tag("[pass type]").Str("[do not pass]")
                .Tag("[basic motion]").Str("animation/stand.ani")
                .Tag("[etc motion]").Str("animation/hit.ani").Tag("[/etc motion]")
                .Tag("[int data]").Int(1, 2, 0, 0, 0, 75, 1).Tag("[/int data]")
                .Tag("[string data]").Str("", "", "", "", PillarHitSound, "").Tag("[/string data]").Build()),
            (BreakableDir + "/grass.obj", NewScript()
                .Tag("[width]").Int(10, 10).Tag("[layer]").Str("[normal]").Tag("[pass type]").Str("[pass all]")
                .Tag("[basic motion]").Str("animation/stand.ani")
                .Tag("[int data]").Int(42, 1).Tag("[/int data]").Build()),
            (BreakableDir + "/ghost.obj", NewScript()
                .Tag("[width]").Int(20, 20).Tag("[layer]").Str("[normal]")
                .Tag("[basic motion]").Str("animation/boxless.ani")
                .Tag("[int data]").Int(1, 1, 1, 0, 0, 30, 1).Tag("[/int data]").Build()),
            ("passiveobject/mapobject/obstacle/rock.obj", NewScript()
                .Tag("[width]").Int(20, 20).Tag("[layer]").Str("[normal]").Tag("[pass type]").Str("[do not pass]")
                .Tag("[basic motion]").Str("animation/stand.ani")
                .Tag("[int data]").Int(1, 1, 1, 0, 0, 30, 1).Tag("[/int data]").Build()),
            (BreakableDir + "/animation/stand.ani", BuildBlobBoxesAni(true, (0, 100, new[] { BlobHurtBox }))),
            ("passiveobject/mapobject/obstacle/animation/stand.ani", BuildBlobBoxesAni(true, (0, 100, new[] { BlobHurtBox }))),
            (BreakableDir + "/animation/hit.ani", BuildBlobBoxesAni(false, (1, 80, new[] { BlobHurtBox }), (2, 80, new[] { BlobHurtBox }))),
            (BreakableDir + "/animation/boxless.ani", BuildBlobBoxesAni(true, (0, 100, new (ushort Type, int[] Values)[0]))),
            (ShotObj, NewScript().Tag("[basic motion]").Str("animation/shot.ani").Tag("[attack info]").Str("attackinfo/shot.atk").Build()),
            (ShotAtk, NewScript().Tag("[damage bonus]").Int(ShotDamageBonus).Tag("[damage reaction]").Tag("[damage]").Build()),
            (ShotParticle, NewScript()
                .Tag("[x axis]")
                .Tag("[move type]").Str("[straight line]")
                .Tag("[move variable 1]").Int(ShotSpeed, ShotSpeed)
                .Tag("[life time]").Int(ShotLifeMs)
                .Tag("[landing type]").Str("[vanish]").Build()),
            (AbsShotParticle, NewScript()
                .Tag("[x axis]")
                .Tag("[move type]").Str("[straight line]")
                .Tag("[move variable 1]").Int(AbsShotSpeed, AbsShotSpeed)
                .Tag("[life time]").Int(ShotLifeMs)
                .Tag("[landing type]").Str("[vanish]").Build()),
            (ShotAni, BuildBlobBoxesAni(true, (0, 100, new[] { ShotBox }), (1, 100, new[] { ShotBox }))),
            (BlobThrowAni, BuildBlobAni(false, (0, 80, BlobHurtBox), (1, 80, BlobHurtBox), (2, 80, BlobHurtBox), (3, 80, BlobHurtBox))),
            (BlobBoxedDownMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[move motion]").Str("Animation_Blob/Move.ani")
                .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
                .Tag("[damage motion 2]").Str("Animation_Blob/Damage2.ani")
                .Tag("[down motion]").Str("Animation_Blob/DownBoxed.ani")
                .Tag("[attack motion]").Str("Animation_Blob/Attack1.ani").Build()),
            (BlobShortDownMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
                .Tag("[down motion]").Str("Animation_Blob/Attack1.ani").Build()),
            (BlobDuoMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[move motion]").Str("Animation_Blob/Move.ani")
                .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
                .Tag("[damage motion 2]").Str("Animation_Blob/Damage2.ani")
                .Tag("[down motion]").Str("Animation_Blob/Down.ani")
                .Tag("[attack motion]").Str("Animation_Blob/Attack1.ani").Str("Animation_Blob/Strike.ani")
                .Tag("[attack info]").Str("AttackInfo/Missing.atk").Str("AttackInfo/Striker.atk").Build()),
            ("monster/blob/big.mob", NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani").Build()),
            // 脚本怪物:全部动作都是 .act(证据 E-128)
            (BlobScriptedMobPath, NewScript()
                .Tag("[waiting action]").Str("Action/Stay.act")
                .Tag("[move action]").Str("Action/Move.act")
                .Tag("[damage action 1]").Str("Action/Damage1.act")
                .Tag("[damage action 2]").Str("Action/Damage2.act")
                .Tag("[down action]").Str("Action/Down.act")
                .Tag("[attack action]").Str("Action/Attack.act").Str("Action/Missing.act").Str("Action/NoBase.act")
                .Tag("[/attack action]")
                .Tag("[attack info]").Str("AttackInfo/Striker.atk").Str("")
                .Tag("[/attack info]").Build()),
            (BlobActionDir + "/stay.act", ActMotion("../Animation_Blob/Stay.ani")),
            (BlobActionDir + "/move.act", ActMotion("../Animation_Blob/Move.ani")),
            (BlobActionDir + "/damage1.act", ActMotion("../Animation_Blob/Damage1.ani")),
            (BlobActionDir + "/damage2.act", ActMotion("../Animation_Blob/Damage2.ani")),
            (BlobActionDir + "/down.act", ActMotion("../Animation_Blob/Down.ani")),
            (BlobActionDir + "/attack.act", ActMotion("../Animation_Blob/Strike.ani")),
            (BlobActionDir + "/nobase.act", NewScript().Tag("[MOTION]").Tag("[/MOTION]").Build()),
            (BlobGhostMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Ghost.ani")
                .Tag("[move motion]").Str("Animation_Blob/Ghost.ani").Build()),
            (BlobAnimationDir + "/ghost.ani", BuildBlobAni(true, (0, 100, default), (1, 100, default))),
            (BlobMixedMobPath, NewScript()
                .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
                .Tag("[waiting action]").Str("Action/Move.act")
                .Tag("[move action]").Str("Action/Move.act")
                .Tag("[action list]").Str("Action/Attack.act").Str("Action/Attack.act")
                .Tag("[/action list]").Build()),
            // 受击动画只有一帧、停留 10000 毫秒(原版的受击动画就是这样:停多久由游戏逻辑决定);倒地动画的中间两帧同理
            (BlobAnimationDir + "/stay.ani", BuildBlobAni(true, (0, 100, BlobHurtBox), (1, 100, BlobHurtBox))),
            (BlobAnimationDir + "/move.ani", BuildBlobAni(true, (2, 100, BlobHurtBox), (3, 100, BlobHurtBox))),
            (BlobAnimationDir + "/damage1.ani", BuildBlobAni(false, (4, 10000, BlobHurtBox))),
            (BlobAnimationDir + "/damage2.ani", BuildBlobAni(false, (5, 10000, BlobHurtBox))),
            (BlobAnimationDir + "/down.ani", BuildBlobAni(false, (6, 80, BlobHurtBox), (7, 10000, default), (8, 500, default))),
            (BlobAnimationDir + "/downboxed.ani", BuildBlobAni(false, (6, 80, BlobHurtBox), (7, 80, BlobHurtBox), (7, 10000, BlobHurtBox), (8, 10000, BlobHurtBox),
                (8, 500, BlobHurtBox))),
            (BlobAnimationDir + "/attack1.ani", BuildBlobAni(false, (0, 80, BlobHurtBox), (1, 80, BlobHurtBox), (2, 80, BlobHurtBox))),
            (BlobStrikeAni, BuildBlobBoxesAni(false, (0, 80, new[] { BlobHurtBox }), (1, 80, new[] { BlobHurtBox }),
                (2, 80, new[] { BlobHurtBox, BlobStrikeBox }), (3, 80, new[] { BlobHurtBox }))),
            // 可走区域:两个矩形 (10,20,100,50) 与 (100,60,30,40),拼成一个 L 形
            (AreaMap, NewScript().Tag("[virtual movable area]").Int(10, 20, 100, 50, 100, 60, 30, 40).Build()),
        };

        scripts.AddRange(ActScripts().Select(a => (a.Name, a.Data)));
        var files = new List<TestData.PvfFileSpec>();
        // 先把所有脚本里的字符串都登记好,再生成字符串表(标记流里存的是字符串表的序号)
        var strings = new List<byte[]>();
        foreach (string s in _pool)
        {
            strings.Add(Encoding.ASCII.GetBytes(s));
        }
        files.Add(new TestData.PvfFileSpec("stringtable.bin", TestData.BuildStringTable(strings), 0));
        foreach ((string name, byte[] data) in scripts)
        {
            files.Add(new TestData.PvfFileSpec(name, data, 0));
        }
        (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
        File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);

        File.WriteAllBytes(Path.Combine(Root, "ImagePacks2", "a_avatar.npk"), TestData.BuildNpk(new (string, byte[])[]
        {
            (AvatarImageRoot + "/skin/tt_body0000.img", BuildImg(SkinColor, -3, -4)),
            (AvatarImageRoot + "/coat/tt_coat0105a.img", BuildImg(CoatAColor, -3, -4)),
            // coat_b:帧 0 偏移 (1,2);帧 1 是带很远偏移的 1×1 全透明占位帧
            (AvatarImageRoot + "/coat/tt_coat0105b.img", BuildImg(CoatBColor, 1, 2, blankSecondFrame: true)),
            (AvatarImageRoot + "/hair/tt_hair0000a.img", BuildImg(HairColor, 0, 0)),
            // 武器图片在 blade/ 目录里,而装备脚本在 sword/ 里
            (WeaponImageRoot + "/blade/sword0002b.img", BuildImg(SwordBColor, -10, -12)),
            (WeaponImageRoot + "/blade/sword0002c.img", BuildImg(SwordCColor, -10, -12)),
            (WeaponImageRoot + "/blade/sword0002b1.img", BuildImg((90, 90, 90), -10, -12)),
            (BlobImage, BuildBlobImg()),
        }));
    }

    /// <summary>
    /// 带判定框的 .ani:每项 (IMG 帧号, 时长毫秒, 判定框);第 0 张图是角色的皮肤 IMG,帧偏移 (-5, -8);不循环。
    /// 判定框为 null 表示这一帧没有框。
    /// </summary>
    private static byte[] BuildBoxAni(params (int FrameIndex, int DelayMs, (ushort Type, int[] Values)[] Boxes)[] frames)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int frameIndex, int delay, (ushort Type, int[] Values)[] boxes) in frames)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = -5, Y = -8 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (boxes != null)
            {
                f.Boxes.AddRange(boxes);
            }
            specs.Add(f);
        }
        return TestData.BuildAni(new[] { "Character/Test/Equipment/Avatar/skin/tt_body0000.img" },
            new (ushort, byte[])[0], specs);
    }

    /// <summary>
    /// 技能特效的 .ani(见 <see cref="HardAttackEffect1"/>):空帧 150、皮肤 IMG 第 0 帧 50、第 1 帧 50、空帧 100 毫秒,不循环;
    /// <paramref name="additive"/> 为真时每帧带图形特效 2(加法混合)。
    /// </summary>
    private static byte[] BuildEffectAni(bool additive)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int image, int frameIndex, int delay) in new[] { (-1, 0, 150), (0, 0, 50), (0, 1, 50), (-1, 0, 100) })
        {
            var f = new TestData.AniFrameSpec { ImageIndex = image, FrameIndex = frameIndex, X = -5, Y = -8 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (additive)
            {
                f.Props.Add((11, new byte[] { 2, 0 }));
            }
            specs.Add(f);
        }
        return TestData.BuildAni(new[] { "Character/Test/Equipment/Avatar/skin/tt_body0000.img" }, new (ushort, byte[])[0], specs);
    }

    /// <summary>带关键帧标志的特效 .ani(见 <see cref="WaveEffect2"/>):空帧 50、图 50、图 50(标志 <see cref="WaveEffect2Flag"/>)、空帧 100 毫秒,不循环。</summary>
    private static byte[] BuildKeyedEffectAni()
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int image, int frameIndex, int delay, int flag) in new[] { (-1, 0, 50, 0), (0, 0, 50, 0), (0, 1, 50, WaveEffect2Flag), (-1, 0, 100, 0) })
        {
            var f = new TestData.AniFrameSpec { ImageIndex = image, FrameIndex = frameIndex, X = -5, Y = -8 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (flag != 0)
            {
                f.Props.Add((23, TestData.LE32(flag)));
            }
            specs.Add(f);
        }
        return TestData.BuildAni(new[] { "Character/Test/Equipment/Avatar/skin/tt_body0000.img" }, new (ushort, byte[])[0], specs);
    }

    /// <summary>
    /// 迷你怪物的 .ani:图片是 <see cref="BlobImage"/>,每项 (IMG 帧号, 时长毫秒, 受击框;default 表示没有框),帧偏移 (-10, -20);
    /// <paramref name="loop"/> 为真时带全局属性 0(循环)。
    /// </summary>
    private static byte[] BuildBlobAni(bool loop, params (int FrameIndex, int DelayMs, (ushort Type, int[] Values) Box)[] frames)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int frameIndex, int delay, (ushort Type, int[] Values) box) in frames)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = -10, Y = -20 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (box.Values != null)
            {
                f.Boxes.Add(box);
            }
            specs.Add(f);
        }
        var globals = loop ? new (ushort, byte[])[] { (0, new byte[] { 1 }) } : new (ushort, byte[])[0];
        return TestData.BuildAni(new[] { "Monster/Blob/Blob.img" }, globals, specs);
    }

    /// <summary>同 <see cref="BuildBlobAni(bool, ValueTuple{int, int, ValueTuple{ushort, int[]}}[])"/>,每一帧可以带多个判定框(受击框加攻击框)。</summary>
    private static byte[] BuildBlobBoxesAni(bool loop, params (int FrameIndex, int DelayMs, (ushort Type, int[] Values)[] Boxes)[] frames)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int frameIndex, int delay, (ushort Type, int[] Values)[] boxes) in frames)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = -10, Y = -20 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (boxes != null)
            {
                f.Boxes.AddRange(boxes);
            }
            specs.Add(f);
        }
        var globals = loop ? new (ushort, byte[])[] { (0, new byte[] { 1 }) } : new (ushort, byte[])[0];
        return TestData.BuildAni(new[] { "Monster/Blob/Blob.img" }, globals, specs);
    }

    /// <summary>迷你怪物的 IMG:十帧,每帧 6×8 不透明,帧头偏移 (-3, -8),绿色通道 = 帧号,红色通道 = 100 + 帧号。</summary>
    private static byte[] BuildBlobImg()
    {
        var frames = new List<TestData.ImgFrameSpec>();
        for (int k = 0; k < 10; k++)
        {
            var bgra = new byte[6 * 8 * 4];
            for (int i = 0; i < 6 * 8; i++)
            {
                bgra[i * 4] = 10;
                bgra[i * 4 + 1] = (byte)k;
                bgra[i * 4 + 2] = (byte)(100 + k);
                bgra[i * 4 + 3] = 255;
            }
            TestData.ImgFrameSpec f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 6, 8, bgra);
            f.X = -3;
            f.Y = -8;
            f.FrameWidth = 6;
            f.FrameHeight = 8;
            frames.Add(f);
        }
        return TestData.BuildImg(frames);
    }

    /// <summary>每个 (IMG 帧号, 时长毫秒) 一帧;.ani 帧偏移 (-5, -8);循环。<paramref name="hurtBox"/> 为真时每帧带一个主角受击框(<see cref="HeroHurtBox"/>)。</summary>
    private static byte[] BuildAni((int FrameIndex, int DelayMs)[] frames, bool hurtBox = false)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int frameIndex, int delay) in frames)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = -5, Y = -8 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (hurtBox)
            {
                f.Boxes.Add(HeroHurtBox);
            }
            specs.Add(f);
        }
        return TestData.BuildAni(new[] { "Character/Test/Equipment/Avatar/skin/tt_body0000.img" },
            new (ushort, byte[])[] { (0, new byte[] { 1 }) }, specs);
    }

    /// <summary>两帧的 IMG,每帧 2×2 不透明,第 k 帧红色通道 = 基色 + k;帧头偏移 (x, y)。</summary>
    private static byte[] BuildImg((int R, int G, int B) color, int x, int y, bool blankSecondFrame = false)
    {
        var frames = new List<TestData.ImgFrameSpec>();
        for (int k = 0; k < 2; k++)
        {
            if (k == 1 && blankSecondFrame)
            {
                var blank = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 1, 1, new byte[] { 0, 0, 0, 0 });
                blank.X = 300;
                blank.Y = -200;
                blank.FrameWidth = 1;
                blank.FrameHeight = 1;
                frames.Add(blank);
                continue;
            }
            var bgra = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                bgra[i * 4] = (byte)color.B;
                bgra[i * 4 + 1] = (byte)color.G;
                bgra[i * 4 + 2] = (byte)(color.R + k);
                bgra[i * 4 + 3] = 255;
            }
            var f = TestData.ImgFrameSpec.Raw(ImgPixelFormat.Argb8888, 2, 2, bgra);
            f.X = x;
            f.Y = y;
            f.FrameWidth = 20;
            f.FrameHeight = 30;
            frames.Add(f);
        }
        return TestData.BuildImg(frames);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
