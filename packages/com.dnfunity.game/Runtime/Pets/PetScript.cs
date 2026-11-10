using System;
using Dnf.Data;

// 服务端静态读码:importCreatureScript 0x08a35e47(顶层循环、各标签的读取形状、成长一节的"块模式")。
namespace Dnf.Game
{
    /// <summary>读一份宠物脚本时的统计(对拍与排查用,不影响读出的字段)。</summary>
    public sealed class PetScriptReport
    {
        /// <summary>为了找到下一个标签而被丢弃的标记个数(包括成长一节里的)。</summary>
        public int DiscardedTokens { get; internal set; }

        /// <summary>遇到几次 <c>[selective grow creature]</c>。</summary>
        public int GrowBlocks { get; internal set; }

        /// <summary>其中走到结束标签、整节提交的次数。</summary>
        public int GrowCommitted { get; internal set; }

        /// <summary>其中在节内读到游离标记或文件结尾、没有提交就退出的次数。</summary>
        public int GrowAbandoned { get; internal set; }
    }

    /// <summary>
    /// 宠物脚本(<c>.cre</c>)里与成长、喂养、神器格有关的字段的读取。
    /// <para>
    /// 读取形状按服务端装载函数:顶层循环"读一个标签 → 按名字分派到处理块";数值标签读一个标记(整数位置的浮点向零取整、
    /// 读到别的标记得 0 并把那个标记消耗掉,读到什么都照存);<c>[name]</c> 读一个字符串;<c>[artifact slot]</c> 反复读字符串直到读到非字符串
    /// (结束标签也在这时被消耗),字符串恰好是 red、blue、green(区分大小写)就置对应颜色,别的字符串忽略;
    /// 成长一节是"块模式":遇 <c>[selective grow creature]</c> 构造一份全缺省的局部信息并进入块模式,块里只认七个子标签,
    /// 遇 <c>[/selective grow creature]</c> 才把局部信息整体提交给脚本,块里读到游离标记(找下一个标签时丢弃了东西)或文件结尾则
    /// 悄悄退出块模式、不提交,那个已经读到的标签也跟着丢掉;块里不认得的子标签被忽略。
    /// </para>
    /// <para>
    /// <b>与服务端的差异</b>:服务端只实现了战斗之外的这一部分标签,其余标签(技能、动作、攻击信息等,约九十个)也在同一个分派里;
    /// 这里不读它们,所以顶层读到不认得的标签时只是继续找下一个标签,而不像服务端那样在顶层遇到游离标记就停止装载
    /// (那些标签的值在这里就是"游离标记")。数据里的写法都规整时两者读出的字段相同。
    /// </para>
    /// </summary>
    public static class PetScript
    {
        public const string NameTag = "[name]";
        public const string StartLevelTag = "[start level]";
        public const string PermissionLevelTag = "[permission level]";
        public const string MaxLevelTag = "[max level]";
        public const string ParentMaxLevelTag = "[parent max level]";
        public const string ArtifactSlotTag = "[artifact slot]";
        public const string EvolutionQuestTag = "[evolution quest]";
        public const string EvolutionCreatureIdTag = "[evolution creature id]";
        public const string EvolutionLevelTag = "[evolution level]";
        public const string GrowOpenTag = "[selective grow creature]";
        public const string GrowCloseTag = "[/selective grow creature]";
        public const string GrowItemsPerLevelTag = "[item count for levelup]";
        public const string GrowUsesExpItemTag = "[using exp item]";
        public const string GrowATypeItemTag = "[grow Atype item index]";
        public const string GrowBTypeItemTag = "[grow Btype item index]";
        public const string GrowATypeCreatureTag = "[grow Atype creature id]";
        public const string GrowBTypeCreatureTag = "[grow Btype creature id]";
        public const string GrowLevelTag = "[grow level]";

        /// <summary>读一份宠物脚本的原始字节;不是标记流脚本返回 null。</summary>
        public static PetDef Parse(ReadOnlySpan<byte> data, PvfStringTable strings, int id, string path, out PetScriptReport report)
        {
            report = null;
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings, id, path, out report);
        }

        /// <summary>读一份已经拆成标记的宠物脚本。</summary>
        public static PetDef Parse(ScriptToken[] tokens, PvfStringTable strings, int id, string path, out PetScriptReport report)
        {
            var cursor = new ScriptCursor(tokens, strings);
            var def = new PetDef(id, path);
            report = new PetScriptReport();
            PetGrowInfo block = null;
            while (true)
            {
                if (block != null)
                {
                    // 块模式:这一轮读到的标签只在块里认。
                    if (!cursor.ScanType(out string sub))
                    {
                        report.GrowAbandoned++;
                        block = null;
                        continue;
                    }
                    switch (sub)
                    {
                        case GrowCloseTag:
                            def.Grow = block.Copy();
                            report.GrowCommitted++;
                            block = null;
                            break;
                        case GrowItemsPerLevelTag:
                            block.ItemsPerLevel = cursor.ScanInt(out _);
                            break;
                        case GrowUsesExpItemTag:
                            block.UsesExpItem = cursor.ScanInt(out _) != 0;
                            break;
                        case GrowATypeItemTag:
                            block.ATypeItemIndex = cursor.ScanInt(out _);
                            break;
                        case GrowBTypeItemTag:
                            block.BTypeItemIndex = cursor.ScanInt(out _);
                            break;
                        case GrowATypeCreatureTag:
                            block.ATypeCreatureId = cursor.ScanInt(out _);
                            break;
                        case GrowBTypeCreatureTag:
                            block.BTypeCreatureId = cursor.ScanInt(out _);
                            break;
                        case GrowLevelTag:
                            block.SelectLevel = cursor.ScanInt(out _);
                            break;
                    }
                    continue;
                }
                cursor.ScanType(out string tag);
                if (tag == null)
                {
                    break;
                }
                switch (tag)
                {
                    case NameTag:
                        if (cursor.ScanStr(out string name))
                        {
                            def.Name = name;
                        }
                        break;
                    case StartLevelTag:
                        def.StartLevel = cursor.ScanInt(out _);
                        break;
                    case PermissionLevelTag:
                        def.PermissionLevel = cursor.ScanInt(out _);
                        break;
                    case MaxLevelTag:
                        def.MaxLevel = cursor.ScanInt(out _);
                        break;
                    case ParentMaxLevelTag:
                        def.ParentMaxLevel = cursor.ScanInt(out _);
                        break;
                    case ArtifactSlotTag:
                        while (cursor.ScanStr(out string color))
                        {
                            if (color == "red")
                            {
                                def.ArtifactColors |= PetArtifactColors.Red;
                            }
                            else if (color == "blue")
                            {
                                def.ArtifactColors |= PetArtifactColors.Blue;
                            }
                            else if (color == "green")
                            {
                                def.ArtifactColors |= PetArtifactColors.Green;
                            }
                        }
                        break;
                    case EvolutionQuestTag:
                        def.EvolutionQuest = cursor.ScanInt(out _);
                        break;
                    case EvolutionCreatureIdTag:
                        def.EvolutionCreatureId = cursor.ScanInt(out _);
                        break;
                    case EvolutionLevelTag:
                        def.EvolutionLevel = cursor.ScanInt(out _);
                        break;
                    case GrowOpenTag:
                        block = new PetGrowInfo();
                        report.GrowBlocks++;
                        break;
                }
            }
            report.DiscardedTokens = cursor.DiscardedTokens;
            return def;
        }
    }
}
