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
    /// [etc attack info] 第 1、2 项是两段的攻击信息;.skl 的 [static data] 是 (200, −400, 100, −100, 2)(第五个数是多段命中的次数,M46),[level info] 四列 (第 1 级 150、250、30、50,第 2 级 180、300、40、60)。
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
    /// 蓄力跳跃类技能(M45,证据 E-138)的合成数据:技能编号 <see cref="HopSkill"/>,[etc motion] 第 5、6 项是蓄力姿势 <see cref="HopReadyAni"/>(一帧 500 毫秒、没有攻击框)
    /// 与跳跃 <see cref="HopAni"/>(七帧 × 100 毫秒,第 3 到 5 帧各带一个攻击框 <see cref="HopStrikeBox"/>);
    /// [etc attack info] 第 3、4 项是第一个命中窗口与重武装窗口的攻击信息(<see cref="HopFirstAtk"/> 击退 <see cref="HopFirstPushAside"/>、不击倒,
    /// <see cref="HopRepeatAtk"/> 击倒、击退 <see cref="HopRepeatPushAside"/>);.skl 的 [static data] 是十个数
    /// (蓄力最短 <see cref="HopHoldMin"/>、最长 <see cref="HopHoldMax"/> 毫秒,距离 <see cref="HopDistanceMin"/> 到 <see cref="HopDistanceMax"/>,高度 <see cref="HopHeightMin"/> 到 <see cref="HopHeightMax"/>,
    /// 变速百分比 <see cref="HopSpeedMin"/> 到 <see cref="HopSpeedMax"/>,命中窗口数 <see cref="HopHitsMin"/> 到 <see cref="HopHitsMax"/>),
    /// [level info] 一列 (第 1 级 <see cref="HopRate1"/>,第 2 级 <see cref="HopRate2"/>),消耗魔法 10、冷却 3500;预载特效两个。数全是测试用的。
    /// </summary>
    public const int HopSkill = 904;

    /// <summary>
    /// 倒地起身类技能(M47,证据 E-140)的合成数据:技能编号 <see cref="QuickStandSkill"/>(与 <c>SkillIds.QuickStand</c> 相同),指令是合成的"下 &amp; 跳跃"(同时按),
    /// [static data] 第 0 项是起身时长 <see cref="QuickStandMs"/>,耗蓝 <see cref="QuickStandMp"/>,冷却 <see cref="QuickStandCool"/>。
    /// </summary>
    public const int QuickStandSkill = 190;

    /// <summary>以跳跃键当扳机的两键指令技能(M47,合成):指令"下,跳跃",[consume MP] 10、[cool time] 1000、两列 [level info]。</summary>
    public const int JumpCommandSkill = 906;

    /// <summary>
    /// 增益类技能(M48,证据 E-141)的合成数据:跳跃力类 <see cref="JumpBuffSkill"/>(指令单键"增益",[static data] 第 0 项是加成 <see cref="JumpBuffAmount"/>,
    /// [level info] 一列是持续毫秒:第 1 级 <see cref="JumpBuffMs1"/>、第 2 级 <see cref="JumpBuffMs2"/>)与魔法攻击力类 <see cref="MagicBuffSkill"/>
    /// (指令"下,增益",[level info] 两列 (持续毫秒,加成):第 1 级 <see cref="MagicBuffMs1"/>/<see cref="MagicBuffAmount1"/>、第 2 级 <see cref="MagicBuffMs2"/>/<see cref="MagicBuffAmount2"/>);
    /// 动作脚本的 [buff motion] 是 <see cref="BuffAni"/>(三帧 × <see cref="BuffFrameMs"/> 毫秒,没有攻击框)。耗蓝 12 与 8、冷却 2000 与 1500。数全是测试用的。
    /// </summary>
    public const int JumpBuffSkill = 907;
    public const int MagicBuffSkill = 908;
    public const int JumpBuffAmount = 30;
    public const int JumpBuffMs1 = 3000;
    public const int JumpBuffMs2 = 4000;
    public const int MagicBuffMs1 = 5000;
    public const int MagicBuffAmount1 = 40;
    public const int MagicBuffMs2 = 6000;
    public const int MagicBuffAmount2 = 60;

    /// <summary>
    /// 概率触发类增益(M49,证据 E-142)的合成数据:指令"上,增益",[level info] 两列 (千分比概率,持续毫秒):第 1 级 <see cref="ProcBuffPermille1"/>/<see cref="ProcBuffMs1"/>、
    /// 第 2 级 <see cref="ProcBuffPermille2"/>/<see cref="ProcBuffMs2"/>;耗蓝 6、冷却 1200。数全是测试用的。
    /// </summary>
    public const int ProcBuffSkill = 909;
    public const int ProcBuffPermille1 = 250;
    public const int ProcBuffMs1 = 7000;
    public const int ProcBuffPermille2 = 600;
    public const int ProcBuffMs2 = 8000;
    public const int BuffFrameMs = 100;
    public const string BuffAni = "character/test/animation/buffcast.ani";

    public const int QuickStandMs = 150;
    public const int QuickStandMp = 5;
    public const int QuickStandCool = 4000;
    public const string HopReadyAni = "character/test/animation/hopready.ani";
    public const string HopAni = "character/test/animation/hop.ani";
    public const string HopFirstAtk = "character/test/attackinfo/hopfirst.atk";
    public const string HopRepeatAtk = "character/test/attackinfo/hoprepeat.atk";
    public const int HopFirstPushAside = 90;
    public const int HopRepeatPushAside = 220;
    public const int HopHoldMin = 100;
    public const int HopHoldMax = 500;
    public const int HopDistanceMin = 100;
    public const int HopDistanceMax = 300;
    public const int HopHeightMin = 60;
    public const int HopHeightMax = 120;
    public const int HopSpeedMin = 100;
    public const int HopSpeedMax = 200;
    public const int HopHitsMin = 1;
    public const int HopHitsMax = 3;
    public const int HopRate1 = 180;
    public const int HopRate2 = 260;
    public const int HopMotionMs = 700;

    /// <summary>
    /// 腾空悬停俯冲类技能(M55,证据 E-149)的合成数据:技能编号 <see cref="DiveSkill"/>,指令单键"技能",耗蓝 11、冷却 2200;
    /// [static data] 是 (1, 2, 3, <see cref="DiveImpactMinHeight"/>)(第 3 个数是落地放冲击对象所需的最小离地高度);[level info] 三列:
    /// 第 0 列是俯冲本身的百分比伤害(第 1 级 <see cref="DiveRate1"/>、第 2 级 <see cref="DiveRate2"/>),第 1、2 列是冲击对象的固定值与百分比
    /// (第 1 级 <see cref="DiveImpactFixed1"/>/<see cref="DiveImpactRate1"/>、第 2 级 <see cref="DiveImpactFixed2"/>/<see cref="DiveImpactRate2"/>);
    /// [etc attack info] 第 5 项是俯冲的攻击信息(<see cref="DiveAtk"/>:击退 <see cref="DivePushAside"/>、不击倒);
    /// 空中攻击动画 <see cref="JumpAttackAni"/> 的最后一帧带一个"定住帧"的攻击框 <see cref="DiveHoldBox"/>(x 从 +100 起宽 40,与第 1 帧的框不重合);
    /// 动作脚本的 [sit motion] 是 <see cref="SitAni"/>(三帧 × <see cref="SitFrameMs"/> 毫秒,没有攻击框);预载特效一个(<c>sqr/common.nut</c> 的分支,与连斩的第一个特效同一个文件)。数全是测试用的。
    /// </summary>
    public const int DiveSkill = 905;
    public const int DiveRate1 = 150;
    public const int DiveRate2 = 210;
    public const int DiveImpactFixed1 = 40;
    public const int DiveImpactRate1 = 300;
    public const int DiveImpactFixed2 = 60;
    public const int DiveImpactRate2 = 420;
    public const int DiveImpactMinHeight = 30;
    public const string DiveAtk = "character/test/attackinfo/dive.atk";
    public const int DivePushAside = 70;
    public const string SitAni = "character/test/animation/sit.ani";
    public const int SitFrameMs = 100;

    /// <summary>
    /// 空中攻击强化类技能(M56,证据 E-150)的合成数据:技能编号 <see cref="JumpSkill"/>,指令是单独一个攻击键(不放进快捷栏,靠攻击键在空中接管空中攻击),
    /// 耗蓝 <see cref="JumpSkillMp"/>、冷却 <see cref="JumpSkillCoolMs"/> 毫秒(比一跳的时间长:放完之后的下一跳还在冷却里);[level info] 一列百分比(第 1 级 <see cref="JumpSkillRate1"/>、第 2 级 <see cref="JumpSkillRate2"/>);
    /// [etc motion] 第 7、8 项是两种动作:<see cref="JumpSkillAAni"/>(四帧 × 50 毫秒,第 2 帧有攻击框,x 从 +20 起宽 40)、<see cref="JumpSkillBAni"/>
    /// (三帧 × 60 毫秒,第 1 帧有攻击框,x 从 +100 起宽 40;所以站在身前 +40 的怪只被第一种打中、站在 +120 的只被第二种打中),两种的框都又高又深(空中也打得到地上的怪);
    /// 预载特效九个:下标 4 到 7 各是不同的文件(<see cref="ChainEffect1"/>、<see cref="ChainEffect2"/>、<see cref="WaveEffect"/>、<see cref="WaveEffect2"/>),
    /// 其余是鬼斩的第一层特效(充数)。数全是测试用的。
    /// </summary>
    public const int JumpSkill = 910;
    public const int JumpSkillRate1 = 220;
    public const int JumpSkillRate2 = 250;
    public const int JumpSkillMp = 9;
    public const int JumpSkillCoolMs = 4000;
    public const string JumpSkillAAni = "character/test/animation/jumpskilla.ani";
    public const string JumpSkillBAni = "character/test/animation/jumpskillb.ani";
    public const int JumpSkillAFrameMs = 50;
    public const int JumpSkillBFrameMs = 60;
    public const int JumpSkillAEtcIndex = 7;
    public const int JumpSkillBEtcIndex = 8;

    /// <summary>
    /// 姿势→挥击类技能(M65,证据 E-163)的合成数据:技能编号 <see cref="PoseStrikeSkill"/>,没有 [cool time],带 [cooltime level info] 1(冷却取 [level info] 的第 1 列);
    /// 耗蓝 <see cref="PoseStrikeMp"/>;[level info] 两列 (百分比, 冷却毫秒):第 1 级 (<see cref="PoseStrikeRate1"/>, <see cref="PoseStrikeCool1"/>)、
    /// 第 2 级 (<see cref="PoseStrikeRate2"/>, <see cref="PoseStrikeCool2"/>)。动作与攻击信息借多段技能的两段:<see cref="ChainAAni"/> 当姿势(动画里带攻击框,
    /// 用来检查姿势段身体不打人)、<see cref="ChainBAni"/> 当挥击。数全是测试用的。
    /// </summary>
    public const int PoseStrikeSkill = 911;
    public const int PoseStrikeMp = 25;
    public const int PoseStrikeRate1 = 160;
    public const int PoseStrikeRate2 = 190;
    public const int PoseStrikeCool1 = 2600;
    public const int PoseStrikeCool2 = 2400;

    /// <summary>
    /// 关键帧标志事件(M65,证据 E-163)的合成数据:技能 <see cref="FlagSkill"/> 播 [etc motion] 第 <see cref="FlagEtcIndex"/> 项 <see cref="FlagAni"/>
    /// (五帧 × 100 毫秒:第 0 帧有攻击框;第 1 帧带标志 <see cref="FlagA"/>、有框;第 2 帧无框;第 3 帧带标志 <see cref="FlagB"/>、有框;第 4 帧无框),
    /// [level info] 两列(百分比):1 级 (<see cref="FlagRate0"/>, <see cref="FlagRate1"/>)、2 级 (140, 280);耗蓝 <see cref="FlagMp"/>。数全是测试用的。
    /// </summary>
    public const int FlagSkill = 912;
    public const int FlagMp = 7;
    public const int FlagRate0 = 130;
    public const int FlagRate1 = 260;
    public const int FlagA = 1;
    public const int FlagB = 2;
    public const int FlagEtcIndex = 9;
    public const string FlagAni = "character/test/animation/flag.ani";

    /// <summary>
    /// 升空出招(M65,证据 E-163)的合成数据:技能 <see cref="RiseSkill"/> 的挥击段播 [etc motion] 第 <see cref="RiseEtcIndex"/> 项 <see cref="RiseAni"/>
    /// (三帧:第 0 帧停 10000 毫秒是占位、带攻击框,第 1 帧 30 毫秒,第 2 帧 40 毫秒);[static data] 是 (<see cref="RiseHeight"/>, <see cref="RiseMs"/>, <see cref="RiseBack"/>)
    /// (升空高度、升空毫秒、后滑像素);[level info] 一列百分比 1 级 <see cref="RiseRate1"/>、2 级 170;耗蓝 <see cref="RiseMp"/>。数全是测试用的。
    /// </summary>
    public const int RiseSkill = 913;
    public const int RiseMp = 11;
    public const int RiseRate1 = 150;
    public const int RiseHeight = 40;
    public const int RiseMs = 300;
    public const int RiseBack = 30;
    public const int RiseEtcIndex = 10;
    public const string RiseAni = "character/test/animation/rise.ani";

    /// <summary>
    /// 防御姿态(M66,证据 E-164)的合成数据:技能 <see cref="GuardSkill"/> 的指令是"下、下、攻击",播 [etc motion] 第 <see cref="GuardEtcIndex"/> 项 <see cref="GuardAni"/>
    /// (两帧、每帧停留 10000 毫秒,每帧都带站立高度的受击框);[static data] 第 0 个数是最短保持毫秒 <see cref="GuardMinHoldMs"/>;[level info] 四列
    /// (后滑速度、后滑毫秒、物理减伤百分比、魔法减伤百分比):1 级 (<see cref="GuardSpeed1"/>, <see cref="GuardRecoilMs1"/>, <see cref="GuardPhysical1"/>, <see cref="GuardMagical1"/>)、
    /// 2 级 (<see cref="GuardSpeed2"/>, <see cref="GuardRecoilMs2"/>, <see cref="GuardPhysical2"/>, <see cref="GuardMagical2"/>);耗蓝 <see cref="GuardMp"/>、冷却 <see cref="GuardCool"/> 毫秒。数全是测试用的。
    /// </summary>
    public const int GuardSkill = 914;
    public const int GuardEtcIndex = 11;
    public const string GuardAni = "character/test/animation/guardpose.ani";
    public const int GuardMinHoldMs = 200;
    public const int GuardMp = 5;
    public const int GuardCool = 1500;
    public const int GuardSpeed1 = 300;
    public const int GuardRecoilMs1 = 200;
    public const int GuardPhysical1 = 50;
    public const int GuardMagical1 = 30;
    public const int GuardSpeed2 = 400;
    public const int GuardRecoilMs2 = 250;
    public const int GuardPhysical2 = 70;
    public const int GuardMagical2 = 40;

    /// <summary>空中攻击动画最后一帧的攻击框(悬停俯冲把动画定在这一帧):x 从 +100 起宽 40,y 从 −10 起深 20,z 从 0 起高 50;与第 1 帧的框(x 从 +20 起)不重合。</summary>
    public static readonly (ushort Type, int[] Values) DiveHoldBox = ((ushort)15, new[] { 100, -10, 0, 40, 20, 50 });

    /// <summary>跳跃动画的攻击框:又宽又高(x 从 +20 起宽 300,y 从 −10 起深 20,z 从 −100 起高 200),空中的主角也打得到地上的怪物、被打退的怪物也还在框里。</summary>
    public static readonly (ushort Type, int[] Values) HopStrikeBox = ((ushort)15, new[] { 20, -10, -100, 300, 20, 200 });

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

    /// <summary>
    /// 每一族动作都写了 .act 的怪物(M53,E-146):[waiting action]、[move action]、[sit action]、[damage action 1/2]、[down action]、[overturn action]、
    /// [last action]、[proc action],以及两项的 [etc action] 列表和一项攻击;.act 文件不一定存在(只测路径解析)。
    /// </summary>
    public const string BlobFamilyMobPath = "monster/blob/famall.mob";

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
    /// M57([CHANGE ACTION FILE],证据 E-151)的换文件素材:各族"换进来的"动画(路径如下)。形状与原来的相同,只是路径不同,好从 CurrentAnimationPath 认出来;
    /// 有两处不同:倒地 B 躺着那一帧停 1500 毫秒(原来 500);出招 B 的攻击框(<see cref="BlobAwayBox"/>)在出招方向的反面很远处,永远够不着主角。
    /// </summary>
    public const string SwapStayAni = "monster/blob/animation_blob/stayb.ani";
    public const string SwapMoveAni = "monster/blob/animation_blob/moveb.ani";
    public const string SwapDamage1Ani = "monster/blob/animation_blob/damage1b.ani";
    public const string SwapDamage2Ani = "monster/blob/animation_blob/damage2b.ani";
    public const string SwapDownAni = "monster/blob/animation_blob/downb.ani";
    public const string SwapStrikeAni = "monster/blob/animation_blob/strikeaway.ani";

    /// <summary>
    /// M62-b(怪物的自定义动作 [etc action],证据 E-159)的动画:A 三帧各 80 毫秒(共 240)、B 两帧各 100(共 200)、循环动画两帧各 100(不会自己播完)、
    /// 带攻击框的一个(形状与出招动画 <see cref="BlobStrikeAni"/> 相同:第 2 帧即 160 到 240 毫秒带 <see cref="BlobStrikeBox"/>)。
    /// </summary>
    public const string CustomAAni = "monster/blob/animation_blob/customa.ani";
    public const string CustomBAni = "monster/blob/animation_blob/customb.ani";
    public const string CustomLoopAni = "monster/blob/animation_blob/customloop.ani";
    public const string CustomStrikeAni = "monster/blob/animation_blob/customstrike.ani";

    /// <summary>出招 B 的攻击框(类型 15):x 从 −200 起宽 40(出招方向的反面),y 从 −10 起深 20,z 从 0 起高 50。</summary>
    public static readonly (ushort Type, int[] Values) BlobAwayBox = ((ushort)15, new[] { -200, -10, 0, 40, 20, 50 });

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
    /// M51(证据 E-144)再加两只:status(帧 1 对攻击目标 [ACTIVE STATUS] [stun] 概率 100、等级 0、持续 2000 毫秒)与 statusmiss(同上,概率 0)。
    /// M52(证据 E-145)按动作族写的 [TRIGGER]:xoff(第 0 招帧 1 把第 1 招的触发器 0 关掉;第 1 招帧 1 起 X 速度 100)、
    /// xon(第 0 招帧 1 把第 1 招的触发器 0 打开再立即切第 1 招;第 1 招的触发器 0 出生时是关着的,帧 1 放 <see cref="ActShotId"/>)与对照 xonctl(同 xon 但不开它)、
    /// xself(第 0 招帧 1 用 [ATTACK] 0 1 [ON] 点名自己这一招,打开帧 3 才放 <see cref="ActShotId"/> 的触发器 1)与对照 xselfctl(不开)、
    /// xmiss(第 0 招帧 1 依次写三条落空的开关:没有的招、越界的触发器、[MOVE] 动作族)。
    /// M58(证据 E-152)[SET POS FORCE]:force(出招开始起每帧把主角摆到自己前方 30 像素)与 forceme(出招开始起每帧把自己摆到主角右侧 50 像素)。
    /// M63(证据 E-161)属性改变附加物:appendme(帧 1 给自己挂无时限的移动速度 -50% 与生命上限 +100%)、appendhero(帧 1 给主角挂 500 毫秒的移动速度与攻击速度 -50%)、
    /// appenddel(帧 1 给主角挂无时限的移动速度 -50%,帧 3 用 [DELETE APPENDAGE] 删掉同一项)。
    /// M68(证据 E-166)条件批一:xendout 与 xendin(出招开始把 X 速度设成向左或向右,帧 1 在 [IS END MAP] 成立时放 <see cref="ActForeverId"/>)、
    /// xendv(帧 1 看 [IS END MAP VERTICALLY])、xstatus 与 xstatusmiss(帧 1 对主角挂眩晕,概率 100 或 0;帧 3 目标带着眩晕才放 <see cref="ActForeverId"/>)、
    /// xfire(出招 .act 里的 [ON DAMAGE] 带火元素条件才放 <see cref="ActForeverId"/>)、xzspeed 与对照 xzspeedn(帧 1 在自己的 Z 速度大于 0 时放 <see cref="ActForeverId"/>;前者出招起 Z 速度 300);
    /// 过程动作 f_procfire、f_procwater、f_procnone:挨打且这一击是火、水、无元素才放 <see cref="ActForeverId"/>;
    /// 站立动作 f_stayfire:挨打且这一击是火才放 <see cref="ActForeverId"/>;
    /// xnear(出招帧 1 放出 <see cref="ActNearId"/> 与 <see cref="ActFarId"/> 两个被动对象,它们出生时各看一次 [CHECK DISTANCE]);
    /// xdrift(出招开始起 X、Y、Z 三个轴的速度各设一个,Y 为正;帧 1 看 [IS END MAP VERTICALLY] 成立才放 <see cref="ActForeverId"/>);
    /// 被动对象 <see cref="ActDriftId"/> 出生起 X、Y、Z 三个轴的速度各设一个。
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

    /// <summary>
    /// M62(证据 E-158)被动对象各动作的触发器开关板:<see cref="ActBoardReadId"/> 出生时读 [CUSTOM] 0 的触发器 0(开着的那条放 X 速度 100,关着的那条放 Y 速度 50)、三种落空的读法(越界序号、不存在的动作、没装触发器的动作,满足了才放 Z 速度 300)与动画读不到的自定义动作 2(池照样存在,通过就放一个 <see cref="ActGraveId"/>);
    /// <see cref="ActBoardWriteId"/> 出生时把 [CUSTOM] 0 的触发器 0 关掉、再写一次越界的触发器 9(落空)、然后切过去(触发器 0 被关了不触发,同一动作的触发器 1 照常触发);
    /// <see cref="ActBoardLoopId"/> 基础动作与自定义动作 0 来回切,自定义动作 0 每次进来放一个 <see cref="ActGraveId"/>、把自己的触发器 0 关掉再切回去(开关保留时只放一个)。
    /// </summary>
    public const int ActBoardReadId = 9207;
    public const int ActBoardWriteId = 9208;
    public const int ActBoardLoopId = 9209;

    /// <summary><see cref="ActBoardInitId"/>:自定义动作 0 的触发器 0 初值是关([ENABLE] [OFF]);基础动作出生时读它的开关(读 [OFF] 通过、读 [ON] 不通过)。</summary>
    public const int ActBoardInitId = 9210;

    /// <summary>
    /// M67(证据 E-165)被动对象追踪([homing] 块,全是合成的数):<see cref="ActHomeInstantId"/> 出生就追 [ENEMY]、速度 200、检查间隔 100 毫秒、没有 [max rotation](检查时一步指过去);
    /// <see cref="ActHomeSmoothId"/> 同上但 [max rotation] 90(每秒最多转 90 度);<see cref="ActHomeStraightId"/> 速度 100、[straight homing](只在第一次检查时直线瞄一下);
    /// <see cref="ActHomeMeId"/> 追 [ME](直接创建者);<see cref="ActHomeMonsterId"/> 追 [MONSTER] <see cref="HomeMonsterCode"/>;<see cref="ActHomeIdleId"/> 的 [homing use] 是 0(不追);
    /// <see cref="ActHomeOffId"/> 出生起 [SET USE HOMING] 0;<see cref="ActHomeOnId"/> 的 [homing use] 是 0、出生起 [SET USE HOMING] 1(没有出生时取的目标,仍然不追);
    /// <see cref="ActHomeTurnId"/> [init rotation] 90 并带 [sync animation rotation];<see cref="ActHomeCharId"/> 追 [CHARACTER];
    /// <see cref="ActHomeCarryId"/> 带 [max rotation] 90 与 [sync target move](目标走多少对象也跟着平移多少)。
    /// </summary>
    public const int ActHomeInstantId = 9211;
    public const int ActHomeSmoothId = 9212;
    public const int ActHomeStraightId = 9213;
    public const int ActHomeMeId = 9214;
    public const int ActHomeMonsterId = 9215;
    public const int ActHomeIdleId = 9216;
    public const int ActHomeOffId = 9217;
    public const int ActHomeOnId = 9218;
    public const int ActHomeTurnId = 9219;
    public const int ActHomeCharId = 9220;
    public const int ActHomeCarryId = 9221;

    /// <summary>M68(证据 E-166):出生起三个轴各设一个速度(X 200、Y 50、Z 300;X 朝向相关),用来看被动对象把脚本速度报给 [ZSPEED]、[IS END MAP];限时 1000 毫秒。</summary>
    public const int ActDriftId = 9222;

    /// <summary>M68(证据 E-166 更正 E-156):被动对象出生时看一次 [CHECK DISTANCE];<see cref="ActNearId"/> 的范围 900×900(看得到主角),<see cref="ActFarId"/> 的范围 0×0(看不到);看到了才放一个 <see cref="ActForeverId"/>。</summary>
    public const int ActNearId = 9223;

    public const int ActFarId = 9224;
    public const int HomeMonsterCode = 77;
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

        /// <summary>括号词标记(类型 6,值是字符串池下标;[command] 里的 (UP)、(SKILL) 就是这种写法,M47)。</summary>
        public Script Paren(params string[] words)
        {
            foreach (string word in words)
            {
                _tokens.Add(((byte)TokenType.Paren, _owner.Intern(word)));
            }
            return this;
        }

        /// <summary>分隔符标记(类型 8,值是字符串池下标:"," 是先后、"&amp;" 是同时,M47)。</summary>
        public Script Sep(string text)
        {
            _tokens.Add(((byte)TokenType.Separator, _owner.Intern(text)));
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

        /// <summary>[CHANGE ACTION FILE] 的一条记录(M57):<paramref name="slot"/> 是 "[STAY]"、"[ATTACK]" 之类的族标签,受击、出招要带序号 <paramref name="index"/>。</summary>
        public Script ChangeFile(string slot, int? index, string path)
        {
            Tag("[CHANGE ACTION FILE]").Tag(slot);
            if (index.HasValue)
            {
                Int(index.Value);
            }
            return Str(path);
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

    /// <summary>M67(证据 E-165)合成的带追踪的被动对象 .obj:基础动作 <paramref name="basicAction"/>,再接 [homing] ... [/homing] 块,块里的各项由 <paramref name="block"/> 写。</summary>
    private byte[] HomingObj(string basicAction, Func<Script, Script> block)
    {
        return block(NewScript().Tag("[basic action]").Str(basicAction).Tag("[homing]")).Tag("[/homing]").Build();
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
    private const string StayActAni = "../Animation_Blob/Stay.ani";
    private const string MoveActAni = "../Animation_Blob/Move.ani";
    private const string Damage1ActAni = "../Animation_Blob/Damage1.ani";
    private const string DownActAni = "../Animation_Blob/Down.ani";
    private const string LastActAni = "../Animation_Blob/Last.ani";
    private const string StrikeActAni = "../Animation_Blob/Strike.ani";
    private const string SwapStayActAni = "../Animation_Blob/StayB.ani";
    private const string SwapMoveActAni = "../Animation_Blob/MoveB.ani";
    private const string SwapDamage1ActAni = "../Animation_Blob/Damage1B.ani";
    private const string SwapDamage2ActAni = "../Animation_Blob/Damage2B.ani";
    private const string SwapDownActAni = "../Animation_Blob/DownB.ani";
    private const string SwapStrikeActAni = "../Animation_Blob/StrikeAway.ani";

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
        ("spawn9207", new[] { "ActSpawn9207" }),
        ("spawn9208", new[] { "ActSpawn9208" }),
        ("dmgoff", new[] { "ActDmgOff" }),
        ("dmgkeep", new[] { "ActDmgKeep" }),
        ("hover", new[] { "ActHover" }),
        ("land", new[] { "ActLand" }),
        ("tele", new[] { "ActTele" }),
        ("force", new[] { "ActForce" }),
        ("forceme", new[] { "ActForceMe" }),
        ("shake", new[] { "ActShake" }),
        ("summon", new[] { "ActSummon" }),
        ("summontimed", new[] { "ActSummonTimed" }),
        ("summonkill", new[] { "ActSummonKill" }),
        ("summononce", new[] { "ActSummonOnce" }),
        ("restore", new[] { "ActRestore" }),
        ("status", new[] { "ActStatus" }),
        ("statusmiss", new[] { "ActStatusMiss" }),
        ("xoff", new[] { "ActXOff", "ActXTarget" }),
        ("xon", new[] { "ActXOn", "ActXLate" }),
        ("xonctl", new[] { "ActXOnCtl", "ActXLate" }),
        ("xself", new[] { "ActXSelf" }),
        ("xselfctl", new[] { "ActXSelfCtl" }),
        ("xmiss", new[] { "ActXMiss" }),
        // M61(证据 E-157):[SAVE TARGET OBJECT] 与 [LOAD TARGET]:第 0 招存下主角、切到第 1 招再取出来放被动对象(对照那只没存,取到空槽)
        ("xsave", new[] { "ActXSave", "ActXLoad" }),
        ("xsavectl", new[] { "ActXSaveCtl", "ActXLoad" }),
        // M63(证据 E-161):[APPENDAGE] 与 [DELETE APPENDAGE]
        ("appendme", new[] { "ActAppendMe" }),
        ("appendhero", new[] { "ActAppendHero" }),
        ("appenddel", new[] { "ActAppendDel" }),
        // M68(证据 E-166):[IS END MAP]、[IS END MAP VERTICALLY]、[IS TARGETACTIVESTATUS]
        ("xendout", new[] { "ActXEndOut" }),
        ("xendin", new[] { "ActXEndIn" }),
        ("xendv", new[] { "ActXEndV" }),
        ("xstatus", new[] { "ActXStatus" }),
        ("xstatusmiss", new[] { "ActXStatusMiss" }),
        ("xfire", new[] { "ActXFire" }),
        ("xzspeed", new[] { "ActXZSpeed" }),
        ("xzspeedn", new[] { "ActXZSpeedN" }),
        ("xdrift", new[] { "ActXDrift" }),
        ("xnear", new[] { "ActXNear" }),
    };

    /// <summary>
    /// 非出招动作族靠 .act 触发器的怪物(M53,E-146):名字都以 f_ 开头,路径同样是 <see cref="ActMobPath"/>。每项:名字、出招 .act 列表、
    /// 成对的(标签, .act 名)——覆盖默认的站立、行走、受击、倒地 .act(只有动画),或补上 [sit action]、[last action]、[proc action] 等。
    /// 各 .act 的内容见 <see cref="ActScripts"/> 里"M53"一段。
    /// </summary>
    private static readonly (string Name, string[] Attacks, string[] Pairs)[] FamilyMobs =
    {
        ("f_plain", new[] { "ActPlain" }, new string[0]),
        ("f_stay", new[] { "ActPlain" }, new[] { "[waiting action]", "FStay" }),
        ("f_move", new[] { "ActPlain" }, new[] { "[move action]", "FMove" }),
        ("f_hop", new[] { "ActPlain" }, new[] { "[move action]", "FHop" }),
        ("f_dmg", new[] { "ActPlain" }, new[] { "[damage action 1]", "FDamage", "[down action]", "FDown" }),
        ("f_procboot", new[] { "ActPlain" }, new[] { "[proc action]", "FProcBoot" }),
        ("f_procdmg", new[] { "ActPlain" }, new[] { "[proc action]", "FProcDmg" }),
        // M68(证据 E-166):[ON DAMAGE] 加元素条件(火、水、无)
        ("f_procfire", new[] { "ActPlain" }, new[] { "[proc action]", "FProcFire" }),
        ("f_procwater", new[] { "ActPlain" }, new[] { "[proc action]", "FProcWater" }),
        ("f_procnone", new[] { "ActPlain" }, new[] { "[proc action]", "FProcNone" }),
        ("f_stayfire", new[] { "ActPlain" }, new[] { "[waiting action]", "FStayFire" }),
        ("f_procsw", new[] { "ActPlain" }, new[] { "[proc action]", "FProcSw", "[waiting action]", "FStaySw" }),
        ("f_stayset", new[] { "ActPlain", "ActPlain" }, new[] { "[waiting action]", "FStaySet" }),
        ("f_stayunh", new[] { "ActPlain" }, new[] { "[waiting action]", "FStayUnh" }),
        ("f_last", new[] { "ActPlain" }, new[] { "[last action]", "FLast" }),
        ("f_xstay", new[] { "ActXStayOff" }, new[] { "[waiting action]", "FStay" }),
        ("f_xunh", new[] { "ActXUnh" }, new string[0]),
        // M61(证据 E-156):坐下族有 .act(复用站立的那份),[TRIGGER CHECK] 要从各族的开关板里读
        ("f_sit", new[] { "ActPlain" }, new[] { "[sit action]", "FStay" }),
        ("f_down", new[] { "ActPlain" }, new[] { "[down action]", "FDown" }),
        // M57(证据 E-151):[CHANGE ACTION FILE] 的换文件怪物(换的目标 .act 见 ActScripts 里"M57"一段)
        ("f_swapfam", new[] { "ActSwapFam" }, new string[0]),
        ("f_swapstay", new[] { "ActPlain" }, new[] { "[waiting action]", "FSwapStay" }),
        ("f_swapstayset", new[] { "ActPlain" }, new[] { "[waiting action]", "FSwapStaySet" }),
        ("f_swapself", new[] { "ActSwapSelf" }, new string[0]),
        ("f_swapslot", new[] { "ActSwapSlot", "ActPlain" }, new string[0]),
        ("f_swapfromstay", new[] { "ActPlain" }, new[] { "[waiting action]", "FSwapAtk" }),
        ("f_swapbad", new[] { "ActSwapBad" }, new string[0]),
        ("f_swapsub", new[] { "ActSwapSub" }, new string[0]),
        ("f_swapmoveset", new[] { "ActPlain" }, new[] { "[move action]", "FSwapMoveSet" }),
        // M62-b(证据 E-159):[etc action] 自定义动作列表。成对写法里 "[etc action]" 的值是用 | 隔开的 .act 名("" 是空位);
        // 出招驱动 ActCustNow 等把脚本切到列表里的某一项(各 .act 见 ActScripts 里"M62-b"一段)
        ("f_custnow", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustA|CustB" }),
        ("f_custend", new[] { "ActCustEnd", "ActPlain" }, new[] { "[etc action]", "CustA" }),
        ("f_custbad", new[] { "ActCustBad", "ActPlain" }, new[] { "[etc action]", "CustA||CustB" }),
        ("f_custnone", new[] { "ActCustNow", "ActPlain" }, new string[0]),
        ("f_custplain", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustAPlain" }),
        ("f_custnoani", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustNoAni" }),
        ("f_custstay", new[] { "ActPlain" }, new[] { "[waiting action]", "FStayCust", "[etc action]", "CustA" }),
        ("f_custmove", new[] { "ActPlain" }, new[] { "[move action]", "FMoveCust", "[etc action]", "CustA" }),
        ("f_custchain", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustChain|CustB" }),
        ("f_custatk", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustAtk" }),
        ("f_custloop", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustLoop" }),
        ("f_custloopexit", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustLoopExit" }),
        ("f_custloopend", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustLoopEnd" }),
        ("f_custstrike", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustStrike" }),
        ("f_custonce", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustOnce" }),
        ("f_custgate", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustGate" }),
        ("f_custsw", new[] { "ActCustSw", "ActPlain" }, new[] { "[etc action]", "CustA|CustGate" }),
        ("f_custread", new[] { "ActCustRead", "ActPlain" }, new[] { "[etc action]", "CustGate" }),
        ("f_custend2", new[] { "ActCustEnd", "ActPlain" }, new[] { "[etc action]", "CustStrike" }),
        ("f_custstaychain", new[] { "ActPlain" }, new[] { "[waiting action]", "FStayCust", "[etc action]", "CustChain|CustB" }),
        ("f_custswap", new[] { "ActCustNow", "ActPlain" }, new[] { "[etc action]", "CustSwap" }),
        ("f_custswap2", new[] { "ActCustSwapNow", "ActPlain" }, new[] { "[etc action]", "CustA" }),
    };

    private IEnumerable<(string Name, byte[] Data)> ActScripts()
    {
        foreach ((string name, string[] attacks, string[] pairs) in FamilyMobs)
        {
            var over = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                over[pairs[i]] = pairs[i + 1];
            }
            Script mob = NewScript();
            var defaults = new[] { ("[waiting action]", "Stay"), ("[move action]", "Move"), ("[damage action 1]", "Damage1"), ("[damage action 2]", "Damage2"), ("[down action]", "Down") };
            foreach ((string tag, string act) in defaults)
            {
                mob.Tag(tag).Str("Action/" + (over.TryGetValue(tag, out string replaced) ? replaced : act) + ".act");
            }
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (pairs[i] == "[etc action]")
                {
                    mob.Tag("[etc action]");
                    foreach (string etc in pairs[i + 1].Split('|'))
                    {
                        mob.Str(etc.Length == 0 ? "" : "Action/" + etc + ".act");
                    }
                    mob.Tag("[/etc action]");
                }
                else if (!Array.Exists(defaults, d => d.Item1 == pairs[i]))
                {
                    mob.Tag(pairs[i]).Str("Action/" + pairs[i + 1] + ".act");
                }
            }
            mob.Tag("[attack action]");
            foreach (string act in attacks)
            {
                mob.Str("Action/" + act + ".act");
            }
            mob.Tag("[/attack action]").Tag("[attack info]");
            foreach (string _ in attacks)
            {
                mob.Str("");
            }
            yield return (ActMobPath(name), mob.Tag("[/attack info]").Build());
        }
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
        // M58(证据 E-152):[SET POS FORCE]。force:出招开始起,每帧把攻击目标(主角)摆到自己前方 30 像素、同一深度、高度 0(基准取自己、朝向取自己的);
        // forceme:出招开始起,每帧把自己摆到攻击目标右侧偏移 50 像素、同一深度处(基准缺省取目标、朝向缺省取目标的、[MOVE ME])
        yield return (BlobActionDir + "/actforce.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0, "[TARGET]")
            .Behavior(b => b.Tag("[SET POS FORCE]").Tag("[USE MY BASEPOS]").Tag("[USE MY DIRECTION]")
                .Tag("[X START]").Int(30).Tag("[Y START]").Int(0).Tag("[Z START]").Int(0).Tag("[/SET POS FORCE]"))
            .Build());
        yield return (BlobActionDir + "/actforceme.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0, "[TARGET]")
            .Behavior(b => b.Tag("[SET POS FORCE]").Tag("[MOVE ME]")
                .Tag("[X START]").Int(50).Tag("[Y START]").Int(0).Tag("[Z START]").Int(0).Tag("[/SET POS FORCE]"))
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
        // M51(证据 E-144):对攻击目标(主角)挂异常状态:眩晕,状态等级 0、持续 2000 毫秒;概率 100 与 0
        yield return (BlobActionDir + "/actstatus.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0, "[TARGET]")
            .Behavior(b => b.Tag("[ACTIVE STATUS]").Tag("[stun]").Float(100f).Int(0, 2000))
            .Build());
        yield return (BlobActionDir + "/actstatusmiss.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0, "[TARGET]")
            .Behavior(b => b.Tag("[ACTIVE STATUS]").Tag("[stun]").Float(0f).Int(0, 2000))
            .Build());
        // M63(证据 E-161):属性改变附加物。appendme:帧 1 给自己挂无时限的 [move speed] -50% 与 [hp max] +100%;appendhero:帧 1 给主角挂 500 毫秒的
        // [move speed] -50% 与 [attack speed] -50%;appenddel:帧 1 给主角挂无时限的 [move speed] -50%,帧 3 把同一项删掉
        yield return (BlobActionDir + "/actappendme.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[APPENDAGE]").Int(0).Tag("[move speed]").Tag("[%]").Int(-50).Tag("[hp max]").Tag("[%]").Int(100).Tag("[/APPENDAGE]"))
            .Build());
        yield return (BlobActionDir + "/actappendhero.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0, "[TARGET]")
            .Behavior(b => b.Tag("[APPENDAGE]").Int(500).Tag("[move speed]").Tag("[%]").Int(-50).Tag("[attack speed]").Tag("[%]").Int(-50).Tag("[/APPENDAGE]"))
            .Build());
        yield return (BlobActionDir + "/actappenddel.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0, "[TARGET]")
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3), 1, "[TARGET]")
            .Behavior(b => b.Tag("[APPENDAGE]").Int(0).Tag("[move speed]").Tag("[%]").Int(-50).Tag("[/APPENDAGE]"))
            .Behavior(b => b.Tag("[DELETE APPENDAGE]").Int(0).Tag("[move speed]").Tag("[%]").Int(-50).Tag("[/DELETE APPENDAGE]"))
            .Build());
        // M68(证据 E-166):舞台端头条件。出招一开始 X 速度设成 100(朝左取反,即向左)或 -100(向右),帧 1 在 [IS END MAP] 成立时放一个 ActForeverId;
        // xendv 不设速度,帧 1 看 [IS END MAP VERTICALLY];xstatus 帧 1 对攻击目标挂眩晕(概率 100)、帧 3 在目标带着异常状态 3(眩晕)时放一个(xstatusmiss 概率 0)
        yield return (BlobActionDir + "/actxendout.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[IS END MAP]"), 1)
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/actxendin.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[IS END MAP]"), 1)
            .Behavior(b => b.SetSpeed("[X AXIS]", -100))
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/actxendv.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[IS END MAP VERTICALLY]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/actxstatus.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0, "[TARGET]")
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3).Tag("[WHICH]").Tag("[TARGET]").Tag("[IS TARGETACTIVESTATUS]").Int(3), 1, "[CHECKUP OBJECT]")
            .Behavior(b => b.Tag("[ACTIVE STATUS]").Tag("[stun]").Float(100f).Int(0, 2000))
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/actxstatusmiss.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0, "[TARGET]")
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3).Tag("[WHICH]").Tag("[TARGET]").Tag("[IS TARGETACTIVESTATUS]").Int(3), 1, "[CHECKUP OBJECT]")
            .Behavior(b => b.Tag("[ACTIVE STATUS]").Tag("[stun]").Float(0f).Int(0, 2000))
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // xzspeed:出招开始起 Z 速度 300,帧 1 在自己的高度速度大于 0 时放一个;xzspeedn 同样的条件但没有起速度(对照)
        yield return (BlobActionDir + "/actxzspeed.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[WHICH]").Tag("[ME]").Tag("[ZSPEED]").Tag("[>]").Int(0), 1, "[CHECKUP OBJECT]")
            .Behavior(b => b.SetSpeed("[Z AXIS]", 300))
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/actxzspeedn.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[WHICH]").Tag("[ME]").Tag("[ZSPEED]").Tag("[>]").Int(0), 0, "[CHECKUP OBJECT]")
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // xdrift:出招开始起 X、Y、Z 三个轴的速度各设一个(Y 为正),帧 1 看 [IS END MAP VERTICALLY] 成立才放一个
        yield return (BlobActionDir + "/actxdrift.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[IS END MAP VERTICALLY]"), 1)
            .Behavior(b => b.SetSpeed("[X AXIS]", 100).SetSpeed("[Y AXIS]", 50).SetSpeed("[Z AXIS]", 300))
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // xnear:出招帧 1 放出 ActNearId 与 ActFarId 两个被动对象
        yield return (BlobActionDir + "/actxnear.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Create(ActNearId, 0, 0, 0).Create(ActFarId, 0, 0, 0))
            .Build());
        // xfire:出招 .act 里挨到火元素的一击才放一个(出招运行器的 [ON DAMAGE] 也带元素)
        yield return (BlobActionDir + "/actxfire.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ON DAMAGE]").Tag("[IS FIRE DAMAGE]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // M52(证据 E-145):按动作族写的 [TRIGGER]。第 1 招的触发器 0 本来开着(xoff)或关着(xon),由第 0 招去改
        yield return (BlobActionDir + "/actxoff.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[ATTACK]").Int(1, 0).Tag("[OFF]"))
            .Build());
        yield return (BlobActionDir + "/actxtarget.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Build());
        yield return (BlobActionDir + "/actxon.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[ATTACK]").Int(1, 0).Tag("[ON]").Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actxonctl.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actxlate.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[ENABLE]").Tag("[OFF]").Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Create(ActShotId, ShotStartX, 0, ShotStartZ))
            .Build());
        yield return (BlobActionDir + "/actxself.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Trigger(r => r.Tag("[ENABLE]").Tag("[OFF]").Tag("[FRAME]").Int(3, 3), 1)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[ATTACK]").Int(0, 1).Tag("[ON]"))
            .Behavior(b => b.Create(ActShotId, ShotStartX, 0, ShotStartZ))
            .Build());
        yield return (BlobActionDir + "/actxselfctl.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Trigger(r => r.Tag("[ENABLE]").Tag("[OFF]").Tag("[FRAME]").Int(3, 3), 1)
            .Behavior(b => b.SetSpeed("[X AXIS]", 0))
            .Behavior(b => b.Create(ActShotId, ShotStartX, 0, ShotStartZ))
            .Build());
        yield return (BlobActionDir + "/actxmiss.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[ATTACK]").Int(5, 0).Tag("[OFF]")
                .Tag("[TRIGGER]").Tag("[ATTACK]").Int(0, 7).Tag("[OFF]")
                .Tag("[TRIGGER]").Tag("[MOVE]").Int(0).Tag("[OFF]"))
            .Build());
        // M61(证据 E-157):[SAVE TARGET OBJECT] [TARGET] 3 存主角后切到第 1 招;第 1 招帧 1 取槽 3 当目标放被动对象(槽空就没有目标,什么都不放)
        yield return (BlobActionDir + "/actxsave.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[SAVE TARGET OBJECT]").Tag("[TARGET]").Int(3), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actxsavectl.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actxload.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag("[WHICH]").Tag("[LOAD TARGET]").Int(3), 0, "[CHECKUP OBJECT]")
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // M53(证据 E-146):非出招动作族的 .act。放出来的 ActForeverId 对象不会消失,会话里的被动对象数就是触发次数
        yield return (BlobActionDir + "/fstay.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/fmove.act", ActWith(MoveActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Build());
        // 行走里跳一下:一开始 Z 方向给个速度,第 1 帧(100 毫秒)把 Z 速度设成 0——"不再上升",之后照样受重力落回地面
        yield return (BlobActionDir + "/fhop.act", ActWith(MoveActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 1)
            .Behavior(b => b.SetSpeed("[Z AXIS]", 600))
            .Behavior(b => b.SetSpeed("[Z AXIS]", 0))
            .Build());
        yield return (BlobActionDir + "/fdamage.act", ActWith(Damage1ActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/fdown.act", ActWith(DownActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // 过程动作没有动画(帧号固定 0):出生就跑,[FRAME] 0 0 在出生那一步放一个;或者等挨打放一个
        yield return (BlobActionDir + "/fprocboot.act", NewScript()
            .Trigger(r => r.Tag("[FRAME]").Int(0, 0), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/fprocdmg.act", NewScript()
            .Trigger(r => r.Tag("[ON DAMAGE]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // M68(证据 E-166):挨打且这一击是火、是水、没有元素时才放一个
        foreach ((string file, string element) in new[] { ("fprocfire", "[IS FIRE DAMAGE]"), ("fprocwater", "[IS WATER DAMAGE]"), ("fprocnone", "[IS NONE DAMAGE]") })
        {
            yield return (BlobActionDir + "/" + file + ".act", NewScript()
                .Trigger(r => r.Tag("[ON DAMAGE]").Tag(element), 0)
                .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
                .Build());
        }
        // 站立动作(当前动作)里挨到火元素的一击才放一个
        yield return (BlobActionDir + "/fstayfire.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[ON DAMAGE]").Tag("[IS FIRE DAMAGE]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // 过程的触发器 0 出生时关着,站立动作一开始就把它打开(跨族 [TRIGGER] [PROC])
        yield return (BlobActionDir + "/fprocsw.act", NewScript()
            .Trigger(r => r.Tag("[ENABLE]").Tag("[OFF]").Tag("[ON DAMAGE]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/fstaysw.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[PROC]").Int(0).Tag("[ON]"))
            .Build());
        // 站立动作里切招(第 1 招)与切到怪物没接的动作(自定义)
        yield return (BlobActionDir + "/fstayset.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/fstayunh.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        // 最后一幕:自己的动画 1200 毫秒(三帧各 400);血量归零那一步放一个,帧 2(800 毫秒,身体 500 毫秒时已消失)再放一个
        yield return (BlobActionDir + "/flast.act", ActWith(LastActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(2, 2), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // M57(证据 E-151):[CHANGE ACTION FILE]。B 系列是换进来的文件:动画路径与原来的不同,从 CurrentAnimationPath 认得出
        yield return (BlobActionDir + "/stayb.act", ActWith(SwapStayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/moveb.act", ActMotion(SwapMoveActAni));
        yield return (BlobActionDir + "/damage1b.act", ActWith(SwapDamage1ActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/damage2b.act", ActMotion(SwapDamage2ActAni));
        yield return (BlobActionDir + "/downb.act", ActMotion(SwapDownActAni));
        yield return (BlobActionDir + "/actaway.act", ActWith(SwapStrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/sub/stayc.act", ActMotion("../../Animation_Blob/StayB.ani"));
        // 出招脚本一开始就把整套族换掉:出招不属于这几族,没有一族在跑,全部当场换上
        yield return (BlobActionDir + "/actswapfam.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[STAY]", null, "StayB.act").ChangeFile("[MOVE]", null, "MoveB.act")
                .ChangeFile("[DAMAGE]", 0, "Damage1B.act").ChangeFile("[DAMAGE]", 1, "Damage2B.act").ChangeFile("[DOWN]", null, "DownB.act"))
            .Build());
        // 待机脚本自己换待机文件:待机正在跑,记成待换
        yield return (BlobActionDir + "/fswapstay.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.ChangeFile("[STAY]", null, "StayB.act"))
            .Build());
        // 换完立刻再设一次待机:重新进入待机,新文件当场换上
        yield return (BlobActionDir + "/fswapstayset.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.ChangeFile("[STAY]", null, "StayB.act").Tag("[SET ACTION]").Tag("[STAND]").Tag("[NOW]"))
            .Build());
        // 出招脚本换自己这一招:正在跑,记成待换
        yield return (BlobActionDir + "/actswapself.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[ATTACK]", 0, "ActAway.act"))
            .Build());
        // 出招脚本换第 1 招再切过去:第 1 招没在跑,当场换上
        yield return (BlobActionDir + "/actswapslot.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[ATTACK]", 1, "ActAway.act").Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        // 行走脚本换行走文件,再设一次行走:行走正在跑,换的记成待换,重新进入行走时换上
        yield return (BlobActionDir + "/fswapmoveset.act", ActWith(MoveActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[MOVE]", null, "MoveB.act").Tag("[SET ACTION]").Tag("[MOVE]").Tag("[NOW]"))
            .Build());
        // 待机脚本给第 0 招换上带触发器的程序:这只怪物原来没有任何出招程序
        yield return (BlobActionDir + "/fswapatk.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.ChangeFile("[ATTACK]", 0, "ActAway.act"))
            .Build());
        // 四条不该换上的记录:受击序号越界、出招序号越界、冲刺族(没有宿主)、文件不存在
        yield return (BlobActionDir + "/actswapbad.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[DAMAGE]", 2, "Damage1B.act").ChangeFile("[ATTACK]", 5, "ActAway.act")
                .ChangeFile("[DASH]", null, "StayB.act").ChangeFile("[STAY]", null, "Missing.act"))
            .Build());
        // 路径相对写这条脚本的 .act 所在目录,可以带子目录
        yield return (BlobActionDir + "/actswapsub.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[STAY]", null, "sub/StayC.act"))
            .Build());
        yield return (BlobActionDir + "/actxstayoff.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[STAY]").Int(0).Tag("[OFF]"))
            .Build());
        yield return (BlobActionDir + "/actxunh.act", ActWith(ThrowActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[DEFAULT]").Int(0).Tag("[OFF]")
                .Tag("[TRIGGER]").Tag("[CUSTOM]").Int(0, 0).Tag("[OFF]"))
            .Build());
        yield return (BlobAnimationDir + "/last.ani", BuildBlobAni(false, (0, 400, default), (1, 400, default), (2, 400, default)));
        foreach (int id in new[] { ActShotId, ActRepeatId, ActGraveId, ActSpawnerId, ActFallId, ActBoardReadId, ActBoardWriteId })
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
        yield return (ActObjectDir + "/actdrift.obj", NewScript()
            .Tag("[basic action]").Str("action/actdrift.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actdrift.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 200).SetSpeed("[Y AXIS]", 50).SetSpeed("[Z AXIS]", 300))
            .Build());
        // M68(证据 E-166):被动对象出生时用 [CHECK DISTANCE] 找主角,看得到才放一个常驻对象
        foreach ((string file, int range) in new[] { ("actnear", 900), ("actfar", 0) })
        {
            yield return (ActObjectDir + "/" + file + ".obj", NewScript()
                .Tag("[basic action]").Str("action/" + file + ".act")
                .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
            yield return (ActObjectDir + "/action/" + file + ".act", ActWith("../animation/idle.ani")
                .Trigger(r => r.Tag("[ON SET ACTION]").Tag("[CHECK DISTANCE]").Int(range, range), 0)
                .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
                .Build());
        }
        // ---- M62(证据 E-158):被动对象各动作的触发器开关板
        string boardRead = "[TRIGGER CHECK]";
        yield return (ActObjectDir + "/actbread.obj", NewScript()
            .Tag("[basic action]").Str("action/actbread.act")
            .Tag("[etc action]").Str("action/actbread_e0.act").Str("action/actidle.act").Str("action/actbread_noani.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actbread.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(0, 0).Tag("[ON]"), 0, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(0, 0).Tag("[OFF]"), 1, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(0, 5).Tag("[ON]"), 2, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(9, 0).Tag("[ON]"), 2, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(1, 0).Tag("[ON]"), 2, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(2, 0).Tag("[ON]"), 3, "[CHECKUP OBJECT]")
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Behavior(b => b.SetSpeed("[Y AXIS]", 50))
            .Behavior(b => b.SetSpeed("[Z AXIS]", 300))
            .Behavior(b => b.Create(ActGraveId, 0, 0, 0))
            .Build());
        yield return (ActObjectDir + "/action/actbread_e0.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 7))
            .Build());
        // 自定义动作 2:.act 读得到、动画读不到(放不出来,但客户端的动作池跟动画无关,脚本问起它的触发器照样有答案)
        yield return (ActObjectDir + "/action/actbread_noani.act", ActWith("../animation/missing.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 5))
            .Build());
        yield return (ActObjectDir + "/actbwrite.obj", NewScript()
            .Tag("[basic action]").Str("action/actbwrite.act")
            .Tag("[etc action]").Str("action/actbwrite_e0.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actbwrite.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[CUSTOM]").Int(0, 0).Tag("[OFF]").Tag("[TRIGGER]").Tag("[CUSTOM]").Int(0, 9).Tag("[OFF]").Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        yield return (ActObjectDir + "/action/actbwrite_e0.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 1)
            .Behavior(b => b.SetSpeed("[X AXIS]", 11))
            .Behavior(b => b.SetSpeed("[Y AXIS]", 22))
            .Build());
        yield return (ActObjectDir + "/actbloop.obj", NewScript()
            .Tag("[basic action]").Str("action/actbloop.act")
            .Tag("[etc action]").Str("action/actbloop_e0.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actbloop.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        yield return (ActObjectDir + "/action/actbloop_e0.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActGraveId, 0, 0, 0).Tag("[TRIGGER]").Int(0).Tag("[OFF]").Tag("[SET ACTION]").Tag("[STAND]").Tag("[NOW]"))
            .Build());
        yield return (ActObjectDir + "/actbinit.obj", NewScript()
            .Tag("[basic action]").Str("action/actbinit.act")
            .Tag("[etc action]").Str("action/actbinit_e0.act")
            .Tag("[object destroy condition]").Str(shotDestroy, "[time limite]").Int(1000).Build());
        yield return (ActObjectDir + "/action/actbinit.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(0, 0).Tag("[OFF]"), 0, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[ON SET ACTION]").Tag(boardRead).Tag("[CUSTOM]").Int(0, 0).Tag("[ON]"), 1, "[CHECKUP OBJECT]")
            .Behavior(b => b.SetSpeed("[X AXIS]", 31))
            .Behavior(b => b.SetSpeed("[Y AXIS]", 32))
            .Build());
        yield return (ActObjectDir + "/action/actbinit_e0.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ENABLE]").Tag("[OFF]").Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.SetSpeed("[X AXIS]", 41))
            .Build());
        yield return (ActObjectDir + "/actforever.obj", NewScript().Tag("[basic action]").Str("action/actidle.act").Build());
        yield return (ActObjectDir + "/action/actidle.act", ActMotion("../animation/idle.ani"));
        // ---- M67(证据 E-165):被动对象追踪
        yield return (ActObjectDir + "/acthomei.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomes.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)
            .Tag("[max rotation]").Float(90f)));
        yield return (ActObjectDir + "/acthomet.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(100, 0).Tag("[straight homing]")));
        yield return (ActObjectDir + "/acthomem.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ME]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomen.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[MONSTER]").Int(HomeMonsterCode).Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomez.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(0).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomeo.obj", HomingObj("action/acthomeoff.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomep.obj", HomingObj("action/acthomeon.act", s => s
            .Tag("[homing use]").Int(0).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomer.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)
            .Tag("[max rotation]").Float(90f).Tag("[init rotation]").Float(90f).Tag("[sync animation rotation]").Int(1)));
        yield return (ActObjectDir + "/acthomec.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[CHARACTER]").Int(0).Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)));
        yield return (ActObjectDir + "/acthomek.obj", HomingObj("action/actidle.act", s => s
            .Tag("[homing use]").Int(1).Tag("[homing follow]").Str("[ENEMY]").Tag("[homing velocity]").Int(200, 0).Tag("[homing check gap]").Int(100)
            .Tag("[max rotation]").Float(90f).Tag("[sync target move]")));
        yield return (ActObjectDir + "/action/acthomeoff.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[SET USE HOMING]").Int(0))
            .Build());
        yield return (ActObjectDir + "/action/acthomeon.act", ActWith("../animation/idle.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Tag("[SET USE HOMING]").Int(1))
            .Build());
        yield return (ActBoomAni, BuildBlobBoxesAni(false, (0, 100, new (ushort Type, int[] Values)[0]), (1, 100, new (ushort Type, int[] Values)[0]),
            (2, 100, new (ushort Type, int[] Values)[0])));
        yield return (ActIdleAni, BuildBlobBoxesAni(true, (0, 100, new (ushort Type, int[] Values)[0])));
        foreach ((string Name, byte[] Data) custom in CustomScripts())
        {
            yield return custom;
        }
    }

    private const string CustomAActAni = "../Animation_Blob/CustomA.ani";
    private const string CustomBActAni = "../Animation_Blob/CustomB.ani";
    private const string CustomLoopActAni = "../Animation_Blob/CustomLoop.ani";
    private const string CustomStrikeActAni = "../Animation_Blob/CustomStrike.ani";

    /// <summary>
    /// M62-b(证据 E-159)怪物的自定义动作:动画、自定义动作的 .act(Cust 开头:A 与 B 一出场放一个永不消失的对象,会话里的被动对象数就是进入次数)、
    /// 切进去的驱动(出招 .act 的 ActCust 开头,站立与行走 .act 的 FStayCust、FMoveCust)。
    /// </summary>
    private IEnumerable<(string Name, byte[] Data)> CustomScripts()
    {
        yield return (CustomAAni, BuildBlobAni(false, (0, 80, BlobHurtBox), (1, 80, BlobHurtBox), (2, 80, BlobHurtBox)));
        yield return (CustomBAni, BuildBlobAni(false, (3, 100, BlobHurtBox), (4, 100, BlobHurtBox)));
        yield return (CustomLoopAni, BuildBlobAni(true, (0, 100, BlobHurtBox), (1, 100, BlobHurtBox)));
        yield return (CustomStrikeAni, BuildBlobBoxesAni(false, (0, 80, new[] { BlobHurtBox }), (1, 80, new[] { BlobHurtBox }),
            (2, 80, new[] { BlobHurtBox, BlobStrikeBox }), (3, 80, new[] { BlobHurtBox })));

        yield return (BlobActionDir + "/custa.act", ActWith(CustomAActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/custaplain.act", ActMotion(CustomAActAni));
        yield return (BlobActionDir + "/custb.act", ActWith(CustomBActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        // 动画读不到的自定义动作:只带程序、没有动画(真实数据里有这样的项)
        yield return (BlobActionDir + "/custnoani.act", ActWith("../Animation_Blob/Missing.ani")
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());
        yield return (BlobActionDir + "/custloop.act", ActMotion(CustomLoopActAni));
        yield return (BlobActionDir + "/custloopexit.act", ActWith(CustomLoopActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[STAND]").Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/custloopend.act", ActWith(CustomLoopActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[END]"))
            .Build());
        yield return (BlobActionDir + "/custstrike.act", ActMotion(CustomStrikeActAni));
        yield return (BlobActionDir + "/custchain.act", ActWith(CustomAActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(1).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/custatk.act", ActWith(CustomAActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[ATTACK]").Int(1).Tag("[NOW]"))
            .Build());
        // 一次性:出场放一个对象并把自己的触发器 0 关掉(开关板进出保留,下一次进来就不触发了)
        yield return (BlobActionDir + "/custonce.act", ActWith(CustomAActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0).Tag("[TRIGGER]").Tag("[CUSTOM]").Int(0, 0).Tag("[OFF]"))
            .Build());
        // 出生时关着的触发器 0:要别处把它打开才放对象
        yield return (BlobActionDir + "/custgate.act", ActWith(CustomAActAni)
            .Trigger(r => r.Tag("[ENABLE]").Tag("[OFF]").Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.Create(ActForeverId, 0, 0, 0))
            .Build());

        yield return (BlobActionDir + "/actcustnow.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/actcustend.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[END]"))
            .Build());
        // 第 1 帧先切越界的:序号正好等于个数(3)与远远越界的(7);第 2 帧切列表里的空位 1;三次都不该动;第 3 帧切到 2(B)
        yield return (BlobActionDir + "/actcustbad.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 3)
            .Trigger(r => r.Tag("[FRAME]").Int(2, 2), 1)
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3), 2)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(3).Tag("[NOW]"))
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(1).Tag("[NOW]"))
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(2).Tag("[NOW]"))
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(7).Tag("[NOW]"))
            .Build());
        // 跨动作开关:先把自定义动作 1 的触发器 0 打开、再往不存在的自定义动作 9 写(落空)、然后切进自定义动作 1
        yield return (BlobActionDir + "/actcustsw.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[CUSTOM]").Int(1, 0).Tag("[ON]").Tag("[TRIGGER]").Tag("[CUSTOM]").Int(9, 0).Tag("[ON]")
                .Tag("[SET ACTION]").Tag("[CUSTOM]").Int(1).Tag("[NOW]"))
            .Build());
        // 读侧:第 1 帧读自定义动作 0 的触发器 0(关着:放 X 速度 100;开着:放 Y 速度 50;自定义动作 9 不存在:放 Z 速度 300,不该触发),
        // 第 2 帧把它打开,第 3 帧再读一次(开着:Y 速度 51)
        string check = "[TRIGGER CHECK]";
        yield return (BlobActionDir + "/actcustread.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag(check).Tag("[CUSTOM]").Int(0, 0).Tag("[OFF]"), 0, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag(check).Tag("[CUSTOM]").Int(0, 0).Tag("[ON]"), 1, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1).Tag(check).Tag("[CUSTOM]").Int(9, 0).Tag("[ON]"), 2, "[CHECKUP OBJECT]")
            .Trigger(r => r.Tag("[FRAME]").Int(2, 2), 3)
            .Trigger(r => r.Tag("[FRAME]").Int(3, 3).Tag(check).Tag("[CUSTOM]").Int(0, 0).Tag("[ON]"), 4, "[CHECKUP OBJECT]")
            .Behavior(b => b.SetSpeed("[X AXIS]", 100))
            .Behavior(b => b.SetSpeed("[Y AXIS]", 50))
            .Behavior(b => b.SetSpeed("[Z AXIS]", 300))
            .Behavior(b => b.Tag("[TRIGGER]").Tag("[CUSTOM]").Int(0, 0).Tag("[ON]"))
            .Behavior(b => b.SetSpeed("[Y AXIS]", 51))
            .Build());
        yield return (BlobActionDir + "/custswap.act", ActWith(CustomAActAni)
            .Trigger(r => r.Tag("[ON SET ACTION]"), 0)
            .Behavior(b => b.ChangeFile("[ATTACK]", 0, "ActAway.act"))
            .Build());
        yield return (BlobActionDir + "/actcustswapnow.act", ActWith(StrikeActAni)
            .Trigger(r => r.Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.ChangeFile("[ATTACK]", 0, "ActAway.act").Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/fstaycust.act", ActWith(StayActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
        yield return (BlobActionDir + "/fmovecust.act", ActWith(MoveActAni)
            .Trigger(r => r.Tag("[LIMIT]").Int(1).Tag("[FRAME]").Int(1, 1), 0)
            .Behavior(b => b.Tag("[SET ACTION]").Tag("[CUSTOM]").Int(0).Tag("[NOW]"))
            .Build());
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
                .Tag("[sit motion]").Str("animation/sit.ani")
                .Tag("[buff motion]").Str("animation/buffcast.ani")
                .Tag("[jump power]").Int(JumpPowerStat)
                .Tag("[weight]").Int(WeightStat)
                .Tag("[jump speed]").Int(JumpSpeedStat)
                .Tag("[width]").Int(BodyWidthStat, BodyDepthStat)
                // 初始技能表("编号 等级"成对,在 [initial value] 块里,与原版一样):181 与 174 是被动,5、46、169 是带出招指令的主动技能,顺序与原版新建剑士一样
                .Tag("[initial value]")
                .Tag("[skill]").Int(181, 2, HardAttackSkill, 1, UpperSlashSkill, UpperSlashLevel, BackStepSkill, 1, 174, 1)
                // 技能动作与攻击信息(两个表的下标不对应:护身动作没有攻击信息,鬼斩在动作表里是第 1 项、在攻击信息表里是第 0 项)
                .Tag("[etc motion]").Str("Animation/Guard.ani").Str("Animation/HardAttack.ani").Str("Animation/ChainA.ani").Str("Animation/ChainB.ani").Str("Animation/WaveCast.ani")
                .Str("Animation/HopReady.ani").Str("Animation/Hop.ani").Str("Animation/JumpSkillA.ani").Str("Animation/JumpSkillB.ani")
                .Str("Animation/Flag.ani").Str("Animation/Rise.ani").Str("Animation/GuardPose.ani")
                .Tag("[etc attack info]").Str("AttackInfo/HardAttack.atk").Str("AttackInfo/ChainA.atk").Str("AttackInfo/ChainB.atk")
                .Str("AttackInfo/HopFirst.atk").Str("AttackInfo/HopRepeat.atk").Str("AttackInfo/Dive.atk")
                .Tag("[attack motion]").Str("animation/attack1.ani").Str("Animation/Attack2.ani").Str("animation/attack3.ani").Build()),
            // 技能目录:职业 0 → ActionChrPath;三个主动技能与一个被动技能的 .skl(数是合成的)
            ("character/character.lst", NewScript().Int(0).Str("test/action.chr").Build()),
            ("skill/skilllist.lst", NewScript().Int(0).Str("testskill.lst").Build()),
            ("skill/testskill.lst", NewScript()
                .Int(181).Str("test/passive.skl").Int(HardAttackSkill).Str("test/hardattack.skl").Int(UpperSlashSkill).Str("test/upperslash.skl")
                .Int(BackStepSkill).Str("test/backstep.skl").Int(174).Str("test/passive.skl").Int(ChainSkill).Str("test/chain.skl").Int(WaveSkill).Str("test/wave.skl").Int(HopSkill).Str("test/hop.skl").Int(QuickStandSkill).Str("test/quickstand.skl").Int(JumpCommandSkill).Str("test/jumpcommand.skl")
                .Int(JumpBuffSkill).Str("test/jumpbuff.skl").Int(MagicBuffSkill).Str("test/magicbuff.skl").Int(ProcBuffSkill).Str("test/procbuff.skl").Int(DiveSkill).Str("test/dive.skl").Int(JumpSkill).Str("test/jumpskill.skl").Int(PoseStrikeSkill).Str("test/posestrike.skl").Int(FlagSkill).Str("test/flag.skl").Int(RiseSkill).Str("test/rise.skl").Int(GuardSkill).Str("test/guard.skl").Build()),
            // 腾空悬停俯冲类技能:指令单键技能,[static data] 四个数(第 3 个是落地放冲击对象的最小离地高度),[level info] 三列 (俯冲百分比、冲击固定值、冲击百分比)
            ("skill/test/dive.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(SKILL)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(11, 11).Tag("[cool time]").Int(2200, 2200)
                .Tag("[static data]").Int(1, 2, 3, DiveImpactMinHeight)
                .Tag("[level info]").Int(3, DiveRate1, DiveImpactFixed1, DiveImpactRate1, DiveRate2, DiveImpactFixed2, DiveImpactRate2).Build()),
            (DiveAtk, NewScript().Tag("[damage reaction]").Tag("[damage]").Tag("[push aside]").Int(DivePushAside).Build()),
            // 姿势→挥击类技能:冷却取 [level info] 第 1 列(没有 [cool time]),[level info] 两列 (百分比, 冷却毫秒)
            ("skill/test/posestrike.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(PoseStrikeMp, PoseStrikeMp).Tag("[cooltime level info]").Int(1)
                .Tag("[level info]").Int(2, PoseStrikeRate1, PoseStrikeCool1, PoseStrikeRate2, PoseStrikeCool2).Build()),
            // 防御姿态技能:指令"下、下、攻击",[static data] 一个数(最短保持毫秒),[level info] 四列 (后滑速度、后滑毫秒、物理减伤百分比、魔法减伤百分比)
            ("skill/test/guard.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(DOWN)").Sep(",").Paren("(DOWN)").Sep(",").Paren("(ATTACK)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(GuardMp, GuardMp).Tag("[cool time]").Int(GuardCool, GuardCool)
                .Tag("[static data]").Int(GuardMinHoldMs)
                .Tag("[level info]").Int(4, GuardSpeed1, GuardRecoilMs1, GuardPhysical1, GuardMagical1, GuardSpeed2, GuardRecoilMs2, GuardPhysical2, GuardMagical2).Build()),
            // 关键帧标志事件技能:[level info] 两列百分比(第 0 列是第一个窗口、第 1 列是标志 B 之后的窗口)
            ("skill/test/flag.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(FlagMp, FlagMp).Tag("[cool time]").Int(3000, 3000)
                .Tag("[level info]").Int(2, FlagRate0, FlagRate1, 140, 280).Build()),
            // 升空出招技能:[static data] 三个数 (升空高度、升空毫秒、后滑像素),[level info] 一列百分比
            ("skill/test/rise.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(RiseMp, RiseMp).Tag("[cool time]").Int(3000, 3000)
                .Tag("[static data]").Int(RiseHeight, RiseMs, RiseBack)
                .Tag("[level info]").Int(1, RiseRate1, 170).Build()),
            // 空中攻击强化类技能:指令单键"攻击",[cool time] 比一跳长,[level info] 一列(百分比)
            ("skill/test/jumpskill.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(ATTACK)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(JumpSkillMp, JumpSkillMp).Tag("[cool time]").Int(JumpSkillCoolMs, JumpSkillCoolMs)
                .Tag("[level info]").Int(1, JumpSkillRate1, JumpSkillRate2).Build()),
            // 增益类技能:跳跃力类(加成在 [static data],持续时间在 [level info] 一列)与魔法攻击力类(持续时间与加成是 [level info] 的两列)
            ("skill/test/jumpbuff.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(BUFF)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(12, 12).Tag("[cool time]").Int(2000, 2000)
                .Tag("[static data]").Int(JumpBuffAmount)
                .Tag("[level info]").Int(1, JumpBuffMs1, JumpBuffMs2).Build()),
            ("skill/test/magicbuff.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(DOWN)").Sep(",").Paren("(BUFF)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(8, 8).Tag("[cool time]").Int(1500, 1500)
                .Tag("[level info]").Int(2, MagicBuffMs1, MagicBuffAmount1, MagicBuffMs2, MagicBuffAmount2).Build()),
            // 概率触发类增益:持续时间是 [level info] 第 1 列、千分比概率是第 0 列
            ("skill/test/procbuff.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(UP)").Sep(",").Paren("(BUFF)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(6, 6).Tag("[cool time]").Int(1200, 1200)
                .Tag("[level info]").Int(2, ProcBuffPermille1, ProcBuffMs1, ProcBuffPermille2, ProcBuffMs2).Build()),
            ("skill/test/jumpcommand.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(DOWN)").Sep(",").Paren("(JUMP)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(10, 10).Tag("[cool time]").Int(1000, 1000)
                .Tag("[level info]").Int(2, 100, 5).Build()),
            // 倒地起身类技能:指令 (DOWN) & (JUMP)
            ("skill/test/quickstand.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(DOWN)").Sep("&").Paren("(JUMP)").Tag("[maximum level]").Int(1)
                .Tag("[consume MP]").Int(QuickStandMp, QuickStandMp).Tag("[cool time]").Int(QuickStandCool, QuickStandCool)
                .Tag("[static data]").Int(QuickStandMs).Build()),
            // 蓄力跳跃类技能:十个 [static data](蓄力、距离、高度、变速、窗口数的最小与最大)与一列 [level info]
            ("skill/test/hop.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(UP)").Sep("&").Paren("(SKILL)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(10, 10).Tag("[cool time]").Int(3500, 3500)
                .Tag("[static data]").Int(HopHoldMin, HopHoldMax, HopDistanceMin, HopDistanceMax, HopHeightMin, HopHeightMax, HopSpeedMin, HopSpeedMax, HopHitsMin, HopHitsMax)
                .Tag("[level info]").Int(1, HopRate1, HopRate2).Build()),
            (HopFirstAtk, NewScript().Tag("[damage reaction]").Tag("[damage]").Tag("[push aside]").Int(HopFirstPushAside).Build()),
            (HopRepeatAtk, NewScript().Tag("[damage reaction]").Tag("[down]").Tag("[push aside]").Int(HopRepeatPushAside).Build()),
            // 波系技能:三列 (固定值、大小比率、百分比),[casting time] 与三个 [static data]
            ("skill/test/wave.skl", NewScript()
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(SKILL)").Tag("[maximum level]").Int(10)
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
                .Tag("[type]").Str("[active]").Tag("[command]").Paren("(RIGHT)").Sep(",").Paren("(SKILL)").Tag("[maximum level]").Int(10)
                .Tag("[consume MP]").Int(20, 20).Tag("[cool time]").Int(3000, 3000).Tag("[static data]").Int(200, -400, 100, -100, 2)
                .Tag("[level info]").Int(4, 150, 250, 30, 50, 180, 300, 40, 60).Build()),     // 第一个数是列数(4),后面按行排:第 1 级 (150, 250, 30, 50),第 2 级 (180, 300, 40, 60)
            // 多段技能两段的攻击信息:第一段击退 ChainAPushAside,第二段击倒并击退 ChainBPushAside
            (ChainAAtk, NewScript().Tag("[damage reaction]").Tag("[damage]").Tag("[push aside]").Int(ChainAPushAside).Build()),
            (ChainBAtk, NewScript().Tag("[damage reaction]").Tag("[down]").Tag("[push aside]").Int(ChainBPushAside).Build()),
            // 技能预载脚本(与原版 sqr/common.nut 同形):常量、职业函数、按技能编号分支、依次预载特效;前面放一个别的技能的分支做干扰
            ("sqr/common.nut", Encoding.ASCII.GetBytes(
                "SKILL_OTHER_TEST <- 902;\r\nSKILL_CHAIN_TEST <- 901;\r\nSKILL_WAVE_TEST <- 903;\r\nSKILL_HOP_TEST <- 904;\r\nSKILL_DIVE_TEST <- 905;\r\nSKILL_JUMP_TEST <- 910;\r\n"
                + "function sq_LoadSkillEffect_Swordman(obj, skillIndex)\r\n{\r\n"
                + "\tif (skillIndex == SKILL_OTHER_TEST)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Other.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_CHAIN_TEST)\r\n\t{\r\n\t\t// comment\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain1.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain2.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_WAVE_TEST)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/WaveSlash.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/WaveSlash2.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_HOP_TEST)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain1.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain2.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_DIVE_TEST)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain1.ani\");\r\n\t}\r\n"
                + "\telse if (skillIndex == SKILL_JUMP_TEST)\r\n\t{\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/HardAttack1.ani\");\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/HardAttack1.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/HardAttack1.ani\");\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/HardAttack1.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain1.ani\");\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Chain2.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/WaveSlash.ani\");\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/WaveSlash2.ani\");\r\n"
                + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/HardAttack1.ani\");\r\n\t}\r\n}\r\n")),
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
            // (最后一帧带 DiveHoldBox:悬停俯冲把动画定在这一帧,命中全靠它;普通的空中攻击在这一帧还是 50 毫秒,框在身前 +100,够不着第 1 帧的框打得到的怪物)
            (JumpAttackAni, BuildBoxAni((0, 100, null), (1, 50, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }), (0, 50, new[] { DiveHoldBox }))),
            // 坐姿动作:三帧,没有攻击框(俯冲落地后播一遍)
            (SitAni, BuildBoxAni((0, SitFrameMs, null), (1, SitFrameMs, null), (0, SitFrameMs, null))),
            // 空中攻击强化技能的两种动作:A 四帧 × 50 毫秒、框在第 2 帧(身前 +20 起宽 40);B 三帧 × 60 毫秒、框在第 1 帧(身前 +100 起宽 40);框都又高又深
            (JumpSkillAAni, BuildBoxAni((0, JumpSkillAFrameMs, null), (1, JumpSkillAFrameMs, null),
                (0, JumpSkillAFrameMs, new[] { ((ushort)15, new[] { 20, -40, -100, 40, 80, 200 }) }), (1, JumpSkillAFrameMs, null))),
            (JumpSkillBAni, BuildBoxAni((0, JumpSkillBFrameMs, null),
                (1, JumpSkillBFrameMs, new[] { ((ushort)15, new[] { 100, -40, -100, 40, 80, 200 }) }), (0, JumpSkillBFrameMs, null))),
            // 三段普攻:四帧 × 100、三帧 × 100、两帧 × 200 毫秒;攻击框(类型 15)在武器图层的动画里,第三段的基础动画自己带一个;
            // 每帧带站立高度的受击框(原版的出招动画也带受击框;M49 的霸体测试要靠它让怪物打得中出招中的主角)
            (Attack1Ani, BuildBoxAni((0, 100, new[] { HeroHurtBox }), (1, 100, new[] { HeroHurtBox }), (0, 100, new[] { HeroHurtBox }), (1, 100, new[] { HeroHurtBox }))),
            (Attack2Ani, BuildBoxAni((0, 100, new[] { HeroHurtBox }), (1, 100, new[] { HeroHurtBox }), (0, 100, new[] { HeroHurtBox }))),
            (Attack3Ani, BuildBoxAni((0, 200, new[] { HeroHurtBox }), (1, 200, new[] { HeroHurtBox, ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }))),
            // 技能动作:护身(占动作表的第 0 项)与鬼斩(600 毫秒,第 2、3 帧各带一个攻击框)
            ("character/test/animation/guard.ani", BuildBoxAni((0, 100, null))),
            // 增益施法动作:三帧,没有攻击框,每帧带受击框(施法时主角站着挨打;M49 的霸体测试靠它让怪物打得中施法中的主角)
            (BuffAni, BuildBoxAni((0, BuffFrameMs, new[] { HeroHurtBox }), (1, BuffFrameMs, new[] { HeroHurtBox }), (0, BuffFrameMs, new[] { HeroHurtBox }))),
            (HardAttackEffect1, BuildEffectAni(false)),
            (HardAttackEffect2, BuildEffectAni(true)),
            (ChainEffect1, BuildEffectAni(false)),
            (ChainEffect2, BuildEffectAni(true)),
            (WaveEffect, BuildEffectAni(false)),
            (WaveEffect2, BuildKeyedEffectAni()),
            (WaveCastAni, BuildBoxAni((0, 10000, null), (1, 50, null), (0, 50, null), (1, 50, null),
                (0, 50, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (1, 50, null))),
            // 蓄力跳跃类技能:蓄力姿势一帧 500 毫秒;跳跃七帧 × 100 毫秒,第 3 到 5 帧各带一个攻击框
            (HopReadyAni, BuildBoxAni((0, 500, null))),
            (HopAni, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, null),
                (1, 100, new[] { HopStrikeBox }), (0, 100, new[] { HopStrikeBox }), (1, 100, new[] { HopStrikeBox }), (0, 100, null))),
            (ChainAAni, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (1, 100, null))),
            (ChainBAni, BuildBoxAni((0, 100, null), (1, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }), (0, 100, null))),
            (FlagAni, BuildFlagBoxAni(
                (0, 100, 0, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }),
                (1, 100, FlagA, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }),
                (0, 100, 0, null),
                (1, 100, FlagB, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }),
                (0, 100, 0, null))),
            (RiseAni, BuildFlagBoxAni(
                (0, 10000, 0, new[] { ((ushort)15, new[] { 20, -10, 0, 60, 20, 50 }) }),
                (1, 30, 0, null),
                (0, 40, 0, null))),
            // 防御姿态的姿势动作:两帧、每帧停留 10000 毫秒(姿态阶段定第 0 帧、后滑阶段定第 1 帧),每帧带站立高度的受击框,没有攻击框
            (GuardAni, BuildBoxAni((0, 10000, new[] { HeroHurtBox }), (1, 10000, new[] { HeroHurtBox }))),
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
                .Int(ActBoardReadId).Str("blob/actbread.obj")
                .Int(ActBoardWriteId).Str("blob/actbwrite.obj")
                .Int(ActBoardLoopId).Str("blob/actbloop.obj")
                .Int(ActBoardInitId).Str("blob/actbinit.obj")
                .Int(ActHomeInstantId).Str("blob/acthomei.obj")
                .Int(ActHomeSmoothId).Str("blob/acthomes.obj")
                .Int(ActHomeStraightId).Str("blob/acthomet.obj")
                .Int(ActHomeMeId).Str("blob/acthomem.obj")
                .Int(ActHomeMonsterId).Str("blob/acthomen.obj")
                .Int(ActHomeIdleId).Str("blob/acthomez.obj")
                .Int(ActHomeOffId).Str("blob/acthomeo.obj")
                .Int(ActHomeOnId).Str("blob/acthomep.obj")
                .Int(ActHomeTurnId).Str("blob/acthomer.obj")
                .Int(ActHomeCharId).Str("blob/acthomec.obj")
                .Int(ActHomeCarryId).Str("blob/acthomek.obj")
                .Int(ActDriftId).Str("blob/actdrift.obj")
                .Int(ActNearId).Str("blob/actnear.obj")
                .Int(ActFarId).Str("blob/actfar.obj")
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
            (BlobFamilyMobPath, NewScript()
                .Tag("[waiting action]").Str("Action/Stay.act")
                .Tag("[move action]").Str("Action/Move.act")
                .Tag("[sit action]").Str("Action/Sit.act")
                .Tag("[damage action 1]").Str("Action/Damage1.act")
                .Tag("[damage action 2]").Str("Action/Damage2.act")
                .Tag("[down action]").Str("Action/Down.act")
                .Tag("[overturn action]").Str("Action/Overturn.act")
                .Tag("[last action]").Str("Action/Last.act")
                .Tag("[proc action]").Str("Action/Proc.act")
                .Tag("[etc action]").Str("Action/Etc1.act").Str("Action/Etc2.act")
                .Tag("[/etc action]")
                .Tag("[attack action]").Str("Action/Attack.act").Tag("[/attack action]").Build()),
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
            // M57:换进来的动画(见 SwapStayAni 的说明)
            (SwapStayAni, BuildBlobAni(true, (0, 100, BlobHurtBox), (1, 100, BlobHurtBox))),
            (SwapMoveAni, BuildBlobAni(true, (2, 100, BlobHurtBox), (3, 100, BlobHurtBox))),
            (SwapDamage1Ani, BuildBlobAni(false, (4, 10000, BlobHurtBox))),
            (SwapDamage2Ani, BuildBlobAni(false, (5, 10000, BlobHurtBox))),
            (SwapDownAni, BuildBlobAni(false, (6, 80, BlobHurtBox), (7, 10000, default), (8, 1500, default))),
            (SwapStrikeAni, BuildBlobBoxesAni(false, (0, 80, new[] { BlobHurtBox }), (1, 80, new[] { BlobHurtBox }),
                (2, 80, new[] { BlobHurtBox, BlobAwayBox }), (3, 80, new[] { BlobHurtBox }))),
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
    /// 同 <see cref="BuildBoxAni"/>,每项多一个关键帧标志(0 表示这一帧不带标志,M65);带标志的帧写帧属性 23。
    /// </summary>
    private static byte[] BuildFlagBoxAni(params (int FrameIndex, int DelayMs, int Flag, (ushort Type, int[] Values)[] Boxes)[] frames)
    {
        var specs = new List<TestData.AniFrameSpec>();
        foreach ((int frameIndex, int delay, int flag, (ushort Type, int[] Values)[] boxes) in frames)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frameIndex, X = -5, Y = -8 };
            f.Props.Add((12, TestData.LE32(delay)));
            if (flag != 0)
            {
                f.Props.Add((23, TestData.LE32(flag)));
            }
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
