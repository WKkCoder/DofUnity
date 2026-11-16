using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 记下被问过哪些码点与字号的字体集(里面是合成的方块字体):用来验证"哪些字被送去画了、哪些没有"
/// (例如密码从来不该交给文字渲染)。
/// </summary>
internal sealed class RecordingFonts : IGlyphFonts
{
    private readonly BlockFont _font = new BlockFont();

    public HashSet<int> CodePoints { get; } = new HashSet<int>();

    public HashSet<int> Sizes { get; } = new HashSet<int>();

    public IGlyphSource ForSize(int pixelSize)
    {
        Sizes.Add(pixelSize);
        return new Source(this);
    }

    public bool Asked(string text) => text.All(c => CodePoints.Contains(c));

    private sealed class Source : IGlyphSource
    {
        private readonly RecordingFonts _owner;

        public Source(RecordingFonts owner)
        {
            _owner = owner;
        }

        public int PixelSize => _owner._font.PixelSize;

        public int Ascent => _owner._font.Ascent;

        public int Descent => _owner._font.Descent;

        public int LineHeight => _owner._font.LineHeight;

        public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
        {
            _owner.CodePoints.Add(codePoint);
            return _owner._font.TryGetGlyph(codePoint, out glyph);
        }
    }
}

/// <summary>
/// 字号会影响字宽的合成字体集:全角字的前进宽度是字号、其余是字号的一半,字形是一个实心方块(宽 = 前进宽度减 1);
/// 记下被问过的字号。用来验证"放不下就缩小字号、再放不下就截断"这类和字宽有关的排版。
/// </summary>
internal sealed class SizedFonts : IGlyphFonts
{
    public List<int> Requested { get; } = new List<int>();

    public IGlyphSource ForSize(int pixelSize)
    {
        Requested.Add(pixelSize);
        return new Source(pixelSize);
    }

    private sealed class Source : IGlyphSource
    {
        private readonly int _px;

        public Source(int px)
        {
            _px = px;
        }

        public int PixelSize => _px;

        public int Ascent => _px - _px / 4;

        public int Descent => _px / 4;

        public int LineHeight => _px + 2;

        public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
        {
            int advance = GlyphText.IsWide(codePoint) ? _px : _px / 2;
            if (codePoint == ' ')
            {
                glyph = GlyphBitmap.Blank(advance);
                return true;
            }
            int w = Math.Max(1, advance - 1);
            int h = Math.Max(1, _px * 3 / 4);
            var alpha = new byte[w * h];
            Array.Fill(alpha, (byte)255);
            glyph = new GlyphBitmap(w, h, 0, Ascent, advance, alpha);
            return true;
        }
    }
}

/// <summary>
/// 字形小得离谱的字体集(每个字都是 1 × 1 像素、前进宽度 1):用来验证画面在字体给不出像样字形时也画得出来(标题字小得连光晕都放不下),
/// 而不是抛异常。
/// </summary>
internal sealed class TinyFonts : IGlyphFonts
{
    public IGlyphSource ForSize(int pixelSize) => new Source(pixelSize);

    private sealed class Source : IGlyphSource
    {
        public Source(int px)
        {
            PixelSize = px;
        }

        public int PixelSize { get; }

        public int Ascent => 1;

        public int Descent => 0;

        public int LineHeight => 1;

        public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
        {
            glyph = new GlyphBitmap(1, 1, 0, 1, 1, new byte[] { 255 });
            return true;
        }
    }
}

/// <summary>什么字体也给不出来的字体集(<see cref="IGlyphFonts.ForSize"/> 永远返回 null):用来验证退回点阵字库。</summary>
internal sealed class NoFonts : IGlyphFonts
{
    public IGlyphSource ForSize(int pixelSize) => null;
}

/// <summary>画面测试里常用的帧比较与取像素。</summary>
internal static class ShellFrames
{
    public static bool Same(SpriteFrame a, SpriteFrame b)
    {
        return a != null && b != null && a.Width == b.Width && a.Height == b.Height && a.Rgba.AsSpan().SequenceEqual(b.Rgba);
    }

    public static (int R, int G, int B, int A) Pixel(SpriteFrame frame, int x, int y)
    {
        int o = (y * frame.Width + x) * 4;
        return (frame.Rgba[o], frame.Rgba[o + 1], frame.Rgba[o + 2], frame.Rgba[o + 3]);
    }

