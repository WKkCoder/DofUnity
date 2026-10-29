using System;

namespace Dnf.Game
{
    /// <summary>
    /// 怪物默认 AI(客户端旧式模式机,见 docs/monster-ai.md)里与走位有关的模式,枚举值就是客户端的模式号。
    /// 客户端一共有三十来个模式,这里收游荡、敌对(近战、远程)、绕背、追击、迟疑、受击后的靠近与站定;
    /// 以及被挡住之后的反向;其余(停步 5 到 7、跟随 24、巡逻 27 与 28、脚本指挥的 25 与 26 等)罕见或由脚本指挥,没有接。
    /// </summary>
    public enum MonsterMoveMode
    {
        /// <summary>迟疑(模式 1):远程怪物被远处打了、靠近完毕之后进入;每次选方向只有 30% 的几率去走(0x00891650)。没有目标时转入站桩(15)。</summary>
        Hesitant = 1,

        /// <summary>追击(模式 4):绕背冲刺途中挨了打、或者冲刺中打中了主角之后进入,一直追到目标丢失为止,没有超时。</summary>
        Chase = 4,

        /// <summary>敌对·近战(模式 9):朝目标所在的格子走。有 [melee combat] 类别的怪物发现目标后进入。</summary>
        HostileMelee = 9,

        /// <summary>敌对·远程(模式 10):与目标保持视野的 0.5 到 0.9 倍距离。只有 [range combat](没有 [melee combat])的怪物进入。</summary>
        HostileRanged = 10,

        /// <summary>绕背冲刺(模式 11):发现目标时有一半的几率(远程怪物除外)先朝目标横向冲过去,冲过目标之后转入 9 或 10。</summary>
        Flank = 11,

        /// <summary>靠近(模式 12):被远处(150 像素以上)的攻击打中之后,朝攻击者靠近;视野临时放大到 1.5 倍。</summary>
        Approach = 12,

        /// <summary>游荡(模式 13):没有目标时随机走走停停。怪物出生就在这个模式(0x0088b350)。</summary>
        Wander = 13,

        /// <summary>站桩(模式 15):迟疑中丢了目标之后进入,不走动,直到重新发现目标。</summary>
        Idle = 15,

        /// <summary>反向(模式 21):走路的动作里位置一秒没变(被挡住)之后进入,朝原方向的反方向走,501 毫秒后转追击(4)。</summary>
        Reverse = 21,
    }

    /// <summary>一个模式的移动节拍:怪物"在走"与"站着"两种情形下,到下一次重新选方向的间隔 = 基数 + 随机数 % 范围(毫秒)。</summary>
    public readonly struct MonsterTempo
    {
        public int MoveBaseMs { get; }
        public int MoveRangeMs { get; }
        public int IdleBaseMs { get; }
        public int IdleRangeMs { get; }

        public MonsterTempo(int moveBaseMs, int moveRangeMs, int idleBaseMs, int idleRangeMs)
        {
            MoveBaseMs = moveBaseMs;
            MoveRangeMs = moveRangeMs;
            IdleBaseMs = idleBaseMs;
            IdleRangeMs = idleRangeMs;
        }

        /// <summary>下一次选方向的间隔:<paramref name="idle"/> 为真(这次选的是"不动")取站着的那一组。</summary>
        public int Interval(bool idle, int random)
        {
            return idle
                ? IdleBaseMs + random % IdleRangeMs
                : MoveBaseMs + random % MoveRangeMs;
        }
    }

    /// <summary>
    /// 怪物默认 AI 走位的规则与常数(证据 E-114):视野与找目标、模式的移动节拍表与停下概率表、游荡与敌对的方向选择、"倒着走"。
    /// 全是纯函数,取自客户端的怪物基类(0x008822b0 更新、0x00889da0 移动节拍、0x00891650 方向选择、0x0088ab90 寻路、0x0088a240 目标有效性、0x007fb7c0 距离)。
    /// 方向编码与客户端一致:0 左、1 右、2 上(y 变小)、3 下(y 变大)、4 不动。
    /// </summary>
    public static class MonsterMoveRules
    {
        public const int Left = 0;
        public const int Right = 1;
        public const int Up = 2;
        public const int Down = 3;
        public const int None = 4;

