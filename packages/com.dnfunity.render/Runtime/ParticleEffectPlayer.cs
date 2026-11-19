using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:客户端 .act [PARTICLE]（0x010c5f91..0x010c6138）构造发射器、Restart(0) 预装 1.0 累积量、
// 单次 Emit 后立即析构发射器并把已发射粒子挂入场景列表逐帧推进；渲染图层按 0x01223a00 对应 TownScene.LayerRank。
namespace Dnf.Render
{
    /// <summary>
    /// 场景内运行的粒子发射播放器（纯 Render 层，无 Unity 依赖、无文件 I/O）。
    /// 封装 <see cref="ParticleEmitter"/>、小数毫秒累积与 <see cref="SceneSprite"/> 输出，供房间战斗会话与 Unity 宿主共用。
    /// </summary>
    public sealed class ParticleEffectPlayer
    {
        private readonly ParticleEmitter _emitter;
        private ParticleSimVec3 _pos;
        private readonly bool _facingLeft;
        private double _carryMs;
        private IReadOnlyList<ParticleSprite> _current;

        public ParticleEffectPlayer(
            ParticleSimDef def,
            uint seed,
            ParticleSimVec3 worldPos,
            bool facingLeft,
            bool oneShot = true,
            int quality = 2,
            Func<string, AniFile> resolveAni = null)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            Path = def.Path;
            _pos = worldPos;
            _facingLeft = facingLeft;
            _emitter = new ParticleEmitter(def, seed, worldPos, quality, resolveAni);
            _current = _emitter.Step(0, _pos, _facingLeft);
            if (oneShot)
            {
                _emitter.StopEmission();
            }
        }

        public string Path { get; }
        public ParticleSimVec3 Position => _pos;
        public bool FacingLeft => _facingLeft;
        public int LiveCount => _emitter.LiveCount;
        public bool Finished => _emitter.IsFinished;
        public IReadOnlyList<ParticleSprite> Sprites => _current;

        /// <summary>更新发射器世界坐标（持续型发射器可跟随对象移动；单次型粒子已独立飞行不受影响）。</summary>
        public void MoveTo(ParticleSimVec3 worldPos)
        {
            _pos = worldPos;
        }

        /// <summary>按毫秒推进粒子状态（累积小数毫秒，确保高频小步长不丢时间）。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0 || Finished)
            {
                return;
            }
            _carryMs += deltaMs;
            int wholeMs = (int)_carryMs;
            if (wholeMs <= 0)
            {
                return;
            }
            _carryMs -= wholeMs;
            _current = _emitter.Step(wholeMs, _pos, _facingLeft);
        }

        /// <summary>将粒子 [layer]（0x01223a00 枚举）映射到场景画家算法大类编号（见 <see cref="TownScene.LayerRank"/>）。</summary>
        public static int LayerRank(ParticleSimLayer layer)
        {
            switch (layer)
            {
                case ParticleSimLayer.DistantBack: return 0;
                case ParticleSimLayer.MiddleBack: return 1;
                case ParticleSimLayer.CloseBack: return 2;
                case ParticleSimLayer.Bottom: return 3;
                case ParticleSimLayer.Close: return 5;
                default: return TownScene.NormalRank;
            }
        }

        /// <summary>将当前存活粒子转为可合成的 <see cref="SceneSprite"/> 列表并追加到 <paramref name="into"/>，返回追加条数。</summary>
        public int AppendSceneSprites(ISpriteSource spriteSource, TownProjection projection, List<SceneSprite> into, int sortBias = 1)
        {
            if (spriteSource == null)
            {
                throw new ArgumentNullException(nameof(spriteSource));
            }
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            if (_current.Count == 0)
            {
                return 0;
            }

            int added = 0;
            for (int i = 0; i < _current.Count; i++)
            {
                ParticleSprite s = _current[i];
                if (s.Alpha <= 0 || s.FrameIndex < 0 || string.IsNullOrEmpty(s.ImagePath))
                {
                    continue;
                }
                if (!spriteSource.TryGetFrame(s.ImagePath, s.FrameIndex, out SpriteFrame src) || src.Width == 0 || src.Height == 0)
                {
                    continue;
                }

                projection.TopLeft(
                    s.WorldX,
                    s.WorldY,
                    s.WorldZ,
                    s.FrameOffsetX,
                    s.FrameOffsetY,
                    src.OffsetX,
                    src.OffsetY,
                    src.Width,
                    s.Mirrored,
                    out int sx,
                    out int sy);

                float absSx = Math.Abs(s.ScaleX);
                float absSy = Math.Abs(s.ScaleY);
                if (absSx <= 1e-4f || absSy <= 1e-4f)
                {
                    continue;
                }

                bool hasTransform = Math.Abs(absSx - 1f) > 1e-3f || Math.Abs(absSy - 1f) > 1e-3f || Math.Abs(s.RotationRadians) > 1e-3f;
                bool hasColorTint = s.ColorR != 255 || s.ColorG != 255 || s.ColorB != 255;
                int rank = LayerRank(s.Layer);
                int sortKey = s.WorldY + sortBias;

                if (!hasTransform && !hasColorTint)
                {
                    into.Add(new SceneSprite(src, sx, sy, sortKey, -1, Path, s.FrameIndex, s.Mirrored, s.Blend, s.Alpha, rank));
                    added++;
                }
                else
                {
                    SpriteFrame transformed = ParticleSimulation.TransformSprite(
                        src,
                        s.Mirrored,
                        absSx,
                        absSy,
                        s.RotationRadians,
                        s.ColorR,
                        s.ColorG,
                        s.ColorB,
                        out int deltaX,
                        out int deltaY);
                    if (transformed != null)
                    {
                        into.Add(new SceneSprite(transformed, sx + deltaX, sy + deltaY, sortKey, -1, Path, s.FrameIndex, false, s.Blend, s.Alpha, rank));
                        added++;
                    }
                }
            }
            return added;
        }
    }
}
