using System;
using Dnf.Game;
using Dnf.Game.Act;

// 证据:E-165(静态读码,非 REA)。客户端被动对象的追踪:[homing] 块的加载 0x0129d570、拷进控制器 0x00805cd0、出生时开始 0x008b26d0、
// 每帧的追踪段 0x008b3847 起(在控制器更新 0x008b36d0 里,先走一步再转向)、定方向与速度 0x008b1b40、取方向角 0x007fa2c0、取追踪对象 0x00801150。
// 只有 [SET USE HOMING] 背后的开关函数 0x022c34cf 在受保护代码里,当黑盒:按"只改开关"建模(推断)。
namespace Dnf.Play
{
    /// <summary>追踪控制器操纵的"身体":被动对象(或测试里的替身)。位置按整数像素读(客户端的坐标就是整数)。</summary>
    public interface IHomingBody
    {
        double X { get; }

        double Y { get; }

        double Z { get; }

        /// <summary>当前水平(X)速度,像素每秒:方向角没设过时,按它反推出生方向。</summary>
        double VelocityX { get; }

        /// <summary>当前纵深(Y)速度,像素每秒(已经带着 Y 方向的压缩)。</summary>
        double VelocityY { get; }

        /// <summary>设 X、Y 两个方向的速度(像素每秒)。</summary>
        void SetPlanarVelocity(double vx, double vy);

        /// <summary>设 Z 方向(高度)的速度(像素每秒):[straight homing] 用。</summary>
        void SetVerticalVelocity(double vz);

        /// <summary>把对象摆到 (x, y)(Z 不动):[sync target move] 用。</summary>
        void MoveTo(double x, double y);

        /// <summary>设朝向(追踪时对象脸朝目标那一侧)。</summary>
        void SetHomingFacing(bool left);

        /// <summary>画面旋转(弧度):[sync animation rotation] 用。渲染这一版不画被动对象,先记在对象上。</summary>
        void SetRotation(float radians);
    }

    /// <summary>
    /// 被动对象的追踪控制器(M67,E-165):.obj 的 [homing] 块配置(<see cref="HomingDef"/>)+ 运行状态。
    /// 对象一出生就按块配置好、定初始方向(<see cref="Start"/>),之后每帧先让对象按速度走一步,再调 <see cref="Step"/> 转向:
    /// <list type="bullet">
    /// <item>[straight homing]:第一次检查时朝目标(三维)直线瞄一下,速度用当前速度,然后关掉追踪。</item>
    /// <item>[homing time]:启用后超过这个毫秒数就关掉追踪(关的那一帧还把这一步走完)。</item>
    /// <item>每过 [homing check gap] 毫秒检查一次:取目标位置(带 [homing direction]/[homing keep distance] 的横向偏移),
    ///   没有 [max rotation] 时一步把方向设成指向目标;有 [max rotation] 时只记下目标位置(带 [sync target move] 时对象还跟着目标的位移平移),
    ///   朝向在没有 [sync animation rotation] 且 [homing direction] 为 -1 时朝向目标那一侧。</item>
    /// <item>有 [max rotation] 时每帧都朝着记下的目标位置转:转角差在 [diff rotation] 以内才转,每帧最多转 dt × [max rotation] 度,够不到就直接指过去。</item>
    /// </list>
    /// 方向是整度的数学角(0 朝右、逆时针):X 速度 = 速度 × cos,Y 速度 = 速度 × sin × 0.29(Y 方向压缩,29 / 100);
    /// 每次定方向后速度按 [homing velocity] 的加速度增减(不低于 0)。
    /// </summary>
    public sealed class PassiveHoming
    {
        private static readonly float[] CosTable = BuildTable(Math.Cos);
        private static readonly float[] SinTable = BuildTable(Math.Sin);

        /// <summary>Y 方向压缩的分子(客户端全局 0x01a39e60 的值 29)与 0.01 的系数:Y 速度 = 速度 × sin × 29 × 0.01。</summary>
        public const int DepthScale = 29;

        private readonly HomingDef _def;
        private float _speed;
        private readonly float _acceleration;
        private int _nextCheckMs;
        private int _startMs;
        private float _lastX;
        private float _lastY;
        private float _angle = -1f;

        public PassiveHoming(HomingDef def)
        {
            _def = def ?? throw new ArgumentNullException(nameof(def));
            _speed = def.Speed;
            _acceleration = def.Acceleration;
        }

        public HomingDef Def => _def;

        /// <summary>追踪开着没有(客户端控制器的开关字节)。</summary>
        public bool On { get; private set; }

        /// <summary>追的对象;出生时取一次,没取到(或者死了)就不追。</summary>
        public IActEntity Target { get; private set; }

