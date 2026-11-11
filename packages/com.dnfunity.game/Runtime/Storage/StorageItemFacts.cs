using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:物品脚本里与仓库有关的三处标签——
//   [packagable]:ImportStackableScript 0x089aeec8 在 0x089b3a71 起把"读到的整数是否不为 0"写进脚本偏移 0xfc 的一个字节,CStackableItem::set_item 0x0850d81e 拷到物品偏移 0x60,
//     CItem::isPackagable 0x0828b5b4 就是它等于 1;
//   [impossible contents]:同一个函数 0x089b740a 起逐个读字符串,与几个关键字比较、各置一个标志,"charac cargo" 置的那个就是 CItem::IsImpossibleCharacCargo 0x085143be 的来源(虚表偏移 0x40);
//   [action type]:0x089b2330 起先读一个字符串、经 getStackableActionType 0x089aeb9b 换成动作编号(字符串 "[upgrade cargo]" 是 0x2b),后面跟若干整数参数。
// 个人仓库扩容道具就是动作编号为 0x2b 的可叠加物品(Dispatcher_UpgradeCargo 0x0822a0a4 里 verifyStackableItemActionPlace(.., 0x2b, 物品))。
namespace Dnf.Game
{
    /// <summary>
    /// 一件物品在物品脚本里写明的、与仓库有关的三个事实(<see cref="ItemInfo"/> 里没有读这几个标签)。
    /// 没读到脚本时用 <see cref="None"/>:三项全假。
    /// </summary>
    public readonly struct StorageItemFacts
    {
        public StorageItemFacts(bool packagable, bool impossibleCharacCargo, bool upgradesCargo)
        {
            Packagable = packagable;
            ImpossibleCharacCargo = impossibleCharacCargo;
            UpgradesCargo = upgradesCargo;
        }

        /// <summary>脚本有 <c>[packagable]</c> 且值不为 0:账号金库只收这样的物品。</summary>
        public bool Packagable { get; }

        /// <summary><c>[impossible contents]</c> 里有 <c>charac cargo</c>:个人仓库不收。</summary>
        public bool ImpossibleCharacCargo { get; }

        /// <summary><c>[action type]</c> 的第一个字符串是 <c>[upgrade cargo]</c>:能用来扩个人仓库。</summary>
        public bool UpgradesCargo { get; }

        /// <summary>什么都没写。</summary>
        public static StorageItemFacts None => default;
    }

    /// <summary>从物品脚本里取 <see cref="StorageItemFacts"/>。</summary>
    public static class StorageItemScript
    {
        /// <summary>个人仓库不收的标记(<c>[impossible contents]</c> 里的字符串)。</summary>
        public const string ImpossibleCharacCargoWord = "charac cargo";

        /// <summary>个人仓库扩容的动作名(<c>[action type]</c> 的第一个字符串)。</summary>
        public const string UpgradeCargoAction = "[upgrade cargo]";

        /// <summary>
        /// 从脚本行里取。<c>[packagable]</c> 取第一个整数(没有整数按 0);<c>[impossible contents]</c> 从头连着读字符串、读到第一个不是字符串的值为止
        /// (服务端是 ScanStr 一路读到失败);<c>[action type]</c> 只看第一个值是不是字符串 <c>[upgrade cargo]</c>。
        /// 同一个标签出现多次时:<c>[packagable]</c> 与 <c>[action type]</c> 以最后一处为准(服务端后写覆盖),<c>[impossible contents]</c> 的标志只增不减。
        /// 脚本为空返回 <see cref="StorageItemFacts.None"/>。
        /// </summary>
        public static StorageItemFacts FromScript(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return StorageItemFacts.None;
            }
            bool packagable = false;
            bool impossible = false;
            bool upgrades = false;
            foreach (ScriptLine line in lines)
            {
                if (line.TagIs("[packagable]"))
                {
                    List<int> ints = line.Ints();
                    packagable = ints.Count > 0 && ints[0] != 0;
                }
                else if (line.TagIs("[impossible contents]"))
                {
                    foreach (ScriptValue value in line.Values)
                    {
                        if (value.Kind != ScriptValueKind.String)
                        {
                            break;
                        }
                        if (string.Equals(value.Text, ImpossibleCharacCargoWord, StringComparison.Ordinal))
                        {
                            impossible = true;
                        }
                    }
                }
                else if (line.TagIs("[action type]"))
                {
                    upgrades = line.Values.Length > 0 && line.Values[0].Kind == ScriptValueKind.String
                        && string.Equals(line.Values[0].Text, UpgradeCargoAction, StringComparison.Ordinal);
                }
            }
            return new StorageItemFacts(packagable, impossible, upgrades);
        }

        /// <summary>读物品脚本取事实;读不到脚本返回 <see cref="StorageItemFacts.None"/>。</summary>
        public static StorageItemFacts Read(GameData data, ItemInfo info)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return info == null ? StorageItemFacts.None : FromScript(data.ReadScript(info.Path));
        }
    }
}
