using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据(服务端静态读码,没有运行;地址是 df_game_r.orig 的虚拟地址,数值与编号不入库):
// 装备脚本导入函数 ImportEquipmentScript 0x08991aeb:[set item](0x08998f9a 起)把整数读进 vector<int>(this+0x66c);
//   [piece set ability](0x08999036)读一个整数当键,[set ability](0x08999196)键固定为 0,两者都写进同一份 map<int, EquipmentParameterInfo>(this+0x678),
//   其后到收尾标签为止的属性标签读进那份参数结构(与装备自己的 this+0x1c0 同型);[limit upgradable level](0x0899add9 起)里的名字映射
//   "normal upgrade"=1、"amplify upgrade"=2、"separate upgrade"=3、"3rd chronicle upgrade"=4,每个名字后两个整数按"下限、上限"存进 map<类型, pair<int,int>>(this+0x6fc),
//   值为 -1 时下限补 1、上限补 70;CEquipItem::isUpgradableLevel 0x085136ec 在条目存在且 角色等级 < 下限 或 上限 < 角色等级 时返回否。
// 可叠加物品:[enchant] 块、[monster card id](CStackableItem::GetMonsterCardId 0x0849f530)、[equipment reinforcement ticket] / [equipment amplify reinforcement ticket](两个整数)。
// 数据普查(2026-11,本机全库,只记计数):[set item master] 5142 件、[set item] 1195 件(每件只一个,值 1 到 9 个)、[set ability] 1133 块、[piece set ability] 170 块(键只有 3 档)、
//   [limit upgradable level] 114 件、附魔块 847 件(含装备徽章与怪物卡)、强化券 70 件(两个整数 = 目标等级、成功率百分数)、增幅强化券 23 件。
namespace Dnf.Game
{
    /// <summary>"满 N 件"的套装加成(<c>[piece set ability] N</c> 块里的属性标签)。</summary>
    public readonly struct SetPieceBonus
    {
        public SetPieceBonus(int pieces, EquipStats stats)
        {
            Pieces = pieces;
            Stats = stats ?? EquipStats.Empty;
        }

        /// <summary>要穿到几件。</summary>
        public int Pieces { get; }

        /// <summary>块里直接写的属性(嵌在条件、效果子块里的不算)。</summary>
        public EquipStats Stats { get; }
    }

    /// <summary>
    /// 强化券的两个整数(<c>[equipment reinforcement ticket]</c> 与 <c>[equipment amplify reinforcement ticket]</c> 都是这个形状):
    /// 用了之后装备直接变成这个强化等级(服务端 <c>GetReinforceVaule</c> 0 到 31 之外按 0 处理),成功率是百分数(<c>GetReinforceRate</c> 0 到 100 之外按 0)。
    /// 两个整数谁先谁后:全库里第一个整数集中在 1 到 13、第二个整数在 1 到 100(有 1、5、10、15、30、50、90、100 这几档),按"等级在前、成功率在后"读(推断)。
    /// </summary>
    public readonly struct ReinforceTicket
    {
        public ReinforceTicket(int level, int ratePercent)
        {
            Level = level;
            RatePercent = ratePercent;
        }

        /// <summary>直接变成的强化等级(0 到 31 之外读作 0,服务端同样处理)。</summary>
        public int Level { get; }

        /// <summary>成功率百分数(0 到 100 之外读作 0)。</summary>
        public int RatePercent { get; }
    }

    /// <summary>某一种强化方式允许的角色等级区间(<c>[limit upgradable level]</c>):下限、上限都含。</summary>
    public readonly struct UpgradeLevelRange
    {
        public UpgradeLevelRange(int type, int min, int max)
        {
            Type = type;
            Min = min;
            Max = max;
        }

        /// <summary>强化方式:1 普通强化,2 增幅强化,3 分离强化,4 编年史强化(服务端 <c>ItemUpgradeType1T</c>)。</summary>
        public int Type { get; }

        public int Min { get; }

        public int Max { get; }

        /// <summary>角色等级在区间里。</summary>
        public bool Contains(int characterLevel) => characterLevel >= Min && characterLevel <= Max;
    }