        /// <summary>.mob 没写 [sight] 时的视野(客户端与服务端的缺省相同)。</summary>
        public const int DefaultSight = 200;

        /// <summary>目标有效的距离上限 = 视野 × 这个数(0x0088a240 里的 double 常数 1.4)。</summary>
        public const double SightFactor = 1.4;

        /// <summary>
        /// 距离里纵向要除的深度缩放:全局整数 29 乘 0.01(0x007fb7c0;全局在数据节里的静态值,运行时没有再写它)。
        /// 即纵向差按 1 : 3.45 折成横向像素。
        /// </summary>
        public const float DepthScale = 0.29f;

        /// <summary>没有目标时,两次找目标之间至少隔这么久(毫秒;从"上一次设置目标"算起,0x008822b0)。</summary>
        public const int FindIntervalMs = 1000;

        /// <summary>游荡时每次重新选方向,有这么大的概率(百分数)真的去走(随机选一个斜向),否则站着。</summary>
        public const int WanderStartPercent = 40;

        /// <summary>游荡选方向时的防撞边距:横坐标小于它就往右走,大于房间宽度减它就往左走(0x0088b1c0)。</summary>
        public const int EdgeMargin = 80;

        /// <summary>寻路网格的格子:横向 16 像素(x 右移 4 位),纵向 12 像素(y 除以 12;0x0088ab90)。</summary>
        public const int CellWidth = 16;

        public const int CellHeight = 12;

        /// <summary>进入敌对模式时"好战度"的加成(0x0088a6e0 把一个字段设成 50);倒着走的判断用 加成 + [warlike]。</summary>
        public const int HostileWarlikeBonus = 50;

        /// <summary>远程怪物保持的距离:视野的这两个倍数之间不动,近于下限后退,远于上限靠近(0x00891650 的模式 10)。</summary>
        public const float KeepFarFactor = 0.9f;

        public const float KeepNearFactor = 0.5f;

        /// <summary>模式更新(0x0088d270:超时、到点转模式)的间隔:距上一次超过 333 毫秒才跑一次(0x008822b0)。</summary>
        public const int ModeUpdateIntervalMs = 333;

        /// <summary>敌对模式(9、10)进入之后超过这么久(≥ 10001 毫秒)就回游荡;回去之后不会自动再接战,除非目标丢了又重新发现、或者又被远处打中。</summary>
        public const int HostileTimeoutMs = 10001;

        /// <summary>靠近模式(12)没有目标时,超过这么久(> 5000 毫秒)回游荡。</summary>
        public const int ApproachNoTargetTimeoutMs = 5000;

        /// <summary>发现目标时尝试绕背的几率(百分数,0x0088b410)。有 [range combat] 类别的怪物不绕背。</summary>
        public const int FlankPercent = 50;

        /// <summary>绕背冲刺的终点:目标的 x 再往同一方向加(或减)视野的这个倍数(0x0088b410)。</summary>
        public const float FlankOvershootFactor = 0.5f;

        /// <summary>追击(模式 4)每次选方向有这么大的几率(百分数)朝目标走,其余站着(0x00891650)。</summary>
        public const int ChaseMovePercent = 80;

        /// <summary>迟疑(模式 1)每次选方向朝目标走的几率(百分数)。</summary>
        public const int HesitantMovePercent = 30;

        /// <summary>被打中时,攻击者离怪物不少于这么远(像素,距离函数的结果)才会触发"靠近"(0x0089dd20 里 与 0x96 比较,小于才不触发)。</summary>
        public const int FarHitDistance = 150;

        /// <summary>靠近模式(12)进入时视野放大的倍数,离开时还原(0x0088a6e0 与 0x00894fb0)。</summary>
        public const float ApproachSightFactor = 1.5f;

        /// <summary>靠近模式里"到了"的边距 = 怪物受击框宽度的一半再加这么多(0x00891650)。</summary>
        public const int ArriveMarginExtra = 10;

