using System;

// 证据:选目的地方案的返回码语义见 E-128:静态读码(控制器的方案分派)得到各码的大意,样本脚本的条件与返回值对照核了几个常用码
// (近处返回"保持距离"、远处返回"走过去"、冷却中返回"原地站着");没读通的码一律按"走向目标"处理。
namespace Dnf.Game.Ai
{
    /// <summary>选目的地脚本(DestinationSelect.ai)算出来的结果:要不要动、动到哪里。</summary>
    public readonly struct AiDestination
    {
        public AiDestination(AiDestinationKind kind, float x, float y)
        {
            Kind = kind;
            X = x;
            Y = y;
        }

        public AiDestinationKind Kind { get; }

        /// <summary>目的地(世界像素);只在 <see cref="AiDestinationKind.MoveTo"/> 时有意义。</summary>
        public float X { get; }

        public float Y { get; }
    }

    public enum AiDestinationKind
    {
        /// <summary>不更新:保持上一次选的目的地(返回 -1)。</summary>
        Keep = 0,

        /// <summary>原地站着(面向目标或面向自己;取消移动)。</summary>
        Hold = 1,

        /// <summary>走到 (X, Y)。</summary>
        MoveTo = 2,
    }

    /// <summary>
    /// 把选目的地脚本返回的方案号换成目的地。方案号怎么用(都是从控制器的分派里读来的大意,不是逐码核过的):
    /// -1 不更新;3、4、33(取消移动)原地站着;1 目标的 X 加减"与目标保持的距离"(站在自己这一侧,近了就往外退、远了就往里走);
    /// 20 在目标的 Y 附近随机抖一点;22 目标的 X 左右 100 像素处随机一侧;其余(0 走到目标脚下、10、11、19 等接近的各种写法,以及没读通的)一律走向目标。
    /// </summary>
    public static class AiDestinations
    {
        public const int Keep = -1;
        public const int FaceTarget = 3;
        public const int FaceSelf = 4;
        public const int KeepDistanceX = 1;
        public const int RandomY = 20;
        public const int RandomSideX = 22;
        public const int CancelMove = 33;

        /// <summary>随机侧移的幅度(像素)。</summary>
        public const int SideStep = 100;

        /// <summary>随机抖动的幅度(像素,单侧)。</summary>
        public const int Jitter = 20;

        public static AiDestination Resolve(int plan, float selfX, float selfY, float targetX, float targetY, float keepDistance, Func<int> rand)
        {
            switch (plan)
            {
                case Keep:
                    return new AiDestination(AiDestinationKind.Keep, selfX, selfY);
                case FaceTarget:
                case FaceSelf:
                case CancelMove:
                    return new AiDestination(AiDestinationKind.Hold, selfX, selfY);
                case KeepDistanceX:
                    {
                        float side = selfX >= targetX ? 1f : -1f;
                        return new AiDestination(AiDestinationKind.MoveTo, targetX + side * keepDistance, targetY);
                    }
                case RandomY:
                    {
                        int r = rand != null ? rand() : 0;
                        float dy = (r % (2 * Jitter + 1)) - Jitter;
                        return new AiDestination(AiDestinationKind.MoveTo, targetX, targetY + dy);
                    }
                case RandomSideX:
                    {
                        int r = rand != null ? rand() : 0;
                        float side = (r & 1) == 0 ? -1f : 1f;
                        return new AiDestination(AiDestinationKind.MoveTo, targetX + side * SideStep, targetY);
                    }
                default:
                    return new AiDestination(AiDestinationKind.MoveTo, targetX, targetY);
            }
        }
    }
}
