using System;
using System.Collections.Generic;
using Dnf.Render;

// 把宠物接进城镇会话的装饰器:不改 TownSession、不改 Render 层,只是在现有会话外面包一层——
// 每帧先让里面的会话走(主角、镜头、出口),再让宠物场按主角的新位置跟一步;出图时把宠物精灵并进里面会话画好的列表、整体按画家算法再排一次
// (里面的列表已经排好,稳定排序对它是幂等的,宠物与同 y 的东西排在后面,压在前面)。
// 会话的几个可选接口(出口、地图声音、NPC 表)原样转给里面的会话,流程(GameFlow、FlowBot)按接口认会话,所以照常工作。
// 接线时要注意的两处宿主代码:TownDemo 里有 `Session as TownSession` 的强转(名牌、任务标记)与 `TownWorld is LegacyTownWorld` 的判断,
// 包了装饰器之后它们认不出里面的会话,用 TownPetScene.Unwrap 与 PetTownWorld.Unwrap 取回里面的对象(本线不改宿主,见交接说明)。
namespace Dnf.Play
{
    /// <summary>带宠物的城镇会话:对外就是一个 <see cref="IHeroSession"/>(以及出口、地图声音、NPC 表这几个可选接口),宠物跟在主角后面。</summary>
    public sealed class TownPetScene : IHeroSession, ITownExitSource, IMapSoundSource, ITownNpcSource
    {
        private readonly IHeroSession _inner;
        private readonly TownPetField _pets;
        private readonly int _groundY;

        /// <param name="inner">被包的城镇会话。</param>
        /// <param name="pets">宠物场(可以跨会话共用一个:宠物不随换区域重来,新会话一造好,已有的实体就落在新场景的主角身边)。</param>
        /// <param name="groundY">投影用的地面 y;里面是 <see cref="TownSession"/> 时以它自己的为准。</param>
        public TownPetScene(IHeroSession inner, TownPetField pets, int groundY = TownSession.DefaultGroundY)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pets = pets ?? throw new ArgumentNullException(nameof(pets));
            _groundY = groundY;
            if (TryMaster(out PetMaster master))
            {
                _pets.Arrive(master);
            }
        }

        /// <summary>被包的会话。</summary>
        public IHeroSession Inner => _inner;

        public TownPetField Pets => _pets;

        /// <summary>剥掉所有宠物装饰器,取回最里面的会话(宿主那些要认 <see cref="TownSession"/> 的地方用)。</summary>
        public static IHeroSession Unwrap(IHeroSession session)
        {
            while (session is TownPetScene wrapped)
            {
                session = wrapped._inner;
            }
            return session;
        }

        /// <summary>主角对宠物来说的位置与朝向。</summary>
        public static PetMaster MasterOf(TownHero hero)
        {
            return new PetMaster(hero.X, hero.Y, hero.FacingLeft);
        }

        /// <summary>
        /// 此刻的投影(与里面会话出图用的同一个:里面是 <see cref="TownSession"/> 就用它自己的,否则镜头 x 加构造时给的地面 y);
        /// 里面的会话没有镜头(只会是测试里的假会话)时返回 false。
        /// </summary>
        public bool TryProjection(out TownProjection projection)
        {
            if (_inner is TownSession town)
            {
                projection = town.Projection;
                return true;
            }
            TownCamera camera = _inner.Camera;
            if (camera == null)
            {
                projection = default;
                return false;
            }
            projection = new TownProjection(camera.IntX, _groundY);
            return true;
        }

        /// <summary>主角此刻对宠物来说的位置与朝向;里面的会话没有主角(只会是测试里的假会话)时返回 false。</summary>
        public bool TryMaster(out PetMaster master)
        {
            TownHero hero = _inner.Hero;
            if (hero == null)
            {
                master = default;
                return false;
            }
            master = MasterOf(hero);
            return true;
        }

        public string MapPath => _inner.MapPath;

        public TownHero Hero => _inner.Hero;

        public TownCamera Camera => _inner.Camera;

