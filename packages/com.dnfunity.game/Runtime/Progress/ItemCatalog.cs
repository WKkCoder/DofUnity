using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-117(服务端 CItemList 装物品的次序:先读 equipment.lst、stackable.lst,再逐个脚本导入;insert_item(grade, code, item) 0x0851129e 按 [grade] 分桶)。
namespace Dnf.Game
{
    /// <summary>
    /// 物品目录:<c>equipment/equipment.lst</c> 与 <c>stackable/stackable.lst</c> 里的"编号 → 脚本路径",以及按需读出的 <see cref="ItemInfo"/>。
    /// 全库有 7 万多个 .equ 与 1 万多个 .stk,所以单件物品按需读(<see cref="Get"/>,缓存),只有建掉落池时才整库扫一遍(<see cref="Droppable"/>,
    /// 只留 <c>[creation rate]</c> 不为 0 的,几千件)。读不到列表或脚本时返回 null / 空表,不抛异常。
    /// </summary>
    public sealed class ItemCatalog
    {
        public const string EquipmentListPath = "equipment/equipment.lst";
        public const string StackableListPath = "stackable/stackable.lst";

        private readonly GameData _data;
        private readonly Dictionary<int, ItemInfo> _cache = new Dictionary<int, ItemInfo>();
        private List<ItemInfo> _droppable;
        private Dictionary<int, string> _equipment;
        private Dictionary<int, string> _stackable;
        private bool _listsRead;

        public ItemCatalog(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>整库扫描时读了多少个脚本(没扫过是 0)。</summary>
        public int Scanned { get; private set; }

        /// <summary>其中读不出来(缺文件或不是标记流)的个数。</summary>
        public int Failed { get; private set; }

        private void ReadLists()
        {
            if (_listsRead)
            {
                return;
            }
            _listsRead = true;
            _equipment = Resolve(EquipmentListPath);
            _stackable = Resolve(StackableListPath);
        }

        private Dictionary<int, string> Resolve(string listPath)
        {
            Dictionary<int, string> raw = _data.ReadList(listPath);
            var resolved = new Dictionary<int, string>();
            if (raw != null)
            {
                foreach (KeyValuePair<int, string> pair in raw)
                {
                    resolved[pair.Key] = PvfPath.ResolveRelativeTo(listPath, pair.Value);
                }
            }
            return resolved;
        }

        /// <summary>两张列表里一共列了多少件物品(装备、可叠加物品)。</summary>
        public int ListedCount
        {
            get
            {
                ReadLists();
                return _equipment.Count + _stackable.Count;
            }
        }

        /// <summary>按编号取一件物品(缓存);编号不在两张列表里、脚本读不到或不是标记流返回 null。</summary>
        public ItemInfo Get(int code)
        {
            if (_cache.TryGetValue(code, out ItemInfo cached))
            {
                return cached;
            }
            ReadLists();
            ItemInfo info = null;
            if (_equipment.TryGetValue(code, out string path))
            {
                info = ReadOne(code, path, ItemKind.Equipment);
            }
            else if (_stackable.TryGetValue(code, out path))
            {
                info = ReadOne(code, path, ItemKind.Stackable);
            }
            _cache[code] = info;
            return info;
        }

        private ItemInfo ReadOne(int code, string path, ItemKind kind)
        {
            PvfStringTable strings = _data.Strings;
            if (strings == null || !_data.TryReadBytes(path, out byte[] bytes))
            {
                return null;
            }
            try
            {
                return ItemScript.Parse(bytes, strings, kind, code, path);
            }
            catch (DnfFormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// 能进掉落池的物品(<c>[creation rate]</c> 不为 0 的,按编号升序,装备在前、可叠加物品在后);第一次调用时整库扫描,之后用缓存。
        /// 特殊怪物掉落的物品也在表里(<see cref="ItemInfo.SpecialMonsterDrop"/>),由 <see cref="ItemPool"/> 排除。
        /// </summary>
        public IReadOnlyList<ItemInfo> Droppable()
        {
            if (_droppable != null)
            {
                return _droppable;
            }
            ReadLists();
            var list = new List<ItemInfo>();
            Scan(_equipment, ItemKind.Equipment, list);
            Scan(_stackable, ItemKind.Stackable, list);
            _droppable = list;
            return list;
        }

        private void Scan(Dictionary<int, string> codes, ItemKind kind, List<ItemInfo> into)
        {
            var sorted = new List<int>(codes.Keys);
            sorted.Sort();
            foreach (int code in sorted)
            {
                ItemInfo info;
                if (!_cache.TryGetValue(code, out info))
                {
                    Scanned++;
                    info = ReadOne(code, codes[code], kind);
                    if (info == null)
                    {
                        Failed++;
                        continue;
                    }
                    if (info.CreationRate != 0)
                    {
                        _cache[code] = info;   // 只缓存要用的几千件,其余不留(整库一起留会占几十兆)
                    }
                }
                if (info != null && info.CreationRate != 0)
                {
                    into.Add(info);
                }
            }
        }
    }
}
