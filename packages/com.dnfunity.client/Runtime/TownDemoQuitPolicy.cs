using System.Globalization;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇演示无人值守退出的判定结果：是否应该退出以及触发退出的一路（bot / sim / wall / null）。
    /// </summary>
    public readonly struct TownDemoQuitDecision
    {
        public TownDemoQuitDecision(bool shouldQuit, string trigger)
        {
            ShouldQuit = shouldQuit;
            Trigger = trigger;
        }

        public bool ShouldQuit { get; }
        public string Trigger { get; }
    }

    /// <summary>
    /// 城镇演示退出策略的纯函数：解析 -dnfquitaftersim、按先到者判定是否退出，并在 DNF_TOWN_DEMO ran 行尾追加游戏时钟与退出原因。
    /// 不依赖 Unity 引擎，供宿主与单元测试共用。
    /// </summary>
    public static class TownDemoQuitPolicy
    {
        public static bool TryParseQuitAfterSim(string raw, out double seconds)
        {
            seconds = 0.0;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return false;
            }
            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0.0)
            {
                return false;
            }
            seconds = parsed;
            return true;
        }

        public static TownDemoQuitDecision EvaluateQuit(
            bool botDone,
            bool flowDoneNoted,
            double simElapsedMs,
            double quitAfterSimSeconds,
            double wallElapsedSeconds,
            double quitAfterWallSeconds)
        {
            bool simHit = quitAfterSimSeconds > 0.0
                && !double.IsInfinity(quitAfterSimSeconds)
                && simElapsedMs >= quitAfterSimSeconds * 1000.0;
            bool wallHit = quitAfterWallSeconds >= 0.0
                && !double.IsInfinity(quitAfterWallSeconds)
                && wallElapsedSeconds >= quitAfterWallSeconds;

            if (wallHit && botDone && flowDoneNoted)
            {
                return new TownDemoQuitDecision(true, "bot");
            }
            if (simHit)
            {
                return new TownDemoQuitDecision(true, "sim");
            }
            if (wallHit)
            {
                return new TownDemoQuitDecision(true, "wall");
            }
            return new TownDemoQuitDecision(false, null);
        }

        public static string AppendQuitLogFields(string baseRanLine, double simElapsedMs, string trigger)
        {
            long simMs = double.IsNaN(simElapsedMs) || double.IsInfinity(simElapsedMs) || simElapsedMs < 0.0
                ? 0L
                : (long)simElapsedMs;
            string who = string.IsNullOrEmpty(trigger) ? "unknown" : trigger;
            return (baseRanLine ?? string.Empty)
                + " simMs=" + simMs.ToString(CultureInfo.InvariantCulture)
                + " quitBy=" + who;
        }
    }
}
