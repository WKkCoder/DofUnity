using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 流程眼里的"城镇世界"(证据 E-095):登录的第一站、从副本回来的落脚点、造某块区域的会话、走出口去哪、换区域要不要加载画面、
    /// 自动机该往哪个出口走。流程只依赖这个接口,所以不带任何原版数据的假世界就能测流程的逻辑。
    /// </summary>
    public interface ITownWorld
    {
        /// <summary>登录进城的第一站。</summary>
        TownPlace Start { get; }

        /// <summary>从副本或副本选择界面回到城镇的落脚点(地下城入口区域)。</summary>
        TownPlace Return { get; }

        /// <summary>造 <paramref name="place"/> 这块区域的会话。</summary>
        IHeroSession Create(TownPlace place);

        /// <summary>
        /// 从 <paramref name="from"/>(为 null 表示从城镇之外来:登录、副本)进入 <paramref name="to"/> 要不要加载画面;不要时返回 null。
        /// </summary>
        SplashScene Splash(TownPlace? from, TownPlace to);

        /// <summary>主角在 <paramref name="from"/> 走进了出口 <paramref name="exit"/>,落到哪(没有去处时返回 null)。<paramref name="facingLeft"/> 是主角的朝向。</summary>
        TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft);

        /// <summary>从 <paramref name="from"/> 往地下城入口走,第一步该走哪个出口(在入口区域里就是通世界地图的出口);走不到时返回 false。自动机用。</summary>
        bool TryRoute(TownPlace from, out TownExit exit);
    }

    /// <summary>
    /// 老式的单张城镇会话工厂(演示、旧测试):没有出口、没有加载画面;每次回城都造一个新会话。
    /// </summary>
    public sealed class LegacyTownWorld : ITownWorld
    {
        private readonly Func<IHeroSession> _factory;

        public LegacyTownWorld(Func<IHeroSession> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public TownPlace Start => new TownPlace(0, 0);

        public TownPlace Return => new TownPlace(0, 0, TownEntry.Return);

        public IHeroSession Create(TownPlace place) => _factory();

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => null;

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            exit = default;
            return false;
        }
    }

    /// <summary>
    /// 用城镇图册与游戏数据造的真实城镇世界(证据 E-095)。登录进 <paramref name="startTown"/> 的网关区域(没有网关就用第一块区域),
    /// 回城落在该城镇的地下城入口区域。换区域要不要加载画面(<see cref="NeedsSplash"/>)的规则是推断。
    /// 主角的换装在第一次造会话时读出来,之后每块区域共用。
    /// </summary>
    public sealed class AtlasTownWorld : ITownWorld
    {
        private readonly DnfAssets _assets;
        private readonly TownAtlas _atlas;
        private readonly Dictionary<int, SplashScene> _splashes = new Dictionary<int, SplashScene>();
        private readonly bool _withSplashes;
        private AvatarOutfit _outfit;
        private string _outfitWeapon;
        private readonly Func<string> _weaponPath;

        /// <param name="withSplashes">为 false 时换区域一律不停加载画面(无人值守检查与离屏对拍用)。</param>
        /// <param name="weaponPath">给了就在每次造城镇会话时取主角现在穿着的武器的脚本路径(没穿返回 null,用起手的演示武器,证据 E-118);武器变了才重造外观。</param>
        public AtlasTownWorld(DnfAssets assets, TownAtlas atlas, int startTown = 1, bool withSplashes = true, Func<string> weaponPath = null)
        {
            _withSplashes = withSplashes;
            _weaponPath = weaponPath;
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
            TownDef def = atlas.GetTown(startTown) ?? throw new ArgumentException("城镇图册里没有城镇 " + startTown, nameof(startTown));
            int first = -1;
            int gate = -1;
            foreach (TownArea a in def.Areas)
            {
                if (first < 0 || a.Id < first)
                {
                    first = a.Id;
                }
                if (a.Kind == TownAreaKind.Gate && (gate < 0 || a.Id < gate))
                {
                    gate = a.Id;
                }
            }
            if (first < 0)
            {
                throw new ArgumentException("城镇 " + startTown + " 没有区域", nameof(startTown));
            }
            Start = new TownPlace(startTown, gate >= 0 ? gate : first);
            TownPlace? dungeonGate = atlas.FindDungeonGate(startTown);
            Return = dungeonGate ?? new TownPlace(Start.Town, Start.Area, TownEntry.Return);
        }

        public TownAtlas Atlas => _atlas;

        public TownPlace Start { get; }

        public TownPlace Return { get; }

        public IHeroSession Create(TownPlace place)
        {
            string weapon = _weaponPath?.Invoke() ?? DemoHero.StarterWeapon;
            if (_outfit == null || !string.Equals(weapon, _outfitWeapon, StringComparison.OrdinalIgnoreCase))
            {
                AvatarOutfit outfit = DemoHero.CreateOutfit(_assets, weapon);
                _outfit = HeroLook.WeaponPathOf(outfit) != null || _outfit == null ? outfit : _outfit;   // 新武器读不出外观就沿用原来的
                _outfitWeapon = weapon;
            }
            return TownWorld.CreateSession(_assets, _atlas, place, _outfit);
        }

        /// <summary>
        /// 换区域要不要加载画面(推断,原版只有"从网关走到艾尔文防线"一张加载画面截图):从城镇之外来要;换城镇要;
        /// 起点或终点是网关区域要;同一座城镇里普通区域之间(含地下城入口区域)不要。
        /// </summary>
        public bool NeedsSplash(TownPlace? from, TownPlace to) => NeedsSplash(_atlas, from, to);

        /// <summary><see cref="NeedsSplash(TownPlace?, TownPlace)"/> 的规则本体(只读图册,测试可以不带游戏数据直接用)。</summary>
        public static bool NeedsSplash(TownAtlas atlas, TownPlace? from, TownPlace to)
        {
            if (!from.HasValue || from.Value.Town != to.Town)
            {
                return true;
            }
            TownAreaInfo a = atlas.GetArea(from.Value.Town, from.Value.Area);
            TownAreaInfo b = atlas.GetArea(to.Town, to.Area);
            return (a != null && a.Kind == TownAreaKind.Gate) || (b != null && b.Kind == TownAreaKind.Gate);
        }

        public SplashScene Splash(TownPlace? from, TownPlace to)
        {
            if (!_withSplashes || !NeedsSplash(from, to))
            {
                return null;
            }
            if (_splashes.TryGetValue(to.Town, out SplashScene cached))
            {
                return cached;
            }
            SplashScene scene = SplashCatalog.ForTown(_assets, _atlas.GetTown(to.Town));
            _splashes[to.Town] = scene;
            return scene;
        }

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            if (!exit.HasTarget)
            {
                return null;
            }
            return new TownPlace(exit.Town, exit.Area, TownEntry.Exit, from.Town, from.Area, facingLeft);
        }

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            return _atlas.TryRouteToDungeonGate(from, out exit);
        }
    }
}
