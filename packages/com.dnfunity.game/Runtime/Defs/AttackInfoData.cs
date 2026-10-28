using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dnf.Game
{
    /// <summary>[attack type]:物理、魔法、无视防御。数值与脚本里的 ATTACKTYPE_PHYSICAL/MAGICAL/ABSOLUTE 一致。</summary>
    public enum AttackKind : byte
    {
        Physic = 0,
        Magic = 1,
        IgnoreDefense = 2,
    }

    /// <summary>[damage reaction]:被击中后的反应。默认是 Damage。</summary>
    public enum DamageReaction : byte
    {
        None = 0,
        Damage = 1,
        Down = 2,
        DamageExceptHuman = 3,
    }

    /// <summary>[attack direction]:击中后把目标往哪个方向打。数值与脚本里的 ATTACK_DIRECTION_UP/HORIZON/DOWN 一致。默认是水平。</summary>
    public enum AttackDirection : byte
    {
        Up = 0,
        Horizon = 1,
        Down = 2,
    }

    /// <summary>[hit info] 的第一个关键字:命中效果的类别。5 表示文件没有写(或写错了)。</summary>
    public enum HitKind : byte
    {
        Blow = 0,
        Cut = 1,
        Burn = 2,
        Etc = 3,
        ManaBurn = 4,
        Unspecified = 5,
    }

    /// <summary>[hit direction]:目标被打向哪一侧。数值与脚本里的 HIT_DIRECTION_* 一致。</summary>
    public enum HitDirection : byte
    {
        Auto = 0,
        Front = 1,
        Back = 2,
        Outer = 3,
        Inner = 4,
    }

    /// <summary>元素:数值与脚本里的 ENUM_ELEMENT_* 一致(火 0、水 1、暗 2、光 3、无 4)。</summary>
    public enum ElementKind : byte
    {
        Fire = 0,
        Water = 1,
        Dark = 2,
        Light = 3,
        None = 4,
    }

    /// <summary>异常状态类型:数值与脚本里的 ACTIVESTATUS_* 一致;18 表示"没有"(默认值,或名字不认识)。</summary>
    public enum ActiveStatusKind : byte
    {
        Slow = 0,
        Freeze = 1,
        Poison = 2,
        Stun = 3,
        Curse = 4,
        Blind = 5,
        Lightning = 6,
        Stone = 7,
        Sleep = 8,
        Burn = 9,
        WeaponBreak = 10,
        Bleeding = 11,
        Haste = 12,
        Bless = 13,
        Element = 14,
        Confuse = 15,
        Hold = 16,
        ArmorBreak = 17,
        None = 18,
    }

    /// <summary>一个 [active status] 槽:一份攻击信息最多带三个。</summary>
    public sealed class ActiveStatusEntry
    {
        public ActiveStatusKind Kind { get; internal set; } = ActiveStatusKind.None;

        /// <summary>
        /// 命中后挂上的概率,单位百分比;写成浮点 1000 表示"必中"(客户端 0x008184e0 与 0x0081efc0 对这个值有特判;证据 E-136)。
        /// 受害者有抗性时再乘 (1 − 抗性),状态等级不为 0 时还要按受害者等级差修正(<see cref="ActiveStatusFormulas.LevelAdjustedProbability"/>)。
        /// </summary>
        public float Probability { get; internal set; }

        /// <summary>
        /// 第二个整数:状态等级(服务端读槽函数 0x088c1e68 与客户端读槽函数 0x012d9d40 都把它放在概率与持续时间之间)。
        /// 与受害者等级相比,决定概率、持续时间与部分附加参数的等级差修正;0 表示不修正(客户端的挂状态入口对 0 直接拒绝,
        /// 这里把 0 当成"没写等级"照常挂上,是推断,见 E-136)。
        /// </summary>
        public int Level { get; internal set; }

        /// <summary>第三个整数:持续时间,毫秒(读槽函数里它在概率与等级之后,走 +0x234 一列;为 0 时用数值表里的默认秒数,证据 E-136)。</summary>
        public int Duration { get; internal set; }

        /// <summary>个别类型后面跟着的附加整数(中毒、灼伤、出血等的伤害参数),个数由类型决定。</summary>
        public int[] Extra { get; internal set; } = Array.Empty<int>();

        /// <summary>手工造一个槽(单元测试与合成数据用;加载器自己用属性赋值)。</summary>
        public static ActiveStatusEntry Create(ActiveStatusKind kind, float probability, int level, int duration, params int[] extra)
        {
            return new ActiveStatusEntry { Kind = kind, Probability = probability, Level = level, Duration = duration, Extra = extra ?? Array.Empty<int>() };
        }
    }

    /// <summary>
    /// [catch object] 里的一组(两个整数加一个浮点)。含义来自客户端怪物受击入口开头的抓取判定(证据 E-058):
    /// First 与调用点给的常数 1 比较;Second 与受击方 +0x254(推断是怪物编号)比较;
    /// Third 是血量百分比阈值,条件是 当前血量 × 100 / 最大血量 ≤ Third。三项都满足就抓到(怪物进入 STATE_UNSUMMON,推断)。
    /// </summary>
    public readonly struct CatchObjectEntry
    {
        public int First { get; }
        public int Second { get; }
        public float Third { get; }

        public CatchObjectEntry(int first, int second, float third)
        {
            First = first;
            Second = second;
            Third = third;
        }
    }

    /// <summary>
    /// 一个 .atk 文件读出来的静态攻击信息。字段名是我们自己起的,标签与取值范围依据 E-047(原版加载函数 + 全库统计 + 脚本枚举头文件三方核对)。
    /// 默认值是原版"清空"函数给的值(加载前先填);文件没写的标签保持默认。
    /// 伤害公式用到这些字段的方式属于战斗阶段(P5),这里只保存读到的值。
    /// </summary>
    public sealed class AttackInfoData
    {
        public const int StatusSlots = 3;

        /// <summary>[damage]:浮点,默认 0。</summary>
        public float Damage { get; internal set; }

        /// <summary>[damage increase rate]:浮点,默认 0(原版的清空函数没有给它赋值,这里按 0 处理)。</summary>
        public float DamageIncreaseRate { get; internal set; }

        /// <summary>[absolute damage]:整数,默认 0。</summary>
        public int AbsoluteDamage { get; internal set; }

        /// <summary>[damage bonus]:整数,默认 0。</summary>
        public int DamageBonus { get; internal set; }

        public AttackKind AttackType { get; internal set; } = AttackKind.Physic;

        /// <summary>[weapon damage apply]:默认 false。</summary>
        public bool WeaponDamageApply { get; internal set; }

        /// <summary>
        /// [human active status rate]:文件里的数除以 100 得到的比例,默认 1.0。客户端只在对战一族的玩法(玩法模式 4、5、7、9)且受害者是玩家时,
        /// 拿它乘 [active status] 槽的概率、持续时间与附加参数(0x008260f0 里的判断 0x0075c6d0);副本里不起作用(证据 E-136)。
        /// </summary>
        public float HumanActiveStatusRate { get; internal set; } = 1f;

        public DamageReaction Reaction { get; internal set; } = DamageReaction.Damage;

        /// <summary>[push aside]:整数,默认 0(与 [knuck back]、[lift up] 一起构成击退/击飞,脚本里的 backForce/upForce 对应它们,推断)。</summary>
        public int PushAside { get; internal set; }

        /// <summary>[lift up]:整数,默认 0。</summary>
        public int LiftUp { get; internal set; }

        /// <summary>[critical hit]:浮点,默认 0。</summary>
        public float CriticalHit { get; internal set; }

        /// <summary>[stuck]:浮点,默认 0(脚本里的 stuckRate 对应它,推断)。</summary>
        public float Stuck { get; internal set; }

        /// <summary>[stuck prob increase]:默认 false。</summary>
        public bool StuckProbIncrease { get; internal set; }

        /// <summary>[knuck back] 的类型:0 普通、1 击退、2 短击退、3 按像素后退、-1 不后退(KNOCK_BACK_TYPE_*)。默认 0。</summary>
        public int KnockBackType { get; internal set; }

        /// <summary>类型为 3 时 [knuck back] 后面多读的一个数(向零取整的像素数);其它类型为 0。</summary>
        public int KnockBackPixels { get; internal set; }

        /// <summary>[weakness]:两个浮点,默认 0、0。</summary>
        public float WeaknessFirst { get; internal set; }

        public float WeaknessSecond { get; internal set; }

        /// <summary>[bounce]:整数,默认 0。</summary>
        public int Bounce { get; internal set; }

        /// <summary>[ignore defense]:默认 false。</summary>
        public bool IgnoreDefense { get; internal set; }

        /// <summary>[hit wav]:命中音效名,默认空。</summary>
        public string HitWav { get; internal set; } = string.Empty;

        public AttackDirection Direction { get; internal set; } = AttackDirection.Horizon;

        /// <summary>[hit info] 的类别,默认 Unspecified。</summary>
        public HitKind HitType { get; internal set; } = HitKind.Unspecified;

        /// <summary>[hit info] 的第二个关键字:[blood] 为 true,[no blood] 为 false;默认 false。</summary>
        public bool HitBlood { get; internal set; }

        /// <summary>
        /// [hit info] 的整数项,默认 -1:命中停顿的毫秒数(客户端把它存进攻击信息的 +0x108,攻击方与受击方各冻结这么久);
        /// -1 或没有 [hit info] 时取角色 .chr 的 [weapon hit info](证据 E-077,静态读码,没有运行时验证)。
        /// </summary>
        public int HitValue { get; internal set; } = -1;

        /// <summary>[hit info] 的浮点项,默认 -1:命中延迟倍率(攻击信息的 +0x114);-1 同样取武器表。受击一侧怎么用它还没读(E-077)。</summary>
        public float HitScale { get; internal set; } = -1f;

        /// <summary>[is use weapon hit delay rate]:默认 false。为真时命中停顿 = 武器表的停顿 × 本文件 [hit info] 的整数(浮点同理),不是取代(E-077)。</summary>
        public bool UseWeaponHitDelayRate { get; internal set; }

        public HitDirection HitSide { get; internal set; } = HitDirection.Auto;

        /// <summary>[ignore super armor]:整数,默认 0。</summary>
        public int IgnoreSuperArmor { get; internal set; }

        /// <summary>[ignore weight]:整数,默认 0。</summary>
        public int IgnoreWeight { get; internal set; }

        /// <summary>[force hit stun time]:整数,默认 0。</summary>
        public int ForceHitStunTime { get; internal set; }

        /// <summary>[HP drain]:默认 false。</summary>
        public bool HpDrain { get; internal set; }

        /// <summary>[MP drain]:默认 false。</summary>
        public bool MpDrain { get; internal set; }

        /// <summary>[is hit number increasing]:默认 true。</summary>
        public bool HitNumberIncreasing { get; internal set; } = true;

        /// <summary>[ignore diehard]:默认 false。</summary>
        public bool IgnoreDiehard { get; internal set; }

        /// <summary>[hit check by screen pos]:默认 false。</summary>
        public bool HitCheckByScreenPos { get; internal set; }

        /// <summary>[max target hit number]:整数,默认 -1。</summary>
        public int MaxTargetHitNumber { get; internal set; } = -1;

        /// <summary>[attack myself]:默认 false。</summary>
        public bool AttackMyself { get; internal set; }

        /// <summary>[attack friend]:默认 false。</summary>
        public bool AttackFriend { get; internal set; }

        /// <summary>[attack enemy]:默认 true。</summary>
        public bool AttackEnemy { get; internal set; } = true;

        /// <summary>[elemental property] 读到的元素集合。空表示无元素。读到"无"会清空,读到具体元素会先去掉"无"再加入。</summary>
        public SortedSet<ElementKind> Elements { get; } = new SortedSet<ElementKind>();

        /// <summary>三个 [active status] 槽,没用到的槽类型为 None。</summary>
        public ActiveStatusEntry[] Statuses { get; } = NewStatuses();

        /// <summary>[human damage rate],默认 100。</summary>
        public float HumanDamageRate { get; internal set; } = 100f;

        /// <summary>[fort damage rate],默认 100。</summary>
        public float FortDamageRate { get; internal set; } = 100f;

        /// <summary>[monster damage rate],默认 100。</summary>
        public float MonsterDamageRate { get; internal set; } = 100f;

        public List<CatchObjectEntry> CatchObjects { get; } = new List<CatchObjectEntry>();

        private static ActiveStatusEntry[] NewStatuses()
        {
            var slots = new ActiveStatusEntry[StatusSlots];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new ActiveStatusEntry();
            }
            return slots;
        }

        /// <summary>
        /// 稳定的一行文本:字段名按字典序,浮点写成 32 位的十六进制位模式。
        /// 与 tools/py/atk_ref.py 的 render 逐字一致,用来对拍两份独立实现(dnf atk-check --dump)。
        /// </summary>
        public string Describe()
        {
            var f = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["absolute_damage"] = I(AbsoluteDamage),
                ["attack_direction"] = I((int)Direction),
                ["attack_enemy"] = B(AttackEnemy),
                ["attack_friend"] = B(AttackFriend),
                ["attack_myself"] = B(AttackMyself),
                ["attack_type"] = I((int)AttackType),
                ["bounce"] = I(Bounce),
                ["critical_hit"] = F(CriticalHit),
                ["damage"] = F(Damage),
                ["damage_bonus"] = I(DamageBonus),
                ["damage_increase_rate"] = F(DamageIncreaseRate),
                ["damage_reaction"] = I((int)Reaction),
                ["force_hit_stun_time"] = I(ForceHitStunTime),
                ["fort_damage_rate"] = F(FortDamageRate),
                ["hit_blood"] = B(HitBlood),
                ["hit_check_by_screen_pos"] = B(HitCheckByScreenPos),
                ["hit_direction"] = I((int)HitSide),
                ["hit_float"] = F(HitScale),
                ["hit_int"] = I(HitValue),
                ["hit_number_increasing"] = B(HitNumberIncreasing),
                ["hit_type"] = I((int)HitType),
                ["hit_wav"] = HitWav,
                ["hp_drain"] = B(HpDrain),
                ["human_active_status_rate"] = F(HumanActiveStatusRate),
                ["human_damage_rate"] = F(HumanDamageRate),
                ["ignore_defense"] = B(IgnoreDefense),
                ["ignore_diehard"] = B(IgnoreDiehard),
                ["ignore_super_armor"] = I(IgnoreSuperArmor),
                ["ignore_weight"] = I(IgnoreWeight),
                ["knock_back"] = I(KnockBackType),
                ["knock_back_param"] = I(KnockBackPixels),
                ["lift_up"] = I(LiftUp),
                ["max_target_hit_number"] = I(MaxTargetHitNumber),
                ["monster_damage_rate"] = F(MonsterDamageRate),
                ["mp_drain"] = B(MpDrain),
                ["push_aside"] = I(PushAside),
                ["stuck"] = F(Stuck),
                ["stuck_prob_increase"] = B(StuckProbIncrease),
                ["use_weapon_hit_delay_rate"] = B(UseWeaponHitDelayRate),
                ["weakness"] = F(WeaknessFirst) + "," + F(WeaknessSecond),
                ["weapon_damage_apply"] = B(WeaponDamageApply),
            };
            var elements = new StringBuilder();
            foreach (ElementKind e in Elements)
            {
                if (elements.Length > 0)
                {
                    elements.Append(',');
                }
                elements.Append(((int)e).ToString(CultureInfo.InvariantCulture));
            }
            f["elements"] = elements.ToString();
            var status = new StringBuilder();
            for (int i = 0; i < Statuses.Length; i++)
            {
                ActiveStatusEntry s = Statuses[i];
                if (i > 0)
                {
                    status.Append(';');
                }
                status.Append(I((int)s.Kind)).Append('/').Append(F(s.Probability)).Append('/').Append(I(s.Level)).Append('/').Append(I(s.Duration)).Append('/')
                    .Append(string.Join(",", Array.ConvertAll(s.Extra, x => I(x))));
            }
            f["status"] = status.ToString();
            var catches = new StringBuilder();
            for (int i = 0; i < CatchObjects.Count; i++)
            {
                if (i > 0)
                {
                    catches.Append(';');
                }
                CatchObjectEntry c = CatchObjects[i];
                catches.Append(I(c.First)).Append('/').Append(I(c.Second)).Append('/').Append(F(c.Third));
            }
            f["catch"] = catches.ToString();

            var line = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in f)
            {
                if (line.Length > 0)
                {
                    line.Append(' ');
                }
                line.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return line.ToString();
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static string B(bool v) => v ? "1" : "0";

        private static string F(float v) => BitConverter.SingleToInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture);
    }
}
