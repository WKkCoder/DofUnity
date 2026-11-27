using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 创建/删除角色模态面板上"文字内容"的补充测试(r21-createcharui2 第二轮):
/// 服务端的未知错误码要出现在两个模态的失败文字里、创建模态的状态条要按"字节数/规则"的格式拼、删除模态的目标行要按"等级 名字 (职业)"的次序拼。
/// 全部直接驱动模态对象,用内存里的合成字体与合成数据(名字、职业、等级都是编的),不触发任何真实创建或删除。
/// 文字内容靠"把期望的整行用同一套画笔重画一遍、逐像素比较"来钉:合成字体里每个码点的字形图案不同,
/// 所以字符不同、次序不同,画出来的像素就不同(总宽度相同、只是拼接次序写反的错误也能分辨)。
/// </summary>
public sealed class CharacterManageModalsTextTests
{
    /// <summary>
    /// 每个码点一个不同图案的合成字体集:全角字的前进宽度是字号、其余是字号的一半(空格只占宽度),
    /// 字形里每个像素亮不亮由码点的二进制位决定。<see cref="SizedFonts"/> 的字形都是实心方块,分不出"次序写反"。
    /// </summary>
    private sealed class DistinctFonts : IGlyphFonts
    {
        public IGlyphSource ForSize(int pixelSize) => new Source(pixelSize);

        private sealed class Source : IGlyphSource
        {
            private readonly int _px;

            public Source(int px)
            {
                _px = px;
            }

            public int PixelSize => _px;

            public int Ascent => _px - _px / 4;

            public int Descent => _px / 4;

            public int LineHeight => _px + 2;

            public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
            {
                int advance = GlyphText.IsWide(codePoint) ? _px : _px / 2;
                if (codePoint == ' ')
                {
                    glyph = GlyphBitmap.Blank(advance);
                    return true;
                }
                int w = Math.Max(1, advance - 1);
                int h = Math.Max(1, _px * 3 / 4);
                var alpha = new byte[w * h];
                for (int i = 0; i < alpha.Length; i++)
                {
                    alpha[i] = ((codePoint >> (i % 16)) & 1) == 1 ? (byte)255 : (byte)0;
                }
                glyph = new GlyphBitmap(w, h, 0, Ascent, advance, alpha);
                return true;
            }
        }
    }

    private static IGlyphFonts FontsFor(bool withFonts) => withFonts ? new DistinctFonts() : null;

    /// <summary>与模态自己用的画笔同一种配置:数据文字不翻译、字体按给定的来。</summary>
    private static ShellPaint PaintFor(IGlyphFonts fonts) => new ShellPaint(new OverlayArt { Localize = false, Fonts = fonts });

    private static List<SceneSprite> Build(ICharacterManageModal modal)
    {
        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.BuildInto(sprites, ref order, 0);
        return sprites;
    }

    private static List<SceneSprite> Tagged(List<SceneSprite> sprites, string tag) => SpriteQuery.Tagged(sprites, tag);

    // ------------------------------------------------------------------ 失败文字里的未知错误码

    /// <summary>
    /// 服务端回了个不认识的错误码时,失败文字要带上这个码(十六进制两位),两个模态都是;
    /// 有字体时用中文句式,没有字体时用点阵字库认得的英文句式。模态把码交给文案表,漏传就只剩一句没头没尾的"未知错误"。
    /// </summary>
    [Theory]
    [InlineData(false, "UNKNOWN SERVER ERROR 0X2A")]
    [InlineData(true, "服务器返回未知错误（0X2A）")]
    public void The_raw_code_of_an_unknown_server_error_is_part_of_the_failure_text_of_both_modals(bool withFonts, string expected)
    {
        var create = new CharacterCreateModal(fonts: FontsFor(withFonts));
        create.SetFailure(OnlineCharacterManageStatus.UnknownError, 0x2A);
        Assert.Equal(expected, create.ErrorText);

        var delete = new CharacterDeleteModal(1, "HeroOld", "SWORDMAN", 12, FontsFor(withFonts));
        delete.SetFailure(OnlineCharacterManageStatus.UnknownError, 0x2A);
        Assert.Equal(expected, delete.ErrorText);

        // 另一个码给出另一句话;没有码时就没有十六进制部分
        create.SetFailure(OnlineCharacterManageStatus.UnknownError, 0x07);
        Assert.NotEqual(expected, create.ErrorText);
        Assert.Contains("0X07", create.ErrorText);
        delete.SetFailure(OnlineCharacterManageStatus.UnknownError);
        Assert.DoesNotContain("0X", delete.ErrorText);
    }

