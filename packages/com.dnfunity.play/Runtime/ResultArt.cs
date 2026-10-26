using Dnf.Render;

// 证据:E-124(客户端结果窗口 0x0102c610 起的一族函数载入 Rank_Letter.img;0x0102baf0 把评分值换成这张图的帧:与第 i 个门槛相等是第 i 帧,都不等是第 8 帧)。
namespace Dnf.Play
{
    /// <summary>
    /// 结算页的评分字母(证据 E-124):图包里 <c>Sprite/Interface2/Result_Ver2.0/Rank_Letter/Rank_Letter.img</c> 九帧,帧 0 = SSS、帧 8 = F
    /// (与 <see cref="Dnf.Game.RankSystem.FrameOf"/> 同一个次序)。每帧是整块的字母图,自带偏移。
    /// 原版结果窗口(Result_Ver2.0.img 的底板、光效、数字滚动等)的版式没有恢复,结算页现在只放这个字母加几行文字。
    /// </summary>
    public static class ResultArt
    {
        /// <summary>评分字母的图包(客户端路径的小写形式)。</summary>
        public const string RankLetterImage = "sprite/interface2/result_ver2.0/rank_letter/rank_letter.img";

        /// <summary>字母的帧数(F 到 SSS 九档)。</summary>
        public const int FrameCount = 9;

        /// <summary>字母图块的上沿(屏幕 y;帧自带的偏移加在它上面)。</summary>
        public const int LetterTop = 20;

        /// <summary>有字母时,战果文字行的起始 y(在最高那一帧的下沿之后)。</summary>
        public const int TextTop = 220;

        /// <summary>摆放时用的标签(<see cref="SceneSprite.AnimationPath"/>)。</summary>
        public const string Tag = "rank";

        /// <summary>取评分字母的帧;图包缺这个图、帧号越界或帧是空的返回 false。</summary>
        public static bool TryRankLetter(ISpriteSource sprites, int frame, out SpriteFrame sprite)
        {
            sprite = null;
            if (sprites == null || frame < 0 || frame >= FrameCount)
            {
                return false;
            }
            return sprites.TryGetFrame(RankLetterImage, frame, out sprite) && sprite != null;
        }

        /// <summary>把评分字母摆在屏幕左侧(<paramref name="left"/> 是图块的左沿,帧的偏移加在上面);取不到这一帧时返回 null。</summary>
        public static SceneSprite PlaceRankLetter(ISpriteSource sprites, int frame, int left, int order)
        {
            if (!TryRankLetter(sprites, frame, out SpriteFrame letter))
            {
                return null;
            }
            return OverlayArt.Place(letter, left + letter.OffsetX, LetterTop + letter.OffsetY, order, Tag);
        }
    }
}
