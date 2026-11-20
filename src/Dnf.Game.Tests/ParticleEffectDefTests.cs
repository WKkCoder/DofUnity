using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 全量 .ptl 粒子与发射器解析器 <see cref="ParticleEffectDef"/> 的合成单测（全部数据为手写合成标记流，不含原版内容）。
/// </summary>
public class ParticleEffectDefTests
{
    private static ScriptLine[] ParseFromTokens(Action<TokenStreamBuilder> build, string path = "synthetic/fx.ptl")
    {
        var b = new TokenStreamBuilder();
        build(b);
        return ScriptDocument.Parse(b.Bytes(), new PvfStringTable(b.StringTableBytes()));
    }

    [Fact]
    public void NullLinesThrowsAndEmptyDocumentUsesExactClientDefaults()
    {
        Assert.Throws<ArgumentNullException>(() => ParticleEffectDef.Parse("a.ptl", null!));

        ParticleEffectDef d = ParticleEffectDef.Parse(null!, Array.Empty<ScriptLine>());
        Assert.Equal(string.Empty, d.Path);
        Assert.Equal(new ParticleVec3i(0, 0, 0), d.CreationPos);
        Assert.Equal((ParticleMoveType.Stop, 0, 0, 0, 0), (d.X.Type, d.X.Var1A, d.X.Var1B, d.X.Var2A, d.X.Var2B));
        Assert.Equal((ParticleMoveType.Stop, 0, 0, 0, 0), (d.Y.Type, d.Y.Var1A, d.Y.Var1B, d.Y.Var2A, d.Y.Var2B));
        Assert.Equal((ParticleMoveType.Stop, 0, 0, 0, 0), (d.Z.Type, d.Z.Var1A, d.Z.Var1B, d.Z.Var2A, d.Z.Var2B));
        Assert.Equal(0, d.ChangingTermMs);
        Assert.Equal(ParticleChanging.KeepChanging, d.ChangingType);
        Assert.Equal(0, d.Gravity);
        Assert.Equal(new ParticleVec3i(0, 0, 0), d.RotationAngle);
        Assert.Equal(0, d.LifeTimeMs);
        Assert.Equal(ParticleLanding.Pierce, d.Landing);
        Assert.Equal((100, 100), (d.TransparentStart, d.TransparentEnd));
        Assert.Equal((1000, 1000, 0), (d.AttackBetweenTimeMs, d.ExpansionRate, d.AttackBonusRate));
        Assert.Equal(0f, d.CreationFrequency);
        Assert.Equal((0, 0), (d.MaximumCreateNumber, d.MaximumCreateTimeMs));
        Assert.Equal(new ParticleVec3i(0, 0, 0), d.CreateAreaSize);
        Assert.Equal(new ParticleVec3i(0, 0, 0), d.CreateAreaRotationAngle);
        Assert.Equal(ParticleRotationRangeType.Rectangle, d.RotationAngleRangeType);
        Assert.Equal((0, 0), (d.RotationAngleVar0, d.RotationAngleVar1));
        Assert.Equal(0, d.RandomRange);
        Assert.False(d.CreatePosCorrect);
        Assert.Equal((100, 100, 100), (d.DisplayEffectRateLow, d.DisplayEffectRateMid, d.DisplayEffectRateHigh));
        Assert.Equal((100, 100, 100), (d.DisplayEffectRate(0), d.DisplayEffectRate(1), d.DisplayEffectRate(2)));
        Assert.Equal(ParticleHowToCreate.Randomize, d.HowToCreateObject);
        Assert.Equal(ParticleObjectKind.Animation, d.ObjectType);
        Assert.Equal("[animation]", d.ObjectTypeRaw);
        Assert.Equal(ParticleLayer.Normal, d.Layer);
        Assert.Empty(d.Objects);
        Assert.Equal(string.Empty, d.StartSound);
    }

