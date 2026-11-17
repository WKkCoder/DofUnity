using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>假的频道目录来源:默认立刻给出一份目录,测试替换委托来给失败、异常、一个手动完成的任务。记录被调次数与最近的令牌。</summary>
internal sealed class FakeDirectoryBackend : IChannelDirectoryBackend
{
    public int LoadCalls;
    public CancellationToken LastToken;
    public Func<CancellationToken, Task<ServerDirectoryResult>> OnLoad;

    public FakeDirectoryBackend(ServerDirectory directory = null)
    {
        ServerDirectory dir = directory ?? PickerData.Directory(PickerData.Server("希洛克", 3), PickerData.Server("卡恩", 2, 4));
        OnLoad = ct => Task.FromResult(ServerDirectoryResult.Ok(dir));
    }

    public Task<ServerDirectoryResult> LoadAsync(CancellationToken cancellationToken)
    {
        LoadCalls++;
        LastToken = cancellationToken;
        return OnLoad(cancellationToken);
    }
}

/// <summary>合成的目录数据(服务器名、频道名都是编的;端口 = 30000 + 序号,所以每个频道的目标都不同)。</summary>
internal static class PickerData
{
    public static int PortOf(int number) => 30000 + number;

    public static DirectoryChannel Channel(int number, ChannelCongestion congestion = ChannelCongestion.Smooth, string title = "测试",
        int port = 0, string host = null, int min = 0, int max = 0, bool noTarget = false)
    {
        ChannelTarget target = noTarget ? null : new ChannelTarget(port > 0 ? port : PortOf(number), host);
        return new DirectoryChannel(number, title, congestion, target, min, max);
    }

    /// <summary>一个服务器:<paramref name="count"/> 个频道,序号从 <paramref name="firstNumber"/> 起连续。</summary>
    public static DirectoryServer Server(string name, int count, int firstNumber = 1, ChannelCongestion congestion = ChannelCongestion.Smooth, bool eventMark = false)
    {
        var channels = new List<DirectoryChannel>();
        for (int i = 0; i < count; i++)
        {
            channels.Add(Channel(firstNumber + i, congestion));
        }
        return new DirectoryServer(name, channels, eventMark);
    }

    public static ServerDirectory Directory(params DirectoryServer[] servers) => new ServerDirectory(servers);

    /// <summary>n 个服务器 "服务器1".. 各有 <paramref name="channels"/> 个频道(序号全局连续)。</summary>
    public static ServerDirectory Many(int servers, int channels = 2)
    {
        var list = new List<DirectoryServer>();
        int next = 1;
        for (int i = 0; i < servers; i++)
        {
            list.Add(Server("服务器" + (i + 1), channels, next));
            next += channels;
        }
        return new ServerDirectory(list);
    }

    public static ChannelTarget Target(int number) => new ChannelTarget(PortOf(number));
}

/// <summary>
/// 接了真界面的外壳装置:假后端 + 假目录来源 + 合成时间。<see cref="Flow"/> 在登录成功之后停在选服务器(读目录),不自动放行。
/// 没有图包与字体(界面画程序底板、英文占位字),逻辑完全一样。
/// </summary>
internal sealed class PickerRig
{
    public readonly ShellRig Rig;
    public readonly FakeDirectoryBackend Directory;

    public PickerRig(ServerDirectory directory = null, Action<OnlineShellOptions> configure = null)
    {
        Directory = new FakeDirectoryBackend(directory);
        Rig = new ShellRig(options =>
        {
            options.AutoPassPlaceholders = false;
            options.ChannelDirectory = Directory;
            options.DefaultChannelPort = 31999;
            configure?.Invoke(options);
        });
    }

    public OnlineShellFlow Flow => Rig.Flow;

    public FakeOnlineBackend Backend => Rig.Backend;

    public ServerSelectScreen Server => Flow.ServerScreen;

    public ChannelSelectScreen Channel => Flow.ChannelScreen;

    public void Poll(long advanceMs = 0) => Rig.Poll(advanceMs);

    public void Pump(int times = 4) => Rig.Pump(times);

    public void Key(OnlineShellKey key, int pumps = 3)
    {
        Flow.Key(key);
        Pump(pumps);
    }

    public void Click(int x, int y, int pumps = 3)
    {
        Flow.Click(x, y);
        Pump(pumps);
    }

    /// <summary>登录并等到选服务器读完目录(默认目录:希洛克 3 个频道、卡恩 2 个频道)。</summary>
    public void ReachServerSelect()
    {
        Rig.Fill();
        Rig.Submit();
        Pump(3);
        Assert.Equal(OnlineShellStage.ServerSelect, Flow.Stage);
    }

    /// <summary>走到选频道(选服务器停在第一个服务器,回车选定)。</summary>
    public void ReachChannelSelect()
    {
        ReachServerSelect();
        Assert.Equal(PickerPhase.Ready, Server.Phase);
        Key(OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.ChannelSelect, Flow.Stage);
    }

    /// <summary>走到选角(选第一个服务器的第一个频道,连接成功)。</summary>
    public void ReachCharacterSelect(int roster = 2)
    {
        Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(roster)));
        ReachChannelSelect();
        Key(OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.CharacterSelect, Flow.Stage);
    }

    /// <summary>走到游戏里(选角回车进入)。</summary>
    public void ReachInGame(int roster = 2)
    {
        ReachCharacterSelect(roster);
        Key(OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.InGame, Flow.Stage);
    }

    /// <summary>画面里有没有这个标签的精灵。</summary>
    public static bool Has(IEnumerable<SceneSprite> sprites, string tag) => sprites.Any(s => s.AnimationPath == tag);

    public static int Count(IEnumerable<SceneSprite> sprites, string tag) => sprites.Count(s => s.AnimationPath == tag);
}

