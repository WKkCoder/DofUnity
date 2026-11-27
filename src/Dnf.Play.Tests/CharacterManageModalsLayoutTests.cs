using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 创建/删除角色模态面板的版式不变量(r20fix-createcharui,相邻点补测):版式是自拟的,不钉像素位置,
/// 只钉"什么必须在什么里面、什么必须居中、什么不能叠在一起":
/// 除整屏变暗层与卡片自己的装饰(投影、四角括号)之外,面板的每一个精灵都在卡片里;
/// 精灵与点击区域一致;按钮、职业格、预览框里的字不出格、居中;输入框里的字不出框;
/// 提示条与它的图标、文字各归其位;底部按键说明在控件下方。
/// 另用"字比预期宽得多"的合成字体扫一遍:字体换了、字宽变了,字也不能溢出自己的框(放不下就缩字号、再放不下就截断)。
/// 全部用内存里的合成字体与合成数据。
/// </summary>
public sealed class CharacterManageModalsLayoutTests
{
    // ------------------------------------------------------------------ 夹具

    /// <summary>屏幕坐标里的矩形:左、上含,右、下不含。</summary>
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int CenterX2 => Left + Right;

        public int CenterY2 => Top + Bottom;

        public bool Inside(Rect outer) => Left >= outer.Left && Top >= outer.Top && Right <= outer.Right && Bottom <= outer.Bottom;

        public bool Overlaps(Rect other) => Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

