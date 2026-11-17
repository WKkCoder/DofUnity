using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>像素级的小工具:从精灵列表里按标签取。</summary>
internal static class SpriteQuery
{
    public static List<SceneSprite> Tagged(IEnumerable<SceneSprite> sprites, string tag)
    {
        return sprites.Where(s => s.AnimationPath == tag).ToList();
    }

    public static List<string> Tags(IEnumerable<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToList();

    /// <summary>精灵里有墨迹(不全透明)的包围盒,屏幕坐标:左、上、右(含)、下(含)。</summary>
    public static (int Left, int Top, int Right, int Bottom) Ink(SceneSprite sprite)
    {
        SpriteFrame f = sprite.Frame;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < f.Height; y++)
        {
            for (int x = 0; x < f.Width; x++)
            {
                if (f.Rgba[(y * f.Width + x) * 4 + 3] != 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        return (sprite.ScreenX + minX, sprite.ScreenY + minY, sprite.ScreenX + maxX, sprite.ScreenY + maxY);
    }
}

/// <summary>
/// 联网选角界面测试用的合成素材:各帧尺寸取自量过的界面图,像素是"行号编码"色块(像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255)),
/// 验证的是布局规则与逻辑,不含任何真实素材。
/// </summary>
internal static class CharSelectKit
{
    public static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = (byte)(y % 256);
                rgba[o + 1] = (byte)(x % 256);
                rgba[o + 2] = tag;
                rgba[o + 3] = 255;
            }
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    /// <summary>界面用到的全部帧(含"开始"的灰帧)。</summary>
    public static ShellFakeSprites FullArt()
    {
        var f = new ShellFakeSprites();
        const string ui = CharacterSelectArt.UiImage;
        const string win = CharacterSelectArt.WindowImage;
        f.Frames[(ui, CharacterSelectArt.BackgroundFrame)] = Coded(800, 600, 1);
        f.Frames[(ui, CharacterSelectArt.TitleFrame)] = Coded(111, 28, 2, 3, 4);
        f.Frames[(ui, CharacterSelectArt.GlowFrame)] = Coded(292, 38, 3);
        f.Frames[(ui, CharacterSelectArt.SlotFrameFrame)] = Coded(119, 207, 4);
        f.Frames[(ui, CharacterSelectArt.StartNormalFrame)] = Coded(125, 43, 5);
        f.Frames[(ui, CharacterSelectArt.StartDisabledFrame)] = Coded(125, 43, 10);
        f.Frames[(ui, CharacterSelectArt.PedestalBrightFrame)] = Coded(119, 44, 6);
        f.Frames[(ui, CharacterSelectArt.PedestalDimFrame)] = Coded(119, 44, 7);
        f.Frames[(CharacterSelectArt.FlagImage, CharacterSelectArt.FlagFrame)] = Coded(36, 53, 8, -1, 2);
        (int W, int H)[] sizes = { (11, 11), (12, 11), (11, 11), (11, 13), (12, 13), (11, 13), (11, 11), (12, 11), (11, 11) };
        for (int i = 0; i < 9; i++)
        {
            f.Frames[(win, CharacterSelectArt.BoardFirstFrame + i)] = Coded(sizes[i].W, sizes[i].H, (byte)(20 + i));
        }
        f.Frames[(win, CharacterSelectArt.ScrollUpFrame)] = Coded(9, 13, 30);
        f.Frames[(win, CharacterSelectArt.ScrollDownFrame)] = Coded(9, 13, 31);
        f.Frames[(win, CharacterSelectArt.ScrollThumbFrame)] = Coded(9, 23, 32);
        f.Frames[(win, CharacterSelectArt.ScrollTrackFrame)] = Coded(9, 1, 33);
        f.Frames[(win, CharacterSelectArt.ButtonLeftFrame)] = Coded(28, 23, 40);
        f.Frames[(win, CharacterSelectArt.ButtonMiddleFrame)] = Coded(2, 23, 41);
        f.Frames[(win, CharacterSelectArt.ButtonRightFrame)] = Coded(28, 23, 42);
        return f;
    }

    public static CharacterSelectScene Scene(ICharacterAvatars avatars = null) => new CharacterSelectScene(FullArt(), avatars);

    /// <summary>联网选角界面:<paramref name="count"/> 个合成角色(见 <see cref="RosterKit.Many"/>),默认用不会撞到全进程字体的合成字体。</summary>
    public static CharacterSelectScreen Online(int count, int index = 0, IGlyphFonts fonts = null, ICharacterAvatars avatars = null, ICharacterTitles titles = null)
    {
        return CharacterSelectScreen.Online(RosterKit.Roster(count, titles), Scene(avatars), index, fonts ?? new SizedFonts());
    }

    /// <summary>名册给定的联网选角界面。</summary>
    public static CharacterSelectScreen OnlineOf(CharacterRoster roster, IGlyphFonts fonts = null, ICharacterAvatars avatars = null, int index = 0)
    {
        return CharacterSelectScreen.Online(roster, Scene(avatars), index, fonts ?? new SizedFonts());
    }

    /// <summary>格子中心的画布坐标。</summary>
    public static (int X, int Y) CellCenter(int cell)
    {
        CharacterSelectArt.SlotOrigin(cell, out int x, out int y);
        return (x + CharacterSelectArt.SlotWidth / 2, y + CharacterSelectArt.SlotHeight / 2);
    }

    /// <summary>按钮中心的画布坐标。</summary>
    public static (int X, int Y) ButtonCenter(CharacterSelectButton button)
    {
        HitRect r = CharacterSelectArt.ButtonRect(button);
        return (r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>点在按钮上。</summary>
    public static bool ClickButton(CharacterSelectScreen screen, CharacterSelectButton button)
    {
        (int x, int y) = ButtonCenter(button);
        return screen.Click(x, y);
    }

    /// <summary>点在第 <paramref name="cell"/> 个格子(窗口里的格号)中心。</summary>
    public static bool ClickCell(CharacterSelectScreen screen, int cell)
    {
        (int x, int y) = CellCenter(cell);
        return screen.Click(x, y);
    }

    /// <summary>记下被问到的角色与脚下点的假头像来源:每个角色给一个 10×20 的色块(标签 90)。</summary>
    public sealed class FakeAvatars : ICharacterAvatars
    {
        public List<(string Name, int FootX, int FootY)> Calls { get; } = new List<(string, int, int)>();

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            Calls.Add((slot.Name, footX, footY));
            return new List<SceneSprite> { new SceneSprite(Coded(10, 20, 90), footX - 5, footY - 20, 3, 0, "fake/a", 0) };
        }
    }
}
