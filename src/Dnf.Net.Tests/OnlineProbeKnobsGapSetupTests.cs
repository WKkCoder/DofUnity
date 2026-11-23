using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>orchestrated</c> 进城之前那段装配(<see cref="OnlineOrchestratedSetup.Prepare"/>)里"仓库根取哪个参数"的边界:
/// 参数个数恰为 0、1、2 以及更多时,交给装配回调(<c>load</c>)的仓库根与选项覆盖各是什么。
/// 回调是记录入参的假回调,期望值全是手写的合成路径字面量;不读本机配置、不联网。
/// </summary>
public sealed class OnlineProbeKnobsGapSetupTests
{
    private const string DefaultRoot = "SYNTH-GAP-DEFAULT-ROOT";
    private const string GivenRoot = "SYNTH-GAP-GIVEN-ROOT";

    private static string[] Args(string line)
    {
        return line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
    }

    /// <summary>记录入参的假装配回调:预置的设置里客户端版本 7、服务器组 9,用来区分"选项覆盖了"与"没动"。</summary>
    private sealed class RecordingLoader
    {
        internal int Calls;
        internal string RootSeen;
        internal OnlineEntrySettings Settings = OnlineSwitchTestKit.Settings(clientVersion: 7, serverGroup: 9);

        internal bool Load(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            Calls++;
            RootSeen = repoRoot;
            settings = Settings;
            problem = null;
            return true;
        }
    }

    /// <summary>
    /// 这条断言保护的行为:参数个数恰为 0、1、2、3、4、5 时,装配回调收到的仓库根与选项覆盖都对——
    /// 只有仓库根(个数恰为 1)时回调收到的就是它(不能漏成默认值,也不能去读不存在的第二个参数);
    /// 没有参数或第一个参数以 <c>--</c> 开头时回调收到默认仓库根,且选项照常生效。
    /// 推导:仓库根只能是第一个参数且不以 <c>--</c> 开头;预置版本 7、组 9,命令行给了才覆盖。
    /// </summary>
    [Theory]
    [InlineData("", DefaultRoot, 7u, 9)]                                                              // 0 个参数:默认根,设置不动
    [InlineData("SYNTH-GAP-GIVEN-ROOT", GivenRoot, 7u, 9)]                                            // 恰好 1 个参数,只有仓库根:收到它
    [InlineData("--stay", DefaultRoot, 7u, 9)]                                                        // 恰好 1 个参数,是选项(--stay 的值缺失,由原来的松散写法管):默认根
    [InlineData("--client-version 5", DefaultRoot, 5u, 9)]                                            // 恰好 2 个参数,只有选项:默认根,版本生效
    [InlineData("SYNTH-GAP-GIVEN-ROOT --stay", GivenRoot, 7u, 9)]                                     // 恰好 2 个参数,根在前:收到根,设置不动
    [InlineData("SYNTH-GAP-GIVEN-ROOT --server-group 2", GivenRoot, 7u, 2)]                           // 3 个参数:根 + 一个选项
    [InlineData("--client-version 5 --server-group 2", DefaultRoot, 5u, 2)]                           // 4 个参数,只有选项:默认根,两个选项都生效
    [InlineData("SYNTH-GAP-GIVEN-ROOT --client-version 5 --server-group 2", GivenRoot, 5u, 2)]        // 5 个参数:根 + 两个选项
    [InlineData("--", DefaultRoot, 7u, 9)]                                                            // 恰为 "--" 也算选项开头,不是根
    [InlineData("-x", "-x", 7u, 9)]                                                                   // 只有一个横线不算选项开头,是根
    public void Prepare_hands_the_loader_the_given_root_only_when_the_first_argument_is_not_an_option(
        string line, string expectedRoot, uint expectedVersion, int expectedGroup)
    {
        var loader = new RecordingLoader();
        var lines = new List<string>();

        int code = OnlineOrchestratedSetup.Prepare(Args(line), DefaultRoot, loader.Load, lines.Add, out OnlineEntrySettings settings);

        Assert.Equal(0, code);
        Assert.Empty(lines);
        Assert.Equal(1, loader.Calls);
        Assert.Equal(expectedRoot, loader.RootSeen);
        Assert.Same(loader.Settings, settings);
        Assert.Equal(expectedVersion, settings.ClientVersion);
        Assert.Equal((ushort)expectedGroup, settings.ServerGroup);
    }

