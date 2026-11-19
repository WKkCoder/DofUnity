namespace Dnf.Play.Tests;

/// <summary>
/// <see cref="OnlineHeroProfile"/> 构造器自己的参数缺省值:最后一个参数 <c>isSyncedSession</c> 缺省为 true。
/// <c>FromEntry</c> 总是显式传值,所以这个缺省值只有"直接 new 且不传最后一个参数"才会用到。数据全是合成值(唯一号 9201/9202、角色名"测试角色1")。
/// </summary>
public sealed class OnlineHeroProfileDefaultsTests
{
    [Fact]
    public void A_profile_built_without_the_session_flag_is_a_synced_session()
    {
        var profile = new OnlineHeroProfile(0, 9201, 9202, 0, 0, 0, 0, 20, "测试角色1", "测试职业", 0, null, default, null);

        Assert.True(profile.IsSyncedSession);
    }

    [Fact]
    public void An_explicit_session_flag_is_kept_both_ways()
    {
        var unsynced = new OnlineHeroProfile(0, 9201, 9202, 0, 0, 0, 0, 20, "测试角色1", "测试职业", 0, null, default, null, false);
        var synced = new OnlineHeroProfile(0, 9201, 9202, 0, 0, 0, 0, 20, "测试角色1", "测试职业", 0, null, default, null, true);

        Assert.False(unsynced.IsSyncedSession);
        Assert.True(synced.IsSyncedSession);
    }

    [Fact]
    public void Leaving_the_session_flag_out_does_not_change_any_other_field()
    {
        var withDefault = new OnlineHeroProfile(2, 9201, 9202, 2, 0, 0, 0, 20, "测试角色1", "测试职业", 3, 11, default, 4);

        Assert.True(withDefault.IsSyncedSession);
        Assert.Equal(2, withDefault.Slot);
        Assert.Equal(2, withDefault.Job);
        Assert.True(withDefault.JobFallback);
        Assert.Equal(3, withDefault.AppearanceEquipCount);
        Assert.Equal(11, withDefault.ServerVillage);
        Assert.Equal(4, withDefault.DungeonPermissionCount);
    }
}