    /// <summary>
    /// 物品脚本里"强化、附魔、套装"周边的数据(定义层面,同编号共享;个体状态见 <see cref="EquipInstance"/>)。没写任何这类标签的物品没有这份数据(<see cref="ItemInfo.Enhance"/> 为 null)。
    /// 属性块的读法与 <see cref="EquipStats.Read"/> 是同一套("直接写在块里的属性标签,取前两个数,同名后者覆盖"),只是对象换成某个块里面。
    /// 不读:条件与效果子块([if]/[then] 里的触发效果、技能等级加成)、元素属性、套装徽章(时装部位的套装)、成长型装备的等级线性公式——这些服务端有读法但这一步没有对应的玩法。
    /// </summary>
    public sealed class EnhanceData
    {
        private static readonly int[] NoMembers = new int[0];
        private static readonly SetPieceBonus[] NoBonuses = new SetPieceBonus[0];
        private static readonly UpgradeLevelRange[] NoRanges = new UpgradeLevelRange[0];

        private readonly int[] _members;
        private readonly int[] _enchantParts;
        private readonly SetPieceBonus[] _pieces;
        private readonly UpgradeLevelRange[] _ranges;

        public EnhanceData(
            int setMaster = 0,
            IEnumerable<int> setMembers = null,
            EquipStats setFullBonus = null,
            IEnumerable<SetPieceBonus> setPieceBonuses = null,
            EquipStats enchantStats = null,
            int monsterCardId = 0,
            ReinforceTicket? reinforceTicket = null,
            ReinforceTicket? amplifyTicket = null,
            IEnumerable<UpgradeLevelRange> upgradeRanges = null,
            IEnumerable<int> enchantParts = null)
        {
            _enchantParts = enchantParts == null ? NoMembers : new List<int>(enchantParts).ToArray();
            SetMaster = setMaster;
            _members = setMembers == null ? NoMembers : new List<int>(setMembers).ToArray();
            SetFullBonus = setFullBonus;
            if (setPieceBonuses == null)
            {
                _pieces = NoBonuses;
            }
            else
            {
                var list = new List<SetPieceBonus>(setPieceBonuses);
                list.Sort((a, b) => a.Pieces.CompareTo(b.Pieces));
                _pieces = list.ToArray();
            }
            EnchantStats = enchantStats;
            MonsterCardId = monsterCardId;
            ReinforceTicket = reinforceTicket;
            AmplifyTicket = amplifyTicket;
            _ranges = upgradeRanges == null ? NoRanges : new List<UpgradeLevelRange>(upgradeRanges).ToArray();
        }

        /// <summary><c>[set item master]</c>:这件装备所属套装的"主件"编号(0 表示没写)。</summary>
        public int SetMaster { get; }

        /// <summary><c>[set item]</c>:套装的全部成员编号(只有套装主件上写)。</summary>
        public IReadOnlyList<int> SetMembers => _members;

        /// <summary><c>[set ability]</c> 块里的属性:穿齐整套(成员全部穿上)的加成;没有这个块为 null,有块但没有直接写的属性是空集。</summary>
        public EquipStats SetFullBonus { get; }

        /// <summary><c>[piece set ability] N</c> 块,按件数从小到大;没有为空表。</summary>
        public IReadOnlyList<SetPieceBonus> SetPieceBonuses => _pieces;

        /// <summary>这件物品是套装主件:写了成员表,或者带了任何套装加成块。</summary>
        public bool IsSetMaster => _members.Length > 0 || SetFullBonus != null || _pieces.Length > 0;

        /// <summary>附魔卡的 <c>[enchant]</c> 块里的属性(怪物卡与徽章都用这个块);没有这个块为 null。</summary>
        public EquipStats EnchantStats { get; }

        /// <summary>
        /// 附魔卡能附在哪些部位:卡的 <c>[string data]</c> 里写的部位标签(<c>[weapon]</c>、<c>[ring]</c> 等,图片路径与不是部位的词不算)。
        /// 服务端用 <c>CStackableItem::getUsableItemType(部位)</c> 0x0850d780 判断("部位在卡上的一张整数表里"),那张表的装载函数没有找到;
        /// 本机全库 258 张装备附魔卡每张的 <c>[string data]</c> 都带 1 到 5 个部位标签,按这个读(推断)。没有附魔块的物品为空表。
        /// </summary>
        public IReadOnlyList<int> EnchantParts => _enchantParts;

