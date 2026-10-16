using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇场景的 Unity 视图:读地图的 [animation] 图层与 [NPC] 列表(证据 E-024、E-030 的数据链),按时间选 .ani 帧,
    /// 交给 <see cref="SpriteStage"/> 画。加法混合的光效与染色 alpha 的帧间插值见证据 E-030。
    /// 有两种用法:只看场景(没有主角,镜头固定),或者交给一个 <see cref="TownSession"/>(带可操作的主角与跟随镜头,证据 E-065、E-066)。
    /// 没有名牌、界面与特效。
    /// </summary>
    public sealed class TownView : IDisposable
    {
        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedNpc> _npcs;
        private readonly List<TownScene.ResolvedLayer> _layers;
        private readonly TownSession _session;
        private TownProjection _projection;

        public SpriteStage Stage { get; }
        public int NpcCount => _session != null ? _session.NpcCount : _npcs.Count;
        public int LayerCount => _session != null ? _session.LayerCount : _layers.Count;

        /// <summary>带主角的用法里的城镇游戏;只看场景时为 null。</summary>
        public TownSession Session => _session;

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
        /// 用一场城镇游戏(地图、NPC、主角与镜头)作为内容;每次 <see cref="ShowAt"/> 取 <see cref="TownSession.Build"/> 的绘制列表。
        /// 主角的推进(<see cref="TownSession.Update"/>)由调用方负责。
        /// </summary>
        public TownView(DnfAssets assets, TownSession session, Color background)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            Stage = SpriteStage.Create(background, "DnfTown");
        }

        /// <summary>把场景摆到 <paramref name="timeMs"/> 时刻并返回这次的绘制列表(与 CPU 合成共用)。</summary>
        public List<SceneSprite> ShowAt(long timeMs)
        {
            List<SceneSprite> sprites = _session != null
                ? _session.Build(timeMs)
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
