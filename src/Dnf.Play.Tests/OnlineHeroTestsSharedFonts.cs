using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网主角名牌注入测试里"要临时换掉全进程共用字体集(<c>OverlayArt.SharedFonts</c>)"的那一条,
/// 从 <see cref="OnlineHeroTests"/> 里原样搬出来(断言、夹具、测试名都没变)。
/// 共用字体集是全进程唯一的静态状态:这条测试运行的那几十毫秒里,别的测试类(默认假设没有共用字体集)若并行执行,
/// 就会看到"有字体"而随机失败。所以放进 <see cref="GlobalStateCollection"/>(禁止并行的集合),
/// 它在所有可并行的测试跑完之后才单独串行执行;以后别把它搬回 <see cref="OnlineHeroTests"/>。
/// 类名特意沿用 <c>OnlineHeroTests</c> 前缀:按"OnlineHeroTests"子串过滤测试名的脚本(变异复跑、集成过滤)照样选得中它。
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class OnlineHeroTestsSharedFonts
{
    // 与 OnlineHeroTests 里的同名辅助一致(那边是 private,这里不跨类引用,抄一份合成数据的构造器)
    private static OnlineCharacterEntry Entry(
        ushort uid = 9101,
        string name = "测试剑魂",
        byte job = 0,
        byte growTypeByte = 0x12,
        byte level = 55)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name ?? string.Empty);
        return new OnlineCharacterEntry(uid, nameBytes, 0, job, growTypeByte, level, 0, 0, Array.Empty<OnlineCharacterEquip>(),
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void Room_combat_nameplate_uses_online_progress_name_and_allows_direct_override_with_local_default()
    {
        using var fx = new HostedRoomFixture();
        OnlineCharacterEntry entry = Entry(uid: 9107, name: "剑舞星河", job: 0, growTypeByte: 0x00, level: 10);
        HeroProgress online = HeroProgress.CreateForOnlineHero(fx.Assets, OnlineHeroProfile.FromEntry(entry, 0));
        var local = new HeroProgress(ExpTests.Table(), ExpTests.Sp(), CharacterGrowth.Parse(ExpTests.GrowthLines()), null, null);

        Assert.Equal(CharacterRoster.LocalName, local.HeroName);
        Assert.Equal("剑舞星河", online.HeroName);

        local.HeroName = string.Empty;
        Assert.Equal(CharacterRoster.LocalName, local.HeroName);

        var fonts = new RecordingFonts();
        // 记下进入时的共用字体集,finally 里写回旧值(而不是写死 null):不管之前是什么,都还原成原样
        var previousFonts = OverlayArt.SharedFonts;
        OverlayArt.SharedFonts = fonts;
        try
        {
            RoomCombatSession session = RoomCombatSession.Create(fx.Assets, fx.Room, 1, heroVitals: true, progress: online);
            _ = session.Build(100);
            Assert.True(fonts.Asked("剑舞星河"));

            fonts.CodePoints.Clear();
            session.HeroName = "临时改名";
            _ = session.Build(200);
            Assert.True(fonts.Asked("临时改名"));

            fonts.CodePoints.Clear();
            RoomCombatSession fallback = RoomCombatSession.Create(fx.Assets, fx.Room, 1, heroVitals: true, progress: null);
            _ = fallback.Build(300);
            Assert.True(fonts.Asked(CharacterRoster.LocalName));
        }
        finally
        {
            OverlayArt.SharedFonts = previousFonts;
        }
    }
}