        /// <summary><c>[monster card id]</c>:附魔宝珠对应的卡片物品编号(服务端 <c>OnEnchantByBead</c> 用宝珠换出卡片);没写为 0。</summary>
        public int MonsterCardId { get; }

        /// <summary><c>[equipment reinforcement ticket]</c>:普通强化券;不是为 null。</summary>
        public ReinforceTicket? ReinforceTicket { get; }

        /// <summary><c>[equipment amplify reinforcement ticket]</c>:增幅强化券;不是为 null。</summary>
        public ReinforceTicket? AmplifyTicket { get; }

        /// <summary><c>[limit upgradable level]</c>:各强化方式允许的角色等级区间;没写为空表(不设限)。</summary>
        public IReadOnlyList<UpgradeLevelRange> UpgradeRanges => _ranges;

        /// <summary>某种强化方式的角色等级区间;没写这种方式返回 null(不设限)。</summary>
        public UpgradeLevelRange? RangeOf(int upgradeType)
        {
            foreach (UpgradeLevelRange range in _ranges)
            {
                if (range.Type == upgradeType)
                {
                    return range;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ 读取

        private const string SetItemMaster = "[set item master]";
        private const string SetItem = "[set item]";
        private const string SetAbility = "[set ability]";
        private const string PieceSetAbility = "[piece set ability]";
        private const string Enchant = "[enchant]";
        private const string MonsterCardIdTag = "[monster card id]";
        private const string ReinforceTag = "[equipment reinforcement ticket]";
        private const string AmplifyReinforceTag = "[equipment amplify reinforcement ticket]";
        private const string LimitUpgradable = "[limit upgradable level]";
        private const string StringData = "[string data]";

        private static readonly HashSet<string> Interesting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SetItemMaster, SetItem, SetAbility, PieceSetAbility, Enchant, MonsterCardIdTag, ReinforceTag, AmplifyReinforceTag, LimitUpgradable,
        };

        /// <summary>强化方式名字到编号(<c>ImportEquipmentScript</c> 0x0899b0ab 起的分支)。</summary>
        internal static int UpgradeTypeOf(string name)
        {
            if (string.Equals(name, "normal upgrade", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (string.Equals(name, "amplify upgrade", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            if (string.Equals(name, "separate upgrade", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }
            if (string.Equals(name, "3rd chronicle upgrade", StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }
            return 0;
        }

        /// <summary>
        /// 从物品脚本的标记流读周边数据:没有任何相关标签返回 null(绝大多数物品,所以先只看标签名,不算块深度)。
        /// 只认深度 0 的标签(块规则见 <see cref="ScriptBlocks"/>);同名标签后一次覆盖前一次(服务端同样:先清再写)。
        /// </summary>
        internal static EnhanceData Read(ScriptToken[] tokens, PvfStringTable strings)
        {
            bool any = false;
            for (int i = 0; i < tokens.Length && !any; i++)
            {
                if (tokens[i].Type == (byte)TokenType.Tag)
                {
                    string tag = ScriptBlocks.TagAt(strings, tokens[i].Value);
                    any = tag != null && Interesting.Contains(tag);
                }
            }
            if (!any)
            {
                return null;
            }
            int[] depths = ScriptBlocks.Depths(tokens, strings);
            int setMaster = 0;
            List<int> members = null;
            EquipStats full = null;
            SortedDictionary<int, EquipStats> pieces = null;
            EquipStats enchant = null;
            int cardId = 0;
            ReinforceTicket? reinforce = null;
            ReinforceTicket? amplify = null;
            List<UpgradeLevelRange> ranges = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type != (byte)TokenType.Tag || depths[i] != 0)
                {
                    continue;
                }
                string tag = ScriptBlocks.TagAt(strings, tokens[i].Value);
                if (tag == null || !Interesting.Contains(tag))
                {
                    continue;
                }
                if (tag.Equals(SetItemMaster, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryFirstInt(tokens, i, out int v))
                    {
                        setMaster = v;
                    }
                }
                else if (tag.Equals(SetItem, StringComparison.OrdinalIgnoreCase))
                {
                    members = Ints(tokens, i);
                }
                else if (tag.Equals(SetAbility, StringComparison.OrdinalIgnoreCase))
                {
                    EquipStats block = BlockStats(tokens, strings, i, "[/set ability]");
                    if (block != null)
                    {
                        full = block;
                    }
                }
                else if (tag.Equals(PieceSetAbility, StringComparison.OrdinalIgnoreCase))
                {
                    EquipStats block = BlockStats(tokens, strings, i, "[/piece set ability]");
                    if (block != null && TryFirstInt(tokens, i, out int n))
                    {
                        if (pieces == null)
                        {
                            pieces = new SortedDictionary<int, EquipStats>();
                        }
                        pieces[n] = block;
                    }
                }
                else if (tag.Equals(Enchant, StringComparison.OrdinalIgnoreCase))
                {
                    EquipStats block = BlockStats(tokens, strings, i, "[/enchant]");
                    if (block != null)
                    {
                        enchant = block;
                    }
                }
                else if (tag.Equals(MonsterCardIdTag, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryFirstInt(tokens, i, out int v))
                    {
                        cardId = v;
                    }
                }
                else if (tag.Equals(ReinforceTag, StringComparison.OrdinalIgnoreCase))
                {
                    reinforce = TicketAt(tokens, i) ?? reinforce;
                }
                else if (tag.Equals(AmplifyReinforceTag, StringComparison.OrdinalIgnoreCase))
                {
                    amplify = TicketAt(tokens, i) ?? amplify;
                }
                else
                {
                    // 服务端:同一种强化方式已经有条目就不覆盖(先写的算数)
                    foreach (UpgradeLevelRange range in LimitRanges(tokens, strings, i))
                    {
                        if (ranges == null)
                        {
                            ranges = new List<UpgradeLevelRange>();
                        }
                        bool have = false;
                        foreach (UpgradeLevelRange old in ranges)
                        {
                            have |= old.Type == range.Type;
                        }
                        if (!have)
                        {
                            ranges.Add(range);
                        }
                    }
                }
            }
            if (setMaster == 0 && members == null && full == null && pieces == null && enchant == null && cardId == 0
                && reinforce == null && amplify == null && (ranges == null || ranges.Count == 0))
            {
                return null;
            }
            var bonuses = new List<SetPieceBonus>();
            if (pieces != null)
            {
                foreach (KeyValuePair<int, EquipStats> pair in pieces)
                {
                    bonuses.Add(new SetPieceBonus(pair.Key, pair.Value));
                }
            }
            List<int> parts = enchant != null ? StringDataParts(tokens, strings, depths) : null;
            return new EnhanceData(setMaster, members, full, bonuses, enchant, cardId, reinforce, amplify, ranges, parts);
        }

        /// <summary>深度 0 的 <c>[string data]</c> 里的部位标签(去重,保持出现次序);图片路径、<c>[foil]</c> 一类不是部位的字符串跳过。</summary>
        private static List<int> StringDataParts(ScriptToken[] tokens, PvfStringTable strings, int[] depths)
        {
            var parts = new List<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type != (byte)TokenType.Tag || depths[i] != 0
                    || !string.Equals(ScriptBlocks.TagAt(strings, tokens[i].Value), StringData, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                for (int j = i + 1; j < tokens.Length && tokens[j].Type != (byte)TokenType.Tag; j++)
                {
                    if (tokens[j].Type != (byte)TokenType.String)
                    {
                        continue;
                    }
                    int part = EquipmentParts.FromTag(ScriptBlocks.TagAt(strings, tokens[j].Value));
                    if (part != EquipmentParts.Unknown && !parts.Contains(part))
                    {
                        parts.Add(part);
                    }
                }
            }
            return parts;
        }

        /// <summary>标签后面直到下一个标签的第一个整数。</summary>
        private static bool TryFirstInt(ScriptToken[] tokens, int tagIndex, out int value)
        {
            for (int i = tagIndex + 1; i < tokens.Length && tokens[i].Type != (byte)TokenType.Tag; i++)
            {
                if (tokens[i].Type == (byte)TokenType.Int || tokens[i].Type == (byte)TokenType.UInt)
                {
                    value = tokens[i].Value;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        /// <summary>标签后面直到下一个标签的全部整数(服务端 <c>ScanInt</c> 循环到读不出整数为止)。</summary>
        private static List<int> Ints(ScriptToken[] tokens, int tagIndex)
        {
            var list = new List<int>();
            for (int i = tagIndex + 1; i < tokens.Length && tokens[i].Type != (byte)TokenType.Tag; i++)
            {
                if (tokens[i].Type == (byte)TokenType.Int || tokens[i].Type == (byte)TokenType.UInt)
                {
                    list.Add(tokens[i].Value);
                }
            }
            return list;
        }

        private static ReinforceTicket? TicketAt(ScriptToken[] tokens, int tagIndex)
        {
            List<int> v = Ints(tokens, tagIndex);
            if (v.Count < 2)
            {
                return null;
            }
            // 服务端取值函数在范围外按 0 处理:等级 0 到 31,成功率 0 到 100(GetReinforceVaule / GetReinforceRate)
            int level = v[0] >= 0 && v[0] <= EquipInstance.MaxUpgrade ? v[0] : 0;
            int rate = v[1] >= 0 && v[1] <= 100 ? v[1] : 0;
            return new ReinforceTicket(level, rate);
        }

        /// <summary>
        /// 开头标签(下标 <paramref name="opener"/>)到收尾标签之间,直接写在这个块里的属性。没有收尾标签(不构成块)返回 null。
        /// 做法:把块里的标记切出来,当成一份独立的脚本交给 <see cref="EquipStats.Read"/>——块里再嵌的块(条件、效果、技能加成)深度不为 0,不算。
        /// </summary>
        private static EquipStats BlockStats(ScriptToken[] tokens, PvfStringTable strings, int opener, string closer)
        {
            int close = -1;
            for (int i = opener + 1; i < tokens.Length; i++)
            {
                if (tokens[i].Type == (byte)TokenType.Tag
                    && string.Equals(ScriptBlocks.TagAt(strings, tokens[i].Value), closer, StringComparison.OrdinalIgnoreCase))
                {
                    close = i;
                    break;
                }
            }
            if (close < 0)
            {
                return null;
            }
            // 开头标签自己后面的整数(如 [piece set ability] 的件数)不是块里的内容:从第一个标签开始切
            int start = opener + 1;
            while (start < close && tokens[start].Type != (byte)TokenType.Tag)
            {
                start++;
            }
            var slice = new ScriptToken[close - start];
            Array.Copy(tokens, start, slice, 0, slice.Length);
            return EquipStats.Read(slice, strings);
        }

        /// <summary><c>[limit upgradable level]</c> 一行里的"名字 下限 上限"三元组(名字不认得的跳过);-1 按服务端补成下限 1、上限 70。</summary>
        private static List<UpgradeLevelRange> LimitRanges(ScriptToken[] tokens, PvfStringTable strings, int tagIndex)
        {
            var list = new List<UpgradeLevelRange>();
            int type = 0;
            var numbers = new List<int>();
            for (int i = tagIndex + 1; i < tokens.Length && tokens[i].Type != (byte)TokenType.Tag; i++)
            {
                ScriptToken t = tokens[i];
                if (t.Type == (byte)TokenType.String)
                {
                    type = UpgradeTypeOf(ScriptBlocks.TagAt(strings, t.Value));
                    numbers.Clear();
                }
                else if (t.Type == (byte)TokenType.Int || t.Type == (byte)TokenType.UInt)
                {
                    numbers.Add(t.Value);
                    if (numbers.Count == 2)
                    {
                        if (type != 0)
                        {
                            int min = numbers[0] == -1 ? 1 : numbers[0];
                            int max = numbers[1] == -1 ? 70 : numbers[1];
                            list.Add(new UpgradeLevelRange(type, min, max));
                        }
                        numbers.Clear();
                    }
                }
            }
            return list;
        }
    }
}