        public int LayerCount => _inner.LayerCount;

        public int NpcCount => _inner.NpcCount;

        /// <summary>里面的会话先走(主角、镜头、出口),然后宠物按主角的新位置跟一步。</summary>
        public void Update(double deltaMs, int dirX, int dirY)
        {
            _inner.Update(deltaMs, dirX, dirY);
            if (TryMaster(out PetMaster master))
            {
                _pets.Advance(deltaMs, master);
            }
        }

        /// <summary>里面的绘制列表加上宠物精灵,整体按画家算法排序;没有宠物精灵时原样返回里面的列表。</summary>
        public List<SceneSprite> Build(long timeMs)
        {
            List<SceneSprite> scene = _inner.Build(timeMs);
            if (!TryMaster(out PetMaster master) || !TryProjection(out TownProjection projection))
            {
                return scene;
            }
            List<SceneSprite> pets = _pets.BuildSprites(projection, master);
            if (pets.Count == 0)
            {
                return scene;
            }
            var merged = new List<SceneSprite>(scene.Count + pets.Count);
            merged.AddRange(scene);
            merged.AddRange(pets);
            return TownScene.SortByDepth(merged);
        }

        public TownExit? PendingExit => (_inner as ITownExitSource)?.PendingExit;

        public void OutwardOf(TownExit exit, out int dx, out int dy)
        {
            if (_inner is ITownExitSource source)
            {
                source.OutwardOf(exit, out dx, out dy);
                return;
            }
            dx = 0;
            dy = 0;
        }

        public double HeroX => _inner is ITownExitSource source ? source.HeroX : HeroOrNull?.X ?? 0.0;

        public double HeroY => _inner is ITownExitSource source ? source.HeroY : HeroOrNull?.Y ?? 0.0;

        public bool HeroFacingLeft => _inner is ITownExitSource source ? source.HeroFacingLeft : HeroOrNull?.FacingLeft ?? false;

        private TownHero HeroOrNull => _inner.Hero;

        public IReadOnlyList<string> MapSounds => (_inner as IMapSoundSource)?.MapSounds ?? Array.Empty<string>();

        public IReadOnlyList<TownNpc> Npcs => (_inner as ITownNpcSource)?.Npcs ?? Array.Empty<TownNpc>();
    }

    /// <summary>
    /// 带宠物的城镇世界:每造一块区域的会话,就给它包上宠物装饰器(<see cref="TownPetScene"/>),并告诉宠物场"进城了"
    /// (<see cref="TownPetField.EnterTown"/>:从副本回来时宠物从消耗中切回回补,饿死的在这时复活)。其余(起点、出口去向、加载画面、自动机路线)原样转给里面的世界。
    /// </summary>
    public sealed class PetTownWorld : ITownWorld
    {
        private readonly ITownWorld _inner;
        private readonly TownPetField _pets;

        public PetTownWorld(ITownWorld inner, TownPetField pets)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pets = pets ?? throw new ArgumentNullException(nameof(pets));
        }

        /// <summary>被包的世界。</summary>
        public ITownWorld Inner => _inner;

        public TownPetField Pets => _pets;

        /// <summary>剥掉宠物装饰器,取回最里面的世界(宿主里按具体世界类型判断的地方用)。</summary>
        public static ITownWorld Unwrap(ITownWorld world)
        {
            while (world is PetTownWorld wrapped)
            {
                world = wrapped._inner;
            }
            return world;
        }

        public TownPlace Start => _inner.Start;

        public TownPlace Return => _inner.Return;

        public IHeroSession Create(TownPlace place)
        {
            IHeroSession session = _inner.Create(place);
            if (session == null)
            {
                return null;
            }
            _pets.EnterTown();
            return new TownPetScene(session, _pets);
        }

        public SplashScene Splash(TownPlace? from, TownPlace to)
        {
            return _inner.Splash(from, to);
        }

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            return _inner.Through(from, exit, facingLeft);
        }

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            return _inner.TryRoute(from, out exit);
        }
    }
}