        /// <summary>靠近模式里离目标不到 max(这个数, 视野 × 0.1) 就算到了。</summary>
        public const int NearTargetMinimum = 70;

        public const float NearTargetSightFactor = 0.1f;

        // 每个模式一行:走的基数、走的范围、站的基数、站的范围(客户端 0x015ce7c0 的 16 字节表项)
        private static readonly MonsterTempo WanderTempo = new MonsterTempo(500, 1000, 300, 3000);
        private static readonly MonsterTempo HostileTempo = new MonsterTempo(1000, 300, 800, 200);

        private static readonly MonsterTempo HesitantTempo = new MonsterTempo(300, 3000, 300, 3000);
        private static readonly MonsterTempo ChaseTempo = new MonsterTempo(1000, 1000, 300, 3000);

        /// <summary>
        /// 模式的移动节拍表(0x015ce7c0):游荡与站桩 走 0.5 到 1.5 秒、站 0.3 到 3.3 秒;敌对、绕背、靠近(9 到 12)走 1.0 到 1.3 秒、站 0.8 到 1.0 秒;
        /// 追击(4)走 1.0 到 2.0 秒、站 0.3 到 3.3 秒;迟疑(1)走与站都是 0.3 到 3.3 秒。
        /// </summary>
        public static MonsterTempo TempoOf(MonsterMoveMode mode)
        {
            switch (mode)
            {
                case MonsterMoveMode.Hesitant:
                    return HesitantTempo;
                case MonsterMoveMode.Chase:
                    return ChaseTempo;
                case MonsterMoveMode.HostileMelee:
                case MonsterMoveMode.HostileRanged:
                case MonsterMoveMode.Flank:
                case MonsterMoveMode.Approach:
                    return HostileTempo;
                default:
                    return WanderTempo;
            }
        }

        /// <summary>
        /// 选完方向之后,横向被"停下"的概率(百分数,0x015ce9a0 的默认表,按模式取):这些模式都是 0
        /// (表里只有模式 1、4 的纵向 30 与模式 13 的纵向 50 不是 0——游荡时纵向有一半的概率被取消)。
        /// </summary>
        public static int StopPercentX(MonsterMoveMode mode)
        {
            return 0;
        }

