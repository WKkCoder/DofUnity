namespace Dnf.Play.Tests;

/// <summary>选服务器、选频道的文字表:中文与英文占位、频道名/等级/页码的拼法、拥挤档位的词与字色。</summary>
public sealed class ServerPickerTextTests
{
    private static DirectoryChannel Channel(int number, string title = "测试", int min = 0, int max = 0, ChannelCongestion congestion = ChannelCongestion.Smooth)
        => new DirectoryChannel(number, title, congestion, new ChannelTarget(30000 + number), min, max);

    [Fact]
    public void Every_label_has_chinese_and_a_distinct_ascii_placeholder()
    {
        foreach (PickerLabel label in Enum.GetValues<PickerLabel>())
        {
            string cjk = ServerPickerText.Label(label, true);
            string ascii = ServerPickerText.Label(label, false);
            Assert.False(string.IsNullOrEmpty(cjk), label + " 中文为空");
            Assert.False(string.IsNullOrEmpty(ascii), label + " 英文为空");
            Assert.NotEqual(cjk, ascii);
            Assert.All(ascii, c => Assert.True(c >= 0x20 && c < 0x7F, label + " 的英文占位里有点阵字库画不出的字符"));
            Assert.Equal(ascii.ToUpperInvariant(), ascii);                    // 点阵字库只认大写
            Assert.Contains(cjk, c => c > 0x7F);
        }
    }

    [Fact]
    public void Labels_say_what_the_screens_need_them_to_say()
    {
        Assert.Equal("请选择进行游戏的伺服器。", ServerPickerText.Label(PickerLabel.ServerSubtitle, true));
        Assert.Equal("重试", ServerPickerText.Label(PickerLabel.RetryButton, true));
        Assert.Equal("默认频道", ServerPickerText.Label(PickerLabel.DirectButton, true));
        Assert.Equal("变更伺服器", ServerPickerText.Label(PickerLabel.ChangeServer, true));
        Assert.Equal("结束游戏", ServerPickerText.Label(PickerLabel.QuitGame, true));
        Assert.Equal("无频道", ServerPickerText.Label(PickerLabel.ServerNoChannels, true));
        Assert.Equal("该频道已满员,请选择其他频道", ServerPickerText.Label(PickerLabel.ChannelFull, true));
        Assert.Equal("该频道暂不可用", ServerPickerText.Label(PickerLabel.ChannelUnavailable, true));
        Assert.Equal("没有可用的服务器", ServerPickerText.Label(PickerLabel.ServerListEmpty, true));
        Assert.Equal("这个服务器没有可用的频道", ServerPickerText.Label(PickerLabel.ChannelListEmpty, true));
        Assert.Equal(string.Empty, ServerPickerText.Label((PickerLabel)999, true));
        Assert.Equal(string.Empty, ServerPickerText.Label((PickerLabel)999, false));
    }

    [Fact]
    public void Congestion_words_and_colors_cover_all_five_tiers()
    {
        Assert.Equal("顺畅", ServerPickerText.Congestion(ChannelCongestion.Smooth, true));
        Assert.Equal("良好", ServerPickerText.Congestion(ChannelCongestion.Good, true));
        Assert.Equal("普通", ServerPickerText.Congestion(ChannelCongestion.Normal, true));
        Assert.Equal("拥挤", ServerPickerText.Congestion(ChannelCongestion.Crowded, true));
        Assert.Equal("满载", ServerPickerText.Congestion(ChannelCongestion.Full, true));
        Assert.Equal("SMOOTH", ServerPickerText.Congestion(ChannelCongestion.Smooth, false));
        Assert.Equal("GOOD", ServerPickerText.Congestion(ChannelCongestion.Good, false));
        Assert.Equal("NORMAL", ServerPickerText.Congestion(ChannelCongestion.Normal, false));
        Assert.Equal("CROWDED", ServerPickerText.Congestion(ChannelCongestion.Crowded, false));
        Assert.Equal("FULL", ServerPickerText.Congestion(ChannelCongestion.Full, false));

        Assert.Equal(0x00FF00u, ServerPickerText.CongestionColor(ChannelCongestion.Smooth));        // 基线上量到的纯绿
        Assert.Equal(0xFFB100u, ServerPickerText.CongestionColor(ChannelCongestion.Good));
        Assert.Equal(0xFF8000u, ServerPickerText.CongestionColor(ChannelCongestion.Normal));
        Assert.Equal(0xFF0000u, ServerPickerText.CongestionColor(ChannelCongestion.Crowded));
        Assert.Equal(0xFF00F0u, ServerPickerText.CongestionColor(ChannelCongestion.Full));
        Assert.Equal(5, Enum.GetValues<ChannelCongestion>().Select(c => ServerPickerText.CongestionColor(c)).Distinct().Count());
    }

