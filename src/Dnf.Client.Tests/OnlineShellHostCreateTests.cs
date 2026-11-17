using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// 验收后补的宿主构造测试(变异存活项):<c>OnlineShellHost.Create</c> 不带"真界面"参数时缺省是关的——
/// 两个阶段是占位(自动放行)、没有目录来源;产品里 TownDemo 总是显式传,但缺省值也要钉住。
/// </summary>
public sealed class OnlineShellHostCreateTests
{
    [Fact]
    public void Create_without_the_server_ui_argument_builds_no_picker_screens_and_the_flag_turns_them_on()
    {
        var rig = new BackendRig();
        var setup = new OnlineShellSetup(rig.Template, OnlineShellConfigSource.ConfigFile, null, 31000, BackendRig.User, null);

        // 不带第四个参数:缺省是关的——没有真界面、也没有目录
        using (OnlineShellHost plain = OnlineShellHost.Create(setup, null, null))
        {
            Assert.Null(plain.Flow.ServerScreen);
            Assert.Null(plain.Flow.ChannelScreen);
            Assert.Null(plain.Flow.Directory);
            Assert.Equal(OnlineShellStage.Login, plain.Stage);
        }

        // 对照:显式打开,两张真界面都造出来
        using (OnlineShellHost real = OnlineShellHost.Create(setup, null, null, serverUi: true))
        {
            Assert.NotNull(real.Flow.ServerScreen);
            Assert.NotNull(real.Flow.ChannelScreen);
            Assert.Equal(OnlineShellStage.Login, real.Stage);
        }

        // 显式关掉与缺省一样
        using (OnlineShellHost off = OnlineShellHost.Create(setup, null, null, serverUi: false))
        {
            Assert.Null(off.Flow.ServerScreen);
            Assert.Null(off.Flow.ChannelScreen);
        }
    }
}
