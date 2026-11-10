using System;
using Dnf.Data;

// 服务端静态读码:importCreatureCommonScript 0x08a383f6(通用配置文件的装载);唯一的读取点是 prepareServerDelayCheckDamage 0x0809510e。
namespace Dnf.Game
{
    /// <summary>
    /// 宠物通用配置(<c>creaturecommon.cre</c>)里与饱食、濒死有关的几项。
    /// <para>
    /// <b>服务端的饱食计算没有用这里的值</b>:满值、每点耗时、每点回补、喂食增量在服务端都是写死的字面量(见 <see cref="PetRules"/>);
    /// 通用配置里的同名项只在装载时读进结构体。这里照样读出来保留,供对拍与以后查证,不参与 <see cref="PetInstance"/> 的规则。
    /// 数据里"吃饲料所需"一项与服务端喂食用的字面增量不是一个数,它在服务端的含义没有读到,所以不接进规则。
    /// </para>
    /// 缺省值都是 0(装载前的缺省没有读到,推断)。
    /// </summary>
    public sealed class PetCommon
    {
        public const string ResponseDelayTag = "[response delay]";
        public const string SpendTag = "[second per fill point spend]";
        public const string ChargeTag = "[second per fill point charge]";
        public const string MaxFillTag = "[MAX fill point]";
        public const string FeedTag = "[fill point to eat feed]";
        public const string DyingDelayTag = "[dying delay]";

        /// <summary><c>[response delay]</c>。</summary>
        public int ResponseDelay { get; set; }

        /// <summary><c>[second per fill point spend]</c>。</summary>
        public int SecondsPerPointSpend { get; set; }

        /// <summary><c>[second per fill point charge]</c>。</summary>
        public int SecondsPerPointCharge { get; set; }

        /// <summary><c>[MAX fill point]</c>。</summary>
        public int MaxFillPoint { get; set; }

        /// <summary><c>[fill point to eat feed]</c>(含义未知,见类说明)。</summary>
        public int FillPointToEatFeed { get; set; }

        /// <summary><c>[dying delay]</c>。</summary>
        public int DyingDelay { get; set; }

        /// <summary>数据里的饱食三项(耗、充、满值)是否与服务端写死的字面量相同(对拍时只报这个是非,不报数值)。</summary>
        public bool FillTermsMatchEngineLiterals =>
            SecondsPerPointSpend == (int)PetRules.DrainSecondsPerPoint
            && SecondsPerPointCharge == (int)PetRules.ChargeSecondsPerPoint
            && MaxFillPoint == (int)PetRules.MaxStomach;

        /// <summary>读一份通用配置的原始字节;不是标记流脚本返回 null。</summary>
        public static PetCommon Parse(ReadOnlySpan<byte> data, PvfStringTable strings)
        {
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings);
        }

        /// <summary>
        /// 读一份已经拆成标记的通用配置:顶层循环读标签,认得的数值标签读一个整数,别的标签继续找下一个
        /// (服务端遇到不认得的标签会整体失败;这里只实现了其中几项,所以不能那样做)。
        /// </summary>
        public static PetCommon Parse(ScriptToken[] tokens, PvfStringTable strings)
        {
            var cursor = new ScriptCursor(tokens, strings);
            var common = new PetCommon();
            while (true)
            {
                cursor.ScanType(out string tag);
                if (tag == null)
                {
                    break;
                }
                switch (tag)
                {
                    case ResponseDelayTag:
                        common.ResponseDelay = cursor.ScanInt(out _);
                        break;
                    case SpendTag:
                        common.SecondsPerPointSpend = cursor.ScanInt(out _);
                        break;
                    case ChargeTag:
                        common.SecondsPerPointCharge = cursor.ScanInt(out _);
                        break;
                    case MaxFillTag:
                        common.MaxFillPoint = cursor.ScanInt(out _);
                        break;
                    case FeedTag:
                        common.FillPointToEatFeed = cursor.ScanInt(out _);
                        break;
                    case DyingDelayTag:
                        common.DyingDelay = cursor.ScanInt(out _);
                        break;
                }
            }
            return common;
        }
    }
}
