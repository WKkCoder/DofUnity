using System;

namespace Dnf.Game
{
    /// <summary>
    /// 一次倒地(DOWN)请求里与运动有关的四项(证据 E-079 的五项列表去掉攻击方那项):
    /// 阶段(决策函数的输出二:0 = 首次腾空,1 = 已经落过地之后再挨打 / 落地后的颠一下,2 = 躺下)、类型 0 到 3(见 <see cref="Knockdown.TypeOf"/>)、
    /// [push aside]、[lift up]。落地后的再请求用的是同一个形状(<see cref="Knockdown.AfterLanding"/>)。
    /// </summary>
    public readonly struct KnockdownRequest
    {
        public int Stage { get; }
        public int Type { get; }
        public int PushAside { get; }
        public int LiftUp { get; }

        public KnockdownRequest(int stage, int type, int pushAside, int liftUp)
        {
            Stage = stage;
            Type = type;
            PushAside = pushAside;
            LiftUp = liftUp;
        }
    }

    /// <summary>开始倒地那一刻物体自己的状态(证据 E-082、E-083):体重、重力、是不是已经在倒地中、旧阶段、旧类型、当前高度。</summary>
    public readonly struct KnockdownBody
    {
        public int Weight { get; }
        public int Gravity { get; }
        public bool PrevDown { get; }
        public int PrevStage { get; }
        public int PrevType { get; }
        public double Z { get; }

        public KnockdownBody(int weight, int gravity, bool prevDown, int prevStage, int prevType, double z)
        {
            Weight = weight;
            Gravity = gravity;
            PrevDown = prevDown;
            PrevStage = prevStage;
            PrevType = prevType;
            Z = z;
        }
    }

    /// <summary>
    /// 一次倒地开始处理算出的运动:阶段(类型 0 的阶段 1 在没有抬升时直接改成 2)、类型、水平初速(像素/秒,≥ 0,方向另算)、
    /// 抬升高度(像素)与 z 初速(像素/秒),以及弹起类要存下的 [lift up]、[push aside](砸地之后再飞用)。
    /// </summary>
    public readonly struct KnockdownLaunch
    {
        public int Stage { get; }
        public int Type { get; }

        /// <summary>水平初速(像素/秒)。首次请求时恒 ≥ 0(负的 [push aside] 翻转朝向,见 <see cref="FlipSide"/>);接续请求里沿用原值的符号。</summary>
        public int Horizontal { get; }

        /// <summary>[push aside] 为负:朝向翻到攻击方的另一边,水平量取反(只在不是接续飞行时,证据 E-083)。</summary>
        public bool FlipSide { get; }

        /// <summary>接续飞行:已在倒地、落地、并且(旧类型是弹起类,或新阶段大于旧阶段)。接续时不改朝向。</summary>
        public bool Continuing { get; }

        /// <summary>抬升高度(像素,带符号;已按 0.9 到 1.1 随机)。</summary>
        public int Height { get; }

        /// <summary>z 初速(像素/秒,向上为正)。</summary>
        public double ZSpeed { get; }

        /// <summary>类型 2、3(弹起):要把 <see cref="StoredLift"/>、<see cref="StoredPush"/> 存下来,砸地落地后再飞一次。</summary>
        public bool StoresBounce { get; }

        public int StoredLift { get; }
        public int StoredPush { get; }

        public KnockdownLaunch(int stage, int type, int horizontal, bool flipSide, bool continuing, int height, double zSpeed,
            bool storesBounce, int storedLift, int storedPush)
        {
            Stage = stage;
            Type = type;
            Horizontal = horizontal;
            FlipSide = flipSide;
            Continuing = continuing;
            Height = height;
            ZSpeed = zSpeed;
            StoresBounce = storesBounce;
            StoredLift = storedLift;
            StoredPush = storedPush;
        }
    }

