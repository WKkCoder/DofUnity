using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码:配方物品是 [stackable type] 为 [recipe] 的可叠加物品,由 ImportStackableScript 0x089aeec8 读脚本、CStackableItem::set_item 0x0850d81e 装进物品,
// 其中 [stackable type] [recipe] 存成物品偏移 0x2b0 的 3(CUser::_GetRecipeItem 0x08670e38 就按这个值认配方物品)。
namespace Dnf.Game
{
    /// <summary>
    /// 从配方物品的脚本读出 <see cref="RecipeDef"/>。读法照服务端装载函数:顶层循环"读一个标签 → 按名字处理";
    /// <c>[int data]</c> 与 <c>[fail output]</c> 反复读整数、读到非整数的标记为止(那个标记就是结尾标签,也在这时被消耗),
    /// 每次遇到都先清空再读,所以后一段覆盖前一段;<c>[bead item]</c> 反复读"一对整数"、读到非整数为止,不清空,多段会累加;
    /// <c>[success rate]</c>、<c>[broadcast type]</c>、<c>[maintain ability]</c> 各读一个整数;
    /// <c>[minimum itemmaking level]</c> 读一个类别名与一个整数,类别名不认得或读不全时服务端整个脚本装载失败(这里记成残缺);
    /// <c>[string data]</c> 反复读字符串到读不出为止。
    /// <para>
    /// 三张表的切法照 createRecipeInfo 0x089ba83c:第一个整数是输入表项数 n1,后面 n1 对"编号、数量";再一个整数是产物表项数 n2 与 n2 对;
    /// 再一个整数是技能要求表项数 n3 与 n3 对(技能编号、等级);表后如果还有整数,第一个是种类标志(1 会员专属、2 副职业配方)。
    /// 服务端读表没有越界检查,整数不够时读到的是越界的内存;这里整数不够就把配方记成残缺(已读到的项保留)。
    /// </para>
    /// <para>
    /// <b>与服务端的差异</b>:顶层读到不认得的标签时只是继续找下一个标签,不像服务端那样在顶层遇到游离标记就停止装载;
    /// 全库配方的写法都规整,两者读出的字段相同(全库普查,见交接文档)。
    /// </para>
    /// </summary>
    public static class RecipeScript
    {
        public const string StackableTypeTag = "[stackable type]";
        public const string IntDataTag = "[int data]";
        public const string StringDataTag = "[string data]";
        public const string SuccessRateTag = "[success rate]";
        public const string BroadcastTypeTag = "[broadcast type]";
        public const string MaintainAbilityTag = "[maintain ability]";
        public const string FailOutputTag = "[fail output]";
        public const string BeadItemTag = "[bead item]";
        public const string MakingLevelTag = "[minimum itemmaking level]";

        /// <summary>读一份物品脚本的原始字节;不是标记流脚本,或不是配方物品时返回 null。</summary>
        public static RecipeDef Parse(ReadOnlySpan<byte> data, PvfStringTable strings, int code, string path)
        {
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings, code, path);
        }

