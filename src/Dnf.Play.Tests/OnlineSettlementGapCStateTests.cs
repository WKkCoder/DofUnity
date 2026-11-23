using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算画面绘制输入快照(<see cref="OnlineSettlementRenderState"/>)的默认值与 <see cref="OnlineSettlementOverlay.Build"/> 的整体接线:
/// 默认没有悬停行(HoverRow = -1,不高亮第 0 行)、精灵序号从 0 起逐个 +1(压暗层先、菜单、附加条、战果、提示)、
/// 营火分支的守卫(营地、图包、投影三者缺一不画,且缺任何一个都不抛异常;齐全时画在两层压暗之后、序号从精灵总数续上)。
/// </summary>
public sealed class OnlineSettlementGapCStateTests
{
    private static OnlineSettlementRenderState State(
        double ageMs = 0,
        int? hoverRow = null,
        ISpriteSource sprites = null,
        ClearCamp camp = null,
        DnfAssets assets = null,
        TownProjection? projection = null)
    {
        var state = new OnlineSettlementRenderState
        {
            Summary = OnlineSettlementGapCKit.SmallSummary(),
            AgeMs = ageMs,
            Sprites = sprites,
            Camp = camp,
            Assets = assets,
            Projection = projection,
        };
        if (hoverRow.HasValue)
        {
            state.HoverRow = hoverRow.Value;
        }
        return state;
    }

    // 保护的行为:绘制输入快照的默认值——没有悬停行(-1)、没有倒计时、没有提示、没有营地。
    [Fact]
    public void DefaultRenderStateHasNoHoverRowAndNothingElseSwitchedOn()
    {
        var state = new OnlineSettlementRenderState();
        Assert.Equal(-1, state.HoverRow);
        Assert.Equal(0.0, state.AgeMs);
        Assert.Equal(0L, state.RemainingCountdownMs);
        Assert.False(state.ShowCountdown);
        Assert.Null(state.Summary);
        Assert.Null(state.Notice);
        Assert.Null(state.IsChoiceEnabled);
        Assert.Null(state.Camp);
        Assert.Null(state.Projection);
        Assert.Null(state.Assets);
        Assert.Null(state.Sprites);
    }

    // 保护的行为:没设悬停行时菜单没有任何一行被高亮(尤其不会高亮第 0 行"再次挑战"),显式设 0 才高亮第 0 行;
    // 显式设 -1 与默认一致;悬停第 3 行时只有那一行高亮。图包路径(按钮文字图的"常态/悬停"帧)。
    [Fact]
    public void WithoutAHoverRowNoMenuRowIsHighlightedOnTheSpritePanel()
    {
        // 按钮文字图蓝色通道 = 100 + 首帧号 + 状态:再次挑战 5、快速组队 17、选择地下城 9、返回城镇 13;状态 0 常态、1 悬停
        byte[] normal = { 105, 117, 109, 113 };

        byte[] Tags(OnlineSettlementRenderState state)
        {
            List<SceneSprite> hud = OnlineSettlementOverlay.Build(new OverlayArt(), state, 0);
            return OnlineSettlementGapCKit.Tagged(hud, "settle-label").Select(s => s.Frame.Rgba[2]).ToArray();
        }

        OnlineSettlementGapCKit.FakeSprites sprites = OnlineSettlementGapCKit.PanelSprites(withPanel: true, withRankLetters: false);

        Assert.Equal(normal, Tags(State(sprites: sprites)));
        Assert.Equal(normal, Tags(State(hoverRow: -1, sprites: sprites)));
        Assert.Equal(new byte[] { 106, 117, 109, 113 }, Tags(State(hoverRow: 0, sprites: sprites)));
        Assert.Equal(new byte[] { 105, 117, 109, 114 }, Tags(State(hoverRow: 3, sprites: sprites)));
    }

