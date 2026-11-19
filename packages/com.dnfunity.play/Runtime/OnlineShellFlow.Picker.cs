using System;
using System.Threading.Tasks;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网外壳阶段机里"选服务器、选频道真界面"的那一部分(<see cref="OnlineShellFlow"/> 的 partial)。
    /// <para>
    /// 可选注入:<see cref="OnlineShellOptions.ChannelDirectory"/> 为 null(默认)时这个文件里的代码一行也不会走到,两个阶段还是原来的占位,行为与没有它之前完全一样;
    /// 注入之后(要求 <see cref="OnlineShellOptions.AutoPassPlaceholders"/> 为 false)选服务器阶段的画面是 <see cref="ServerSelectScreen"/>、
    /// 选频道阶段的画面是 <see cref="ChannelSelectScreen"/>,键盘与鼠标交给它们;它们只攒意图,这里在 <see cref="OnlineShellFlow.Poll"/> 里把意图翻成既有的阶段转移:
    /// "游戏开始"(选服务器)= <see cref="OnlineShellFlow.ChooseServer"/>,"游戏开始"(选频道)= <see cref="OnlineShellFlow.ChooseChannel"/>,
    /// Esc 与"变更伺服器"走原有的返回(选频道 → 选服务器 → 登录),没有新增任何阶段转移。
    /// </para>
    /// <para>
    /// 目录在登录成功、第一次进入选服务器时读(<see cref="OpKind.Directory"/>,与别的网络操作一样是唯一的在途操作,可取消、会超时),整个会话只读一次;
    /// 选频道里的"刷新"重读它(保留着旧列表,读失败也不丢)。读不到目录时,两张界面都给"重试"与"默认频道"两个出口(后者用 <see cref="OnlineShellOptions.DefaultChannelPort"/> 直连)。
    /// 回到登录时目录与选中的服务器一并丢掉(下一次登录是全新的会话)。
    /// </para>
    /// </summary>
    public sealed partial class OnlineShellFlow
    {
        private IChannelDirectoryBackend _directoryBackend;
        private ServerPickerScene _pickerScene;
        private ServerSelectScreen _serverScreen;
        private ChannelSelectScreen _channelScreen;
        private ServerDirectory _directory;
        private DirectoryServer _pickedServer;
        private bool _directoryRefresh;       // 在途的目录读取是"刷新"(界面上有旧列表在显示)

        /// <summary>选服务器真界面;没注入真界面(<see cref="OnlineShellOptions.ChannelDirectory"/> 为 null)时为 null。</summary>
        public ServerSelectScreen ServerScreen => _serverScreen;

        /// <summary>选频道真界面;没注入真界面时为 null。</summary>
        public ChannelSelectScreen ChannelScreen => _channelScreen;

        /// <summary>这个会话里读到的频道目录;还没读到(或回到登录之后)为 null。</summary>
        public ServerDirectory Directory => _directory;

        /// <summary>选定的服务器(选频道显示的就是它的频道);还没选为 null。</summary>
        public DirectoryServer PickedServer => _pickedServer;

        /// <summary>鼠标指针在 800×600 画布坐标 (<paramref name="x"/>, <paramref name="y"/>) 上(只影响选服务器与选频道界面悬停的画法;其它阶段没有反应)。</summary>
        public void Hover(int x, int y)
        {
            if (_shutdown || _directoryBackend == null)
            {
                return;
            }
            if (Stage == OnlineShellStage.ServerSelect)
            {
                _serverScreen.Hover(x, y);
            }
            else if (Stage == OnlineShellStage.ChannelSelect)
            {
                _channelScreen.Hover(x, y);
            }
        }

        /// <summary>鼠标指针不在画面上了。</summary>
        public void HoverNone()
        {
            if (_shutdown || _directoryBackend == null)
            {
                return;
            }
            _serverScreen.HoverNone();
            _channelScreen.HoverNone();
        }

        // ------------------------------------------------------------------ 构造与重置

        private void PickerInit()
        {
            _directoryBackend = _options.ChannelDirectory;
            if (_directoryBackend == null)
            {
                return;
            }
            if (_options.AutoPassPlaceholders)
            {
                throw new ArgumentException("选服务器、选频道真界面要停在那两个阶段等玩家选,AutoPassPlaceholders 必须是 false", "options");
            }
            CharacterSelectScene art = _options.Art;
            _pickerScene = _options.PickerScene ?? (art == null ? null : new ServerPickerScene(art.Sprites, art.Beams));
            BuildPickerScreens();
        }

        private void BuildPickerScreens()
        {
            _serverScreen = new ServerSelectScreen(_pickerScene, _options.Fonts);
            _channelScreen = new ChannelSelectScreen(_pickerScene, _options.Fonts);
        }

        /// <summary>回到登录:目录、选中的服务器、两张界面的状态一并丢掉。</summary>
        private void PickerReset()
        {
            if (_directoryBackend == null)
            {
                return;
            }
            _directory = null;
            _pickedServer = null;
            _directoryRefresh = false;
            BuildPickerScreens();
        }

        // ------------------------------------------------------------------ 阶段进入与同步

        /// <summary>进入阶段之后(<see cref="OnlineShellFlow.EnterStage"/> 里):这两个阶段的画面换成真界面,并让它显示该显示的内容。</summary>
        private void PickerEnter(OnlineShellStage stage)
        {
            if (_directoryBackend == null)
            {
                return;
            }
            if (stage == OnlineShellStage.ServerSelect)
            {
                _scene = _serverScreen;
                if (_directory != null)
                {
                    _serverScreen.SetDirectory(_directory, IndexOfPicked(_directory));
                }
                else if (_op == null)
                {
                    StartDirectory(false);
                }
            }
            else if (stage == OnlineShellStage.ChannelSelect)
            {
                _scene = _channelScreen;
                ShowChannels(_channelTarget);
            }
        }

        /// <summary>
        /// 选频道界面显示选定服务器的频道;没有选定的服务器时:读到了目录就按当前连着的频道找所在的服务器、再不行取第一个能选的;
        /// 目录是空的显示"没有频道";没读到目录显示"读失败"(出口是重试与默认频道)。
        /// </summary>
        private void ShowChannels(ChannelTarget prefer)
        {
            if (_directory == null)
            {
                _channelScreen.SetFailed();
                return;
            }
            if (_pickedServer == null)
            {
                _pickedServer = FindContaining(_directory, prefer) ?? FirstSelectable(_directory);
            }
            _channelScreen.SetServer(_pickedServer ?? new DirectoryServer(string.Empty, null), prefer);
        }

        /// <summary>每帧:选频道界面的忙状态跟着在途操作走(连接中、刷新中)。</summary>
        private void PickerSync()
        {
            if (_directoryBackend == null)
            {
                return;
            }
            ChannelBusy busy = ChannelBusy.None;
            if (_op != null)
            {
                if (_op.Kind == OpKind.Connect)
                {
                    busy = ChannelBusy.Connecting;
                }
                else if (_op.Kind == OpKind.Directory && _directoryRefresh)
                {
                    busy = ChannelBusy.Refreshing;
                }
            }
            if (_channelScreen.Busy != busy)
            {
                _channelScreen.SetBusy(busy);
            }
        }

        private void PickerNotice(OnlineNotice notice)
        {
            if (_directoryBackend == null)
            {
                return;
            }
            _serverScreen.SetNotice(notice);
            _channelScreen.SetNotice(notice);
        }

        private void PickerAdvance(double deltaMs)
        {
            if (_directoryBackend == null)
            {
                return;
            }
            if (Stage == OnlineShellStage.ServerSelect)
            {
                _serverScreen.Advance(deltaMs);
            }
            else if (Stage == OnlineShellStage.ChannelSelect)
            {
                _channelScreen.Advance(deltaMs);
            }
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>真界面开着时选服务器、选频道两个阶段的按键;返回 true 表示按键已处理(Esc 不在其中:它仍走原有的返回)。</summary>
        private bool PickerKey(OnlineShellKey key)
        {
            if (_directoryBackend == null)
            {
                return false;
            }
            if (Stage == OnlineShellStage.ServerSelect)
            {
                switch (key)
                {
                    case OnlineShellKey.Left: _serverScreen.Move(-1, 0); return true;
                    case OnlineShellKey.Right: _serverScreen.Move(1, 0); return true;
                    case OnlineShellKey.Up: _serverScreen.Move(0, -1); return true;
                    case OnlineShellKey.Down: _serverScreen.Move(0, 1); return true;
                    case OnlineShellKey.Enter: _serverScreen.Confirm(); return true;
                    case OnlineShellKey.Space:
                        if (_serverScreen.Phase == PickerPhase.Failed || _serverScreen.Phase == PickerPhase.Empty)
                        {
                            _serverScreen.DirectConnect();
                        }
                        else
                        {
                            _serverScreen.Confirm();
                        }
                        return true;
                    case OnlineShellKey.Tab:
                    case OnlineShellKey.ShiftTab: return true;
                    default: return false;
                }
            }
            if (Stage == OnlineShellStage.ChannelSelect)
            {
                switch (key)
                {
                    case OnlineShellKey.Left: _channelScreen.Move(-1, 0); return true;
                    case OnlineShellKey.Right: _channelScreen.Move(1, 0); return true;
                    case OnlineShellKey.Up: _channelScreen.Move(0, -1); return true;
                    case OnlineShellKey.Down: _channelScreen.Move(0, 1); return true;
                    case OnlineShellKey.Enter: _channelScreen.Confirm(); return true;
                    case OnlineShellKey.Space:
                        if (_channelScreen.Phase == PickerPhase.Failed || _channelScreen.Phase == PickerPhase.Empty)
                        {
                            _channelScreen.DirectConnect();
                        }
                        else
                        {
                            _channelScreen.Confirm();
                        }
                        return true;
                    case OnlineShellKey.Tab: _channelScreen.NextPage(); return true;
                    case OnlineShellKey.ShiftTab: _channelScreen.PrevPage(); return true;
                    default: return false;
                }
            }
            return false;
        }

        private void PickerClick(int x, int y)
        {
            if (Stage == OnlineShellStage.ServerSelect)
            {
                _serverScreen.Click(x, y);
            }
            else if (Stage == OnlineShellStage.ChannelSelect)
            {
                _channelScreen.Click(x, y);
            }
        }

        /// <summary>真界面开着:选服务器、选频道两个阶段是不是由它们接管。</summary>
        private bool PickerOwns(OnlineShellStage stage)
        {
            return _directoryBackend != null && (stage == OnlineShellStage.ServerSelect || stage == OnlineShellStage.ChannelSelect);
        }

        // ------------------------------------------------------------------ 意图

        /// <summary>
        /// <see cref="OnlineShellFlow.ProcessIntents"/> 开头调:把界面攒下的事翻成既有的意图(返回、选定服务器、选定频道),
        /// 其余几件只有真界面才有的事(重读目录、直连默认频道、结束游戏)直接办。
        /// </summary>
        private void PickerIntents(ref bool back, ref bool server, ref ChannelTarget chosen)
        {
            if (_directoryBackend == null)
            {
                return;
            }
            if (Stage == OnlineShellStage.ServerSelect)
            {
                switch (_serverScreen.TakeAction())
                {
                    case ServerSelectAction.Choose:
                        server = true;
                        break;
                    case ServerSelectAction.Back:
                        back = true;
                        break;
                    case ServerSelectAction.Retry:
                        if (_op == null)
                        {
                            StartDirectory(false);
                        }
                        break;
                    case ServerSelectAction.DirectConnect:
                        if (_op == null)
                        {
                            StartDirectConnect();
                        }
                        break;
                }
                if (server)
                {
                    // 外部调 ChooseServer() 也只在界面真有可选的服务器时算数(读目录期间、读失败时不放行)
                    if (_serverScreen.CanStart && Stage == OnlineShellStage.ServerSelect)
                    {
                        _pickedServer = _serverScreen.SelectedServer;
                    }
                    else
                    {
                        server = false;
                    }
                }
            }
            else if (Stage == OnlineShellStage.ChannelSelect)
            {
                switch (_channelScreen.TakeAction(out ChannelTarget target))
                {
                    case ChannelSelectAction.Start:
                        if (chosen == null)
                        {
                            chosen = target;
                        }
                        break;
                    case ChannelSelectAction.Back:
                        back = true;
                        break;
                    case ChannelSelectAction.Quit:
                        PickerQuit();
                        back = false;
                        server = false;
                        chosen = null;
                        return;
                    case ChannelSelectAction.Refresh:
                        if (_op == null)
                        {
                            StartDirectory(_channelScreen.Phase == PickerPhase.Ready);
                        }
                        break;
                    case ChannelSelectAction.DirectConnect:
                        if (_op == null)
                        {
                            StartConnect(_defaultChannel);
                        }
                        break;
                }
                if (back && _op != null && _op.Kind == OpKind.Directory)
                {
                    // 刷新途中按返回:只取消这次读取(连着的频道不受影响),留在选频道
                    back = false;
                    DropOperation();
                    DirectoryCancelled();
                }
            }
        }

        /// <summary>结束游戏:放弃在途的操作、断开连接、请宿主退出。</summary>
        private void PickerQuit()
        {
            Log(Stage, "ok", "quit");
            DropOperation();
            _hasLink = false;
            _backend.Disconnect();
            QuitRequested = true;
        }

        // ------------------------------------------------------------------ 读目录

        /// <summary>
        /// 开始读目录。<paramref name="refresh"/> 为 true 是在选频道里刷新(界面保留着旧列表,忙状态显示"刷新中");否则界面显示"正在读"。
        /// 必须没有别的在途操作。
        /// </summary>
        private void StartDirectory(bool refresh)
        {
            SetNotice(null);
            _directoryRefresh = refresh;
            if (!refresh)
            {
                if (Stage == OnlineShellStage.ServerSelect)
                {
                    _serverScreen.SetLoading();
                }
                else if (Stage == OnlineShellStage.ChannelSelect)
                {
                    _channelScreen.SetLoading();
                }
            }
            IChannelDirectoryBackend backend = _directoryBackend;
            _op = Begin(OpKind.Directory, ct => backend.LoadAsync(ct));
            PickerSync();
        }

        /// <summary>读不到目录时的出口:不选了,直接连默认频道(进选频道阶段,界面显示"正在连接")。</summary>
        private void StartDirectConnect()
        {
            Log(Stage, "ok", "direct");
            EnterStage(OnlineShellStage.ChannelSelect, "direct");
            StartConnect(_defaultChannel);
        }

        private void FinishDirectory(Operation op, Exception error)
        {
            if (error != null && IsConnectionError(error))
            {
                GoToLogin(new OnlineNotice(OnlineNoticeKind.Disconnected), "disconnected");
                return;
            }
            OnlineNotice failure = null;
            ServerDirectory directory = null;
            if (error != null)
            {
                failure = NoticeForError(error);
            }
            else
            {
                ServerDirectoryResult result = ((System.Threading.Tasks.Task<ServerDirectoryResult>)op.Task).Result;
                if (result == null)
                {
                    failure = new OnlineNotice(OnlineNoticeKind.Unexpected);
                }
                else if (!result.Success)
                {
                    failure = new OnlineNotice(result.Failure);
                }
                else
                {
                    directory = result.Directory ?? ServerDirectory.Empty;
                }
            }
            if (failure != null && failure.Kind == OnlineNoticeKind.Disconnected)
            {
                GoToLogin(failure, "disconnected");
                return;
            }
            if (failure != null)
            {
                DirectoryFailed(failure);
                return;
            }
            DirectoryLoaded(directory);
        }

        private void DirectoryLoaded(ServerDirectory directory)
        {
            bool refresh = _directoryRefresh;
            _directoryRefresh = false;
            _directory = directory;
            Log(Stage, "ok", refresh ? "refresh" : "directory");
            SetNotice(null);
            if (Stage == OnlineShellStage.ServerSelect)
            {
                _serverScreen.SetDirectory(directory, IndexOfPicked(directory));
            }
            else if (Stage == OnlineShellStage.ChannelSelect)
            {
                ChannelTarget prefer = refresh && _channelScreen.SelectedChannel != null ? _channelScreen.SelectedChannel.Target : _channelTarget;
                if (_pickedServer != null)
                {
                    // 按名字找回选定的服务器;它从新目录里没了就显示成没有频道(出口是重试与默认频道)
                    _pickedServer = FindByName(directory, _pickedServer.Name) ?? new DirectoryServer(_pickedServer.Name, null);
                }
                ShowChannels(prefer);
            }
        }

        /// <summary>读目录失败:提示原因。刷新失败时保留旧列表(留在 Ready),第一次读失败时界面显示"读失败"与两个出口。</summary>
        private void DirectoryFailed(OnlineNotice failure)
        {
            bool refresh = _directoryRefresh;
            _directoryRefresh = false;
            Log(Stage, "fail", failure.Reason);
            SetNotice(failure);
            if (Stage == OnlineShellStage.ServerSelect)
            {
                _serverScreen.SetFailed();
            }
            else if (Stage == OnlineShellStage.ChannelSelect && !refresh)
            {
                _channelScreen.SetFailed();
            }
        }

        /// <summary>刷新被取消:留着旧列表,提示"已取消"。</summary>
        private void DirectoryCancelled()
        {
            bool refresh = _directoryRefresh;
            _directoryRefresh = false;
            Log(Stage, "fail", OnlineNotices.Code(OnlineNoticeKind.Cancelled));
            SetNotice(new OnlineNotice(OnlineNoticeKind.Cancelled));
            if (!refresh && Stage == OnlineShellStage.ChannelSelect)
            {
                _channelScreen.SetFailed();
            }
        }

        // ------------------------------------------------------------------ 目录里找服务器

        private static DirectoryServer FindByName(ServerDirectory directory, string name)
        {
            foreach (DirectoryServer server in directory.Servers)
            {
                if (string.Equals(server.Name, name, StringComparison.Ordinal))
                {
                    return server;
                }
            }
            return null;
        }

        private static DirectoryServer FindContaining(ServerDirectory directory, ChannelTarget target)
        {
            if (target == null)
            {
                return null;
            }
            foreach (DirectoryServer server in directory.Servers)
            {
                if (ContainsTarget(server.Channels, target) || ContainsTarget(server.SpecialChannels, target))
                {
                    return server;
                }
            }
            return null;
        }

        private static bool ContainsTarget(System.Collections.Generic.IReadOnlyList<DirectoryChannel> channels, ChannelTarget target)
        {
            for (int i = 0; i < channels.Count; i++)
            {
                if (target.Equals(channels[i].Target))
                {
                    return true;
                }
            }
            return false;
        }

        private static DirectoryServer FirstSelectable(ServerDirectory directory)
        {
            foreach (DirectoryServer server in directory.Servers)
            {
                if (server.IsSelectable)
                {
                    return server;
                }
            }
            return null;
        }

        /// <summary>选定的服务器在目录里的序号;还没选定(或找不到)是 0。</summary>
        private int IndexOfPicked(ServerDirectory directory)
        {
            if (_pickedServer == null)
            {
                return 0;
            }
            for (int i = 0; i < directory.Servers.Count; i++)
            {
                if (string.Equals(directory.Servers[i].Name, _pickedServer.Name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return 0;
        }
    }
}