    /// <summary>
    /// 一次命中给怪物的倒地输入(证据 E-079、E-083):攻击信息的 [lift up]、[bounce]、[ignore weight]、[damage reaction] 是不是 1(普通受击),
    /// 加上两个 0 到 999 的随机数(水平量、高度的 ±20%、±10% 抖动用)。没有它(<see cref="IsSet"/> 为假)时怪物走占位的倒地。
    /// </summary>
    public readonly struct KnockdownInput
    {
        public bool IsSet { get; }
        public int LiftUp { get; }
        public bool Bounce { get; }
        public bool IgnoreWeight { get; }

        /// <summary>[damage reaction] 是普通受击(1):怪物在空中或已在倒地时也走倒地分支,而不是硬直(E-079 第 6' 步)。</summary>
        public bool ReactionDamage { get; }

        public int RollHorizontal { get; }
        public int RollHeight { get; }

        public KnockdownInput(int liftUp, bool bounce, bool ignoreWeight, bool reactionDamage, int rollHorizontal, int rollHeight)
        {
            IsSet = true;
            LiftUp = liftUp;
            Bounce = bounce;
            IgnoreWeight = ignoreWeight;
            ReactionDamage = reactionDamage;
            RollHorizontal = rollHorizontal;
            RollHeight = rollHeight;
        }
    }

    /// <summary>
    /// 倒地(DOWN)状态的开始处理与落地后的再请求(证据 E-082、E-083;静态读码,没有运行时验证)。这里只有数值:
    /// 谁来飞、飞多高、落地之后做什么,由 <c>MonsterActor</c> 的倒地状态机按每帧函数的结构去执行。
    /// 数值全部来自引擎代码里的常数与调用方给的攻击信息、体重,不含任何原版数据值。
    /// </summary>
    public static class Knockdown
    {
        /// <summary>怪物的默认重力,像素/秒²(物理参数 +0x24 的整数,初值 −1500,证据 E-082)。</summary>
        public const int Gravity = 1500;

        /// <summary>弹起类(类型 2、3)先砸地:把 [lift up] 换成这个数去缩放(证据 E-083)。</summary>
        public const int SlamLift = -400;

        /// <summary>
        /// 倒地类型:[bounce] 非 0 且输出二为 0 时 = [ignore weight] 非 0 ? 2 : 3,否则 = [ignore weight] 非 0 ? 0 : 1(证据 E-079)。
        /// 即"弹起"与"受体重影响"两位:0 = 不受体重影响且不弹,1 = 受体重影响,2 = 弹起不受体重影响,3 = 弹起且受体重影响。
        /// </summary>
        public static int TypeOf(bool bounce, bool ignoreWeight, int stage)
        {
            if (bounce && stage == 0)
            {
                return ignoreWeight ? 2 : 3;
            }
            return ignoreWeight ? 0 : 1;
        }

        /// <summary>
        /// 体重缩放(0x008327c0):a × 4000 ÷ ((w × 100 ÷ k − 10000) × 0.0121 + 2100.0),调用方再向零取整。
        /// 客户端用 x87 做浮点部分,这里按 32 位浮点算(Direct3D 的 24 位精度,FPU 模式还没核对,证据 E-080 的说明同)。
        /// </summary>
        public static int WeightScale(int a, int weight, int k = 100)
        {
            if (k == 0)
            {
                return 0;
            }
            int numerator = unchecked(a * 4000);
            int ratio = unchecked(weight * 100) / k;
            float denominator = (float)((float)(ratio - 10000) * 0.0121f) + 2100.0f;
            if (denominator == 0f)
            {
                return 0;
            }
            return (int)(float)((float)numerator / denominator);
        }

        /// <summary>
        /// 0x011b98c0(a, b, r, m):r ≥ m 取 b,r ≤ 0 取 a,否则 a + (b − a) × r ÷ m,32 位浮点,向零取整(证据 E-083)。
        /// </summary>
        public static int Lerp(int a, int b, int r, int m)
        {
            float fa = a;
            float fb = b;
            if (r >= m)
            {
                return (int)fb;
            }
            if (r <= 0)
            {
                return (int)fa;
            }
            float v = (float)((float)((float)(fb - fa) * (float)r) / (float)m) + fa;
            return (int)v;
        }

        /// <summary>
        /// 0x008b25c0:由高度 h 换 z 初速 v = ±√(|2 × g × h|),h 为负取负号(g 取 <see cref="Gravity"/> 的绝对值,证据 E-082)。
        /// </summary>
        public static double ZSpeed(int height, int gravity = Gravity)
        {
            long product = 2L * gravity * height;
            float v = (float)Math.Sqrt((double)Math.Abs(product));
            return height < 0 ? -v : v;
        }

