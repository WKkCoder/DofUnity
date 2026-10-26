using System;
using System.Runtime.CompilerServices;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    /// <summary>
    /// 从用户自己的 PVF 里读来的怪物金币掉落数据(证据 E-116):<c>Etc/ItemDropInfo_Monseter.etc</c>(掉落概率行与各档倍率)
    /// 与 <c>Etc/ItemDropInfo_Common.etc</c>(按怪物等级的基础金额)。两个都读到才算有数据;缺一个或读错时 <see cref="Table"/>、<see cref="Gold"/> 为 null,
    /// 杀怪不掉金币(旧行为),原因写在 <see cref="Error"/>。同一个 <see cref="DnfAssets"/> 只读一次(一座副本每个房间各建一个战斗会话)。
    /// 物品掉落池(<see cref="Pool"/>,证据 E-117)要整库扫描装备与可叠加物品的脚本头,第一次用到时才建(同样每份素材只建一次)。
    /// 物件掉物品(M34,证据 E-126)另用 <c>Etc/ItemDropInfo_Object.etc</c>(<see cref="ObjectTable"/>、<see cref="ObjectPool"/>,格式同怪物掉落表),
    /// 与怪物的池共用同一次整库扫描;这张表读不到不影响怪物掉落,只是物件没有随机物品。
    /// </summary>
    public sealed class MonsterDrops
    {
        /// <summary>掉落概率表的脚本路径(原版数据文件名就是这样拼的)。</summary>
        public const string MonsterPath = "Etc/ItemDropInfo_Monseter.etc";

        /// <summary>基础金额表的脚本路径。</summary>
        public const string CommonPath = "Etc/ItemDropInfo_Common.etc";

        /// <summary>物件掉落概率表的脚本路径(服务端 <c>CObjectDrop::loadScript</c> 读它,证据 E-126)。</summary>
        public const string ObjectPath = "Etc/ItemDropInfo_Object.etc";

        private static readonly ConditionalWeakTable<DnfAssets, MonsterDrops> Cache = new ConditionalWeakTable<DnfAssets, MonsterDrops>();

        private readonly Func<ItemPool> _poolFactory;
        private ItemPool _pool;
        private bool _poolReady;
        private readonly Func<ItemPool> _objectPoolFactory;
        private ItemPool _objectPool;
        private bool _objectPoolReady;
        private readonly Func<int, ItemInfo> _resolver;

        /// <param name="pool">物品掉落池;测试里直接给一个合成的,不给就没有物品掉落。</param>
        /// <param name="objectTable">物件掉落表(没有就没有物件的随机物品)。</param>
        /// <param name="objectPool">按物件掉落表建的物品池。</param>
        /// <param name="resolver">按物品编号取物品(物件的"指定物品"用);不给就取不到。</param>
        public MonsterDrops(MonsterDropTable table, GoldGenTable gold, string error, ItemPool pool = null, MonsterDropTable objectTable = null, ItemPool objectPool = null,
            Func<int, ItemInfo> resolver = null)
        {
            Table = table;
            Gold = gold;
            Error = error;
            _pool = pool;
            _poolReady = true;
            ObjectTable = objectTable;
            _objectPool = objectPool;
            _objectPoolReady = true;
            _resolver = resolver;
        }

        private MonsterDrops(MonsterDropTable table, GoldGenTable gold, Func<ItemPool> poolFactory, MonsterDropTable objectTable, Func<ItemPool> objectPoolFactory,
            Func<int, ItemInfo> resolver)
        {
            Table = table;
            Gold = gold;
            _poolFactory = poolFactory;
            ObjectTable = objectTable;
            _objectPoolFactory = objectPoolFactory;
            _resolver = resolver;
        }

        public MonsterDropTable Table { get; }

        public GoldGenTable Gold { get; }

        /// <summary>读不到数据的原因;读到了是 null。</summary>
        public string Error { get; }

        /// <summary>物件掉落概率表(<see cref="ObjectPath"/>);读不到是 null。</summary>
        public MonsterDropTable ObjectTable { get; }

        /// <summary>按物件掉落表建的物品池(与 <see cref="Pool"/> 同一次整库扫描,第一次读时才建);没有表或没有可掉的物品是 null。</summary>
        public ItemPool ObjectPool
        {
            get
            {
                if (!_objectPoolReady)
                {
                    _objectPoolReady = true;
                    _objectPool = _objectPoolFactory?.Invoke();
                }
                return _objectPool;
            }
        }

        /// <summary>按物品编号取物品(物件的"指定物品");找不到或没有取法是 null。</summary>
        public ItemInfo ItemById(int id)
        {
            return _resolver?.Invoke(id);
        }

        /// <summary>两张表都读到了。</summary>
        public bool Available => Table != null && Gold != null;

        /// <summary>
        /// 物品掉落池(<see cref="ItemPool"/>:全部创建率不为 0 的装备与可叠加物品,按等级与稀有度分档,证据 E-117);第一次读它时整库扫描
        /// (真实数据约一秒,之后缓存);读不到物品列表、没有可掉的物品或没有掉落表时是 null(杀怪只掉金币)。
        /// </summary>
        public ItemPool Pool
        {
            get
            {
                if (!_poolReady)
                {
                    _poolReady = true;
                    _pool = _poolFactory?.Invoke();
                }
                return _pool;
            }
        }

        /// <summary>读两张表(按 <see cref="DnfAssets"/> 缓存);<paramref name="assets"/> 为 null 时抛 <see cref="ArgumentNullException"/>。</summary>
        public static MonsterDrops Load(DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            return Cache.GetValue(assets, ReadFrom);
        }

        private static MonsterDrops ReadFrom(DnfAssets assets)
        {
            ScriptCursor monster = Open(assets, MonsterPath, out string error);
            if (monster == null)
            {
                return new MonsterDrops(null, null, error);
            }
            MonsterDropTable table = MonsterDropTable.Load(monster, out error);
            if (table == null)
            {
                return new MonsterDrops(null, null, MonsterPath + ": " + error);
            }
            ScriptCursor common = Open(assets, CommonPath, out error);
            if (common == null)
            {
                return new MonsterDrops(null, null, error);
            }
            GoldGenTable gold = GoldGenTable.Load(common, out error);
            if (gold == null)
            {
                return new MonsterDrops(null, null, CommonPath + ": " + error);
            }
            // 物件掉落表读不到就只是没有物件的随机物品(证据 E-126),不影响怪物掉落
            MonsterDropTable objectTable = null;
            ScriptCursor objectCursor = Open(assets, ObjectPath, out string objectError);
            if (objectCursor != null)
            {
                objectTable = MonsterDropTable.Load(objectCursor, out objectError);
            }
            var catalog = new Lazy<ItemCatalog>(() => new ItemCatalog(new GameData(new PvfScriptSource(assets.Pvf))));
            var droppable = new Lazy<System.Collections.Generic.IReadOnlyList<ItemInfo>>(() => catalog.Value.Droppable());
            return new MonsterDrops(table, gold, () => BuildPool(droppable.Value, table), objectTable,
                objectTable == null ? null : (Func<ItemPool>)(() => BuildPool(droppable.Value, objectTable)), id => catalog.Value.Get(id));
        }

        private static ItemPool BuildPool(System.Collections.Generic.IReadOnlyList<ItemInfo> items, MonsterDropTable table)
        {
            return items == null || items.Count == 0 ? null : new ItemPool(items, table);
        }

        private static ScriptCursor Open(DnfAssets assets, string path, out string error)
        {
            error = null;
            if (assets.Pvf == null || !assets.Pvf.TryGetEntry(PvfPath.Normalize(path), out PvfEntry entry))
            {
                error = path + ": 没有这个脚本";
                return null;
            }
            byte[] raw = assets.Pvf.Read(entry);
            if (!TokenScript.IsTokenScript(raw))
            {
                error = path + ": 不是二进制脚本";
                return null;
            }
            return new ScriptCursor(TokenScript.Parse(raw), assets.Strings);
        }
    }
}
