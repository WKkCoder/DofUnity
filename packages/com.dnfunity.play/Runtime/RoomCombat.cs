using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    /// <summary>一击普攻带的静态数据(取自角色 .chr 的 [attack info] 指向的 .atk;取不到时全是默认值)。</summary>
    public readonly struct HeroStrikeInfo
    {
        /// <summary>[damage bonus]:这一击相对基础伤害的加成百分数(可以是负数)。</summary>
        public int DamageBonus { get; }

        /// <summary>[push aside]:水平击退的像素数(单位按像素理解,没有核对)。</summary>
        public int PushAside { get; }

        /// <summary>[damage reaction] 是倒地。</summary>
        public bool KnockDown { get; }

        /// <summary>
        /// 技能伤害的两个数(技能 .skl 的 [level info] 取当前等级的第 0、1 列,证据 E-076):按攻击力百分之几、外加固定值。
        /// 两个都是 0 表示不是技能伤害(普攻按 <see cref="DamageBonus"/> 算)。
        /// </summary>
        public int SkillRatePercent { get; }

        public int SkillFixed { get; }

        /// <summary>伤害按技能的两个数算(<see cref="SkillRatePercent"/>、<see cref="SkillFixed"/>)。</summary>
        public bool IsSkillDamage => SkillRatePercent != 0 || SkillFixed != 0;

        // 内部存"值 + 1":default(HeroStrikeInfo) 里整数是 0,正好对应"没写"(-1)
        private readonly int _hitValuePlusOne;

        /// <summary>[hit info] 的整数(命中停顿毫秒,证据 E-077);没写是 -1,表示取武器表。</summary>
        public int HitValue => _hitValuePlusOne - 1;

        /// <summary>[is use weapon hit delay rate]:为真时停顿 = 武器的停顿 × <see cref="HitValue"/>。</summary>
        public bool UseWeaponHitDelayRate { get; }

        private readonly float _hitScale;
        private readonly bool _hasHitScale;

        /// <summary>[hit info] 的浮点项(命中延迟倍率,受击硬直要乘它,证据 E-080);没写是 -1,表示取武器表。</summary>
        public float HitScale => _hasHitScale ? _hitScale : -1f;

        /// <summary>[knuck back] 的类型(0 普通、1 击退、2 短击退、3 按像素、-1 不后退;证据 E-080)。</summary>
        public int KnockBackType { get; }

        /// <summary>命中模式为 [burn](客户端的 2):受击不击退(证据 E-080)。</summary>
        public bool NoKnockBack { get; }

        /// <summary>[force hit stun time]:非 0 时直接当作受击硬直(毫秒)。</summary>
        public int ForceHitStunMs { get; }

        /// <summary>[attack type] 是魔法:受击硬直的倍率再乘 0.7(证据 E-080)。</summary>
        public bool Magical { get; }

        /// <summary>[lift up]:倒地的抬升量(类型 1 到 3 当初速、按体重缩放,类型 0 当高度;证据 E-079、E-083)。</summary>
        public int LiftUp { get; }

        /// <summary>[bounce] 非 0:倒地时先砸地再弹起飞第二次(证据 E-079、E-083)。</summary>
        public bool Bounce { get; }

        /// <summary>[ignore weight] 非 0:倒地的击飞不受怪物体重影响(类型 0 或 2;证据 E-079)。</summary>
        public bool IgnoreWeight { get; }

        /// <summary>[damage reaction] 是普通受击(1,或按 1 处理的 3):怪物在空中或已在倒地中时也走倒地分支(证据 E-079 第 6' 步)。</summary>
        public bool ReactionDamage { get; }

        /// <summary>[hit wav]:命中时放的声音标签(audio.xml 的 ID,证据 E-089);没写为 null(这时命中声取武器的 [weapon wav],推断)。</summary>
        public string HitWav { get; }

        /// <summary>
        /// [damage reaction] 是 none(0):命中只扣血,不产生受击反应(决策函数的第一步直接返回"不产生反应",证据 E-079、E-105)。
        /// 缺省(没写 [damage reaction])按受击算,所以 <c>default</c> 与旧的构造都是 false。
        /// </summary>
        public bool ReactionNone { get; }

        // 攻击信息读出来的伤害输入(没有 .atk 的手填写法为 null)
        private readonly HitInput _source;

        /// <summary>
        /// 真实伤害公式要的输入(证据 E-110):取自 .atk 的 [damage]、[attack type]、[weapon damage apply]、[damage bonus] 等;
        /// 手填(没有 .atk)的只有 <see cref="DamageBonus"/> 与 <see cref="Magical"/>,按"带武器攻击力"的普攻写法补;
        /// 技能伤害(<see cref="IsSkillDamage"/>)按 <see cref="HitInput.WithSkillDamage"/> 换算。
        /// </summary>
        public HitInput Input
        {
            get
            {
                HitInput input = _source ?? HitInput.FromBonus(DamageBonus, Magical);
                return IsSkillDamage ? input.WithSkillDamage(SkillRatePercent, SkillFixed) : input;
            }
        }

        public HeroStrikeInfo(int damageBonus, int pushAside, bool knockDown, int skillRatePercent = 0, int skillFixed = 0,
            int hitValue = -1, bool useWeaponHitDelayRate = false, float hitScale = -1f, int knockBackType = 0, bool noKnockBack = false,
            int forceHitStunMs = 0, bool magical = false, int liftUp = 0, bool bounce = false, bool ignoreWeight = false, bool reactionDamage = false,
            string hitWav = null, bool reactionNone = false, HitInput source = null)
        {
            _source = source;
            ReactionNone = reactionNone;
            HitWav = string.IsNullOrEmpty(hitWav) ? null : hitWav;
            DamageBonus = damageBonus;
            PushAside = pushAside;
            KnockDown = knockDown;
            SkillRatePercent = skillRatePercent;
            SkillFixed = skillFixed;
            _hitValuePlusOne = unchecked(hitValue + 1);
            UseWeaponHitDelayRate = useWeaponHitDelayRate;
            _hitScale = hitScale;
            _hasHitScale = true;
            KnockBackType = knockBackType;
            NoKnockBack = noKnockBack;
            ForceHitStunMs = forceHitStunMs;
            Magical = magical;
            LiftUp = liftUp;
            Bounce = bounce;
            IgnoreWeight = ignoreWeight;
            ReactionDamage = reactionDamage;
        }

        /// <summary>读一份 .atk 里命中要用的字段;路径为 null、读不到时全是默认值。</summary>
        public static HeroStrikeInfo Read(GameData data, string atkPath)
        {
            AttackInfoData info = atkPath == null || data == null ? null : data.GetAttackInfo(atkPath)?.Data;
            return info == null
                ? new HeroStrikeInfo(0, 0, false)
                : new HeroStrikeInfo(info.DamageBonus, info.PushAside, info.Reaction == DamageReaction.Down, 0, 0, info.HitValue, info.UseWeaponHitDelayRate,
                    info.HitScale, info.KnockBackType, info.HitType == HitKind.Burn, info.ForceHitStunTime, info.AttackType == AttackKind.Magic,
                    info.LiftUp, info.Bounce != 0, info.IgnoreWeight != 0,
                    info.Reaction == DamageReaction.Damage || info.Reaction == DamageReaction.DamageExceptHuman, info.HitWav,
                    info.Reaction == DamageReaction.None, HitInput.From(info));
        }

        /// <summary>同一份攻击信息,换上技能伤害的两个数。</summary>
        public HeroStrikeInfo WithSkillDamage(int ratePercent, int fixedDamage)
        {
            return new HeroStrikeInfo(DamageBonus, PushAside, KnockDown, ratePercent, fixedDamage, HitValue, UseWeaponHitDelayRate,
                HitScale, KnockBackType, NoKnockBack, ForceHitStunMs, Magical, LiftUp, Bounce, IgnoreWeight, ReactionDamage, HitWav, ReactionNone, _source);
        }
    }

    /// <summary>
    /// 占位的伤害数:主角没有攻击面板、或没读到公式常量时(命令行 --no-damage、没有数据的合成试验场)主角打怪物还用它;
    /// 有面板与常量时走原版公式(<see cref="HitDamage"/>,证据 E-110),不再经过这里。
    /// 基础值 25.3 是照基线录像标的:一级哥布林挨第一击(加成 −15%)时屏幕上出现 21 与 22,即 25.3 × 0.85 = 21.5 上下取整;
    /// 之后每一击按 [damage bonus] 缩放,小数部分按概率进位(同一份基础值给出 21 与 22 两种读数)。证据 E-069。
    /// </summary>
    public static class PlaceholderDamage
    {
        public const double BaseHit = 25.3;

        public static int Roll(int damageBonusPercent, Random random)
        {
            return Round(BaseHit * (100 + damageBonusPercent) / 100.0, random);
        }

        /// <summary>
        /// 技能的基础值:照基线里一级鬼斩打在一级哥布林身上显示 146 标的(把 .skl 一级的两个数代入 <see cref="RollSkill"/> 的式子反推,
        /// 得 31.15 上下;与普攻的 <see cref="BaseHit"/> 不同,可能是魔法/物理攻击力的差别,也可能式子本身不是这样,只有一个样本,证据 E-076)。
        /// </summary>
        public const double SkillBaseHit = 31.15;

        /// <summary>
        /// 技能一击的占位伤害:<see cref="SkillBaseHit"/> × 百分比 / 100 + 固定值(两个数取自技能 .skl 的 [level info]),小数部分按概率进位。
        /// 真实公式见 <see cref="HitDamage"/>(E-110),占位值只在没有面板时用。
        /// </summary>
        public static int RollSkill(int ratePercent, int fixedDamage, Random random)
        {
            return Round(SkillBaseHit * ratePercent / 100.0 + fixedDamage, random);
        }

        private static int Round(double v, Random random)
        {
            double floor = Math.Floor(v);
            int result = (int)floor + (random.NextDouble() < v - floor ? 1 : 0);
            return result < 1 ? 1 : result;
        }
    }

    /// <summary>
    /// 场景里的一个伤害数字(证据 E-107):数值、标志(<see cref="DamageNumberFlags"/>,决定数字图和动画)、世界位置与已经过去的时间。
    /// 位置是客户端放数字的位置:x = 命中点(攻击框与受击框的相交盒)的横向中心加 [-10, 9] 的抖动,y = 受击者的 y,
    /// z = 命中点的高度中心 + 60 + [0, 19] 的抖动(都是单位像素;抖动用的随机数与伤害无关)。
    /// </summary>
    public sealed class HitPopup
    {
        /// <summary>显示的数(伤害点数)。</summary>
        public int Value { get; }

        /// <summary>数的十进制文字(占位文字画法与测试用)。</summary>
        public string Text => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>标志位,见 <see cref="DamageNumberFlags"/>。</summary>
        public int Flags { get; }

        public double WorldX { get; }
        public double WorldY { get; }
        public int Z { get; }
        public double AgeMs { get; set; }

        /// <summary>主角挨的伤害(红橙色一套);否则是怪物挨的(黄色一套)。</summary>
        public bool OnHero => (Flags & DamageNumberFlags.VictimIsHero) != 0;

        /// <summary>动画模板(<see cref="DamageNumberFlags.Template"/>)。</summary>
        public int Template => DamageNumberFlags.Template(Flags);

        /// <summary>数字图里这一套的 "0" 所在帧号。</summary>
        public int FirstFrame => DamageNumberFlags.FirstFrame(Flags, Value);

        /// <summary>从弹出到消失一共多少毫秒。</summary>
        public double LifeMs => DamageNumberAnimation.DurationMs(Template);

        public HitPopup(int value, int flags, double worldX, double worldY, int z)
        {
            Value = value;
            Flags = flags;
            WorldX = worldX;
            WorldY = worldY;
            Z = z;
        }
    }

    /// <summary>
    /// 主角的受击框:基础动作 .ani 当前帧里的类型 14 框(基础动作的 .ani 只有受击框,攻击框在武器图层里,证据 E-069),换成世界坐标;
    /// 朝向与离地高度都算进去。受击姿势(受击动作)的帧也有自己的受击框。
    /// </summary>
    public static class HeroHurtBoxes
    {
        public static List<Aabb> At(TownHero hero)
        {
            var result = new List<Aabb>();
            if (hero == null)
            {
                return result;
            }
            AniFile ani = hero.CurrentAni;
            int frame = hero.CurrentFrame;
            if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
            {
                return result;
            }
            int direction = hero.FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(hero.X), wy = (int)Math.Floor(hero.Y), wz = (int)Math.Round(hero.Z);
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Damage)
                {
                    result.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
            return result;
        }

        /// <summary>头顶的世界高度(受击框的最高点);没有受击框时用 <paramref name="fallback"/>。</summary>
        public static int HeadHeight(TownHero hero, int fallback)
        {
            int top = int.MinValue;
            foreach (Aabb b in At(hero))
            {
                top = Math.Max(top, b.MaxZ);
            }
            return top == int.MinValue ? fallback : top;
        }
    }

    /// <summary>主角受击的几个占位值:没有受击硬直表(Etc/EtcParameter.etc)时用。有表时硬直与击退都按客户端的公式算(<see cref="HitStun"/>,证据 E-080)。</summary>
    public static class HeroHurtTuning
    {
        /// <summary>没有硬直表时的受击硬直(毫秒,未量)。</summary>
        public const int FallbackStunMs = 350;

        /// <summary>没有硬直表时的击退(像素与毫秒,取公式默认值的量级)。</summary>
        public const int FallbackKnockPixels = 30;

        public const int FallbackKnockMs = 150;

        /// <summary>主角被打死后停在受击姿势上的"硬直"(毫秒):实际上是永远。</summary>
        public const double DeadHoldMs = 1.0e12;
    }

    /// <summary>
    /// 主角普攻的攻击框:每个武器图层有自己的动作动画(与基础动作同帧数),攻击框(类型 15)写在武器图层的动画里;
    /// 基础动作里的类型 15 也一并算(例如第三击的基础动画自己带攻击框)。证据 E-069。
    /// </summary>
    public sealed class HeroStrikeBoxes
    {
        private readonly DnfAssets _assets;
        private readonly Dictionary<string, List<AniFile>> _layerAnis = new Dictionary<string, List<AniFile>>(StringComparer.Ordinal);

        public HeroStrikeBoxes(DnfAssets assets)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        }

        /// <summary>主角此刻这一击在第 <paramref name="frame"/> 帧的攻击框(世界坐标)。不在攻击或这一帧没有攻击框时返回空表。</summary>
        public List<Aabb> At(TownHero hero, int frame)
        {
            var result = new List<Aabb>();
            if (hero == null || !hero.IsAttacking)
            {
                return result;
            }
            int direction = hero.FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(hero.X), wy = (int)Math.Floor(hero.Y);
            int wz = (int)Math.Round(hero.Z);   // 空中攻击的判定框跟着主角离地的高度
            AddBoxes(result, hero.CurrentAni, frame, wx, wy, wz, direction);
            string file = hero.AttackMotionFile;
            if (file == null)
            {
                return result;
            }
            foreach (AniFile ani in LayerAnimations(hero.Outfit, file))
            {
                AddBoxes(result, ani, frame, wx, wy, wz, direction);
            }
            return result;
        }

        private List<AniFile> LayerAnimations(AvatarOutfit outfit, string motionFile)
        {
            List<string> paths = outfit.WeaponAnimationPaths(motionFile);
            string key = string.Join("|", paths);
            if (_layerAnis.TryGetValue(key, out List<AniFile> cached))
            {
                return cached;
            }
            var anis = new List<AniFile>();
            foreach (string path in paths)
            {
                AniFile ani = _assets.LoadAni(path);
                if (ani != null)
                {
                    anis.Add(ani);
                }
            }
            _layerAnis[key] = anis;
            return anis;
        }

        private static void AddBoxes(List<Aabb> into, AniFile ani, int frame, int wx, int wy, int wz, int direction)
        {
            if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
            {
                return;
            }
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Attack)
                {
                    into.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
        }
    }

    /// <summary>
    /// 一场能打的副本房间:房间场景 + 主角 + 房间地图 [monster] 里的怪物。每帧 <see cref="Update"/> 推进主角、怪物与命中判定,
    /// <see cref="Build"/> 把怪物、受击对象的血条、伤害数字与连击数叠进绘制列表。
    /// 已做:主角三段普攻(.chr [attack motion])的攻击框与怪物受击框相交判定(同一击对同一怪只算一次)、
    /// 怪物生命值取自 <see cref="MonsterAbility"/>、受击硬直与击退(E-080)、倒地(E-082、E-083)、死亡(E-084)、命中停顿(E-077)。
    /// 技能(证据 E-076):A 鬼斩、S 上挑、D 后跳三个快捷槽(<see cref="Skills"/>),冷却取自技能脚本;鬼斩与上挑能打中怪物,后跳是位移;
    /// 鬼斩放出来时还有两层斩击光影(<see cref="Effects"/>,<see cref="AnimatedEffect"/>)。
    /// 怪物还手(证据 E-086):怪物追到"这一招的攻击框够得着主角受击框"的位置再出招,招式动画里的攻击框(类型 15)与主角当前动作的受击框(类型 14)相交就算打中,
    /// 一次挥击只打一下;伤害交给原版公式(<see cref="DamageFormula"/>,输入是占位值,见 <see cref="MonsterDamage"/>);主角有生命与魔法(<see cref="Vitals"/>)、
    /// 受击硬直与击退、左下角的占位血条、红色伤害数字;生命归零后主角停在受击姿势、画面上显示 DEAD,不再接受输入。
    /// 构造时不给 <see cref="Vitals"/> 就是旧的行为:主角打不到、怪物按固定距离出招、没有血条(命令行 --no-damage、播放器 -dnfnodamage 用它,按时刻对拍的回归检查靠它保持原来的时刻)。
    /// 魔法值(证据 E-087):放技能时扣 .skl 的 [consume MP],不够就放不出来;按 .chr 的 [MP regen speed](每分钟,单位是推断的)连续恢复。
    /// 主角打怪物的伤害(证据 E-110):给了攻击面板(<see cref="HeroPanel"/>)与公式常量(随 <see cref="Vitals"/> 一起装配)时按原版公式算——
    /// 攻击信息(<see cref="HeroStrikeInfo.Input"/>)、主角面板(<see cref="Panel"/>)、怪物的防御面板(<see cref="MonsterActor.Defense"/>)填进伤害结构,
    /// 交给 <see cref="HitDamage"/>:物理/魔法、防御减免、元素抗性、暴击(数字用暴击那套)、低等级怪物挨的第一击算额外命中;
    /// 没有面板或常量时仍用 <see cref="PlaceholderDamage"/>(<see cref="UsesFormulaDamage"/> 为假)。
    /// 没做:主角的等级成长与属性点(面板只对一级的新角色成立)、装备的其余加成、技能的逐级成长、上挑自己的攻击信息与伤害数、
    /// 复活与结算、技能音效与语音、掉落。出口开关与换房间在 <see cref="DungeonSession"/>。证据 E-069。
    /// </summary>
    public sealed class RoomCombatSession : ISkillSession, ISoundSource
    {
        /// <summary>一小步的最长时间(毫秒):比任何动画帧都短,保证每一帧的判定框至少被检查一次。</summary>
        public const double MaxStepMs = 10;

        /// <summary>伤害数字落在命中点之上多高(像素,证据 E-107)。</summary>
        public const int NumberRise = 60;

        public const double TargetBarMs = 3500;

        /// <summary>连击窗口(毫秒):最后一击之后多久清零。原来的占位是 2600,现在取界面窗口的基线标定值(证据 E-108)。</summary>
        public const double ComboMs = ComboCounter.DefaultWindowMs;

        public const int TargetBarX = 36;
        public const int TargetBarY = 118;
        public const int TargetBarWidth = 112;
        public const int TargetBarHeight = 12;

        private readonly DnfAssets _assets;
        private readonly RoomSession _room;
        private readonly List<MonsterActor> _monsters;
        private readonly HeroStrikeInfo[] _strikes;
        private readonly HeroStrikeInfo _dashStrike;
        private readonly HeroStrikeInfo _jumpStrike;
        private readonly HeroStrikeBoxes _boxes;
        private readonly OverlayArt _art = new OverlayArt();

        /// <summary>原版的底栏(血魔球、技能槽等,证据 E-088);图包里没有时为 null,退回占位的色块血条。</summary>
        private readonly HudArt _hud;

        /// <summary>原版样式的目标怪物血条窗口(证据 E-106);图包里没有 monster_hp.img 时为 null,退回占位的色块血条(<see cref="Target"/>)。</summary>
        private MonsterHpWindow _monsterHp;

        /// <summary>界面计时用的墙钟(毫秒):每一小步累加,与命中停顿无关(目标血条的闪光与缩短、死亡 X 的闪烁都按它走)。</summary>
        private double _hudClockMs;

        /// <summary>伤害数字的位图字画法(证据 E-107);图包里没有 combo_big.img 时为 null,退回占位的文字。</summary>
        private DamageNumberArt _numberArt;

        /// <summary>连击计数(累计、窗口、滑入与抖动的计时,证据 E-108);按界面时钟走,与命中停顿无关。</summary>
        private readonly ComboCounter _combo = new ComboCounter();

        /// <summary>连击图的画法(证据 E-108);图包里没有 combo.img 时为 null,退回占位的文字。</summary>
        private ComboArt _comboArt;

        /// <summary>伤害数字位置抖动用的随机数(客户端用的是全局随机数;这里单独一个,免得改变伤害序列)。</summary>
        private readonly Random _numberRandom;
        private readonly List<HitPopup> _popups = new List<HitPopup>();
        private readonly List<AnimatedEffect> _effects = new List<AnimatedEffect>();
        private readonly List<MonsterProjectile> _projectiles = new List<MonsterProjectile>();
        private readonly List<string> _throwSoundScratch = new List<string>();
        private readonly HashSet<MonsterActor> _hitThisSwing = new HashSet<MonsterActor>();
        private readonly Random _random;

        /// <summary>怪物打主角的伤害种子用的随机数,与主角打怪物的伤害序列分开,免得互相改变对方的序列。</summary>
        private readonly Random _monsterRandom;
        private readonly DamageConstants _damageConstants;

        /// <summary>主角打怪物时的攻击面板(证据 E-110);为 null 时主角打怪物仍用占位伤害(<see cref="PlaceholderDamage"/>)。</summary>
        private readonly HeroPanel _heroPanel;
        private readonly AttackerPanel _attacker;

        /// <summary>倒地的随机抖动(水平量 ±20%、高度 ±10%;E-083)用的随机数,与伤害的随机数分开,免得改变伤害序列。</summary>
        private readonly Random _physicsRandom;

        /// <summary>主角的倒地与起身(证据 E-079、E-082、E-085、E-105)。</summary>
        private readonly HeroKnockdown _heroDown;

        /// <summary>怪物出招语音掷骰的种子(客户端的线性同余随机数,<see cref="ClientRandom"/>),与其余随机数分开。</summary>
        private uint _voiceSeed;

        /// <summary>怪物每次进入出招状态放出招语音的概率(百分数,缺省 15,证据 E-104);测试可以改。</summary>
        public int AttackVoicePercent { get; set; } = MonsterAttackRules.AttackVoicePercent;
        private long _evalSwing = -1;
        private int _evalFrame = -1;
        private double _targetMs = double.MaxValue;
        private double _heroPauseMs;
        private bool _bufferedAttack;
        private bool _bufferedJump;
        private SkillKeys _bufferedSkills;

        // 声音提示(证据 E-089、E-090):逻辑只发提示,播放在别处
        private readonly SoundCueQueue _sounds = new SoundCueQueue();
        private readonly WeaponSound _weaponSound;
        private HeroMotion _cueMotion;
        private long _cueSwing;
        private readonly Dictionary<MonsterActor, long> _cueMonsterSwing = new Dictionary<MonsterActor, long>();
        private readonly bool[] _cueCooling;

        public RoomSession Room => _room;
        public IReadOnlyList<MonsterActor> Monsters => _monsters;
        public IReadOnlyList<HitPopup> Popups => _popups;

        /// <summary>正在播的技能特效(鬼斩的斩击光影等)。</summary>
        public IReadOnlyList<AnimatedEffect> Effects => _effects;

        /// <summary>怪物扔出来、还在飞的东西(证据 E-103)。</summary>
        public IReadOnlyList<MonsterProjectile> Projectiles => _projectiles;

        /// <summary>从开始玩起怪物一共扔出过多少个投射物(含已经消失的)。</summary>
        public int ProjectilesThrown { get; private set; }

        /// <summary>其中打中主角的个数。</summary>
        public int ProjectileHits { get; private set; }

        /// <summary>最近一次被打中的怪物(占位血条用,<see cref="TargetBarMs"/> 毫秒内没再被打中就清空);原版样式的窗口见 <see cref="MonsterWindow"/>。</summary>
        public MonsterActor Target { get; private set; }

        /// <summary>原版样式的目标怪物血条窗口(证据 E-106);图包里没有对应的图时为 null。</summary>
        public MonsterHpWindow MonsterWindow => _monsterHp;

        /// <summary>用另一个图源重建目标血条窗口(没有原版图包的测试里喂合成的图);传 null,或图源里没有窗口的基本两帧,就退回占位的色块血条。</summary>
        public void UseMonsterWindowArt(ISpriteSource sprites)
        {
            _monsterHp = sprites == null ? null : MonsterHpWindow.For(sprites, _art);
        }

        /// <summary>用另一个图源重建伤害数字的位图字画法(测试里喂合成的图);传 null,或图源里没有数字图,就退回占位的文字。</summary>
        public void UseDamageNumberArt(ISpriteSource sprites)
        {
            _numberArt = DamageNumberArt.For(sprites);
        }

        /// <summary>伤害数字的位图字画法;图包里没有数字图时为 null。</summary>
        public DamageNumberArt NumberArt => _numberArt;

        /// <summary>用另一个图源重建连击图的画法(测试里喂合成的图);传 null,或图源里没有连击图,就退回占位的文字。</summary>
        public void UseComboArt(ISpriteSource sprites)
        {
            _comboArt = ComboArt.For(sprites);
        }

        /// <summary>连击图的画法;图包里没有连击图时为 null。</summary>
        public ComboArt ComboPicture => _comboArt;

        /// <summary>连击计数的状态(显示数、颜色档、显示计时器的年龄)。</summary>
        public ComboCounter ComboState => _combo;

        public int Hits { get; private set; }
        public int Kills { get; private set; }

        /// <summary>累计连击数:窗口(<see cref="ComboMs"/>)内每一击加 1,最后一击之后走满窗口清零;屏幕上从第 2 击起才显示(证据 E-108)。</summary>
        public int Combo => _combo.Count;
        public long TotalDamage { get; private set; }

        /// <summary>放出来的技能次数(冷却中、忙碌或没绑定的按键不算)。</summary>
        public int SkillCasts { get; private set; }

        public string MapPath => _room.MapPath;
        public TownHero Hero => _room.Hero;
        public TownCamera Camera => _room.Camera;
        public int LayerCount => _room.LayerCount;
        public int NpcCount => _monsters.Count;

        /// <summary>没打死、还站着的怪物个数。</summary>
        public int AliveCount
        {
            get
            {
                int n = 0;
                foreach (MonsterActor m in _monsters)
                {
                    if (m.IsAlive)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>A、S、D 三个技能快捷槽与冷却(证据 E-076);构造时没给就是空的(按技能键没有反应)。</summary>
        public HeroSkills Skills { get; }

        /// <summary>
        /// 主角手上这件武器的命中停顿毫秒数(角色 .chr [weapon hit info] 里按装备 [sub type] 取的那一项,证据 E-077)。
        /// 攻击信息的 [hit info] 整数是 -1 或没写时用它;0 表示没有这张表(那就不停顿)。
        /// </summary>
        public int WeaponStunMs { get; }

        /// <summary>
        /// 主角手上这件武器的命中延迟倍率(同一项 [weapon hit info] 里的浮点,证据 E-077、E-080):受击一侧的硬直要乘它。
        /// 攻击信息的 [hit info] 浮点项是 -1 或没写时用它;-1 表示没有这张表。
        /// </summary>
        public float WeaponDelayRate { get; }

        /// <summary>
        /// 受击硬直的三张表(取自 Etc/EtcParameter.etc,证据 E-080);null 表示没有,怪物挨打退回占位硬直。
        /// </summary>
        public HitStunTable StunTable { get; }

        /// <summary>主角还要定住多少毫秒(命中停顿,证据 E-077);0 表示没在停顿。</summary>
        public double HeroPauseMs => _heroPauseMs;

        /// <summary>
        /// 主角的生命与魔法(占位,<see cref="HeroVitals"/>)。为 null 时主角不会受伤、怪物只按固定距离出招、没有血条(旧的行为)。
        /// </summary>
        public HeroVitals Vitals { get; }

        /// <summary>主角被怪物打中的次数(不含被挡住的)。</summary>
        public int HeroHits { get; private set; }

        /// <summary>主角累计挨的伤害。</summary>
        public long HeroDamageTaken { get; private set; }

        /// <summary>主角已经被打死(血量归零)。死后不再接受输入,怪物待机。</summary>
        public bool HeroDead => Vitals != null && !Vitals.IsAlive;

        /// <summary>
        /// 主角的倒地与起身(证据 E-079、E-082、E-085、E-105):倒地类的攻击、空中与倒地中被打、已死的一击,都会让主角飞起来、落地颠一下、躺着、起身;
        /// 躺着时累计伤害超过最大血量的 15% 就提前起身。角色脚本没有 [down motion] 时一律走普通受击。
        /// </summary>
        public HeroKnockdown HeroDown => _heroDown;

        /// <summary>伤害按原版公式算(读到了 Etc/EtcParameter.etc 的公式常量);否则用占位的"攻击力 × 加成"。</summary>
        public bool HasDamageConstants => _damageConstants != null;

        /// <summary>主角的攻击面板(证据 E-110);为 null 表示主角打怪物用占位伤害。</summary>
        public HeroPanel Panel => _heroPanel;

        /// <summary>主角每一段普攻的攻击信息(与 .chr 的 [attack info] 同下标)。</summary>
        public IReadOnlyList<HeroStrikeInfo> Strikes => _strikes;

        /// <summary>奔跑攻击的攻击信息。</summary>
        public HeroStrikeInfo DashStrike => _dashStrike;

        /// <summary>空中攻击的攻击信息。</summary>
        public HeroStrikeInfo JumpStrike => _jumpStrike;

        /// <summary>主角打怪物的伤害按原版公式算(有攻击面板,又读到了公式常量);否则用占位伤害。</summary>
        public bool UsesFormulaDamage => _attacker != null && _damageConstants != null;

        /// <summary>主角在怪物上留下的"攻击者"记号(怪物 <see cref="MonsterActor.LastAttackerId"/> 记的就是它;额外命中判断用,证据 E-110)。</summary>
        public const int HeroAttackerId = 1;

        /// <summary>
        /// 声音提示队列(证据 E-089、E-090):进房间时的地图声音与怪物出场声、挥剑与语音、命中、怪物受击与死亡、主角受击与死亡、没魔法与冷却中的语音、跳跃与技能音效。
        /// 播放的一方每帧调 <see cref="DrainSounds"/> 取走;没人取时只留最近一批。
        /// </summary>
        public SoundCueQueue Sounds => _sounds;

        public void DrainSounds(List<SoundCue> into)
        {
            _sounds.Drain(into);
        }

        /// <summary>主角手上这件武器的挥动与命中声音标签(角色 .chr 的 [weapon wav] 里按装备 [sub type] 取的那一块,证据 E-089);取不到是空值。</summary>
        public WeaponSound HeroWeaponSound => _weaponSound;

        public RoomCombatSession(DnfAssets assets, RoomSession room, IReadOnlyList<MonsterActor> monsters, IReadOnlyList<HeroStrikeInfo> strikes, int seed = 1,
            HeroStrikeInfo dashStrike = default, HeroStrikeInfo jumpStrike = default, HeroSkills skills = null, int weaponStunMs = 0,
            float weaponDelayRate = -1f, HitStunTable stunTable = null, HeroVitals vitals = null, DamageConstants damageConstants = null,
            WeaponSound weaponSound = default, HeroPanel heroPanel = null)
        {
            _weaponSound = weaponSound;
            _heroPanel = heroPanel;
            _attacker = heroPanel?.ToAttacker();
            Skills = skills ?? new HeroSkills(new HeroSkillSlot[0]);
            _cueCooling = new bool[Skills.Slots.Count];
            WeaponStunMs = weaponStunMs;
            WeaponDelayRate = weaponDelayRate;
            StunTable = stunTable;
            Vitals = vitals;
            _damageConstants = damageConstants;
            _monsterRandom = new Random(unchecked(seed * 131 + 29));
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _room = room ?? throw new ArgumentNullException(nameof(room));
            _monsters = new List<MonsterActor>(monsters ?? new List<MonsterActor>());
            _strikes = new HeroStrikeInfo[strikes == null ? 0 : strikes.Count];
            for (int i = 0; i < _strikes.Length; i++)
            {
                _strikes[i] = strikes[i];
            }
            _dashStrike = dashStrike;
            _jumpStrike = jumpStrike;
            _boxes = new HeroStrikeBoxes(assets);
            _hud = HudArt.For(assets);
            _monsterHp = MonsterHpWindow.For(assets, _art);
            _numberArt = DamageNumberArt.For(assets);
            _comboArt = ComboArt.For(assets);
            _random = new Random(seed);
            _physicsRandom = new Random(unchecked(seed * 31 + 17));
            _numberRandom = new Random(unchecked(seed * 53 + 41));
            _heroDown = new HeroKnockdown(_room.Hero);
            _voiceSeed = unchecked((uint)seed * 2246822519u + 3266489917u);
            SyncStance();
            _cueMotion = _room.Hero.Motion;
            _cueSwing = _room.Hero.SwingNumber;
            EmitRoomEntryCues();
        }

        // ------------------------------------------------------------------ 声音提示(证据 E-089、E-090)

        /// <summary>进房间:先发一个房间标记,再是地图 [sound] 里的每一项(背景音乐、环境声……),再加每只怪物的出场声。</summary>
        private void EmitRoomEntryCues()
        {
            ReplayRoomSounds();
            foreach (MonsterActor m in _monsters)
            {
                _sounds.Add(m.Sounds.Appear, SoundCueKind.Voice, m.Index + 1);
            }
        }

        /// <summary>
        /// 再发一遍这个房间的地图声音(房间标记加 [sound] 每一项,不含怪物出场声):副本里走回一个去过的房间时用,
        /// 播放的一方据此换成这个房间的背景音乐与环境声(证据 E-089;换房间时沿用相同标签、停掉别的是推断)。
        /// </summary>
        public void ReplayRoomSounds()
        {
            if (_room.Map == null)
            {
                return;
            }
            _sounds.Add(_room.Map.MapPath, SoundCueKind.RoomEnter);
            foreach (string id in _room.Map.Sounds)
            {
                _sounds.Add(id, SoundCueKind.RoomSound);
            }
        }

        /// <summary>
        /// 主角的动作切换:离开地面起跳放起跳声(后跳放后跳语音,其余放跳跃语音)、回到地面放落地声;新一次挥击开始放挥击声与语音。
        /// 状态切换的那一步发,不等动画走到哪一帧(客户端是按状态内部的阶段放的,阶段与我们的切换点是否一致没有核对)。
        /// </summary>
        private void EmitHeroCues()
        {
            TownHero hero = _room.Hero;
            HeroMotion motion = hero.Motion;
            if (motion != _cueMotion)
            {
                bool wasAir = _cueMotion == HeroMotion.Jump || _cueMotion == HeroMotion.JumpAttack;
                bool isAir = motion == HeroMotion.Jump || motion == HeroMotion.JumpAttack;
                if (!wasAir && motion == HeroMotion.Jump)
                {
                    _sounds.Add(SwordmanVoice.JumpStart, SoundCueKind.Effect);
                    _sounds.Add(hero.IsBackStep ? SwordmanVoice.Backstep : SwordmanVoice.Jump, SoundCueKind.Voice);
                }
                else if (wasAir && !isAir && motion != HeroMotion.Hurt && motion != HeroMotion.Down)
                {
                    _sounds.Add(SwordmanVoice.JumpEnd, SoundCueKind.Effect);
                }
                _cueMotion = motion;
            }
            if (hero.SwingNumber != _cueSwing)
            {
                _cueSwing = hero.SwingNumber;
                if (hero.IsAttacking)
                {
                    EmitSwingCues(hero);
                }
            }
        }

        /// <summary>
        /// 一次挥击开始:语音按攻击种类(普攻 R_SM_ATK、奔跑攻击 SM_THRUST、跳跃攻击 R_SM_JUMP_ATK),再加武器的挥动声(.chr [weapon wav]:
        /// 连招最后一段用 B、其余用 A,是推断)。技能的声音在放技能时另外发(<see cref="EmitSkillCues"/>)。
        /// </summary>
        private void EmitSwingCues(TownHero hero)
        {
            switch (hero.AttackKind)
            {
                case HeroAttackKind.Normal:
                    _sounds.Add(SwordmanVoice.Attack, SoundCueKind.Voice);
                    _sounds.Add(IsFinalCombo(hero) ? _weaponSound.SwingB : _weaponSound.SwingA, SoundCueKind.Effect);
                    break;
                case HeroAttackKind.Dash:
                    _sounds.Add(SwordmanVoice.Thrust, SoundCueKind.Voice);
                    _sounds.Add(_weaponSound.SwingA, SoundCueKind.Effect);
                    break;
                case HeroAttackKind.Jump:
                    _sounds.Add(SwordmanVoice.JumpAttack, SoundCueKind.Voice);
                    _sounds.Add(_weaponSound.SwingA, SoundCueKind.Effect);
                    break;
            }
        }

        private static bool IsFinalCombo(TownHero hero)
        {
            return hero.AttackCount > 0 && hero.AttackStage >= hero.AttackCount - 1;
        }

        /// <summary>
        /// 按了技能键:放出来的发各技能自己的声音(上挑 UPPER_SLASH_02;后跳的语音在起跳切换时发;鬼斩没有读到专门的声音);
        /// 没放出来时,魔法不够放"没魔法"语音,冷却中放"冷却中"语音(两个语音在 12 项语音表里有位置,触发的位置是按名字推断的)。
        /// </summary>
        private void EmitSkillCues(HeroSkillSlot cast)
        {
            if (cast != null)
            {
                if (cast.SkillId == SkillIds.UpperSlash)
                {
                    _sounds.Add(SwordmanVoice.UpperSlash, SoundCueKind.Effect);
                }
                return;
            }
            switch (Skills.LastCastResult)
            {
                case SkillCastResult.NoMp:
                    _sounds.Add(SwordmanVoice.NoMana, SoundCueKind.Voice);
                    break;
                case SkillCastResult.Cooldown:
                    _sounds.Add(SwordmanVoice.CoolTime, SoundCueKind.Voice);
                    break;
            }
        }

        /// <summary>
        /// 怪物开始新的一次出招:按 <see cref="AttackVoicePercent"/>(15%,证据 E-104)掷骰,中了放它的 [attack sound]。
        /// 客户端是进入出招状态时掷一次"随机数 % 100 &lt; 15"再调语音;语音类别向量由哪个 .mob 标签填充没读到,这里按 [attack sound] 推断。
        /// </summary>
        private void EmitMonsterCues()
        {
            foreach (MonsterActor m in _monsters)
            {
                if (m.Sounds.Attack == null)
                {
                    continue;
                }
                _cueMonsterSwing.TryGetValue(m, out long seen);
                if (m.SwingNumber != seen)
                {
                    _cueMonsterSwing[m] = m.SwingNumber;
                    if (MonsterAttackRules.RollAttackVoice(ClientRandom.Next(ref _voiceSeed), AttackVoicePercent))
                    {
                        _sounds.Add(m.Sounds.Attack, SoundCueKind.Voice, m.Index + 1);
                    }
                }
            }
        }

        /// <summary>技能冷却走完的那一刻放"冷却完成"的界面音(客户端有这个声音标签,放的条件是推断)。</summary>
        private void EmitCooldownCues()
        {
            IReadOnlyList<HeroSkillSlot> slots = Skills.Slots;
            for (int i = 0; i < slots.Count && i < _cueCooling.Length; i++)
            {
                bool cooling = !slots[i].Ready;
                if (_cueCooling[i] && !cooling)
                {
                    _sounds.Add(CooldownEndSound, SoundCueKind.Ui);
                }
                _cueCooling[i] = cooling;
            }
        }

        /// <summary>技能冷却完成的界面音标签(客户端的字符串表里有它,在技能栏的更新函数末尾被引用;触发条件没有读到)。</summary>
        public const string CooldownEndSound = "COOLTIME_SKILL_END";

        /// <summary>主角打中怪物:命中声(攻击信息的 [hit wav],没写取武器的命中声,连招最后一段用 B,其余 A;推断)与怪物的受击或死亡叫声。</summary>
        private void EmitHitCues(TownHero hero, HeroStrikeInfo info, MonsterActor monster, bool killed)
        {
            string hit = info.HitWav;
            if (hit == null)
            {
                hit = hero.AttackKind == HeroAttackKind.Normal && IsFinalCombo(hero) ? _weaponSound.HitB : _weaponSound.HitA;
            }
            _sounds.Add(hit, SoundCueKind.Effect);
            _sounds.Add(killed ? monster.Sounds.Die : monster.Sounds.Damage, SoundCueKind.Voice, monster.Index + 1);
        }

        /// <summary>
        /// 房间里还有活着的怪物就站战斗待机动作,杀光之后站普通待机(证据 E-070,基线整局观察:有怪时主角站的是 [waiting motion],清完怪之后是 [rest motion])。
        /// 换姿态发生在最后一只怪物被打死的那一刻(死亡动画开始时);原版是在那一刻还是在怪物消失(0.5 秒后)切换没有量过。
        /// </summary>
        private void SyncStance()
        {
            _room.Hero.BattleStance = AliveCount > 0;
        }

        /// <summary>
        /// 为房间造出怪物与普攻数据:怪物按地图 [monster] 在出生点摆好(朝左,与原版进图画面一致,证据 E-069),
        /// 等级取 .mob 的 [level] 第一个数,生命值由 <see cref="MonsterAbility.Compute"/> 算(首领按首领种类);
        /// 普攻数据读 swordman 的 .chr [attack info]。缺图或缺脚本的怪物跳过。
        /// <paramref name="heroVitals"/> 为假时主角没有生命值(<see cref="Vitals"/> 为 null):怪物照样走近、出招,但打不到主角,没有血条(自动化检查用)。
        /// </summary>
        public static RoomCombatSession Create(DnfAssets assets, RoomSession room, int seed = 1, bool heroVitals = true)
        {
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            var strikes = new List<HeroStrikeInfo>();
            CharacterDef chr = data.GetCharacter(0);
            for (int i = 0; chr != null && i < room.Hero.AttackCount; i++)
            {
                strikes.Add(HeroStrikeInfo.Read(data, chr.AttackInfoPath(i)));
            }
            // 奔跑攻击与空中攻击的攻击信息:.chr 的 [dashattack info]、[jumpattack info](证据 E-072)
            HeroStrikeInfo dash = HeroStrikeInfo.Read(data, chr?.MotionPath("[dashattack info]"));
            HeroStrikeInfo jump = HeroStrikeInfo.Read(data, chr?.MotionPath("[jumpattack info]"));

            MonsterCatalog catalog = MonsterCatalog.Read(assets);
            var models = new Dictionary<int, MonsterModel>();
            var actors = new List<MonsterActor>();
            int index = 0;
            // 主角的碰撞宽度与进深(.chr 的 [width]):怪物出招的射程判断把目标的半边按它们的四分之一放宽(证据 E-101)
            int[] heroWidth = chr?.Tags.Ints("[width]");
            foreach (RoomMonsterSpawn spawn in room.Map.Monsters)
            {
                if (!models.TryGetValue(spawn.Code, out MonsterModel model))
                {
                    string mobPath = catalog.PathOf(spawn.Code);
                    model = mobPath == null ? null : MonsterModel.Read(assets, mobPath);
                    models[spawn.Code] = model;
                }
                MonsterDef def = data.GetMonster(spawn.Code);
                if (model == null || def == null || model.Waiting == null)
                {
                    continue;
                }
                int level = def.Tags.IntOr("[level]", 1);
                MonsterKind kind = spawn.IsBoss ? MonsterKind.Boss : MonsterKind.Normal;
                MonsterStats stats = MonsterAbility.Compute(def, data.MonsterBaseTables, data.GetMonsterOwnTable(def), level, 0, kind);
                var actor = new MonsterActor(index++, spawn, model, level, stats.HpMax, WalkSpeed(room.Hero, stats.MoveSpeed > 0 ? stats.MoveSpeed : def.Tags.IntOr("[move speed]", 0)),
                    def.Tags.IntOr("[attack delay]", 0), def.Tags.IntOr("[attack speed]", 0), stats.HitRecovery, stats.Weight);
                // 出招的攻击信息:[attack info] 与 [attack motion] 同下标(证据 E-086);出哪一招由 [attack kind] 的记录决定(证据 E-100、E-101),
                // 没有记录的怪物仍固定用第 0 个动作
                var attackStrikes = new HeroStrikeInfo[model.AttackInfoPaths.Count];
                for (int a = 0; a < attackStrikes.Length; a++)
                {
                    attackStrikes[a] = !string.IsNullOrEmpty(model.AttackInfoPaths[a])
                        ? HeroStrikeInfo.Read(data, model.AttackInfoPaths[a])
                        : new HeroStrikeInfo(0, 0, false);
                }
                actor.Strike = attackStrikes.Length > 0 ? attackStrikes[0] : new HeroStrikeInfo(0, 0, false);
                actor.SetStrikes(attackStrikes);
                actor.SetAttackKinds(AttackKindRecord.Parse(def.Tags), unchecked((uint)seed * 2654435761u + (uint)actor.Index * 40503u + 12345u));
                actor.SetThrows(MonsterThrows.Load(assets, data, def.Path, ThrowAttackRecord.Parse(def.Tags)));   // [throw attack]:出招动画到发射帧时扔东西(证据 E-103)
                if (heroWidth != null && heroWidth.Length >= 2)
                {
                    actor.TargetWidth = heroWidth[0];
                    actor.TargetDepth = heroWidth[1];
                }
                actor.PhysicalAttack = stats.PhysicalAttack;
                actor.MagicalAttack = stats.MagicalAttack;
                actor.Defense = DefenderPanel.FromMonster(stats, level);                    // 挨打时的防御面板(证据 E-110)
                actor.Panel = MonsterPanelInfo.From(def, kind, level, stats.HpMax, data);   // 目标血条窗口的资料(证据 E-106)
                actor.Sounds = new MonsterSounds(def.AppearSound, def.DamageSound, def.DieSound, def.AttackSound, def.AmbientSound);
                actors.Add(actor);
            }
            // 受击硬直的三张表:Etc/EtcParameter.etc(证据 E-080);读不到就退回占位硬直
            ScriptLine[] etc = data.ReadScript(HitStunTablePath);
            HitStunTable stunTable = etc == null ? null : HitStunTable.FromEtcParameter(new TagTable(etc));
            bool hasWeapon = TryWeaponHit(chr, room.Hero, out WeaponHitInfo weapon);
            TryWeaponSound(chr, room.Hero, out WeaponSound weaponSound);
            // 主角的生命值:没有就是旧行为(怪物只按固定距离出招、打不到主角、没有血条)——自动化检查里需要"不会被打"的主角时用
            HeroVitals vitals = heroVitals && chr != null ? HeroVitals.FromChr(chr) : null;
            // 主角的攻击面板(证据 E-110):与公式常量、生命值一起给;没有生命值(--no-damage)时仍用占位伤害,按时刻对拍的回归检查靠它保持原来的时刻
            HeroPanel heroPanel = vitals != null ? HeroPanel.FromChr(chr, vitals.Level, HeroPanel.WornEquipment(assets, room.Hero)) : null;
            return new RoomCombatSession(assets, room, actors, strikes, seed, dash, jump, HeroSkills.Create(data, chr, 0), hasWeapon ? weapon.StunMs : 0,
                hasWeapon ? weapon.DelayRate : -1f, stunTable, vitals, vitals != null ? MonsterDamage.LoadConstants(assets) : null, weaponSound, heroPanel);
        }

        /// <summary>主角穿的武器在 .chr 的 [weapon wav] 里那一块(挥动与命中声音标签);取不到返回 false。</summary>
        public static bool TryWeaponSound(CharacterDef chr, TownHero hero, out WeaponSound sound)
        {
            sound = default;
            if (chr == null || hero?.Outfit == null)
            {
                return false;
            }
            foreach (AvatarItem item in hero.Outfit.Items)
            {
                if (string.Equals(item.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase) && chr.TryGetWeaponSound(item.SubType, out sound))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>受击硬直的三张表所在的脚本(证据 E-080)。</summary>
        public const string HitStunTablePath = "Etc/EtcParameter.etc";

        /// <summary>主角穿的武器([equipment type] 为 [weapon] 的那件)的 [sub type] 在 .chr 的 [weapon hit info] 里对应的命中停顿;取不到是 0。</summary>
        public static int WeaponStunOf(CharacterDef chr, TownHero hero)
        {
            return TryWeaponHit(chr, hero, out WeaponHitInfo info) ? info.StunMs : 0;
        }

        /// <summary>主角穿的武器在 .chr 的 [weapon hit info] 里那一项(命中停顿与命中延迟倍率);取不到返回 false。</summary>
        public static bool TryWeaponHit(CharacterDef chr, TownHero hero, out WeaponHitInfo info)
        {
            info = default;
            if (chr == null || hero?.Outfit == null)
            {
                return false;
            }
            foreach (AvatarItem item in hero.Outfit.Items)
            {
                if (string.Equals(item.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase) && chr.TryGetWeaponHitInfo(item.SubType, out info))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 怪物的水平行走速度(像素/秒):主角实测的 <see cref="TownHero.WalkSpeedX"/> 乘以怪物与主角的 [move speed] 之比(证据 E-066、E-069)。
        /// 把怪物的数当成与主角同一个单位是假设;没有数据时取与主角同速。
        /// </summary>
        public static double WalkSpeed(TownHero hero, int monsterMoveSpeed)
        {
            if (hero.MoveSpeedStat <= 0 || monsterMoveSpeed <= 0)
            {
                return hero.WalkSpeedX;
            }
            return hero.WalkSpeedX * monsterMoveSpeed / hero.MoveSpeedStat;
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false, false);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, false);
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。长帧(卡顿)会被切成不超过 <see cref="MaxStepMs"/> 的小步,
        /// 每一小步都推进主角、怪物并判定命中,这样挥击的最后一帧不会因为一帧过长而被跳过、怪物的追击与击退也与帧率无关。
        /// 攻击键与跳跃键是"刚按下"的边沿,只在第一小步里生效。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, jumpPressed, SkillKeys.None);
        }

        /// <summary>
        /// 同上,多一组技能键(A、S、D 的"刚按下"边沿,同样只在第一小步里生效):这一小步里技能放出来了,同一小步的攻击键与跳跃键就作废
        /// (一次按键只做一件事)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            if (deltaMs <= 0)
            {
                Step(0, dirX, dirY, attackPressed, jumpPressed, skillsPressed);
                return;
            }
            double remaining = deltaMs;
            bool first = true;
            while (remaining > 0)
            {
                double step = remaining < MaxStepMs ? remaining : MaxStepMs;
                Step(step, dirX, dirY, first && attackPressed, first && jumpPressed, first ? skillsPressed : SkillKeys.None);
                first = false;
                remaining -= step;
            }
        }

        private void Step(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            Skills.Advance(deltaMs);
            Vitals?.RegenMp(deltaMs);   // 魔法值按墙钟恢复(证据 E-087);死了不恢复在 RegenMp 里判断

            if (HeroDead)
            {
                // 主角死了:不再接受任何输入(占位:没有复活与结算)
                dirX = 0;
                dirY = 0;
                attackPressed = false;
                jumpPressed = false;
                skillsPressed = SkillKeys.None;
                _bufferedAttack = false;
                _bufferedJump = false;
                _bufferedSkills = SkillKeys.None;
            }

            // 命中停顿(证据 E-077):主角被定住时,他自己、他的技能特效的时间都不走;冷却、伤害数字、连击计时是墙钟,照常走。
            // 这一小步里停顿结束的话,剩下的时间照常推进。定住期间按下的键存起来,停顿一结束就生效(假设,原版的输入缓冲没读)。
            double heroDelta = deltaMs;
            bool heroFrozen = false;
            if (_heroPauseMs > 0)
            {
                double used = Math.Min(_heroPauseMs, deltaMs);
                _heroPauseMs -= used;
                heroDelta = deltaMs - used;
                heroFrozen = heroDelta <= 0;
                // 受击硬直是不被停顿冻结的秒表(与怪物一样,证据 E-080)
                _room.Hero.AddHurtClock(used);
            }
            if (heroFrozen)
            {
                _bufferedAttack |= attackPressed;
                _bufferedJump |= jumpPressed;
                _bufferedSkills |= skillsPressed;
            }
            else
            {
                attackPressed |= _bufferedAttack;
                jumpPressed |= _bufferedJump;
                skillsPressed |= _bufferedSkills;
                _bufferedAttack = false;
                _bufferedJump = false;
                _bufferedSkills = SkillKeys.None;
                _heroDown.Step(heroDelta);     // 倒地在命中停顿里一起冻结(与怪物的倒地一样);先走倒地,摄像机在后面的 Update 里跟着新位置
                HeroSkillSlot cast = skillsPressed == SkillKeys.None ? null : Skills.TryCast(skillsPressed, _room.Hero, Vitals);
                if (skillsPressed != SkillKeys.None)
                {
                    EmitSkillCues(cast);
                }
                if (cast != null)
                {
                    attackPressed = false;
                    jumpPressed = false;
                    SkillCasts++;
                    SpawnEffects(cast);
                }
                _room.Update(heroDelta, dirX, dirY, attackPressed, jumpPressed);
            }
            EmitHeroCues();
            TownHero hero = _room.Hero;
            // 有主角的生命值时,怪物按"这一击能不能打到主角的受击框"决定靠近与出招,主角死了就待机;没有时按固定距离(旧行为)
            bool heroAlive = !HeroDead;
            List<Aabb> heroBoxes = Vitals == null ? null : HeroHurtBoxes.At(hero);
            foreach (MonsterProjectile p in _projectiles)
            {
                p.Update(deltaMs);
            }
            foreach (MonsterActor m in _monsters)
            {
                m.Update(deltaMs, hero.X, hero.Y, heroAlive, hero.Walk, heroBoxes);
            }
            CollectThrown();
            EmitMonsterCues();
            ResolveHits();
            ResolveMonsterHits();
            ResolveProjectileHits();
            _projectiles.RemoveAll(p => p.Finished);
            SyncStance();
            EmitCooldownCues();
            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                _popups[i].AgeMs += deltaMs;
                if (_popups[i].AgeMs >= _popups[i].LifeMs)
                {
                    _popups.RemoveAt(i);
                }
            }
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                _effects[i].Advance(heroDelta);
                if (_effects[i].Finished)
                {
                    _effects.RemoveAt(i);
                }
            }
            _targetMs += deltaMs;
            _hudClockMs += deltaMs;
            _monsterHp?.Update(_hudClockMs);
            _combo.Update(_hudClockMs);
            if (_targetMs >= TargetBarMs)
            {
                Target = null;
            }
        }

        /// <summary>技能放出来的那一刻,在主角前方 <see cref="HeroSkillSlot.EffectForwardX"/> 像素、同一条地面线上放这个技能的特效动画(朝向跟主角)。画在主角前面一层。</summary>
        private void SpawnEffects(HeroSkillSlot slot)
        {
            TownHero hero = _room.Hero;
            double x = hero.X + (hero.FacingLeft ? -slot.EffectForwardX : slot.EffectForwardX);
            foreach (string path in slot.EffectPaths)
            {
                AnimatedEffect effect = AnimatedEffect.Create(_assets, path, x, hero.Y, 0.0, hero.FacingLeft);
                if (effect != null)
                {
                    _effects.Add(effect);
                }
            }
        }

        private void ResolveHits()
        {
            TownHero hero = _room.Hero;
            if (!hero.IsAttacking)
            {
                _evalSwing = -1;
                return;
            }
            if (hero.SwingNumber != _evalSwing)
            {
                _evalSwing = hero.SwingNumber;
                _evalFrame = -1;
                _hitThisSwing.Clear();
            }
            int frame = hero.CurrentFrame;
            for (int f = _evalFrame + 1; f <= frame; f++)
            {
                EvaluateFrame(hero, f);
            }
            if (frame > _evalFrame)
            {
                _evalFrame = frame;
            }
        }

        private void EvaluateFrame(TownHero hero, int frame)
        {
            List<Aabb> strike = _boxes.At(hero, frame);
            if (strike.Count == 0)
            {
                return;
            }
            foreach (MonsterActor m in _monsters)
            {
                if (!m.IsAlive || _hitThisSwing.Contains(m))
                {
                    continue;
                }
                if (TryHit(strike, m.DamageBoxes(), out Aabb hit))
                {
                    _hitThisSwing.Add(m);
                    Strike(hero, m, hit);
                }
            }
        }

        /// <summary>
        /// 攻击框与受击框的相交判定:外层攻击框、内层受击框,第一对相交的就是命中,返回它们的相交盒(客户端的"命中信息",证据 E-058);
        /// 伤害数字按它的中心放(证据 E-107)。
        /// </summary>
        private static bool TryHit(List<Aabb> strike, List<Aabb> hurt, out Aabb hit)
        {
            foreach (Aabb a in strike)
            {
                foreach (Aabb b in hurt)
                {
                    if (a.Overlaps(b))
                    {
                        hit = Aabb.Intersection(a, b);
                        return true;
                    }
                }
            }
            hit = default;
            return false;
        }

        /// <summary>
        /// 放一个伤害数字:位置按命中点算(x = 相交盒横向中心 + [-10, 9] 的抖动,y = 受击者的 y,z = 相交盒高度中心 + 60 + [0, 19] 的抖动,证据 E-107)。
        /// 抖动是客户端的"随机数 % 20":x 减 10,z 加在 60 上。
        /// </summary>
        private void AddNumber(int value, int flags, Aabb hit, double victimY)
        {
            int jitterX = _numberRandom.Next(20) - 10;
            int jitterZ = _numberRandom.Next(20);
            _popups.Add(new HitPopup(value, flags, hit.CenterX + jitterX, victimY, hit.CenterZ + NumberRise + jitterZ));
        }

        private void Strike(TownHero hero, MonsterActor monster, Aabb hit)
        {
            HeroStrikeInfo info;
            switch (hero.AttackKind)
            {
                case HeroAttackKind.Dash:
                    info = _dashStrike;
                    break;
                case HeroAttackKind.Jump:
                    info = _jumpStrike;
                    break;
                case HeroAttackKind.Skill:
                    // 有自己攻击信息与伤害数的技能(鬼斩)用自己的;借普攻动作放的技能(上挑)沿用那一段普攻的攻击信息
                    HeroSkillSlot slot = Skills.SlotOfSkill(hero.SkillId);
                    if (slot != null && slot.Strike.HasValue)
                    {
                        info = slot.Strike.Value;
                        break;
                    }
                    goto default;
                default:
                    info = hero.AttackStage < _strikes.Length ? _strikes[hero.AttackStage] : new HeroStrikeInfo(0, 0, false);
                    break;
            }
            // 伤害:有攻击面板与公式常量就走原版公式(证据 E-110),打完在怪物身上记下攻击者(下一击不再算额外命中);否则是占位伤害
            int damage;
            bool critical = false;
            if (UsesFormulaDamage)
            {
                damage = RollFormulaDamage(info, monster, unchecked((uint)_random.Next()), out critical);
                monster.LastAttackerId = HeroAttackerId;
            }
            else
            {
                damage = info.IsSkillDamage
                    ? PlaceholderDamage.RollSkill(info.SkillRatePercent, info.SkillFixed, _random)
                    : PlaceholderDamage.Roll(info.DamageBonus, _random);
            }
            // 受击硬直与击退按客户端的公式算(证据 E-080);没有硬直表时 StunInput 无效,怪物走占位值。
            // 倒地(以及空中、倒地中又挨打)按客户端的开始处理算:体重缩放的击飞、z 抛物线、落地后颠一下再躺下(证据 E-082、E-083)
            _monsterHp?.SetTarget(monster, _hudClockMs);    // 先选中再结算:血条先取这一击之前的生命,这一击才会掉出一段闪光(证据 E-106)
            bool killed = monster.ApplyHit(damage, info.KnockDown, info.PushAside, hero.FacingLeft, StunInput(info), DownInput(info));
            EmitHitCues(hero, info, monster, killed);
            int stun = HitStop.StunMs(info.HitValue, info.UseWeaponHitDelayRate, WeaponStunMs);
            if (stun > 0)
            {
                // 攻击方与受击方定住同样久,已经在停顿的取剩余更长的那个(E-077)
                monster.Pause(stun);
                if (stun > _heroPauseMs)
                {
                    _heroPauseMs = stun;
                }
            }
            Hits++;
            _combo.Hit(_hudClockMs);
            TotalDamage += damage;
            Target = monster;
            _targetMs = 0;
            if (killed)
            {
                Kills++;
            }
            AddNumber(damage, DamageNumberFlags.AttackerIsHero | (critical ? DamageNumberFlags.Critical : 0), hit, monster.Y);
        }

        /// <summary>
        /// 主角这一击打这只怪物的原版公式伤害(证据 E-110,至少 1):输入按 <see cref="HeroStrikeInfo.Input"/>、主角的攻击面板(<see cref="Panel"/>)、
        /// 怪物的防御面板(<see cref="MonsterActor.Defense"/>,没给就按零防御零抗性)填进伤害结构;额外命中按怪物记下的上一个攻击者
        /// (<see cref="MonsterActor.LastAttackerId"/>)判断。<paramref name="seed"/> 是这一击的随机种子,<paramref name="critical"/> 返回是否暴击。
        /// 不改怪物的状态:记攻击者在命中结算(<see cref="Strike"/>)里做。没有攻击面板或公式常量时抛 <see cref="InvalidOperationException"/>。
        /// </summary>
        public int RollFormulaDamage(HeroStrikeInfo info, MonsterActor monster, uint seed, out bool critical)
        {
            if (!UsesFormulaDamage)
            {
                throw new InvalidOperationException("没有攻击面板或伤害公式常量");
            }
            if (monster == null)
            {
                throw new ArgumentNullException(nameof(monster));
            }
            DefenderPanel defender = monster.Defense ?? new DefenderPanel { Level = monster.Level };
            bool bonusHit = HitDamage.BonusHitApplies(defender.Level, monster.LastAttackerId == HeroAttackerId);
            return HitDamage.Roll(info.Input, _attacker, defender, bonusHit, _damageConstants, seed, out critical);
        }

        // ------------------------------------------------------------------ 怪物打主角(P5.M5,证据 E-086)

        /// <summary>
        /// 怪物出招时,出招动画每一帧的攻击框与主角此刻的受击框相交判定(与主角打怪物同一套相交盒规则);
        /// 同一次出招只打中一次,按"上次判到哪一帧"补判中间跳过的帧。
        /// </summary>
        private void ResolveMonsterHits()
        {
            if (Vitals == null || !Vitals.IsAlive)
            {
                return;
            }
            TownHero hero = _room.Hero;
            foreach (MonsterActor m in _monsters)
            {
                if (!m.IsAlive || m.Phase != MonsterPhase.Attack)
                {
                    continue;
                }
                int frame = m.CurrentFrame;
                for (int f = m.AttackEvalFrame + 1; f <= frame && !m.HitHeroThisSwing; f++)
                {
                    List<Aabb> strike = m.AttackBoxesAt(f);
                    if (strike.Count > 0 && TryHit(strike, HeroHurtBoxes.At(hero), out Aabb hit))
                    {
                        m.HitHeroThisSwing = true;
                        StrikeHero(m, hero, hit);
                    }
                }
                if (frame > m.AttackEvalFrame)
                {
                    m.AttackEvalFrame = frame;
                }
            }
        }

        /// <summary>取走怪物这一步发射出来的投射物(从此归会话管)并放发射声音(攻击信息之外的"扔"的声音标签,证据 E-103)。</summary>
        private void CollectThrown()
        {
            foreach (MonsterActor m in _monsters)
            {
                int before = _projectiles.Count;
                if (!m.TakeThrown(_projectiles, _throwSoundScratch))
                {
                    continue;
                }
                ProjectilesThrown += _projectiles.Count - before;
                foreach (string sound in _throwSoundScratch)
                {
                    _sounds.Add(sound, SoundCueKind.Effect);
                }
                _throwSoundScratch.Clear();
            }
        }

        /// <summary>
        /// 飞行中的投射物打主角:当前帧的攻击框与主角的受击框相交就打中,投射物随即消失(推断:原版有穿透与"同一对象只打一次"的规则,没有读通);
        /// 攻击信息取投射物配方里 .obj 的 [attack info],伤害按发射者的等级与攻击力算,与怪物自己的攻击走同一条受击流程。
        /// </summary>
        private void ResolveProjectileHits()
        {
            if (Vitals == null || !Vitals.IsAlive || _projectiles.Count == 0)
            {
                return;
            }
            TownHero hero = _room.Hero;
            List<Aabb> hurt = HeroHurtBoxes.At(hero);
            foreach (MonsterProjectile p in _projectiles)
            {
                if (p.Finished)
                {
                    continue;
                }
                List<Aabb> strike = p.AttackBoxes();
                if (strike.Count == 0 || !TryHit(strike, hurt, out Aabb hit))
                {
                    continue;
                }
                p.Vanish();
                ProjectileHits++;
                StrikeHero(p.Owner, p.Template.Strike, p.FacingLeft, hero, null, hit);
                if (!Vitals.IsAlive)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 怪物这一击打中了主角:伤害按 <see cref="MonsterDamage"/>(原版公式,输入是推断)、扣血、飘红字;
        /// 主角进受击硬直(硬直与击退按客户端的公式,受击方是主角,攻击方没有所属者所以攻击方恢复项为 0;命中模式 [burn] 不击退,证据 E-080),
        /// 命中停顿按攻击信息的 [hit info] 整数(怪物没有武器表,没写就是 0,不停顿,证据 E-077)。
        /// 受击反应按决策函数选(证据 E-079、E-105,见 <see cref="ReactHero"/>):倒地类的攻击、空中与倒地中被打、已死的一击让主角倒地(<see cref="HeroDown"/>),
        /// 其余走受击硬直;[damage reaction] 为 none 的只扣血;主角没有倒地动作时血量归零停在受击姿势上(占位的死亡)。
        /// </summary>
        private void StrikeHero(MonsterActor monster, TownHero hero, Aabb hit)
        {
            StrikeHero(monster, monster.Strike, monster.FacingLeft, hero, monster, hit);
        }

        /// <param name="owner">攻击的所有者(取等级与攻击力;投射物的所有者是扔它的怪物)。</param>
        /// <param name="info">这一击的攻击信息。</param>
        /// <param name="attackerFacingLeft">攻击者(怪物或投射物)朝左:主角被打退的方向。</param>
        /// <param name="attacker">命中停顿时一起定住的攻击者;投射物打中就消失,传 null。</param>
        /// <param name="hit">攻击框与主角受击框的相交盒,伤害数字按它的中心放(证据 E-107)。</param>
        private void StrikeHero(MonsterActor owner, HeroStrikeInfo info, bool attackerFacingLeft, TownHero hero, MonsterActor attacker, Aabb hit)
        {
            int attack = info.Magical ? owner.MagicalAttack : owner.PhysicalAttack;
            int damage = _damageConstants != null
                ? MonsterDamage.Roll(info, owner.Level, attack, Vitals, _damageConstants, unchecked((uint)_monsterRandom.Next()))
                : Math.Max(1, (int)Math.Round(attack * (100.0 + info.DamageBonus) / 100.0));
            bool killed = Vitals.Damage(damage);
            HeroHits++;
            HeroDamageTaken += damage;
            _heroDown.NoteDamage(damage, Vitals.HpMax);
            ReactHero(info, attackerFacingLeft, hero, killed);
            // 声音:攻击信息的命中声,加主角的受击或死亡语音(客户端有"职业 → (受击, 死亡)"的全局表,证据 E-089)
            _sounds.Add(info.HitWav, SoundCueKind.Effect);
            _sounds.Add(killed ? SwordmanVoice.Die : SwordmanVoice.Hurt, SoundCueKind.Voice);
            int pause = HitStop.StunMs(info.HitValue, info.UseWeaponHitDelayRate, 0);
            if (pause > 0)
            {
                attacker?.Pause(pause);
                if (pause > _heroPauseMs)
                {
                    _heroPauseMs = pause;
                }
            }
            AddNumber(damage, DamageNumberFlags.VictimIsHero, hit, hero.Y);
        }

        /// <summary>
        /// 主角被打中之后的受击反应(决策函数 0x00822ca0 对"没有霸体、没有异常状态"的主角,证据 E-079、E-105):
        /// 已死 → 有倒地动作就倒地(请求用这一击自己的推力与抬升;[damage reaction] 为 none 时决策函数不产生反应,由每帧函数补发推力、抬升各 400 的倒地),
        /// 没有倒地动作就停在受击姿势上(占位的死亡);没死:none → 不产生反应;倒地类的攻击(2),或普通受击(1、3)打中腾空的主角、已在倒地里的主角 →
        /// 倒地分支(有倒地动作才走,没有退到受击分支);其余走受击分支(交替放 [damage motion 1]、[damage motion 2],硬直与击退按 E-080 的公式)。
        /// 3([damage except human])按 1 处理:决策函数对"是不是人类"的那一问没有读通(推断)。
        /// </summary>
        private void ReactHero(HeroStrikeInfo info, bool attackerFacingLeft, TownHero hero, bool killed)
        {
            // 受击硬直与击退按客户端的公式算(E-080);没有硬直表时用占位值
            double stunMs = HeroHurtTuning.FallbackStunMs, knockPixels = HeroHurtTuning.FallbackKnockPixels, knockMs = HeroHurtTuning.FallbackKnockMs;
            if (StunTable != null)
            {
                float rate = HitStun.Rate(info.HitScale, -1f, info.UseWeaponHitDelayRate, info.Magical);
                HitStunResult r = HitStun.Damage(StunTable, Vitals.HitRecovery, 0, rate, info.ForceHitStunMs, info.KnockBackType, info.NoKnockBack);
                stunMs = r.StunMs;
                knockPixels = r.KnockPixels;
                knockMs = r.KnockMs;
            }
            if (killed)
            {
                if (_heroDown.Available)
                {
                    if (info.ReactionNone)
                    {
                        var fallback = new KnockdownInput(HeroKnockdown.DeadDefaultLift, false, false, false, _physicsRandom.Next(1000), _physicsRandom.Next(1000));
                        _heroDown.Begin(fallback, HeroKnockdown.DeadDefaultPush, attackerFacingLeft, true);
                    }
                    else
                    {
                        _heroDown.Begin(DownInput(info), info.PushAside, attackerFacingLeft, true);
                    }
                    return;
                }
                hero.BeginHurt(HeroHurtTuning.DeadHoldMs, knockPixels, knockMs, attackerFacingLeft);
                return;
            }
            if (info.ReactionNone)
            {
                return;
            }
            if (_heroDown.Available && (info.KnockDown || (info.ReactionDamage && (_heroDown.Active || hero.Z > 0))))
            {
                _heroDown.Begin(DownInput(info), info.PushAside, attackerFacingLeft, false);
                return;
            }
            hero.BeginHurt(stunMs, knockPixels, knockMs, attackerFacingLeft);
        }

        /// <summary>这一击的倒地输入(证据 E-083):攻击信息的抬升、弹起、不受体重,外加两个抖动用的随机数。</summary>
        private KnockdownInput DownInput(HeroStrikeInfo info)
        {
            return new KnockdownInput(info.LiftUp, info.Bounce, info.IgnoreWeight, info.ReactionDamage, _physicsRandom.Next(1000), _physicsRandom.Next(1000));
        }

        /// <summary>
        /// 这一击的受击硬直输入(证据 E-080):倍率取攻击信息的命中延迟倍率([hit info] 的浮点项,没写取武器表),按客户端的两次取整换成 rate;
        /// 没有硬直表时返回无效值,怪物走占位硬直。
        /// </summary>
        private HitStunInput StunInput(HeroStrikeInfo info)
        {
            if (StunTable == null)
            {
                return default;
            }
            float rate = HitStun.Rate(info.HitScale, WeaponDelayRate, info.UseWeaponHitDelayRate, info.Magical);
            return new HitStunInput(StunTable, rate, info.ForceHitStunMs, info.KnockBackType, info.NoKnockBack);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return Build(timeMs, null);
        }

        /// <summary>同 <see cref="Build(long)"/>,另外把 <paramref name="hud"/>(副本的小地图等外部拼好的屏幕空间精灵)叠在最上层。</summary>
        public List<SceneSprite> Build(long timeMs, IReadOnlyList<SceneSprite> hud)
        {
            TownProjection projection = _room.Projection;
            var extra = new List<SceneSprite>(_monsters.Count + 8 + (hud == null ? 0 : hud.Count));
            foreach (MonsterActor m in _monsters)
            {
                SceneSprite s = m.Sprite(_assets, projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            foreach (AnimatedEffect e in _effects)
            {
                SceneSprite s = e.Sprite(_assets, projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            foreach (MonsterProjectile p in _projectiles)
            {
                SceneSprite s = p.Sprite(_assets, projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            AddOverlay(extra, projection);
            if (hud != null)
            {
                extra.AddRange(hud);
            }
            return _room.Build(timeMs, extra);
        }

        private void AddOverlay(List<SceneSprite> into, TownProjection projection)
        {
            int order = 0;
            MonsterActor target = Target;
            if (_monsterHp != null)
            {
                _monsterHp.Build(into, ref order);
            }
            else if (target != null)
            {
                int filled = (int)Math.Round((double)(TargetBarWidth - 2) * target.Hp / target.HpMax);
                if (target.Hp > 0 && filled < 1)
                {
                    filled = 1;
                }
                into.Add(OverlayArt.Place(_art.Bar(TargetBarWidth, TargetBarHeight, filled), TargetBarX, TargetBarY, order++, "target-bar"));
                SpriteFrame label = _art.Text("Lv." + target.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "  " + target.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + target.HpMax.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    2, OverlayArt.White);
                into.Add(OverlayArt.Place(label, TargetBarX, TargetBarY - OverlayArt.TextHeight(2) - 2, order++, "target-label"));
            }
            if (Vitals != null)
            {
                if (_hud != null)
                {
                    AddHud(into, ref order);
                }
                else
                {
                    AddHeroBars(into, ref order);
                }
                if (!Vitals.IsAlive)
                {
                    SpriteFrame dead = _art.Text("DEAD", 8, HeroPopupColor);
                    into.Add(OverlayArt.Place(dead, (800 - dead.Width) / 2, 220, order++, "hero-dead"));
                }
            }
            foreach (HitPopup p in _popups)
            {
                // 原版的位图字(证据 E-107);图包里没有数字图或缺帧时退回占位文字,按同一条动画的偏移与不透明度画
                SceneSprite number = _numberArt?.Place(p, projection, order);
                if (number != null)
                {
                    into.Add(number);
                    order++;
                    continue;
                }
                if (!DamageNumberAnimation.TryPose(p.Template, p.AgeMs, out DamageNumberPose pose))
                {
                    continue;
                }
                SpriteFrame text = _art.Text(p.Text, 3, p.OnHero ? HeroPopupColor : OverlayArt.Yellow);
                int sx = (int)Math.Floor(p.WorldX) - projection.CameraX - text.Width / 2;
                int sy = (int)Math.Floor(p.WorldY) + projection.GroundY - p.Z + (int)Math.Round(pose.YOffset);
                into.Add(OverlayArt.Place(text, sx, sy, order++, "damage", pose.Alpha));
            }
            int shown = _combo.Shown;
            if (shown >= ComboCounter.ShowFrom && (_comboArt == null || !_comboArt.Build(shown, _combo.AgeMs, into, ref order)))
            {
                // 图包里没有连击图(或缺帧)时的占位文字
                SpriteFrame combo = _art.Text(shown.ToString(System.Globalization.CultureInfo.InvariantCulture) + " HIT", 4, OverlayArt.Orange);
                into.Add(OverlayArt.Place(combo, 800 - combo.Width - 24, 236, order++, "combo"));
            }
        }

        /// <summary>主角受伤飘字的颜色(红)。</summary>
        public const uint HeroPopupColor = 0xFF5050u;

        /// <summary>主角生命条与魔法条(占位的叠加层,不是原版的界面):屏幕左下角,红色与蓝色,上面各一行 "HP 当前/上限"。只在图包里没有原版底栏时用(<see cref="HudArt"/>);死后居中显示 DEAD(两种底栏都有)。</summary>
        public const int HeroBarX = 24;
        public const int HeroBarY = 540;
        public const int HeroBarWidth = 180;
        public const int HeroBarHeight = 12;

        private void AddHeroBars(List<SceneSprite> into, ref int order)
        {
            HeroVitals v = Vitals;
            int hpFill = FilledWidth(v.Hp, v.HpMax);
            into.Add(OverlayArt.Place(_art.Bar(HeroBarWidth, HeroBarHeight, hpFill), HeroBarX, HeroBarY, order++, "hero-hp"));
            SpriteFrame hpLabel = _art.Text("HP " + v.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                + v.HpMax.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, OverlayArt.White);
            into.Add(OverlayArt.Place(hpLabel, HeroBarX, HeroBarY - OverlayArt.TextHeight(2) - 2, order++, "hero-hp-label"));
            if (v.MpMax > 0)
            {
                int mpY = HeroBarY + HeroBarHeight + 14;
                into.Add(OverlayArt.Place(_art.Bar(HeroBarWidth, HeroBarHeight, FilledWidth(v.Mp, v.MpMax), OverlayArt.BlueBright, OverlayArt.BlueDark, OverlayArt.BlueBack),
                    HeroBarX, mpY, order++, "hero-mp"));
                SpriteFrame mpLabel = _art.Text("MP " + v.Mp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                    + v.MpMax.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, OverlayArt.White);
                into.Add(OverlayArt.Place(mpLabel, HeroBarX, mpY - OverlayArt.TextHeight(2) - 2, order++, "hero-mp-label"));
            }
        }

        /// <summary>
        /// 原版底栏(<see cref="HudArt"/>,证据 E-088):血球、魔法球、等级牌与 A S D 技能槽(能放亮、冷却或魔法不够时灰)。
        /// 技能槽按 <see cref="Skills"/> 的次序对应 A、S、D。
        /// </summary>
        private void AddHud(List<SceneSprite> into, ref int order)
        {
            HeroVitals v = Vitals;
            var states = new List<HudSkillState>(Skills.Slots.Count);
            for (int i = 0; i < Skills.Slots.Count; i++)
            {
                HeroSkillSlot s = Skills.Slots[i];
                bool usable = s.Ready && v.IsAlive && v.Mp >= s.ConsumeMp;
                states.Add(new HudSkillState(i, s.Def, usable));
            }
            _hud.Build(into, ref order, v.Level, v.Hp, v.HpMax, v.Mp, v.MpMax, states);
        }

        private static int FilledWidth(int value, int max)
        {
            int filled = (int)Math.Round((double)(HeroBarWidth - 2) * value / Math.Max(1, max));
            return value > 0 && filled < 1 ? 1 : filled;
        }
    }

    /// <summary>演示用的房间战斗:在 <see cref="DemoRoom"/> 的房间上摆怪物。</summary>
    public static class DemoCombat
    {
        public static RoomCombatSession CreateSession(DnfAssets assets, string mapPath, string weaponEqu = DemoHero.StarterWeapon, int seed = 1,
            bool heroVitals = true)
        {
            RoomSession room = DemoRoom.CreateSession(assets, mapPath, weaponEqu);
            return RoomCombatSession.Create(assets, room, seed, heroVitals);
        }
    }
}
