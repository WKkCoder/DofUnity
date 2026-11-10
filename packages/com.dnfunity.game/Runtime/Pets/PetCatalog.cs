using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码:装载入口 initCreatureScript 0x08a35d37(目录、列表文件、经验表文件)与 CCreatureScriptMgr::LoadCreatureScriptFile 0x0833cba6
// (按列表逐个导入;脚本编号 = 列表里的键)。物品到宠物的对应:CCreature::GetCreatureId 0x0833769c(物品的 [creature species] → 脚本编号)。
namespace Dnf.Game
{
    /// <summary>
    /// 宠物目录:<c>creature/creature.lst</c> 里的"编号 → 脚本路径"、按需读出的 <see cref="PetDef"/>、全局经验表、通用配置,
    /// 以及从宠物物品(<see cref="ItemInfo"/>)到宠物定义的对应。读不到列表、脚本或配置时返回 null / 空表,不抛异常;结果会缓存。
    /// </summary>
    public sealed class PetCatalog
    {
        public const string ListPath = "creature/creature.lst";
        public const string ExpTablePath = "creature/exptable.tbl";
        public const string CommonPath = "creature/common/creaturecommon.cre";

        private readonly GameData _data;
        private readonly Dictionary<int, PetDef> _defs = new Dictionary<int, PetDef>();
        private readonly Dictionary<int, PetScriptReport> _reports = new Dictionary<int, PetScriptReport>();
        private readonly Dictionary<int, PetItemData> _items = new Dictionary<int, PetItemData>();
        private Dictionary<int, string> _paths;
        private int[] _ids;
        private PetExpTable _expTable;
        private bool _expTableRead;
        private PetCommon _common;
        private bool _commonRead;

        public PetCatalog(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        private void ReadList()
        {
            if (_paths != null)
            {
                return;
            }
            _paths = new Dictionary<int, string>();
            Dictionary<int, string> raw = _data.ReadList(ListPath);
            if (raw != null)
            {
                foreach (KeyValuePair<int, string> pair in raw)
                {
                    _paths[pair.Key] = PvfPath.ResolveRelativeTo(ListPath, pair.Value);
                }
            }
            _ids = new int[_paths.Count];
            _paths.Keys.CopyTo(_ids, 0);
            Array.Sort(_ids);
        }

        /// <summary>列表里的全部宠物编号(从小到大)。</summary>
        public IReadOnlyList<int> Ids
        {
            get
            {
                ReadList();
                return _ids;
            }
        }

        /// <summary>某只宠物脚本的完整路径;不在列表里返回 null。</summary>
        public string PathOf(int id)
        {
            ReadList();
            return _paths.TryGetValue(id, out string path) ? path : null;
        }

        /// <summary>宠物定义;不在列表里、读不到或不是标记流脚本返回 null。</summary>
        public PetDef Get(int id)
        {
            if (_defs.TryGetValue(id, out PetDef cached))
            {
                return cached;
            }
            PetDef def = null;
            PetScriptReport report = null;
            string path = PathOf(id);
            PvfStringTable strings = _data.Strings;
            if (path != null && strings != null && _data.TryReadBytes(path, out byte[] bytes))
            {
                def = PetScript.Parse(bytes, strings, id, path, out report);
            }
            _defs[id] = def;
            _reports[id] = report;
            return def;
        }

        /// <summary>读某只宠物脚本时的统计(没读过或读不到是 null)。</summary>
        public PetScriptReport ReportOf(int id)
        {
            return _reports.TryGetValue(id, out PetScriptReport report) ? report : null;
        }

        /// <summary>全局经验表;读不到返回 null。</summary>
        public PetExpTable ExpTable
        {
            get
            {
                if (!_expTableRead)
                {
                    _expTableRead = true;
                    PvfStringTable strings = _data.Strings;
                    if (strings != null && _data.TryReadBytes(ExpTablePath, out byte[] bytes))
                    {
                        _expTable = PetExpTable.Parse(bytes, strings);
                    }
                }
                return _expTable;
            }
        }

        /// <summary>通用配置;读不到返回 null。</summary>
        public PetCommon Common
        {
            get
            {
                if (!_commonRead)
                {
                    _commonRead = true;
                    PvfStringTable strings = _data.Strings;
                    if (strings != null && _data.TryReadBytes(CommonPath, out byte[] bytes))
                    {
                        _common = PetCommon.Parse(bytes, strings);
                    }
                }
                return _common;
            }
        }

        /// <summary>宠物物品或神器上的宠物字段(按物品脚本路径读,按物品编号缓存);物品为空或脚本读不到返回 null。</summary>
        public PetItemData ItemData(ItemInfo item)
        {
            if (item == null)
            {
                return null;
            }
            if (_items.TryGetValue(item.Code, out PetItemData cached))
            {
                return cached;
            }
            PetItemData data = null;
            PvfStringTable strings = _data.Strings;
            if (strings != null && item.Path != null && _data.TryReadBytes(item.Path, out byte[] bytes))
            {
                data = PetItemData.Parse(bytes, strings);
            }
            _items[item.Code] = data;
            return data;
        }

        /// <summary>
        /// 宠物物品对应的宠物定义(物品的 <c>[creature species]</c> 就是宠物编号,服务端 <c>GetCreatureId</c> 之后 <c>FindCreatureScript</c>)。
        /// 物品不是宠物类、不是装备(服务端要求不可叠加)、没写 species 或编号不在列表里,返回 null。
        /// </summary>
        public PetDef DefOf(ItemInfo petItem)
        {
            if (petItem == null || !petItem.IsEquipment || petItem.Part != EquipmentParts.Creature)
            {
                return null;
            }
            PetItemData data = ItemData(petItem);
            return data == null ? null : Get(data.Species);
        }
    }
}
