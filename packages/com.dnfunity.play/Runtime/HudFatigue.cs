using System;

// 本项目自己的设计:疲劳条的数据。原版疲劳条的图与位置没有读(见 HudArt 的说明),这里只给"剩多少、上限多少",画的一方自己决定怎么画。
namespace Dnf.Play
{
    /// <summary>
    /// 疲劳条此刻的数(r15-growth;<see cref="HeroProgress.FatigueHud"/> 取):<see cref="Remaining"/> 是剩余点数,<see cref="Max"/> 是上限。
    /// 数据随 <see cref="HeroProgress.FatigueVersion"/> 变:扣疲劳、每日恢复、读档时加一,画的一方据此决定要不要重画。
    /// 每日恢复要有人"刷新"才会发生(<see cref="HeroProgress.RefreshFatigue"/>):进副本的门槛与扣疲劳会刷新;宿主想让条在过了恢复点之后自己涨回去,就在城镇里隔一会儿调一次。
    /// </summary>
    public readonly struct HudFatigue
    {
        public HudFatigue(int remaining, int max)
        {
            Max = Math.Max(0, max);
            Remaining = Math.Max(0, Math.Min(remaining, Max));
        }

        /// <summary>剩余点数(0 到 <see cref="Max"/>)。</summary>
        public int Remaining { get; }

        /// <summary>上限。</summary>
        public int Max { get; }

        /// <summary>已经消耗的点数。</summary>
        public int Consumed => Max - Remaining;

        /// <summary>剩余占上限的比例(0 到 1;上限为 0 时是 0)。</summary>
        public double Ratio => Max > 0 ? (double)Remaining / Max : 0.0;

        /// <summary>一点都不剩了(进副本的门槛挡下)。</summary>
        public bool Empty => Remaining <= 0;
    }
}
