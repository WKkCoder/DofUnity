using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码:配方不是单独的目录,而是 stackable.lst 里 [stackable type] 为 [recipe] 的那些可叠加物品(全库约六千多个,
// 大多在 stackable/recipe 下,副职业配方在 stackable/professional/recipe 下,另有零散的在活动目录里);配方的编号就是配方物品的编号。
namespace Dnf.Game
{
    /// <summary>
    /// 配方目录:按配方物品编号取 <see cref="RecipeDef"/>(缓存),以及全部配方编号(第一次调用整库扫一遍,之后用缓存)。
    /// 读不到列表、脚本,或编号不是配方物品时返回 null / 空表,不抛异常。
    /// </summary>
    public sealed class RecipeCatalog
    {
        private readonly GameData _data;
        private readonly ItemCatalog _items;
        private readonly Dictionary<int, RecipeDef> _cache = new Dictionary<int, RecipeDef>();
        private int[] _codes;

        public RecipeCatalog(GameData data, ItemCatalog items)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _items = items ?? throw new ArgumentNullException(nameof(items));
        }

        /// <summary>背后的物品目录(取产物、材料的物品信息用)。</summary>
        public ItemCatalog Items => _items;

        /// <summary>配方定义;编号不是配方物品(不在 stackable.lst、不是配方类、脚本读不到)返回 null。</summary>
        public RecipeDef Get(int code)
        {
            if (_cache.TryGetValue(code, out RecipeDef cached))
            {
                return cached;
            }
            RecipeDef def = null;
            ItemInfo info = _items.Get(code);
            PvfStringTable strings = _data.Strings;
            if (info != null && info.IsStackable && info.StackableType == StackableTypes.Recipe && strings != null
                && _data.TryReadBytes(info.Path, out byte[] bytes))
            {
                def = RecipeScript.Parse(bytes, strings, code, info.Path);
            }
            _cache[code] = def;
            return def;
        }

        /// <summary>全部配方编号(升序)。第一次调用要把 stackable.lst 里的物品都读一遍(缓存),之后直接用缓存。</summary>
        public IReadOnlyList<int> Codes()
        {
            if (_codes != null)
            {
                return _codes;
            }
            var list = new List<int>();
            foreach (int code in _items.StackableCodes())
            {
                ItemInfo info = _items.Get(code);
                if (info != null && info.StackableType == StackableTypes.Recipe && Get(code) != null)
                {
                    list.Add(code);
                }
            }
            _codes = list.ToArray();
            return _codes;
        }
    }
}