    /// <summary>有多少个像素不是全透明。</summary>
    public static int Inked(SpriteFrame frame)
    {
        int count = 0;
        for (int i = 3; i < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i] != 0)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>有多少个像素是完全不透明、颜色恰好是 <paramref name="rgb"/>。</summary>
    public static int Solid(SpriteFrame frame, uint rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF), g = (byte)((rgb >> 8) & 0xFF), b = (byte)(rgb & 0xFF);
        int count = 0;
        for (int i = 0; i < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i] == r && frame.Rgba[i + 1] == g && frame.Rgba[i + 2] == b && frame.Rgba[i + 3] == 255)
            {
                count++;
            }
        }
        return count;
    }

    public static bool HasColor(SpriteFrame frame, uint rgb) => Solid(frame, rgb) > 0;

    /// <summary>两个数相差不超过 <paramref name="tolerance"/>。</summary>
    public static bool Near(int actual, int expected, int tolerance = 2) => Math.Abs(actual - expected) <= tolerance;
}

/// <summary>联网外壳测试用的假图包:只放要测的帧。</summary>
internal sealed class ShellFakeSprites : ISpriteSource
{
    public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite);

    /// <summary>全屏的合成底图(标签色 <paramref name="tag"/>,偏移取自参数,用来验证"原点 + 帧偏移")。</summary>
    public SpriteFrame AddBackground(byte tag = 1, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[800 * 600 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 2] = tag;
            rgba[i + 3] = 255;
        }
        var frame = new SpriteFrame(800, 600, offsetX, offsetY, 800, 600, rgba);
        Frames[(CharacterSelectArt.UiImage, CharacterSelectArt.BackgroundFrame)] = frame;
        return frame;
    }
}

/// <summary>合成的角色列表(名字、装备都是编的;槽位 i 的等级 10 + i、职业 i)。</summary>
internal static class ShellTestData
{
    public static OnlineCharacterList Roster(int count)
    {
        var entries = new List<OnlineCharacterEntry>();
        for (int i = 0; i < count; i++)
        {
            var equips = new[] { new OnlineCharacterEquip((byte)i, (uint)(1000 + i), (uint)(2000 + i), (byte)(3 + i), (uint)(4000 + i), (uint)(5000 + i)) };
            entries.Add(new OnlineCharacterEntry(
                (ushort)i, new byte[] { (byte)(0x41 + i), 0x42, 0x43 }, (byte)(i % 2), (byte)i, (byte)(0x10 + i), (byte)(10 + i), 0, (byte)(i % 3),
                equips, (byte)(7 + i), 0, (uint)(100 + i), 1, (byte)(i % 2), (uint)(200 + i), 1, 0, 2, (uint)(300 + i)));
        }
        return new OnlineCharacterList(6, 1, 2, 77u, entries, 4);
    }
}

/// <summary>
/// 假的联网后端:每个操作默认立刻成功,测试替换对应的委托来给出别的结局(失败、异常、一个手动完成的任务)。
/// 记录每个方法被调了几次、最近一次的参数与令牌,以及调用顺序。
/// </summary>
internal sealed class FakeOnlineBackend : IOnlineBackend
{
    public int LoginCalls;
    public int RegisterCalls;
    public int ChangePasswordCalls;
    public int ConnectCalls;
    public int SwitchCalls;
    public int SelectCalls;
    public int DisconnectCalls;
    public string LastUser;
    public string LastPassword;
    public string LastNewPassword;
    public ChannelTarget LastTarget;
    public int LastSlot = -1;
    public CancellationToken LastToken;
    public readonly List<string> Calls = new List<string>();

    public Func<string, string, CancellationToken, Task<OnlineAuthOutcome>> OnLogin = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
    public Func<string, string, CancellationToken, Task<OnlineAuthOutcome>> OnRegister = (u, p, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
    public Func<string, string, string, CancellationToken, Task<OnlineAuthOutcome>> OnChangePassword = (u, o, n, ct) => Task.FromResult(OnlineAuthOutcome.Ok());
    public Func<ChannelTarget, CancellationToken, Task<OnlineConnectOutcome>> OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
    public Func<ChannelTarget, CancellationToken, Task<OnlineConnectOutcome>> OnSwitch = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
    public Func<int, CancellationToken, Task<OnlineSelectOutcome>> OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Ok());

