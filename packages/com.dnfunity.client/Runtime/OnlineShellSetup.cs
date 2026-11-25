using System;
using Dnf.Net.Session;

namespace Dnf.Client
{
    /// <summary>联网外壳的连接配置从哪里来。</summary>
    public enum OnlineShellConfigSource
    {
        /// <summary>没有可用的配置(外壳显示"未配置连接",不发任何请求)。</summary>
        None = 0,

        /// <summary>正式分发:应用持久数据目录下的 <see cref="OnlineConnectionConfig.FileName"/>。</summary>
        ConfigFile,

        /// <summary>开发版:<c>-dnfnetroot</c> 指的仓库根里的本地凭据(与 <c>-dnfnet</c> 同源)。</summary>
        LocalDev,
    }

    /// <summary>
    /// 联网外壳的启动设置:连接配置(网关、密钥、鼠标口令密文、数据版本、默认频道端口)与登录界面的预填。
    /// 不可用时 <see cref="Configured"/> 为 false,<see cref="ConfigDetail"/> 只点名哪一项有问题(不带任何配置的值)。
    /// <see cref="ToString"/> 不输出任何内容(预填的口令只来自开发版本机凭据,但也不该有任何办法被无意打进日志)。
    /// </summary>
    public sealed class OnlineShellSetup
    {
        private readonly OnlineEntrySettings _settings;

        internal OnlineShellSetup(OnlineEntrySettings settings, OnlineShellConfigSource source, string configDetail,
            int defaultChannelPort, string prefillUser, string prefillPassword)
        {
            _settings = settings;
            Source = source;
            ConfigDetail = configDetail;
            DefaultChannelPort = defaultChannelPort;
            PrefillUser = prefillUser;
            PrefillPassword = prefillPassword;
        }

        /// <summary>配置可用。</summary>
        public bool Configured => _settings != null;

        public OnlineShellConfigSource Source { get; }

        /// <summary>不可用时,哪一项配置有问题的一句话(只点名配置项);可用时为 null。</summary>
        public string ConfigDetail { get; }

        /// <summary>默认频道端口(选服务器与选频道是占位时用它连接)。</summary>
        public int DefaultChannelPort { get; }

        /// <summary>登录界面预填的账号;没有为 null。</summary>
        public string PrefillUser { get; }

        /// <summary>登录界面预填的口令:只有开发版读了本机凭据、且账号没被命令行改掉时才有;其它情况为 null(口令只靠手输)。</summary>
        public string PrefillPassword { get; }

        /// <summary>联网会话要的入参模板(账号与口令留空);每次返回新对象,鼠标口令密文是副本;不可用时为 null。</summary>
        public OnlineEntrySettings CreateSettings()
        {
            if (_settings == null)
            {
                return null;
            }
            return _settings.CloneWithoutCredentials();
        }

        public override string ToString()
        {
            return "OnlineShellSetup";
        }
    }

