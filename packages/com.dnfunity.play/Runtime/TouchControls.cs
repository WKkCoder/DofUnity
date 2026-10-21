using System;
using Dnf.Render;

namespace Dnf.Play
{
    // 触屏操作层(M13):纯逻辑,不依赖 Unity。
    // 这一层是本项目自己的设计,不是对原版的还原——原版客户端是键盘游戏,没有触屏界面。它的任务只有一个:
    // 把手指的按下、移动、抬起变成键盘路径本来就吃的那一组输入(方向 -1/0/1、刚按下的攻击/跳跃/技能键),
    // 这样主角、战斗与技能的规则一行不改,触屏与键盘走同一条路。
    // 所以凡是"原版怎样响应按键"的问题都留给原版的逻辑(TownHero 的双击奔跑、连招缓冲、技能冷却),这里只负责翻译手势。

    /// <summary>触屏上的一个圆形按钮。</summary>
    public enum TouchButton
    {
        /// <summary>普通攻击(键盘 X)。</summary>
        Attack = 0,

        /// <summary>跳跃(键盘 C)。</summary>
        Jump = 1,

        /// <summary>技能快捷槽 A。</summary>
        SkillA = 2,

        /// <summary>技能快捷槽 S。</summary>
        SkillS = 3,

        /// <summary>技能快捷槽 D。</summary>
        SkillD = 4,

        /// <summary>暂停/继续(键盘空格)。</summary>
        Pause = 5,
    }

    /// <summary>屏幕像素坐标(原点在左上、y 向下)里的一个圆。</summary>
    public readonly struct TouchDisc
    {
        public TouchDisc(double x, double y, double radius)
        {
            X = x;
            Y = y;
            Radius = radius;
        }

        public double X { get; }

        public double Y { get; }

        public double Radius { get; }

        /// <summary>点到圆心的距离,以半径为单位(1 = 正好在圆周上)。</summary>
        public double Normalized(double x, double y)
        {
            double dx = x - X, dy = y - Y;
            return Math.Sqrt(dx * dx + dy * dy) / Radius;
        }

        /// <summary>两个圆的间隙(像素,负数表示重叠)。</summary>
        public double GapTo(TouchDisc other)
        {
            double dx = other.X - X, dy = other.Y - Y;
            return Math.Sqrt(dx * dx + dy * dy) - Radius - other.Radius;
        }
    }

    /// <summary>屏幕像素坐标里的一个矩形。</summary>
    public readonly struct TouchRect
    {
        public TouchRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double X { get; }

        public double Y { get; }

        public double Width { get; }

        public double Height { get; }

        public double Right => X + Width;

        public double Bottom => Y + Height;

        public bool Contains(double x, double y)
        {
            return x >= X && x < X + Width && y >= Y && y < Y + Height;
        }
    }

    /// <summary>
    /// 触屏控件在屏幕上的摆法:左下是摇杆区,右下是攻击键,攻击键左边是跳跃键、上方一段弧上是 A S D 三个技能键,右上角是暂停键。
    /// 所有尺寸都按屏幕短边的 1%(<see cref="Unit"/>)缩放,所以手机、平板、桌面窗口里的比例一样;
    /// 控件贴着"安全区"摆(刘海、圆角、手势条之外),而不是贴着整块屏幕的边。
    /// 画面是 800×600 整数倍放大居中的,在宽屏手机上两侧有黑边:控件按屏幕摆而不是按画面摆,手指就不会盖住游戏画面。
    /// </summary>
    public sealed class TouchLayout
    {
        private readonly TouchDisc[] _buttons;

        private TouchLayout(int width, int height, double unit, TouchDisc[] buttons, TouchRect stickZone, double stickRadius, double idleX, double idleY)
        {
            Width = width;
            Height = height;
            Unit = unit;
            _buttons = buttons;
            StickZone = stickZone;
            StickRadius = stickRadius;
            StickIdleX = idleX;
            StickIdleY = idleY;
        }

        /// <summary>屏幕宽(像素)。</summary>
        public int Width { get; }

        /// <summary>屏幕高(像素)。</summary>
        public int Height { get; }

        /// <summary>短边的 1%(像素)。</summary>
        public double Unit { get; }

        /// <summary>摇杆区:手指在这里按下就开始一次摇杆操作(摇杆的原点就是按下的那一点)。</summary>
        public TouchRect StickZone { get; }

