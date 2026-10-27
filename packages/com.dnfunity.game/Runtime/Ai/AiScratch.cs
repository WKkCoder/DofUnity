using System;

// 证据:暂存槽的布局、每次思考前的清空与各关键字的写入点见 E-052(静态读码);怪物脚本实际用到的求值写法与怪物侧的接线见 E-128。
namespace Dnf.Game.Ai
{
    /// <summary>
    /// 暂存槽的编号。槽号就是条件的类型号(<see cref="AiVocabulary.KindOf"/>):
    /// 比较类条件(血量、距离、状态……)比的就是这一格里的数,所以"关键字"和"槽"是同一套编号。
    /// 只列怪物脚本里用得到、而且读通了写入点的槽;其余槽号的关键字读出来恒为"未写入"(-1)。
    /// </summary>
    public static class AiSlot
    {
        /// <summary>槽的个数(类型号 0 到 0x3f)。</summary>
        public const int Count = 0x40;

        public const int Mind = 1;
        public const int Hp = 2;
        public const int Mp = 3;
        public const int State = 4;
        public const int Event = 5;
        public const int Random = 6;

        /// <summary>上一次函数条件写出来的数([void] 的 get random()、各种计数函数都写这一格);脚本里写 [last result] 读它。</summary>
        public const int LastResult = 7;

        public const int WeaponType = 9;
        public const int TargetDistance = 0x0a;
        public const int TargetState = 0x0b;
        public const int ComboBonus = 0x0c;
        public const int ActiveStatus = 0x0d;
        public const int MyState = 0x0e;
        public const int Hit = 0x11;
        public const int Distance = 0x14;
        public const int MyAttack = 0x27;
        public const int MyLevel = 0x33;
        public const int Command = 0x36;
        public const int MapIndex = 0x3b;
    }

    /// <summary>
    /// 一次思考用的暂存区:64 格,每格两个浮点数(值与"最大值")。每次思考前整体清成(值 -1,最大值 0),
    /// 再由宿主按角色(事件、选目的地、动作、移动方式)写上这一刻的情形(血量、距离、状态……);
    /// 比较运算符读的就是这两个数(百分比比较用 值 ÷ 最大值 × 100)。
    /// </summary>
    public sealed class AiScratch
    {
        /// <summary>清空时每一格的值:"没写过"。</summary>
        public const float Unset = -1f;

        private readonly float[] _value = new float[AiSlot.Count];
        private readonly float[] _max = new float[AiSlot.Count];

        public AiScratch()
        {
            Clear();
        }

        /// <summary>整体清成(-1,0)。</summary>
        public void Clear()
        {
            for (int i = 0; i < AiSlot.Count; i++)
            {
                _value[i] = Unset;
                _max[i] = 0f;
            }
        }

        public float Value(int slot) => InRange(slot) ? _value[slot] : Unset;

        public float Max(int slot) => InRange(slot) ? _max[slot] : 0f;

        public void Set(int slot, float value, float max = 0f)
        {
            if (!InRange(slot))
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }
            _value[slot] = value;
            _max[slot] = max;
        }

        private static bool InRange(int slot) => slot >= 0 && slot < AiSlot.Count;
    }
}
