using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇场景的 Unity 视图:读地图的 [animation] 图层与 [NPC] 列表(证据 E-024、E-030 的数据链),按时间选 .ani 帧,
    /// 交给 <see cref="SpriteStage"/> 画。加法混合的光效与染色 alpha 的帧间插值见证据 E-030。
    /// 没有主角、名牌、界面与特效。
    /// </summary>
    public sealed class TownView : IDisposable
    {
        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedNpc> _npcs;
        private readonly List<TownScene.ResolvedLayer> _layers;
        private TownProjection _projection;

        public SpriteStage Stage { get; }
        public int NpcCount => _npcs.Count;
        public int LayerCount => _layers.Count;

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

        /// <summary>把场景摆到 <paramref name="timeMs"/> 时刻并返回这次的绘制列表(与 CPU 合成共用)。</summary>
        public List<SceneSprite> ShowAt(long timeMs)
        {
            List<SceneSprite> sprites = TownScene.BuildResolvedScene(_assets, _layers, _npcs, _projection, timeMs);
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
