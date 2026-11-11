using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码(df_game_r.orig):物品脚本里与副职业、分解有关的标签。
//   [impossible contents] 的值 "disjoint" → 物品 +0x86 → CItem::IsImpossibleDisjoint(虚函数槽 +0x38);
//   [random option] → CItem::IsRandomOption(虚函数槽 +0x4c;分解主产物的价格档用它,商店收购用的是 [possible kiri protect]——两处用的不是同一个标志);
//   [expertjob only] 名字 + 等级的成对值 → STItemScript::SEXPERTJOB::SUSABLE 列表(物品 +0x104),判定见 CItem::isExpertJobUsable 0x084e95ce;
//   [expert type]、[portable disjoint](物品 +0x324,便携分解器序号)、[prof disjoint big win rate](+0x148)、[prof disjoint result variation](+0x14c)、
//   [prof additional gain exp](+0x140 浮点、+0x144 整数)。
// 观察:全库里 [portable disjoint] 只在 4 个可叠加物品上,[prof disjoint result variation] 与 [prof additional gain exp] 各只有 1 件装备,[prof disjoint big win rate] 一件也没有;
// 其余几个 [prof ...] 标签(制作的材料与成功率变化)服务端也读,这里不建模。
namespace Dnf.Game
{
    /// <summary><c>[expertjob only]</c> 的一对:副职业种类(<see cref="ExpertJobKinds.AnyJob"/> 表示"任何副职业")与要求的最低副职业等级。</summary>
    public readonly struct ExpertJobUsable
    {
        public ExpertJobUsable(int type, int level)
        {
            Type = type;
            Level = level;
        }

        public int Type { get; }

        public int Level { get; }
    }

    /// <summary>一件物品脚本里的副职业相关标签。<see cref="None"/> 表示什么都没有(默认值是服务端构造函数的缺省:可用、不是随机选项、便携序号 −1、加成 0)。</summary>
    public sealed class ExpertJobItemTags
    {
        public static readonly ExpertJobItemTags None = new ExpertJobItemTags();

        private readonly List<ExpertJobUsable> _usable = new List<ExpertJobUsable>();
        private readonly List<string> _errors = new List<string>();

        /// <summary>读标签时发现的错误(服务端这些情况下整份物品脚本装载失败);空表示读得干净。</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary><c>[impossible contents]</c> 里有 "disjoint":这件物品不能分解。</summary>
        public bool ImpossibleDisjoint { get; private set; }

        /// <summary><c>[random option]</c> 出现过(全库取值恒为 1)。</summary>
        public bool RandomOption { get; private set; }

        /// <summary><c>[expert type]</c>(可叠加物品标明它属于哪条副职业线);没有是 <see cref="ExpertJobKind.None"/>。</summary>
        public ExpertJobKind ExpertType { get; private set; }

        /// <summary><c>[expertjob only]</c> 的各对(按书写次序)。</summary>
        public IReadOnlyList<ExpertJobUsable> Usable => _usable;

        /// <summary><c>[portable disjoint]</c> 的序号;没有是 −1(服务端缺省)。</summary>
        public int PortableDisjointIndex { get; private set; } = -1;

        /// <summary><c>[prof disjoint big win rate]</c>:穿在身上时给自己的分解机加的大成功百分点。</summary>
        public float BigWinRate { get; private set; }

        /// <summary><c>[prof disjoint result variation]</c>:穿在身上时自己的分解机产物个数的百分比变化。</summary>
        public float ResultVariation { get; private set; }

        /// <summary><c>[prof additional gain exp]</c> 的第一个值:每次分解额外得经验的概率(百分点)。</summary>
        public float AdditionalExpChance { get; private set; }

        /// <summary><c>[prof additional gain exp]</c> 的第二个值:额外得到的经验。</summary>
        public int AdditionalExpAmount { get; private set; }

        /// <summary>
        /// <c>CItem::isExpertJobUsable</c>(0x084e95ce):没写 <c>[expertjob only]</c> 时人人可用;写了就按次序找第一个种类相同的对
        /// (或种类为 5 且当前有副职业的对),等级够才可用;一对也没对上就不可用。
        /// </summary>
        public bool IsUsable(int type, int level)
        {
            if (_usable.Count == 0)
            {
                return true;
            }
            foreach (ExpertJobUsable u in _usable)
            {
                if (u.Type == type)
                {
                    return u.Level <= level;
                }
                if (u.Type == ExpertJobKinds.AnyJob && type != 0)
                {
                    return u.Level <= level;
                }
            }
            return false;
        }