    public event Action Disconnected;

    public void RaiseDisconnected() => Disconnected?.Invoke();

    public bool HasDisconnectedSubscribers => Disconnected != null;

    public Task<OnlineAuthOutcome> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        LoginCalls++;
        Calls.Add("login");
        LastUser = username;
        LastPassword = password;
        LastToken = cancellationToken;
        return OnLogin(username, password, cancellationToken);
    }

    public Task<OnlineAuthOutcome> RegisterAsync(string username, string password, CancellationToken cancellationToken)
    {
        RegisterCalls++;
        Calls.Add("register");
        LastUser = username;
        LastPassword = password;
        LastToken = cancellationToken;
        return OnRegister(username, password, cancellationToken);
    }

    public Task<OnlineAuthOutcome> ChangePasswordAsync(string username, string oldPassword, string newPassword, CancellationToken cancellationToken)
    {
        ChangePasswordCalls++;
        Calls.Add("change-password");
        LastUser = username;
        LastPassword = oldPassword;
        LastNewPassword = newPassword;
        LastToken = cancellationToken;
        return OnChangePassword(username, oldPassword, newPassword, cancellationToken);
    }

    public Task<OnlineConnectOutcome> ConnectChannelAsync(ChannelTarget target, CancellationToken cancellationToken)
    {
        ConnectCalls++;
        Calls.Add("connect");
        LastTarget = target;
        LastToken = cancellationToken;
        return OnConnect(target, cancellationToken);
    }

    public Task<OnlineConnectOutcome> SwitchChannelAsync(ChannelTarget target, CancellationToken cancellationToken)
    {
        SwitchCalls++;
        Calls.Add("switch");
        LastTarget = target;
        LastToken = cancellationToken;
        return OnSwitch(target, cancellationToken);
    }

    public Task<OnlineSelectOutcome> SelectCharacterAsync(int slot, CancellationToken cancellationToken)
    {
        SelectCalls++;
        Calls.Add("select");
        LastSlot = slot;
        LastToken = cancellationToken;
        return OnSelect(slot, cancellationToken);
    }

    public void Disconnect()
    {
        DisconnectCalls++;
        Calls.Add("disconnect");
    }
}

/// <summary>一个外壳加假后端、日志与合成时间的小装置。</summary>
internal sealed class ShellRig
{
    public readonly FakeOnlineBackend Backend = new FakeOnlineBackend();
    public readonly List<string> Lines = new List<string>();
    public readonly OnlineShellFlow Flow;
    public long Now = 1000;

    public ShellRig(Action<OnlineShellOptions> configure = null)
    {
        var options = new OnlineShellOptions { Log = Lines.Add };
        configure?.Invoke(options);
        Flow = new OnlineShellFlow(Backend, options);
    }

    public void Poll(long advanceMs = 0)
    {
        Now += advanceMs;
        Flow.Poll(Now);
    }

    public void Pump(int times = 4)
    {
        for (int i = 0; i < times; i++)
        {
            Poll();
        }
    }

    /// <summary>在登录界面敲账号、Tab、敲密码。</summary>
    public void Fill(string user = "player_01", string password = "synthetic-pw")
    {
        Flow.Type(user);
        Flow.Key(OnlineShellKey.Tab);
        Flow.Type(password);
    }

    /// <summary>回车提交并轮询一次。</summary>
    public void Submit()
    {
        Flow.Key(OnlineShellKey.Enter);
        Poll();
    }

    /// <summary>从登录走到选角(默认两个角色),假后端全部立刻成功。</summary>
    public void ReachCharacterSelect(int roster = 2)
    {
        Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(roster)));
        Fill();
        Submit();
        Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, Flow.Stage);
    }

    public void ReachInGame(int roster = 2)
    {
        ReachCharacterSelect(roster);
        Flow.Key(OnlineShellKey.Enter);
        Pump(3);
        Assert.Equal(OnlineShellStage.InGame, Flow.Stage);
    }

    public IEnumerable<string> LinesFor(string stage) => Lines.Where(l => l.StartsWith("DNF_ONLINE stage=" + stage + " "));
}
