using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:服务端读码(静态,没运行)。可叠加物品脚本里 [booster info] 的块在 ImportStackableScript 0x089aeec8 的 0x089b4058 起那一段读,
// 块的种类标签由 stStackableBooster_t::tag2Type 0x089c2832 翻成编号,条目的权重在导入时累加成前缀和(CStackableItem::getBoosterItem 0x0850f75e 按它抽);
// 礼包的 [package data] 是定长的 (编号, 数量) 对,开包处理(respond_special_item_package 0x0827c204)按序全给。
// 全库统计(只记数量):[booster info] 1022 个脚本、4237 块、条目三万多条,无一处解析问题;[package data] 2192 个脚本、无一处格式问题。
namespace Dnf.Game
{
    /// <summary>
    /// 助推器(随机箱)里一个块的种类。编号来自服务端 <c>tag2Type</c>:avatar 0、special avatar 1、cera 2、creature 3、equipment 4、stackable 5、etc 6、
    /// special creature 7、emblem 8,其余是非法(服务端导入直接失败)。<b>推断</b>:种类只是发放时分的处理类别,不限定条目编号落在哪张清单里
    /// (全库统计里 <c>[etc]</c> 块的条目大多是装备清单里的编号),所以条目一律按编号到物品目录里找。
    /// </summary>
    public enum BoosterKind
    {
        Avatar = 0,
        SpecialAvatar = 1,
        Cera = 2,
        Creature = 3,
        Equipment = 4,
        Stackable = 5,
        Etc = 6,
        SpecialCreature = 7,
        Emblem = 8,
    }

    /// <summary>一个候选:物品编号、权重(相对值)、数量。</summary>
    public readonly struct BoosterEntry
    {
        public BoosterEntry(int code, int weight, int count)
        {
            Code = code;
            Weight = weight;
            Count = count;
        }

        public int Code { get; }

        public int Weight { get; }

        public int Count { get; }
    }

    /// <summary>
    /// 助推器的一个块(<c>[种类] N (编号 权重 数量)+ [/种类]</c>):抽 <see cref="Draws"/> 次,每次独立、有放回地按权重在 <see cref="Entries"/> 里挑一条。
    /// 权重是相对值(服务端累加成前缀和,抽 <c>get_rand_int(总和)</c> 取第一个"累计大于抽到的数"的条目),不要求总和是 1000;
    /// 权重 0 的条目永远抽不到,只有一条且权重等于总和的块必出。
    /// </summary>
    public sealed class BoosterBlock
    {
        private readonly BoosterEntry[] _entries;
        private readonly long[] _cumulative;

        public BoosterBlock(BoosterKind kind, int draws, IEnumerable<BoosterEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }
            Kind = kind;
            Draws = draws;
            _entries = new List<BoosterEntry>(entries).ToArray();
            _cumulative = new long[_entries.Length];
            long sum = 0;
            for (int i = 0; i < _entries.Length; i++)
            {
                sum += Math.Max(0, _entries[i].Weight);      // 负权重按 0 算(全库没有);服务端是直接累加,这里不让前缀和倒退
                _cumulative[i] = sum;
            }
            TotalWeight = sum;
        }

        public BoosterKind Kind { get; }

        /// <summary>抽取次数(块头的 N)。</summary>
        public int Draws { get; }

        public IReadOnlyList<BoosterEntry> Entries => _entries;

        /// <summary>权重总和(抽数的上界,不含)。</summary>
        public long TotalWeight { get; }

