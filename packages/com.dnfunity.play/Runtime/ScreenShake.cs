using System;

// 证据:E-133。客户端场景的震屏起点 0x008e9950(记下强度、清零计时、强度为 0 时清偏移)与逐帧更新 0x008e9a80 的静态读码(非 REA 自动化):
// 震动期间每一帧给整个世界画面一个随机的整数偏移——横向在 [-强度, +强度]、纵向同样(把 2×强度×缩放取整当范围 r,偏移 = 随机数 % (r+1) − r/2),
// 缩放系数是一个全局浮点与 1.0 近似相等时取 1.0、否则取 0.5(那个全局的运行时取值没有读到,按 1.0);没有衰减;计时到了持续时间就停,偏移清零;
// 再次开始会覆盖强度并重新计时。偏移落在场景的视图原点上,只挪世界层(地图、对象),不挪界面。
namespace Dnf.Play
{
    /// <summary>
    /// 屏幕震动:<see cref="Start"/> 给强度(像素)与持续毫秒,之后每次 <see cref="Step"/> 换一个随机偏移,时间到了归零。
    /// 随机数由外面给(测试里用固定的),默认用确定种子的 <see cref="Random"/>,同一局的震动可重放。
    /// </summary>
    public sealed class ScreenShake
    {
        private readonly Func<int, int> _random;
        private int _strength;
        private int _durationMs;
        private double _elapsedMs;
        private bool _active;

        public ScreenShake(Func<int, int> random = null)
        {
            if (random == null)
            {
                var rng = new Random(1);
                random = n => n <= 1 ? 0 : rng.Next(n);
            }
            _random = random;
        }

        /// <summary>画面此刻的横向偏移(像素;0 不震)。</summary>
        public int OffsetX { get; private set; }

        /// <summary>画面此刻的纵向偏移(像素)。</summary>
        public int OffsetY { get; private set; }

        /// <summary>正在震。</summary>
        public bool Active => _active;

        public int Strength => _strength;

        /// <summary>开始(或覆盖)一次震动。强度小于等于 0 时只清偏移;持续时间为 0 的震动第一步就结束,画面不会动(与客户端的先算后判一致)。</summary>
        public void Start(int strength, int durationMs)
        {
            _strength = Math.Max(0, strength);
            _durationMs = durationMs;
            _elapsedMs = 0;
            _active = _strength > 0;
            if (!_active)
            {
                OffsetX = 0;
                OffsetY = 0;
            }
        }

        /// <summary>过去 <paramref name="deltaMs"/> 毫秒:没在震就什么都不做;在震就换新的随机偏移,时间到了停下并清偏移。</summary>
        public void Step(double deltaMs)
        {
            if (!_active)
            {
                return;
            }
            _elapsedMs += deltaMs;
            int range = _strength * 2;
            OffsetX = _random(range + 1) - range / 2;
            OffsetY = _random(range + 1) - range / 2;
            if (_elapsedMs >= _durationMs)
            {
                _active = false;
                _strength = 0;
                OffsetX = 0;
                OffsetY = 0;
            }
        }

        /// <summary>立刻停下(换房间时用)。</summary>
        public void Stop()
        {
            _active = false;
            _strength = 0;
            OffsetX = 0;
            OffsetY = 0;
        }
    }
}