    // ------------------------------------------------------------------ 创建模态的状态条

    /// <summary>期望的状态条:用同一套画笔、同一个通知条画法重画一遍,取出里面的文字精灵。</summary>
    private static SceneSprite ExpectedStatusLine(IGlyphFonts fonts, ShellTone tone, string text)
    {
        var scratch = new List<SceneSprite>();
        int order = 0;
        OnlineShellArt.AddNoticeBar(scratch, ref order, PaintFor(fonts), tone, text,
            CharacterCreateModal.NoticeRect.X, CharacterCreateModal.NoticeRect.Y, CharacterCreateModal.NoticeRect.Width,
            0, "expected-status", false);
        return Assert.Single(Tagged(scratch, "expected-status"));
    }

    /// <summary>
    /// 没有失败文字、不在忙的时候,创建模态的状态条显示名字的字节数和规则:
    /// 名字空着是"提示"、合法是"成功"、打了字但不合法是"错误"(并带上具体的毛病);有字体用中文句式,没有字体用英文句式。
    /// 数字夹在句子中间的次序、括号的位置、色调、不带图标,都靠与期望整行逐像素比较钉住。
    /// </summary>
    [Theory]
    [InlineData(false, "", ShellTone.Info, "UTF-8 BYTES: 0/18 (4-18 BYTES)")]
    [InlineData(false, "HeroNew", ShellTone.Success, "UTF-8 BYTES: 7/18 (4-18 BYTES)")]
    [InlineData(false, "ab", ShellTone.Error, "NAME NEEDS AT LEAST 4 BYTES (2/18)")]
    [InlineData(true, "", ShellTone.Info, "当前字节数：0/18（4-18字节，不含空格与特殊符号）")]
    [InlineData(true, "HeroNew", ShellTone.Success, "当前字节数：7/18（4-18字节，不含空格与特殊符号）")]
    [InlineData(true, "ab", ShellTone.Error, "角色名至少需要 4 个 UTF-8 字节 (2/18)")]
    public void The_create_modal_status_line_shows_the_byte_count_and_the_name_rule(
        bool withFonts, string typed, ShellTone tone, string expectedText)
    {
        IGlyphFonts fonts = FontsFor(withFonts);
        var modal = new CharacterCreateModal(fonts: fonts);
        modal.Type(typed);

        SceneSprite line = Assert.Single(Tagged(Build(modal), "create-modal-status"));

        SceneSprite expected = ExpectedStatusLine(fonts, tone, expectedText);
        Assert.True(ShellFrames.Same(expected.Frame, line.Frame));
        Assert.Equal(expected.ScreenX, line.ScreenX);
        Assert.Equal(expected.ScreenY, line.ScreenY);
    }

    // ------------------------------------------------------------------ 删除模态的目标行

    /// <summary>
    /// 删除模态顶上的目标行把"等级、名字、职业"按固定的次序和标点拼成一行("Lv" 加等级、空格、名字、两个空格、括号里的职业),
    /// 玩家据此核对删的是不是这个角色;拼接次序写反会让人看错。与期望整行逐像素比较,位置也钉住。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_delete_modal_target_line_shows_level_then_name_then_job_in_brackets(bool withFonts)
    {
        IGlyphFonts fonts = FontsFor(withFonts);
        var modal = new CharacterDeleteModal(3, "HeroOld", "SWORDMAN", 12, fonts);

        SceneSprite target = Assert.Single(Tagged(Build(modal), "delete-modal-target"));

        SpriteFrame expected = PaintFor(fonts).Data(
            "Lv12 HeroOld  (SWORDMAN)", 16, ShellPaint.Gold, ShellPaint.Navy, CharacterDeleteModal.CardWidth - 48);
        Assert.True(ShellFrames.Same(expected, target.Frame));
        Assert.Equal(CharacterDeleteModal.CardX + 24, target.ScreenX);
        Assert.Equal(CharacterDeleteModal.CardY + 60, target.ScreenY);
    }
}
