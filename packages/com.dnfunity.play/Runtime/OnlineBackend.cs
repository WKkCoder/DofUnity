using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Play
{
    /// <summary>
    /// 联网外壳依赖的网络操作(Play 层不引用 Net 程序集,真实现在 Client 层,测试用假的)。
    /// 约定:
    /// <list type="bullet">
    /// <item>所有方法返回 Task,实现不能在调用线程上阻塞(外壳在渲染线程上调用它们,之后只轮询任务是否完成);期望之内的失败用结果对象表达(带 <see cref="OnlineNoticeKind"/> 类别),
    /// 只有"连接断了"抛 <see cref="System.IO.IOException"/>(外壳据此回到登录并提示),其它意外异常外壳按"意外错误"处理。</item>
    /// <item><see cref="Disconnect"/> 立刻放弃当前会话(连接、令牌都不再可用,在途的操作随之取消),不阻塞,重复调用无害;之后可以重新登录。</item>
    /// <item><see cref="Disconnected"/> 在连接已经建立之后掉线时触发,<b>可能在任何线程上</b>;外壳只用它置一个标志,真正的处理在渲染线程的轮询里。主动调 <see cref="Disconnect"/> 不触发它。</item>
    /// </list>
    /// </summary>
    public interface IOnlineBackend
    {
        /// <summary>登录:账号与口令换令牌并取游戏服地址(同一个会话里接着连频道)。</summary>
        Task<OnlineAuthOutcome> LoginAsync(string username, string password, CancellationToken cancellationToken);

        /// <summary>注册账号(不登录)。</summary>
        Task<OnlineAuthOutcome> RegisterAsync(string username, string password, CancellationToken cancellationToken);

        /// <summary>修改口令(不登录)。</summary>
        Task<OnlineAuthOutcome> ChangePasswordAsync(string username, string oldPassword, string newPassword, CancellationToken cancellationToken);

        /// <summary>登录之后连一个频道并取回角色列表。</summary>
        Task<OnlineConnectOutcome> ConnectChannelAsync(ChannelTarget target, CancellationToken cancellationToken);

        /// <summary>已经连着频道(或进过游戏)时换一个频道,同样取回角色列表。</summary>
        Task<OnlineConnectOutcome> SwitchChannelAsync(ChannelTarget target, CancellationToken cancellationToken);

        /// <summary>用角色列表里第 <paramref name="slot"/> 个角色进入游戏(槽位 = 它在列表里的位置)。</summary>
        Task<OnlineSelectOutcome> SelectCharacterAsync(int slot, CancellationToken cancellationToken);

        /// <summary>放弃当前会话(见接口说明)。</summary>
        void Disconnect();

        /// <summary>连接掉线(可能在任何线程上)。</summary>
        event Action Disconnected;
    }

    /// <summary>登录、注册、改密的结果:成功,或带类别的失败(限流时带建议等待的秒数)。</summary>
    public sealed class OnlineAuthOutcome
    {
        private OnlineAuthOutcome(bool success, OnlineNoticeKind failure, int retryAfterSeconds)
        {
            Success = success;
            Failure = failure;
            RetryAfterSeconds = retryAfterSeconds < 0 ? 0 : retryAfterSeconds;
        }

        public bool Success { get; }

        /// <summary>失败的类别;成功时是 <see cref="OnlineNoticeKind.None"/>。</summary>
        public OnlineNoticeKind Failure { get; }

        /// <summary>被限流时建议等待的秒数;其它情况是 0。</summary>
        public int RetryAfterSeconds { get; }

        public static OnlineAuthOutcome Ok()
        {
            return new OnlineAuthOutcome(true, OnlineNoticeKind.None, 0);
        }

        /// <summary><paramref name="failure"/> 传 <see cref="OnlineNoticeKind.None"/> 当作意外错误。</summary>
        public static OnlineAuthOutcome Fail(OnlineNoticeKind failure, int retryAfterSeconds = 0)
        {
            return new OnlineAuthOutcome(false, failure == OnlineNoticeKind.None ? OnlineNoticeKind.Unexpected : failure, retryAfterSeconds);
        }
    }

    /// <summary>连频道的结果:成功时带角色列表(可以是空列表),失败带类别。</summary>
    public sealed class OnlineConnectOutcome
    {
        private OnlineConnectOutcome(OnlineCharacterList characters, OnlineNoticeKind failure)
        {
            Characters = characters;
            Failure = failure;
        }

        public bool Success => Characters != null;

        /// <summary>成功时的角色列表;失败时是 null。</summary>
        public OnlineCharacterList Characters { get; }

        public OnlineNoticeKind Failure { get; }

        public static OnlineConnectOutcome Ok(OnlineCharacterList characters)
        {
            return characters == null
                ? new OnlineConnectOutcome(null, OnlineNoticeKind.CharacterListMissing)
                : new OnlineConnectOutcome(characters, OnlineNoticeKind.None);
        }

        public static OnlineConnectOutcome Fail(OnlineNoticeKind failure)
        {
            return new OnlineConnectOutcome(null, failure == OnlineNoticeKind.None ? OnlineNoticeKind.Unexpected : failure);
        }
    }

    /// <summary>选角的结果:进去了,或失败(失败时说明还能不能换一个槽位再试)。</summary>
    public sealed class OnlineSelectOutcome
    {
        private OnlineSelectOutcome(bool entered, OnlineNoticeKind failure, bool mayTryAnotherSlot)
        {
            Entered = entered;
            Failure = failure;
            MayTryAnotherSlot = mayTryAnotherSlot;
        }

        public bool Entered { get; }

        public OnlineNoticeKind Failure { get; }

        /// <summary>失败之后连接还好,可以换一个角色再试;为 false 表示连接已经不可用(外壳回到登录)。</summary>
        public bool MayTryAnotherSlot { get; }

        public static OnlineSelectOutcome Ok()
        {
            return new OnlineSelectOutcome(true, OnlineNoticeKind.None, false);
        }

        public static OnlineSelectOutcome Fail(OnlineNoticeKind failure, bool mayTryAnotherSlot)
        {
            return new OnlineSelectOutcome(false, failure == OnlineNoticeKind.None ? OnlineNoticeKind.Unexpected : failure, mayTryAnotherSlot);
        }
    }

    /// <summary>角色条目里的一条装备记录(服务端角色列表里的原样字段,中性命名,含义见 Net 层的同名解析器)。</summary>
    public sealed class OnlineCharacterEquip
    {
        public OnlineCharacterEquip(byte slot, uint itemId, uint blob, byte packed, uint clearAvatar, uint extra)
        {
            Slot = slot;
            ItemId = itemId;
            Blob = blob;
            Packed = packed;
            ClearAvatar = clearAvatar;
            Extra = extra;
        }

        public byte Slot { get; }
        public uint ItemId { get; }
        public uint Blob { get; }
        public byte Packed { get; }
        public uint ClearAvatar { get; }
        public uint Extra { get; }
    }

    /// <summary>
    /// 服务端角色列表里的一个角色:解析器给出的每个字段都在这里,一个不丢、一个不改(业务含义没读通的字段按读序中性命名),
    /// 供以后的选角界面按需取用。<see cref="NameBytes"/> 是名字的原始字节(不转字符串:日志与诊断输出里不要带它)。
    /// </summary>
    public sealed class OnlineCharacterEntry
    {
        public OnlineCharacterEntry(
            ushort uid, byte[] nameBytes, byte x1, byte job, byte growTypeByte, byte level, byte d, byte status,
            IEnumerable<OnlineCharacterEquip> equips, byte g, byte h, uint i, byte j, byte premium, uint k, byte a2, byte b2, byte c2, uint d2)
        {
            Uid = uid;
            NameBytes = Array.AsReadOnly((byte[])(nameBytes ?? Array.Empty<byte>()).Clone());
            X1 = x1;
            Job = job;
            GrowTypeByte = growTypeByte;
            Level = level;
            D = d;
            Status = status;
            Equips = new List<OnlineCharacterEquip>(equips ?? Array.Empty<OnlineCharacterEquip>()).AsReadOnly();
            G = g;
            H = h;
            I = i;
            J = j;
            Premium = premium;
            K = k;
            A2 = a2;
            B2 = b2;
            C2 = c2;
            D2 = d2;
        }

        public ushort Uid { get; }
        public IReadOnlyList<byte> NameBytes { get; }
        public byte X1 { get; }
        public byte Job { get; }
        public byte GrowTypeByte { get; }
        public byte Level { get; }
        public byte D { get; }
        public byte Status { get; }
        public IReadOnlyList<OnlineCharacterEquip> Equips { get; }
        public byte G { get; }
        public byte H { get; }
        public uint I { get; }
        public byte J { get; }
        public byte Premium { get; }
        public uint K { get; }
        public byte A2 { get; }
        public byte B2 { get; }
        public byte C2 { get; }
        public uint D2 { get; }
    }

    /// <summary>
    /// 服务端的角色列表(头部四个字段、全部条目与尾部字节数),原样、不截断地交给以后的选角界面。
    /// 外壳自己的占位选角画面最多画 <see cref="CharacterRoster.Capacity"/> 个,但这个列表里的条目一个不少。
    /// </summary>
    public sealed class OnlineCharacterList
    {
        public OnlineCharacterList(byte a, byte b, byte c, uint d, IEnumerable<OnlineCharacterEntry> entries, int trailingByteCount)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            Entries = new List<OnlineCharacterEntry>(entries ?? Array.Empty<OnlineCharacterEntry>()).AsReadOnly();
            TrailingByteCount = trailingByteCount;
        }

        public byte A { get; }
        public byte B { get; }
        public byte C { get; }
        public uint D { get; }
        public IReadOnlyList<OnlineCharacterEntry> Entries { get; }
        public int TrailingByteCount { get; }
    }
}
