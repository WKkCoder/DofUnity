using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 被动对象 .obj 里 [homing] ... [/homing] 块的读法(M67,证据 E-165):没有这个块就没有追踪;块里没写的项取客户端的缺省值
/// (加载函数 0x0129d570 的缺省值函数 0x01297030);[homing velocity] 的小数截成整数;[sync target move] 与 [straight homing] 只看有没有这个标签。
/// 数据全是合成的。
/// </summary>
public class HomingDefTests
{
    private static HomingDef Read(Action<TokenWriter> block)
    {
        var fx = new Fixture();
        fx.List("passiveobject/passiveobject.lst", (1, "a/x.obj"));
        fx.Tokens("passiveobject/a/x.obj", w =>
        {
            w.Tag("[basic action]").Str("action/main.act");
            block(w);
        });
        PassiveObjectDef def = new GameData(fx.Build()).GetPassiveObject(1);
        Assert.NotNull(def);
        return def.Homing;
    }

    [Fact]
    public void AnObjectWithoutAHomingBlockHasNoHoming()
    {
        Assert.Null(Read(w => w.Tag("[attack info]").Str("attackinfo/hit.atk")));
    }

    [Fact]
    public void AnEmptyBlockTakesTheClientDefaults()
    {
        HomingDef d = Read(w => w.Tag("[homing]").Tag("[/homing]"));
        Assert.NotNull(d);
        Assert.False(d.Use);
        Assert.Equal(HomingFollow.Enemy, d.Follow);
        Assert.Equal(0, d.FollowParam);
        Assert.Equal(0, d.Speed);
        Assert.Equal(0, d.Acceleration);
        Assert.Equal(500, d.CheckGapMs);
        Assert.False(d.SyncAnimationRotation);
        Assert.Equal(0f, d.MaxRotation);
        Assert.Equal(0f, d.InitRotation);
        Assert.Equal(180f, d.DiffRotation);
        Assert.Equal(0, d.TimeMs);
        Assert.Equal(0, d.ZAxis);
        Assert.Equal(-1, d.Direction);
        Assert.Equal(0, d.KeepDistance);
        Assert.False(d.SyncTargetMove);
        Assert.False(d.Straight);
    }

    [Fact]
    public void EveryItemOfTheBlockIsRead()
    {
        HomingDef d = Read(w => w
            .Tag("[homing]")
            .Tag("[homing use]").Int(1)
            .Tag("[homing follow]").Str("[MONSTER]").Int(77)
            .Tag("[homing velocity]").Int(250).Int(40)
            .Tag("[homing check gap]").Int(120)
            .Tag("[sync animation rotation]").Int(1)
            .Tag("[max rotation]").Float(90.5f)
            .Tag("[init rotation]").Float(30f)
            .Tag("[diff rotation]").Float(75f)
            .Tag("[homing time]").Int(1200)
            .Tag("[homing z axis]").Int(3)
            .Tag("[homing direction]").Int(1)
            .Tag("[homing keep distance]").Int(45)
            .Tag("[sync target move]")
            .Tag("[straight homing]")
            .Tag("[/homing]"));
        Assert.True(d.Use);
        Assert.Equal(HomingFollow.Monster, d.Follow);
        Assert.Equal(77, d.FollowParam);
        Assert.Equal(250, d.Speed);
        Assert.Equal(40, d.Acceleration);
        Assert.Equal(120, d.CheckGapMs);
        Assert.True(d.SyncAnimationRotation);
        Assert.Equal(90.5f, d.MaxRotation);
        Assert.Equal(30f, d.InitRotation);
        Assert.Equal(75f, d.DiffRotation);
        Assert.Equal(1200, d.TimeMs);
        Assert.Equal(3, d.ZAxis);
        Assert.Equal(1, d.Direction);
        Assert.Equal(45, d.KeepDistance);
        Assert.True(d.SyncTargetMove);
        Assert.True(d.Straight);
    }

    [Fact]
    public void VelocityFractionsAreTruncatedTowardZeroAndTheAccelerationMayBeNegative()
    {
        HomingDef d = Read(w => w.Tag("[homing]").Tag("[homing velocity]").Float(250.9f).Float(-40.5f).Tag("[/homing]"));
        Assert.Equal(250, d.Speed);
        Assert.Equal(-40, d.Acceleration);

        HomingDef onlySpeed = Read(w => w.Tag("[homing]").Tag("[homing velocity]").Float(99.99f).Tag("[/homing]"));
        Assert.Equal(99, onlySpeed.Speed);
        Assert.Equal(0, onlySpeed.Acceleration);
    }

