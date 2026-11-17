using System.Globalization;
using System.Text;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// charselect-shot:选角界面(<see cref="CharacterSelectScreen"/>,证据 E-096)在界面打开 --time 毫秒时的 800×600 画面,写成 PNG。
/// 名册是 --chars 个默认角色(默认 1 个,与流程里的本地存档占位一样),--select 选中第几个。
/// Unity 播放器带 -dnfflow -dnfflowfrom char -dnfshot -dnftime 的离屏渲染以它为 CPU 参照(见 tools/unity-build-town.sh):
/// 两边都是"界面时间一次走到这一刻再画",所以逐字节一致。
/// <para>
/// 联网画法(r18-charui,<c>--online</c>):名册由合成的服务端角色列表适配(<see cref="OnlineCharacterRoster"/>),窗口按行滚动、不能进入的角色多一行红字、
/// 创立与删除灰着;<c>--text</c> 再装上合成的方块字体,让文字走中文那一套(命令行没有汉字字体,方块字只用来对版面)并打印各文字精灵的位置,
/// 与基线 S02 上量出的墨迹中心对照。不带这两个开关时的输出与以前逐字节一致(离线回归)。
/// </para>
/// </summary>
internal static class CharSelectShot
{
    /// <summary>联网画法的角色数上限(超过 12 个就滚动)。</summary>
    public const int MaxOnlineChars = 40;

    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("charselect-shot 需要 --png <输出.png>");
        }
        bool online = opts.CharOnline || opts.CharText;
        if (!online)
        {
            if (opts.CharCount > CharacterRoster.Capacity)
            {
                throw new UserError("--chars 需要 1 到 12 的整数");
            }
            if (opts.CharFirstRow != 0 || opts.CharPrompt != null)
            {
                throw new UserError("--first-row、--char-prompt 只用于联网画法(--online)");
            }
        }
        if (opts.SelectIndex > opts.CharCount)
        {
            throw new UserError("--select 超出了 --chars 个角色");
        }
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        return online ? RunOnline(assets, opts) : RunOffline(assets, opts);
    }

    private static int RunOffline(DnfAssets assets, Options opts)
    {
        var slots = new List<CharacterSlot>();
        for (int i = 0; i < opts.CharCount; i++)
        {
            slots.Add(new CharacterSlot(CharacterRoster.LocalName, CharacterRoster.LocalJob, 1));
        }
        CharacterSelectScene scene = opts.NoAvatars
            ? new CharacterSelectScene(assets, null, assets.LoadAni)
            : CharacterSelectScene.From(assets);
        var screen = new CharacterSelectScreen(new CharacterRoster(slots), scene, opts.SelectIndex - 1);
        screen.Advance(opts.TimeMs);
        List<SceneSprite> sprites = screen.Build(opts.TimeMs);
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        PngWriter.Write(opts.PngFile!, 800, 600, canvas.Pixels);
        string? failure = screen.Scene?.Avatars is DnfCharacterAvatars avatars ? avatars.Failure : null;
        Console.WriteLine("选角界面:时间 " + opts.TimeMs + " ms,角色 " + opts.CharCount + " 个,选中第 " + opts.SelectIndex + " 个:绘制列表 " + sprites.Count + " 个精灵"
            + (failure != null ? ",头像没画出来:" + failure : string.Empty));
        Console.WriteLine("已写 " + opts.PngFile);
        return 0;
    }

    private static int RunOnline(DnfAssets assets, Options opts)
    {
        OnlineCharacterList list = SyntheticList(opts.CharCount);
        CharacterRoster roster = OnlineCharacterRoster.From(list);
        CharacterSelectScene scene = opts.NoAvatars
            ? new CharacterSelectScene(assets, null, assets.LoadAni)
            : CharacterSelectScene.ForOnline(assets);
        IGlyphFonts? fonts = opts.CharText ? new BoxFonts() : null;
        var screen = CharacterSelectScreen.Online(roster, scene, opts.SelectIndex - 1, fonts);
        if (opts.CharFirstRow != screen.FirstRow)
        {
            screen.Scroll(opts.CharFirstRow - screen.FirstRow);
        }
        ApplyState(screen, opts);
        screen.Advance(opts.TimeMs);
        List<SceneSprite> sprites = screen.Build(opts.TimeMs);
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        PngWriter.Write(opts.PngFile!, 800, 600, canvas.Pixels);
        string? failure = screen.Scene?.Avatars is DnfJobAvatars avatars ? avatars.Failure : null;
        Console.WriteLine("联网选角界面:时间 " + opts.TimeMs + " ms,角色 " + opts.CharCount + " 个,选中第 " + (screen.Index + 1) + " 个,窗口第一行 " + screen.FirstRow
            + (fonts != null ? ",方块字体" : ",点阵占位") + ":绘制列表 " + sprites.Count + " 个精灵"
            + (failure != null ? ",头像没画出来:" + failure : string.Empty));
        PrintTextSprites(sprites);
        Console.WriteLine("已写 " + opts.PngFile);
        return 0;
    }

    // 提示与等待状态:走界面自己的逻辑(点按钮、确认),不绕过它
    private static void ApplyState(CharacterSelectScreen screen, Options opts)
    {
        switch (opts.CharPrompt)
        {
            case null:
                break;
            case "na":
                screen.Click(CharacterSelectArt.ButtonRect(CharacterSelectButton.Create).X + 5, CharacterSelectArt.SmallButtonY + 5);
                break;
            case "cannot":
            case "deleting":
                CharacterBlock want = opts.CharPrompt == "cannot" ? CharacterBlock.NameFlag : CharacterBlock.PendingDelete;
                if (screen.Selected?.Online == null || screen.Selected.Online.Block != want)
                {
                    throw new UserError("--char-prompt " + opts.CharPrompt + " 需要 --select 选中一个" + (want == CharacterBlock.NameFlag ? "X1=1(每 5 个里的第 5 个)" : "待删除(每 7 个里的第 7 个)") + "的角色");
                }
                screen.Confirm();
                break;
            default:
                throw new UserError("--char-prompt 只认 cannot、deleting、na");
        }
        if (opts.LoginBusy)
        {
            screen.Confirm();
            if (!screen.Busy)
            {
                throw new UserError("--busy 需要 --select 选中一个能进入的角色");
            }
        }
    }

    /// <summary>合成的服务端角色列表:第 i 个叫"角色(i+1)"、职业 i 轮流、等级 i+1;每 5 个里的第 5 个 X1=1(不可进入),每 7 个里的第 7 个待删除。全是编的。</summary>
    internal static OnlineCharacterList SyntheticList(int count)
    {
        var entries = new List<OnlineCharacterEntry>();
        for (int i = 0; i < count; i++)
        {
            byte[] name = Encoding.UTF8.GetBytes("角色" + (i + 1).ToString(CultureInfo.InvariantCulture));
            entries.Add(new OnlineCharacterEntry(
                (ushort)(900 + i), name, (byte)(i % 5 == 4 ? 1 : 0), (byte)(i % EquipRulesJobCount), (byte)((i % 3) * 0x10 + (i % 2)), (byte)(i + 1), 0, 0,
                null, 0, 0, 0, 0, 0, 0, 0, 0, 0, (uint)(i % 7 == 6 ? 3 : 0)));
        }
        return new OnlineCharacterList(0, 0, 0, 0, entries, 0);
    }

    private const int EquipRulesJobCount = 11;

    private static void PrintTextSprites(List<SceneSprite> sprites)
    {
        Console.WriteLine("文字精灵(标签 左上角x,y 宽×高 中心x,y):");
        foreach (SceneSprite s in sprites)
        {
            string tag = s.AnimationPath;
            if (tag.StartsWith("select-char-caption", StringComparison.Ordinal) || tag.StartsWith("select-char-marker", StringComparison.Ordinal)
                || tag == "select-char-button-text" || tag.StartsWith("select-char-prompt", StringComparison.Ordinal) || tag.StartsWith("select-char-busy", StringComparison.Ordinal))
            {
                double cx = s.ScreenX + s.Frame.Width / 2.0;
                double cy = s.ScreenY + s.Frame.Height / 2.0;
                Console.WriteLine("  " + tag + " " + s.ScreenX + "," + s.ScreenY + " " + s.Frame.Width + "x" + s.Frame.Height
                    + " 中心 " + cx.ToString("F1", CultureInfo.InvariantCulture) + "," + cy.ToString("F1", CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// 命令行用的合成方块字体(不含任何真实字形):全角字是 (字号 − 1) 见方的实心块、前进一个字号,其余字符宽半个字号;空格只占宽度。
    /// 只用来让联网选角界面的文字走字体那条路、量出文字框的位置与大小,不代表真字体的字形。
    /// </summary>
    private sealed class BoxFonts : IGlyphFonts
    {
        public IGlyphSource ForSize(int pixelSize) => new Source(pixelSize);

        private sealed class Source : IGlyphSource
        {
            public Source(int px)
            {
                PixelSize = px;
            }

            public int PixelSize { get; }

            public int Ascent => PixelSize - PixelSize / 8 - 1;

            public int Descent => PixelSize / 8 + 1;

            public int LineHeight => PixelSize + 2;

            public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
            {
                int advance = GlyphText.IsWide(codePoint) ? PixelSize : Math.Max(1, PixelSize / 2);
                if (codePoint == ' ')
                {
                    glyph = GlyphBitmap.Blank(advance);
                    return true;
                }
                int w = Math.Max(1, advance - 1);
                int h = Math.Max(1, PixelSize - 1);
                var alpha = new byte[w * h];
                Array.Fill(alpha, (byte)255);
                glyph = new GlyphBitmap(w, h, 0, h, advance, alpha);
                return true;
            }
        }
    }
}
