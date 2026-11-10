using System;
using System.Collections.Generic;

// 证据:服务端静态读码(df_game_r,只读,没有运行)——
//  · 格数与范围检查:CTitleBook::checkError 0x08641fac(越界或分类不认得返回 3);
//  · 放入:CTitleBook::putTitle 0x08642058(检查次序:来源格里的物品对得上 → 所在页没锁(12)→ 等级 ≥ 10(8)→ "绝望"分类等级 ≥ 70(9)→ _checkFitSlot 0x08642d42 → _checkInsertTitle 0x08641bdc,
//    其中任何一步不过都不动东西;通过后从来源删除、把原来占着这一格的称号放回来源、再写进图鉴);
//  · 取出:CTitleBook::getTitle 0x086425c4(范围 → 对得上 → 去背包要 _checkMovetoInven 0x08641c52(5)、背包放得下(11);去装备栏要目标是 11 号格、装备栏这一格是空的(7)、能穿(6));
//  · 直接放(成就奖励):CTitleBook::putTitleOnly 0x08641e88(_getFitSlot 0x08642baa 找第一个列出这个编号的格,_checkInsertTitle,直接写入——不看这一格有没有人,也不看等级与锁);
//  · 过期清理:deleteTimeoutItem 0x0864125a(本项目的物品实例没有到期日,不做,见 TitleExpiry 的说明)。
// 称号实例在服务端是 61 字节的 Inven_Item;本项目的图鉴只存物品编号(0 表示空格),实例上的标志位(绑定、锁定)不建模。
namespace Dnf.Game
{
    /// <summary>图鉴放入与取出的结果;数值与服务端返回码一致(没读到的号不占用)。</summary>
    public enum TitleBookResult
    {
        Ok = 0,

        /// <summary>来源里的物品对不上、这一格不收这件称号、或这件物品不能放进图鉴(找不到、可叠加)。</summary>
        Mismatch = 2,

        /// <summary>没有图鉴数据,或分类与格下标越界。</summary>
        NoData = 3,

        /// <summary>这一格绑了成就,里面的称号不能拿回背包(只能直接穿上)。</summary>
        NotMovable = 5,

        /// <summary>角色穿不了这件称号。</summary>
        NotEquipable = 6,

        /// <summary>目标装备栏格不是称号格,或称号格上已经穿着一件。</summary>
        SlotUnavailable = 7,

        /// <summary>角色等级低于 <see cref="TitleBook.MinLevel"/>。</summary>
        LevelTooLow = 8,

        /// <summary>"绝望"分类要求等级不低于 <see cref="TitleBook.DespairMinLevel"/>。</summary>
        DespairLevel = 9,

        /// <summary>背包放不下。</summary>
        BagFull = 11,

        /// <summary>这一格所在的页锁着(页的锁定文字不为空)。</summary>
        SectionLocked = 12,
    }

    /// <summary>图鉴里的一件称号(分类、格下标、物品编号)。</summary>
    public readonly struct TitleEntry
    {
        public TitleEntry(TitleCategory category, int slot, int code)
        {
            Category = category;
            Slot = slot;
            Code = code;
        }

        public TitleCategory Category { get; }

        public int Slot { get; }

        public int Code { get; }
    }

    /// <summary>
    /// 称号图鉴(角色身上的那份):四个分类、各 70/200/120/60 格,每格一个称号物品编号(0 = 空)。规则取自服务端(见文件头),
    /// 定义(哪一格收什么)在 <see cref="TitleBookDef"/>。本类只管图鉴自己;背包与装备栏之间的搬运在宿主层(<c>TitleSession</c>)。
    /// <see cref="Version"/> 每次有变化加一。
    /// </summary>
    public sealed class TitleBook
    {
        /// <summary>放入图鉴要求的最低角色等级(putTitle:低于 10 返回 8)。</summary>
        public const int MinLevel = 10;

        /// <summary>"绝望"分类要求的最低角色等级(putTitle:分类 2 且低于 0x46 返回 9)。</summary>
        public const int DespairMinLevel = 70;

        private readonly TitleBookDef _def;
        private readonly int[][] _codes = new int[TitleCategories.Count][];