    // 同上,占位面板路径(取不到图包时):没设悬停行时"RETRY"是白色,显式悬停第 0 行才变黄;
    // 不可用的"PARTY"一直灰色。文字帧按内容缓存,同参数同对象。
    [Fact]
    public void WithoutAHoverRowNoMenuRowIsHighlightedOnTheFallbackPanel()
    {
        var art = new OverlayArt();

        List<SceneSprite> Labels(OnlineSettlementRenderState state) =>
            OnlineSettlementGapCKit.Tagged(OnlineSettlementOverlay.Build(art, state, 0), "settle-label");

        List<SceneSprite> byDefault = Labels(State());
        Assert.Equal(4, byDefault.Count);
        Assert.Same(art.Text("RETRY", 2, OverlayArt.White), byDefault[0].Frame);
        Assert.Same(art.Text("PARTY", 2, 0x808080u), byDefault[1].Frame);
        Assert.Same(art.Text("SELECT", 2, OverlayArt.White), byDefault[2].Frame);
        Assert.Same(art.Text("TOWN", 2, OverlayArt.White), byDefault[3].Frame);

        List<SceneSprite> hoverZero = Labels(State(hoverRow: 0));
        Assert.Same(art.Text("RETRY", 2, OverlayArt.Yellow), hoverZero[0].Frame);
        Assert.Same(art.Text("TOWN", 2, OverlayArt.White), hoverZero[3].Frame);

        List<SceneSprite> hoverNone = Labels(State(hoverRow: -1));
        Assert.Same(art.Text("RETRY", 2, OverlayArt.White), hoverNone[0].Frame);
    }

    // 保护的行为:精灵序号(SortKey)从 0 起逐个 +1 不跳号——有压暗层时后层黑幕 0、前层黑幕 1,接着菜单面板从 2 起;
    // 没有压暗层(刚进结算)时菜单面板的第一个精灵就是 0。图包面板与占位面板两条路径都核对。
    [Fact]
    public void SpriteOrderStartsAtZeroAndCountsUpOneByOne()
    {
        OnlineSettlementGapCKit.FakeSprites sprites = OnlineSettlementGapCKit.PanelSprites(withPanel: true, withRankLetters: false);
        foreach (ISpriteSource source in new ISpriteSource[] { null, sprites })
        {
            // 压暗层满额:两张整屏黑幕,序号 0、1,不透明度 56,层次分别是 BackDimRank 与 DimRank
            List<SceneSprite> dimmed = OnlineSettlementOverlay.Build(new OverlayArt(), State(ageMs: DungeonSession.SettlementDimMs, sprites: source), 0);
            Assert.Equal("settle-dim-back", dimmed[0].AnimationPath);
            Assert.Equal("settle-dim", dimmed[1].AnimationPath);
            Assert.Equal((0, 1), (dimmed[0].SortKey, dimmed[1].SortKey));
            Assert.Equal((OverlayArt.BackDimRank, OverlayArt.DimRank), (dimmed[0].LayerRank, dimmed[1].LayerRank));
            Assert.Equal((56, 56), (dimmed[0].TintAlpha, dimmed[1].TintAlpha));
            Assert.Equal((800, 600), (dimmed[0].Frame.Width, dimmed[0].Frame.Height));
            Assert.Equal((0, 0), (dimmed[0].ScreenX, dimmed[0].ScreenY));
            Assert.Equal(source == null ? "settle-fallback" : "settle-header", dimmed[2].AnimationPath);
            Assert.Equal(2, dimmed[2].SortKey);
            Assert.Equal(Enumerable.Range(0, dimmed.Count), dimmed.Select(s => s.SortKey));

            // 压暗层才开始(淡入到一半):不透明度 28,序号照样 0、1
            List<SceneSprite> half = OnlineSettlementOverlay.Build(new OverlayArt(), State(ageMs: DungeonSession.SettlementDimMs / 2, sprites: source), 0);
            Assert.Equal((28, 28), (half[0].TintAlpha, half[1].TintAlpha));
            Assert.Equal(Enumerable.Range(0, half.Count), half.Select(s => s.SortKey));

            // 没有压暗层:第一个精灵是菜单面板的第一个,序号 0
            List<SceneSprite> bare = OnlineSettlementOverlay.Build(new OverlayArt(), State(ageMs: 0, sprites: source), 0);
            Assert.DoesNotContain(bare, s => s.AnimationPath == "settle-dim-back" || s.AnimationPath == "settle-dim");
            Assert.Equal(source == null ? "settle-fallback" : "settle-header", bare[0].AnimationPath);
            Assert.Equal(0, bare[0].SortKey);
            Assert.Equal(Enumerable.Range(0, bare.Count), bare.Select(s => s.SortKey));
            Assert.Equal(dimmed.Count - 2, bare.Count);
        }
    }

