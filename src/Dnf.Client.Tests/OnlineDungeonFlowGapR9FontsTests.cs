using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;
using static Dnf.Client.Tests.OnlineDungeonFlowGapR4Kit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第九轮补测之一(OnlineDungeonFlowGapR9Fonts):控制器配置副本选择屏时对"字体集"的处理——
/// 流程选项里带了字体集,每一块被配置的选择屏都拿到<b>同一份</b>(引用相同)、屏画面时真的用它;
/// 流程选项里没带字体集,屏自己已经带着的字体集原样保留、不会被抹成 null;选项里带了的话则替换屏原来带的那份。
/// 三条入口都覆盖:流程打开选择屏时的 <c>ConfigureSelect</c> 钩子、直接调用钩子配置一块自己造的屏、控制器晚于选择屏创建时对已开屏的补配置。
/// 全部合成数据、同步执行,不涉及线程调度与墙钟。
/// </summary>
public sealed class OnlineDungeonFlowGapR9FontsTests
{
    /// <summary>
    /// 最小的合成字体集:每个字都是 1×1 的实心点、前进宽度 1;记下被问过的字号。
    /// 每个实例是一份独立的"身份"(测试靠 <c>Assert.Same</c> 区分是哪一份),只在测试线程上用,不需要加锁。
    /// </summary>
    private sealed class ProbeFonts : IGlyphFonts
    {
        public List<int> Requested { get; } = new List<int>();

        public IGlyphSource ForSize(int pixelSize)
        {
            Requested.Add(pixelSize);
            return new Dot(pixelSize);
        }

        private sealed class Dot : IGlyphSource
        {
            public Dot(int px)
            {
                PixelSize = px;
            }

            public int PixelSize { get; }

            public int Ascent => 1;

            public int Descent => 0;

            public int LineHeight => 1;

            public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
            {
                glyph = new GlyphBitmap(1, 1, 0, 1, 1, new byte[] { 255 });
                return true;
            }
        }
    }

    [Fact]
    public void Every_select_screen_the_flow_opens_gets_the_option_fonts_and_draws_with_them()
    {
        // 保护:流程选项带了字体集时,控制器配置每一块新开的选择屏都把"这一份"字体集交给屏(引用相同,不是复制、不是只给第一块);
        // 屏画面时真的用它(占位画面里副本名是数据文字,按 22 号向字体集要字形);控制器释放后钩子撤掉,之后新开的屏不再被配置、没有字体集。
        var fonts = new ProbeFonts();
        using var rig = new Rig(o => o.Fonts = fonts);

        rig.Start();
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        DungeonSelectScreen first = rig.Flow.Select;
        Assert.Same(fonts, first.Fonts);
        first.Build(0);
        Assert.Contains(22, fonts.Requested);   // 两个副本的名字各画一行,都向"选项里那份"字体集要 22 号

        rig.Flow.EnterSelect("again");           // 第二块新屏
        DungeonSelectScreen second = rig.Flow.Select;
        Assert.NotSame(first, second);
        Assert.Same(fonts, second.Fonts);
        Assert.Same(fonts, first.Fonts);         // 第一块屏上的也没被动过

        rig.Ctrl.Dispose();
        Assert.Null(rig.Flow.ConfigureSelect);
        rig.Flow.EnterSelect("after dispose");   // 释放之后新开的屏:没人配置它
        Assert.NotSame(second, rig.Flow.Select);
        Assert.Null(rig.Flow.Select.Fonts);
        Assert.Same(fonts, second.Fonts);        // 已经配置过的屏也不被收回
    }

