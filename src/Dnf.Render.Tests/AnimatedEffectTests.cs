using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 世界里的一次性动画特效(证据 E-076)。数据是合成的:350 毫秒——空帧 150、皮肤 IMG 第 0 帧 50、第 1 帧 50、空帧 100(见 <see cref="AvatarFixture.HardAttackEffect1"/>),
/// 皮肤 IMG 的第 k 帧红色通道 = 200 + k,帧头偏移 (-3, -4),宽 2;.ani 帧偏移 (-5, -8)。
/// </summary>
public class AnimatedEffectTests
{
    [Fact]
    public void AnEffectShowsNothingDuringItsLeadingEmptyFrameThenTheImageFramesInOrderAndEndsAfterItsDuration()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(0, 200);
        AnimatedEffect e = AnimatedEffect.Create(assets, AvatarFixture.HardAttackEffect1, 100, 50, 0, false);

        Assert.NotNull(e);
        Assert.Equal(AvatarFixture.EffectDurationMs, e.DurationMs);
        Assert.False(e.Finished);
        Assert.Null(e.Sprite(assets, projection));                       // 第 0 帧是空帧(0 到 150 毫秒)
        e.Advance(149);
        Assert.Null(e.Sprite(assets, projection));

        e.Advance(1);                                                    // 150 毫秒:第 1 帧,皮肤 IMG 第 0 帧
        SceneSprite a = e.Sprite(assets, projection);
        Assert.Equal((1, 200), (a.AniFrame, (int)a.Frame.Rgba[0]));
        // 位置 = 世界 − 镜头 + .ani 偏移 + IMG 头偏移:x = 100 − 5 − 3 = 92,y = 50 + 200 − 8 − 4 = 238;排序键比主角的世界 y 大 1,画在主角前面
        Assert.Equal((92, 238, 51), (a.ScreenX, a.ScreenY, a.SortKey));
        Assert.Equal((SpriteBlend.Normal, false, -1), (a.Blend, a.Mirrored, a.NpcId));
        Assert.Equal(AvatarFixture.HardAttackEffect1, a.AnimationPath);

        e.Advance(50);                                                   // 200 毫秒:第 2 帧,皮肤 IMG 第 1 帧
        SceneSprite b = e.Sprite(assets, projection);
        Assert.Equal((2, 201), (b.AniFrame, (int)b.Frame.Rgba[0]));

        e.Advance(50);                                                   // 250 毫秒:尾部的空帧
        Assert.Null(e.Sprite(assets, projection));
        Assert.False(e.Finished);
        e.Advance(99);
        Assert.False(e.Finished);
        e.Advance(1);                                                    // 350 毫秒:播完
        Assert.True(e.Finished);
        Assert.Null(e.Sprite(assets, projection));
        e.Advance(-10);                                                  // 负的时间不倒带
        Assert.True(e.Finished);
    }

    [Fact]
    public void FacingLeftMirrorsAboutTheWorldXAndTheFramesOwnGraphicEffectDecidesTheBlend()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(10, 200);
        AnimatedEffect plain = AnimatedEffect.Create(assets, AvatarFixture.HardAttackEffect1, 100, 50, 0, true, sortBias: 3);
        AnimatedEffect glow = AnimatedEffect.Create(assets, AvatarFixture.HardAttackEffect2, 100, 50, 0, true);
        plain.Advance(160);
        glow.Advance(160);

        SceneSprite p = plain.Sprite(assets, projection);
        SceneSprite g = glow.Sprite(assets, projection);
        Assert.True(p.Mirrored);
        // 翻转后的左边缘 = 世界 x − 镜头 − (.ani x + IMG x + 宽) = 100 − 10 − (−5 − 3 + 2) = 96
        Assert.Equal((96, 238, 53), (p.ScreenX, p.ScreenY, p.SortKey));
        Assert.Equal(SpriteBlend.Normal, p.Blend);
        Assert.Equal(SpriteBlend.Additive, g.Blend);                     // 第二层每帧带图形特效 2
    }

    [Fact]
    public void ALiftedEffectDrawsHigherAndAMissingOrEmptyAnimationMakesNoEffect()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(0, 200);
        AnimatedEffect high = AnimatedEffect.Create(assets, AvatarFixture.HardAttackEffect1, 100, 50, 30, false);
        high.Advance(150);
        Assert.Equal(238 - 30, high.Sprite(assets, projection).ScreenY);

        Assert.Null(AnimatedEffect.Create(assets, "character/test/effect/animation/nothing.ani", 0, 0, 0, false));
        Assert.Null(AnimatedEffect.Create(assets, null, 0, 0, 0, false));
        Assert.Null(AnimatedEffect.Create(null, AvatarFixture.HardAttackEffect1, 0, 0, 0, false));
    }
}