    /// <summary>
    /// 这条断言保护的行为:恰好一个参数、且它就是仓库根时,装配回调收到的字面量就是它(同一个字符串),不是默认值。
    /// 推导:个数 1 → 取第 0 个参数;不以 <c>--</c> 开头 → 就是仓库根。
    /// </summary>
    [Fact]
    public void Prepare_with_exactly_one_argument_that_is_a_repo_root_passes_that_very_string_to_the_loader()
    {
        var loader = new RecordingLoader();
        string[] args = { "SYNTH-GAP-GIVEN-ROOT" };

        int code = OnlineOrchestratedSetup.Prepare(args, DefaultRoot, loader.Load, _ => { }, out _);

        Assert.Equal(0, code);
        Assert.Equal("SYNTH-GAP-GIVEN-ROOT", loader.RootSeen);
        Assert.NotEqual(DefaultRoot, loader.RootSeen);
    }

    /// <summary>
    /// 这条断言保护的行为:参数列表本身为 null 时当作没有参数——装配回调收到默认仓库根,不抛异常。
    /// 推导:<c>args != null</c> 为假 → 仓库根取默认值;选项解析把 null 当作 0 个参数。
    /// </summary>
    [Fact]
    public void Prepare_treats_a_null_argument_list_like_no_arguments()
    {
        var loader = new RecordingLoader();

        int code = OnlineOrchestratedSetup.Prepare(null, DefaultRoot, loader.Load, _ => { }, out OnlineEntrySettings settings);

        Assert.Equal(0, code);
        Assert.Equal(DefaultRoot, loader.RootSeen);
        Assert.Equal(7u, settings.ClientVersion);
        Assert.Equal((ushort)9, settings.ServerGroup);
    }

    /// <summary>
    /// 这条断言保护的行为:第一个参数是 null 时不当仓库根(也不抛异常),装配回调收到默认仓库根。
    /// 推导:<c>args[0] != null</c> 为假 → 仓库根取默认值。
    /// </summary>
    [Fact]
    public void Prepare_does_not_take_a_null_first_argument_as_the_repo_root()
    {
        var loader = new RecordingLoader();
        string[] args = { null };

        int code = OnlineOrchestratedSetup.Prepare(args, DefaultRoot, loader.Load, _ => { }, out _);

        Assert.Equal(0, code);
        Assert.Equal(DefaultRoot, loader.RootSeen);
    }

    /// <summary>
    /// 这条断言保护的行为:仓库根在第一个位置时,即使后面跟了不合规的选项(用法错误),也在碰装配回调之前就返回 64,
    /// 回调一次都没被调用(与"只有仓库根"的正常路径是同一个参数个数边界的另一侧)。
    /// 推导:<c>--server-group 18</c> 超过上限 17 → 用法错误 → 打用法行并返回 64。
    /// </summary>
    [Fact]
    public void Prepare_with_a_repo_root_and_a_bad_option_exits_64_before_touching_the_loader()
    {
        var loader = new RecordingLoader();
        var lines = new List<string>();

        int code = OnlineOrchestratedSetup.Prepare(Args("SYNTH-GAP-GIVEN-ROOT --server-group 18"), DefaultRoot, loader.Load, lines.Add, out OnlineEntrySettings settings);

        Assert.Equal(64, code);
        Assert.Equal(new[] { OnlineOrchestratedSetup.UsageLine }, lines);
        Assert.Equal(0, loader.Calls);
        Assert.Null(loader.RootSeen);
        Assert.Null(settings);
    }
}