    /// <summary>
    /// 读联网外壳的启动设置(纯函数加两个文件读取,不碰引擎)。来源按这个顺序定,不混用:
    /// <list type="number">
    /// <item>开发版(<c>devBuild</c>)且给了 <c>-dnfnetroot</c>(<paramref name="devRepoRoot"/>):读那个仓库根的本地凭据(<see cref="LocalDevEntrySettings"/>,与 <c>-dnfnet</c> 同源)。
    /// 读不出来就是"未配置",不退回配置文件(开发者要看到是哪一项缺了)。网关、鼠标口令密文、数据版本来自它;默认频道端口取 <c>-dnfnetport</c>(给了的话),否则 30011;
    /// 登录界面预填它的账号与口令(<c>-dnfonlineuser</c> 给了别的账号时只预填那个账号,口令留空手输)。</item>
    /// <item>其余情况(正式分发,或开发版没给 <c>-dnfnetroot</c>):读应用持久数据目录下的 <see cref="OnlineConnectionConfig.FileName"/>(<see cref="OnlineConnectionConfig"/>)。
    /// 口令从不预填;账号预填 <c>-dnfonlineuser</c>,没有就用上次登录成功记下的账号。</item>
    /// </list>
    /// 正式分发(非开发版)忽略 <c>-dnfnetroot</c>:本机凭据只给开发版用。
    /// </summary>
    public static class OnlineShellSetupLoader
    {
        /// <param name="devBuild">是开发版(编辑器或开发构建)。</param>
        /// <param name="devRepoRoot">-dnfnetroot 的值;没给为 null。</param>
        /// <param name="devChannelPort">-dnfnetport 的值;没给传 0 或负数。</param>
        /// <param name="persistentDataPath">应用持久数据目录(配置文件放在它下面)。</param>
        /// <param name="userArgument">-dnfonlineuser 的值;没给为 null。</param>
        /// <param name="rememberedUser">上次登录成功记下的账号;没有为 null。</param>
        public static OnlineShellSetup Load(bool devBuild, string devRepoRoot, int devChannelPort, string persistentDataPath, string userArgument, string rememberedUser)
        {
            string user = string.IsNullOrEmpty(userArgument) ? null : userArgument;
            if (devBuild && !string.IsNullOrEmpty(devRepoRoot))
            {
                return LoadLocalDev(devRepoRoot, devChannelPort, user, rememberedUser);
            }
            return LoadConfigFile(persistentDataPath, user ?? NullIfEmpty(rememberedUser));
        }

        private static OnlineShellSetup LoadLocalDev(string repoRoot, int devChannelPort, string user, string rememberedUser)
        {
            if (!LocalDevEntrySettings.TryLoad(repoRoot, out OnlineEntrySettings settings, out string problem))
            {
                return new OnlineShellSetup(null, OnlineShellConfigSource.None, problem, OnlineConnectionConfig.DefaultChannelPort,
                    user ?? NullIfEmpty(rememberedUser), null);
            }
            int port = devChannelPort >= 1 && devChannelPort <= 65535 ? devChannelPort : settings.ChannelPort;
            string account = NullIfEmpty(settings.Account);
            string prefillUser = user ?? account;
            string prefillPassword = user == null || string.Equals(user, account, StringComparison.Ordinal)
                ? NullIfEmpty(settings.Password)
                : null;
            // 账号与口令只用来预填登录界面;会话的入参模板里不留(登录时用界面里输入的)
            settings.Account = string.Empty;
            settings.Password = string.Empty;
            settings.ChannelPort = port;
            return new OnlineShellSetup(settings, OnlineShellConfigSource.LocalDev, null, port, prefillUser, prefillPassword);
        }

        private static OnlineShellSetup LoadConfigFile(string persistentDataPath, string prefillUser)
        {
            OnlineConnectionConfig config = null;
            OnlineConnectionProblem problem = OnlineConnectionProblem.FileMissing;
            // 没有持久数据目录就当文件不存在(不拿相对路径去读进程当前目录里的同名文件)
            bool loaded = !string.IsNullOrEmpty(persistentDataPath)
                && OnlineConnectionConfig.TryLoad(OnlineConnectionConfig.PathUnder(persistentDataPath), out config, out problem);
            if (!loaded)
            {
                return new OnlineShellSetup(null, OnlineShellConfigSource.None, OnlineConnectionConfig.Describe(problem),
                    OnlineConnectionConfig.DefaultChannelPort, prefillUser, null);
            }
            return new OnlineShellSetup(config.ToSettings(), OnlineShellConfigSource.ConfigFile, null, config.ChannelPort, prefillUser, null);
        }

        private static string NullIfEmpty(string text)
        {
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>非开发构建强制关闭角色创建/删除写操作，仅 devBuild == true 且显式传 -dnfallowcharwrite 时开启。</summary>
        public static bool ResolveAllowCharWrite(bool devBuild, bool flagRequested)
        {
            return devBuild && flagRequested;
        }
    }
}