    [Fact]
    public void A_channel_title_is_ch_plus_the_number_plus_the_name_and_drops_chinese_without_fonts()
    {
        Assert.Equal("ch11.测试", ServerPickerText.ChannelTitle(Channel(11), true));
        Assert.Equal("ch11", ServerPickerText.ChannelTitle(Channel(11), false));
        Assert.Equal("ch03.测试a", ServerPickerText.ChannelTitle(Channel(3, "测试a"), true));
        Assert.Equal("ch03.a", ServerPickerText.ChannelTitle(Channel(3, "测试a"), false));
        Assert.Equal("ch07", ServerPickerText.ChannelTitle(Channel(7, null), true));
        Assert.Equal("ch00", ServerPickerText.ChannelTitle(Channel(0, ""), true));
    }

    [Fact]
    public void The_level_line_exists_only_with_a_valid_recommended_range()
    {
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(Channel(1, "x", 41, 55)));
        Assert.Equal("(Lv.01~20)", ServerPickerText.Level(Channel(1, "x", 1, 20)));
        Assert.Equal("(Lv.30~30)", ServerPickerText.Level(Channel(1, "x", 30, 30)));
        Assert.Equal(string.Empty, ServerPickerText.Level(Channel(1)));
        Assert.Equal(string.Empty, ServerPickerText.Level(Channel(1, "x", 41, 0)));
        Assert.Equal(string.Empty, ServerPickerText.Level(Channel(1, "x", 0, 55)));
        Assert.Equal(string.Empty, ServerPickerText.Level(Channel(1, "x", 55, 41)));
    }

    /// <summary>带固定文字的频道;文字全是合成的,不是真实文案。</summary>
    private static DirectoryChannel WithFixedText(string fixedText, int min = 0, int max = 0)
        => new DirectoryChannel(7, "测试", ChannelCongestion.Smooth, new ChannelTarget(30007), min, max, DirectoryChannel.DefaultSlotFrame, fixedText);

    [Fact]
    public void A_fixed_level_text_replaces_the_numeric_range_verbatim_when_the_font_can_draw_it()
    {
        // 原样返回:不加括号、不去空白、不改大小写;默认参数(cjk = true)与显式 true 一致
        Assert.Equal("合成备注", ServerPickerText.Level(WithFixedText("合成备注", 41, 55)));
        Assert.Equal("合成备注", ServerPickerText.Level(WithFixedText("合成备注", 41, 55), true));
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(WithFixedText("SYNTH-NOTE", 41, 55), true));
        Assert.Equal(" Syn (a) ", ServerPickerText.Level(WithFixedText(" Syn (a) ", 41, 55), true));
        Assert.Equal("a>b\\nc", ServerPickerText.Level(WithFixedText("a>b\\nc", 1, 20), true));              // 文字里的 > 与两字符 \n 不动
    }

    [Fact]
    public void A_fixed_level_text_does_not_need_a_recommended_range()
    {
        // 类型 2 的频道没有推荐等级,照样有第二行;区间反了(HasRecommendedLevel 为假)也一样
        Assert.False(WithFixedText("合成备注").HasRecommendedLevel);
        Assert.Equal("合成备注", ServerPickerText.Level(WithFixedText("合成备注")));
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(WithFixedText("SYNTH-NOTE"), false));
        Assert.Equal("合成备注", ServerPickerText.Level(WithFixedText("合成备注", 55, 41)));
    }

    [Fact]
    public void Without_a_cjk_font_only_an_ascii_fixed_text_is_drawn_and_a_cjk_one_falls_back_to_the_numeric_range()
    {
        // 点阵字库只有 ASCII:含汉字的固定文字退回数值区间(没有区间就是空串);全 ASCII 的照画
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(WithFixedText("合成备注", 41, 55), false));
        Assert.Equal(string.Empty, ServerPickerText.Level(WithFixedText("合成备注"), false));
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(WithFixedText("SYNTH-NOTE", 41, 55), false));
        Assert.Equal("SYNTH-NOTE", ServerPickerText.Level(WithFixedText("SYNTH-NOTE"), false));
        // 边界:U+007F 仍算 ASCII,U+0080 起不算(混进一个字符就整句当中文)
        Assert.Equal("SYNTH\u007FNOTE", ServerPickerText.Level(WithFixedText("SYNTH\u007FNOTE", 41, 55), false));
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(WithFixedText("SYNTH\u0080NOTE", 41, 55), false));
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(WithFixedText("SYNTH-NOTE合", 41, 55), false));
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(WithFixedText("合SYNTH-NOTE", 41, 55), false));
    }

    [Fact]
    public void An_empty_or_missing_fixed_text_means_none_and_keeps_the_old_behaviour()
    {
        DirectoryChannel empty = WithFixedText(string.Empty, 41, 55);
        DirectoryChannel none = WithFixedText(null, 41, 55);
        Assert.Null(empty.FixedLevelText);                           // 空串与 null 都表示"没有"
        Assert.Null(none.FixedLevelText);
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(empty));
        Assert.Equal("(Lv.41~55)", ServerPickerText.Level(none, false));
        Assert.Equal(string.Empty, ServerPickerText.Level(WithFixedText(string.Empty)));
        Assert.Equal(string.Empty, ServerPickerText.Level(WithFixedText(null), false));
        // 老的构造调用不带这个参数,还是"没有"
        Assert.Null(Channel(1).FixedLevelText);
        Assert.Null(new DirectoryChannel(1, "x", ChannelCongestion.Smooth, new ChannelTarget(30001), 1, 20, 5).FixedLevelText);
        Assert.Equal("SYNTH-NOTE", WithFixedText("SYNTH-NOTE").FixedLevelText);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" \r\n ")]
    [InlineData("\u3000")]
    public void A_whitespace_only_fixed_text_is_kept_verbatim_because_only_null_and_empty_mean_none(string whitespace)
    {
        // 契约(DirectoryChannel 的文档注释):只有 null 与空串算"没有";只含空白的文字原样保留——把全空白当作没有是适配层的事,构造函数与画字都不动它。
        DirectoryChannel channel = new DirectoryChannel(7, "测试", ChannelCongestion.Smooth, new ChannelTarget(30007), 41, 55, DirectoryChannel.DefaultSlotFrame, fixedLevelText: whitespace);
        Assert.NotNull(channel.FixedLevelText);
        Assert.Equal(whitespace, channel.FixedLevelText);
        Assert.Equal(whitespace, ServerPickerText.Level(channel));          // cjk 缺省为真:原样画它,不退回 "(Lv.41~55)"
        Assert.Equal(whitespace, ServerPickerText.Level(channel, true));
    }

    [Fact]
    public void Two_spaces_as_the_fixed_text_are_kept_as_two_spaces()
    {
        // 具名参数的写法(与适配层以外的调用方一致):"  " 进去、"  " 出来。
        var channel = new DirectoryChannel(7, "测试", ChannelCongestion.Smooth, new ChannelTarget(30007), fixedLevelText: "  ");
        Assert.Equal("  ", channel.FixedLevelText);
        Assert.Equal("  ", ServerPickerText.Level(channel));
    }

    [Fact]
    public void Page_text_is_current_over_total_counting_from_one()
    {
        Assert.Equal("1/3", ServerPickerText.Page(0, 3));
        Assert.Equal("3/3", ServerPickerText.Page(2, 3));
    }

    [Fact]
    public void The_server_placeholder_counts_from_one()
    {
        Assert.Equal("SERVER 1", ServerPickerText.ServerPlaceholder(0));
        Assert.Equal("SERVER 12", ServerPickerText.ServerPlaceholder(11));
    }

    [Fact]
    public void Ascii_only_keeps_printable_ascii_and_trims()
    {
        Assert.Equal("a b", ServerPickerText.AsciiOnly(" a 测b试 "));
        Assert.Equal(string.Empty, ServerPickerText.AsciiOnly("测试"));
        Assert.Equal(string.Empty, ServerPickerText.AsciiOnly(null));
        Assert.Equal(string.Empty, ServerPickerText.AsciiOnly(""));
        Assert.Equal("ab", ServerPickerText.AsciiOnly("a\tb\n"));                    // 控制字符不留
        Assert.Equal("ab", ServerPickerText.AsciiOnly("a\u007Fb"));       // 0x7F 不可见,不留
    }
}
