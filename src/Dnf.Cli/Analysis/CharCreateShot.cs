using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// charcreate-shot:离屏渲染角色创建/删除模态面板（800×600 PNG）并打印精灵统计。
/// 布局未对拍原版（无原版创角/删角基线），仅作自测快照；支持 --no-art 离线合成底图与 --text 方块字体。
/// </summary>
internal static class CharCreateShot
{
    public const string Usage =
        "charcreate-shot --png <输出.png> [--mode create|delete] [--job 0..10] [--name <合成名>] [--text] [--time <毫秒>] [--no-art]";

    public static int Run(Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("charcreate-shot 需要 --png <输出.png>");
        }
        string mode = opts.CharCreateMode.ToLowerInvariant();
        if (mode != "create" && mode != "delete")
        {
            throw new UserError("--mode 只认 create 或 delete");
        }
        if (opts.CharCreateJob < 0 || opts.CharCreateJob > OnlineCharacterNameRules.MaxCreateJobId)
        {
            throw new UserError("--job 需要 0 到 10 的整数");
        }

        DnfAssets? assets = opts.LoginNoArt ? null : DnfAssets.Open(Locator.Resolve(opts.DnfDir, opts.OutDir).DnfDir);
        try
        {
            IGlyphFonts? fonts = opts.CharText ? new ShotBoxFonts() : null;
            List<SceneSprite> sprites = BuildSprites(assets, fonts, mode, (byte)opts.CharCreateJob, opts.CharCreateName, opts.TimeMs);
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);

            int modalSprites = sprites.Count(s =>
                s.AnimationPath.StartsWith("create-modal-", StringComparison.Ordinal)
                || s.AnimationPath.StartsWith("delete-modal-", StringComparison.Ordinal));
            int avatarSprites = sprites.Count(s => s.AnimationPath == "create-modal-avatar");
            Console.WriteLine(
                "创角/删角面板:mode=" + mode
                + ",job=" + opts.CharCreateJob
                + ",name_bytes=" + Encoding.UTF8.GetByteCount(opts.CharCreateName)
                + ",time_ms=" + opts.TimeMs
                + ",total_sprites=" + sprites.Count
                + ",modal_sprites=" + modalSprites
                + ",preview_avatars=" + avatarSprites);
            Console.WriteLine("已写 " + opts.PngFile);
            return 0;
        }
        finally
        {
            assets?.Dispose();
        }
    }

    internal static List<SceneSprite> BuildSprites(
        DnfAssets? assets,
        IGlyphFonts? fonts,
        string mode,
        byte job,
        string name,
        long timeMs)
    {
        CharacterSelectScene? scene = assets == null ? null : CharacterSelectScene.ForOnline(assets);
        ICharacterTitles? titles = null;
        if (assets != null && fonts != null)
        {
            PvfArchive pvf = assets.Pvf;
            var library = new StrLibrary(path => pvf.TryGetEntry(path, out PvfEntry entry) ? pvf.Read(entry) : null);
            titles = new DnfCharacterTitles(assets.ReadScript, library);
        }

        OnlineCharacterList list = CharSelectShot.SyntheticList(2);
        CharacterRoster roster = OnlineCharacterRoster.From(list, titles);
        var selectScreen = CharacterSelectScreen.Online(roster, scene, 0, fonts);
        selectScreen.CreateEnabled = true;
        selectScreen.DeleteEnabled = true;

        ICharacterManageModal modal;
        if (mode == "delete")
        {
            string jobLabel = titles?.ClassName(job, 0, 0) ?? OnlineCharacterRoster.PlaceholderClassName(job, 0, 0);
            var del = new CharacterDeleteModal(0, name, jobLabel, 15, fonts);
            del.Type(name);
            modal = del;
        }
        else
        {
            var create = new CharacterCreateModal(titles, scene?.Avatars, fonts);
            create.SelectJob(job);
            create.Type(name);
            create.Advance(timeMs);
            modal = create;
        }

        var overlay = new OnlineShellNoticeOverlay(selectScreen, fonts) { Modal = modal };
        return overlay.Build(timeMs);
    }

    private sealed class ShotBoxFonts : IGlyphFonts
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
