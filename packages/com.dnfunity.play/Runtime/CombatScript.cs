using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 按时间表替主角按键(无人值守地验证走动、挥击与命中,也用来让命令行与 Unity 播放器跑出完全相同的战斗画面)。
    /// 时间表是逗号分隔的 "秒:动作":动作是 none、left、right、up、down(可用 + 组合),再加上 attack、jump 表示在那一刻按一下攻击键或跳跃键(只按一次的边沿),
    /// a、s、d、f、g、h 表示按一下技能快捷键 A S D F G H(例如 "1:a" 在第 1 秒放 A 槽的技能);
    /// z 与 buff 表示按一下出招指令里的"技能"键与"增益"键(M47,证据 E-140;和方向键写在同一项里就是一串指令,例如 "1:right+z"),要会话开着出招指令才有用;
    /// 方向保持到下一项改变为止,例如 "0.5:left,2:left+attack,3:none"。
    /// 蓄力类技能要按住技能键:holda、holds、holdd、holdf、holdg、holdh(出招指令的技能键是 holdz)表示从这一项起按住对应的键、直到下一项改变(和方向一样,
    /// 每一项都重写"按着哪些键",所以 none 就是全部松开);例如 "1:f+holdf,1.4:none" 在第 1 秒按下 F 键并按住 0.4 秒(证据 E-138)。
    /// 攻击键当扳机的指令要按住攻击键(防御姿态,M66,证据 E-164):holdx 表示从这一项起按住攻击键(它不产生"刚按下",按一下还是写 attack),
    /// 例如 "1:down,1.1:none,1.2:down,1.3:down+attack+holdx,2:down+holdx,3:none" 是"下、下、攻击"打出指令再一直按住到第 3 秒。
    /// 双击方向键奔跑就写成"按、松、按":"0:right,0.1:none,0.2:right"(两次按下相隔 0.2 秒)。
    /// 两种用法:<see cref="Poll"/> 按任意时刻读取按键状态(实时运行);<see cref="Step"/> 以固定的 1/60 秒步长推进一个会话(逐帧确定的模拟,
    /// 与命令行 combat-check 同一份代码,用来对拍两条绘制路径)。
    /// </summary>
    public sealed class CombatScript
    {
        /// <summary>固定步长的模拟:每步 1000/60 毫秒。</summary>
        public const double StepMs = 1000.0 / 60.0;

        private sealed class Key
        {
            public double Seconds;
            public int DirX;
            public int DirY;
            public bool Attack;
            public bool AttackFired;
            public bool Jump;
            public bool JumpFired;
            public SkillKeys Skills;
            public SkillKeys SkillsFired;
            public SkillKeys Held;
        }

        private readonly List<Key> _keys = new List<Key>();
        private int _dirX;
        private int _dirY;
        private SkillKeys _held;

        /// <summary><see cref="Step"/> 已经模拟到的时刻(毫秒)。</summary>
        public double TimeMs { get; private set; }

        /// <summary>没有任何一项。</summary>
        public bool IsEmpty => _keys.Count == 0;

        /// <summary>时间表里用了出招指令的键(z、buff、holdz):解析时记下;<see cref="Step"/> 据此给会话打开出招指令。</summary>
        public bool UsesCommands { get; private set; }

        /// <summary>最近一次读取时按着的技能键(时间表里的 holda 到 holdh,证据 E-138);蓄力类技能每步查它。</summary>
        public SkillKeys HeldSkills => _held;

        /// <summary>解析时间表;空串得到空的时间表。格式不对抛 <see cref="FormatException"/>。</summary>
        public static CombatScript Parse(string text)
        {
            var script = new CombatScript();
            if (string.IsNullOrWhiteSpace(text))
            {
                return script;
            }
            foreach (string item in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = item.Split(':');
                if (kv.Length != 2 || !double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                {
                    throw new FormatException("时间表的每一项形如 秒:动作,例如 0.5:left,2:left+attack");
                }
                var key = new Key { Seconds = sec };
                foreach (string word in kv[1].Split('+'))
                {
                    switch (word)
                    {
                        case "left": key.DirX = -1; break;
                        case "right": key.DirX = 1; break;
                        case "up": key.DirY = -1; break;
                        case "down": key.DirY = 1; break;
                        case "attack": key.Attack = true; break;
                        case "jump": key.Jump = true; break;
                        case "a": key.Skills |= SkillKeys.A; break;
                        case "s": key.Skills |= SkillKeys.S; break;
                        case "d": key.Skills |= SkillKeys.D; break;
                        case "f": key.Skills |= SkillKeys.F; break;
                        case "g": key.Skills |= SkillKeys.G; break;
                        case "h": key.Skills |= SkillKeys.H; break;
                        case "z": key.Skills |= SkillKeys.CommandSkill; script.UsesCommands = true; break;
                        case "buff": key.Skills |= SkillKeys.CommandBuff; script.UsesCommands = true; break;
                        case "holda": key.Held |= SkillKeys.A; break;
                        case "holds": key.Held |= SkillKeys.S; break;
                        case "holdd": key.Held |= SkillKeys.D; break;
                        case "holdf": key.Held |= SkillKeys.F; break;
                        case "holdg": key.Held |= SkillKeys.G; break;
                        case "holdh": key.Held |= SkillKeys.H; break;
                        case "holdz": key.Held |= SkillKeys.CommandSkill; script.UsesCommands = true; break;
                        case "holdx": key.Held |= SkillKeys.Attack; script.UsesCommands = true; break;
                        case "none": break;
                        default: throw new FormatException("时间表里不认识的动作 " + word);
                    }
                }
                script._keys.Add(key);
            }
            script._keys.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
            return script;
        }

        /// <summary>
        /// 读到 <paramref name="timeMs"/> 为止的按键状态:方向取已经到点的最后一项;返回自上次读取以来是否有新到点的攻击键(每一项只会返回一次 true)。
        /// 跳跃键在这个重载里被忽略(也算作已读);要跳跃键用带 jump 参数的重载。
        /// </summary>
        public bool Poll(double timeMs, out int dirX, out int dirY)
        {
            return Poll(timeMs, out dirX, out dirY, out _);
        }

        /// <summary>
        /// 同上,另外给出自上次读取以来是否有新到点的跳跃键(<paramref name="jump"/>,每一项只会给出一次 true)。
        /// </summary>
        public bool Poll(double timeMs, out int dirX, out int dirY, out bool jump)
        {
            return Poll(timeMs, out dirX, out dirY, out jump, out _);
        }

        /// <summary>
        /// 同上,另外给出自上次读取以来新到点的技能键(<paramref name="skills"/>:时间表里的 a、s、d、f、g、h 对应 A S D F G H 六个快捷键,每一项只会给出一次)。
        /// </summary>
        public bool Poll(double timeMs, out int dirX, out int dirY, out bool jump, out SkillKeys skills)
        {
            bool attack = false;
            jump = false;
            skills = SkillKeys.None;
            foreach (Key k in _keys)
            {
                if (k.Seconds * 1000.0 <= timeMs)
                {
                    _dirX = k.DirX;
                    _dirY = k.DirY;
                    _held = k.Held;
                    if (k.Attack && !k.AttackFired)
                    {
                        k.AttackFired = true;
                        attack = true;
                    }
                    if (k.Jump && !k.JumpFired)
                    {
                        k.JumpFired = true;
                        jump = true;
                    }
                    SkillKeys fresh = k.Skills & ~k.SkillsFired;
                    if (fresh != SkillKeys.None)
                    {
                        k.SkillsFired |= fresh;
                        skills |= fresh;
                    }
                }
            }
            dirX = _dirX;
            dirY = _dirY;
            return attack;
        }

        /// <summary>按当前模拟时刻读一次按键,把会话推进一个固定步长,时刻加一步。返回这一步有没有按攻击键。</summary>
        public bool Step(IActionSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            bool attack = Poll(TimeMs, out int dirX, out int dirY, out bool jump, out SkillKeys skills);
            if (session is ISkillHoldSession withHold)
            {
                withHold.SkillsHeld = _held;
            }
            if (UsesCommands && session is ICommandSession withCommands)
            {
                withCommands.CommandsEnabled = true;     // 时间表里写了 z 或 buff:这一局要开出招指令(没写的时间表不碰开关,旧行为不变)
            }
            if (session is ISkillSession withSkills)
            {
                withSkills.Update(StepMs, dirX, dirY, attack, jump, skills);
            }
            else
            {
                session.Update(StepMs, dirX, dirY, attack, jump);
            }
            TimeMs += StepMs;
            return attack;
        }

        /// <summary>一直推进到模拟时刻不早于 <paramref name="toMs"/>(与逐步循环 <c>while (TimeMs &lt; toMs) Step()</c> 一样)。</summary>
        public void RunUntil(IActionSession session, double toMs)
        {
            while (TimeMs < toMs)
            {
                Step(session);
            }
        }
    }
}
