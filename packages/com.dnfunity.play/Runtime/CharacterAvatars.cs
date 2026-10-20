using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选角界面上头像的来源:按角色与界面时间给出站在底座上的角色精灵(原版数据的实现是 <see cref="DnfCharacterAvatars"/>,测试里用合成的)。</summary>
    public interface ICharacterAvatars
    {
        /// <summary>
        /// 角色 <paramref name="slot"/> 在界面时间 <paramref name="ageMs"/> 的头像精灵,脚下落在屏幕坐标 (<paramref name="footX"/>, <paramref name="footY"/>);
        /// 返回的精灵按图层顺序排好,界面会统一改写它们的图层大类与先后。没有素材时返回空列表。
        /// </summary>
        IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY);
    }

    /// <summary>
    /// 用户自备数据里的鬼剑士头像(证据 E-096):默认外观的换装(与城镇里的主角同一套 <see cref="DemoHero.CreateOutfit"/>)加一个循环的站立动画,
    /// 摆法与城镇、副本里的主角同一个模型(<see cref="AvatarOutfit.SpritesAt"/>,脚下为原点)。站立动画默认是角色脚本的 [rest motion]。
    /// 第一次要头像时才读数据;读不到(数据不全)就一直返回空列表,界面照样能画(没有头像),原因在 <see cref="Failure"/>。
    /// </summary>
    public sealed class DnfCharacterAvatars : ICharacterAvatars
    {
        /// <summary>角色脚本里站立动作的标签。</summary>
        public const string DefaultPoseTag = TownHero.TownRestTag;

        private readonly DnfAssets _assets;
        private readonly string _poseTag;
        private readonly string _chrPath;
        private readonly Func<DnfAssets, AvatarOutfit> _createOutfit;
        private bool _tried;
        private AvatarOutfit _outfit;
        private AniFile _pose;
        private string _posePath;
        private AniTimeline _timeline;

        /// <summary>
        /// <paramref name="chrPath"/> 与 <paramref name="createOutfit"/> 默认是鬼剑士的角色脚本与默认外观(<see cref="DemoHero.SwordmanChrPath"/>、
        /// <see cref="DemoHero.CreateOutfit"/>);合成数据的测试换成自己的。
        /// </summary>
        public DnfCharacterAvatars(DnfAssets assets, string poseTag = DefaultPoseTag, string chrPath = null, Func<DnfAssets, AvatarOutfit> createOutfit = null)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _poseTag = poseTag ?? throw new ArgumentNullException(nameof(poseTag));
            _chrPath = chrPath ?? DemoHero.SwordmanChrPath;
            _createOutfit = createOutfit ?? (a => DemoHero.CreateOutfit(a));
        }

        /// <summary>读不到头像素材时的原因;读到了或还没试过是 null。</summary>
        public string Failure { get; private set; }

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            if (!Prepare())
            {
                return Array.Empty<SceneSprite>();
            }
            int frame = _timeline.FrameAt(ageMs);
            return _outfit.SpritesAt(_assets, _pose, _posePath, frame, new TownProjection(0, 0), footX, footY, 0, false, 0, -3);
        }

        private bool Prepare()
        {
            if (_tried)
            {
                return _outfit != null;
            }
            _tried = true;
            try
            {
                ScriptLine[] doc = _assets.ReadScript(_chrPath);
                string relative = null;
                if (doc != null)
                {
                    foreach (ScriptLine line in doc)
                    {
                        if (line.TagIs(_poseTag))
                        {
                            relative = line.FirstString();
                            break;
                        }
                    }
                }
                if (relative == null)
                {
                    throw new DnfFormatException("角色脚本里没有 " + _poseTag + ": " + _chrPath);
                }
                _posePath = PvfPath.ResolveRelativeTo(_chrPath, relative);
                _pose = _assets.LoadAni(_posePath) ?? throw new DnfFormatException("缺少站立动画: " + _posePath);
                _timeline = new AniTimeline(_pose);
                _outfit = _createOutfit(_assets);
                return true;
            }
            catch (Exception e) when (e is DnfFormatException || e is System.IO.IOException || e is KeyNotFoundException)
            {
                Failure = e.Message;
                _outfit = null;
                return false;
            }
        }
    }
}