    private static List<SceneSprite> BuildWithCamp(ClearCamp camp, DnfAssets assets, TownProjection? projection, OnlineSettlementGapCKit.FakeSprites sprites)
    {
        // 图包(菜单面板)由 Sprites 单独给,不依赖营地用的 Assets,这样各种组合下菜单部分完全一样
        return OnlineSettlementOverlay.Build(new OverlayArt(), State(ageMs: DungeonSession.SettlementDimMs, sprites: sprites, camp: camp, assets: assets, projection: projection), 0);
    }

    // 保护的行为:营火分支的守卫——营地、图包、投影三者缺一不画,且缺任何一个都不抛异常(尤其"有营地没图包"、"有图包没营地");
    // 三者齐全时营火各层(3 层)画在两层压暗之后、序号从此时的精灵总数续上。
    [Fact]
    public void TheCampfireIsDrawnOnlyWhenCampAssetsAndProjectionAreAllPresent()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        ClearCamp camp = ClearCamp.Create(assets, null, 400, 120);
        Assert.Equal(3, camp.FireLayers);                       // 木柴 + 两层子动画,有真素材可画
        var projection = new TownProjection(0, 200);
        OnlineSettlementGapCKit.FakeSprites sprites = OnlineSettlementGapCKit.PanelSprites(withPanel: true, withRankLetters: false);

        // 基线:没有营地(只有压暗层与菜单面板等)
        List<SceneSprite> baseline = BuildWithCamp(null, null, null, sprites);
        string[] baselineTags = baseline.Select(s => s.AnimationPath).ToArray();
        Assert.DoesNotContain(baseline, s => s.LayerRank == ClearCamp.FireRank && s.AnimationPath.StartsWith("passiveobject", StringComparison.Ordinal));

        // 缺任何一个都不画、不抛异常,精灵与基线完全一样
        void AssertNoCampfire(string what, ClearCamp c, DnfAssets a, TownProjection? p)
        {
            List<SceneSprite> hud = BuildWithCamp(c, a, p, sprites);
            Assert.True(baselineTags.SequenceEqual(hud.Select(s => s.AnimationPath)), what);
            Assert.True(Enumerable.Range(0, hud.Count).SequenceEqual(hud.Select(s => s.SortKey)), what + "(序号)");
        }
        AssertNoCampfire("有营地、没有图包、有投影", camp, null, projection);
        AssertNoCampfire("有营地、没有图包、没有投影", camp, null, null);
        AssertNoCampfire("有营地、有图包、没有投影", camp, assets, null);
        AssertNoCampfire("没有营地、有图包、有投影", null, assets, projection);
        AssertNoCampfire("没有营地、有图包、没有投影", null, assets, null);
        AssertNoCampfire("没有营地、没有图包、有投影", null, null, projection);

        // 齐全:营火三层紧跟在两层压暗之后
        List<SceneSprite> all = BuildWithCamp(camp, assets, projection, sprites);
        Assert.Equal(baseline.Count + 3, all.Count);
        Assert.Equal(new[] { "settle-dim-back", "settle-dim" }, all.Take(2).Select(s => s.AnimationPath));
        Assert.Equal(
            new[] { AvatarFixture.CampBaseAni, AvatarFixture.CampGlowAni, AvatarFixture.CampBaseAni },
            all.Skip(2).Take(3).Select(s => s.AnimationPath));
        Assert.All(all.Skip(2).Take(3), s => Assert.Equal(ClearCamp.FireRank, s.LayerRank));
        // 营火自己的排序键是世界 y(120、115、120);之后的菜单面板序号从精灵总数 5 续上
        Assert.Equal(new[] { 120, 115, 120 }, all.Skip(2).Take(3).Select(s => s.SortKey));
        Assert.Equal("settle-header", all[5].AnimationPath);
        Assert.Equal(Enumerable.Range(5, all.Count - 5), all.Skip(5).Select(s => s.SortKey));
        // 压暗层的序号不受影响
        Assert.Equal((0, 1), (all[0].SortKey, all[1].SortKey));
        // 菜单及之后的精灵与基线逐个同名
        Assert.Equal(baselineTags.Skip(2), all.Skip(5).Select(s => s.AnimationPath));
    }
}