        /// <summary>当前速度(像素每秒,标量):每次定方向后按加速度变。</summary>
        public float Speed => _speed;

        /// <summary>当前方向角(度,小数);没设过是 -1。</summary>
        public float Angle => _angle;

        /// <summary>检查(重新瞄准)执行过多少次(审计与测试用)。</summary>
        public int Checks { get; private set; }

        /// <summary>定方向(设速度向量)执行过多少次。</summary>
        public int Steers { get; private set; }

        /// <summary>[SET USE HOMING] 的开关:只改开关,别的什么都不做(开关函数在受保护代码里,按这样建模是推断;没有目标的追踪开了也不动)。</summary>
        public void SetOn(bool on)
        {
            On = on;
        }

        /// <summary>
        /// 出生:开追踪、记下起始时刻、按 [init rotation] 定初始方向(朝左的对象再加 180 度)、取追踪对象。
        /// 客户端 0x008b26d0:[homing direction] 在 0 到 3 时用它代替对象的朝向(并把对象的朝向码设成它),否则用对象自己的朝向。
        /// </summary>
        public void Start(IHomingBody body, bool facingLeft, double nowMs, IActEntity target)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }
            On = true;
            _startMs = ToMs(nowMs);
            bool fixedDirection = _def.Direction >= 0 && _def.Direction < 4;
            bool left = fixedDirection ? _def.Direction == 0 : facingLeft;
            float angle = _def.InitRotation;
            if (left)
            {
                angle += 180f;
            }
            Target = target;
            SetHeading(body, angle, 0);
            if (fixedDirection && _def.Direction < 2)
            {
                body.SetHomingFacing(_def.Direction == 0);
            }
        }

        /// <summary>
        /// 追踪的一步(对象这一帧已经按旧速度走过了):<paramref name="dtMs"/> 是这一帧的毫秒数,<paramref name="nowMs"/> 是对象的年龄(毫秒)。
        /// 追踪关着、没有目标、目标已经死了都不动;速度保持原样。
        /// </summary>
        public void Step(IHomingBody body, double dtMs, double nowMs)
        {
            if (!On || Target == null || !Target.IsAlive)
            {
                return;
            }
            int now = ToMs(nowMs);
            if (_def.Straight)
            {
                Aim(body);
                On = false;
                return;
            }
            if (_def.TimeMs > 0 && now > _startMs + _def.TimeMs)
            {
                On = false;     // 关的这一帧后面的检查与转向照做(客户端先清开关字节,往下接着走)
            }
            if (now > _nextCheckMs)
            {
                _nextCheckMs = now + _def.CheckGapMs;
                Checks++;
                Check(body, dtMs);
            }
            if (_def.MaxRotation != 0f)
            {
                Rotate(body, dtMs);
            }
        }

        /// <summary>[straight homing]:从对象指向目标的三维向量,取单位向量乘当前速度,三个方向的速度一起设(没有 Y 压缩,也不过 sin/cos 表)。</summary>
        private void Aim(IHomingBody body)
        {
            float vx = (int)Target.X - (int)body.X;
            float vy = (int)Target.Y - (int)body.Y;
            float vz = (int)Target.Z - (int)body.Z;
            float length = (float)Math.Sqrt((double)vx * vx + (double)vy * vy + (double)vz * vz);
            if (!(length > 0f))
            {
                return;     // 对象正好压在目标上:没有方向可指。客户端的单位化(0x011a51a0 起)没有零长度保护,结果是非数;这里保持速度(偏离原版,罕见且无害)
            }
            float scale = _speed / length;
            body.SetPlanarVelocity(vx * scale, vy * scale);
            body.SetVerticalVelocity(vz * scale);
        }

        /// <summary>每隔 [homing check gap] 毫秒的那一次检查(客户端 0x008b3a29 起)。</summary>
        private void Check(IHomingBody body, double dtMs)
        {
            int targetX = (int)Target.X;
            int targetY = (int)Target.Y;
            if (_def.MaxRotation == 0f)
            {
                SetHeading(body, Heading((int)body.X, (int)body.Y, AimX(targetX), targetY), dtMs);
            }
            else
            {
                if (_def.SyncTargetMove && (_lastX != 0f || _lastY != 0f))
                {
                    // 目标自上次检查走了多少,对象也平移多少(两个差都先取整数;位置只改 X、Y)
                    int dx = (int)(targetX - _lastX);
                    int dy = (int)(targetY - _lastY);
                    body.MoveTo((int)body.X + dx, (int)body.Y + dy);
                }
                _lastX = targetX;
                _lastY = targetY;
            }
            if (!_def.SyncAnimationRotation && _def.Direction == -1)
            {
                body.SetHomingFacing(!((int)body.X < (int)Target.X));     // 对象在目标左边(严格小于)才朝右,其余朝左
            }
        }

        /// <summary>瞄准点的 X:[homing direction] 为 0 时在目标 x 上加 [homing keep distance],为 1 时减,其余原样。</summary>
        private int AimX(int targetX)
        {
            if (_def.Direction == 0)
            {
                return targetX + _def.KeepDistance;
            }
            if (_def.Direction == 1)
            {
                return targetX - _def.KeepDistance;
            }
            return targetX;
        }

        /// <summary>有 [max rotation] 时每帧的转向(客户端 0x008b3c58 起):朝上一次检查记下的目标位置转。</summary>
        private void Rotate(IHomingBody body, double dtMs)
        {
            int target = Heading((int)body.X, (int)body.Y, AimX((int)_lastX), (int)_lastY);
            if (_angle < 0f)
            {
                _angle = Heading(0, 0, (int)body.VelocityX, (int)body.VelocityY);     // 没设过方向(小于 0):按当前速度反推,纵深差照常乘 100 除以 29 还原压缩
            }
            int current = ((int)_angle + 360) % 360;
            if (target == current)
            {
                SetHeading(body, target, dtMs);
                return;
            }
            int diff = target - current;
            int gap;
            bool counterClockwise;
            if (diff > 180)
            {
                gap = 360 - diff;
                counterClockwise = false;
            }
            else if (diff > 0)
            {
                gap = diff;
                counterClockwise = true;
            }
            else if (diff <= -180)
            {
                gap = diff + 360;
                counterClockwise = true;
            }
            else
            {
                gap = -diff;
                counterClockwise = false;
            }
            if (_def.DiffRotation <= gap)
            {
                SetHeading(body, current, dtMs);     // 目标在追踪锥外:保持当前整度方向(速度向量照常重设一遍)
                return;
            }
            float step = (float)dtMs * _def.MaxRotation * 0.001f;
            if (step > gap)
            {
                SetHeading(body, target, dtMs);      // 这一帧够得到:直接指过去
                return;
            }
            if (counterClockwise)
            {
                _angle += step;
                if (_angle > 360f)
                {
                    _angle -= 360f;
                }
            }
            else
            {
                _angle -= step;
                if (_angle < 0f)
                {
                    _angle += 360f;
                }
            }
            SetHeading(body, _angle, dtMs);
        }

        /// <summary>
        /// 定方向并设速度(客户端 0x008b1b40):方向角取整数度查表,X 速度 = 速度 × cos,Y 速度 = 速度 × sin × 29 × 0.01;
        /// 然后速度加上 dt × 加速度 × 0.001(不低于 0);带 [sync animation rotation] 的对象画面转到 ((度 + 180) % 360) / 180 × 3.14 弧度。
        /// </summary>
        public void SetHeading(IHomingBody body, float angle, double dtMs)
        {
            _angle = angle;
            int degrees = (int)angle;
            int index = ((degrees % 360) + 360) % 360;
            body.SetPlanarVelocity(
                (float)((double)_speed * CosTable[index]),
                (float)((double)_speed * SinTable[index] * DepthScale * 0.009999999776482582));
            _speed = (float)dtMs * _acceleration * 0.001f + _speed;
            if (_speed < 0f)
            {
                _speed = 0f;
            }
            Steers++;
            if (_def.SyncAnimationRotation)
            {
                // 对整度数加 180 再取余(负数不修正,与客户端同),乘 1/180 与 3.14(客户端写死的近似 π)
                body.SetRotation((float)(((degrees + 180) % 360) * 0.0055555556900799274f * 3.14));
            }
        }

        /// <summary>
        /// 从 (fromX, fromY) 指向 (toX, toY) 的整度方向角 [0, 360)(客户端 0x007fa2c0,调用时纵深差先乘 100 除以 29 还原 Y 压缩):
        /// 数学角(0 朝右、逆时针);atan2 的结果先落到单精度,再乘 180/π 截成整数。
        /// </summary>
        public static int Heading(int fromX, int fromY, int toX, int toY)
        {
            float dx = toX - fromX;
            float dy = toY - fromY;
            dy = dy * 100f / DepthScale;
            float radians = (float)Math.Atan2(dy, dx);
            int negated = (int)((double)radians * -57.29577951308232);
            return (360 - negated) % 360;
        }

        private static int ToMs(double ms)
        {
            return ms >= int.MaxValue ? int.MaxValue : (int)ms;
        }

        private static float[] BuildTable(Func<double, double> function)
        {
            var table = new float[360];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (float)function(i * 0.017453292519943295);
            }
            return table;
        }
    }
}
