using Dnf.Data;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// serverselect-shot:选服务器界面(<see cref="ServerSelectScreen"/>,基线 S02-client-00 到 02)与选频道界面(<see cref="ChannelSelectScreen"/>,基线 S02-client-03,
/// r18-serverui)在界面打开 --time 毫秒时的 800×600 画面,写成 PNG。目录是合成的(服务器名只用已登记素材的名字,频道名是占位),不连任何服务器。
/// 这条命令没有汉字字体,文字是点阵字库的英文大写占位(版面、配色、形状与有字体时完全一样,只是字换成占位);中文的样子看 Unity 一侧的 EditMode 截图测试。
/// 背景与各件素材来自用户自备数据(只在本机看,不入库);--no-art 不读数据、背景纯色、各件退回程序画的。
/// 默认参数画出来的就是基线的内容(一个"希洛克"服务器带活动角标;选频道是它的一个频道 ch11 带推荐等级 41~55、顺畅)。
/// 选项见 <see cref="Usage"/>。
/// </summary>
internal static class ServerSelectShot
{
    /// <summary>用法片段(Program 的总用法里引用)。</summary>
    public const string Usage = "serverselect-shot --png <输出.png> [--screen server|channel] [--time <毫秒>] [--servers N] [--channels N] [--select N] [--page N] "
        + "[--state ready|loading|empty|failed|connecting|refreshing] [--start-button normal|hover|pressed] [--hover x,y] [--server-name <名字>] [--no-event] "
        + "[--congestion smooth,good,normal,crowded,full,noport,…] [--notice <类别>] [--tint 标签=值,…] [--no-art]";

    private static readonly string[] ServerNames =
    {
        "希洛克", "卡恩", "狄瑞吉", "普雷", "卡西利亚斯", "赫尔德", "卢克", "安徒恩", "巴卡尔", "奥兹玛", "卡赞", "活动", "新手",
    };

    private static readonly string[] ChannelTitles = { "格兰之森", "天空之城", "暗黑城", "黑暗城堡", "时间之门", "洛雷", "幽灵船", "雷鸣废墟" };

    public static int Run(Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("serverselect-shot 需要 --png <输出.png>");
        }
        bool channel = opts.PickerScreen switch
        {
            "server" => false,
            "channel" => true,
            _ => throw new UserError("--screen 需要 server 或 channel"),
        };
        if (opts.PickerState is not ("ready" or "loading" or "empty" or "failed" or "connecting" or "refreshing"))
        {
            throw new UserError("--state 需要 ready、loading、empty、failed、connecting 或 refreshing");
        }
        if (opts.PickerStart is not ("normal" or "hover" or "pressed"))
        {
            throw new UserError("--start-button 需要 normal、hover 或 pressed");
        }
        if (!channel && opts.PickerState is "connecting" or "refreshing")
        {
            throw new UserError("--state " + opts.PickerState + " 只有选频道界面有");
        }
        if (opts.SelectIndex > opts.PickerServers)
        {
            throw new UserError("--select 超出了 --servers 个服务器");
        }
        OnlineNotice? notice = ParseNotice(opts);
        Dictionary<string, int> tints = ParseTints(opts.PickerTints);
        (int X, int Y)? hover = ParsePoint(opts.PickerHover);

