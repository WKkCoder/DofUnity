using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇场景的 Unity 视图:读地图的 [animation] 图层与 [NPC] 列表(证据 E-024、E-030 的数据链),按时间选 .ani 帧,
    /// 交给 <see cref="SpriteStage"/> 画。加法混合的光效与染色 alpha 的帧间插值见证据 E-030。
    /// 有三种用法:只看场景(没有主角,镜头固定),或者交给一场 <see cref="IHeroSession"/>(城镇的 <see cref="TownSession"/> 或副本房间的 <see cref="RoomSession"/>,
    /// 带可操作的主角与跟随镜头,证据 E-065、E-066、E-068),或者交给任意 <see cref="ISceneSource"/>(没有主角的整屏界面,例如副本选择)。
    /// </summary>
    public sealed class TownView : IDisposable
    {
        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedNpc> _npcs;
        private readonly List<TownScene.ResolvedLayer> _layers;
        private readonly ISceneSource _source;
        private readonly IHeroSession _session;
        private TownProjection _projection;

        public SpriteStage Stage { get; }
        public int NpcCount => _session != null ? _session.NpcCount : _npcs != null ? _npcs.Count : 0;
        public int LayerCount => _session != null ? _session.LayerCount : _layers != null ? _layers.Count : 0;

        /// <summary>带主角的用法里的游戏(城镇或副本房间);只看场景或整屏界面时为 null。</summary>
        public IHeroSession Session => _session;

        /// <summary>交给视图的内容(带主角的游戏或整屏界面);只看场景时为 null。</summary>
        public ISceneSource Source => _source;

        /// <summary>
        /// 打开地图并解析图层与 NPC。<paramref name="assets"/> 的所有权不转移(由调用方释放)。
        /// </summary>
        public TownView(DnfAssets assets, string mapPath, TownProjection projection, Color background)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _projection = projection;
            _layers = TownScene.ResolveLayers(assets, mapPath);
            List<TownNpc> npcs = TownScene.ReadMapNpcs(assets, mapPath);
            _npcs = TownScene.Resolve(assets, npcs);
            Stage = SpriteStage.Create(background, "DnfTown");
        }

        /// <summary>
        /// 用一场游戏(地图、主角与镜头;城镇还有 NPC)或一张整屏界面作为内容;每次 <see cref="ShowAt"/> 取 <see cref="ISceneSource.Build"/> 的绘制列表。
        /// 主角的推进(<see cref="IHeroSession.Update"/>)由调用方负责。
        /// </summary>
        public TownView(DnfAssets assets, ISceneSource source, Color background)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _session = source as IHeroSession;
            Stage = SpriteStage.Create(background, "DnfTown");
        }

        /// <summary>把场景摆到 <paramref name="timeMs"/> 时刻并返回这次的绘制列表(与 CPU 合成共用)。</summary>
        public List<SceneSprite> ShowAt(long timeMs)
        {
            List<SceneSprite> sprites = _source != null
                ? _source.Build(timeMs)
                : TownScene.BuildResolvedScene(_assets, _layers, _npcs, _projection, timeMs);
            Stage.SetSprites(sprites);
            return sprites;
        }

        public void Dispose()
        {
            if (Stage != null)
            {
                UnityObjects.Destroy(Stage.gameObject);
            }
        }
    }
}