    [Fact]
    public void ParsesAll32TagsIncludingAxisCursorSwitchingAndBlockObjects()
    {
        ScriptLine[] lines = ParseFromTokens(b =>
        {
            b.Tag("[creation pos]").Ints(12, -34, 56);

            // 显式 [x axis] 之前默认就落在 X 轴
            b.Tag("[move type]").Str("[straight line]");
            b.Tag("[move variable 1]").Ints(100, 250);
            b.Tag("[move variable 2]").Ints(11, 22);

            b.Tag("[y axis]");
            b.Tag("[move type]").Str("[sin]");
            b.Tag("[move variable 1]").Ints(40, 80);
            b.Tag("[move variable 2]").Ints(300, 600);

            b.Tag("[z axis]");
            b.Tag("[move type]").Str("[cos]");
            b.Tag("[move variable 1]").Ints(-20, -60);
            b.Tag("[move variable 2]").Ints(500, 250);

            // 切回 X 后验证 [stop] 能覆盖，再改回 [straight line]
            b.Tag("[x axis]");
            b.Tag("[move type]").Str("[stop]");
            b.Tag("[move type]").Str("[straight line]");

            b.Tag("[changing term]").Int(750);
            b.Tag("[changing type]").Str("[ping pong]");
            b.Tag("[gravity]").Int(980);
            b.Tag("[rotation angle]").Ints(15, 30, 45);
            b.Tag("[life time]").Int(1200);
            b.Tag("[landing type]").Str("[vanish]");
            b.Tag("[transparent value]").Ints(90, 10);

            b.Tag("[attack between time]").Int(250);
            b.Tag("[expansion rate]").Int(1500);
            b.Tag("[attack bonus rate]").Int(35);

            b.Tag("[creation frequency]").Float(12.5f);
            b.Tag("[maximum create number]").Int(24);
            b.Tag("[maximum create time]").Int(1800);
            b.Tag("[create area size]").Ints(80, 40, 20);
            b.Tag("[create area rotation angle]").Ints(5, 10, 15);
            b.Tag("[rotation angle range type]").Str("[circle]");
            b.Tag("[rotation angle variable]").Ints(180, -50);
            b.Tag("[random range]").Int(60);
            b.Tag("[create pos correct]").Int(1);
            b.Tag("[display effect rate]").Ints(30, 65, 85);
            b.Tag("[how to create object]").Str("[in order]");
            b.Tag("[object type]").Str("[passive object]");
            b.Tag("[layer]").Str("[closeback]");

            b.Tag("[object]")
                .Str("part/one.ani").Int(-1)
                .Str("part/frame.img").Int(3)
                .Str(string.Empty).Int(0);
            // 嵌套伪标签与内部标签在 [/object] 关闭前应被跳过
            b.Tag("[gravity]").Int(9999);
            b.Tag("[/object]");

            b.Tag("[start sound]").Str("SYNTH_PARTICLE_HIT");
        });

        ParticleEffectDef d = ParticleEffectDef.Parse("synthetic/all.ptl", lines);
        Assert.Equal("synthetic/all.ptl", d.Path);
        Assert.Equal(new ParticleVec3i(12, -34, 56), d.CreationPos);
        Assert.Equal((ParticleMoveType.StraightLine, 100, 250, 11, 22), (d.X.Type, d.X.Var1A, d.X.Var1B, d.X.Var2A, d.X.Var2B));
        Assert.Equal((ParticleMoveType.Sin, 40, 80, 300, 600), (d.Y.Type, d.Y.Var1A, d.Y.Var1B, d.Y.Var2A, d.Y.Var2B));
        Assert.Equal((ParticleMoveType.Cos, -20, -60, 500, 250), (d.Z.Type, d.Z.Var1A, d.Z.Var1B, d.Z.Var2A, d.Z.Var2B));

        Assert.Equal(750, d.ChangingTermMs);
        Assert.Equal(ParticleChanging.PingPong, d.ChangingType);
        Assert.Equal(980, d.Gravity); // 未被 [object] 块里的 9999 覆盖
        Assert.Equal(new ParticleVec3i(15, 30, 45), d.RotationAngle);
        Assert.Equal(1200, d.LifeTimeMs);
        Assert.Equal(ParticleLanding.Vanish, d.Landing);
        Assert.Equal((90, 10), (d.TransparentStart, d.TransparentEnd));
        Assert.Equal((250, 1500, 35), (d.AttackBetweenTimeMs, d.ExpansionRate, d.AttackBonusRate));

        Assert.Equal(12.5f, d.CreationFrequency);
        Assert.Equal((24, 1800), (d.MaximumCreateNumber, d.MaximumCreateTimeMs));
        Assert.Equal(new ParticleVec3i(80, 40, 20), d.CreateAreaSize);
        Assert.Equal(new ParticleVec3i(5, 10, 15), d.CreateAreaRotationAngle);
        Assert.Equal(ParticleRotationRangeType.Circle, d.RotationAngleRangeType);
        Assert.Equal((180, -50), (d.RotationAngleVar0, d.RotationAngleVar1));
        Assert.Equal(60, d.RandomRange);
        Assert.True(d.CreatePosCorrect);
        Assert.Equal((30, 65, 85), (d.DisplayEffectRateLow, d.DisplayEffectRateMid, d.DisplayEffectRateHigh));
        Assert.Equal((30, 65, 85, 85), (d.DisplayEffectRate(0), d.DisplayEffectRate(1), d.DisplayEffectRate(2), d.DisplayEffectRate(9)));
        Assert.Equal(ParticleHowToCreate.InOrder, d.HowToCreateObject);
        Assert.Equal(ParticleObjectKind.PassiveObject, d.ObjectType);
        Assert.Equal("[passive object]", d.ObjectTypeRaw);
        Assert.Equal(ParticleLayer.CloseBack, d.Layer);

        Assert.Equal(3, d.Objects.Count);
        Assert.Equal(new ParticleObjectEntry("part/one.ani", -1), d.Objects[0]);
        Assert.True(d.Objects[0].IsAni);
        Assert.Equal(new ParticleObjectEntry("part/frame.img", 3), d.Objects[1]);
        Assert.False(d.Objects[1].IsAni);
        Assert.Equal(new ParticleObjectEntry(string.Empty, 0), d.Objects[2]);
        Assert.Equal("SYNTH_PARTICLE_HIT", d.StartSound);
    }