        DnfAssets? assets = opts.LoginNoArt ? null : DnfAssets.Open(Locator.Resolve(opts.DnfDir, opts.OutDir).DnfDir);
        try
        {
            ServerPickerScene? scene = assets == null ? null : ServerPickerScene.From(assets);
            ServerDirectory directory = BuildDirectory(opts);
            List<SceneSprite> sprites = channel
                ? BuildChannel(opts, scene, directory, notice, hover)
                : BuildServer(opts, scene, directory, notice, hover);
            sprites = ApplyTints(sprites, tints);
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);
            Console.WriteLine((channel ? "选频道界面" : "选服务器界面") + ":时间 " + opts.TimeMs + " ms,状态 " + opts.PickerState
                + ",服务器 " + opts.PickerServers + " 个,频道 " + opts.PickerChannels + " 个"
                + (notice != null ? ",提示 " + notice.Kind : string.Empty)
                + (assets == null ? ",纯色背景" : ",选角底图背景")
                + ":绘制列表 " + sprites.Count + " 个精灵");
            Console.WriteLine("已写 " + opts.PngFile);
            return 0;
        }
        finally
        {
            assets?.Dispose();
        }
    }

    private static List<SceneSprite> BuildServer(Options opts, ServerPickerScene? scene, ServerDirectory directory, OnlineNotice? notice, (int X, int Y)? hover)
    {
        var screen = new ServerSelectScreen(scene);
        switch (opts.PickerState)
        {
            case "loading":
                screen.SetLoading();
                break;
            case "empty":
                screen.SetDirectory(ServerDirectory.Empty);
                break;
            case "failed":
                screen.SetFailed();
                break;
            default:
                screen.SetDirectory(directory, opts.SelectIndex - 1);
                break;
        }
        screen.SetNotice(notice);
        screen.Advance(opts.TimeMs);
        if (opts.PickerStart != "normal")
        {
            HitRect start = PickerArt.StartRect();
            screen.Hover(start.X + start.Width / 2, start.Y + start.Height / 2);
            if (opts.PickerStart == "pressed")
            {
                screen.Click(start.X + start.Width / 2, start.Y + start.Height / 2);
            }
        }
        else if (hover.HasValue)
        {
            screen.Hover(hover.Value.X, hover.Value.Y);
        }
        return screen.Build(opts.TimeMs);
    }

    private static List<SceneSprite> BuildChannel(Options opts, ServerPickerScene? scene, ServerDirectory directory, OnlineNotice? notice, (int X, int Y)? hover)
    {
        var screen = new ChannelSelectScreen(scene);
        DirectoryServer server = directory.Servers[opts.SelectIndex - 1];
        switch (opts.PickerState)
        {
            case "loading":
                screen.SetLoading();
                break;
            case "empty":
                screen.SetServer(new DirectoryServer(server.Name, null));
                break;
            case "failed":
                screen.SetServer(server);
                screen.SetFailed();
                break;
            default:
                screen.SetServer(server);
                for (int p = 1; p < opts.PickerPage; p++)
                {
                    screen.NextPage();
                }
                if (opts.PickerState == "connecting")
                {
                    screen.SetBusy(ChannelBusy.Connecting);
                }
                else if (opts.PickerState == "refreshing")
                {
                    screen.SetBusy(ChannelBusy.Refreshing);
                }
                break;
        }
        screen.SetNotice(notice);
        screen.Advance(opts.TimeMs);
        if (opts.PickerStart != "normal")
        {
            HitRect start = PickerArt.StartRect();
            screen.Hover(start.X + start.Width / 2, start.Y + start.Height / 2);
            if (opts.PickerStart == "pressed")
            {
                screen.Click(start.X + start.Width / 2, start.Y + start.Height / 2);
            }
        }
        else if (hover.HasValue)
        {
            screen.Hover(hover.Value.X, hover.Value.Y);
        }
        return screen.Build(opts.TimeMs);
    }

    /// <summary>合成目录:--servers 个服务器(第一个用 --server-name,默认"希洛克",带活动角标),每个 --channels 个频道。</summary>
    private static ServerDirectory BuildDirectory(Options opts)
    {
        string[] tiers = opts.PickerCongestion.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tiers.Length == 0)
        {
            tiers = new[] { "smooth" };
        }
        var servers = new List<DirectoryServer>();
        for (int s = 0; s < opts.PickerServers; s++)
        {
            string name = s == 0 ? opts.PickerServerName : (s < ServerNames.Length ? ServerNames[s] : "测试" + (s + 1));
            var channels = new List<DirectoryChannel>();
            for (int c = 0; c < opts.PickerChannels; c++)
            {
                string tier = tiers[c % tiers.Length].ToLowerInvariant();
                ChannelCongestion congestion = tier switch
                {
                    "smooth" => ChannelCongestion.Smooth,
                    "good" => ChannelCongestion.Good,
                    "normal" => ChannelCongestion.Normal,
                    "crowded" => ChannelCongestion.Crowded,
                    "full" => ChannelCongestion.Full,
                    "noport" => ChannelCongestion.Smooth,
                    _ => throw new UserError("--congestion 里的 " + tier + " 不认识,可用:smooth、good、normal、crowded、full、noport"),
                };
                ChannelTarget? target = tier == "noport" ? null : new ChannelTarget(10000 + c);
                channels.Add(new DirectoryChannel(11 + c, ChannelTitles[c % ChannelTitles.Length], congestion, target!, 41 + 5 * (c % 8), 55 + 5 * (c % 8)));
            }
            servers.Add(new DirectoryServer(name, channels, hasEventMark: s == 0 && !opts.PickerNoEvent));
        }
        return new ServerDirectory(servers);
    }

    /// <summary>--tint 标签=值,…:把标签精确匹配的精灵的染色 alpha 改成指定值(基线上光晕的强度随时间变,对拍时把它钉成基线那一刻的值)。</summary>
    private static List<SceneSprite> ApplyTints(List<SceneSprite> sprites, Dictionary<string, int> tints)
    {
        if (tints.Count == 0)
        {
            return sprites;
        }
        var result = new List<SceneSprite>(sprites.Count);
        foreach (SceneSprite s in sprites)
        {
            if (s.AnimationPath != null && tints.TryGetValue(s.AnimationPath, out int tint))
            {
                result.Add(new SceneSprite(s.Frame, s.ScreenX, s.ScreenY, s.SortKey, s.NpcId, s.AnimationPath, s.AniFrame, s.Mirrored, s.Blend, tint, s.LayerRank));
            }
            else
            {
                result.Add(s);
            }
        }
        return result;
    }

    private static Dictionary<string, int> ParseTints(string? text)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq <= 0 || !int.TryParse(part.AsSpan(eq + 1), out int value) || value < 0 || value > 255)
            {
                throw new UserError("--tint 需要 标签=0..255(逗号分隔多项),收到 " + part);
            }
            map[part.Substring(0, eq)] = value;
        }
        return map;
    }

    private static (int X, int Y)? ParsePoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string[] parts = text.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
        {
            throw new UserError("--hover 需要 x,y(画布坐标)");
        }
        return (x, y);
    }

    /// <summary>--notice 的名字(忽略大小写、连字符与下划线)→ 提示;没带返回 null。</summary>
    private static OnlineNotice? ParseNotice(Options opts)
    {
        if (opts.LoginNotice == null)
        {
            return null;
        }
        string wanted = opts.LoginNotice.Replace("-", string.Empty).Replace("_", string.Empty);
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>())
        {
            if (kind != OnlineNoticeKind.None && string.Equals(kind.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return new OnlineNotice(kind, seconds: opts.LoginCountdown);
            }
        }
        string known = string.Join("、", Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None).Select(k => k.ToString()));
        throw new UserError("--notice 不认识 " + opts.LoginNotice + ",可用:" + known);
    }
}
