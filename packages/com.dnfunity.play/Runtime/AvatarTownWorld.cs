using System;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 给城镇世界(<see cref="ITownWorld"/>)包一层装扮(r15-avatarhost):每次造某块区域的会话,都让那个会话里的主角穿上装扮会话(<see cref="AvatarSession"/>)
    /// 当下的外观——所以换区域、从副本回城、读档之后进城,主角身上穿着的装扮都在;其余成员(起点、回城点、加载画面、出口、自动机路线)原样交给被包的世界。
    /// 包的是城镇世界这一层的"造会话"入口,没有新造渲染:外观仍是 <see cref="AvatarOutfit"/>,仍赋给 <see cref="TownHero.Outfit"/>,与 <see cref="HeroLook"/> 的做法一致。
    /// 老式的单会话工厂(<see cref="LegacyTownWorld"/>)不包:宿主有几处按它的类型分支。
    /// </summary>
    public sealed class AvatarTownWorld : ITownWorld
    {
        private readonly ITownWorld _inner;
        private readonly AvatarSession _avatar;

        public AvatarTownWorld(ITownWorld inner, AvatarSession avatar)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _avatar = avatar ?? throw new ArgumentNullException(nameof(avatar));
        }

        /// <summary>被包的城镇世界。</summary>
        public ITownWorld Inner => _inner;

        /// <summary>给主角穿装扮的会话。</summary>
        public AvatarSession Avatar => _avatar;

        public TownPlace Start => _inner.Start;

        public TownPlace Return => _inner.Return;

        public IHeroSession Create(TownPlace place)
        {
            IHeroSession session = _inner.Create(place);
            _avatar.Sync(session?.Hero);
            return session;
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => _inner.Splash(from, to);

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => _inner.Through(from, exit, facingLeft);

        public bool TryRoute(TownPlace from, out TownExit exit) => _inner.TryRoute(from, out exit);
    }
}
