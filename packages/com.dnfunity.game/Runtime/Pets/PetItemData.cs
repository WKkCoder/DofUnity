using System;
using Dnf.Data;

// 服务端静态读码:装备脚本装载 ImportEquipmentScript 0x08991aeb 里的宠物字段([creature species]、[creature minimum level]、
// [creature experience amount rate]);CItem 上对应的访问器 CCreature::GetCreatureId 0x0833769c、CArtifact::GetCreatureMinimumLevel 0x08337012、
// CArtifact::GetExperienceAmount 0x08336fc8。
namespace Dnf.Game
{
    /// <summary>
    /// 宠物物品与神器(装备脚本 <c>.equ</c>)上与成长有关的三个字段。其余宠物字段(攻击、暴击、技能耗蓝倍率等)属于战斗部分,不在这里。
    /// 缺省值按 0(装备脚本装载前的缺省没有读到,推断)。
    /// </summary>
    public sealed class PetItemData
    {
        public const string SpeciesTag = "[creature species]";
        public const string MinimumLevelTag = "[creature minimum level]";
        public const string ExperienceRateTag = "[creature experience amount rate]";

        /// <summary>
        /// <c>[creature species]</c>:这件宠物物品孵出(或就是)哪只宠物(<see cref="PetDef.Id"/>)。0 表示没写。
        /// </summary>
        public int Species { get; set; }

        /// <summary>
        /// <c>[creature minimum level]</c>:神器要求宠物至少多少级才生效(0 表示不限制)。注意与物品自己的 <c>[minimum level]</c>(角色等级)不是一回事。
        /// </summary>
        public int MinimumLevel { get; set; }

        /// <summary><c>[creature experience amount rate]</c>:神器给宠物经验加的百分数(float)。</summary>
        public float ExperienceRate { get; set; }

        /// <summary>读一份装备脚本的原始字节;不是标记流脚本返回 null。只取三个宠物字段,别的标签与值被跳过。</summary>
        public static PetItemData Parse(ReadOnlySpan<byte> data, PvfStringTable strings)
        {
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings);
        }

        /// <summary>读一份已经拆成标记的装备脚本。</summary>
        public static PetItemData Parse(ScriptToken[] tokens, PvfStringTable strings)
        {
            var cursor = new ScriptCursor(tokens, strings);
            var item = new PetItemData();
            while (true)
            {
                cursor.ScanType(out string tag);
                if (tag == null)
                {
                    break;
                }
                switch (tag)
                {
                    case SpeciesTag:
                        item.Species = cursor.ScanInt(out _);
                        break;
                    case MinimumLevelTag:
                        item.MinimumLevel = cursor.ScanInt(out _);
                        break;
                    case ExperienceRateTag:
                        item.ExperienceRate = cursor.ScanFloat(out _);
                        break;
                }
            }
            return item;
        }
    }
}
