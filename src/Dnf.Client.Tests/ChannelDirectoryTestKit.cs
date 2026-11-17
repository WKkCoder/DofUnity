using Dnf.Net.ChannelDirectory;

namespace Dnf.Client.Tests;

/// <summary>合成的频道目录快照(服务器名、频道名、地址都是编的;端口默认 31000 + 序号)。</summary>
internal static class DirectoryTestData
{
    /// <summary>一个频道条目。<paramref name="validPort"/> 为 false 时不带"端口有效"标志(端口值仍然写进去,用来测"标志与数值都要看")。</summary>
    public static ChannelEntry Entry(
        string group,
        int number,
        string prefix = "测试",
        ChannelCongestionTier tier = ChannelCongestionTier.Comfortable,
        string host = "",
        int port = 0,
        bool full = false,
        bool validPort = true,
        int? minLevel = null,
        int? maxLevel = null)
    {
        ChannelEntryFlags flags = ChannelEntryFlags.None;
        if (full)
        {
            flags |= ChannelEntryFlags.Full;
        }
        if (validPort)
        {
            flags |= ChannelEntryFlags.HasValidPort;
        }
        if (minLevel.HasValue && maxLevel.HasValue)
        {
            flags |= ChannelEntryFlags.HasRecommendLevel;
        }
        var evaluation = new ChannelCongestionEvaluation(tier, full || tier == ChannelCongestionTier.Full, 0, 0u);
        return new ChannelEntry(group, number, prefix + number, prefix, 1000, 0, host, port == 0 ? 31000 + number : port, evaluation, flags, minLevel, maxLevel);
    }

    public static ChannelGroup Group(string tag, params ChannelEntry[] channels) => new ChannelGroup(tag, 0, channels);

    public static ChannelDirectorySnapshot Snapshot(params ChannelGroup[] groups)
    {
        var flat = groups.SelectMany(g => g.Channels).ToList();
        return new ChannelDirectorySnapshot(null, false, null, groups, flat);
    }

    /// <summary>默认目录:希洛克 3 个频道(序号 1 到 3)、卡恩 2 个频道(序号 4、5)。</summary>
    public static ChannelDirectorySnapshot Default()
    {
        return Snapshot(
            Group("希洛克", Entry("希洛克", 1), Entry("希洛克", 2), Entry("希洛克", 3)),
            Group("卡恩", Entry("卡恩", 4), Entry("卡恩", 5)));
    }
}
