using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 被动对象 .obj 里 .act 驱动的那几项(M36-4,证据 E-130):[basic action]、[etc action]、消失条件,以及列表里的全部编号。
/// 数据是合成的。
/// </summary>
public class PassiveObjectDefTests
{
    private static GameData Data()
    {
        var fx = new Fixture();
        fx.List("passiveobject/passiveobject.lst", (7, "a/x.obj"), (3, "a/missing.obj"), (5, "b/y.obj"));
        fx.Tokens("passiveobject/a/x.obj", w => w
            .Tag("[basic action]").Str("action/main.act")
            .Tag("[etc action]").Str("action/boom.act").Str("").Str("../shared/other.act")
            .Tag("[attack info]").Str("attackinfo/hit.atk")
            .Tag("[object destroy condition]").Str("[destroy condition]").Str("[on end of animation]").Str("[time limite]").Int(1500));
        fx.Tokens("passiveobject/b/y.obj", w => w.Tag("[basic motion]").Str("animation/y.ani").Tag("[attack info]").Str(""));
        return new GameData(fx.Build());
    }

    [Fact]
    public void TheListGivesEveryPassiveObjectIdInOrder()
    {
        Assert.Equal(new[] { 3, 5, 7 }, Data().PassiveObjectIds());
        Assert.Empty(new GameData(new Fixture().Build()).PassiveObjectIds());   // 没有列表
    }

    [Fact]
    public void ActionPathsResolveRelativeToTheObjectFile()
    {
        PassiveObjectDef def = Data().GetPassiveObject(7);
        Assert.NotNull(def);
        Assert.Equal("passiveobject/a/action/main.act", def.BasicActionPath);
        Assert.Equal(new[] { "passiveobject/a/action/boom.act", null, "passiveobject/shared/other.act" }, def.EtcActionPaths);
        Assert.Equal("passiveobject/a/attackinfo/hit.atk", def.AttackInfoPath);
    }

    [Fact]
    public void TheDestroyConditionYieldsTheTimeLimitAndTheEndOfAnimationFlag()
    {
        PassiveObjectDef def = Data().GetPassiveObject(7);
        Assert.Equal(1500, def.TimeLimitMs);
        Assert.True(def.DestroyOnAnimationEnd);
    }

    [Fact]
    public void AnOldStyleObjectHasNoActionsAndNoDestroyCondition()
    {
        GameData data = Data();
        PassiveObjectDef def = data.GetPassiveObject(5);
        Assert.NotNull(def);
        Assert.Null(def.BasicActionPath);
        Assert.Empty(def.EtcActionPaths);
        Assert.Equal("passiveobject/b/animation/y.ani", def.BasicMotionPath);
        Assert.Null(def.AttackInfoPath);                    // 空字符串表示没有攻击信息
        Assert.Equal(-1, def.TimeLimitMs);
        Assert.False(def.DestroyOnAnimationEnd);
        Assert.Null(data.GetPassiveObject(3));              // 列表里有、文件不存在
    }
}
