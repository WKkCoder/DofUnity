using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算画面两层黑幕(<see cref="OnlineSettlementOverlay.Build"/> 里 <c>int dim = DimAlpha(AgeMs); if (dim &gt; 0) { 两层黑幕 }</c>)的出现条件,
/// 重点是压暗不透明度恰为 1 的头几毫秒:此时两层黑幕仍要画出(不能因为"才 1"就省掉),恰为 0 时一层都不画且后面的精灵序号从 0 起。
/// 公式(<see cref="OnlineSettlementOverlay.DimAlpha"/>,沿用离线结算的 500 毫秒线性淡入至 56):
/// 不透明度 = 四舍五入(远离零)(56 × min(AgeMs / 500, 1)),AgeMs ≤ 0 时为 0。
/// </summary>
public sealed class OnlineSettlementGapB6DimLayerTests
{
    private static List<SceneSprite> Build(double ageMs, ISpriteSource sprites = null)
    {
        var state = new OnlineSettlementRenderState
        {
            Summary = OnlineSettlementGapCKit.SmallSummary(),
            AgeMs = ageMs,
            Sprites = sprites,
            IsChoiceEnabled = SettlementArt.IsEnabled,
        };
        return OnlineSettlementOverlay.Build(new OverlayArt(), state, 0);
    }

    /// <summary>两种面板来源:没有图包(后备占位面板,第一个精灵是 settle-fallback)与合成图包(第一个精灵是 settle-header)。</summary>
    private static IEnumerable<(ISpriteSource Source, string FirstPanelTag)> PanelSources()
    {
        yield return (null, "settle-fallback");
        yield return (OnlineSettlementGapCKit.PanelSprites(), "settle-header");
    }

    /// <summary>断言列表头两个精灵正是后层、前层黑幕(不透明度都是 <paramref name="tint"/>),其余精灵紧随其后、序号从 2 起逐个 +1。</summary>
    private static void AssertTwoDimLayers(List<SceneSprite> hud, int tint, string firstPanelTag)
    {
        Assert.Equal("settle-dim-back", hud[0].AnimationPath);
        Assert.Equal("settle-dim", hud[1].AnimationPath);
        // 两层各恰好一个,没有多画
        Assert.Single(hud, s => s.AnimationPath == "settle-dim-back");
        Assert.Single(hud, s => s.AnimationPath == "settle-dim");

        // 摆放:都在屏幕原点;序号 0、1;不透明度 = 压暗值;图层大类:后层只盖地图图层(BackDimRank),前层盖住全部图层与物件(DimRank)
        Assert.Equal(new[] { (0, 0), (0, 0) }, hud.Take(2).Select(s => (s.ScreenX, s.ScreenY)).ToArray());
        Assert.Equal(new[] { 0, 1 }, hud.Take(2).Select(s => s.SortKey).ToArray());
        Assert.Equal(new[] { tint, tint }, hud.Take(2).Select(s => s.TintAlpha).ToArray());
        Assert.Equal(new[] { OverlayArt.BackDimRank, OverlayArt.DimRank }, hud.Take(2).Select(s => s.LayerRank).ToArray());
        Assert.NotEqual(OverlayArt.BackDimRank, OverlayArt.DimRank);

        // 帧:同一块整屏 800 × 600 的纯黑块(不透明度由精灵的 TintAlpha 承担,不是把像素画淡)
        Assert.Same(hud[0].Frame, hud[1].Frame);
        Assert.Equal((800, 600), (hud[0].Frame.Width, hud[0].Frame.Height));
        Assert.Equal(new HashSet<uint> { OverlayArt.Black }, OnlineSettlementGapCKit.OpaqueColors(hud[0].Frame));
        Assert.Equal(800 * 600 * 4, hud[0].Frame.Rgba.Length);

        // 黑幕之后接着是菜单面板的第一个精灵,序号 2;整张列表的序号从 0 起逐个 +1 不跳号
        Assert.Equal(firstPanelTag, hud[2].AnimationPath);
        Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
    }

