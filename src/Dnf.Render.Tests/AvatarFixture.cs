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
    public const int HardAttackRate = 200;
    public const int HardAttackFixed = 40;
    public const int HardAttackPushAside = 150;
    public const int HardAttackMotionMs = 600;

    /// <summary>
    /// 鬼斩的两层特效(角色目录下 effect/animation/):都是 350 毫秒——空帧 150、图 50、图 50、空帧 100(原版的斩击光影也是先空一段再出图);
    /// 第二层每帧带图形特效 2(加法混合)。图用皮肤 IMG 的第 0、1 帧,帧偏移 (-5, -8)。
    /// </summary>
    public const string HardAttackEffect1 = "character/test/effect/animation/hardattack1.ani";
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
    public const string BlobAnimationDir = "monster/blob/animation_blob";
    public const string BlobImage = "sprite/monster/blob/blob.img";

    /// <summary>受击框(类型 14):x 从 −10 起宽 40(故意不对称,朝向才看得出镜像),y 从 −6 起深 12,z 从 0 起高 60。</summary>
    public static readonly (ushort Type, int[] Values) BlobHurtBox = ((ushort)14, new[] { -10, -6, 0, 40, 12, 60 });

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

        public Script Str(string text)
        {
            _tokens.Add((StrType, _owner.Intern(text)));
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
            (ActionChrPath, NewScript()
                .Tag("[body image path]").Str("Character/Test/Equipment/Avatar/skin/tt_body%04d.img")
                .Tag("[rest motion]").Str("animation/rest.ani")
                .Tag("[waiting motion]").Str("animation/wait.ani")
                .Tag("[move motion]").Str("animation/move.ani")
                .Tag("[move speed]").Float(MoveSpeedStat)
                .Tag("[damage motion 1]").Str("animation/damage1.ani")
                .Tag("[damage motion 2]").Str("Animation/Damage2.ani")
                .Tag("[dash motion]").Str("animation/dash.ani")
                .Tag("[dashattack motion]").Str("Animation/DashAttack.ani")
                .Tag("[jump motion]").Str("animation/jump.ani")
                .Tag("[jumpattack motion]").Str("animation/jumpattack.ani")
                .Tag("[jump power]").Int(JumpPowerStat)
                .Tag("[weight]").Int(WeightStat)
                .Tag("[jump speed]").Int(JumpSpeedStat)
                .Tag("[width]").Int(BodyWidthStat, BodyDepthStat)
                // 初始技能表("编号 等级"成对):181 与 174 是被动,5、46、169 是带出招指令的主动技能,顺序与原版新建剑士一样
                .Tag("[skill]").Int(181, 2, HardAttackSkill, 1, UpperSlashSkill, UpperSlashLevel, BackStepSkill, 1, 174, 1)
                // 技能动作与攻击信息(两个表的下标不对应:护身动作没有攻击信息,鬼斩在动作表里是第 1 项、在攻击信息表里是第 0 项)
                .Tag("[etc motion]").Str("Animation/Guard.ani").Str("Animation/HardAttack.ani")
                .Tag("[etc attack info]").Str("AttackInfo/HardAttack.atk")
                .Tag("[attack motion]").Str("animation/attack1.ani").Str("Animation/Attack2.ani").Str("animation/attack3.ani").Build()),
            // 技能目录:职业 0 → ActionChrPath;三个主动技能与一个被动技能的 .skl(数是合成的)
            ("character/character.lst", NewScript().Int(0).Str("test/action.chr").Build()),
            ("skill/skilllist.lst", NewScript().Int(0).Str("testskill.lst").Build()),
            ("skill/testskill.lst", NewScript()
                .Int(181).Str("test/passive.skl").Int(HardAttackSkill).Str("test/hardattack.skl").Int(UpperSlashSkill).Str("test/upperslash.skl")
                .Int(BackStepSkill).Str("test/backstep.skl").Int(174).Str("test/passive.skl").Build()),
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
                .Tag("[animation job]").Str(JobTag)
                .Tag("[variation]").Int(3, 2)
                .Tag("[layer variation]").Int(650).Str("swordb").Int(2999).Str("swordc").Int(3000).Str("swordd")
                .Tag("[animation job]").Str("[other]")
                .Tag("[variation]").Int(8, 8)
                .Tag("[layer variation]").Int(1).Str("swordz")
                .Tag("[sub type]").Int(WeaponSubType).Build()),
            (EquipRoot + "/weapon/sword/sw_digit.equ", NewScript()
                .Tag("[equipment type]").Str("[weapon]")
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
            // 奔跑(循环)、奔跑攻击;跳跃动画有两个停留帧(最高点与落地前,各 10000 毫秒),最后一帧是 50 毫秒的落地帧;空中攻击 200 毫秒
            (DashAni, BuildAni(new[] { (0, 100), (1, 100), (0, 100), (1, 100) })),
            // 奔跑攻击在第 1 帧(100 到 200 毫秒)、空中攻击在第 1 帧(100 到 150 毫秒)各有一个攻击框:x 从 +20 起宽 40,y 从 −10 起深 20,z 从 0 起高 50
            (DashAttackAni, BuildBoxAni((0, 100, null), (1, 100, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }), (0, 100, null))),
            (JumpAni, BuildBoxAni((0, 100, null), (1, 100, null), (0, 80, null), (1, 10000, null), (0, 100, null), (1, 80, null), (0, 10000, null), (1, 50, null))),
            (JumpAttackAni, BuildBoxAni((0, 100, null), (1, 50, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }), (0, 50, null))),
            // 三段普攻:四帧 × 100、三帧 × 100、两帧 × 200 毫秒;攻击框(类型 15)在武器图层的动画里,第三段的基础动画自己带一个
            (Attack1Ani, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, null), (1, 100, null))),
            (Attack2Ani, BuildBoxAni((0, 100, null), (1, 100, null), (0, 100, null))),
            (Attack3Ani, BuildBoxAni((0, 200, null), (1, 200, new[] { ((ushort)15, new[] { 20, -10, 0, 40, 20, 50 }) }))),
            // 技能动作:护身(占动作表的第 0 项)与鬼斩(600 毫秒,第 2、3 帧各带一个攻击框)
            ("character/test/animation/guard.ani", BuildBoxAni((0, 100, null))),
            (HardAttackEffect1, BuildEffectAni(false)),
            (HardAttackEffect2, BuildEffectAni(true)),
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
