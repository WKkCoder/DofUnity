using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

// 宠物形象:不写新的渲染,按动作取 .ani、按时间取帧(AniTimeline)、按世界坐标摆放(MonsterSprites.SpriteAt,与怪物、NPC、主角同一套投影与镜像规则)。
// 推断(没有样本核对):宠物的 .ani 与怪物一样按朝右画,朝左时水平镜像;帧自己的 x 缩放为负时再翻一次。
namespace Dnf.Play
{
    /// <summary>用游戏数据(<see cref="DnfAssets"/>)出宠物精灵的生产版精灵来源;动画按路径缓存(读不到的也记下,不重复读)。</summary>
    public sealed class PetSprites : IPetSpriteSource
    {
        private sealed class Clip
        {
            public Clip(string path, AniFile ani)
            {
                Path = path;
                Ani = ani;
                Timeline = new AniTimeline(ani);
            }

            public string Path { get; }

            public AniFile Ani { get; }

            public AniTimeline Timeline { get; }
        }

        private readonly DnfAssets _assets;
        private readonly Dictionary<string, Clip> _clips = new Dictionary<string, Clip>(StringComparer.Ordinal);

        public PetSprites(DnfAssets assets)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        }

        /// <summary>
        /// 宠物此刻的精灵:先试这个动作的动画,读不到(文件缺失、不是二进制 .ani、没有帧)就按"奔跑 → 行走 → 站立"的次序往后退;
        /// 动画里这一刻的帧没有图时返回 null。
        /// </summary>
        public SceneSprite Build(PetLook look, PetMotion motion, long animMs, int worldX, int worldY, int worldZ, bool facingLeft, TownProjection projection, int objectId)
        {
            if (look == null)
            {
                return null;
            }
            Clip clip = ClipOf(look.AniOf(motion));
            if (clip == null && motion == PetMotion.Run)
            {
                clip = ClipOf(look.AniOf(PetMotion.Walk));
            }
            if (clip == null && motion != PetMotion.Stand)
            {
                clip = ClipOf(look.AniOf(PetMotion.Stand));
            }
            if (clip == null)
            {
                return null;
            }
            AniSample sample = clip.Timeline.Sample(animMs);
            if (sample.Frame < 0)
            {
                return null;
            }
            return MonsterSprites.SpriteAt(_assets, clip.Ani, clip.Path, sample.Frame, projection, worldX, worldY, worldZ, !facingLeft, objectId, false,
                clip.Timeline.TintAlphaAt(animMs));
        }

        private Clip ClipOf(string path)
        {
            if (path == null)
            {
                return null;
            }
            if (_clips.TryGetValue(path, out Clip cached))
            {
                return cached;
            }
            AniFile ani = _assets.LoadAni(path);
            Clip clip = ani == null || ani.Frames.Length == 0 ? null : new Clip(path, ani);
            _clips[path] = clip;
            return clip;
        }
    }
}
