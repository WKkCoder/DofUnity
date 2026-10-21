using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dnf.Play
{
    /// <summary>
    /// 按时间表替手指操作触屏层(无人值守地验证触屏这一条路,桌面上没有多点触摸也能验)。
    /// 格式与 <see cref="CombatScript"/> 相仿,是逗号分隔的 "秒:动作":
    /// <c>stick=right</c>、<c>stick=left+up</c>、<c>stick=right+run</c>(推到最外圈)、<c>stick=none</c>(抬起)——摇杆保持到下一项改变为止;
    /// <c>tap=attack</c>、<c>tap=jump</c>、<c>tap=a</c>、<c>tap=s</c>、<c>tap=d</c>、<c>tap=pause</c>——按一下按钮(按下 <see cref="TapMs"/> 毫秒后抬起);
    /// <c>hold=attack</c> 与 <c>release=attack</c> 分开按下与抬起。例如 "0.5:stick=right+run,2:tap=attack,3:stick=none"。
    /// 手指的位置由当前的摆法(<see cref="TouchLayout"/>)算出:摇杆从静止位置按下,再按方向拖到 <see cref="WalkReach"/> 或 <see cref="RunReach"/> 倍半径处,
    /// 所以同一份时间表在任何分辨率下都一样。
    /// </summary>
    public sealed class TouchScript
    {
        /// <summary><c>tap</c> 里按下到抬起隔多久(毫秒)。</summary>
        public const double TapMs = 80.0;

        /// <summary>走路时拇指离原点的倍数(半径)。</summary>
        public const double WalkReach = 0.65;

        /// <summary>奔跑(<c>+run</c>)时拇指离原点的倍数(半径)。</summary>
        public const double RunReach = 1.25;

        private const int StickFinger = 1000;
        private const int ButtonFingerBase = 2000;

        private enum Kind
        {
            Stick,
            Press,
            Lift,
        }

        private sealed class Item
        {
            public double TimeMs;
            public Kind Kind;
            public bool Released;          // stick=none
            public int DirX;
            public int DirY;
            public bool Run;
            public TouchButton Button;
        }

        private readonly List<Item> _items = new List<Item>();
        private int _next;

        /// <summary>没有任何一项。</summary>
        public bool IsEmpty => _items.Count == 0;

        /// <summary>解析时间表;空串得到空的时间表。格式不对抛 <see cref="FormatException"/>。</summary>
        public static TouchScript Parse(string text)
        {
            var script = new TouchScript();
            if (string.IsNullOrWhiteSpace(text))
            {
                return script;
            }
            foreach (string item in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = item.IndexOf(':');
                if (colon <= 0 || !double.TryParse(item.Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
                {
                    throw new FormatException("触屏时间表的每一项形如 秒:动作,例如 0.5:stick=right、2:tap=attack");
                }
                string action = item.Substring(colon + 1).Trim();
                int eq = action.IndexOf('=');
                if (eq <= 0 || eq == action.Length - 1)
                {
                    throw new FormatException("触屏时间表里的动作形如 stick=right、tap=attack:" + action);
                }
                string verb = action.Substring(0, eq), arg = action.Substring(eq + 1);
                double ms = seconds * 1000.0;
                switch (verb)
                {
                    case "stick":
                        script._items.Add(ParseStick(ms, arg));
                        break;
                    case "tap":
                        script._items.Add(new Item { TimeMs = ms, Kind = Kind.Press, Button = ParseButton(arg) });
                        script._items.Add(new Item { TimeMs = ms + TapMs, Kind = Kind.Lift, Button = ParseButton(arg) });
                        break;
                    case "hold":
                        script._items.Add(new Item { TimeMs = ms, Kind = Kind.Press, Button = ParseButton(arg) });
                        break;
                    case "release":
                        script._items.Add(new Item { TimeMs = ms, Kind = Kind.Lift, Button = ParseButton(arg) });
                        break;
                    default:
                        throw new FormatException("触屏时间表里不认识的动作 " + verb);
                }
            }
            // 稳定排序:同一时刻保持写的顺序
            for (int i = 1; i < script._items.Count; i++)
            {
                Item current = script._items[i];
                int j = i - 1;
                while (j >= 0 && script._items[j].TimeMs > current.TimeMs)
                {
                    script._items[j + 1] = script._items[j];
                    j--;
                }
                script._items[j + 1] = current;
            }
            return script;
        }

        private static Item ParseStick(double ms, string arg)
        {
            var item = new Item { TimeMs = ms, Kind = Kind.Stick };
            foreach (string word in arg.Split('+'))
            {
                switch (word)
                {
                    case "none": item.Released = true; break;
                    case "left": item.DirX = -1; break;
                    case "right": item.DirX = 1; break;
                    case "up": item.DirY = -1; break;
                    case "down": item.DirY = 1; break;
                    case "run": item.Run = true; break;
                    default: throw new FormatException("触屏时间表里 stick 不认识的词 " + word);
                }
            }
            if (!item.Released && item.DirX == 0 && item.DirY == 0)
            {
                throw new FormatException("stick 需要方向(left、right、up、down)或 none:" + arg);
            }
            return item;
        }

        private static TouchButton ParseButton(string name)
        {
            switch (name)
            {
                case "attack": return TouchButton.Attack;
                case "jump": return TouchButton.Jump;
                case "a": return TouchButton.SkillA;
                case "s": return TouchButton.SkillS;
                case "d": return TouchButton.SkillD;
                case "pause": return TouchButton.Pause;
                default: throw new FormatException("触屏时间表里不认识的按钮 " + name);
            }
        }

        /// <summary>把到点(不晚于 <paramref name="timeMs"/>)而还没执行的项按顺序喂给触屏层。</summary>
        public void Advance(TouchControls controls, double timeMs)
        {
            if (controls == null)
            {
                throw new ArgumentNullException(nameof(controls));
            }
            while (_next < _items.Count && _items[_next].TimeMs <= timeMs)
            {
                Apply(controls, _items[_next++]);
            }
        }

        private static void Apply(TouchControls c, Item item)
        {
            switch (item.Kind)
            {
                case Kind.Stick:
                    if (item.Released)
                    {
                        if (c.StickActive)
                        {
                            c.Up(StickFinger);
                        }
                        return;
                    }
                    if (!c.StickActive)
                    {
                        c.Down(StickFinger, c.Layout.StickIdleX, c.Layout.StickIdleY);
                    }
                    double reach = (item.Run ? RunReach : WalkReach) * c.Layout.StickRadius;
                    double n = Math.Sqrt(item.DirX * item.DirX + item.DirY * item.DirY);
                    c.Move(StickFinger, c.StickOriginX + item.DirX / n * reach, c.StickOriginY + item.DirY / n * reach);
                    break;
                case Kind.Press:
                    TouchDisc disc = c.Layout.Button(item.Button);
                    c.Down(ButtonFingerBase + (int)item.Button, disc.X, disc.Y);
                    break;
                case Kind.Lift:
                    c.Up(ButtonFingerBase + (int)item.Button);
                    break;
            }
        }
    }
}