        /// <summary>读一份已经拆成标记的物品脚本;不是配方物品返回 null。</summary>
        public static RecipeDef Parse(ScriptToken[] tokens, PvfStringTable strings, int code, string path)
        {
            var cursor = new ScriptCursor(tokens, strings);
            int stackableType = StackableTypes.Unknown;
            int typeIndex = 0;
            List<int> data = null;
            List<int> fail = null;
            var beads = new List<RecipeItem>();
            var categories = new List<string>();
            int rate = 0;
            int broadcast = 0;
            int maintain = 0;
            int makingType = ItemMakingTypes.None;
            int makingLevel = 0;
            bool malformed = false;
            bool failed = false;
            while (!failed)
            {
                cursor.ScanType(out string tag);
                if (tag == null)
                {
                    break;
                }
                switch (tag)
                {
                    case StackableTypeTag:
                        stackableType = cursor.ScanStr(out string typeName) ? StackableTypes.FromTag(typeName) : StackableTypes.Unknown;
                        typeIndex = cursor.ScanInt(out _);
                        break;
                    case IntDataTag:
                        data = ReadInts(cursor);
                        break;
                    case FailOutputTag:
                        fail = ReadInts(cursor);
                        break;
                    case SuccessRateTag:
                        rate = cursor.ScanInt(out _);
                        break;
                    case BroadcastTypeTag:
                        broadcast = cursor.ScanInt(out _);
                        break;
                    case MaintainAbilityTag:
                        maintain = cursor.ScanInt(out _);
                        break;
                    case BeadItemTag:
                        while (true)
                        {
                            int itemCode = cursor.ScanInt(out bool ok);
                            if (!ok)
                            {
                                break;
                            }
                            int count = cursor.ScanInt(out ok);
                            if (!ok)
                            {
                                break;
                            }
                            beads.Add(new RecipeItem(itemCode, count));
                        }
                        break;
                    case StringDataTag:
                        categories.Clear();
                        while (cursor.ScanStr(out string text))
                        {
                            categories.Add(text);
                        }
                        break;
                    case MakingLevelTag:
                        if (!cursor.ScanStr(out string typeText) || ItemMakingTypes.FromName(typeText) == ItemMakingTypes.None)
                        {
                            malformed = true;
                            failed = true;
                            break;
                        }
                        makingType = ItemMakingTypes.FromName(typeText);
                        makingLevel = cursor.ScanInt(out bool levelOk);
                        if (!levelOk)
                        {
                            malformed = true;
                            failed = true;
                        }
                        break;
                }
            }
            if (stackableType != StackableTypes.Recipe)
            {
                return null;
            }

            var inputs = new List<RecipeItem>();
            var outputs = new List<RecipeItem>();
            var requirements = new List<RecipeSkill>();
            RecipeKind kind = RecipeKind.Normal;
            if (data != null && data.Count > 0 && !BuildTables(data, inputs, outputs, requirements, out kind))
            {
                malformed = true;
            }
            var failOutputs = new List<RecipeItem>();
            if (fail != null)
            {
                for (int i = 0; i + 1 < fail.Count; i += 2)
                {
                    failOutputs.Add(new RecipeItem(fail[i], fail[i + 1]));
                }
            }
            return new RecipeDef(
                code, inputs, outputs, kind, requirements, beads, rate, failOutputs, broadcast, maintain, makingType, makingLevel,
                categories, typeIndex, path, malformed);
        }

        private static List<int> ReadInts(ScriptCursor cursor)
        {
            var list = new List<int>();
            while (true)
            {
                int value = cursor.ScanInt(out bool ok);
                if (!ok)
                {
                    return list;
                }
                list.Add(value);
            }
        }

        /// <summary>按 createRecipeInfo 的切法把整数串切成三张表与种类标志;整数不够返回 false(已读到的项留在表里)。</summary>
        private static bool BuildTables(List<int> d, List<RecipeItem> inputs, List<RecipeItem> outputs, List<RecipeSkill> requirements, out RecipeKind kind)
        {
            kind = RecipeKind.Normal;
            int i = 0;
            if (!ReadItems(d, ref i, inputs) || !ReadItems(d, ref i, outputs))
            {
                return false;
            }
            var skills = new List<RecipeItem>();
            bool ok = ReadItems(d, ref i, skills);
            foreach (RecipeItem s in skills)
            {
                requirements.Add(new RecipeSkill(s.Code, s.Count));
            }
            if (!ok)
            {
                return false;
            }
            if (i < d.Count)
            {
                kind = d[i] == 1 ? RecipeKind.Premium : d[i] == 2 ? RecipeKind.ExpertJob : RecipeKind.Normal;
            }
            return true;
        }

        private static bool ReadItems(List<int> d, ref int i, List<RecipeItem> into)
        {
            if (i >= d.Count)
            {
                return false;
            }
            int n = d[i++];
            for (int k = 0; k < n; k++)
            {
                if (i + 1 >= d.Count)
                {
                    return false;
                }
                into.Add(new RecipeItem(d[i], d[i + 1]));
                i += 2;
            }
            return true;
        }
    }
}