    [Theory]
    [InlineData("[keep changing]", ParticleChanging.KeepChanging)]
    [InlineData("[keep last value]", ParticleChanging.KeepLastValue)]
    [InlineData("[ping pong]", ParticleChanging.PingPong)]
    public void ParsesAllChangingTypes(string keyword, ParticleChanging expected)
    {
        ScriptLine[] lines = ParseFromTokens(b => b.Tag("[changing type]").Str(keyword));
        Assert.Equal(expected, ParticleEffectDef.Parse("c.ptl", lines).ChangingType);
    }

    [Theory]
    [InlineData("[pierce]", ParticleLanding.Pierce)]
    [InlineData("[slide]", ParticleLanding.Slide)]
    [InlineData("[stop]", ParticleLanding.Stop)]
    [InlineData("[vanish]", ParticleLanding.Vanish)]
    public void ParsesAllLandingTypes(string keyword, ParticleLanding expected)
    {
        ScriptLine[] lines = ParseFromTokens(b => b.Tag("[landing type]").Str(keyword));
        Assert.Equal(expected, ParticleEffectDef.Parse("l.ptl", lines).Landing);
    }

    [Theory]
    [InlineData("[close]", ParticleLayer.Close)]
    [InlineData("[normal]", ParticleLayer.Normal)]
    [InlineData("[bottom]", ParticleLayer.Bottom)]
    [InlineData("[closeback]", ParticleLayer.CloseBack)]
    [InlineData("[middleback]", ParticleLayer.MiddleBack)]
    [InlineData("[distantback]", ParticleLayer.DistantBack)]
    public void ParsesAllLayerKeywords(string keyword, ParticleLayer expected)
    {
        ScriptLine[] lines = ParseFromTokens(b => b.Tag("[layer]").Str(keyword));
        Assert.Equal(expected, ParticleEffectDef.Parse("layer.ptl", lines).Layer);
    }

