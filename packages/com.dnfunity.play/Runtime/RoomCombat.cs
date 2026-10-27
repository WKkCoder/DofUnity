using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Game.Ai;
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
    /// 经验与升级(证据 E-111):给了等级进度(<see cref="HeroProgress"/>)时,击杀按服务端的公式给经验(<see cref="KillExp"/>),升级换上新等级的生命魔法上限与攻击面板、
    /// 放升级声音与光效,底栏多一条经验条;没给就是不涨级的老行为。
    /// 没做:属性点与技能点的花费、装备的其余加成、技能的逐级成长、上挑自己的攻击信息与伤害数、
    /// 复活与结算里的经验显示、技能音效与语音、掉落。出口开关与换房间在 <see cref="DungeonSession"/>。证据 E-069。
    /// </summary>
    public sealed class RoomCombatSession : ISkillSession, ISoundSource, IItemSession
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

        /// <summary>脚本运行器的毫秒时钟:每一小步开头累加,不随命中停顿冻结([GET TIME] 与 [DELAY DO BEHAVIOR] 读它,E-132)。</summary>
        private double _actClockMs;

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

        // .act 触发器放出来的被动对象(证据 E-130):程序库、宿主与主角在脚本里的身份;没接上程序库(AttachActs)时全空,怪物只用 .mob/.ai 的行为
        private readonly List<PassiveObject> _passives = new List<PassiveObject>();
        private ActLibrary _acts;
        private ActWorld _actWorld;
        private HeroActEntity _heroEntity;

        // 金币掉落(证据 E-116):表为 null 时杀怪不掉东西(旧行为)
        private readonly List<FieldDrop> _drops = new List<FieldDrop>();
        private readonly List<GainPopup> _gains = new List<GainPopup>();
        private MonsterDropTable _dropTable;
        private GoldGenTable _goldTable;
        private ItemPool _itemPool;
        private DropRandom _dropRandom = new DropRandom(1);

        /// <summary>物品掉落(掷骰与落点)用的随机数,与金币的分开:装上物品池不改变金币的金额与落点序列。</summary>
        private DropRandom _itemRandom = new DropRandom(2);
        private int _dropSerial;

        /// <summary>被选中的地上物品(每一小步末尾重算,见 <see cref="SelectedDrop"/>)。</summary>
        private FieldDrop _selectedDrop;

        /// <summary>地上名牌的画法(只在有字体和文字表的运行里出图)。</summary>
        private DropLabels _dropLabels;

        /// <summary>名牌用的画笔与文字表;不给就用全进程共用的字体(<see cref="OverlayArt.SharedFonts"/>)与文字表(<see cref="WorldTexts.Shared"/>)。</summary>
        private OverlayArt _labelArt;
        private WorldTexts _labelTexts;

        /// <summary>地上金币与拾取飘字取图用的图源;默认是素材库,测试里换成合成的(<see cref="UseDropArt"/>)。</summary>
        private ISpriteSource _dropSprites;
        private readonly List<string> _throwSoundScratch = new List<string>();
        private readonly HashSet<MonsterActor> _hitThisSwing = new HashSet<MonsterActor>();

        // 可破坏物件(证据 E-126):房间里能被打的物件与每一击已经打中过的
        private readonly List<BreakableObject> _breakables = new List<BreakableObject>();
        private readonly HashSet<BreakableObject> _hitBreakablesThisSwing = new HashSet<BreakableObject>();
        private ObjectDropSetup _objectDrops;
        private DropRandom _objectRandom = new DropRandom(3);
        private readonly Random _random;

        /// <summary>怪物打主角的伤害种子用的随机数,与主角打怪物的伤害序列分开,免得互相改变对方的序列。</summary>
        private readonly Random _monsterRandom;

        /// <summary>.act 脚本的随机数([RANDOM]、[RANDOM SELECT]、随机朝向),与其余随机数分开,免得改变伤害与掉落的序列。</summary>
        private readonly Random _actRandom;
        private readonly DamageConstants _damageConstants;

        /// <summary>主角打怪物时的攻击面板(证据 E-110);为 null 时主角打怪物仍用占位伤害(<see cref="PlaceholderDamage"/>)。升级时换成新等级的(证据 E-111)。</summary>
        private HeroPanel _heroPanel;
        private AttackerPanel _attacker;

        /// <summary>最近一次同步过的等级进度版本(<see cref="HeroProgress.Version"/>):别的房间里升了级,这个房间回来时据此换上新等级的属性。</summary>
        private int _progressVersion;

        /// <summary>最近一次同步过的装备栏版本(<see cref="HeroProgress.EquipVersion"/>):穿脱之后据此换武器外观与跟武器走的常量、重算攻击面板的装备加成(证据 E-118)。</summary>
        private int _equipVersion;

        /// <summary>现在的 <see cref="WeaponStunMs"/> 等常量是按哪件武器取的(外观里的武器路径)。</summary>
        private string _derivedWeapon;

        /// <summary>跟着主角走的特效(升级光效,证据 E-111):每一步把它们挪到主角脚下。</summary>
        private readonly List<AnimatedEffect> _followEffects = new List<AnimatedEffect>();

        /// <summary>倒地的随机抖动(水平量 ±20%、高度 ±10%;E-083)用的随机数,与伤害的随机数分开,免得改变伤害序列。</summary>
        private readonly Random _physicsRandom;

        /// <summary>主角的倒地与起身(证据 E-079、E-082、E-085、E-105)。</summary>
        private readonly HeroKnockdown _heroDown;

        /// <summary>复活倒计时的画法(证据 E-115 的界面图);图包里没有时为 null,退回占位文字。</summary>
        private ReviveArt _reviveArt;

        /// <summary>复活后还剩的无敌毫秒(墙钟,每一小步递减)。</summary>
        private double _invincibleMs;

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
        private WeaponSound _weaponSound;
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

        /// <summary>怪物的 .act 脚本放出来、还在场上的被动对象(证据 E-130)。</summary>
        public IReadOnlyList<PassiveObject> Passives => _passives;

        /// <summary>从开始玩起脚本一共放出过多少个被动对象(含已经消失的)。</summary>
        public int PassivesSpawned { get; private set; }

        /// <summary>其中打中主角的次数(一个对象被脚本 [ATTACKRECT] [RESET] 之后可以再打,所以可能比对象个数多)。</summary>
        public int PassiveHits { get; private set; }

        /// <summary>被动对象的程序库;没接上时为 null。</summary>
        public ActLibrary Acts => _acts;

        /// <summary>这个房间里出招带有可执行 .act 触发器的怪物个数(没接上程序库时为 0)。</summary>
        public int ActMonsterCount
        {
            get
            {
                int n = 0;
                foreach (MonsterActor m in _monsters)
                {
                    if (m.HasActs)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>主角在 .act 脚本里的身份;没接上程序库时为 null。</summary>
        public IActEntity HeroEntity => _heroEntity;

        /// <summary>脚本运行器眼里的房间(随机数、放被动对象、解析选择器);没接上程序库时为 null。单测与工具用它直接放对象。</summary>
        public IActHost ActHost => _actWorld;

        /// <summary>地上还没捡的金币堆(证据 E-116)。</summary>
        public IReadOnlyList<FieldDrop> Drops => _drops;

        /// <summary>正在升起的拾取飘字。</summary>
        public IReadOnlyList<GainPopup> Gains => _gains;

        /// <summary>金币掉落用的怪物掉落表;没有时为 null(杀怪不掉金币)。</summary>
        public MonsterDropTable DropTable => _dropTable;

        /// <summary>
        /// 副本的标准等级(掉落概率行按它选,<c>CDungeon::get_standard_level</c>,证据 E-116):<see cref="DungeonSession"/> 从 .dgn 的 [basis level] 读来
        /// (<see cref="GoldDrop.StandardLevel"/>);0 表示没有副本背景(单个房间的演示),按被杀怪物自己的等级选(推断)。
        /// </summary>
        public int DropDungeonLevel { get; set; }

        /// <summary>掉落率百分比(服务端全局或活动加成,100 = 不变;证据 E-116)。</summary>
        public int DropRate { get; set; } = 100;

        /// <summary>
        /// 任务事件的接收方(M31,<see cref="QuestBook"/>):主角杀死怪物时通知它;没有任务系统时为 null。
        /// 副本编号与难度由 <see cref="DungeonSession"/> 填(任务的猎杀目标按"副本 + 难度 + 怪物"匹配,证据 E-123);单个房间的演示是 −1 与 0。
        /// </summary>
        public IQuestEvents QuestEvents { get; set; }

        /// <summary>任务事件里的副本编号(dungeon/dungeon.lst 的号);不在副本里是 −1。</summary>
        public int QuestDungeonId { get; set; } = -1;

        /// <summary>任务事件里的副本难度(0 = 普通)。</summary>
        public int QuestDifficulty { get; set; }

        /// <summary>这个房间里一共掉出过多少堆金币(含已经捡走的)。</summary>
        public int GoldDropped { get; private set; }

        /// <summary>掉出过的金币总额。</summary>
        public long GoldDroppedAmount { get; private set; }

        /// <summary>主角在这个房间里捡了多少堆。</summary>
        public int GoldPicked { get; private set; }

        /// <summary>主角在这个房间里捡到的金币总额。</summary>
        public long GoldEarned { get; private set; }

        /// <summary>最近一次捡到的金额(没捡过是 0)。</summary>
        public int LastGoldPickup { get; private set; }

        /// <summary>物品掉落池(证据 E-117);没有时为 null(杀怪只掉金币)。</summary>
        public ItemPool ItemPool => _itemPool;

        /// <summary>这个房间里一共掉出过多少件物品(含已经捡走的)。</summary>
        public int ItemsDropped { get; private set; }

        // ------------------------------------------------------------------ 可破坏物件(证据 E-126)

        /// <summary>这个房间里能被打的物件(木桶、箱子、会晃的树……),包括已经打碎的。</summary>
        public IReadOnlyList<BreakableObject> Breakables => _breakables;

        /// <summary>主角一共打中物件多少次(同一击对同一个物件只算一次)。</summary>
        public int ObjectsHit { get; private set; }

        /// <summary>打碎了多少个物件。</summary>
        public int ObjectsDestroyed { get; private set; }

        /// <summary>物件掉出了多少件物品(已计入 <see cref="ItemsDropped"/>)。</summary>
        public int ObjectItemsDropped { get; private set; }

        /// <summary>执行过多少个物件的 <c>[item]</c> 动作(每个动作掷一次,可能一件也掷不出)。</summary>
        public int ObjectItemActions { get; private set; }

        /// <summary>
        /// 装上这座副本里物件掉物品要用的东西(见 <see cref="ObjectDropSetup"/>);不装就是物件被打碎时不掉东西。
        /// <paramref name="seed"/> 是掷骰用的随机数种子(与怪物的掉落、伤害的随机序列分开)。
        /// </summary>
        public void UseObjectDrops(ObjectDropSetup setup, int seed = 1)
        {
            _objectDrops = setup;
            _objectRandom = new DropRandom(unchecked(seed * 17 + 0x1B873593));
        }

        /// <summary>主角在这个房间里捡了多少件物品。</summary>
        public int ItemsPicked { get; private set; }

        /// <summary>主角够得着、但背包放不下的物品有多少件(每件只算一次;东西留在地上)。</summary>
        public int ItemsBagFull { get; private set; }

        /// <summary>一共画出过多少块地上名牌(每次出图画了几块就加几;没有字体或文字表时一直是 0),无人值守的检查靠它确认名牌真的画出来过。</summary>
        public int DropLabelsDrawn { get; private set; }

        /// <summary>出图时带着"选中"名牌(主角够得着、按攻击键就会捡的那一件)的次数。</summary>
        public int SelectedLabelFrames { get; private set; }

        /// <summary>最近一次捡到的物品(没捡过是 null)。</summary>
        public ItemInfo LastItemPickup { get; private set; }

        /// <summary>
        /// 被选中的地上物品(证据 E-122):主角够得着的物品里离他最近的一件(背包放不下、已经提示过的不算),按攻击键就捡它;没有就是 null。
        /// 画成名牌带黄框的那一件。金币不选,走到就捡。
        /// </summary>
        public FieldDrop SelectedDrop => _selectedDrop;

        /// <summary>
        /// 装上怪物金币掉落数据(证据 E-116):<paramref name="table"/> 与 <paramref name="gold"/> 任一为 null 都等于关掉金币掉落;
        /// <paramref name="items"/>(证据 E-117)有就同时掉物品(需要 <paramref name="table"/>),不给就不掉物品;
        /// <paramref name="seed"/> 是掷骰与落点用的随机数种子(与其余随机数分开,免得改变伤害序列)。
        /// </summary>
        public void UseDrops(MonsterDropTable table, GoldGenTable gold, int seed = 1, ItemPool items = null)
        {
            _dropTable = table;
            _goldTable = gold;
            _itemPool = items;
            _dropRandom = new DropRandom(seed);
            _itemRandom = new DropRandom(unchecked(seed * 31 + 0x2545F491));
        }

        /// <summary>换地上金币与拾取飘字的图源(没有原版图包的测试里喂合成的图);传 null 回到素材库。图源里缺帧时金币不画、飘字退回占位文字。</summary>
        public void UseDropArt(ISpriteSource sprites)
        {
            _dropSprites = sprites ?? _assets;
        }

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

        /// <summary>在连击里(连击数已经显示,即至少第 2 击)打死的怪物数(M32:评分的"连击杀"估计用;连击窗口见 <see cref="ComboCounter"/>)。</summary>
        public int ComboKills { get; private set; }

        /// <summary>这个房间里出现过的最高连击数。</summary>
        public int MaxCombo { get; private set; }

        /// <summary>累计连击数:窗口(<see cref="ComboMs"/>)内每一击加 1,最后一击之后走满窗口清零;屏幕上从第 2 击起才显示(证据 E-108)。</summary>
        public int Combo => _combo.Count;
        public long TotalDamage { get; private set; }

        /// <summary>放出来的技能次数(冷却中、忙碌或没绑定的按键不算)。</summary>
        public int SkillCasts { get; private set; }

        /// <summary>多段技能接上下一段的次数(证据 E-129;不算进 <see cref="SkillCasts"/>,接段不是新的一次施放)。</summary>
        public int SkillChains { get; private set; }

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

        /// <summary>房间里由 .ai 脚本驱动的怪物个数(M36;没有 [ai pattern] 的旧式怪物不算),状态日志与实机检查用。</summary>
        public int ScriptedCount
        {
            get
            {
                int n = 0;
                foreach (MonsterActor m in _monsters)
                {
                    if (m.Script != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>脚本驱动的怪物里已经发现主角的个数。</summary>
        public int AwareCount
        {
            get
            {
                int n = 0;
                foreach (MonsterActor m in _monsters)
                {
                    if (m.Script != null && m.IsAware)
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
        public int WeaponStunMs { get; private set; }

        /// <summary>
        /// 主角手上这件武器的命中延迟倍率(同一项 [weapon hit info] 里的浮点,证据 E-077、E-080):受击一侧的硬直要乘它。
        /// 攻击信息的 [hit info] 浮点项是 -1 或没写时用它;-1 表示没有这张表。
        /// </summary>
        public float WeaponDelayRate { get; private set; }

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

        /// <summary>
        /// 测试旋钮:主角打出的每一击伤害乘上这个数(默认 1,正常游戏恒为 1)。全库副本扫描(dungeon_sweep)用它排除"一级主角打不动高等级怪物"的干扰,
        /// 单独量布局、进图、清房与出口通不通;它不是原版规则。
        /// </summary>
        public int HeroDamageScale { get; set; } = 1;

        /// <summary>
        /// 测试旋钮:怪物打中主角的每一击伤害乘上这个数(默认 1,正常游戏恒为 1)。死亡、复活与失败回城的无人值守检查用它让主角几下就死,
        /// 不必等一个一级主角被慢慢磨死;它不是原版规则。
        /// </summary>
        public int MonsterDamageScale { get; set; } = 1;

        /// <summary>主角已经被打死(血量归零)。死后不再接受输入,怪物待机。</summary>
        public bool HeroDead => Vitals != null && !Vitals.IsAlive;

        /// <summary>
        /// 主角的死亡窗口与复活(证据 E-115):主角躺下之后窗口打开、失败计时开始,按攻击键(X)用币复活,计时走完副本失败(<see cref="HeroRevive.Failed"/>)。
        /// 副本里各房间的会话共用同一个对象(已用次数、计时按整座副本算);没给(null)就是老行为:主角死了一直躺着、没有复活也没有失败(测试与无复活的工具用)。
        /// </summary>
        public HeroRevive Revive { get; set; }

        /// <summary>复活窗口开着而且此刻按复活键能复活(<see cref="HeroRevive.CanRevive"/>)。</summary>
        public bool CanRevive => Revive != null && Revive.CanRevive;

        /// <summary>从开始玩起这个房间里主角复活了几次。</summary>
        public int Revived { get; private set; }

        /// <summary>复活之后还剩多少毫秒的无敌(推断的占位,见 <see cref="HeroRevive.InvincibleMs"/>);0 表示不无敌。</summary>
        public double InvincibleMs => _invincibleMs;

        /// <summary>主角此刻无敌:怪物的攻击与投射物都打不到他(攻击框照常走完、不会攒到无敌结束后一起结算)。</summary>
        public bool HeroInvincible => _invincibleMs > 0;

        /// <summary>复活时放在主角身上的特效动画(<c>UseCoin.ani</c>;客户端用币的特效函数 0x00865b50 加载它,放约 1 秒,挂在主角身上是推断)。</summary>
        public const string ReviveEffectPath = "Common/Etc/Animation/UseCoin.ani";

        /// <summary>用另一个图源重建复活倒计时的画法(测试里喂合成的图);传 null,或图源里没有倒计时的数字图,就退回占位的文字。</summary>
        public void UseReviveArt(ISpriteSource sprites)
        {
            _reviveArt = ReviveArt.For(sprites);
        }

        /// <summary>复活倒计时的画法;图包里没有数字图时为 null。</summary>
        public ReviveArt RevivePicture => _reviveArt;

        /// <summary>
        /// 用复活键(X):窗口开着、检查通过就扣币、让主角原地站起来(<see cref="ReviveHero"/>)并返回 true;否则什么也不变。
        /// 玩家按攻击键、自动机的"攻击"都走 <see cref="Step"/> 里的同一条路;这个入口留给测试与不经过按键的调用方。
        /// </summary>
        public bool TryReviveHero()
        {
            if (Revive == null || Vitals == null || !HeroDead || !Revive.TryRevive())
            {
                return false;
            }
            ReviveHero();
            return true;
        }

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

        /// <summary>主角的等级与经验(证据 E-111);为 null 时杀怪不给经验(旧行为)。副本里各房间的会话共用同一个对象。</summary>
        public HeroProgress Progress { get; }

        /// <summary>
        /// 主角的角色定义(.chr):穿脱武器之后重新取跟武器走的常量(命中停顿、命中延迟倍率、挥砍与命中声音)要用;
        /// <see cref="Create"/> 给了它,直接 new 出来的会话没有(那样换武器时只换外观与攻击面板,不换这些常量)。
        /// </summary>
        public CharacterDef Character { get; set; }

        /// <summary>这座副本的经验权重(.dgn 的 [experience increasing point],证据 E-111);没有副本背景时是 1。</summary>
        public float ExpWeight { get; }

        /// <summary>副本难度编号(0 到 4):经验倍率表、掉落与怪物数值的难度加成(<see cref="MonsterDifficultyTable"/>)按它取;直接 new 出来的会话是 0。</summary>
        public int ExpDifficulty { get; }

        /// <summary>这个房间里主角一共得到的击杀经验。</summary>
        public long ExpEarned { get; private set; }

        /// <summary>这个房间里主角涨了几级。</summary>
        public int LevelUps { get; private set; }

        /// <summary>最近一次击杀给的经验(没杀过是 0)。</summary>
        public int LastKillExp { get; private set; }

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
            WeaponSound weaponSound = default, HeroPanel heroPanel = null, HeroProgress progress = null, float expWeight = 1f, int expDifficulty = 0)
        {
            _weaponSound = weaponSound;
            _heroPanel = heroPanel;
            _attacker = heroPanel?.ToAttacker();
            Progress = progress;
            ExpWeight = expWeight;
            ExpDifficulty = expDifficulty;
            _progressVersion = progress == null ? 0 : progress.Version;
            _equipVersion = progress == null ? 0 : progress.EquipVersion;
            _derivedWeapon = HeroLook.WeaponPathOf(room.Hero.Outfit);
            Skills = skills ?? new HeroSkills(new HeroSkillSlot[0]);
            _cueCooling = new bool[Skills.Slots.Count];
            WeaponStunMs = weaponStunMs;
            WeaponDelayRate = weaponDelayRate;
            StunTable = stunTable;
            Vitals = vitals;
            _damageConstants = damageConstants;
            _monsterRandom = new Random(unchecked(seed * 131 + 29));
            _actRandom = new Random(unchecked(seed * 977 + 61));
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _dropSprites = _assets;
            _room = room ?? throw new ArgumentNullException(nameof(room));
            _monsters = new List<MonsterActor>(monsters ?? new List<MonsterActor>());
            foreach (RoomObject o in room.Content.Objects)
            {
                BreakableObject b = BreakableObject.TryCreate(o);
                if (b != null)
                {
                    _breakables.Add(b);
                }
            }
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
            _reviveArt = ReviveArt.For(assets);
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
        /// 等级取 .mob 的 [level] 第一个数,生命值由 <see cref="MonsterAbility.Compute"/> 算(首领按首领种类,再加副本难度的加成,
        /// <paramref name="difficulty"/> 为 0 到 4;<paramref name="dungeonBonus"/> 是 .dgn 自带的难度加成,没有就按等级行);
        /// 普攻数据读 swordman 的 .chr [attack info]。缺图或缺脚本的怪物跳过。
        /// <paramref name="heroVitals"/> 为假时主角没有生命值(<see cref="Vitals"/> 为 null):怪物照样走近、出招,但打不到主角,没有血条(自动化检查用)。
        /// </summary>
        public static RoomCombatSession Create(DnfAssets assets, RoomSession room, int seed = 1, bool heroVitals = true, HeroProgress progress = null,
            float expWeight = 1f, int difficulty = 0, DungeonDifficultyBonus dungeonBonus = null)
        {
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            var strikes = new List<HeroStrikeInfo>();
            CharacterDef chr = data.GetCharacter(0);
            // 装备栏里穿着武器时,外观与跟武器走的常量都按它来(证据 E-118);房间造出来时已经是这件的话什么也不做
            if (progress?.WeaponPath != null)
            {
                HeroLook.Apply(assets, room.Hero, progress);
            }
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
                MonsterStats stats = MonsterAbility.Compute(def, data.MonsterBaseTables, data.GetMonsterOwnTable(def), level, 0, kind,
                    data.MonsterDifficulty, difficulty, dungeonBonus);                  // 难度加成接在 [ability category] 之后(证据 E-125)
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
                // 默认 AI 的走位:游荡、发现主角、按节拍朝主角走(证据 E-114);没有 [melee combat]/[range combat]/[stay] 类别的怪物仍走占位
                actor.SetDefaultAi(MonsterBrain.For(def, room.Map.Width, unchecked((uint)seed * 2246822519u + (uint)actor.Index * 3266489917u + 374761393u)));
                actor.SetThrows(MonsterThrows.Load(assets, data, def.Path, ThrowAttackRecord.Parse(def.Tags)));   // [throw attack]:出招动画到发射帧时扔东西(证据 E-103)
                // .ai 脚本驱动(证据 E-128):带 [ai pattern] 的怪物出招、走位交给四个 .ai 脚本;读不到脚本的仍走上面的默认 AI 与占位
                uint aiState = unchecked((uint)seed * 3266489917u + (uint)actor.Index * 668265263u + 2246822519u);
                actor.SetScript(MonsterScript.Create(data, def, model, difficulty, () => ClientRandom.Next(ref aiState)));
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
            // 给了等级进度(证据 E-111)时,生命魔法与攻击面板是这个等级的(初始值加逐级增量);没给就是只读初始值的老行为
            HeroVitals vitals = heroVitals && chr != null
                ? (progress != null ? HeroVitals.FromStats(progress.Level, progress.Stats) : HeroVitals.FromChr(chr))
                : null;
            // 主角的攻击面板(证据 E-110):与公式常量、生命值一起给;没有生命值(--no-damage)时仍用占位伤害,按时刻对拍的回归检查靠它保持原来的时刻
            HeroPanel heroPanel = null;
            if (vitals != null)
            {
                EquipmentStats worn = HeroPanel.WornEquipment(assets, room.Hero, progress);
                heroPanel = progress != null ? HeroPanel.FromStats(progress.Level, progress.Stats, worn) : HeroPanel.FromChr(chr, vitals.Level, worn);
            }
            // 技能快捷槽:有等级进度并接上了技能数据时按它的技能簿排(学会的技能、等级、快捷栏,M35);否则按角色的初始技能表(老行为)
            HeroSkills heroSkills = progress != null && progress.HasSkillData ? HeroSkills.Create(data, chr, 0, progress.Skills) : HeroSkills.Create(data, chr, 0);
            var session = new RoomCombatSession(assets, room, actors, strikes, seed, dash, jump, heroSkills, hasWeapon ? weapon.StunMs : 0,
                hasWeapon ? weapon.DelayRate : -1f, stunTable, vitals, vitals != null ? MonsterDamage.LoadConstants(assets) : null, weaponSound, heroPanel,
                progress, expWeight, difficulty);
            session.Character = chr;
            session.AttachActs(new ActLibrary(assets, data));                      // .act 的触发器与行为:被动对象、冲刺、攻击框开关(证据 E-130)
            foreach (MonsterActor a in actors)
            {
                MonsterActor self = a;
                if (self.Script != null)
                {
                    self.Script.Counter = (x0, x1, y0, y1, team, type, id) => session.CountForMonster(self, x0, x1, y0, y1, team, type, id);
                }
            }
            MonsterDrops drops = MonsterDrops.Load(assets);
            if (drops.Available)
            {
                session.UseDrops(drops.Table, drops.Gold, unchecked(seed * 7919 + 17), drops.Pool);
            }
            return session;
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
        /// 怪物的水平行走速度(像素/秒):[move speed] 乘以基线量到的每点步速 <see cref="MonsterTuning.WalkPixelsPerStat"/>(证据 E-114,非 REA 的测量);
        /// 没有数据时取与主角同速。之前(E-066、E-069)按主角的实测步速与 [move speed] 的比例折算,基线对不上,改成直接用量到的斜率。
        /// </summary>
        public static double WalkSpeed(TownHero hero, int monsterMoveSpeed)
        {
            if (monsterMoveSpeed <= 0)
            {
                return hero.WalkSpeedX;
            }
            return monsterMoveSpeed * MonsterTuning.WalkPixelsPerStat;
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
            SyncProgress();   // 别的房间里升了级的话,换上新等级的生命上限与攻击面板(证据 E-111)
            _actClockMs += deltaMs;
            Skills.Advance(deltaMs);
            Vitals?.RegenMp(deltaMs);   // 魔法值按墙钟恢复(证据 E-087);死了不恢复在 RegenMp 里判断
            if (Vitals != null)
            {
                Vitals.AdvanceRecovery(deltaMs);        // 药水的药效按墙钟线性发放(证据 E-120),倒下就作废
                Progress?.Cooldowns.Advance(deltaMs);   // 消耗品的冷却也走墙钟
            }
            if (_invincibleMs > 0)
            {
                _invincibleMs = Math.Max(0.0, _invincibleMs - deltaMs);   // 复活后的无敌也按墙钟走
            }

            bool reviveKey = false;
            if (HeroDead)
            {
                // 主角死了:不再接受任何输入;攻击键(X)在复活窗口开着时就是"使用复活币"(证据 E-115)
                reviveKey = attackPressed || _bufferedAttack;
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
                // 正在放多段技能时,再按它的键就是接下一段(证据 E-129);不是接段才按起手处理
                HeroSkillSlot chained = skillsPressed == SkillKeys.None ? null : Skills.TryChain(skillsPressed, _room.Hero, dirX, dirY);
                HeroSkillSlot cast = skillsPressed == SkillKeys.None || chained != null ? null : Skills.TryCast(skillsPressed, _room.Hero, Vitals, dirX, dirY);
                if (skillsPressed != SkillKeys.None && chained == null)
                {
                    EmitSkillCues(cast);
                }
                if (chained != null)
                {
                    attackPressed = false;
                    jumpPressed = false;
                    SkillChains++;
                }
                else if (cast != null)
                {
                    attackPressed = false;
                    jumpPressed = false;
                    SkillCasts++;
                    SpawnEffects(cast);
                }
                else if (attackPressed && TryPickupSelected(_room.Hero))
                {
                    attackPressed = false;     // 攻击键这一下用来捡东西了(证据 E-122)
                }
                _room.Update(heroDelta, dirX, dirY, attackPressed, jumpPressed);
                SpawnStageEffect(_room.Hero);
            }
            EmitHeroCues();
            UpdateRevive(deltaMs, reviveKey);
            TownHero hero = _room.Hero;
            // 有主角的生命值时,怪物按"这一击能不能打到主角的受击框"决定靠近与出招,主角死了就待机;没有时按固定距离(旧行为)
            bool heroAlive = !HeroDead;
            List<Aabb> heroBoxes = Vitals == null ? null : HeroHurtBoxes.At(hero);
            foreach (MonsterProjectile p in _projectiles)
            {
                p.Update(deltaMs);
            }
            int heroAiState = HeroAiState(hero);
            foreach (MonsterActor m in _monsters)
            {
                m.Blocker = hero.Blocker;                      // 关着的出口门体怪物也走不进去(E-114)
                m.TargetAiState = heroAiState;
                m.Update(deltaMs, hero.X, hero.Y, heroAlive, hero.Walk, heroBoxes);
            }
            UpdatePassives(deltaMs);
            CollectThrown();
            EmitMonsterCues();
            ResolveHits();
            ResolveMonsterHits();
            ResolveProjectileHits();
            ResolvePassiveHits();
            _projectiles.RemoveAll(p => p.Finished);
            _passives.RemoveAll(p => p.Finished);
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
                    _followEffects.Remove(_effects[i]);
                    _effects.RemoveAt(i);
                }
            }
            foreach (AnimatedEffect follow in _followEffects)
            {
                follow.MoveTo(hero.X, hero.Y, hero.Z);   // 升级光效跟着主角走(跟随是推断:客户端把它挂在主角身上)
            }
            foreach (BreakableObject b in _breakables)
            {
                b.Object.Advance(heroDelta);             // 物件的受击动画与命中停顿一起冻结(受击方与攻击方定住同样久,E-077)
            }
            UpdateDrops(deltaMs, hero);
            _targetMs += deltaMs;
            _hudClockMs += deltaMs;
            _monsterHp?.Update(_hudClockMs);
            _combo.Update(_hudClockMs);
            if (_targetMs >= TargetBarMs)
            {
                Target = null;
            }
        }

        /// <summary>主角此刻的状态号(.ai 里的 [target state],OperatingValue.lst 的写法):死亡 5、倒地 4、受击 3、跳 6、空中攻击 7、出招与技能 8、冲刺 11、冲刺攻击 12,其余站着 0(证据 E-128)。</summary>
        private int HeroAiState(TownHero hero)
        {
            if (HeroDead)
            {
                return _heroDown.Active ? 4 : 5;
            }
            if (_heroDown.Active)
            {
                return 4;
            }
            switch (hero.Motion)
            {
                case HeroMotion.Hurt:
                    return 3;
                case HeroMotion.Jump:
                    return 6;
                case HeroMotion.JumpAttack:
                    return 7;
                case HeroMotion.Attack:
                case HeroMotion.Skill:
                    return 8;
                case HeroMotion.Dash:
                    return 11;
                case HeroMotion.DashAttack:
                    return 12;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// 给 .ai 的区域计数函数用:数 <paramref name="self"/> 眼里闭区间内的活对象。敌对(队伍 -1)与不限(100)里算上主角(种类 4 或不限),
        /// 友方(1)与不限里算上别的怪物(种类 2 或不限);召唤物、被动物件、中立这几类还没有对象,数为 0。<paramref name="id"/> 不是 -1 时只数怪物编号相同的(主角没有编号,不算)。
        /// </summary>
        public int CountForMonster(MonsterActor self, float xMin, float xMax, float yMin, float yMax, int team, int type, int id)
        {
            int count = 0;
            if ((team == -1 || team == 100) && (type == AiObjectKind.Character || type == AiObjectKind.All) && id == -1 && !HeroDead
                && AiArea.Contains(xMin, xMax, yMin, yMax, (float)_room.Hero.X, (float)_room.Hero.Y))
            {
                count++;
            }
            if ((team == 1 || team == 100) && (type == AiObjectKind.Monster || type == AiObjectKind.All))
            {
                foreach (MonsterActor m in _monsters)
                {
                    if (!ReferenceEquals(m, self) && m.IsAlive && (id == -1 || m.Code == id) && AiArea.Contains(xMin, xMax, yMin, yMax, (float)m.X, (float)m.Y))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// 复活窗口(证据 E-115):主角躺平(倒地的飞行与颠簸结束,对应服务端收到死亡通知的那一刻,E-084)时打开、失败计时开始;
        /// 窗口开着时推进计时,按了复活键(这一小步的攻击键)而且检查通过就复活。没有复活对象时什么也不做。
        /// </summary>
        private void UpdateRevive(double deltaMs, bool reviveKey)
        {
            HeroRevive revive = Revive;
            if (revive == null || !HeroDead || _heroDown.Active)
            {
                return;
            }
            if (!revive.Open && !revive.Failed)
            {
                revive.Begin();
            }
            if (!revive.Open)
            {
                return;
            }
            revive.Tick(deltaMs);
            if (reviveKey && revive.Open && revive.TryRevive())
            {
                ReviveHero();
            }
        }

        /// <summary>
        /// 主角原地复活:生命魔法回满(推断:满血满蓝)、躺着的主角起身(<see cref="HeroKnockdown.Revive"/>,没有倒地动作的占位死亡就结束受击姿势)、
        /// 起身期间与之后 <see cref="HeroRevive.InvincibleMs"/> 毫秒无敌(推断)、身上放币的特效。
        /// </summary>
        private void ReviveHero()
        {
            TownHero hero = _room.Hero;
            Vitals.Restore();
            if (!_heroDown.Revive())
            {
                hero.EndHurt();
            }
            _invincibleMs = HeroRevive.InvincibleMs + (_heroDown.Active ? MonsterTuning.KnockDownHoldMs : 0.0);
            _heroPauseMs = 0;
            Revived++;
            AnimatedEffect effect = AnimatedEffect.Create(_assets, ReviveEffectPath, hero.X, hero.Y, hero.Z, hero.FacingLeft);
            if (effect != null)
            {
                _effects.Add(effect);
                _followEffects.Add(effect);
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

        /// <summary>
        /// 配方技能的分段特效(证据 E-129):这一段(按 <see cref="TownHero.SwingNumber"/> 区分)的动画播到指定帧时放一次,
        /// 特效挂在主角身上跟着走(客户端是把它放进主角的动画图层,所以滑行时跟着动),朝向与主角一致。
        /// </summary>
        private void SpawnStageEffect(TownHero hero)
        {
            if (hero.Motion != HeroMotion.Skill)
            {
                _stageEffectSwing = -1;
                return;
            }
            HeroSkillSlot slot = Skills.SlotOfSkill(hero.SkillId);
            SkillRecipe recipe = slot?.Recipe;
            if (recipe == null || _stageEffectSwing == hero.SwingNumber)
            {
                return;
            }
            SkillStageSpec stage = recipe.Stages[Math.Min(hero.SkillStage, recipe.Stages.Count - 1)];
            if (stage.EffectIndex < 0 || hero.CurrentFrame < stage.EffectFrame)
            {
                return;
            }
            _stageEffectSwing = hero.SwingNumber;
            if (stage.EffectIndex >= slot.StageEffects.Count)
            {
                return;   // 技能预载表里没有这个下标(读不到脚本或脚本没写):这一段不放特效
            }
            AnimatedEffect effect = AnimatedEffect.Create(_assets, slot.StageEffects[stage.EffectIndex], hero.X, hero.Y, hero.Z, hero.FacingLeft);
            if (effect != null)
            {
                _effects.Add(effect);
                _followEffects.Add(effect);
            }
        }

        private long _stageEffectSwing = -1;

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
                _hitBreakablesThisSwing.Clear();
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
            // 可破坏物件:同一套攻击框与受击框相交判定;打碎了的不再挨打(证据 E-126)
            for (int i = 0; i < _breakables.Count; i++)
            {
                BreakableObject b = _breakables[i];
                if (b.Destroyed || _hitBreakablesThisSwing.Contains(b))
                {
                    continue;
                }
                if (TryHit(strike, b.HurtBoxes, out _))
                {
                    _hitBreakablesThisSwing.Add(b);
                    StrikeBreakable(hero, b);
                }
            }
        }

        /// <summary>
        /// 攻击框与受击框的相交判定:外层攻击框、内层受击框,第一对相交的就是命中,返回它们的相交盒(客户端的"命中信息",证据 E-058);
        /// 伤害数字按它的中心放(证据 E-107)。
        /// </summary>
        private static bool TryHit(List<Aabb> strike, IReadOnlyList<Aabb> hurt, out Aabb hit)
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

        /// <summary>这一击用的攻击信息:奔跑攻击、空中攻击、技能(有自己攻击信息的)与普攻各取各的,对怪物与对物件都一样。</summary>
        private HeroStrikeInfo CurrentStrikeInfo(TownHero hero)
        {
            switch (hero.AttackKind)
            {
                case HeroAttackKind.Dash:
                    return _dashStrike;
                case HeroAttackKind.Jump:
                    return _jumpStrike;
                case HeroAttackKind.Skill:
                    // 有自己攻击信息与伤害数的技能(鬼斩)用自己的;借普攻动作放的技能(上挑)沿用那一段普攻的攻击信息
                    HeroSkillSlot slot = Skills.SlotOfSkill(hero.SkillId);
                    HeroStrikeInfo? own = slot?.StrikeAt(hero.SkillStage);
                    if (own.HasValue)
                    {
                        return own.Value;
                    }
                    break;
            }
            return hero.AttackStage < _strikes.Length ? _strikes[hero.AttackStage] : new HeroStrikeInfo(0, 0, false);
        }

        private void Strike(TownHero hero, MonsterActor monster, Aabb hit)
        {
            HeroStrikeInfo info = CurrentStrikeInfo(hero);
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
            if (HeroDamageScale > 1)
            {
                damage = (int)Math.Min(int.MaxValue, (long)damage * HeroDamageScale);
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
            if (_combo.Count > MaxCombo)
            {
                MaxCombo = _combo.Count;
            }
            TotalDamage += damage;
            Target = monster;
            _targetMs = 0;
            if (killed)
            {
                Kills++;
                if (_combo.Count >= ComboCounter.ShowFrom)
                {
                    ComboKills++;
                }
                AwardKillExp(monster);
                SpawnDrops(monster);
                QuestEvents?.MonsterKilled(QuestDungeonId, QuestDifficulty, monster.Code, monster.IsBoss);
            }
            AddNumber(damage, DamageNumberFlags.AttackerIsHero | (critical ? DamageNumberFlags.Critical : 0), hit, monster.Y);
        }

        /// <summary>
        /// 主角的这一击打中了一个物件(证据 E-126):计一下,放受击声(打碎时再放破坏声),命中停顿只定住主角(物件没有"受击硬直"),
        /// 打够了就触发它挂的动作(掉物品)。不算连击、不出伤害数字、不进伤害统计——这些怎么对物件结算没有读到,按不算处理(推断)。
        /// </summary>
        private void StrikeBreakable(TownHero hero, BreakableObject b)
        {
            HeroStrikeInfo info = CurrentStrikeInfo(hero);
            BreakableOutcome outcome = b.Hit(_assets);
            ObjectsHit++;
            _sounds.Add(b.Data.HitSound, SoundCueKind.Effect);
            if (outcome == BreakableOutcome.Destroyed)
            {
                ObjectsDestroyed++;
                _sounds.Add(b.Data.DestroySound, SoundCueKind.Effect);
            }
            int stun = HitStop.StunMs(info.HitValue, info.UseWeaponHitDelayRate, WeaponStunMs);
            if (stun > _heroPauseMs)
            {
                _heroPauseMs = stun;
            }
            if (outcome != BreakableOutcome.Reacted)
            {
                ReleaseObjectActions(b);
            }
        }

        /// <summary>
        /// 打够的物件触发它挂的动作(证据 E-126):<c>[item]</c> 按副本的掉落项生成物品、从物件脚下抛出;<c>[monster]</c> 与 <c>[trap]</c>、<c>[quest]</c> 这一版不做。
        /// 没装物件掉落数据(<see cref="UseObjectDrops"/>)时什么也不掉。
        /// </summary>
        private void ReleaseObjectActions(BreakableObject b)
        {
            SpecialPlacement special = b.Object.Special;
            if (special == null || _objectDrops == null)
            {
                return;
            }
            foreach (SpecialAction action in special.Actions)
            {
                if (action.Kind != SpecialActionKind.Item)
                {
                    continue;
                }
                ObjectItemActions++;
                foreach (ItemInfo item in _objectDrops.Roll(action, _objectRandom))
                {
                    SpawnItem(item, b.Object.X, b.Object.Y);
                    ObjectItemsDropped++;
                }
            }
        }

        // ------------------------------------------------------------------ 经验与升级(证据 E-111)

        /// <summary>升级时放的声音标签(audio.xml 的界面音;客户端在升级处理里放它,证据 E-111)。</summary>
        public const string LevelUpSound = "LEVEL_UP";

        /// <summary>升级时在主角身上放的光效动画(证据 E-111:客户端的升级处理里加载它;挂在主角身上是推断,逐帧缩放没有画)。</summary>
        public const string LevelUpEffectPath = "Common/CommonEffect/Animation/LevelUp.ani";

        /// <summary>
        /// 主角杀死一只怪物得到的经验(证据 E-111):基础经验取怪物等级、总经验按种类(首领 3,其余 0)、有名怪物、副本难度与副本经验权重算,
        /// 再乘等级差倍率(<see cref="KillExp.ForKill"/>);单人,不分队。没有等级进度时什么也不做。
        /// </summary>
        private void AwardKillExp(MonsterActor monster)
        {
            HeroProgress progress = Progress;
            if (progress == null)
            {
                return;
            }
            int role = monster.IsBoss ? (int)MonsterKind.Boss : (int)MonsterKind.Normal;
            bool named = MonsterAbility.IsNamedMonster(monster.Code);
            int exp = KillExp.ForKill(progress.Rewards, progress.Rates, progress.Level, monster.Level, role, named, ExpDifficulty, ExpWeight);
            LastKillExp = exp;
            GainExp(exp);
        }

        // ------------------------------------------------------------------ 金币掉落与拾取(证据 E-116)

        /// <summary>
        /// 主角杀死一只怪物时先掷金币(<see cref="GoldDrop.Roll"/>)再掷物品(<see cref="ItemDrop.Roll"/>,证据 E-117,服务端同一个函数里也是这个顺序):
        /// 种类首领为 3、其余 0(与经验同一条取法,推断),难度取 <see cref="ExpDifficulty"/>,单人,
        /// 副本等级见 <see cref="DropDungeonLevel"/>,掷一次(服务端 <c>map_monster</c> 里的次数这里按 1,推断)。每一堆、每一件都从怪物身上抛到周围的可站处。
        /// 没装掉落数据时什么也不做。
        /// </summary>
        private void SpawnDrops(MonsterActor monster)
        {
            if (_dropTable == null)
            {
                return;
            }
            int role = monster.IsBoss ? (int)MonsterKind.Boss : (int)MonsterKind.Normal;
            int dungeonLevel = DropDungeonLevel > 0 ? DropDungeonLevel : monster.Level;
            var context = new DropContext(monster.Level, role, dungeonLevel, ExpDifficulty, 1, 0, DropRate, 1);
            if (_goldTable != null)
            {
                foreach (int amount in GoldDrop.Roll(_dropTable, _goldTable, context, _dropRandom))
                {
                    SpawnGold(amount, monster.X, monster.Y);
                }
            }
            if (_itemPool != null)
            {
                foreach (RolledItem rolled in ItemDrop.Roll(_dropTable, _itemPool, context, _itemRandom))
                {
                    SpawnItem(rolled.Info, monster.X, monster.Y);
                }
            }
        }

        /// <summary>在 (<paramref name="x"/>, <paramref name="y"/>) 抛出一件物品(落点与物理同金币,见 <see cref="FieldDrop.LaunchItem"/>);怪物死亡的物品掉落走这里。</summary>
        public FieldDrop SpawnItem(ItemInfo item, double x, double y)
        {
            FieldDrop drop = FieldDrop.LaunchItem(++_dropSerial, item, x, y, _itemRandom, CanStandAt);
            _drops.Add(drop);
            ItemsDropped++;
            return drop;
        }

        /// <summary>在 (<paramref name="x"/>, <paramref name="y"/>) 抛出一堆 <paramref name="amount"/> 金币(落点在周围的可站处,见 <see cref="FieldDrop.Launch"/>);怪物死亡的掉落走这里。</summary>
        public FieldDrop SpawnGold(int amount, double x, double y)
        {
            FieldDrop drop = FieldDrop.Launch(++_dropSerial, amount, x, y, _dropRandom, CanStandAt);
            _drops.Add(drop);
            GoldDropped++;
            GoldDroppedAmount += amount;
            return drop;
        }

        private bool CanStandAt(double x, double y)
        {
            IWalkArea walk = _room.Hero.Walk;
            return walk == null || walk.IsUnrestricted || walk.Contains(x, y);
        }

        /// <summary>
        /// 推进地上的东西与飘字:受重力落地;金币在主角活着、走到它的范围里就捡起——加到身上(有进度时)与本房间的统计、放拾取声音、在头顶升起金额,
        /// 飘字存满寿命就清掉,一个小步里同时够得着的几堆都会捡;物品不自动捡,只重算被选中的那一件(见 <see cref="SelectedDrop"/>、<see cref="TryPickupSelected"/>)。
        /// </summary>
        private void UpdateDrops(double deltaMs, TownHero hero)
        {
            for (int i = _gains.Count - 1; i >= 0; i--)
            {
                _gains[i].AgeMs += deltaMs;
                if (_gains[i].Finished)
                {
                    _gains.RemoveAt(i);
                }
            }
            for (int i = 0; i < _drops.Count; i++)
            {
                FieldDrop drop = _drops[i];
                drop.Advance(deltaMs);
                if (HeroDead || drop.IsItem || !drop.IsInReach(hero.X, hero.Y, hero.Z))
                {
                    continue;      // 物品要按攻击键才捡(见 TryPickupSelected),这里只自动捡金币
                }
                _drops.RemoveAt(i--);
                GoldPicked++;
                GoldEarned += drop.Amount;
                LastGoldPickup = drop.Amount;
                Progress?.AddGold(drop.Amount);
                _sounds.Add(FieldDrop.SoundFor(drop.Amount), SoundCueKind.Effect);
                _gains.Add(new GainPopup(drop.Amount, hero.X, hero.Y, (int)Math.Round(hero.Z)));
            }
            _selectedDrop = HeroDead ? null : FindSelectable(hero);
        }

        /// <summary>
        /// 够得着的物品里离主角最近的一件(横向距离加四倍进深距离,同样远取先掉的);背包放不下、已经提示过的(<see cref="FieldDrop.Blocked"/> 且背包还是放不下)不选,
        /// 免得主角站在它旁边攻击键永远被吃掉。没有返回 null。
        /// </summary>
        private FieldDrop FindSelectable(TownHero hero)
        {
            FieldDrop best = null;
            double bestDistance = double.MaxValue;
            foreach (FieldDrop drop in _drops)
            {
                if (!drop.IsItem || !drop.IsInReach(hero.X, hero.Y, hero.Z))
                {
                    continue;
                }
                if (drop.Blocked && Progress != null && !Progress.Bag.CanAdd(drop.Item))
                {
                    continue;
                }
                double distance = Math.Abs(drop.X - hero.X) + 4.0 * Math.Abs(drop.Y - hero.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = drop;
                }
            }
            return best;
        }

        /// <summary>
        /// 攻击键的"拾取"用法(证据 E-122;推断):有被选中的物品时,这一下按键捡它而不出招。背包放得下就放进去、放拾取声音;
        /// 放不下就留在地上、只记一次(第一下被吃掉,之后它不再被选中,攻击键照常出招)。没有被选中的物品返回 false(按键照常出招)。
        /// </summary>
        private bool TryPickupSelected(TownHero hero)
        {
            FieldDrop drop = FindSelectable(hero);
            if (drop == null)
            {
                return false;
            }
            if (Progress != null && !Progress.Bag.CanAdd(drop.Item))
            {
                if (!drop.Blocked)
                {
                    drop.Blocked = true;
                    ItemsBagFull++;
                }
                return true;
            }
            _drops.Remove(drop);
            ItemsPicked++;
            LastItemPickup = drop.Item;
            Progress?.AddItem(drop.Item);
            _sounds.Add(drop.PickupSound, SoundCueKind.Effect);
            _selectedDrop = FindSelectable(hero);
            return true;
        }

        /// <summary>
        /// 给主角加经验并升级(<see cref="HeroProgress.Gain"/>);升了级就换上新等级的属性、放升级声音与光效。
        /// 没有等级进度时返回全 0 的结果。
        /// </summary>
        public ExpGainResult GainExp(int exp)
        {
            HeroProgress progress = Progress;
            if (progress == null)
            {
                return default;
            }
            ExpGainResult result = progress.Gain(exp);
            if (exp > 0)
            {
                ExpEarned += exp;
            }
            if (result.LeveledUp)
            {
                LevelUps += result.LevelsGained;
                SyncProgress();
                _sounds.Add(LevelUpSound, SoundCueKind.Ui);
                SpawnLevelUpEffect();
            }
            else
            {
                _progressVersion = progress.Version;   // 只涨了经验:属性没变,不用换
            }
            return result;
        }

        /// <summary>
        /// 把等级进度里的当前等级同步到这个房间的主角:生命魔法上限、防御、受击恢复(<see cref="HeroVitals.ApplyStats"/>)与攻击面板换成这个等级的,
        /// 当前生命与魔法不变。进度没变(或没有进度)时什么也不做。副本换房间时由 <see cref="DungeonSession"/> 先调它再抄生命魔法。
        /// </summary>
        public void SyncProgress()
        {
            HeroProgress progress = Progress;
            if (progress == null)
            {
                return;
            }
            if (_equipVersion != progress.EquipVersion)
            {
                _equipVersion = progress.EquipVersion;
                ApplyEquipment(progress);
            }
            if (_progressVersion == progress.Version)
            {
                return;
            }
            _progressVersion = progress.Version;
            if (Vitals == null)
            {
                return;
            }
            StatBlock stats = progress.Stats;
            Vitals.ApplyStats(HeroVitals.FromStats(progress.Level, stats));
            if (_heroPanel != null)
            {
                _heroPanel = HeroPanel.FromStats(progress.Level, stats, _heroPanel.Equipment);
                _attacker = _heroPanel.ToAttacker();
            }
        }

        /// <summary>
        /// 装备栏变了(证据 E-118):装备栏里的武器换了就换主角的外观(没穿武器退回起手的演示武器),
        /// 并重取跟武器走的常量(命中停顿、命中延迟倍率、挥砍与命中声音);攻击面板的装备加成按装备栏重算(外观里的武器被装备栏的顶掉)。
        /// 新武器读不出外观(没有这个职业的动画图层)就保留原来的外观与常量。
        /// </summary>
        private void ApplyEquipment(HeroProgress progress)
        {
            TownHero hero = _room.Hero;
            string want = progress.WeaponPath ?? DemoHero.StarterWeapon;
            if (!HeroLook.SameEquipPath(want, HeroLook.WeaponPathOf(hero.Outfit)))
            {
                HeroLook.TrySwapWeapon(_assets, hero, want);
            }
            string have = HeroLook.WeaponPathOf(hero.Outfit);
            if (!HeroLook.SameEquipPath(have, _derivedWeapon))
            {
                _derivedWeapon = have;
                CharacterDef chr = Character;
                if (chr != null)
                {
                    bool has = TryWeaponHit(chr, hero, out WeaponHitInfo weapon);
                    WeaponStunMs = has ? weapon.StunMs : 0;
                    WeaponDelayRate = has ? weapon.DelayRate : -1f;
                    TryWeaponSound(chr, hero, out WeaponSound sound);
                    _weaponSound = sound;
                }
            }
            if (_heroPanel != null)
            {
                EquipmentStats worn = HeroPanel.WornEquipment(_assets, hero, progress);
                _heroPanel = new HeroPanel(_heroPanel.Level, _heroPanel.PhysicalAttack, _heroPanel.MagicalAttack, worn);
                _attacker = _heroPanel.ToAttacker();
            }
        }

        private void SpawnLevelUpEffect()
        {
            TownHero hero = _room.Hero;
            AnimatedEffect effect = AnimatedEffect.Create(_assets, LevelUpEffectPath, hero.X, hero.Y, hero.Z, hero.FacingLeft);
            if (effect != null)
            {
                _effects.Add(effect);
                _followEffects.Add(effect);
            }
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
            bool hurtable = !HeroInvincible;   // 无敌时攻击框照常走完(评估到的帧推进),只是打不中
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
                    if (hurtable && strike.Count > 0 && TryHit(strike, HeroHurtBoxes.At(hero), out Aabb hit))
                    {
                        m.HitHeroThisSwing = true;
                        StrikeHero(m, hero, hit);
                        m.NotifyAttackSuccess(_heroEntity);          // 脚本的 [ON ATTACKSUCCESS](E-130)
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
            if (Vitals == null || !Vitals.IsAlive || _projectiles.Count == 0 || HeroInvincible)
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

        // ------------------------------------------------------------------ .act 触发器与被动对象(M36-4,证据 E-130)

        /// <summary>场上同时最多的被动对象个数:脚本写坏了(无限再生成)时的保险,正常的房间远用不到(推断)。</summary>
        public const int MaxPassives = 64;

        /// <summary>
        /// 接上 .act 程序库:每只怪物的每一招(.mob 的 [attack action]/[action list] 指向的 .act)里有触发器的,装给怪物;
        /// 同时预读它们会放出来的被动对象。传 null 把脚本触发器全部撤掉(怪物只用 .mob/.ai 的行为)。
        /// </summary>
        public void AttachActs(ActLibrary library)
        {
            _acts = library;
            _passives.Clear();
            if (library == null)
            {
                _actWorld = null;
                _heroEntity = null;
                foreach (MonsterActor m in _monsters)
                {
                    m.ActTarget = null;
                    m.SetActs(null, null);
                }
                return;
            }
            _heroEntity = new HeroActEntity(_room.Hero, () => !HeroDead, () => Vitals?.Hp ?? 0, () => Vitals?.HpMax ?? 0);
            _actWorld = new ActWorld(this);
            foreach (MonsterActor m in _monsters)
            {
                IReadOnlyList<string> paths = m.Model.AttackActionPaths;
                var programs = new ActProgram[paths.Count];
                bool any = false;
                for (int i = 0; i < programs.Length; i++)
                {
                    ActProgram p = library.Program(paths[i]);
                    if (p != null && p.Triggers.Count > 0)
                    {
                        programs[i] = p;
                        any = true;
                        library.Preload(p);
                    }
                }
                m.ActTarget = _heroEntity;
                m.SetActs(any ? programs : null, any ? _actWorld : null);
            }
        }

        /// <summary>被动对象各走一步(这一步里新放出来的从下一步起走)。</summary>
        private void UpdatePassives(double deltaMs)
        {
            int count = _passives.Count;
            for (int i = 0; i < count; i++)
            {
                _passives[i].Update(deltaMs);
            }
        }

        /// <summary>
        /// 被动对象打主角:当前帧的攻击框与主角的受击框相交就算打中(规则与投射物同,E-103);一个对象打中一次之后不再打,
        /// 直到脚本 [ATTACKRECT] [RESET]。命中后唤醒对象自己带 [ON ATTACKSUCCESS] 的触发器(常见写法是接着销毁自己或切动作)。
        /// 伤害按链条上最早那只怪物的等级与攻击力算,攻击信息取 .obj 的 [attack info]。
        /// </summary>
        private void ResolvePassiveHits()
        {
            if (Vitals == null || !Vitals.IsAlive || _passives.Count == 0 || HeroInvincible)
            {
                return;
            }
            TownHero hero = _room.Hero;
            List<Aabb> hurt = HeroHurtBoxes.At(hero);
            for (int i = 0; i < _passives.Count; i++)
            {
                PassiveObject o = _passives[i];
                if (o.Finished)
                {
                    continue;
                }
                List<Aabb> strike = o.AttackBoxes();
                if (strike.Count == 0 || !TryHit(strike, hurt, out Aabb hit))
                {
                    continue;
                }
                PassiveHits++;
                StrikeHero(o.Owner, o.Template.Strike, o.FacingLeft, hero, null, hit);
                o.NotifyHit(_heroEntity);
                if (!Vitals.IsAlive)
                {
                    break;
                }
            }
        }

        private void SpawnPassive(IActEntity owner, int index, float x, float y, float z, bool facingLeft)
        {
            if (_acts == null || _passives.Count >= MaxPassives)
            {
                return;
            }
            MonsterActor root = MonsterActor.From(owner);
            PassiveTemplate template = root == null ? null : _acts.Passive(index);
            if (template == null)
            {
                return;
            }
            _passives.Add(new PassiveObject(template, owner, root, _actWorld, x, y, z, facingLeft));
            PassivesSpawned++;
        }

        /// <summary>运行器自己不认识的选择器:敌人、角色队伍、主角这几种落到主角;己方、怪物队伍落到活着的怪物;被动对象落到场上的对象。其余这一版不接。</summary>
        private void ResolveSelector(ActSelector selector, List<IActEntity> into)
        {
            switch (selector)
            {
                case ActSelector.AllEnemy:
                case ActSelector.AllCharacterTeam:
                case ActSelector.Character:
                case ActSelector.Player:
                case ActSelector.AiCharacter:
                case ActSelector.PartyTarget:
                    if (_heroEntity != null && _heroEntity.IsAlive)
                    {
                        into.Add(_heroEntity);
                    }
                    break;
                case ActSelector.AllOurTeam:
                case ActSelector.AllMonsterTeam:
                case ActSelector.Monster:
                    foreach (MonsterActor m in _monsters)
                    {
                        if (m.IsAlive && m.Phase != MonsterPhase.Gone)
                        {
                            into.Add(m.ActSelf);
                        }
                    }
                    break;
                case ActSelector.Passive:
                    foreach (PassiveObject o in _passives)
                    {
                        if (!o.Finished)
                        {
                            into.Add(o);
                        }
                    }
                    break;
            }
        }

        /// <summary>脚本运行器眼里的房间:随机数、生成被动对象、解析选择器。</summary>
        private sealed class ActWorld : IActHost
        {
            private readonly RoomCombatSession _session;

            public ActWorld(RoomCombatSession session)
            {
                _session = session;
            }

            public int Random(int n)
            {
                return n <= 1 ? 0 : _session._actRandom.Next(n);
            }

            public long NowMs => (long)_session._actClockMs;

            public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
            {
                _session.SpawnPassive(owner, index, x, y, z, facingLeft);
            }

            public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
            {
                _session.ResolveSelector(selector, into);
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
            if (MonsterDamageScale > 1)
            {
                damage = (int)Math.Min(int.MaxValue, (long)damage * MonsterDamageScale);
            }
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
            foreach (PassiveObject o in _passives)
            {
                SceneSprite s = o.Sprite(_assets, projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            foreach (FieldDrop d in _drops)
            {
                SceneSprite s = d.Sprite(_dropSprites, projection);
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

        /// <summary>
        /// 换掉名牌用的画笔(带字体)与文字表,不再看全进程共用的那两份(测试与要自己管字体的宿主用);都给 null 就回到共用的。
        /// </summary>
        public void UseDropLabels(OverlayArt art, WorldTexts texts)
        {
            _labelArt = art;
            _labelTexts = texts;
            _dropLabels = null;
        }

        /// <summary>地上物品与金币的名牌(证据 E-122);没有字体或文字表时什么也不画。</summary>
        private void AddDropLabels(List<SceneSprite> into, ref int order, TownProjection projection)
        {
            OverlayArt art = _labelArt ?? _art;
            WorldTexts texts = _labelTexts ?? WorldTexts.Shared;
            if (texts == null || _drops.Count == 0 || !art.HasFonts)
            {
                return;
            }
            if (_dropLabels == null)
            {
                _dropLabels = new DropLabels(art);
            }
            int before = into.Count;
            DropLabelsDrawn += _dropLabels.Add(into, ref order, _drops, _dropSprites, projection, _selectedDrop, texts);
            for (int i = before; i < into.Count; i++)
            {
                if (into[i].AnimationPath == "drop-label-selected")
                {
                    SelectedLabelFrames++;
                    break;
                }
            }
        }

        private void AddOverlay(List<SceneSprite> into, TownProjection projection)
        {
            int order = 0;
            AddDropLabels(into, ref order, projection);
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
                    AddDeathOverlay(into, ref order);
                }
                else
                {
                    AddHeroPlate(into, ref order, projection);
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
            foreach (GainPopup g in _gains)
            {
                // 拾取金币的飘字:原版的位图字(证据 E-116);图包缺帧时退回占位文字
                if (g.Build(into, ref order, _dropSprites, projection))
                {
                    continue;
                }
                int alpha = g.AlphaAt(g.AgeMs);
                if (alpha > 0)
                {
                    SpriteFrame gain = _art.Text(g.Text, 3, OverlayArt.Yellow);
                    int gx = (int)Math.Floor(g.WorldX) - projection.CameraX - gain.Width / 2;
                    int gy = (int)Math.Floor(g.WorldY) + projection.GroundY - g.Z - GainPopup.HeadHeight - g.RiseAt(g.AgeMs);
                    into.Add(OverlayArt.Place(gain, gx, gy, order++, "gain", alpha));
                }
            }
            int shown = _combo.Shown;
            if (shown >= ComboCounter.ShowFrom && (_comboArt == null || !_comboArt.Build(shown, _combo.AgeMs, into, ref order)))
            {
                // 图包里没有连击图(或缺帧)时的占位文字
                SpriteFrame combo = _art.Text(shown.ToString(System.Globalization.CultureInfo.InvariantCulture) + " HIT", 4, OverlayArt.Orange);
                into.Add(OverlayArt.Place(combo, 800 - combo.Width - 24, 236, order++, "combo"));
            }
        }

        /// <summary>
        /// 主角死后的画面(证据 E-115):有复活对象时,窗口开着画倒计时、提示与币存量(<see cref="ReviveArt"/>),窗口没开(还在倒地、或副本已失败)什么也不画;
        /// 没有复活对象(老行为)仍是占位的大号 DEAD。
        /// </summary>
        private void AddDeathOverlay(List<SceneSprite> into, ref int order)
        {
            HeroRevive revive = Revive;
            if (revive == null)
            {
                SpriteFrame dead = _art.Text("DEAD", 8, HeroPopupColor);
                into.Add(OverlayArt.Place(dead, (800 - dead.Width) / 2, 220, order++, "hero-dead"));
                return;
            }
            if (!revive.Open)
            {
                return;
            }
            int stock = revive.Coins == null ? 0 : revive.Coins.Count;
            if (_reviveArt == null || !_reviveArt.Build(into, ref order, _art, revive.Seconds, revive.Verdict, stock))
            {
                ReviveArt.BuildPlaceholder(into, ref order, _art, revive.Seconds, revive.Verdict, stock);
            }
        }

        /// <summary>
        /// 主角头顶的名牌 "Lv.等级 名字"(M30,证据 E-122):只在有汉字字体的运行里画;主角倒下后不画。
        /// </summary>
        private void AddHeroPlate(List<SceneSprite> into, ref int order, TownProjection projection)
        {
            if (!_art.HasFonts)
            {
                return;
            }
            TownHero hero = Hero;
            NamePlates.AddHero(into, ref order, _art, Vitals.Level, CharacterRoster.LocalName,
                (int)Math.Floor(hero.X) - projection.CameraX, (int)Math.Floor(hero.Y) + projection.GroundY);
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
        /// 原版底栏(<see cref="HudArt"/>,证据 E-088):血球、魔法球、等级牌、SP 数字与 A S D 技能槽(能放亮;冷却中、主角腾不出手或魔法不够时灰,冷却中另盖遮罩,证据 E-112)。
        /// 技能槽按 <see cref="Skills"/> 的次序对应 A、S、D。
        /// </summary>
        private void AddHud(List<SceneSprite> into, ref int order)
        {
            HeroVitals v = Vitals;
            // 经验条(证据 E-111):有等级进度才画;分子是本级已攒的经验,分母是本级一共要攒的,等级 85 盖满级图
            HudExp? exp = Progress == null ? (HudExp?)null : new HudExp(Progress.ExpInLevel, Progress.LevelSpan, Progress.Level == Leveling.MaxLevel);
            _hud.Build(into, ref order, v.Level, v.Hp, v.HpMax, v.Mp, v.MpMax, SkillSlotStates(), exp, Progress?.SkillPoints, ItemSlotStates());
        }

        /// <summary>
        /// 物品快捷槽此刻的显示状态(证据 E-120):快捷栏里每个放了药水的格子——图标、背包里这种药水的件数、冷却还剩的比例。没有等级进度(没有背包)时返回空表。
        /// </summary>
        public IReadOnlyList<HudItemState> ItemSlotStates()
        {
            return HudStates.ItemSlots(Progress);
        }

        /// <summary>从创建起这个房间会话里成功用掉几件消耗品(日志与检查用)。</summary>
        public int ItemsUsed { get; private set; }

        /// <summary>最近一次用消耗品的结果(没用过是 <see cref="UseResult.Ok"/>;日志与检查用)。</summary>
        public UseResult LastUse { get; private set; }

        /// <summary>
        /// 用快捷栏第 <paramref name="slot"/> 格的消耗品(键 1 到 6;证据 E-120):规则见 <see cref="HeroProgress.UseQuickSlot"/>,
        /// 回复发在主角的生命值对象上(<see cref="HeroVitals.StartRecovery"/>)。没有等级进度(没有背包)或没有生命值对象(无伤害模式)返回 <see cref="UseResult.Unavailable"/>。
        /// </summary>
        public UseResult TryUseQuickSlot(int slot)
        {
            return Progress == null ? NoteUse(UseResult.Unavailable) : NoteUse(Progress.UseQuickSlot(slot, Vitals));
        }

        /// <summary>用这件物品(背包窗口里按回车):同 <see cref="TryUseQuickSlot"/>,只是物品由调用方指定。</summary>
        public UseResult TryUseItem(ItemInfo item)
        {
            return Progress == null ? NoteUse(UseResult.Unavailable) : NoteUse(Progress.UseConsumable(item, Vitals));
        }

        private UseResult NoteUse(UseResult result)
        {
            LastUse = result;
            if (result == UseResult.Ok)
            {
                ItemsUsed++;
            }
            return result;
        }

        /// <summary>
        /// 技能槽此刻的显示状态(证据 E-112):主角此刻腾不出手(挥击、放技能、受伤、腾空、倒下)或魔法不够时,图标变灰、小字变灰;
        /// 只是冷却中,图标变灰、小字仍是金色,另按剩余比例盖冷却遮罩。没有生命值对象(无伤害模式)时没有底栏,返回空表。
        /// </summary>
        public IReadOnlyList<HudSkillState> SkillSlotStates()
        {
            HeroVitals v = Vitals;
            var states = new List<HudSkillState>(Skills.Slots.Count);
            if (v == null)
            {
                return states;
            }
            bool free = v.IsAlive && Hero.CanBeginSkill;
            for (int i = 0; i < Skills.Slots.Count; i++)
            {
                HeroSkillSlot s = Skills.Slots[i];
                bool castable = free && v.Mp >= s.ConsumeMp;
                states.Add(new HudSkillState(s.Position, s.Def, s.Ready && castable, s.CooldownFraction, s.Level, !castable));
            }
            return states;
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
            bool heroVitals = true, HeroProgress progress = null)
        {
            RoomSession room = DemoRoom.CreateSession(assets, mapPath, progress?.WeaponPath ?? weaponEqu);   // 装备栏里穿着武器就用它(证据 E-118)
            return RoomCombatSession.Create(assets, room, seed, heroVitals, progress);
        }
    }
}