        public static ExpertJobItemTags Parse(IReadOnlyList<ScriptLine> lines)
        {
            var t = new ExpertJobItemTags();
            if (lines == null)
            {
                return t;
            }
            foreach (ScriptLine line in lines)
            {
                if (line == null || line.Tag == null)
                {
                    continue;
                }
                if (line.TagIs("[impossible contents]"))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.String && string.Equals(v.Text, "disjoint", StringComparison.OrdinalIgnoreCase))
                        {
                            t.ImpossibleDisjoint = true;
                        }
                    }
                }
                else if (line.TagIs("[random option]"))
                {
                    t.RandomOption = true;
                }
                else if (line.TagIs("[expert type]"))
                {
                    t.ExpertType = ExpertJobKinds.FromTypeTag(line.FirstString());
                }
                else if (line.TagIs("[expertjob only]"))
                {
                    t.ReadUsable(line);
                }
                else if (line.TagIs("[portable disjoint]"))
                {
                    var r = new ExjReader(line.Values);
                    if (r.TryInt(out int index))
                    {
                        t.PortableDisjointIndex = index;
                    }
                }
                else if (line.TagIs("[prof disjoint big win rate]"))
                {
                    var r = new ExjReader(line.Values);
                    if (r.TryFloat(out float v))
                    {
                        t.BigWinRate = v;
                    }
                }
                else if (line.TagIs("[prof disjoint result variation]"))
                {
                    var r = new ExjReader(line.Values);
                    if (r.TryFloat(out float v))
                    {
                        t.ResultVariation = v;
                    }
                }
                else if (line.TagIs("[prof additional gain exp]"))
                {
                    var r = new ExjReader(line.Values);
                    if (r.TryFloat(out float chance))
                    {
                        t.AdditionalExpChance = chance;
                        if (r.TryInt(out int amount))
                        {
                            t.AdditionalExpAmount = amount;
                        }
                    }
                }
            }
            return t;
        }

        private void ReadUsable(ScriptLine line)
        {
            _usable.Clear();
            ScriptValue[] values = line.Values;
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                if (values[i].Kind != ScriptValueKind.String || !values[i + 1].IsNumber)
                {
                    break;
                }
                int type = ExpertJobKinds.UsableTypeOf(values[i].Text);
                if (type < 0)
                {
                    // 服务端 GetExpertJobTypeList(0x089bf6ca)遇到不认得的名字就让整份物品脚本装载失败;这里记一条错误并停止读这个标签。
                    _errors.Add("[expertjob only] 里有不认得的副职业名字:" + values[i].Text);
                    break;
                }
                _usable.Add(new ExpertJobUsable(type, values[i + 1].Int));
            }
        }
    }

    /// <summary>
    /// 穿在身上的装备对分解的加成(<c>CInventory::GetExpertJobSelfDisjointBigWinRate</c> 0x0850cae2、<c>...ResultVariation</c> 0x0850cb94、
    /// <c>GetExpertJobAddtionalExp</c> 0x0850cc46):只数装备槽 10 到 21 里的物品,逐件要求 <see cref="ExpertJobItemTags.IsUsable"/>,把各自的值按单精度累加;
    /// 副职业种类为"没有"(0)时三个都返回 0。
    /// </summary>
    public static class ExpertJobGear
    {
        /// <summary>计入加成的第一个装备槽(武器)。</summary>
        public const int FirstSlot = EquipmentParts.Weapon;

        /// <summary>计入加成的最后一个装备槽(魔法石)。</summary>
        public const int LastSlot = EquipmentParts.MagicStone;

        /// <summary>自分解大成功率加成(百分点)。<paramref name="tagsAt"/> 按装备槽号给出身上那件物品的标签,槽里没有物品返回 null。</summary>
        public static float SelfBigWinRate(Func<int, ExpertJobItemTags> tagsAt, int type, int level)
        {
            return Sum(tagsAt, type, level, t => t.BigWinRate);
        }

        /// <summary>自分解产物个数变化(百分比)。</summary>
        public static float SelfResultVariation(Func<int, ExpertJobItemTags> tagsAt, int type, int level)
        {
            return Sum(tagsAt, type, level, t => t.ResultVariation);
        }

        /// <summary>每次分解的额外经验:概率(百分点,各件相加)与经验(各件相加)。</summary>
        public static void AdditionalExp(Func<int, ExpertJobItemTags> tagsAt, int type, int level, out float chance, out int amount)
        {
            chance = 0f;
            amount = 0;
            if (type == 0 || tagsAt == null)
            {
                return;
            }
            for (int part = FirstSlot; part <= LastSlot; part++)
            {
                ExpertJobItemTags t = tagsAt(part);
                if (t != null && t.IsUsable(type, level))
                {
                    chance += t.AdditionalExpChance;
                    amount += t.AdditionalExpAmount;
                }
            }
        }

        private static float Sum(Func<int, ExpertJobItemTags> tagsAt, int type, int level, Func<ExpertJobItemTags, float> pick)
        {
            float sum = 0f;
            if (type == 0 || tagsAt == null)
            {
                return sum;
            }
            for (int part = FirstSlot; part <= LastSlot; part++)
            {
                ExpertJobItemTags t = tagsAt(part);
                if (t != null && t.IsUsable(type, level))
                {
                    sum += pick(t);
                }
            }
            return sum;
        }
    }
}