        /// <summary>
        /// 倒地开始处理(0x008360e0 的 DOWN 分支,证据 E-083)。
        /// 类型 0:水平量与 [lift up] 取原值,[lift up] 直接当高度;类型 1 到 3:两者都按体重缩放,高度 h = 缩放后的 [lift up]² ÷ (2 × 重力)(符号随 [lift up]);
        /// 弹起类先存下 [lift up]([lift up] 在已躺下的状态里再被打弹起时取一半)与 [push aside],再把 [lift up] 换成 <see cref="SlamLift"/> 去缩放。
        /// 水平量除以 2;阶段 1 且已在倒地中(旧阶段 ≥ 1)高度除以 4,阶段 2 水平与高度都是 0;最后水平量 × [0.8, 1.2]、高度 × [0.9, 1.1] 随机。
        /// </summary>
        /// <param name="rollHorizontal">0 到 999 的随机数(水平量抖动)。</param>
        /// <param name="rollHeight">0 到 999 的随机数(高度抖动)。</param>
        public static KnockdownLaunch Begin(KnockdownRequest request, KnockdownBody body, int rollHorizontal, int rollHeight)
        {
            int stage = request.Stage;
            int type = request.Type;
            int gravity = body.Gravity > 0 ? body.Gravity : Gravity;
            int horizontal;
            int height;
            bool stores = false;
            int storedLift = 0;
            int storedPush = 0;

            if (type == 0)
            {
                horizontal = request.PushAside;
                height = request.LiftUp;
                if (stage == 1 && request.LiftUp <= 0 && body.Z <= 0)
                {
                    stage = 2;
                }
            }
            else
            {
                int lift = request.LiftUp;
                if (type == 2 || type == 3)
                {
                    stores = true;
                    storedLift = body.PrevDown && body.PrevStage >= 2 ? lift / 2 : lift;
                    storedPush = request.PushAside;
                    lift = SlamLift;
                }
                horizontal = WeightScale(request.PushAside, body.Weight);
                int scaled = WeightScale(lift, body.Weight);
                int square = unchecked(scaled * scaled);
                height = square / (2 * gravity);
                if (scaled < 0)
                {
                    height = -height;
                }
                if (stage == 1 && scaled > 0 && height < 1)
                {
                    height = 1;
                }
            }

            // 接续飞行的判断用的是请求里的阶段(类型 0 把阶段 1 改成 2 发生在这个判断之后,E-083)
            bool continuing = body.PrevDown && body.Z <= 0
                && (body.PrevType == 2 || body.PrevType == 3 || request.Stage > body.PrevStage);
            bool flip = false;
            if (!continuing && horizontal < 0)
            {
                flip = true;
                horizontal = -horizontal;
            }
            horizontal /= 2;

            if (stage == 1)
            {
                if (body.PrevDown && body.PrevStage >= 1)
                {
                    height /= 4;
                }
            }
            else if (stage == 2)
            {
                horizontal = 0;
                height = 0;
            }

            horizontal = Lerp((int)(horizontal * 0.8), (int)(horizontal * 1.2), rollHorizontal, 999);
            height = Lerp((int)(height * 0.9), (int)(height * 1.1), rollHeight, 999);
            return new KnockdownLaunch(stage, type, horizontal, flip, continuing, height, ZSpeed(height, gravity), stores, storedLift, storedPush);
        }

        /// <summary>
        /// 落地后的再请求(每帧函数 0x00834760,证据 E-082):阶段 0 里弹起类(类型 2、3)用存下的 [push aside]/[lift up] 再飞一次
        /// (类型 2 → 0,类型 3 → 1);其余(类型 0、1)颠一下:阶段 1、类型 0、水平量沿用上一次(已除过 2 并抖动过)、高度 = 最高点 ÷ 5;阶段 1 落地 → 阶段 2 躺下。
        /// </summary>
        /// <param name="maxHeight">这一段腾空的最高点(像素,取整)。</param>
        public static KnockdownRequest AfterLanding(int stage, int type, int horizontal, int maxHeight, int storedPush, int storedLift)
        {
            if (stage == 0)
            {
                if (type == 2 || type == 3)
                {
                    return new KnockdownRequest(0, type != 2 ? 1 : 0, storedPush, storedLift);
                }
                return new KnockdownRequest(1, 0, horizontal, maxHeight / 5);
            }
            return new KnockdownRequest(2, 0, 0, 0);
        }
    }
}