        /// <summary>摇杆半径(像素)。</summary>
        public double StickRadius { get; }

        /// <summary>摇杆没有被按着时画在哪里(在摇杆区里)。</summary>
        public double StickIdleX { get; }

        public double StickIdleY { get; }

        public TouchDisc Button(TouchButton button)
        {
            return _buttons[(int)button];
        }

        /// <summary>
        /// 按屏幕尺寸与安全区边距(像素)算出摆法。<paramref name="insetLeft"/> 等是安全区到屏幕四边的距离。
        /// 只为横屏设计(宽不小于高);竖屏窗口里控件也算得出来,但会挤在一起。
        /// </summary>
        public static TouchLayout For(int width, int height, double insetLeft = 0, double insetTop = 0, double insetRight = 0, double insetBottom = 0)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "屏幕尺寸必须是正数");
            }
            double u = Math.Min(width, height) / 100.0;
            double left = insetLeft, top = insetTop, right = width - insetRight, bottom = height - insetBottom;
            double usableWidth = Math.Max(1.0, right - left), usableHeight = Math.Max(1.0, bottom - top);

            double ax = right - 33 * u, ay = bottom - 22 * u;
            var buttons = new TouchDisc[6];
            buttons[(int)TouchButton.Attack] = new TouchDisc(ax, ay, 12.0 * u);
            buttons[(int)TouchButton.Jump] = new TouchDisc(ax - 24.5 * u, ay + 3.0 * u, 8.5 * u);
            buttons[(int)TouchButton.SkillA] = new TouchDisc(ax - 19.0 * u, ay - 20.0 * u, 7.5 * u);
            buttons[(int)TouchButton.SkillS] = new TouchDisc(ax + 2.0 * u, ay - 28.0 * u, 7.5 * u);
            buttons[(int)TouchButton.SkillD] = new TouchDisc(ax + 22.0 * u, ay - 17.0 * u, 7.5 * u);
            buttons[(int)TouchButton.Pause] = new TouchDisc(right - 7.0 * u, top + 7.0 * u, 4.5 * u);

            double zoneWidth = Math.Max(0.40 * usableWidth, 48.0 * u);
            double zoneTop = top + 0.30 * usableHeight;
            var zone = new TouchRect(left, zoneTop, zoneWidth, bottom - zoneTop);
            return new TouchLayout(width, height, u, buttons, zone, 14.0 * u, left + 24.0 * u, bottom - 26.0 * u);
        }
    }

    /// <summary>触屏层一帧的输出:与键盘路径同一组输入(攻击、跳跃、技能键都是"刚按下"的边沿,每次按下只给出一帧)。</summary>
    public readonly struct TouchFrame
    {
        public TouchFrame(int dirX, int dirY, bool attack, bool jump, SkillKeys skills)
        {
            DirX = dirX;
            DirY = dirY;
            Attack = attack;
            Jump = jump;
            Skills = skills;
        }

        /// <summary>水平方向:-1 左、0 无、1 右。</summary>
        public int DirX { get; }

        /// <summary>竖直方向:-1 上、0 无、1 下(与键盘路径一致:向上是 -1)。</summary>
        public int DirY { get; }

        public bool Attack { get; }

        public bool Jump { get; }

        public SkillKeys Skills { get; }
    }

    /// <summary>
    /// 触屏操作层的状态机:喂进手指的按下、移动、抬起(屏幕像素坐标、每根手指一个编号),每帧 <see cref="Poll"/> 取出这一帧的输入。
    ///
    /// 摇杆是浮动的:手指在摇杆区按下的那一点就是原点,往哪个方向拖就走哪个方向(八向,斜着走水平与竖直同时有输入),
    /// 拖得很近(死区)不动;拖得超过 <see cref="FollowLimit"/> 倍半径时原点跟着手指走,所以反向时不用拖回很远。
    /// 方向判定带滞回(进入与退出用不同的阈值),手指在扇区边界上抖动时方向不会来回跳。
    ///
    /// 奔跑:原版的奔跑只认"双击方向键"(<c>TownHero</c>,证据 E-072/E-073),触屏上没有"双击摇杆"这个自然手势,
    /// 所以把"把摇杆推到最外圈"翻译成一次双击:手指进入外圈(<see cref="RunEnter"/> 倍半径)时,在接下来几帧的输出里插进"松开、按下"的序列,
    /// 让 <c>TownHero</c> 看到的恰好是一次间隔很短的双击;退回内圈(<see cref="RunExit"/>)时输出一帧"全松开",奔跑就结束、接着走路。
    /// 奔跑是否真的开始仍由原版的逻辑决定(主角没有奔跑动作、正在挥击或跳跃时序列白放,没有副作用)。
    ///
    /// 按钮:手指按下的那一刻出一次"刚按下"(与键盘的 GetKeyDown 一样),按住不连发;判定半径比画出来的大 <see cref="ButtonSlop"/> 倍,手指比鼠标粗。
    /// 不在任何控件上的按下不被认领(<see cref="Down"/> 返回 false),交给宿主处理(例如点菜单)。
    /// </summary>
    public sealed class TouchControls
    {
        /// <summary>死区:离原点不到这么多倍半径时没有方向。</summary>
        public const double DeadZone = 0.30;

        /// <summary>进入奔跑区:离原点至少这么多倍半径。</summary>
        public const double RunEnter = 1.00;

        /// <summary>退出奔跑区:离原点不到这么多倍半径(比进入的小,是滞回)。</summary>
        public const double RunExit = 0.80;

        /// <summary>手指离原点超过这么多倍半径,原点就跟着手指走。</summary>
        public const double FollowLimit = 1.50;

        /// <summary>某个轴上的方向"进入":单位向量在这个轴上的分量超过它(约 25°,所以水平与竖直各占约 50° 的扇区,斜向约 40°)。</summary>
        public const double AxisEnter = 0.42;

        /// <summary>某个轴上的方向"退出":分量回落到它以下(比进入的小,是滞回)。</summary>
        public const double AxisExit = 0.32;

        /// <summary>按钮的判定半径是画出来的几倍。</summary>
        public const double ButtonSlop = 1.25;

        private const int NoTouch = int.MinValue;
        private const int ButtonCount = 6;

        private readonly int[] _buttonTouch = new int[ButtonCount];
        private readonly bool[] _buttonEdge = new bool[ButtonCount];
        private readonly int[] _sequence = new int[3];
        private int _sequenceLength;
        private int _sequencePosition;
        private int _sequenceDir;

        private int _stickTouch = NoTouch;
        private double _originX;
        private double _originY;
        private double _thumbX;
        private double _thumbY;
        private int _rawX;
        private int _rawY;
        private bool _runZone;
        private int _runDir;
        private bool _runEnded;
        private int _lastOutX;

        private int _insetsWidth;
        private int _insetsHeight;
        private double _insetLeft;
        private double _insetTop;
        private double _insetRight;
        private double _insetBottom;

        public TouchControls(int width, int height, double insetLeft = 0, double insetTop = 0, double insetRight = 0, double insetBottom = 0)
        {
            for (int i = 0; i < ButtonCount; i++)
            {
                _buttonTouch[i] = NoTouch;
            }
            Layout = TouchLayout.For(width, height, insetLeft, insetTop, insetRight, insetBottom);
            RememberInsets(width, height, insetLeft, insetTop, insetRight, insetBottom);
        }

        /// <summary>当前的摆法。</summary>
        public TouchLayout Layout { get; private set; }

        /// <summary>摇杆正被一根手指按着。</summary>
        public bool StickActive => _stickTouch != NoTouch;

        /// <summary>摇杆原点(画底圈的位置);没有被按着时是 <see cref="TouchLayout.StickIdleX"/>、<see cref="TouchLayout.StickIdleY"/>。</summary>
        public double StickOriginX => StickActive ? _originX : Layout.StickIdleX;

        public double StickOriginY => StickActive ? _originY : Layout.StickIdleY;

        /// <summary>摇杆拇指的位置(画拇指的位置,限制在原点的 <see cref="FollowLimit"/> 倍半径之内);没有被按着时在原点。</summary>
        public double StickThumbX => StickActive ? _thumbX : Layout.StickIdleX;

        public double StickThumbY => StickActive ? _thumbY : Layout.StickIdleY;

        /// <summary>摇杆处在奔跑区(画面上让外圈亮起来)。</summary>
        public bool InRunZone => StickActive && _runZone;

        /// <summary>这个按钮正被按着(画面上让它亮起来)。</summary>
        public bool IsPressed(TouchButton button)
        {
            return _buttonTouch[(int)button] != NoTouch;
        }

        /// <summary>屏幕尺寸或安全区变了就换新的摆法(并松开所有手指);没变什么都不做。</summary>
        public void Resize(int width, int height, double insetLeft = 0, double insetTop = 0, double insetRight = 0, double insetBottom = 0)
        {
            if (width == _insetsWidth && height == _insetsHeight && insetLeft == _insetLeft && insetTop == _insetTop
                && insetRight == _insetRight && insetBottom == _insetBottom)
            {
                return;
            }
            Layout = TouchLayout.For(width, height, insetLeft, insetTop, insetRight, insetBottom);
            RememberInsets(width, height, insetLeft, insetTop, insetRight, insetBottom);
            Reset();
        }

        private void RememberInsets(int width, int height, double l, double t, double r, double b)
        {
            _insetsWidth = width;
            _insetsHeight = height;
            _insetLeft = l;
            _insetTop = t;
            _insetRight = r;
            _insetBottom = b;
        }

        /// <summary>松开所有手指、丢掉没取走的按键(切场景、暂停、换布局时用)。</summary>
        public void Reset()
        {
            _stickTouch = NoTouch;
            ClearStick();
            for (int i = 0; i < ButtonCount; i++)
            {
                _buttonTouch[i] = NoTouch;
                _buttonEdge[i] = false;
            }
            _lastOutX = 0;
        }

        /// <summary>
        /// 一根手指按下。落在按钮上就按下那个按钮(几个按钮的判定圈叠在一起时取离圆心最近的);否则落在摇杆区里、而且摇杆没被别的手指占着,就开始摇杆。
        /// 返回这根手指有没有被认领;没被认领的手指之后的移动与抬起也都被忽略。
        /// </summary>
        public bool Down(int id, double x, double y)
        {
            if (id == NoTouch)
            {
                throw new ArgumentOutOfRangeException(nameof(id));
            }
            if (id == _stickTouch)
            {
                Move(id, x, y);
                return true;
            }
            int button = FindButton(x, y);
            if (button >= 0)
            {
                _buttonTouch[button] = id;
                _buttonEdge[button] = true;
                return true;
            }
            if (_stickTouch == NoTouch && Layout.StickZone.Contains(x, y))
            {
                _stickTouch = id;
                _originX = x;
                _originY = y;
                _thumbX = x;
                _thumbY = y;
                _rawX = 0;
                _rawY = 0;
                _runZone = false;
                _runDir = 0;
                _runEnded = false;
                _sequenceLength = 0;
                _sequencePosition = 0;
                return true;
            }
            return false;
        }

        /// <summary>一根手指移动。只有摇杆的手指有用;按钮上的手指按住就算按着,拖出去也不松开。</summary>
        public void Move(int id, double x, double y)
        {
            if (id != _stickTouch)
            {
                return;
            }
            _thumbX = x;
            _thumbY = y;
            UpdateStick();
        }

        /// <summary>一根手指抬起(或被系统取消)。</summary>
        public void Up(int id)
        {
            if (id == _stickTouch)
            {
                _stickTouch = NoTouch;
                ClearStick();
            }
            for (int i = 0; i < ButtonCount; i++)
            {
                if (_buttonTouch[i] == id)
                {
                    _buttonTouch[i] = NoTouch;
                }
            }
        }

        /// <summary>取走暂停键的"刚按下"(宿主在暂停时也要调它,因为暂停时不再 <see cref="Poll"/>)。</summary>
        public bool TakePause()
        {
            bool pressed = _buttonEdge[(int)TouchButton.Pause];
            _buttonEdge[(int)TouchButton.Pause] = false;
            return pressed;
        }

        /// <summary>
        /// 取出这一帧的输入(每帧调一次):方向是摇杆当前的方向(奔跑序列里插进去的"松开、按下"也在这里给出),
        /// 攻击、跳跃、技能键是自上次取走以来刚按下的(各只给一次)。
        /// </summary>
        public TouchFrame Poll()
        {
            int dirX = _rawX;
            int dirY = _rawY;

            if (_stickTouch != NoTouch && _runZone && _rawX != 0 && _rawX != _runDir)
            {
                // 刚进入奔跑区、或者在奔跑区里换了水平方向:替摇杆"双击"一下
                _runDir = _rawX;
                StartRunSequence(_rawX);
            }
            if (_sequencePosition < _sequenceLength)
            {
                if (_stickTouch == NoTouch || _rawX != _sequenceDir)
                {
                    _sequenceLength = 0;                 // 手指抬了或方向变了:这次双击作废
                    _sequencePosition = 0;
                }
                else
                {
                    dirX = _sequence[_sequencePosition++];
                }
            }
            else if (_runEnded)
            {
                dirX = 0;
                dirY = 0;                                // 全松开一帧:原版的奔跑只有方向全空(或反向)才会结束
                _runEnded = false;
            }
            _lastOutX = dirX;

            SkillKeys skills = SkillKeys.None;
            if (_buttonEdge[(int)TouchButton.SkillA]) { skills |= SkillKeys.A; }
            if (_buttonEdge[(int)TouchButton.SkillS]) { skills |= SkillKeys.S; }
            if (_buttonEdge[(int)TouchButton.SkillD]) { skills |= SkillKeys.D; }
            var frame = new TouchFrame(dirX, dirY, _buttonEdge[(int)TouchButton.Attack], _buttonEdge[(int)TouchButton.Jump], skills);
            _buttonEdge[(int)TouchButton.Attack] = false;
            _buttonEdge[(int)TouchButton.Jump] = false;
            _buttonEdge[(int)TouchButton.SkillA] = false;
            _buttonEdge[(int)TouchButton.SkillS] = false;
            _buttonEdge[(int)TouchButton.SkillD] = false;
            return frame;
        }

        /// <summary>
        /// 奔跑要主角看到一次"按、松、按"。上一帧输出的就是这个方向(正在走):先松开、按、松开,最后一步由常规输出的"按"补上;
        /// 否则(站着或朝另一个方向):按、松开,最后一步由常规输出补上。时间窗是 300 毫秒,两三帧远远够。
        /// </summary>
        private void StartRunSequence(int dir)
        {
            if (_lastOutX == dir)
            {
                _sequence[0] = 0;
                _sequence[1] = dir;
                _sequence[2] = 0;
                _sequenceLength = 3;
            }
            else
            {
                _sequence[0] = dir;
                _sequence[1] = 0;
                _sequenceLength = 2;
            }
            _sequencePosition = 0;
            _sequenceDir = dir;
        }

        private void ClearStick()
        {
            _rawX = 0;
            _rawY = 0;
            _runZone = false;
            _runDir = 0;
            _runEnded = false;
            _sequenceLength = 0;
            _sequencePosition = 0;
        }

        private int FindButton(double x, double y)
        {
            int best = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < ButtonCount; i++)
            {
                double score = Layout.Button((TouchButton)i).Normalized(x, y);
                if (score <= ButtonSlop && score < bestScore)
                {
                    best = i;
                    bestScore = score;
                }
            }
            return best;
        }

        private void UpdateStick()
        {
            double r = Layout.StickRadius;
            double dx = _thumbX - _originX, dy = _thumbY - _originY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double limit = FollowLimit * r;
            if (dist > limit)
            {
                // 原点跟着手指走,手指始终在原点的 limit 之内
                double k = (dist - limit) / dist;
                _originX += dx * k;
                _originY += dy * k;
                dx = _thumbX - _originX;
                dy = _thumbY - _originY;
                dist = limit;
            }
            if (dist < DeadZone * r)
            {
                _rawX = 0;
                _rawY = 0;
            }
            else
            {
                _rawX = Axis(dx / dist, _rawX);
                _rawY = Axis(dy / dist, _rawY);
            }
            bool zone = dist >= (_runZone ? RunExit : RunEnter) * r;
            if (_runZone && !zone)
            {
                _runEnded = _runDir != 0;                // 只有真发过奔跑的"双击"才需要一帧全松开来结束它
                _runDir = 0;
            }
            _runZone = zone;
        }

        private static int Axis(double n, int current)
        {
            if (current > 0)
            {
                return n > AxisExit ? 1 : (n < -AxisEnter ? -1 : 0);
            }
            if (current < 0)
            {
                return n < -AxisExit ? -1 : (n > AxisEnter ? 1 : 0);
            }
            return n > AxisEnter ? 1 : (n < -AxisEnter ? -1 : 0);
        }
    }
}
