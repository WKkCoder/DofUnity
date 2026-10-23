using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

// 证据:E-119(NPC 脚本的 [role] "[item shop]" N 里的 N 是 itemshop.lst 的编号;"走到 NPC 旁按键交互"的距离与按键是本项目自己的设计)。
namespace Dnf.Play
{
    /// <summary>
    /// 城镇里的商店 NPC:NPC 编号 → 它的商店编号(读 NPC 脚本的 <c>[role]</c>,结果缓存),以及"主角身边有没有商店 NPC"的判定。
    /// 判定范围是以 NPC 的摆放点为中心的椭圆(横向 <see cref="RangeX"/>、纵深 <see cref="RangeY"/> 个世界像素;本项目自己的设计,
    /// 原版的交互距离没有读到),范围里有好几个商店 NPC 时取最近的(按椭圆归一化后的距离)。
    /// </summary>
    public sealed class TownShopNpcs
    {
        /// <summary>
        /// 交互范围:横向与纵深(世界像素,以 NPC 摆放点为中心的椭圆半轴)。本项目自己的取值:横向放宽到 240,
        /// 是因为有的商店 NPC 站在可走区域外面一截,主角站在可走区域的边缘也得够得着它(<c>shop-check</c> 逐个城镇区域核对过,放到 240 都够得着);
        /// 纵深 60 大致是一条走道的宽度。
        /// </summary>
        public const int RangeX = 240;
        public const int RangeY = 60;

        private readonly Func<int, int> _shopOfNpc;
        private readonly Func<int, bool> _shopExists;
        private readonly Dictionary<int, int> _cache = new Dictionary<int, int>();

        /// <param name="shopOfNpc">NPC 编号 → 商店编号(没有商店返回负数);结果缓存,所以每个编号只问一次。</param>
        /// <param name="shopExists">
        /// 商店编号 → 这家店读得出来吗(null 表示不核对)。NPC 角色里写的编号可能指向 <c>itemshop.lst</c> 里没有的店
        /// (公会守护石一类,数据里就是悬空的):核对不过的当成"这个 NPC 不开商店",免得走到它身边出现"空格开商店"的提示却打不开。
        /// </param>
        public TownShopNpcs(Func<int, int> shopOfNpc, Func<int, bool> shopExists = null)
        {
            _shopOfNpc = shopOfNpc ?? throw new ArgumentNullException(nameof(shopOfNpc));
            _shopExists = shopExists;
        }

        /// <summary>这个 NPC 开的商店编号;不开商店(或写的商店读不出来)返回 −1。</summary>
        public int ShopOf(int npcId)
        {
            if (!_cache.TryGetValue(npcId, out int shop))
            {
                shop = _shopOfNpc(npcId);
                if (shop < 0 || (_shopExists != null && !_shopExists(shop)))
                {
                    shop = -1;
                }
                _cache[npcId] = shop;
            }
            return shop;
        }

        /// <summary>
        /// 主角 (<paramref name="heroX"/>, <paramref name="heroY"/>) 身边最近的商店 NPC;范围内没有返回 false。
        /// <paramref name="npcs"/> 为 null 或空也返回 false。
        /// </summary>
        public bool TryFindNear(IReadOnlyList<TownNpc> npcs, double heroX, double heroY, out TownNpc npc, out int shopIndex)
        {
            npc = default;
            shopIndex = -1;
            if (npcs == null)
            {
                return false;
            }
            double best = double.MaxValue;
            for (int i = 0; i < npcs.Count; i++)
            {
                TownNpc n = npcs[i];
                double dx = (n.X - heroX) / RangeX, dy = (n.Y - heroY) / RangeY;
                double d = dx * dx + dy * dy;
                if (d > 1.0 || d >= best)
                {
                    continue;
                }
                int shop = ShopOf(n.Id);
                if (shop < 0)
                {
                    continue;
                }
                best = d;
                npc = n;
                shopIndex = shop;
            }
            return shopIndex >= 0;
        }

        /// <summary>
        /// 用游戏素材造:NPC 编号走 <c>npc/npc.lst</c> 找到脚本,读脚本的 <c>[role]</c> 里的商店编号(列表或脚本读不到的 NPC 没有商店)。
        /// <paramref name="onlyReadableShops"/> 为真(默认)时,角色里写的商店在 <c>itemshop.lst</c> 里找不到(或脚本读不出来)的 NPC 也当成没有商店;
        /// 传 false 拿到角色里写的原始编号(核对工具用,看数据里有多少悬空的)。
        /// </summary>
        public static TownShopNpcs FromAssets(DnfAssets assets, bool onlyReadableShops = true)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            Dictionary<int, string> catalog = null;
            ItemShopCatalog shops = onlyReadableShops ? new ItemShopCatalog(new GameData(new Dnf.Script.PvfScriptSource(assets.Pvf))) : null;
            return new TownShopNpcs(id =>
            {
                if (catalog == null)
                {
                    try
                    {
                        catalog = TownScene.ReadNpcCatalog(assets);
                    }
                    catch (DnfFormatException)
                    {
                        catalog = new Dictionary<int, string>();
                    }
                }
                if (!catalog.TryGetValue(id, out string file))
                {
                    return -1;
                }
                return NpcRoles.ItemShopIndex(assets.ReadScript(PvfPath.ResolveRelativeTo("npc/", file)));
            }, shops == null ? (Func<int, bool>)null : index => shops.Get(index) != null);
        }
    }
}
