using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 技能的"按等级取值"表:[level info] 的第一个数是每级的列数 N,后面每 N 个数是一级(第 1 级在最前)。
    /// 结构在全库核对过:整数个数减 1 能被 N 整除(见 E-036 的核对);第 N 列叫什么、怎么用由技能脚本决定。
    /// </summary>
    public sealed class SkillLevelTable
    {
        private readonly int[] _values;

        public int Columns { get; }
        public int Levels { get; }

        public SkillLevelTable(int columns, int[] values)
        {
            Columns = columns;
            _values = values;
            Levels = columns > 0 ? values.Length / columns : 0;
        }

        public static readonly SkillLevelTable Empty = new SkillLevelTable(0, Array.Empty<int>());

        /// <summary>取第 level 级(从 1 开始)第 column 列(从 0 开始)的值;超出范围时返回 0。等级超出表长时取最后一级。</summary>
        public int Get(int level, int column)
        {
            if (Levels == 0 || column < 0 || column >= Columns)
            {
                return 0;
            }
            int row = level - 1;
            if (row < 0)
            {
                row = 0;
            }
            if (row >= Levels)
            {
                row = Levels - 1;
            }
            return _values[row * Columns + column];
        }

        /// <summary>整数个数是否能被列数整除(结构自洽)。</summary>
        public bool IsConsistent => Columns > 0 && _values.Length % Columns == 0;
    }

    /// <summary>前置技能的一项:要先学到某个技能的某一级以上([pre required skill] 的一对数)。</summary>
    public readonly struct SkillPrereq
    {
        public int SkillId { get; }
        public int Level { get; }

        public SkillPrereq(int skillId, int level)
        {
            SkillId = skillId;
            Level = level;
        }
    }

    /// <summary>技能脚本里的文字引用(种类编号 + 键;原文在 skill.kor.str 里,运行时才取,证据 E-121)。</summary>
    public readonly struct SkillText
    {
        public SkillText(int kind, string key)
        {
            Kind = kind;
            Key = key;
        }

        public int Kind { get; }

        public string Key { get; }

        public bool IsEmpty => string.IsNullOrEmpty(Key);
    }

    /// <summary>
    /// 一个 .skl 技能脚本里与运行时有关的数据:静态数据([static data])、按等级取值表([level info]),
    /// 以及决斗场([pvp])里的同名两项、可以使用的状态、冷却与耗蓝的原始数字。
    /// 数字的单位与换算(耗蓝两个数、冷却两个数分别是什么)**未验证**,这里按原样保存。
    /// </summary>
    public sealed class SkillDef
    {
        public int Id { get; }

        /// <summary>规范化的 PVF 路径,例如 skill/atmage/icecrash.skl。</summary>
        public string Path { get; }

        public int[] StaticData { get; }
        public SkillLevelTable LevelInfo { get; }
        public int[] PvpStaticData { get; }
        public SkillLevelTable PvpLevelInfo { get; }

        /// <summary>
        /// [executable states]:技能可以从哪些状态编号发出(没有这个标签时为空,表示不限制)。
        /// 同名标签可以出现多次(观察:2008 个 .skl 里 10 个出现两次、1 个三次;第二次总是 0 14,前一次含 8),
        /// 这里取全部出现的并集(推断:各次取值叠加,而不是后一次覆盖前一次)。
        /// </summary>
        public int[] ExecutableStates { get; }

        public int[] ConsumeMp { get; }
        public int[] CoolTime { get; }

        /// <summary>
        /// [cooltime level info]:冷却取 [level info] 的哪一列(证据 E-163:服务端导入函数 <c>0x08a9792f</c> 把这个数读进脚本结构,客户端载入函数 <c>0x012d6390</c> 写进受保护整数;
        /// 没有这个标签为 -1)。带这个标签的技能都没有 [cool time],该列的值随等级递减,量级与毫秒相符——取列值当毫秒是推断(消费这个字段的函数没找到)。
        /// </summary>
        public int CoolTimeColumn { get; }

        /// <summary>[casting time]:两个数(推断:第 1 级与最高级的施法时间,毫秒);没有这个标签时为空。</summary>
        public int[] CastingTime { get; }
        public int RequiredLevel { get; }
        public int MaximumLevel { get; }

        /// <summary>[type] 的取值,如 "[active]"、"[passive]";没有时为 null。</summary>
        public string Type { get; }

        /// <summary>有 [command](出招指令)标签:主动技能里能放进快捷槽、按指令放出的才有(证据 E-076:新建角色的初始技能表里,恰好是它们占了 A S D 三个快捷槽)。</summary>
        public bool HasCommand { get; }

        /// <summary>出招指令的键序列(.skl 的 [command];没有标签或标签为空时是 <see cref="SkillCommand.Empty"/>,E-140)。</summary>
        public SkillCommand Command { get; }

        /// <summary>
        /// [icon] 的图标图像(原样,例如 "Character/Swordman/Effect/SkillIcon.img";在图包里的路径是前面加 sprite/ 再转小写,证据 E-088);没有 [icon] 时为 null。
        /// </summary>
        public string IconImage { get; }

        /// <summary>[icon] 的第一个帧号:能用时的图标;没有 [icon] 时为 -1。</summary>
        public int IconFrame { get; }

        /// <summary>[icon] 的第二个图像(观察:洛兰初始三个技能里与第一个相同);没有 [icon] 时为 null。</summary>
        public string IconDisabledImage { get; }

        /// <summary>[icon] 的第二个帧号:不能用时(冷却、魔法不够)的灰色图标;没有 [icon] 时为 -1。</summary>
        public int IconDisabledFrame { get; }

        /// <summary>[name] 的文字引用(技能名;没有或形状不对是空引用)。</summary>
        public SkillText Name { get; }

        /// <summary>[explain] 的文字引用(技能说明;没有或形状不对是空引用)。</summary>
        public SkillText Explain { get; }

        /// <summary>主动技能([type] 为 "[active]")。</summary>
        public bool IsActive => string.Equals(Type, "[active]", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// [skill class]:技能类别 0 到 4(服务端 +0x60;没有这个标签是 0)。界面分页由它与"适合的成长类型个数"、是否特殊技能一起决定,见 <see cref="SkillRules.UiGroup"/>。
        /// </summary>
        public int SkillClass { get; }

        /// <summary>
        /// [required level range]:学到第 n 级需要的主角等级 = <see cref="RequiredLevel"/> + 本值 × (n − 1)(证据 E-127,服务端 can_learn);没有这个标签是 0(只一级的技能)。
        /// </summary>
        public int RequiredLevelRange { get; }

        /// <summary>
        /// [growtype maximum level]:六个成长类型各自能学到的最高级(0 = 这个成长类型学不了)。下标 0 是转职前,1 起是各转职方向(与 .chr 的 [growtype 下标+1] 块同序,证据 E-127);
        /// 没有这个标签为空数组(谁也学不了)。
        /// </summary>
        public int[] GrowtypeMaxLevels { get; }

        /// <summary>
        /// [purchase cost]:每一级要花的技能点(第 n 个数是升到第 n 级的花费)。服务端载入后把它用最后一个数补到 <see cref="EffectiveMaxLevel"/> 个,
        /// 所以绝大多数技能只写一个数(每级同价);原样保存,按级取用 <see cref="SkillRules.NeedSp"/>。同一标签出现多次时后一次覆盖前一次。
        /// </summary>
        public int[] PurchaseCost { get; }

        /// <summary>[special purchase cost]:特殊技能(<see cref="IsSpecial"/>)每级要花的"特殊点"(觉醒点);补齐规则同 <see cref="PurchaseCost"/>。</summary>
        public int[] SpecialPurchaseCost { get; }

        /// <summary>[skill fitness growtype]:这个技能"适合"的成长类型(下标);只用它的个数决定界面分页。</summary>
        public int[] FitGrowtypes { get; }

        /// <summary>
        /// 特殊技能:有 [feature skill type] 且值非 0(服务端取这个值的低字节当"特殊"标志,证据 E-127)。它们花的是觉醒点,不是普通技能点。
        /// </summary>
        public bool IsSpecial { get; }

        /// <summary>[pre required skill]:前置技能,"技能编号 最低等级"成对;同一标签出现多次时后一次覆盖前一次(服务端每次先清空)。</summary>
        public IReadOnlyList<SkillPrereq> PreRequired { get; }

        /// <summary>
        /// 有效最高级:服务端载入后把 [maximum level] 抬到不低于任一成长类型的最高级(低于时打警告并改写),所以取两者较大者。
        /// 只用来决定花费表补到多长;学得到几级以成长类型的最高级为准。
        /// </summary>
        public int EffectiveMaxLevel { get; }

        public SkillDef(int id, string path, ScriptLine[] lines)
        {
            Id = id;
            Path = path;
            var table = new TagTable(lines);
            // 这几个标签服务端读到一次就覆盖一次(向量先清空再装,整数直接赋值),所以后出现的为准
            int skillClass = 0;
            int range = 0;
            int[] growtypeMax = Array.Empty<int>();
            int[] purchase = Array.Empty<int>();
            int[] special = Array.Empty<int>();
            int[] fit = Array.Empty<int>();
            bool isSpecial = false;
            SkillText name = default;
            SkillText explain = default;
            var prereq = new List<SkillPrereq>();
            foreach (ScriptLine tagged in lines)
            {
                if (tagged.TagIs("[name]"))
                {
                    name = TextOf(tagged);
                }
                else if (tagged.TagIs("[explain]"))
                {
                    explain = TextOf(tagged);
                }
                else if (tagged.TagIs("[skill class]"))
                {
                    List<int> v = tagged.Ints();
                    skillClass = v.Count > 0 ? v[0] : 0;
                }
                else if (tagged.TagIs("[required level range]"))
                {
                    List<int> v = tagged.Ints();
                    range = v.Count > 0 ? v[0] : 0;
                }
                else if (tagged.TagIs("[growtype maximum level]"))
                {
                    growtypeMax = tagged.Ints().ToArray();
                }
                else if (tagged.TagIs("[purchase cost]"))
                {
                    purchase = tagged.Ints().ToArray();
                }
                else if (tagged.TagIs("[special purchase cost]"))
                {
                    special = tagged.Ints().ToArray();
                }
                else if (tagged.TagIs("[skill fitness growtype]"))
                {
                    fit = tagged.Ints().ToArray();
                }
                else if (tagged.TagIs("[feature skill type]"))
                {
                    List<int> v = tagged.Ints();
                    isSpecial = v.Count > 0 && (v[0] & 0xff) != 0;
                }
                else if (tagged.TagIs("[pre required skill]"))
                {
                    prereq.Clear();
                    List<int> v = tagged.Ints();
                    for (int i = 0; i + 1 < v.Count; i += 2)
                    {
                        prereq.Add(new SkillPrereq(v[i], v[i + 1]));
                    }
                }
            }
            Name = name;
            Explain = explain;
            SkillClass = skillClass;
            RequiredLevelRange = range;
            GrowtypeMaxLevels = growtypeMax;
            PurchaseCost = purchase;
            SpecialPurchaseCost = special;
            FitGrowtypes = fit;
            IsSpecial = isSpecial;
            PreRequired = prereq;
            ExecutableStates = table.IntsUnion("[executable states]");
            ConsumeMp = table.Ints("[consume MP]");
            CoolTime = table.Ints("[cool time]");
            CoolTimeColumn = table.IntOr("[cooltime level info]", -1);
            CastingTime = table.Ints("[casting time]");
            RequiredLevel = table.IntOr("[required level]", 0);
            MaximumLevel = table.IntOr("[maximum level]", 0);
            int effective = MaximumLevel;
            foreach (int m in growtypeMax)
            {
                if (m > effective)
                {
                    effective = m;
                }
            }
            EffectiveMaxLevel = effective;
            Type = table.FirstString("[type]");
            HasCommand = table.Has("[command]");
            Command = table.TryGetLine("[command]", out ScriptLine commandLine) ? SkillCommand.From(commandLine) : SkillCommand.Empty;

            // [icon] "图像" 帧 "图像" 帧:能用时的图标与不能用时的灰图标(形状不对就当没有)
            IconFrame = -1;
            IconDisabledFrame = -1;
            if (table.TryGetLine("[icon]", out ScriptLine iconLine) && iconLine.Values.Length >= 4
                && iconLine.Values[0].Kind == ScriptValueKind.String && iconLine.Values[1].IsNumber
                && iconLine.Values[2].Kind == ScriptValueKind.String && iconLine.Values[3].IsNumber)
            {
                IconImage = iconLine.Values[0].Text;
                IconFrame = iconLine.Values[1].Int;
                IconDisabledImage = iconLine.Values[2].Text;
                IconDisabledFrame = iconLine.Values[3].Int;
            }

            // 按出现顺序走一遍:[pvp] ... [/pvp] 之间的 [static data]/[level info] 属于决斗场,其余属于普通模式
            int[] pveStatic = Array.Empty<int>();
            int[] pvpStatic = Array.Empty<int>();
            SkillLevelTable pveLevels = SkillLevelTable.Empty;
            SkillLevelTable pvpLevels = SkillLevelTable.Empty;
            bool inPvp = false;
            bool haveStatic = false;
            bool haveLevels = false;
            bool havePvpStatic = false;
            bool havePvpLevels = false;
            foreach (ScriptLine line in lines)
            {
                if (line.TagIs("[pvp]"))
                {
                    inPvp = true;
                }
                else if (line.TagIs("[/pvp]"))
                {
                    inPvp = false;
                }
                else if (line.TagIs("[static data]"))
                {
                    int[] values = line.Ints().ToArray();
                    if (inPvp && !havePvpStatic)
                    {
                        pvpStatic = values;
                        havePvpStatic = true;
                    }
                    else if (!inPvp && !haveStatic)
                    {
                        pveStatic = values;
                        haveStatic = true;
                    }
                }
                else if (line.TagIs("[level info]"))
                {
                    List<int> all = line.Ints();
                    if (all.Count == 0)
                    {
                        continue;
                    }
                    int columns = all[0];
                    int[] rest = all.GetRange(1, all.Count - 1).ToArray();
                    var levels = new SkillLevelTable(columns, rest);
                    if (inPvp && !havePvpLevels)
                    {
                        pvpLevels = levels;
                        havePvpLevels = true;
                    }
                    else if (!inPvp && !haveLevels)
                    {
                        pveLevels = levels;
                        haveLevels = true;
                    }
                }
            }
            StaticData = pveStatic;
            PvpStaticData = pvpStatic;
            LevelInfo = pveLevels;
            PvpLevelInfo = pvpLevels;
        }

        private static SkillText TextOf(ScriptLine line)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Keyed)
                {
                    return new SkillText(v.Int, v.Text);
                }
            }
            return default;
        }

        /// <summary>
        /// 按等级取成对数据([casting time]、[cool time]、[consume MP])的值:第 1 级取第一个数,最高级取第二个数,中间线性插值。
        /// 这个对应关系是依据'耗蓝 150 1148、最高级 70'这类数据的形状推断的,没有核对;没有数据返回 0。
        /// </summary>
        public int Interpolate(int[] pair, int level)
        {
            if (pair == null || pair.Length == 0)
            {
                return 0;
            }
            if (pair.Length == 1 || MaximumLevel <= 1)
            {
                return pair[0];
            }
            int l = Math.Max(1, Math.Min(level, MaximumLevel));
            return pair[0] + (int)Math.Round((pair[1] - pair[0]) * (double)(l - 1) / (MaximumLevel - 1));
        }

        public int GetStatic(int index, bool pvp)
        {
            int[] data = pvp && PvpStaticData.Length > 0 ? PvpStaticData : StaticData;
            return (uint)index < (uint)data.Length ? data[index] : 0;
        }

        public int GetLevel(int level, int column, bool pvp)
        {
            SkillLevelTable t = pvp && PvpLevelInfo.Levels > 0 ? PvpLevelInfo : LevelInfo;
            return t.Get(level, column);
        }
    }
}