    [Fact]
    public void TypoKeywordsUnknownTagsAndEdgeNumericShapesMatchClientTolerances()
    {
        ScriptLine[] lines = ParseFromTokens(b =>
        {
            // 文件开头的无标签杂项会被跳过，多余的 [/object] 也不会下溢
            b.Int(42).Tag("[/object]");
            b.Tag("[unknown legacy tag]").Int(777);

            // 拼写笔误保持默认，不抛异常
            b.Tag("[x axis]").Tag("[move type]").Str("[keep changing]");
            b.Tag("[landing type]").Str("[vainish]");
            b.Tag("[rotation angle range type]").Str("[round]");
            b.Tag("[how to create object]").Str("[random]");
            b.Tag("[object type]").Str("[animationmation]");

            // 浮点数出现在整型标签 [transparent value] 上需按截断转 int；整数出现在 [creation frequency] 上可转 float
            b.Tag("[transparent value]").Float(79.9f).Float(-12.7f);
            b.Tag("[creation frequency]").Int(-1);
            b.Tag("[create pos correct]").Int(0);

            // 部分缺失的 vec3 保留未提供的 Z 分量
            b.Tag("[creation pos]").Ints(5, 6);

            // [object] 里内嵌子 [object] 块时，内层不会提前关闭外层也不会重复加入对象
            b.Tag("[object]")
                .Str("outer.ani").Int(-1)
                .Str("orphan_without_int")
                .Str("second.img").Float(2.9f)
                .Str("trailing_orphan_at_end");
            b.Tag("[object]").Str("nested_ignored.ani").Int(-1);
            b.Tag("[/object]");
            b.Tag("[gravity]").Int(333); // 仍在 outer [object] 内
            b.Tag("[/object]");
            b.Tag("[life time]").Int(444); // 闭合后恢复正常读取
        });

        ParticleEffectDef d = ParticleEffectDef.Parse("typo.ptl", lines);
        Assert.Equal(ParticleMoveType.Stop, d.X.Type);
        Assert.Equal(ParticleLanding.Pierce, d.Landing);
        Assert.Equal(ParticleRotationRangeType.Rectangle, d.RotationAngleRangeType);
        Assert.Equal(ParticleHowToCreate.Randomize, d.HowToCreateObject);
        Assert.Equal(ParticleObjectKind.Unknown, d.ObjectType);
        Assert.Equal("[animationmation]", d.ObjectTypeRaw);
        Assert.Equal((79, -12), (d.TransparentStart, d.TransparentEnd));
        Assert.Equal(-1f, d.CreationFrequency);
        Assert.False(d.CreatePosCorrect);
        Assert.Equal(new ParticleVec3i(5, 6, 0), d.CreationPos);
        Assert.Equal(2, d.Objects.Count);
        Assert.Equal(new ParticleObjectEntry("outer.ani", -1), d.Objects[0]);
        Assert.Equal(new ParticleObjectEntry("second.img", 2), d.Objects[1]);
        Assert.Equal(0, d.Gravity);
        Assert.Equal(444, d.LifeTimeMs);
    }

    [Fact]
    public void ValueStructsEqualityAndHashCodeBehaveConsistentWithValues()
    {
        var v1 = new ParticleVec3i(1, 2, 3);
        var v2 = new ParticleVec3i(1, 2, 3);
        var v3 = new ParticleVec3i(1, 2, 4);
        Assert.True(v1 == v2);
        Assert.False(v1 != v2);
        Assert.True(v1 != v3);
        Assert.True(v1.Equals((object)v2));
        Assert.False(v1.Equals((object)"x"));
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());