    // 保护的行为:压暗不透明度恰为 1(淡入刚开始的头几毫秒)时,两层黑幕仍要画出——后层(settle-dim-back)序号 0 在先、前层(settle-dim)序号 1 在后,
    // 两层的不透明度都是 1;后续的菜单面板序号从 2 起(黑幕占了 0 与 1)。图包面板与后备占位面板两条路径都核对。
    // 手算:要让结果恰为 1,需要 0.5 ≤ 56 × AgeMs / 500 < 1.5,即 AgeMs ∈ [4.464…, 13.392…)(因为 56 / 500 = 0.112,0.5 / 0.112 = 4.464…,1.5 / 0.112 = 13.392…);
    // 取 AgeMs = 9 毫秒:t = 9 / 500 = 0.018,56 × 0.018 = 1.008,四舍五入得 1(与区间两端都离得远,不受浮点误差影响)。
    [Fact]
    public void BothDimLayersAreStillDrawnWhenTheDimOpacityIsExactlyOne()
    {
        const double AgeMs = 9.0;
        // 自检:这个年龄确实让压暗不透明度恰为 1(防止公式或常量变了而测试悄悄变成空转)
        Assert.Equal(1, OnlineSettlementOverlay.DimAlpha(AgeMs));

        foreach ((ISpriteSource source, string firstPanelTag) in PanelSources())
        {
            List<SceneSprite> dimmed = Build(AgeMs, source);
            AssertTwoDimLayers(dimmed, 1, firstPanelTag);

            // 与"没有压暗"的同一画面相比:除了多出头两个黑幕,其余精灵逐个同名同位置同不透明度,序号整体后移 2
            List<SceneSprite> bare = Build(0, source);
            Assert.Equal(bare.Count + 2, dimmed.Count);
            Assert.Equal(
                bare.Select(s => (s.AnimationPath, s.ScreenX, s.ScreenY, s.TintAlpha, s.LayerRank, s.SortKey + 2)).ToArray(),
                dimmed.Skip(2).Select(s => (s.AnimationPath, s.ScreenX, s.ScreenY, s.TintAlpha, s.LayerRank, s.SortKey)).ToArray());
        }
    }

    // 保护的行为:压暗不透明度恰为 0(刚进结算、AgeMs = 0)时一层黑幕都不画,菜单面板的第一个精灵序号是 0;
    // AgeMs 为负时同样没有黑幕;刚好越过 0 的最小一档(AgeMs = 4.5 毫秒,56 × 4.5 / 500 = 0.504 → 1)才出现两层。
    // 图包面板与后备占位面板两条路径都核对。
    [Fact]
    public void NoDimLayerIsDrawnWhileTheDimOpacityIsZero()
    {
        foreach ((ISpriteSource source, string firstPanelTag) in PanelSources())
        {
            foreach (double ageMs in new[] { 0.0, -5.0, 4.4 })
            {
                // 手算:AgeMs = 4.4 → 56 × 4.4 / 500 = 0.4928 → 四舍五入得 0;≤ 0 直接是 0
                Assert.Equal(0, OnlineSettlementOverlay.DimAlpha(ageMs));
                List<SceneSprite> hud = Build(ageMs, source);
                Assert.DoesNotContain(hud, s => s.AnimationPath == "settle-dim-back" || s.AnimationPath == "settle-dim");
                Assert.Equal(firstPanelTag, hud[0].AnimationPath);
                Assert.Equal(0, hud[0].SortKey);
                Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
            }

            Assert.Equal(1, OnlineSettlementOverlay.DimAlpha(4.5));
            AssertTwoDimLayers(Build(4.5, source), 1, firstPanelTag);
        }
    }

    // 保护的行为:黑幕的不透明度随停留时长按四舍五入的线性公式取值,头几档(1、2)与淡入尽头(56)都画出两层,且两层的不透明度始终相等、等于公式值。
    // 手算(56 / 500 = 0.112 每毫秒):
    //   AgeMs = 13.3 → 1.4896 → 1;13.4 → 1.5008 → 2;(区间 [13.393…, 22.321…) 都是 2,取 15 → 1.68 → 2);
    //   AgeMs = 250 → 28;AgeMs = 500 → 56(到顶);AgeMs = 600 → t 夹到 1 → 56。
    [Theory]
    [InlineData(5.0, 1)]
    [InlineData(13.0, 1)]
    [InlineData(13.3, 1)]
    [InlineData(13.4, 2)]
    [InlineData(15.0, 2)]
    [InlineData(250.0, 28)]
    [InlineData(500.0, 56)]
    [InlineData(600.0, 56)]
    public void DimLayersCarryTheRoundedLinearOpacityFromTheFirstStepOn(double ageMs, int expectedTint)
    {
        Assert.Equal(expectedTint, OnlineSettlementOverlay.DimAlpha(ageMs));
        List<SceneSprite> hud = Build(ageMs);
        AssertTwoDimLayers(hud, expectedTint, "settle-fallback");
    }
}