        public TitleBook(TitleBookDef def)
        {
            _def = def ?? throw new ArgumentNullException(nameof(def));
            for (int c = 0; c < _codes.Length; c++)
            {
                _codes[c] = new int[TitleCategories.CapacityOf((TitleCategory)c)];
            }
        }

        public TitleBookDef Definition => _def;

        /// <summary>图鉴的变化次数(放入、取出、清空、读档各算一次)。</summary>
        public int Version { get; private set; }

        /// <summary>这一格里的称号物品编号;空格、越界返回 0。</summary>
        public int CodeAt(TitleCategory category, int slot)
        {
            return TitleCategories.IsValidSlot(category, slot) ? _codes[(int)category][slot] : 0;
        }

        /// <summary>这个分类里放了几件。</summary>
        public int Count(TitleCategory category)
        {
            if (!TitleCategories.IsValid(category))
            {
                return 0;
            }
            int n = 0;
            foreach (int code in _codes[(int)category])
            {
                if (code != 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>一共放了几件。</summary>
        public int TotalCount
        {
            get
            {
                int n = 0;
                for (int c = 0; c < TitleCategories.Count; c++)
                {
                    n += Count((TitleCategory)c);
                }
                return n;
            }
        }

        /// <summary>放着的全部称号,按分类、格下标从小到大。</summary>
        public IReadOnlyList<TitleEntry> Entries()
        {
            var list = new List<TitleEntry>();
            for (int c = 0; c < _codes.Length; c++)
            {
                for (int s = 0; s < _codes[c].Length; s++)
                {
                    if (_codes[c][s] != 0)
                    {
                        list.Add(new TitleEntry((TitleCategory)c, s, _codes[c][s]));
                    }
                }
            }
            return list;
        }

        /// <summary>图鉴里有没有这个物品编号。</summary>
        public bool Contains(int code)
        {
            return TryFind(code, out _, out _);
        }

        /// <summary>图鉴里第一个放着这个物品编号的格(分类、格下标从小到大);没有返回 false。</summary>
        public bool TryFind(int code, out TitleCategory category, out int slot)
        {
            if (code != 0)
            {
                for (int c = 0; c < _codes.Length; c++)
                {
                    for (int s = 0; s < _codes[c].Length; s++)
                    {
                        if (_codes[c][s] == code)
                        {
                            category = (TitleCategory)c;
                            slot = s;
                            return true;
                        }
                    }
                }
            }
            category = default;
            slot = -1;
            return false;
        }

        // ---------------------------------------------------------------- 放入

        /// <summary>
        /// 放入的检查(putTitle 里"来源对得上"之后的几步,次序照服务端):所在页锁着 → 12;角色等级低于 10 → 8;"绝望"分类且低于 70 → 9;
        /// 这一格不收这件称号 → 2;物品为空或不是装备(可叠加)→ 2。都通过返回 <see cref="TitleBookResult.Ok"/>。不改动图鉴。
        /// </summary>
        public TitleBookResult CheckPut(TitleCategory category, int slot, ItemInfo item, int level)
        {
            TitleSectionDef section = _def.SectionAt(category, slot);
            if (section != null && section.IsLocked)
            {
                return TitleBookResult.SectionLocked;
            }
            if (level < MinLevel)
            {
                return TitleBookResult.LevelTooLow;
            }
            if (category == TitleCategory.Despair && level < DespairMinLevel)
            {
                return TitleBookResult.DespairLevel;
            }
            if (item == null || !TitleCategories.IsValidSlot(category, slot) || !_def.CheckFitSlot(item.Code, category, slot))
            {
                return TitleBookResult.Mismatch;
            }
            if (!item.IsEquipment)
            {
                return TitleBookResult.Mismatch;
            }
            return TitleBookResult.Ok;
        }

        /// <summary>
        /// 把称号放进这一格(<see cref="CheckPut"/> 通过才动):原来占着这一格的称号编号给 <paramref name="replaced"/>(没有是 0),由调用方放回来源。
        /// 不通过时 <paramref name="replaced"/> 是 0、图鉴不变。
        /// </summary>
        public TitleBookResult Put(TitleCategory category, int slot, ItemInfo item, int level, out int replaced)
        {
            replaced = 0;
            TitleBookResult check = CheckPut(category, slot, item, level);
            if (check != TitleBookResult.Ok)
            {
                return check;
            }
            replaced = _codes[(int)category][slot];
            Write(category, slot, item.Code);
            return TitleBookResult.Ok;
        }

        /// <summary>
        /// 直接放(putTitleOnly,成就奖励走它):找第一个列出这个编号的格,物品得存在且不可叠加,然后覆盖写入——不看这一格有没有人、不看等级与页锁。
        /// 返回是否写入。<paramref name="category"/> 与 <paramref name="slot"/> 在找到格时给出那一格(即使随后因为物品不合格没有写入),
        /// 找不到格时是 (分类 0, 格 0):调用方(服务端的 <c>_checkComplete</c>)就是拿这对默认值去判"整页奖励"的。
        /// </summary>
        public bool PutOnly(int code, Func<int, ItemInfo> lookup, out TitleCategory category, out int slot)
        {
            category = default;
            slot = 0;
            if (code == 0 || lookup == null)
            {
                return false;
            }
            ItemInfo item = lookup(code);
            if (item == null)
            {
                return false;
            }
            if (!_def.TryFindFitSlot(code, out TitleCategory foundCategory, out int foundSlot))
            {
                return false;
            }
            category = foundCategory;
            slot = foundSlot;
            if (!item.IsEquipment || !TitleCategories.IsValidSlot(foundCategory, foundSlot))
            {
                return false;
            }
            Write(foundCategory, foundSlot, code);
            return true;
        }

        // ---------------------------------------------------------------- 取出

        /// <summary>
        /// 取出的检查(getTitle 里图鉴这一侧的几步):分类或格越界 → 3;格里是空的或编号对不上 <paramref name="expectCode"/> → 2;
        /// 去背包(<paramref name="toBag"/>)而这一格绑了成就 → 5。背包放不放得下(11)、装备栏能不能穿(6、7)由调用方查。不改动图鉴。
        /// </summary>
        public TitleBookResult CheckTake(TitleCategory category, int slot, int expectCode, bool toBag)
        {
            if (!TitleCategories.IsValidSlot(category, slot))
            {
                return TitleBookResult.NoData;
            }
            int code = _codes[(int)category][slot];
            if (expectCode == 0 || code == 0 || code != expectCode)
            {
                return TitleBookResult.Mismatch;
            }
            if (toBag && !_def.CanTakeBack(category, slot))
            {
                return TitleBookResult.NotMovable;
            }
            return TitleBookResult.Ok;
        }

        /// <summary>清空这一格并返回里面的称号编号(空格、越界返回 0,图鉴不变)。</summary>
        public int Remove(TitleCategory category, int slot)
        {
            if (!TitleCategories.IsValidSlot(category, slot))
            {
                return 0;
            }
            int code = _codes[(int)category][slot];
            if (code != 0)
            {
                _codes[(int)category][slot] = 0;
                Version++;
            }
            return code;
        }

        // ---------------------------------------------------------------- 存档

        /// <summary>清空图鉴(有东西才算一次变化)。</summary>
        public void Clear()
        {
            bool any = false;
            foreach (int[] list in _codes)
            {
                for (int i = 0; i < list.Length; i++)
                {
                    if (list[i] != 0)
                    {
                        list[i] = 0;
                        any = true;
                    }
                }
            }
            if (any)
            {
                Version++;
            }
        }

        /// <summary>读档用:直接写一格(不查规则);分类或格越界、编号不大于 0 返回 false。</summary>
        public bool Restore(TitleCategory category, int slot, int code)
        {
            if (!TitleCategories.IsValidSlot(category, slot) || code <= 0)
            {
                return false;
            }
            Write(category, slot, code);
            return true;
        }

        private void Write(TitleCategory category, int slot, int code)
        {
            if (_codes[(int)category][slot] != code)
            {
                _codes[(int)category][slot] = code;
                Version++;
            }
        }
    }
}