/// <summary>界面测试用的图包:给选服务器、选频道要用的几张帧各一个能区分的合成帧(宽高各不相同),用引用比较知道画的是哪一帧。</summary>
internal static class PickerSprites
{
    public static ShellFakeSprites Create()
    {
        var sprites = new ShellFakeSprites();
        sprites.AddBackground();
        // 大按钮的四个状态与光
        foreach (int frame in new[] { CharacterSelectArt.StartNormalFrame, CharacterSelectArt.StartHoverFrame, CharacterSelectArt.StartPressedFrame, CharacterSelectArt.StartDisabledFrame, PickerArt.StartGlowFrame })
        {
            sprites.Frames[(CharacterSelectArt.UiImage, frame)] = Block(40 + frame, 20 + frame, 0, 0);
        }
        sprites.Frames[(ServerArt.ChannelSlotImage, ChannelSelectScreen.ItemFrame)] = Block(186, 40, 0, 0);
        sprites.Frames[(ServerArt.ChannelBaseImage, 2)] = Block(186, 40, 0, 0);
        sprites.Frames[(ServerArt.BaseImage, 5)] = Block(170, 41, 0, 0);
        sprites.Frames[(ServerArt.BaseImage, 4)] = Block(30, 30, 0, 0);
        sprites.Frames[(ServerArt.BaseImage, 3)] = Block(36, 14, 0, 0);
        sprites.Frames[(ServerArt.SlotImage, 8)] = Block(170, 41, 0, 0);       // 希洛克的入口牌
        sprites.Frames[(ServerArt.SlotImage, 9)] = Block(170, 41, 0, 0);       // 希洛克的灰牌
        return sprites;
    }

    /// <summary>
    /// 逐件检查画面用的完整图包:两张界面要摆的每一帧都有,而且各自是不同的对象(用引用比较知道画的是哪一帧)。
    /// 窗口边框与暗面板的九个图块分别是 2×3 与 1×1(合成出来的大图尺寸因此是 104×90 与 287×170),小按钮三块是 5、3、5 宽。
    /// 服务器素材只给"希洛克"一份(入口牌与灰牌、插画、名牌);所有帧的偏移都是 0。
    /// </summary>
    public static ShellFakeSprites Full()
    {
        ShellFakeSprites sprites = Create();
        for (int i = 0; i < 9; i++)
        {
            sprites.Frames[(PickerArt.WindowImage, PickerArt.BoardFirstFrame + i)] = Block(2, 3, 0, 0);
            sprites.Frames[(PickerArt.WindowImage, PickerArt.DimFirstFrame + i)] = Block(1, 1, 0, 0);
        }
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ButtonLeftFrame)] = Block(5, 23, 0, 0);
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ButtonMiddleFrame)] = Block(3, 23, 0, 0);
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ButtonRightFrame)] = Block(5, 23, 0, 0);
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ScrollUpFrame)] = Block(10, 14, 0, 0);
        sprites.Frames[(PickerArt.WindowImage, CharacterSelectArt.ScrollDownFrame)] = Block(10, 14, 0, 0);
        // server_base:0 标题字、1 标题光晕、2 分隔线、3 活动角标、4 角标光晕、5 选中光、6 顶部标题
        for (int f = 0; f <= 6; f++)
        {
            sprites.Frames[(ServerArt.BaseImage, f)] = Block(60 + f, 10 + f, 0, 0);
        }
        sprites.Frames[(ServerArt.BackgroundImage, 4)] = Block(300, 300, 0, 0);                  // 希洛克的大插画
        sprites.Frames[(ServerArt.ChannelBackgroundImage, 6)] = Block(80, 560, 0, 0);            // 希洛克的竖插画
        sprites.Frames[(ServerArt.ChannelBackgroundImage, 7)] = Block(150, 40, 0, 0);            // 希洛克的名牌
        // channelbase:0 标题、1 横幅、2 选中框、3 到 6 刷新(常态、悬停、按下、灰)、8 分隔线、14 提示条
        foreach (int f in new[] { 0, 1, 3, 4, 5, 6, 8, 14 })
        {
            sprites.Frames[(ServerArt.ChannelBaseImage, f)] = Block(70 + f, 20, 0, 0);
        }
        return sprites;
    }

    /// <summary>画面里恰好一个这个标签的精灵(多了少了都算失败)。</summary>
    public static SceneSprite One(IEnumerable<SceneSprite> sprites, string tag)
    {
        var found = sprites.Where(s => s.AnimationPath == tag).ToList();
        Assert.True(found.Count == 1, "标签 " + tag + " 应有 1 个精灵,实际 " + found.Count);
        return found[0];
    }

    /// <summary>这个精灵画的是不是图包里那一帧(引用相等)。</summary>
    public static bool IsFrame(SceneSprite sprite, ShellFakeSprites art, string image, int frame)
    {
        return ReferenceEquals(sprite.Frame, art.Frames[(image, frame)]);
    }

    public static SpriteFrame Block(int width, int height, int offsetX, int offsetY)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 1] = 100;
            rgba[i + 2] = 50;
            rgba[i + 3] = 255;
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }
}