    [Fact]
    public void A_select_screen_that_already_carries_fonts_keeps_them_when_the_flow_options_have_none()
    {
        // 保护:流程选项没带字体集(null)时,配置选择屏不去碰屏上的字体集——屏本来带着一份(比如宿主在更早的钩子里装的),配置之后还是那一份,
        // 不会被赋成 null(那样屏会回退到点阵占位字)。流程打开屏与直接调用钩子配置自己造的屏,两条路径都要保住。
        var own = new ProbeFonts();
        using var rig = new Rig();
        Assert.Null(rig.Options.Fonts);

        // 路径一:钩子被宿主套了一层,先给新屏装上自己的字体集,再交给控制器的钩子。
        Action<DungeonSelectScreen> controllerHook = rig.Flow.ConfigureSelect;
        Assert.NotNull(controllerHook);
        rig.Flow.ConfigureSelect = screen =>
        {
            Assert.Null(screen.Fonts);           // 新造出来的屏什么字体集也没有
            screen.Fonts = own;
            controllerHook(screen);
        };
        rig.Start();
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Same(own, rig.Flow.Select.Fonts);
        Assert.NotNull(rig.Flow.Select.EntryStatus);   // 控制器的钩子确实跑过了(状态查询已接上)

        // 路径二:直接把一块自己造的、带着字体集的屏交给钩子。
        var mine = new DungeonSelectScreen(rig.Entries, 0, null, rig.Policy.DifficultyLimit, 0) { Fonts = own };
        controllerHook(mine);
        Assert.Same(own, mine.Fonts);
        Assert.NotNull(mine.EntryStatus);

        // 对照:屏本来就没带字体集、选项也没有时,配置之后还是没有。
        var bare = new DungeonSelectScreen(rig.Entries, 0, null, rig.Policy.DifficultyLimit, 0);
        controllerHook(bare);
        Assert.Null(bare.Fonts);
        Assert.NotNull(bare.EntryStatus);
    }

    [Fact]
    public void Option_fonts_replace_the_fonts_a_screen_already_carried()
    {
        // 保护:流程选项带了字体集时,配置选择屏是"赋值"而不是"没有才补":屏原来带的另一份字体集被换成选项里的那份;
        // 选项里的字体集对每一块被配置的屏一视同仁(这里连配两块屏,各带不同的旧字体集,配完都是选项里那份)。
        var optionFonts = new ProbeFonts();
        var oldA = new ProbeFonts();
        var oldB = new ProbeFonts();
        using var rig = new Rig(o => o.Fonts = optionFonts);

        var a = new DungeonSelectScreen(rig.Entries, 0, null, rig.Policy.DifficultyLimit, 0) { Fonts = oldA };
        var b = new DungeonSelectScreen(rig.Entries, 1, null, rig.Policy.DifficultyLimit, 0) { Fonts = oldB };
        rig.Flow.ConfigureSelect(a);
        rig.Flow.ConfigureSelect(b);

        Assert.Same(optionFonts, a.Fonts);
        Assert.Same(optionFonts, b.Fonts);
        Assert.NotSame(oldA, a.Fonts);
        Assert.NotSame(oldB, b.Fonts);
        Assert.Empty(oldA.Requested);            // 旧字体集没被用过
        Assert.Empty(oldB.Requested);
    }

    [Fact]
    public void A_controller_created_over_an_open_select_screen_hands_that_screen_the_option_fonts()
    {
        // 保护:控制器晚于选择屏创建(宿主晚接线)时,构造函数里对"已经打开的那块屏"的补配置也带上选项里的字体集(引用相同);
        // 配置之前那块屏没有字体集(是流程在控制器出现之前自己造的),配置之后才有;屏画面时用的是这份字体集。
        var fonts = new ProbeFonts();
        DungeonSelectScreen before = null;
        using var rig = new Rig4(
            configure: o => o.Fonts = fonts,
            beforeController: f =>
            {
                f.EnterTown("boot");
                f.EnterSelect("gate");
                before = f.Select;
                Assert.Null(before.Fonts);
            });

        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Same(before, rig.Flow.Select);    // 同一块屏被补配置,而不是换了新屏
        Assert.Same(fonts, rig.Flow.Select.Fonts);
        Assert.NotNull(rig.Flow.Select.EntryStatus);
        rig.Flow.Select.Build(0);
        Assert.Contains(22, fonts.Requested);
    }

    [Fact]
    public void A_controller_created_over_an_open_select_screen_keeps_the_fonts_that_screen_already_had_when_options_have_none()
    {
        // 保护:同上那条晚接线的路径,选项没带字体集时不抹掉已开屏自己的字体集:
        // 流程在控制器出现之前先造了屏、屏上装了自己的字体集,控制器一创建就补配置这块屏,配完字体集还是那一份,状态查询已经接上。
        var own = new ProbeFonts();
        using var rig = new Rig4(beforeController: f =>
        {
            f.EnterTown("boot");
            f.EnterSelect("gate");
            f.Select.Fonts = own;
        });

        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Null(rig.Options.Fonts);
        Assert.NotNull(rig.Flow.Select.EntryStatus);   // 补配置确实发生过
        Assert.Same(own, rig.Flow.Select.Fonts);
    }
}
