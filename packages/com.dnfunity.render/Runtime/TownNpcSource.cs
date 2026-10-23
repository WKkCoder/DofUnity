using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>城镇会话对外说明"地图上摆着哪些 NPC"的接口(流程据此找主角身边能交互的 NPC,商店就是这样打开的;证据 E-119)。</summary>
    public interface ITownNpcSource
    {
        /// <summary>地图里摆着的 NPC(编号与位置,和画面上看到的一致:脚本或动画读不出来的被跳过)。</summary>
        IReadOnlyList<TownNpc> Npcs { get; }

        /// <summary>主角的位置(与 NPC 的坐标同一套世界坐标)。</summary>
        double HeroX { get; }

        double HeroY { get; }
    }
}
