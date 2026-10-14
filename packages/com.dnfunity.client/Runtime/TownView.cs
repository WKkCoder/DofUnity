using System;
using System.Collections.Generic;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇场景的 Unity 视图:读地图的 [NPC] 列表(证据 E-024 的数据链),按时间选 .ani 帧,交给 <see cref="SpriteStage"/> 画。
    /// 目前只有 NPC;没有地图背景、主角与特效。
    /// </summary>
    public sealed class TownView : IDisposable
    {
        private readonly DnfAssets _assets;
        private readonly List<TownScene.ResolvedNpc> _npcs;
        private TownProjection _projection;

        public SpriteStage Stage { get; }
        public int NpcCount => _npcs.Count;

        /// <summary>
        /// 打开地图并解析 NPC。<paramref name="assets"/> 的所有权不转移(由调用方释放)。
        /// </summary>
        public TownView(DnfAssets assets, string mapPath, TownProjection projection, Color background)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _projection = projection;
            List<TownNpc> npcs = TownScene.ReadMapNpcs(assets, mapPath);
            _npcs = TownScene.Resolve(assets, npcs);
            Stage = SpriteStage.Create(background, "DnfTown");
        }

        /// <summary>把场景摆到 <paramref name="timeMs"/> 时刻并返回这次的绘制列表(与 CPU 合成共用)。</summary>
        public List<SceneSprite> ShowAt(long timeMs)
        {
            List<SceneSprite> sprites = TownScene.BuildResolved(_assets, _npcs, _projection, timeMs);
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
