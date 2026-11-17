using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Play
{
    /// <summary>
    /// 频道的拥挤档位(选频道界面上的那个词):顺畅、良好、普通、拥挤、满载。档位怎么算是联网层的事(按负载与容量),这里只认结果;
    /// <see cref="Full"/> 的频道进不去。
    /// </summary>
    public enum ChannelCongestion
    {
        Smooth = 0,
        Good = 1,
        Normal = 2,
        Crowded = 3,
        Full = 4,
    }

    /// <summary>
    /// 目录里的一个频道(与联网层的类型无关的中性描述,由客户端层的适配器从联网层的快照翻过来;选频道界面只认它)。
    /// <see cref="Target"/> 为 null 表示这个频道没有可用的端口,连不了。不可变。
    /// </summary>
    public sealed class DirectoryChannel
    {
        /// <param name="number">显示序号(界面上 "ch" 后面的数字);不能是负数。</param>
        /// <param name="title">去掉数字的频道名(可能是中文;没有字体时界面只画序号);可为 null。</param>
        /// <param name="congestion">拥挤档位。</param>
        /// <param name="target">要连的地址;没有可用端口时为 null。</param>
        /// <param name="recommendedMinLevel">推荐最低等级;没有推荐等级时 0。</param>
        /// <param name="recommendedMaxLevel">推荐最高等级;没有推荐等级时 0。</param>
        public DirectoryChannel(int number, string title, ChannelCongestion congestion, ChannelTarget target,
            int recommendedMinLevel = 0, int recommendedMaxLevel = 0)
        {
            if (number < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(number));
            }
            Number = number;
            Title = title ?? string.Empty;
            Congestion = congestion;
            Target = target;
            RecommendedMinLevel = recommendedMinLevel;
            RecommendedMaxLevel = recommendedMaxLevel;
        }

        public int Number { get; }

        public string Title { get; }

        public ChannelCongestion Congestion { get; }

        public ChannelTarget Target { get; }

        public int RecommendedMinLevel { get; }

        public int RecommendedMaxLevel { get; }

        /// <summary>有推荐等级(两头都为正且最高不低于最低);没有时界面不画第二行。</summary>
        public bool HasRecommendedLevel => RecommendedMinLevel > 0 && RecommendedMaxLevel >= RecommendedMinLevel;

        public bool IsFull => Congestion == ChannelCongestion.Full;

        /// <summary>能进:有可用端口、没满载。</summary>
        public bool CanEnter => Target != null && Congestion != ChannelCongestion.Full;
    }

    /// <summary>目录里的一个服务器(一组频道)。不可变。</summary>
    public sealed class DirectoryServer
    {
        /// <param name="name">服务器名(目录里的组标签)。</param>
        /// <param name="channels">这个服务器的频道,按目录给的顺序;可为 null(当作没有)。</param>
        /// <param name="hasEventMark">要不要在入口上挂活动角标(原版的来源没有读到,默认不挂,见 NOTES)。</param>
        public DirectoryServer(string name, IReadOnlyList<DirectoryChannel> channels, bool hasEventMark = false)
        {
            Name = name ?? string.Empty;
            Channels = channels ?? Array.Empty<DirectoryChannel>();
            HasEventMark = hasEventMark;
        }

        public string Name { get; }

        public IReadOnlyList<DirectoryChannel> Channels { get; }

        public bool HasEventMark { get; }

        /// <summary>能选:至少有一个频道(频道全满的服务器也能选,进去看到的是一排"满载")。</summary>
        public bool IsSelectable => Channels.Count > 0;

        /// <summary>有没有进得去的频道。</summary>
        public bool HasEnterableChannel
        {
            get
            {
                for (int i = 0; i < Channels.Count; i++)
                {
                    if (Channels[i].CanEnter)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }

    /// <summary>频道目录:所有服务器(顺序就是目录给的顺序)。不可变。</summary>
    public sealed class ServerDirectory
    {
        public static readonly ServerDirectory Empty = new ServerDirectory(null);

        public ServerDirectory(IReadOnlyList<DirectoryServer> servers)
        {
            Servers = servers ?? Array.Empty<DirectoryServer>();
        }

        public IReadOnlyList<DirectoryServer> Servers { get; }
    }

    /// <summary>读目录的结果:成功带目录(可以是空的);失败带提示类别(只有类别,不带地址或原始内容)。</summary>
    public sealed class ServerDirectoryResult
    {
        private ServerDirectoryResult(bool success, ServerDirectory directory, OnlineNoticeKind failure)
        {
            Success = success;
            Directory = directory;
            Failure = failure;
        }

        public bool Success { get; }

        /// <summary>成功时的目录(不为 null);失败时为 null。</summary>
        public ServerDirectory Directory { get; }

        /// <summary>失败时的提示类别;成功时 <see cref="OnlineNoticeKind.None"/>。</summary>
        public OnlineNoticeKind Failure { get; }

        public static ServerDirectoryResult Ok(ServerDirectory directory)
        {
            return new ServerDirectoryResult(true, directory ?? ServerDirectory.Empty, OnlineNoticeKind.None);
        }

        public static ServerDirectoryResult Fail(OnlineNoticeKind failure)
        {
            return new ServerDirectoryResult(false, null, failure == OnlineNoticeKind.None ? OnlineNoticeKind.Unexpected : failure);
        }
    }

    /// <summary>
    /// 取频道目录的小接口(可选注入,<see cref="OnlineShellOptions.ChannelDirectory"/>):登录成功之后调用。
    /// 任务要在后台线程上跑、立刻返回(阶段机在渲染线程上调它,之后只轮询任务有没有完成);抛异常按网络错误处理。
    /// 不在 <see cref="IOnlineBackend"/> 里:那个接口的成员签名不动。
    /// </summary>
    public interface IChannelDirectoryBackend
    {
        Task<ServerDirectoryResult> LoadAsync(CancellationToken cancellationToken);
    }
}