        /// <summary>纵向被"停下"的概率:游荡 50,迟疑与追击 30,其余 0。</summary>
        public static int StopPercentY(MonsterMoveMode mode)
        {
            switch (mode)
            {
                case MonsterMoveMode.Wander:
                    return 50;
                case MonsterMoveMode.Hesitant:
                case MonsterMoveMode.Chase:
                    return 30;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// 客户端的距离函数(0x007fb7c0,不带"原样"开关):纵向差先除以 <see cref="DepthScale"/> 并取整,再与横向差求平方和的平方根,取整。
        /// </summary>
        public static int Distance(int x1, int y1, int x2, int y2)
        {
            int scaledY = (int)((float)(y1 - y2) / DepthScale);
            long dx = x1 - x2;
            long squares = (long)scaledY * scaledY + dx * dx;
            return (int)Math.Sqrt((double)squares);
        }

        /// <summary>
        /// 目标是否有效(0x0088a240 里与距离有关的那一支):视野为 0 表示无限远,永远有效;否则距离不超过 视野 × <see cref="SightFactor"/>(取整)。
        /// 客户端还要求目标活着、能被攻击等,这里由调用方负责。
        /// </summary>
        public static bool IsValidTarget(int sight, int selfX, int selfY, int targetX, int targetY)
        {
            if (sight == 0)
            {
                return true;
            }
            int limit = (int)((double)sight * SightFactor);
            return Distance(selfX, selfY, targetX, targetY) <= limit;
        }

        /// <summary>
        /// 靠近防撞边:离左边不到 <see cref="EdgeMargin"/> 就往右、离右边不到它就往左;返回是否改了方向。
        /// <paramref name="roomWidth"/> 是房间的像素宽度(客户端按背景块数算,这里由调用方给)。
        /// </summary>
        public static bool NearEdge(int x, int roomWidth, out int dirX)
        {
            if (x < EdgeMargin)
            {
                dirX = Right;
                return true;
            }
            if (roomWidth - EdgeMargin < x)
            {
                dirX = Left;
                return true;
            }
            dirX = None;
            return false;
        }

        /// <summary>
        /// 近战敌对选方向(0x0088ab90 在没有障碍时的结果):比较自己与目标所在的格子,横向、纵向各自朝目标那一格走,同格就不动。
        /// 客户端先按攻击记录的射程算一遍方向,最后被寻路的下一格覆盖,所以起作用的是格子比较。
        /// </summary>
        public static void ApproachDirections(int selfX, int selfY, int targetX, int targetY, out int dirX, out int dirY)
        {
            int selfCellX = selfX / CellWidth, targetCellX = targetX / CellWidth;
            dirX = targetCellX > selfCellX ? Right : (targetCellX < selfCellX ? Left : None);
            int selfCellY = selfY / CellHeight, targetCellY = targetY / CellHeight;
            dirY = targetCellY > selfCellY ? Down : (targetCellY < selfCellY ? Up : None);
        }

        /// <summary>
        /// 远程敌对的横向选择(0x00891650 模式 10):距离 ≥ 视野 × 0.9 就靠近,≤ 视野 × 0.5 就后退,中间不动。
        /// </summary>
        public static int KeepDistanceX(int sight, int selfX, int targetX)
        {
            float far = sight * KeepFarFactor;
            float near = sight * KeepNearFactor;
            float distance = Math.Abs(selfX - targetX);
            if (targetX < selfX)
            {
                if (distance < far)
                {
                    return near < distance ? None : Right;
                }
                return Left;
            }
            if (far <= distance)
            {
                return Right;
            }
            return near < distance ? None : Left;
        }

        /// <summary>远程敌对的纵向选择:总是朝目标的 y 走(目标更靠上就上,否则下;从不选"不动")。</summary>
        public static int KeepDistanceY(int selfY, int targetY)
        {
            return targetY < selfY ? Up : Down;
        }

        /// <summary>方向取反(0x00766110):左右互换、上下互换,其余变成不动。</summary>
        public static int Reverse(int dir)
        {
            switch (dir)
            {
                case Left:
                    return Right;
                case Right:
                    return Left;
                case Up:
                    return Down;
                case Down:
                    return Up;
                default:
                    return None;
            }
        }

        /// <summary>
        /// 这一次选方向要不要"倒着走"(0x0088ab90 末尾):敌对加成加 [warlike] 不超过 随机数 % 100 就取反两个方向。
        /// [warlike] ≥ 50 的怪物在敌对模式里永远不会倒着走,[warlike] 为 0 的怪物一半的选择会背对目标走。
        /// </summary>
        public static bool ShouldReverse(int warlike, int random)
        {
            return ShouldReverse(warlike, HostileWarlikeBonus, random);
        }

        /// <summary>
        /// 同上,加成按当前模式给:进入敌对(9、10)时是 <see cref="HostileWarlikeBonus"/>,其余模式是 0(退出时清掉,0x00894fb0)——
        /// 所以追击、迟疑里 [warlike] 为 50 的怪物有一半的选择是背对目标走的。
        /// </summary>
        public static bool ShouldReverse(int warlike, int bonus, int random)
        {
            return bonus + warlike <= random % 100;
        }

        /// <summary>好战度加成:只有敌对模式(9、10)是 <see cref="HostileWarlikeBonus"/>,其余是 0。</summary>
        public static int WarlikeBonusOf(MonsterMoveMode mode)
        {
            return mode == MonsterMoveMode.HostileMelee || mode == MonsterMoveMode.HostileRanged ? HostileWarlikeBonus : 0;
        }

        /// <summary>
        /// 绕背冲刺的终点横坐标(0x0088b410):怪物在目标左边就取 目标 x + 视野 × 0.5,否则取 目标 x − 视野 × 0.5(浮点结果截断取整)。
        /// </summary>
        public static int FlankDestinationX(int selfX, int targetX, int sight)
        {
            float overshoot = sight * FlankOvershootFactor;
            return selfX < targetX ? (int)(overshoot + targetX) : (int)(targetX - overshoot);
        }

        /// <summary>绕背冲刺的横向选择(0x00891650 模式 11):朝目标当前的 x 走;纵向不动。</summary>
        public static int FlankDirectionX(int selfX, int targetX)
        {
            return selfX < targetX ? Right : Left;
        }

        /// <summary>绕背冲刺到了没有(0x0088d270 模式 11):朝左走的已经不在终点右边、或者朝右走的已经不在终点左边。</summary>
        public static bool FlankArrived(int dirX, int selfX, int destX)
        {
            return (dirX == Left && selfX <= destX) || (dirX == Right && destX <= selfX);
        }

        /// <summary>追击与迟疑里"这一次要不要走"的掷骰:随机数 % 100 小于 <paramref name="percent"/> 才去走。</summary>
        public static bool RollMove(int percent, int random)
        {
            return random % 100 < percent;
        }

        /// <summary>被打中之后靠近的终点所用的类别。</summary>
        public enum ApproachStyle
        {
            /// <summary>[close-passive] 或 [close-carefully]:目标左边或右边(随机一侧)、与自己横向差的一半远的地方,纵向再随机偏一点。</summary>
            Careful,

            /// <summary>[close-tough]:直接奔向目标所在的位置。</summary>
            Tough,

            /// <summary>[close-manace]:停在目标靠自己的这一侧、横向差的五分之一的地方,纵向偏到场地中线一侧。</summary>
            Manace,

            /// <summary>没有这四个类别之一:客户端的终点没有赋值(为 0,0 再被校正),这里按目标位置处理(推断)。</summary>
            Plain,
        }

        /// <summary>
        /// 被远处打中之后"靠近"的终点(0x0089dd20):按类别算出终点 (x, y)。三个随机数按客户端取的顺序:
        /// <paramref name="randomSideX"/> 决定横向偏左还是偏右、<paramref name="randomSideY"/> 决定纵向偏上还是偏下、<paramref name="randomSpan"/> 是纵向偏移的幅度;
        /// 结果之后客户端还会把终点校正到可走区里(0x00e4 号虚函数),这里由调用方处理。
        /// </summary>
        public static void ApproachDestination(ApproachStyle style, int selfX, int targetX, int targetY, int randomSideX, int randomSideY, int randomSpan, out int destX, out int destY)
        {
            switch (style)
            {
                case ApproachStyle.Tough:
                case ApproachStyle.Plain:
                    destX = targetX;
                    destY = targetY;
                    return;
                case ApproachStyle.Manace:
                {
                    int fifth = Math.Abs(targetX - selfX) / 5;
                    bool targetOnRight = selfX < targetX;
                    destX = targetOnRight ? targetX - fifth : targetX + fifth;
                    destY = targetY > 230 ? targetY - 50 : targetY + 50;
                    return;
                }
                default:
                {
                    int half = Math.Abs(targetX - selfX + 10) / 2;
                    int span = Math.Max(half / 2, 1);
                    destX = targetX + ((randomSideX & 1) == 0 ? -half : half);
                    int jitter = randomSpan % span;
                    destY = targetY + ((randomSideY & 1) == 0 ? -jitter : jitter);
                    return;
                }
            }
        }

        /// <summary>
        /// 靠近模式的方向选择(0x00891650 模式 12):横向、纵向各自朝终点走;离终点的距离小于 <paramref name="margin"/> 的那一轴不动。
        /// 返回 true 表示两轴都到了(调用方再按目标是不是已经够近、类别来转模式)。
        /// </summary>
        public static bool ApproachStep(int selfX, int selfY, int destX, int destY, int margin, out int dirX, out int dirY)
        {
            dirX = selfX <= destX ? Right : Left;
            dirY = selfY <= destY ? Down : Up;
            if (Math.Abs(selfX - destX) < margin)
            {
                dirX = None;
            }
            if (Math.Abs(selfY - destY) < margin)
            {
                dirY = None;
            }
            return dirX == None && dirY == None;
        }
    }
}