    [Fact]
    public void FloatTagsAcceptIntegerWriting()
    {
        HomingDef d = Read(w => w
            .Tag("[homing]")
            .Tag("[max rotation]").Int(90)
            .Tag("[init rotation]").Int(-20)
            .Tag("[diff rotation]").Int(60)
            .Tag("[/homing]"));
        Assert.Equal(90f, d.MaxRotation);
        Assert.Equal(-20f, d.InitRotation);
        Assert.Equal(60f, d.DiffRotation);
    }

    [Fact]
    public void TheFollowKindsAreMeEnemyMonsterAndCharacterAndAnUnknownWordKeepsTheDefault()
    {
        Assert.Equal(HomingFollow.Me, Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[ME]").Tag("[/homing]")).Follow);
        Assert.Equal(HomingFollow.Enemy, Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[ENEMY]").Tag("[/homing]")).Follow);

        HomingDef monster = Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[MONSTER]").Int(31).Tag("[/homing]"));
        Assert.Equal((HomingFollow.Monster, 31), (monster.Follow, monster.FollowParam));

        HomingDef character = Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[CHARACTER]").Int(2).Tag("[/homing]"));
        Assert.Equal((HomingFollow.Character, 2), (character.Follow, character.FollowParam));

        HomingDef noNumber = Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[MONSTER]").Tag("[/homing]"));
        Assert.Equal((HomingFollow.Monster, 0), (noNumber.Follow, noNumber.FollowParam));

        HomingDef unknown = Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[NOBODY]").Int(5).Tag("[/homing]"));
        Assert.Equal((HomingFollow.Enemy, 0), (unknown.Follow, unknown.FollowParam));

        Assert.Equal(HomingFollow.Me, Read(w => w.Tag("[homing]").Tag("[homing follow]").Str("[me]").Tag("[/homing]")).Follow);     // 关键字不分大小写
    }

    [Fact]
    public void TheTwoFlagTagsCountAsSetWheneverTheTagIsThereWithoutLookingAtAValue()
    {
        HomingDef bare = Read(w => w.Tag("[homing]").Tag("[sync target move]").Tag("[straight homing]").Tag("[/homing]"));
        Assert.True(bare.SyncTargetMove);
        Assert.True(bare.Straight);

        HomingDef zero = Read(w => w.Tag("[homing]").Tag("[sync target move]").Int(0).Tag("[straight homing]").Int(0).Tag("[/homing]"));
        Assert.True(zero.SyncTargetMove);
        Assert.True(zero.Straight);

        HomingDef absent = Read(w => w.Tag("[homing]").Tag("[homing use]").Int(1).Tag("[/homing]"));
        Assert.False(absent.SyncTargetMove);
        Assert.False(absent.Straight);
    }

    [Fact]
    public void TheValueFlagsAreOffWhenTheyAreZero()
    {
        HomingDef d = Read(w => w.Tag("[homing]").Tag("[homing use]").Int(0).Tag("[sync animation rotation]").Int(0).Tag("[/homing]"));
        Assert.False(d.Use);
        Assert.False(d.SyncAnimationRotation);
        HomingDef on = Read(w => w.Tag("[homing]").Tag("[homing use]").Int(2).Tag("[sync animation rotation]").Int(1).Tag("[/homing]"));
        Assert.True(on.Use);
        Assert.True(on.SyncAnimationRotation);
        HomingDef negative = Read(w => w.Tag("[homing]").Tag("[homing use]").Int(-1).Tag("[sync animation rotation]").Int(-3).Tag("[/homing]"));
        Assert.True(negative.Use);                                     // 只看是不是 0
        Assert.True(negative.SyncAnimationRotation);
    }

    [Fact]
    public void TheBlockIsReadOnceAndReused()
    {
        var fx = new Fixture();
        fx.List("passiveobject/passiveobject.lst", (1, "a/x.obj"));
        fx.Tokens("passiveobject/a/x.obj", w => w.Tag("[homing]").Tag("[homing use]").Int(1).Tag("[/homing]"));
        PassiveObjectDef def = new GameData(fx.Build()).GetPassiveObject(1);
        Assert.Same(def.Homing, def.Homing);
    }
}