        public override string ToString() => "[" + Left + "," + Top + " - " + Right + "," + Bottom + ")";
    }

    private static Rect Of(SceneSprite sprite) => new Rect(sprite.ScreenX, sprite.ScreenY, sprite.ScreenX + sprite.Frame.Width, sprite.ScreenY + sprite.Frame.Height);

    private static Rect Of(HitRect rect) => new Rect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);

    private static readonly Rect Screen = new Rect(0, 0, 800, 600);

    private static readonly Rect CreateCard = new Rect(
        CharacterCreateModal.CardX, CharacterCreateModal.CardY,
        CharacterCreateModal.CardX + CharacterCreateModal.CardWidth, CharacterCreateModal.CardY + CharacterCreateModal.CardHeight);

    private static readonly Rect DeleteCard = new Rect(
        CharacterDeleteModal.CardX, CharacterDeleteModal.CardY,
        CharacterDeleteModal.CardX + CharacterDeleteModal.CardWidth, CharacterDeleteModal.CardY + CharacterDeleteModal.CardHeight);

    private static readonly Rect PreviewBox = new Rect(
        CharacterCreateModal.PreviewX, CharacterCreateModal.PreviewY,
        CharacterCreateModal.PreviewX + CharacterCreateModal.PreviewWidth, CharacterCreateModal.PreviewY + CharacterCreateModal.PreviewHeight);

    /// <summary>前进宽度由 (字号, 码点) 决定的合成字体集;字形是实心方块(宽 = 前进宽度减 1,高 = 字号的四分之三)。</summary>
    private sealed class FuncFonts : IGlyphFonts
    {
        private readonly Func<int, int, int> _advance;

        public FuncFonts(Func<int, int, int> advance)
        {
            _advance = advance;
        }

        public IGlyphSource ForSize(int pixelSize) => new Source(pixelSize, _advance);

        private sealed class Source : IGlyphSource
        {
            private readonly int _px;
            private readonly Func<int, int, int> _advance;

            public Source(int px, Func<int, int, int> advance)
            {
                _px = px;
                _advance = advance;
            }

            public int PixelSize => _px;

            public int Ascent => _px - _px / 4;

            public int Descent => _px / 4;

            public int LineHeight => _px + 2;

            public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
            {
                int advance = _advance(_px, codePoint);
                if (codePoint == ' ')
                {
                    glyph = GlyphBitmap.Blank(advance);
                    return true;
                }
                int w = Math.Max(1, advance - 1);
                int h = Math.Max(1, _px * 3 / 4);
                var alpha = new byte[w * h];
                Array.Fill(alpha, (byte)255);
                glyph = new GlyphBitmap(w, h, 0, Ascent, advance, alpha);
                return true;
            }
        }
    }

    /// <summary>字宽是"正常字宽"(全角字 = 字号,其余 = 半个字号)的 tenths / 10 倍。</summary>
    private static IGlyphFonts Scaled(int tenths) =>
        new FuncFonts((px, cp) => Math.Max(1, (GlyphText.IsWide(cp) ? px : px / 2) * tenths / 10));

    /// <summary>每个字(含空格)都占 <paramref name="advance"/> 像素,与字号无关:一段 n 个字的文字不被缩放截断时,画出来是 n × advance + 2 像素宽。</summary>
    private static IGlyphFonts Fixed(int advance) => new FuncFonts((px, cp) => advance);

    private sealed class Titles : ICharacterTitles
    {
        private readonly Func<int, string> _name;

        public Titles(Func<int, string> name)
        {
            _name = name;
        }

        public string DefaultName => "默认名";

        public string ClassName(int job, int growType, int awakening) => _name(job);
    }

    private static readonly ICharacterTitles ShortTitles = new Titles(job => "职业" + job);

    /// <summary>每个职业名都有 20 个全角字,字号 13 时有 260 像素,远超职业格的宽度。</summary>
    private static readonly ICharacterTitles LongTitles = new Titles(job => new string('长', 20) + job);

    private static List<SceneSprite> Build(ICharacterManageModal modal)
    {
        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.BuildInto(sprites, ref order, 0);
        return sprites;
    }

    private static List<SceneSprite> Tagged(List<SceneSprite> sprites, string tag) => SpriteQuery.Tagged(sprites, tag);

    /// <summary>把断言攒起来最后一起报:扫一圈字宽时,哪个字宽下哪个元素出了界要一次看全。</summary>
    private sealed class Problems
    {
        private readonly List<string> _list = new List<string>();

        public void Expect(bool condition, string message)
        {
            if (!condition)
            {
                _list.Add(message);
            }
        }

        public void AssertNone()
        {
            Assert.True(_list.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, _list));
        }
    }

    /// <summary>名字为 <paramref name="tag"/> 的精灵恰好一个时返回它,否则记一条问题并返回 null。</summary>
    private static SceneSprite One(List<SceneSprite> sprites, string tag, string label, Problems p)
    {
        List<SceneSprite> found = Tagged(sprites, tag);
        p.Expect(found.Count == 1, label + ": expected exactly one " + tag + " but found " + found.Count);
        return found.Count == 1 ? found[0] : null;
    }

    private static void CheckText(Problems p, string label, SceneSprite text, Rect container, bool centerX, bool centerY)
    {
        if (text == null)
        {
            return;
        }
        Rect r = Of(text);
        p.Expect(r.Inside(container), label + ": " + text.AnimationPath + " " + r + " is not inside " + container);
        if (centerX)
        {
            p.Expect(Math.Abs(r.CenterX2 - container.CenterX2) <= 2,
                label + ": " + text.AnimationPath + " " + r + " is not horizontally centred in " + container);
        }
        if (centerY)
        {
            p.Expect(Math.Abs(r.CenterY2 - container.CenterY2) <= 2,
                label + ": " + text.AnimationPath + " " + r + " is not vertically centred in " + container);
        }
    }

    private static void CheckSameRect(Problems p, string label, SceneSprite sprite, Rect expected)
    {
        if (sprite == null)
        {
            return;
        }
        p.Expect(Of(sprite).Equals(expected), label + ": " + sprite.AnimationPath + " is drawn at " + Of(sprite) + " but its hit area is " + expected);
    }

    private static bool IsCardDecoration(string tag, string prefix) =>
        tag == prefix + "-dim" || tag == prefix + "-panel" || tag.StartsWith(prefix + "-panel-", StringComparison.Ordinal);

    /// <summary>变暗层盖满整屏、卡片就是它自己的矩形且在屏内;其余精灵都在卡片里(<paramref name="includeHints"/> 为 false 时不查底部按键说明)。</summary>
    private static void CheckCard(List<SceneSprite> sprites, string prefix, Rect card, string label, Problems p, bool includeHints)
    {
        p.Expect(card.Inside(Screen), label + ": the card " + card + " is not inside the 800x600 screen");
        SceneSprite dim = One(sprites, prefix + "-dim", label, p);
        if (dim != null)
        {
            Rect d = Of(dim);
            p.Expect(d.Left <= 0 && d.Top <= 0 && d.Right >= 800 && d.Bottom >= 600, label + ": the dim layer " + d + " does not cover the screen");
        }
        CheckSameRect(p, label, One(sprites, prefix + "-panel", label, p), card);
        foreach (SceneSprite s in sprites)
        {
            string tag = s.AnimationPath;
            if (IsCardDecoration(tag, prefix) || (!includeHints && tag.StartsWith(prefix + "-hint-", StringComparison.Ordinal)))
            {
                continue;
            }
            p.Expect(Of(s).Inside(card), label + ": " + tag + " " + Of(s) + " is outside the card " + card);
        }
    }

    /// <summary>提示条:恰好画出 <paramref name="tags"/> 里的一种;条本身就是提示区域,字在条里居中,图标(或转圈)在条里、在字的左边。</summary>
    private static string CheckNoticeBar(List<SceneSprite> sprites, string[] tags, Rect noticeArea, string label, Problems p, bool mustExist)
    {
        List<string> present = tags.Where(t => Tagged(sprites, t).Count > 0).ToList();
        p.Expect(present.Count == (mustExist ? 1 : present.Count), label + ": exactly one notice expected, found " + string.Join(",", present));
        p.Expect(present.Count <= 1, label + ": more than one notice drawn: " + string.Join(",", present));
        if (present.Count != 1)
        {
            return null;
        }
        string tag = present[0];
        SceneSprite text = One(sprites, tag, label, p);
        SceneSprite bar = One(sprites, tag + "-bar", label, p);
        CheckSameRect(p, label, bar, noticeArea);
        if (bar != null)
        {
            CheckText(p, label, text, Of(bar), false, true);
            foreach (string suffix in new[] { "-icon", "-spinner" })
            {
                foreach (SceneSprite icon in Tagged(sprites, tag + suffix))
                {
                    p.Expect(Of(icon).Inside(Of(bar)), label + ": " + icon.AnimationPath + " is outside its bar");
                    if (text != null)
                    {
                        p.Expect(Of(icon).Right <= Of(text).Left, label + ": " + icon.AnimationPath + " overlaps the notice text");
                    }
                }
            }
        }
        return tag;
    }

    private static readonly string[] CreateNoticeTags =
    {
        "create-modal-busy", "create-modal-error", "create-modal-validation-error", "create-modal-status",
    };

    private static readonly string[] DeleteNoticeTags = { "delete-modal-busy", "delete-modal-error" };

    private static void CheckCreateModal(List<SceneSprite> sprites, string label, Problems p, bool includeHints)
    {
        CheckCard(sprites, "create-modal", CreateCard, label, p, includeHints);

        // 职业格:第 j 个格子的精灵就是 JobRect(j),格里的字居中、不出格
        List<SceneSprite> cells = sprites
            .Where(s => s.AnimationPath == "create-modal-job" || s.AnimationPath == "create-modal-job-selected")
            .ToList();
        List<SceneSprite> jobTexts = Tagged(sprites, "create-modal-job-text");
        p.Expect(cells.Count == 11, label + ": expected 11 job cells but found " + cells.Count);
        p.Expect(jobTexts.Count == 11, label + ": expected 11 job labels but found " + jobTexts.Count);
        for (int job = 0; job < Math.Min(cells.Count, 11); job++)
        {
            Rect rect = Of(CharacterCreateModal.JobRect(job));
            CheckSameRect(p, label + " job " + job, cells[job], rect);
            if (job < jobTexts.Count)
            {
                CheckText(p, label + " job " + job, jobTexts[job], rect, true, true);
            }
        }

        // 预览框与框里的职业名(横向居中)
        CheckSameRect(p, label, One(sprites, "create-modal-preview-box", label, p), PreviewBox);
        CheckText(p, label, One(sprites, "create-modal-preview-job", label, p), PreviewBox, true, false);

        // 名字框:输入过就画名字,没输入就画占位提示;字在框里、竖向居中
        Rect nameBox = Of(CharacterCreateModal.NameBoxRect);
        CheckSameRect(p, label, One(sprites, "create-modal-name-box", label, p), nameBox);
        bool typed = Tagged(sprites, "create-modal-name-text").Count > 0;
        CheckText(p, label, One(sprites, typed ? "create-modal-name-text" : "create-modal-name-placeholder", label, p), nameBox, false, true);

        // 确认/取消按钮:忙时确认钮换成灰的;按钮就是点击区域,字在按钮里居中
        bool busy = Tagged(sprites, "create-modal-confirm-disabled").Count > 0;
        Rect confirm = Of(CharacterCreateModal.ConfirmButtonRect);
        Rect cancel = Of(CharacterCreateModal.CancelButtonRect);
        CheckSameRect(p, label, One(sprites, busy ? "create-modal-confirm-disabled" : "create-modal-confirm", label, p), confirm);
        CheckText(p, label, One(sprites, "create-modal-confirm-text", label, p), confirm, true, true);
        CheckSameRect(p, label, One(sprites, "create-modal-cancel", label, p), cancel);
        CheckText(p, label, One(sprites, "create-modal-cancel-text", label, p), cancel, true, true);

        CheckNoticeBar(sprites, CreateNoticeTags, Of(CharacterCreateModal.NoticeRect), label, p, true);

        // 标题在分割线上方,分割线在职业格上方
        SceneSprite title = One(sprites, "create-modal-title", label, p);
        SceneSprite divider = One(sprites, "create-modal-divider", label, p);
        if (title != null && divider != null)
        {
            p.Expect(Of(title).Bottom <= Of(divider).Top, label + ": the title " + Of(title) + " is not above the divider " + Of(divider));
            p.Expect(Of(divider).Bottom <= Of(CharacterCreateModal.JobRect(0)).Top, label + ": the divider " + Of(divider) + " is not above the job grid");
        }

        if (includeHints)
        {
            int hintBottom = Math.Max(confirm.Bottom, Math.Max(cancel.Bottom, Of(CharacterCreateModal.NoticeRect).Bottom));
            CheckHintsBelow(sprites, "create-modal-hint", hintBottom, label, p);
        }
    }

    private static void CheckDeleteModal(List<SceneSprite> sprites, string label, Problems p, bool includeHints)
    {
        CheckCard(sprites, "delete-modal", DeleteCard, label, p, includeHints);

        SceneSprite title = One(sprites, "delete-modal-title", label, p);
        SceneSprite target = One(sprites, "delete-modal-target", label, p);
        SceneSprite prompt = One(sprites, "delete-modal-prompt", label, p);
        Rect inputBox = Of(CharacterDeleteModal.ConfirmBoxRect);
        if (title != null && target != null && prompt != null)
        {
            p.Expect(Of(title).Bottom <= Of(target).Top, label + ": the title is not above the target line");
            p.Expect(Of(target).Bottom <= Of(prompt).Top, label + ": the target line is not above the prompt");
            p.Expect(Of(prompt).Bottom <= inputBox.Top, label + ": the prompt is not above the input box");
        }

        CheckSameRect(p, label, One(sprites, "delete-modal-input-box", label, p), inputBox);
        bool typed = Tagged(sprites, "delete-modal-input-text").Count > 0;
        CheckText(p, label, One(sprites, typed ? "delete-modal-input-text" : "delete-modal-input-placeholder", label, p), inputBox, false, true);

        bool busy = Tagged(sprites, "delete-modal-confirm-disabled").Count > 0;
        Rect confirm = Of(CharacterDeleteModal.ConfirmButtonRect);
        Rect cancel = Of(CharacterDeleteModal.CancelButtonRect);
        CheckSameRect(p, label, One(sprites, busy ? "delete-modal-confirm-disabled" : "delete-modal-confirm", label, p), confirm);
        CheckText(p, label, One(sprites, "delete-modal-confirm-text", label, p), confirm, true, true);
        CheckSameRect(p, label, One(sprites, "delete-modal-cancel", label, p), cancel);
        CheckText(p, label, One(sprites, "delete-modal-cancel-text", label, p), cancel, true, true);

        CheckNoticeBar(sprites, DeleteNoticeTags, Of(CharacterDeleteModal.NoticeRect), label, p, false);

        if (includeHints)
        {
            CheckHintsBelow(sprites, "delete-modal-hint", Math.Max(confirm.Bottom, cancel.Bottom), label, p);
        }
    }

    private static void CheckHintsBelow(List<SceneSprite> sprites, string prefix, int controlsBottom, string label, Problems p)
    {
        List<SceneSprite> hints = sprites.Where(s => s.AnimationPath.StartsWith(prefix + "-", StringComparison.Ordinal)).ToList();
        p.Expect(hints.Count > 0, label + ": no key hints drawn");
        foreach (SceneSprite hint in hints)
        {
            p.Expect(Of(hint).Top >= controlsBottom, label + ": " + hint.AnimationPath + " " + Of(hint) + " is not below the controls (bottom " + controlsBottom + ")");
        }
    }

    // ------------------------------------------------------------------ 各种状态下的面板

    private static IEnumerable<(string Name, CharacterCreateModal Modal)> CreateStates(IGlyphFonts fonts, ICharacterTitles titles)
    {
        CharacterCreateModal Make() => new CharacterCreateModal(titles, null, fonts);

        yield return ("idle", Make());

        CharacterCreateModal last = Make();
        last.SelectJob(10);
        yield return ("last job selected", last);

        CharacterCreateModal longName = Make();
        longName.SelectJob(5);
        longName.Type(new string('汉', 32));
        yield return ("32 wide characters typed", longName);

        CharacterCreateModal tooShort = Make();
        tooShort.Type("ab");
        yield return ("too short name typed", tooShort);

        CharacterCreateModal good = Make();
        good.Type("HeroName");
        yield return ("good name typed", good);

        CharacterCreateModal busy = Make();
        busy.Type("HeroName");
        busy.SetBusy(true);
        yield return ("busy", busy);

        CharacterCreateModal failed = Make();
        failed.Type("HeroName");
        failed.SetFailure(OnlineCharacterManageStatus.ServiceRestricted);
        yield return ("server failure", failed);

        CharacterCreateModal rejected = Make();
        rejected.Type("abc");
        rejected.Submit();
        yield return ("local validation error", rejected);
    }

    private static IEnumerable<(string Name, CharacterDeleteModal Modal)> DeleteStates(IGlyphFonts fonts)
    {
        CharacterDeleteModal Make() => new CharacterDeleteModal(2, new string('名', 40), new string('职', 12), 25, fonts);

        yield return ("idle", Make());

        CharacterDeleteModal typed = Make();
        typed.Type(new string('汉', 64));
        yield return ("64 wide characters typed", typed);

        CharacterDeleteModal busy = Make();
        busy.Type("HeroA");
        busy.SetBusy(true);
        yield return ("busy", busy);

        CharacterDeleteModal failed = Make();
        failed.SetFailure(OnlineCharacterManageStatus.GuildMasterCannotDelete);
        yield return ("server failure", failed);

        CharacterDeleteModal mismatch = Make();
        mismatch.Submit();
        yield return ("name mismatch", mismatch);
    }

    // ------------------------------------------------------------------ 版式不变量

    [Fact]
    public void Every_state_of_the_create_modal_keeps_its_parts_in_place_with_a_normal_width_font()
    {
        var p = new Problems();
        foreach ((string name, CharacterCreateModal modal) in CreateStates(Scaled(10), ShortTitles))
        {
            CheckCreateModal(Build(modal), "create/" + name, p, includeHints: true);
        }
        foreach ((string name, CharacterCreateModal modal) in CreateStates(Scaled(10), LongTitles))
        {
            CheckCreateModal(Build(modal), "create/long titles/" + name, p, includeHints: true);
        }
        p.AssertNone();
    }

    [Fact]
    public void Every_state_of_the_delete_modal_keeps_its_parts_in_place_with_a_normal_width_font()
    {
        var p = new Problems();
        foreach ((string name, CharacterDeleteModal modal) in DeleteStates(Scaled(10)))
        {
            CheckDeleteModal(Build(modal), "delete/" + name, p, includeHints: true);
        }
        p.AssertNone();
    }

    /// <summary>
    /// 字体比预期宽得多(最多 8 倍)时,字放不下就缩字号、再放不下就截断,仍然不溢出自己的框。
    /// 一格一格(0.1 倍)扫过去,因为"溢出"只出现在某一个字宽区间里:限宽差一点点的实现,总有某个字宽让文字刚好从框里挤出来。
    /// 底部的按键说明没有限宽(靠字少),不在这里扫。
    /// </summary>
    [Fact]
    public void No_text_leaves_its_container_whatever_the_font_width_up_to_eight_times_normal()
    {
        var p = new Problems();
        for (int tenths = 10; tenths <= 80; tenths++)
        {
            string width = "k=" + (tenths / 10) + "." + (tenths % 10);
            IGlyphFonts fonts = Scaled(tenths);
            foreach ((string name, CharacterCreateModal modal) in CreateStates(fonts, ShortTitles))
            {
                CheckCreateModal(Build(modal), width + " create/" + name, p, includeHints: false);
            }
            foreach ((string name, CharacterCreateModal modal) in CreateStates(fonts, LongTitles))
            {
                CheckCreateModal(Build(modal), width + " create/long titles/" + name, p, includeHints: false);
            }
            foreach ((string name, CharacterDeleteModal modal) in DeleteStates(fonts))
            {
                CheckDeleteModal(Build(modal), width + " delete/" + name, p, includeHints: false);
            }
        }
        p.AssertNone();
    }

    // ------------------------------------------------------------------ 点击区域的几何

    [Fact]
    public void The_create_modal_hit_areas_lie_inside_its_card_and_never_overlap_each_other()
    {
        var named = new List<(string Name, Rect Rect)>
        {
            ("name box", Of(CharacterCreateModal.NameBoxRect)),
            ("confirm button", Of(CharacterCreateModal.ConfirmButtonRect)),
            ("cancel button", Of(CharacterCreateModal.CancelButtonRect)),
            ("notice area", Of(CharacterCreateModal.NoticeRect)),
            ("preview box", PreviewBox),
        };
        for (int job = 0; job <= 10; job++)
        {
            named.Add(("job " + job, Of(CharacterCreateModal.JobRect(job))));
        }

        AssertInsideAndApart(named, CreateCard);
    }

    [Fact]
    public void The_delete_modal_hit_areas_lie_inside_its_card_and_never_overlap_each_other()
    {
        var named = new List<(string Name, Rect Rect)>
        {
            ("input box", Of(CharacterDeleteModal.ConfirmBoxRect)),
            ("notice area", Of(CharacterDeleteModal.NoticeRect)),
            ("confirm button", Of(CharacterDeleteModal.ConfirmButtonRect)),
            ("cancel button", Of(CharacterDeleteModal.CancelButtonRect)),
        };

        AssertInsideAndApart(named, DeleteCard);
    }

    private static void AssertInsideAndApart(List<(string Name, Rect Rect)> areas, Rect card)
    {
        var p = new Problems();
        p.Expect(card.Inside(Screen), "the card " + card + " is not inside the 800x600 screen");
        for (int i = 0; i < areas.Count; i++)
        {
            p.Expect(areas[i].Rect.Inside(card), areas[i].Name + " " + areas[i].Rect + " is outside the card " + card);
            for (int j = i + 1; j < areas.Count; j++)
            {
                p.Expect(!areas[i].Rect.Overlaps(areas[j].Rect), areas[i].Name + " " + areas[i].Rect + " overlaps " + areas[j].Name + " " + areas[j].Rect);
            }
        }
        p.AssertNone();
    }

    /// <summary>11 个职业格排成 2 列:同列同 x,同行同 y,下一行在上一行下面,右格在左格右边,每格的正中点点中的就是它自己。</summary>
    [Fact]
    public void The_job_cells_form_a_two_column_grid_in_reading_order()
    {
        for (int job = 0; job <= 10; job++)
        {
            HitRect cell = CharacterCreateModal.JobRect(job);
            HitRect sameColumnFirst = CharacterCreateModal.JobRect(job % 2);
            HitRect sameRowFirst = CharacterCreateModal.JobRect(job - job % 2);

            Assert.Equal(sameColumnFirst.X, cell.X);
            Assert.Equal(sameRowFirst.Y, cell.Y);
            if (job >= 2)
            {
                Assert.True(cell.Y >= CharacterCreateModal.JobRect(job - 2).Y + CharacterCreateModal.JobRect(job - 2).Height,
                    "job " + job + " is not below job " + (job - 2));
            }
            if (job % 2 == 1)
            {
                HitRect left = CharacterCreateModal.JobRect(job - 1);
                Assert.True(cell.X >= left.X + left.Width, "job " + job + " is not to the right of job " + (job - 1));
            }
            Assert.Equal(job, CharacterCreateModal.HitJob(cell.X + cell.Width / 2, cell.Y + cell.Height / 2));
        }
    }

    // ------------------------------------------------------------------ 画了什么

    [Fact]
    public void With_fonts_every_label_of_the_create_modal_is_drawn_once()
    {
        List<SceneSprite> sprites = Build(new CharacterCreateModal(ShortTitles, null, Scaled(10)));

        string[] once =
        {
            "create-modal-dim", "create-modal-panel", "create-modal-title", "create-modal-divider", "create-modal-preview-box",
            "create-modal-preview-job", "create-modal-name-box", "create-modal-name-placeholder", "create-modal-confirm",
            "create-modal-confirm-text", "create-modal-cancel", "create-modal-cancel-text", "create-modal-status",
            "create-modal-status-bar", "create-modal-status-accent",
            "create-modal-hint-key-0", "create-modal-hint-keytext-0", "create-modal-hint-text-0",
            "create-modal-hint-key-1", "create-modal-hint-keytext-1", "create-modal-hint-text-1",
            "create-modal-hint-key-2", "create-modal-hint-keytext-2", "create-modal-hint-text-2",
        };
        foreach (string tag in once)
        {
            Assert.True(Tagged(sprites, tag).Count == 1, tag + " should be drawn exactly once");
        }
        Assert.Equal(11, Tagged(sprites, "create-modal-job-text").Count);
        Assert.Empty(Tagged(sprites, "create-modal-name-text"));
        Assert.Empty(Tagged(sprites, "create-modal-hint-key-3"));
    }

    [Fact]
    public void With_fonts_every_label_of_the_delete_modal_is_drawn_once()
    {
        List<SceneSprite> sprites = Build(new CharacterDeleteModal(2, "HeroA", "职业0", 25, Scaled(10)));

        string[] once =
        {
            "delete-modal-dim", "delete-modal-panel", "delete-modal-title", "delete-modal-target", "delete-modal-prompt",
            "delete-modal-input-box", "delete-modal-input-placeholder", "delete-modal-confirm", "delete-modal-confirm-text",
            "delete-modal-cancel", "delete-modal-cancel-text",
            "delete-modal-hint-key-0", "delete-modal-hint-keytext-0", "delete-modal-hint-text-0",
            "delete-modal-hint-key-1", "delete-modal-hint-keytext-1", "delete-modal-hint-text-1",
        };
        foreach (string tag in once)
        {
            Assert.True(Tagged(sprites, tag).Count == 1, tag + " should be drawn exactly once");
        }
        Assert.Empty(Tagged(sprites, "delete-modal-input-text"));
        Assert.Empty(Tagged(sprites, "delete-modal-hint-key-2"));
        Assert.Empty(Tagged(sprites, "delete-modal-error"));
        Assert.Empty(Tagged(sprites, "delete-modal-busy"));
    }

    /// <summary>
    /// 提示条的图标:状态条(名字检查的实时说明)不画图标,出错条画图标,忙条画转圈而不画图标;
    /// 删除面板的忙条画转圈、出错条画图标。
    /// </summary>
    [Fact]
    public void Only_the_name_status_bar_goes_without_an_icon_and_the_busy_bars_spin()
    {
        var states = CreateStates(Scaled(10), ShortTitles).ToDictionary(s => s.Name, s => s.Modal);

        List<SceneSprite> status = Build(states["good name typed"]);
        Assert.Single(Tagged(status, "create-modal-status"));
        Assert.Single(Tagged(status, "create-modal-status-accent"));
        Assert.Empty(Tagged(status, "create-modal-status-icon"));
        Assert.Empty(Tagged(status, "create-modal-status-spinner"));

        List<SceneSprite> tooShort = Build(states["too short name typed"]);
        Assert.Single(Tagged(tooShort, "create-modal-status"));
        Assert.Empty(Tagged(tooShort, "create-modal-status-icon"));

        List<SceneSprite> rejected = Build(states["local validation error"]);
        Assert.Single(Tagged(rejected, "create-modal-validation-error-icon"));
        Assert.Empty(Tagged(rejected, "create-modal-validation-error-spinner"));

        List<SceneSprite> failed = Build(states["server failure"]);
        Assert.Single(Tagged(failed, "create-modal-error-icon"));
        Assert.Empty(Tagged(failed, "create-modal-error-spinner"));

        List<SceneSprite> busy = Build(states["busy"]);
        Assert.Single(Tagged(busy, "create-modal-busy-spinner"));
        Assert.Empty(Tagged(busy, "create-modal-busy-icon"));

        var deleteStates = DeleteStates(Scaled(10)).ToDictionary(s => s.Name, s => s.Modal);
        List<SceneSprite> deleteBusy = Build(deleteStates["busy"]);
        Assert.Single(Tagged(deleteBusy, "delete-modal-busy-spinner"));
        Assert.Empty(Tagged(deleteBusy, "delete-modal-busy-icon"));
        List<SceneSprite> deleteFailed = Build(deleteStates["server failure"]);
        Assert.Single(Tagged(deleteFailed, "delete-modal-error-icon"));
        Assert.Empty(Tagged(deleteFailed, "delete-modal-error-spinner"));
    }

    /// <summary>
    /// 标题、主按钮(确认)与职业预览名是加粗的(笔画向右多一像素,整张字帧宽一像素),其余文字不加粗。
    /// 用"每个字都占 10 像素"的字体,字数乘 10 再加描边 2 就是不加粗的宽度,与字号无关,所以不钉字号。
    /// </summary>
    [Fact]
    public void The_titles_the_confirm_buttons_and_the_preview_name_are_bold_and_other_texts_are_not()
    {
        IGlyphFonts ten = Fixed(10);

        List<SceneSprite> create = Build(new CharacterCreateModal(ShortTitles, null, ten));

        Assert.Equal(4 * 10 + 2 + 1, Assert.Single(Tagged(create, "create-modal-title")).Frame.Width);           // 创建角色
        Assert.Equal(4 * 10 + 2 + 1, Assert.Single(Tagged(create, "create-modal-confirm-text")).Frame.Width);    // 确认创建
        Assert.Equal(3 * 10 + 2 + 1, Assert.Single(Tagged(create, "create-modal-preview-job")).Frame.Width);     // 职业0
        Assert.Equal(2 * 10 + 2, Assert.Single(Tagged(create, "create-modal-cancel-text")).Frame.Width);         // 取消
        Assert.Equal(14 * 10 + 2, Assert.Single(Tagged(create, "create-modal-name-placeholder")).Frame.Width);   // 请输入角色名（4-18字节）
        List<SceneSprite> jobTexts = Tagged(create, "create-modal-job-text");
        Assert.Equal(3 * 10 + 2, jobTexts[0].Frame.Width);                                                       // 职业0
        Assert.Equal(4 * 10 + 2, jobTexts[10].Frame.Width);                                                      // 职业10

        List<SceneSprite> delete = Build(new CharacterDeleteModal(2, "HeroA", "职业0", 25, ten));

        Assert.Equal(6 * 10 + 2 + 1, Assert.Single(Tagged(delete, "delete-modal-title")).Frame.Width);          // 删除角色确认
        Assert.Equal(4 * 10 + 2 + 1, Assert.Single(Tagged(delete, "delete-modal-confirm-text")).Frame.Width);   // 确认删除
        Assert.Equal(2 * 10 + 2, Assert.Single(Tagged(delete, "delete-modal-cancel-text")).Frame.Width);        // 取消
        Assert.Equal(20 * 10 + 2, Assert.Single(Tagged(delete, "delete-modal-prompt")).Frame.Width);            // 删除可能不可恢复,请逐字输入角色名确认:
        Assert.Equal(10 * 10 + 2, Assert.Single(Tagged(delete, "delete-modal-input-placeholder")).Frame.Width); // 请输入要删除的角色名
        Assert.Equal(17 * 10 + 2, Assert.Single(Tagged(delete, "delete-modal-target")).Frame.Width);            // Lv25 HeroA  (职业0)
    }
}
