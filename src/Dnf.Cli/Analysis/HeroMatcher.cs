using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// 主角与原版截图的逐像素对照:把主角的精灵分别合成到黑底和白底上,两次结果相同的像素就是主角完全不透明的像素;
/// 这些像素在截图里与我们的合成精确一致的比例,就是主角这一帧的吻合率(名牌、聊天文字等界面会盖住少数像素,所以不会是 100%)。
/// 只看主角自己的像素,是为了不让整屏平均数(背景光效、界面)盖住主角的偏差。
/// </summary>
internal sealed class HeroMatcher
{
    public const int W = 800, H = 600;

    private readonly RgbaCanvas _onBlack = new(W, H);
    private readonly RgbaCanvas _onWhite = new(W, H);

    public (int Opaque, int Exact, int Near) Measure(IReadOnlyList<SceneSprite> heroSprites, Raster shot)
    {
        _onBlack.Clear(0, 0, 0, 255);
        TownScene.Render(_onBlack, heroSprites);
        _onWhite.Clear(255, 255, 255, 255);
        TownScene.Render(_onWhite, heroSprites);
        byte[] a = _onBlack.Pixels;
        byte[] b = _onWhite.Pixels;
        int opaque = 0, exact = 0, near = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                if (a[o] != b[o] || a[o + 1] != b[o + 1] || a[o + 2] != b[o + 2])
                {
                    continue;
                }
                opaque++;
                uint c = shot.Pixels[y * shot.Width + x];
                int d = Math.Max(Math.Abs(a[o] - (int)((c >> 16) & 0xFF)),
                    Math.Max(Math.Abs(a[o + 1] - (int)((c >> 8) & 0xFF)), Math.Abs(a[o + 2] - (int)(c & 0xFF))));
                if (d == 0)
                {
                    exact++;
                }
                if (d <= 2)
                {
                    near++;
                }
            }
        }
        return (opaque, exact, near);
    }
}