        /// <summary>
        /// 抽到的数 <paramref name="roll"/>(0 到总权重减 1)落在哪一条:第一条"累计权重大于 roll"的下标。
        /// 总权重为 0、roll 为负或不小于总权重(服务端同样"没有一条对上")返回 −1。
        /// </summary>
        public int IndexAt(long roll)
        {
            if (roll < 0)
            {
                return -1;
            }
            for (int i = 0; i < _cumulative.Length; i++)
            {
                if (roll < _cumulative[i])
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>这个种类本地能不能发:商城币与商城商品(cera)、宠物(creature、special creature)要等对应的系统,先不发。</summary>
        public static bool IsGrantable(BoosterKind kind)
        {
            switch (kind)
            {
                case BoosterKind.Avatar:
                case BoosterKind.SpecialAvatar:
                case BoosterKind.Equipment:
                case BoosterKind.Stackable:
                case BoosterKind.Etc:
                case BoosterKind.Emblem:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>礼包里的一项固定内容:物品编号与数量。</summary>
    public readonly struct PackageEntry
    {
        public PackageEntry(int code, int count)
        {
            Code = code;
            Count = count;
        }

        public int Code { get; }

        public int Count { get; }
    }

    /// <summary>
    /// 可叠加物品里"开了会给东西"的那两块数据:<c>[booster info]</c>(随机箱:一组带权重的块)与 <c>[package data]</c>(礼包:固定的编号与数量,全给)。
    /// 没有这两块的物品 <see cref="ItemInfo.Contents"/> 是 null。块的长度对不上、种类不认识这类坏数据记在 <see cref="Malformed"/>(服务端会导入失败),
    /// 这样的物品本地不开(不扣不给)。
    /// </summary>
    public sealed class ItemContents
    {
        private static readonly IReadOnlyList<BoosterBlock> NoBlocks = new BoosterBlock[0];
        private static readonly IReadOnlyList<PackageEntry> NoEntries = new PackageEntry[0];

        public ItemContents(IReadOnlyList<BoosterBlock> boosters, IReadOnlyList<PackageEntry> package, bool malformed)
        {
            Boosters = boosters ?? NoBlocks;
            Package = package ?? NoEntries;
            Malformed = malformed;
        }

        /// <summary><c>[booster info]</c> 的块(按在脚本里出现的次序;同一种类可以出现好几块,各抽各的)。</summary>
        public IReadOnlyList<BoosterBlock> Boosters { get; }

        /// <summary><c>[package data]</c> 的固定内容(按脚本里的次序)。</summary>
        public IReadOnlyList<PackageEntry> Package { get; }

        /// <summary>数据有坏(整数个数对不上、块种类不认识、块里混了别的标记)。</summary>
        public bool Malformed { get; }

        public bool HasBooster => Boosters.Count > 0;

        public bool HasPackage => Package.Count > 0;

        /// <summary>每个块的种类本地都能发(见 <see cref="BoosterBlock.IsGrantable"/>)。</summary>
        public bool AllBlocksGrantable
        {
            get
            {
                foreach (BoosterBlock block in Boosters)
                {
                    if (!BoosterBlock.IsGrantable(block.Kind))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// 在一件可叠加物品的标记流里找 <c>[booster info]</c> 与 <c>[package data]</c>;两块都没有返回 null。
        /// 块的读法:标签后面连续的整数(遇到下一个标签为止);块头 N,后面每 3 个整数一条(编号 权重 数量),avatar 与 special avatar 每条多 2 个整数(含义没读到,忽略)。
        /// </summary>
        public static ItemContents Read(ScriptToken[] tokens, PvfStringTable strings)
        {
            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }
            List<BoosterBlock> boosters = null;
            List<PackageEntry> package = null;
            bool malformed = false;
            bool inBooster = false;
            bool seen = false;
            var ints = new List<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type != (byte)TokenType.Tag)
                {
                    continue;
                }
                string tag = (uint)tokens[i].Value < (uint)strings.Count ? strings[tokens[i].Value] : null;
                if (tag == null)
                {
                    continue;
                }
                if (string.Equals(tag, "[booster info]", StringComparison.OrdinalIgnoreCase))
                {
                    inBooster = true;
                    seen = true;
                    continue;
                }
                if (string.Equals(tag, "[/booster info]", StringComparison.OrdinalIgnoreCase))
                {
                    inBooster = false;
                    continue;
                }
                if (inBooster)
                {
                    if (tag.StartsWith("[/", StringComparison.Ordinal))
                    {
                        continue;                      // 块的结束标签
                    }
                    if (!TryKind(tag, out BoosterKind kind))
                    {
                        malformed = true;              // 服务端 tag2Type 返回 9(非法),导入失败
                        continue;
                    }
                    if (!ReadInts(tokens, i + 1, ints))
                    {
                        malformed = true;
                        continue;
                    }
                    int step = kind == BoosterKind.Avatar || kind == BoosterKind.SpecialAvatar ? 5 : 3;
                    if (ints.Count < 1 || (ints.Count - 1) % step != 0)
                    {
                        malformed = true;
                        continue;
                    }
                    var entries = new List<BoosterEntry>((ints.Count - 1) / step);
                    for (int k = 1; k + step <= ints.Count; k += step)
                    {
                        entries.Add(new BoosterEntry(ints[k], ints[k + 1], ints[k + 2]));
                    }
                    (boosters ?? (boosters = new List<BoosterBlock>())).Add(new BoosterBlock(kind, ints[0], entries));
                    continue;
                }
                if (string.Equals(tag, "[package data]", StringComparison.OrdinalIgnoreCase))
                {
                    seen = true;
                    if (!ReadInts(tokens, i + 1, ints) || ints.Count % 2 != 0)
                    {
                        malformed = true;
                        continue;
                    }
                    package = package ?? new List<PackageEntry>();
                    for (int k = 0; k + 2 <= ints.Count; k += 2)
                    {
                        package.Add(new PackageEntry(ints[k], ints[k + 1]));
                    }
                }
            }
            return seen ? new ItemContents(boosters, package, malformed) : null;
        }

        /// <summary>块的种类标签,下标就是 <see cref="BoosterKind"/> 的编号(服务端 <c>tag2Type</c> 的次序)。</summary>
        private static readonly string[] KindTags =
        {
            "[avatar]", "[special avatar]", "[cera]", "[creature]", "[equipment]", "[stackable]", "[etc]", "[special creature]", "[emblem]",
        };

        private static bool TryKind(string tag, out BoosterKind kind)
        {
            for (int i = 0; i < KindTags.Length; i++)
            {
                if (string.Equals(KindTags[i], tag, StringComparison.OrdinalIgnoreCase))
                {
                    kind = (BoosterKind)i;
                    return true;
                }
            }
            kind = default;
            return false;
        }

        /// <summary>读 <paramref name="start"/> 起连续的整数标记放进 <paramref name="into"/>(先清空),遇到下一个标签停;中间混了别的类型的标记返回 false。</summary>
        private static bool ReadInts(ScriptToken[] tokens, int start, List<int> into)
        {
            into.Clear();
            for (int j = start; j < tokens.Length; j++)
            {
                byte type = tokens[j].Type;
                if (type == (byte)TokenType.Tag)
                {
                    break;
                }
                if (type != (byte)TokenType.Int && type != (byte)TokenType.UInt)
                {
                    return false;
                }
                into.Add(tokens[j].Value);
            }
            return true;
        }
    }
}
