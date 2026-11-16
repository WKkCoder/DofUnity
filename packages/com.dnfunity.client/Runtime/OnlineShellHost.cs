using System;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 一帧里读到的输入(宿主把引擎的键盘鼠标读成它:文字来自 <c>Input.inputString</c>,按键是这一帧刚按下的,点击是画布坐标)。
    /// 全是"刚发生的事",不带状态:外壳自己管焦点、选中格与提交。
    /// </summary>
    public struct OnlineShellFrameInput
    {
        /// <summary>这一帧键盘输入的文字(退格是 '\b',其它控制字符外壳会忽略);没有为 null。</summary>
        public string Text;

        public bool Enter;
        public bool Escape;
        public bool Tab;

        /// <summary>按 Tab 时 Shift 是不是按着(往回切焦点)。</summary>
        public bool Shift;

        public bool Up;
        public bool Down;
        public bool Left;
        public bool Right;
        public bool Space;

        /// <summary>鼠标左键(或触摸)这一帧按下。</summary>
        public bool Click;

        /// <summary>点击位置(800×600 画布坐标)。</summary>
        public int ClickX;

        public int ClickY;
    }

    /// <summary>把一帧输入交给外壳。顺序:先文字(同一帧里先打字再按回车是常见的),再按键,最后点击。</summary>
    public static class OnlineShellInput
    {
        public static void Apply(OnlineShellFlow flow, OnlineShellFrameInput input)
        {
            if (flow == null)
            {
                throw new ArgumentNullException(nameof(flow));
            }
            if (!string.IsNullOrEmpty(input.Text))
            {
                flow.Type(input.Text);
            }
            if (input.Tab)
            {
                flow.Key(input.Shift ? OnlineShellKey.ShiftTab : OnlineShellKey.Tab);
            }
            if (input.Enter)
            {
                flow.Key(OnlineShellKey.Enter);
            }
            if (input.Escape)
            {
                flow.Key(OnlineShellKey.Escape);
            }
            if (input.Up)
            {
                flow.Key(OnlineShellKey.Up);
            }
            if (input.Down)
            {
                flow.Key(OnlineShellKey.Down);
            }
            if (input.Left)
            {
                flow.Key(OnlineShellKey.Left);
            }
            if (input.Right)
            {
                flow.Key(OnlineShellKey.Right);
            }
            if (input.Space)
            {
                flow.Key(OnlineShellKey.Space);
            }
            if (input.Click)
            {
                flow.Click(input.ClickX, input.ClickY);
            }
        }
    }

    /// <summary>
    /// 联网外壳的宿主一侧(不碰引擎,TownDemo 的 <c>-dnfonline</c> 分支只做"读输入、每帧调 <see cref="Tick"/>、阶段变了换画面"):
    /// 把连接配置(<see cref="OnlineShellSetup"/>)、真后端(<see cref="OnlineShellBackend"/>)与阶段机(<see cref="OnlineShellFlow"/>)接在一起,
    /// 并拥有联网会话——进入游戏之后会话继续活着(<see cref="Session"/>),直到退出、换角色(<see cref="SwapCharacter"/>)、断线或回到登录时被放弃。
    /// </summary>
    public sealed class OnlineShellHost : IDisposable
    {
        private readonly OnlineShellFlow _flow;
        private readonly OnlineShellBackend _backend;
        private bool _disposed;

        /// <summary>造真实的宿主:后端用 <paramref name="setup"/> 的连接配置(没有配置时后端什么网络操作也不做)。</summary>
        public static OnlineShellHost Create(OnlineShellSetup setup, CharacterSelectScene art, Action<string> log)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }
            return new OnlineShellHost(setup, new OnlineShellBackend(setup.CreateSettings(), log), art, log, null);
        }

        internal OnlineShellHost(OnlineShellSetup setup, OnlineShellBackend backend, CharacterSelectScene art, Action<string> log, IGlyphFonts fonts)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            var options = new OnlineShellOptions
            {
                Configured = setup.Configured,
                ConfigDetail = setup.ConfigDetail,
                DefaultChannelPort = setup.DefaultChannelPort,
                Art = art,
                Log = log,
                InitialUsername = setup.PrefillUser,
                Fonts = fonts,
            };
            _flow = new OnlineShellFlow(backend, options);
            if (!string.IsNullOrEmpty(setup.PrefillPassword))
            {
                _flow.Login.Prefill(setup.PrefillUser ?? string.Empty, setup.PrefillPassword);
            }
        }

        public OnlineShellFlow Flow => _flow;

        public OnlineShellStage Stage => _flow.Stage;

        /// <summary>外壳已经把控制权交给游戏(<see cref="OnlineShellStage.InGame"/>)。</summary>
        public bool InGame => _flow.Stage == OnlineShellStage.InGame;

        /// <summary>玩家在选角界面点了"结束游戏"。</summary>
        public bool QuitRequested => _flow.QuitRequested;

        /// <summary>
        /// 当前的联网会话(登录之后到放弃之前);进入游戏之后它还活着,给以后的进副本线用(<c>EnterExclusiveReceiveAsync</c>)。没有会话时为 null。
        /// </summary>
        public OnlineClientSession Session => _backend.Session;

        /// <summary>每帧调一次(渲染线程):先把这一帧的输入交给外壳,再推动选角界面的动画,最后轮询阶段机。<paramref name="nowMs"/> 要单调递增。</summary>
        public void Tick(OnlineShellFrameInput input, double deltaMs, long nowMs)
        {
            if (_disposed)
            {
                return;
            }
            OnlineShellInput.Apply(_flow, input);
            _flow.Advance(deltaMs);
            _flow.Poll(nowMs);
        }

        /// <summary>扩展点:进入游戏之后换角色(回到选频道,重连、重新取角色列表)。</summary>
        public void SwapCharacter()
        {
            _flow.SwapCharacter();
        }

        /// <summary>退出:放弃会话(最多等一秒让后台释放做完)。可重复调用。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _flow.Shutdown();
            _backend.Dispose();
        }
    }
}
