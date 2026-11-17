using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 选服务器、选频道两张界面的素材来源:图包、背景光束的三个动画(与选角界面同一组,同一个原点)、选频道提示条的呼吸光(.ani)。
    /// 原版数据由 <see cref="From"/> 接上;合成数据的测试自己拼(不给动画就不画动画,图包只放要测的帧)。
    /// </summary>
    public sealed class ServerPickerScene
    {
        /// <summary>选频道界面提示条的呼吸光动画(三帧、每帧 500 毫秒、染色 alpha 50/255/50,加法混合)。</summary>
        public const string StripGlowPath = "ui/channel/animation/channel_guide02.ani";

        public const int StripX = 119;
        public const int StripY = 137;

        public ServerPickerScene(ISpriteSource sprites, IReadOnlyList<UiAnimation> beams = null, UiAnimation stripGlow = null)
        {
            Sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
            Beams = beams ?? Array.Empty<UiAnimation>();
            StripGlow = stripGlow;
        }

        public ISpriteSource Sprites { get; }

        /// <summary>背景光束(读得到的才有)。</summary>
        public IReadOnlyList<UiAnimation> Beams { get; }

        /// <summary>提示条的呼吸光;读不到为 null(此时提示条不闪)。</summary>
        public UiAnimation StripGlow { get; }

        /// <summary>
        /// 用选角界面已经建好的素材(图包与光束)加上读 .ani 的函数建:光束沿用,提示条呼吸光现读。
        /// </summary>
        public static ServerPickerScene From(CharacterSelectScene characterScene, Func<string, AniFile> loadAni)
        {
            if (characterScene == null)
            {
                throw new ArgumentNullException(nameof(characterScene));
            }
            UiAnimation strip = UiAnimation.Create(characterScene.Sprites, loadAni, StripGlowPath, StripX, StripY);
            return new ServerPickerScene(characterScene.Sprites, characterScene.Beams, strip);
        }

        /// <summary>用户自备数据:原版图包、背景光束、提示条呼吸光。</summary>
        public static ServerPickerScene From(DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            var beams = new List<UiAnimation>(3);
            foreach (string path in new[] { CharacterSelectArt.BeamBackPath1, CharacterSelectArt.BeamBackPath2, CharacterSelectArt.BeamSparklePath })
            {
                UiAnimation animation = UiAnimation.Create(assets, assets.LoadAni, path, CharacterSelectArt.EffectX, CharacterSelectArt.EffectY);
                if (animation != null)
                {
                    beams.Add(animation);
                }
            }
            return new ServerPickerScene(assets, beams, UiAnimation.Create(assets, assets.LoadAni, StripGlowPath, StripX, StripY));
        }
    }
}
