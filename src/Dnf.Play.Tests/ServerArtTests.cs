namespace Dnf.Play.Tests;

/// <summary>服务器名 → 原版素材帧号的对照表与名字归一化。</summary>
public sealed class ServerArtTests
{
    private static void AssertEntry(string name, string id, int slot, int gray, int background, int channelIllustration, int channelLabel)
    {
        ServerArtEntry entry = ServerArt.Find(name);
        Assert.NotNull(entry);
        Assert.Equal(id, entry.Id);
        Assert.Equal((slot, gray, background, channelIllustration, channelLabel),
            (entry.SlotFrame, entry.SlotGrayFrame, entry.BackgroundFrame, entry.ChannelIllustrationFrame, entry.ChannelLabelFrame));
    }

    [Fact]
    public void Known_servers_map_to_their_frames()
    {
        AssertEntry("希洛克", "siroco", 8, 9, 4, 6, 7);
        AssertEntry("卡恩", "cain", 4, -1, 2, 2, 3);
        AssertEntry("狄瑞吉", "diregie", 6, -1, 3, 4, 5);
        AssertEntry("普雷", "prey", 10, 11, 5, 8, 9);
        AssertEntry("卡西利亚斯", "casillas", 12, 13, 6, 10, 11);
        AssertEntry("赫尔德", "hilder", 14, 15, 7, 12, 13);
        AssertEntry("卢克", "luke", 16, 17, 8, 14, 15);
        AssertEntry("赛丽亚", "cerria", -1, -1, -1, 16, 17);
        AssertEntry("安徒恩", "anton", 20, 21, 10, 18, 19);
        AssertEntry("卢克西", "lukse", -1, -1, -1, 20, 21);
        AssertEntry("巴卡尔", "bakal", 24, 25, 12, 22, 23);
        AssertEntry("奥兹玛", "ozma", 26, 27, 13, 24, 25);
        AssertEntry("卡赞", "kazan", 28, 29, 14, 26, 27);
        AssertEntry("start", "start", 36, 37, 18, 34, 35);
    }

    [Fact]
    public void Names_match_ignoring_case_spaces_separators_and_the_word_server()
    {
        Assert.Equal("siroco", ServerArt.Find("Siroco").Id);
        Assert.Equal("siroco", ServerArt.Find("SIROCO").Id);
        Assert.Equal("siroco", ServerArt.Find(" 希 洛 克 ").Id);
        Assert.Equal("siroco", ServerArt.Find("希洛克服务器").Id);
        Assert.Equal("siroco", ServerArt.Find("希洛克伺服器").Id);
        Assert.Equal("siroco", ServerArt.Find("Siroco Server").Id);
        Assert.Equal("siroco", ServerArt.Find("希洛克-服").Id);
        Assert.Equal("cain", ServerArt.Find("Cain").Id);
        Assert.Equal("casillas", ServerArt.Find("卡西利亞斯").Id);       // 繁体写法
        Assert.Equal("casillas", ServerArt.Find("Casillas").Id);
    }

    [Fact]
    public void An_exact_alias_wins_over_a_longer_containing_name_and_digits_pick_the_variant()
    {
        Assert.Equal("first-a", ServerArt.Find("体验A").Id);
        Assert.Equal("first-b", ServerArt.Find("体验B").Id);
        Assert.Equal("first-a", ServerArt.Find("体验").Id);
        Assert.Equal("event-1", ServerArt.Find("活动").Id);
        Assert.Equal("event-2", ServerArt.Find("活动2").Id);
        Assert.Equal("event-2", ServerArt.Find("活动 2").Id);
        Assert.Equal("event-3", ServerArt.Find("活动3").Id);
    }

    [Fact]
    public void A_name_that_merely_contains_an_alias_matches_the_longest_alias()
    {
        Assert.Equal("cain", ServerArt.Find("卡恩2区").Id);
        Assert.Equal("siroco", ServerArt.Find("希洛克卡恩").Id);            // 两个都包含,取更长的
        Assert.Equal("cain", ServerArt.Find("卡恩希洛").Id);                // 只有"卡恩"这个写法被包含
    }

    [Fact]
    public void Unknown_empty_or_null_names_have_no_art()
    {
        Assert.Null(ServerArt.Find(null));
        Assert.Null(ServerArt.Find(""));
        Assert.Null(ServerArt.Find("   "));
        Assert.Null(ServerArt.Find("服务器"));        // 归一化后只剩空
        Assert.Null(ServerArt.Find("没见过的"));
        Assert.Null(ServerArt.Find("a"));
        Assert.Null(ServerArt.Find("1"));
    }

    [Fact]
    public void Normalize_lowercases_and_strips_whitespace_separators_and_noise_words()
    {
        Assert.Equal("1", ServerArt.Normalize("  Server-1 "));
        Assert.Equal("希洛克", ServerArt.Normalize("希洛克 服务器"));
        Assert.Equal("希洛克", ServerArt.Normalize("希洛克·伺服器"));
        Assert.Equal("a1", ServerArt.Normalize("A_1.服"));
        Assert.Equal(string.Empty, ServerArt.Normalize(null));
        Assert.Equal(string.Empty, ServerArt.Normalize(""));
    }

    [Fact]
    public void The_table_is_internally_consistent()
    {
        Assert.Equal(19, ServerArt.Count);
        Assert.Equal(19, ServerArt.All.Count);
        Assert.Equal(19, ServerArt.All.Select(e => e.Id).Distinct().Count());
        foreach (ServerArtEntry entry in ServerArt.All)
        {
            Assert.False(string.IsNullOrEmpty(entry.Id));
            Assert.True(entry.SlotGrayFrame == -1 || entry.SlotGrayFrame == entry.SlotFrame + 1, entry.Id + ":灰牌是彩牌的下一帧");
            Assert.Equal(entry.ChannelIllustrationFrame + 1, entry.ChannelLabelFrame);                // 名牌是竖插画的下一帧
            Assert.True(entry.ChannelIllustrationFrame >= 0 && entry.ChannelIllustrationFrame % 2 == 0, entry.Id);
            Assert.True(entry.SlotFrame >= -1 && entry.SlotFrame <= 37, entry.Id);
            Assert.True(entry.BackgroundFrame >= -1 && entry.BackgroundFrame <= 18, entry.Id);
            Assert.True(entry.ChannelLabelFrame <= 35, entry.Id);
        }
    }

    [Fact]
    public void Every_registered_alias_finds_its_own_entry_when_normalized()
    {
        foreach (ServerArtEntry entry in ServerArt.All)
        {
            foreach (string alias in entry.Aliases)
            {
                ServerArtEntry found = ServerArt.Find(alias);
                Assert.True(found != null, "别名 " + alias + " 找不到");
                Assert.True(ReferenceEquals(found, entry) || found.Aliases.Contains(alias), "别名 " + alias + " 找到了别人 " + found.Id);
            }
        }
    }

    [Fact]
    public void The_image_paths_are_the_original_game_files()
    {
        Assert.EndsWith("serverselect/base/server_base.img", ServerArt.BaseImage);
        Assert.EndsWith("serverselect/server_bg/server_bg.img", ServerArt.BackgroundImage);
        Assert.EndsWith("serverselect/serverslot/serverslot.img", ServerArt.SlotImage);
        Assert.EndsWith("channelselect/channelbase/channelbase.img", ServerArt.ChannelBaseImage);
        Assert.EndsWith("channelselect/channelbackground/channelbackground.img", ServerArt.ChannelBackgroundImage);
        Assert.EndsWith("channelselect/channelslot/channelslot.img", ServerArt.ChannelSlotImage);
    }
}