        var e1 = new ParticleObjectEntry("a.ani", -1);
        var e2 = new ParticleObjectEntry("a.ani", -1);
        var e3 = new ParticleObjectEntry("b.ani", -1);
        var e4 = new ParticleObjectEntry("a.ani", 1);
        Assert.True(e1.Equals(e2));
        Assert.True(e1.Equals((object)e2));
        Assert.False(e1.Equals((object)123));
        Assert.False(e1.Equals(e3));
        Assert.False(e1.Equals(e4));
        Assert.Equal(e1.GetHashCode(), e2.GetHashCode());
        Assert.Equal(string.Empty, new ParticleObjectEntry(null!, 0).Path);

        ParticleEffectDef explicitRandomize = ParticleEffectDef.Parse("r.ptl", ParseFromTokens(b =>
            b.Tag("[how to create object]").Str("[in order]")
             .Tag("[how to create object]").Str("[randomize]")
             .Tag("[object type]").Str("[animation]")
             .Tag("[layer]").Str("[normal]")
             .Tag("[rotation angle range type]").Str("[rectangle]")
             .Tag("[changing type]").Str("[keep last value]")));
        Assert.Equal(ParticleHowToCreate.Randomize, explicitRandomize.HowToCreateObject);
        Assert.Equal(ParticleObjectKind.Animation, explicitRandomize.ObjectType);
        Assert.Equal(ParticleLayer.Normal, explicitRandomize.Layer);
        Assert.Equal(ParticleRotationRangeType.Rectangle, explicitRandomize.RotationAngleRangeType);
        Assert.Equal(ParticleChanging.KeepLastValue, explicitRandomize.ChangingType);

        // 客户端真实枚举数值（0x00e57c60 / 0x01223a00）不可漂移
        Assert.Equal((0, 2, 33), ((int)ParticleObjectKind.Unknown, (int)ParticleObjectKind.Animation, (int)ParticleObjectKind.PassiveObject));
        Assert.Equal((0, 1, 2, 3, 4, 5, 6), (
            (int)ParticleLayer.Close, (int)ParticleLayer.Normal, (int)ParticleLayer.Bottom,
            (int)ParticleLayer.CloseBack, (int)ParticleLayer.MiddleBack, (int)ParticleLayer.DistantBack, (int)ParticleLayer.Door));
        Assert.Equal((0, 1, 0, 1), (
            (int)ParticleRotationRangeType.Rectangle, (int)ParticleRotationRangeType.Circle,
            (int)ParticleHowToCreate.InOrder, (int)ParticleHowToCreate.Randomize));

        // 先设 [create pos correct] 1，后续若再出现无整型参数的 [create pos correct] 应保留 true（验证 ReadIntAt 返回 false 不覆盖）
        ParticleEffectDef keepCorrect = ParticleEffectDef.Parse("cpc.ptl", ParseFromTokens(b =>
            b.Tag("[create pos correct]").Int(1)
             .Tag("[create pos correct]").Str("[unused]")));
        Assert.True(keepCorrect.CreatePosCorrect);
    }

    [Fact]
    public void ExistingE103ParticleDefPublicApiRemainsUntouched()
    {
        // 验证与既有 ParticleDef 并存且互不影响
        var axis = new ParticleAxis(ParticleMoveType.Sin, 10, 20, 30, 40, ParticleChanging.PingPong, 100);
        var p = new ParticleDef("legacy.ptl", axis, default, default, 200, ParticleLanding.Slide, 50);
        Assert.Equal("legacy.ptl", p.Path);
        Assert.Equal((ParticleMoveType.Sin, ParticleChanging.PingPong, 200, ParticleLanding.Slide, 50), (p.X.Type, p.X.Changing, p.LifeTimeMs, p.Landing, p.Gravity));
    }
}
