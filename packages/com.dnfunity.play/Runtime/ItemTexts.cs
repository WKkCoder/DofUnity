using System;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 物品的文字:名字、说明、背景故事,按物品脚本里的文字引用(<see cref="ItemInfo.TextKind"/> + 键)去字符串表(<see cref="StrLibrary"/>)里取。
    /// 取不到(没装这份文本、键不在表里、值是空的)返回 null,调用方退回占位画法。文字是用户自备数据里的原版内容,只在运行时读,不入库。
    /// 表里的百分号写成 <c>%%</c>(printf 风格的转义,物品与商店文字里只有这一种 % 写法,证据 E-121),显示时还原成一个 %。
    /// </summary>
    public sealed class ItemTexts
    {
        private readonly StrLibrary _library;

        public ItemTexts(StrLibrary library)
        {
            _library = library ?? throw new ArgumentNullException(nameof(library));
        }

        /// <summary>物品名;取不到返回 null。</summary>
        public string NameOf(ItemInfo item)
        {
            return Resolve(item, item?.NameKey);
        }

        /// <summary>说明:优先 <c>[explain]</c>,没有就用 <c>[basic explain]</c>;都没有或取不到返回 null。</summary>
        public string ExplainOf(ItemInfo item)
        {
            return Resolve(item, item?.ExplainKey) ?? Resolve(item, item?.BasicExplainKey);
        }

        /// <summary>背景故事一类的文字(<c>[flavor text]</c>);取不到返回 null。</summary>
        public string FlavorOf(ItemInfo item)
        {
            return Resolve(item, item?.FlavorKey);
        }

        /// <summary>商店一页的名字(<c>[tab name]</c> 的键,种类是商店文本);没写键或取不到返回 null。</summary>
        public string ShopTabName(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            return Display(_library.Get(StrLibrary.KindItemShop, key));
        }

        private string Resolve(ItemInfo item, string key)
        {
            if (item == null || string.IsNullOrEmpty(key))
            {
                return null;
            }
            int kind = item.TextKind >= 0 ? item.TextKind : (item.IsEquipment ? StrLibrary.KindEquipment : StrLibrary.KindStackable);
            return Display(_library.Get(kind, key));
        }

        /// <summary>空值当作没有;<c>%%</c> 还原成 <c>%</c>。</summary>
        public static string Display(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }
            return raw.IndexOf('%') < 0 ? raw : raw.Replace("%%", "%");
        }
    }
}
