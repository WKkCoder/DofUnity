using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:客户端粒子发射器与运动学逆向结论（0x00e581e0 各轴运动、0x00e58b00 旋转/缩放/镜像/重力/落地/透明度、
// 0x00e59030 有效时长、0x00e59570 发射区与随机范围、0x00e59b20 发射累积与插值、0x00e57090 对象挑选、
// 0x00e50e60 三步 LCG 随机数、0x007fcbb0 / 0x0108f770 透明乘算与帧插值）。
// 本文件在代码中明确区分【读码定案】与【工程选型】:
// - 纯函数、确定性、无 Unity 依赖、无文件 I/O。
namespace Dnf.Render
{
    public enum ParticleSimMoveType
    {
        Stop = 0,
        StraightLine = 1,
        Sin = 2,
        Cos = 3,
    }

    public enum ParticleSimChanging
    {
        KeepChanging = 0,
        KeepLastValue = 1,
        PingPong = 2,
    }

    public enum ParticleSimLanding
    {
        Pierce = 0,
        Slide = 1,
        Stop = 2,
        Vanish = 3,
    }

    public enum ParticleSimRotationRange
    {
        Rectangle = 0,
        Circle = 1,
    }

    public enum ParticleSimHowToCreate
    {
        InOrder = 0,
        Randomize = 1,
    }

    public enum ParticleSimObjectKind
    {
        Unknown = 0,
        Animation = 2,
        PassiveObject = 33,
    }

    public enum ParticleSimLayer
    {
        Close = 0,
        Normal = 1,
        Bottom = 2,
        CloseBack = 3,
        MiddleBack = 4,
        DistantBack = 5,
        Door = 6,
    }

    public readonly struct ParticleSimVec3 : IEquatable<ParticleSimVec3>
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public ParticleSimVec3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(ParticleSimVec3 other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) => obj is ParticleSimVec3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = X;
                h = (h * 397) ^ Y;
                h = (h * 397) ^ Z;
                return h;
            }
        }
    }

    public readonly struct ParticleSimAxis
    {
        public ParticleSimMoveType Type { get; }
        public int Var1A { get; }
        public int Var1B { get; }
        public int Var2A { get; }
        public int Var2B { get; }

        public ParticleSimAxis(ParticleSimMoveType type, int var1A, int var1B, int var2A, int var2B)
        {
            Type = type;
            Var1A = var1A;
            Var1B = var1B;
            Var2A = var2A;
            Var2B = var2B;
        }
    }

    public readonly struct ParticleSimObject
    {
        public string Path { get; }
        public int Arg { get; }

        public ParticleSimObject(string path, int arg)
        {
            Path = path ?? string.Empty;
            Arg = arg;
        }
    }

    /// <summary>Render 层粒子的纯数据输入规格（无脚本解析与 I/O）。</summary>
    public sealed class ParticleSimDef
    {
        public string Path { get; }
        public ParticleSimVec3 CreationPos { get; }
        public ParticleSimAxis X { get; }
        public ParticleSimAxis Y { get; }
        public ParticleSimAxis Z { get; }
        public int ChangingTermMs { get; }
        public ParticleSimChanging ChangingType { get; }
        public int Gravity { get; }
        public ParticleSimVec3 RotationAngle { get; }
        public int LifeTimeMs { get; }
        public ParticleSimLanding Landing { get; }
        public int TransparentStart { get; }
        public int TransparentEnd { get; }
        public float CreationFrequency { get; }
        public int MaximumCreateNumber { get; }
        public int MaximumCreateTimeMs { get; }
        public ParticleSimVec3 CreateAreaSize { get; }
        public ParticleSimVec3 CreateAreaRotationAngle { get; }
        public ParticleSimRotationRange RotationAngleRangeType { get; }
        public int RotationAngleVar0 { get; }
        public int RotationAngleVar1 { get; }
        public int RandomRange { get; }
        public bool CreatePosCorrect { get; }
        public int DisplayEffectRateLow { get; }
        public int DisplayEffectRateMid { get; }
        public int DisplayEffectRateHigh { get; }
        public ParticleSimHowToCreate HowToCreateObject { get; }
        public ParticleSimObjectKind ObjectType { get; }
        public ParticleSimLayer Layer { get; }
        public IReadOnlyList<ParticleSimObject> Objects { get; }

        public ParticleSimDef(
            string path = "",
            ParticleSimVec3 creationPos = default,
            ParticleSimAxis x = default,
            ParticleSimAxis y = default,
            ParticleSimAxis z = default,
            int changingTermMs = 0,
            ParticleSimChanging changingType = ParticleSimChanging.KeepChanging,
            int gravity = 0,
            ParticleSimVec3 rotationAngle = default,
            int lifeTimeMs = 0,
            ParticleSimLanding landing = ParticleSimLanding.Pierce,
            int transparentStart = 100,
            int transparentEnd = 100,
            float creationFrequency = 0f,
            int maximumCreateNumber = 0,
            int maximumCreateTimeMs = 0,
            ParticleSimVec3 createAreaSize = default,
            ParticleSimVec3 createAreaRotationAngle = default,
            ParticleSimRotationRange rotationAngleRangeType = ParticleSimRotationRange.Rectangle,
            int rotationAngleVar0 = 0,
            int rotationAngleVar1 = 0,
            int randomRange = 0,
            bool createPosCorrect = false,
            int displayEffectRateLow = 100,
            int displayEffectRateMid = 100,
            int displayEffectRateHigh = 100,
            ParticleSimHowToCreate howToCreateObject = ParticleSimHowToCreate.Randomize,
            ParticleSimObjectKind objectType = ParticleSimObjectKind.Animation,
            ParticleSimLayer layer = ParticleSimLayer.Normal,
            IReadOnlyList<ParticleSimObject> objects = null)
        {
            Path = path ?? string.Empty;
            CreationPos = creationPos;
            X = x;
            Y = y;
            Z = z;
            ChangingTermMs = changingTermMs;
            ChangingType = changingType;
            Gravity = gravity;
            RotationAngle = rotationAngle;
            LifeTimeMs = lifeTimeMs;
            Landing = landing;
            TransparentStart = transparentStart;
            TransparentEnd = transparentEnd;
            CreationFrequency = creationFrequency;
            MaximumCreateNumber = maximumCreateNumber;
            MaximumCreateTimeMs = maximumCreateTimeMs;
            CreateAreaSize = createAreaSize;
            CreateAreaRotationAngle = createAreaRotationAngle;
            RotationAngleRangeType = rotationAngleRangeType;
            RotationAngleVar0 = rotationAngleVar0;
            RotationAngleVar1 = rotationAngleVar1;
            RandomRange = randomRange;
            CreatePosCorrect = createPosCorrect;
            DisplayEffectRateLow = displayEffectRateLow;
            DisplayEffectRateMid = displayEffectRateMid;
            DisplayEffectRateHigh = displayEffectRateHigh;
            HowToCreateObject = howToCreateObject;
            ObjectType = objectType;
            Layer = layer;
            Objects = objects ?? Array.Empty<ParticleSimObject>();
        }

        public int DisplayEffectRate(int quality)
        {
            switch (quality)
            {
                case 0: return DisplayEffectRateLow;
                case 1: return DisplayEffectRateMid;
                default: return DisplayEffectRateHigh;
            }
        }
    }

    /// <summary>单步仿真输出的一颗粒子精灵快照。</summary>
    public sealed class ParticleSprite
    {
        public int ParticleId { get; }
        public int AgeMs { get; }
        public int WorldX { get; }
        public int WorldY { get; }
        public int WorldZ { get; }
        public bool Mirrored { get; }
        public int AlphaPercent { get; }
        public string ObjectPath { get; }
        public int ObjectArg { get; }
        public string ImagePath { get; }
        public int FrameIndex { get; }
        public int FrameOffsetX { get; }
        public int FrameOffsetY { get; }
        public float ScaleX { get; }
        public float ScaleY { get; }
        public float RotationRadians { get; }
        public byte ColorR { get; }
        public byte ColorG { get; }
        public byte ColorB { get; }
        public int Alpha { get; }
        public int GraphicEffect { get; }
        public SpriteBlend Blend { get; }
        public ParticleSimLayer Layer { get; }

        public ParticleSprite(
            int particleId,
            int ageMs,
            int worldX,
            int worldY,
            int worldZ,
            bool mirrored,
            int alphaPercent,
            string objectPath,
            int objectArg,
            string imagePath,
            int frameIndex,
            int frameOffsetX,
            int frameOffsetY,
            float scaleX,
            float scaleY,
            float rotationRadians,
            byte colorR,
            byte colorG,
            byte colorB,
            int alpha,
            int graphicEffect,
            SpriteBlend blend,
            ParticleSimLayer layer)
        {
            ParticleId = particleId;
            AgeMs = ageMs;
            WorldX = worldX;
            WorldY = worldY;
            WorldZ = worldZ;
            Mirrored = mirrored;
            AlphaPercent = alphaPercent;
            ObjectPath = objectPath;
            ObjectArg = objectArg;
            ImagePath = imagePath;
            FrameIndex = frameIndex;
            FrameOffsetX = frameOffsetX;
            FrameOffsetY = frameOffsetY;
            ScaleX = scaleX;
            ScaleY = scaleY;
            RotationRadians = rotationRadians;
            ColorR = colorR;
            ColorG = colorG;
            ColorB = colorB;
            Alpha = alpha;
            GraphicEffect = graphicEffect;
            Blend = blend;
            Layer = layer;
        }
    }

    /// <summary>某一步仿真时刻及其全部存活粒子。</summary>
    public sealed class ParticleStepFrame
    {
        public int TimeMs { get; }
        public IReadOnlyList<ParticleSprite> Sprites { get; }

        public ParticleStepFrame(int timeMs, IReadOnlyList<ParticleSprite> sprites)
        {
            TimeMs = timeMs;
            Sprites = sprites;
        }
    }

    /// <summary>多步仿真结果。</summary>
    public sealed class ParticleSimulationResult
    {
        public IReadOnlyList<ParticleStepFrame> Steps { get; }

        public ParticleSimulationResult(IReadOnlyList<ParticleStepFrame> steps)
        {
            Steps = steps;
        }
    }

    /// <summary>
    /// 粒子发射器确定性状态机（步进式 + 静态批处理入口）。
    /// - 【读码定案】:运动积分公式(0x00e581e0)、旋转平面与顺序(0x00e58b00/0x00e58760)、2.5D 纵深压缩 0.29(0x01a39e60)、
    ///   重力 -0.5e-6*g*t^2、四种落地策略、transparent 百分比乘算(0x007fcbb0)、accum=1.0 初值(0x00e58e90)、
    ///   effectiveMaxTime(0x00e59030)、圆形 CDF 采样(0x00e591f0)、random range 扰动(0x00e59570)、create pos correct 插值(0x00e59b20)。
    /// - 【工程选型】:确定性 LCG 始终使用显式传入种子（不读系统时钟 timeGetTime）；未接入屏幕裁剪/帧率动态限流（throttle=1.0f）；
    ///   除零（周期为 0）安全兜底返回 0；缩放与旋转的 CPU 参考渲染采用围绕精灵中心的双线性逆变换。
    /// </summary>
    public sealed class ParticleEmitter
    {
        private const float MsToSeconds = 0.001f;
        private const float Half = 0.5f;
        private const float GravityScale = -0.5e-6f;
        private const float PercentToUnit = 0.01f;
        private const float FloatEps = 1.1920929e-7f;
        private const double DegToRad = Math.PI / 180.0;
        private const double TwoPi = Math.PI * 2.0;
        private const int MaxSpawnPerStep = 50;
        private const int ScaleXPercent = 100;
        private const int ScaleYPercent = 29;
        private const int ScaleZPercent = 100;

        // 【读码定案】0x00e591f0: 预先构造 0..180 度的 sin(k)*1000 累加表用于 [circle] 圆锥分布采样
        private static readonly int[] CircleCdf = BuildCircleCdf();

        private readonly ParticleSimDef _def;
        private readonly int _quality;
        private readonly Func<string, AniFile> _resolveAni;
        private readonly Dictionary<string, AniTimeline> _timelines = new Dictionary<string, AniTimeline>(StringComparer.Ordinal);
        private readonly List<ParticleInstance> _particles = new List<ParticleInstance>();

        private uint _rngState;
        private int _nextParticleId;
        private int _elapsedMs;
        private int _prevTimeMs;
        private float _accum;
        private bool _finished;
        private ParticleSimVec3 _prevEmitterPos;
        private bool _hasStepped;

        public ParticleEmitter(
            ParticleSimDef def,
            uint seed,
            ParticleSimVec3 emitterPos = default,
            int quality = 2,
            Func<string, AniFile> resolveAni = null)
        {
            _def = def ?? throw new ArgumentNullException(nameof(def));
            _quality = quality < 0 ? 0 : (quality > 2 ? 2 : quality);
            _resolveAni = resolveAni;
            _rngState = seed;
            _accum = 1.0f; // 【读码定案】0x00e58e90 Restart 预置 1.0f
            _prevEmitterPos = emitterPos;
        }

        public bool IsEmissionFinished => _finished;

        public bool IsFinished => _finished && _particles.Count == 0;

        public int LiveCount => _particles.Count;

        public int ElapsedMs => _elapsedMs;

        /// <summary>
        /// 立即停止后续发射，仅继续推进已经存活的粒子直至全部死亡。
        /// 对应客户端 .act [PARTICLE] 执行器（0x010c6138）单次 Emit 后立即析构发射器的语义。
        /// </summary>
        public void StopEmission()
        {
            _finished = true;
        }

        /// <summary>推进 <paramref name="deltaMs"/> 毫秒并返回本步所有存活粒子精灵。</summary>
        public IReadOnlyList<ParticleSprite> Step(int deltaMs, ParticleSimVec3 emitterPos, bool facingLeft)
        {
            if (deltaMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deltaMs));
            }

            if (!_hasStepped)
            {
                _prevEmitterPos = emitterPos;
                _hasStepped = true;
            }

            // 1. 推进已有存活粒子
            if (deltaMs > 0 && _particles.Count > 0)
            {
                for (int i = _particles.Count - 1; i >= 0; i--)
                {
                    ParticleInstance p = _particles[i];
                    p.AgeMs += deltaMs;
                    UpdateParticle(p);
                    if (!p.Alive)
                    {
                        _particles.RemoveAt(i);
                    }
                }
            }

            // 2. 推进发射器时间与发射（0x00e57090 + 0x00e59b20）
            _elapsedMs += deltaMs;
            Emit(emitterPos, facingLeft);
            _prevEmitterPos = emitterPos;

            // 3. 构建当前存活粒子精灵快照
            if (_particles.Count == 0)
            {
                return Array.Empty<ParticleSprite>();
            }
            var result = new ParticleSprite[_particles.Count];
            for (int i = 0; i < _particles.Count; i++)
            {
                result[i] = BuildSprite(_particles[i]);
            }
            return result;
        }

        private void Emit(ParticleSimVec3 emitterPos, bool facingLeft)
        {
            if (_finished)
            {
                return;
            }
            if (_def.ObjectType != ParticleSimObjectKind.Animation || _def.Objects.Count == 0)
            {
                return;
            }

            float freq = _def.CreationFrequency;
            float absFreq = Math.Abs(freq);
            if (absFreq * FloatEps >= absFreq)
            {
                // freq == 0.0f（0x00e59b20 置 finished=1）
                _finished = true;
                return;
            }
            if (_def.DisplayEffectRate(_quality) == 0)
            {
                return;
            }

            bool isBurst = Math.Abs(freq - (-1.0f)) <= absFreq * FloatEps;
            int spawnCount;
            int stepSpanMs = 0;

            if (isBurst)
            {
                _finished = true;
                float rawBurst = _def.MaximumCreateNumber * _def.DisplayEffectRate(_quality) * PercentToUnit;
                spawnCount = (int)rawBurst;
                if (spawnCount <= 0)
                {
                    return;
                }
                if (spawnCount > MaxSpawnPerStep)
                {
                    spawnCount = MaxSpawnPerStep;
                }
            }
            else
            {
                if (freq < 0f)
                {
                    // 非 -1 的负频率（0x00e59030 返回 0，累积为负不生成）
                    return;
                }

                int curTime = _elapsedMs;
                if (curTime != _prevTimeMs)
                {
                    int effMax = EffectiveMaxTimeMs();
                    if (effMax != 0 && curTime >= effMax)
                    {
                        _finished = true;
                        curTime = effMax;
                    }
                    stepSpanMs = curTime - _prevTimeMs;
                    _prevTimeMs = curTime;
                    _accum += stepSpanMs * freq * MsToSeconds * _def.DisplayEffectRate(_quality) * PercentToUnit;
                }

                spawnCount = (int)_accum;
                if (spawnCount <= 0)
                {
                    return;
                }
                _accum -= spawnCount;
                if (spawnCount > MaxSpawnPerStep)
                {
                    spawnCount = MaxSpawnPerStep;
                }
            }

            int objCount = _def.Objects.Count;
            for (int i = 0; i < spawnCount; i++)
            {
                int birthAgeMs = isBurst ? _elapsedMs : (int)((float)i * stepSpanMs / spawnCount);
                if (_def.LifeTimeMs != 0 && birthAgeMs >= _def.LifeTimeMs)
                {
                    continue;
                }

                // 【读码定案】0x00e57090: 对象选择（InOrder 在当前发射批内钳位到末项；Randomize 取模）
                int objIndex = _def.HowToCreateObject == ParticleSimHowToCreate.InOrder
                    ? (i < objCount - 1 ? i : objCount - 1)
                    : NextRandom(objCount);
                ParticleSimObject obj = _def.Objects[objIndex];

                ParticleInstance p = CreateParticleInstance(emitterPos, facingLeft, obj);

                // 【读码定案】0x00e59b20: [create pos correct] 在当前与上帧发射器位置之间插值起步坐标
                if (_def.CreatePosCorrect && !isBurst)
                {
                    if (emitterPos.X != _prevEmitterPos.X)
                    {
                        p.StartX = IntLerp(emitterPos.X, _prevEmitterPos.X, i + 1, spawnCount);
                    }
                    if (emitterPos.Y != _prevEmitterPos.Y)
                    {
                        p.StartY = IntLerp(emitterPos.Y, _prevEmitterPos.Y, i + 1, spawnCount);
                    }
                    if (emitterPos.Z != _prevEmitterPos.Z)
                    {
                        p.StartZ = IntLerp(emitterPos.Z, _prevEmitterPos.Z, i + 1, spawnCount);
                    }
                }

                // 【读码定案】0x00e58150: 初始化 pos = start, alpha = transparentStart, 并以 birthAgeMs 更新首帧
                int clampedBirth = birthAgeMs;
                if (p.LifeTimeMs != 0 && p.LifeTimeMs <= clampedBirth)
                {
                    p.Alive = false;
                    clampedBirth = p.LifeTimeMs;
                }
                p.AgeMs = clampedBirth;
                p.PosX = p.StartX;
                p.PosY = p.StartY;
                p.PosZ = p.StartZ;
                p.AlphaPercent = p.TransparentStart;

                UpdateParticle(p);
                if (p.Alive)
                {
                    _particles.Add(p);
                }
            }
        }

        private int EffectiveMaxTimeMs()
        {
            if (_def.CreationFrequency <= 0f)
            {
                return 0;
            }
            int fromTime = _def.MaximumCreateTimeMs == 0 ? 0 : _def.MaximumCreateTimeMs - 1;
            int fromNum = _def.MaximumCreateNumber == 0
                ? 0
                : (int)((_def.MaximumCreateNumber * 1000L - 1000L) / _def.CreationFrequency);

            if (fromTime != 0)
            {
                if (fromNum == 0)
                {
                    return fromTime;
                }
                return fromTime <= fromNum ? fromTime : fromNum;
            }
            return fromNum;
        }

        private ParticleInstance CreateParticleInstance(ParticleSimVec3 emitterPos, bool facingLeft, ParticleSimObject obj)
        {
            // 1. 发射区域随机偏移（0x00e59570）
            float ax = SampleAreaAxis(_def.CreateAreaSize.X);
            float ay = SampleAreaAxis(_def.CreateAreaSize.Y);
            float az = SampleAreaAxis(_def.CreateAreaSize.Z);

            ApplyRotation3D(
                ref ax,
                ref ay,
                ref az,
                _def.CreateAreaRotationAngle.X,
                _def.CreateAreaRotationAngle.Y,
                _def.CreateAreaRotationAngle.Z);

            int startX = emitterPos.X + _def.CreationPos.X + (facingLeft ? -((int)ax * ScaleXPercent) / 100 : ((int)ax * ScaleXPercent) / 100);
            int startY = emitterPos.Y + _def.CreationPos.Y + ((int)ay * ScaleYPercent) / 100;
            int startZ = emitterPos.Z + _def.CreationPos.Z + ((int)az * ScaleZPercent) / 100;

            // 2. 发射方向角分布（0x00e59570）
            int rotX = _def.RotationAngle.X;
            int rotY = _def.RotationAngle.Y;
            int rotZ = _def.RotationAngle.Z;

            if (_def.RotationAngleRangeType == ParticleSimRotationRange.Rectangle)
            {
                if (_def.RotationAngleVar0 > 0)
                {
                    rotX += NextRandom(_def.RotationAngleVar0 + 1) - _def.RotationAngleVar0 / 2;
                }
                if (_def.RotationAngleVar1 > 0)
                {
                    rotY += NextRandom(_def.RotationAngleVar1 + 1) - _def.RotationAngleVar1 / 2;
                }
            }
            else if (_def.RotationAngleRangeType == ParticleSimRotationRange.Circle)
            {
                int deg = NextRandom(360);
                rotZ = 90 - deg;
                int halfVar0 = _def.RotationAngleVar0 / 2;
                if (halfVar0 > 0)
                {
                    int clampedHalf = halfVar0 > 180 ? 180 : halfVar0;
                    int totalWeight = CircleCdf[clampedHalf];
                    int target = NextRandom(totalWeight + 1);
                    int radius = LowerBound(CircleCdf, clampedHalf, target);
                    double rad = deg * DegToRad;
                    rotX += (int)(radius * Math.Cos(rad));
                    rotY += (int)(radius * Math.Sin(rad));
                }
            }

            // 3. [random range] 扰动（0x00e59570）
            ParticleSimAxis xAxis = _def.X;
            ParticleSimAxis yAxis = _def.Y;
            ParticleSimAxis zAxis = _def.Z;
            int changingTerm = _def.ChangingTermMs;
            int lifeTime = _def.LifeTimeMs;

            int rr = _def.RandomRange;
            if (rr > 0)
            {
                int permAmp = SamplePermille(rr);
                int permPeriod = SamplePermille(rr);
                xAxis = PerturbAxis(xAxis, rr, permAmp, permPeriod);
                yAxis = PerturbAxis(yAxis, rr, permAmp, permPeriod);
                zAxis = PerturbAxis(zAxis, rr, permAmp, permPeriod);
                changingTerm = ScalePermille(changingTerm, SamplePermille(rr));
                if (lifeTime != 0)
                {
                    lifeTime = ScalePermille(lifeTime, SamplePermille(rr));
                    if (lifeTime < 1)
                    {
                        lifeTime = 1;
                    }
                }
            }

            return new ParticleInstance(
                _nextParticleId++,
                startX,
                startY,
                startZ,
                xAxis,
                yAxis,
                zAxis,
                changingTerm,
                _def.ChangingType,
                _def.Gravity,
                rotX,
                rotY,
                rotZ,
                facingLeft,
                lifeTime,
                _def.Landing,
                _def.TransparentStart,
                _def.TransparentEnd,
                _def.Layer,
                obj.Path,
                obj.Arg);
        }

        private float SampleAreaAxis(int size)
        {
            if (size <= 0)
            {
                return 0f;
            }
            return NextRandom(size + 1) - size / 2;
        }

        private int SamplePermille(int randomRange)
        {
            int span = randomRange * 10 + 1;
            return NextRandom(span) - randomRange * 5 + 1000;
        }

        private ParticleSimAxis PerturbAxis(ParticleSimAxis axis, int rr, int permAmp, int permPeriod)
        {
            if (axis.Type == ParticleSimMoveType.StraightLine)
            {
                int p = SamplePermille(rr);
                return new ParticleSimAxis(
                    axis.Type,
                    ScalePermille(axis.Var1A, p),
                    ScalePermille(axis.Var1B, p),
                    axis.Var2A,
                    axis.Var2B);
            }
            if (axis.Type == ParticleSimMoveType.Sin || axis.Type == ParticleSimMoveType.Cos)
            {
                return new ParticleSimAxis(
                    axis.Type,
                    ScalePermille(axis.Var1A, permAmp),
                    ScalePermille(axis.Var1B, permAmp),
                    ScalePermille(axis.Var2A, permPeriod),
                    ScalePermille(axis.Var2B, permPeriod));
            }
            return axis;
        }

        private static int ScalePermille(int value, int permille)
        {
            long scaled = ((long)value * permille) / 1000L;
            if (scaled > int.MaxValue) return int.MaxValue;
            if (scaled < int.MinValue) return int.MinValue;
            return (int)scaled;
        }

        private int NextRandom(int bound)
        {
            if (bound <= 1)
            {
                return 0;
            }
            // 【工程选型】确定性 31 位三步 LCG（同 0x00e50e60 / SdcRandom 结构），不读系统时钟
            unchecked
            {
                uint x1 = _rngState * 0x41c64e6dU + 0x3039U;
                uint x2 = x1 * 0x41c64e6dU + 0x3039U;
                uint x3 = x2 * 0x41c64e6dU + 0x3039U;
                _rngState = x3;
                uint r = ((x3 >> 16) & 0x3ffU) | (((x2 >> 16) & 0x3ffU) << 10) | (((x1 >> 16) & 0x7ffU) << 20);
                return (int)(r % (uint)bound);
            }
        }

        /// <summary>按 0x00e58b00 规则推进单个粒子在当前 <see cref="ParticleInstance.AgeMs"/> 下的位置、落地状态与透明度。</summary>
        private static void UpdateParticle(ParticleInstance p)
        {
            int t = p.AgeMs;
            if (p.LifeTimeMs != 0 && t >= p.LifeTimeMs && p.Alive)
            {
                p.Alive = false;
            }

            float dx = ComputeAxisDisplacement(p.X, p.ChangingTermMs, p.ChangingType, t);
            float dy = ComputeAxisDisplacement(p.Y, p.ChangingTermMs, p.ChangingType, t);
            float dz = ComputeAxisDisplacement(p.Z, p.ChangingTermMs, p.ChangingType, t);

            if (p.Landing == ParticleSimLanding.Stop && p.PosZ == 0)
            {
                return;
            }

            ApplyRotation3D(ref dx, ref dy, ref dz, p.RotX, p.RotY, p.RotZ);

            p.PosX = p.StartX + (p.Mirror ? -(int)(ScaleXPercent * dx * PercentToUnit) : (int)(ScaleXPercent * dx * PercentToUnit));
            p.PosY = p.StartY + (int)(ScaleYPercent * dy * PercentToUnit);
            p.PosZ = p.StartZ + (int)(ScaleZPercent * dz * PercentToUnit);

            p.PosZ += (int)(p.Gravity * (float)t * (float)t * GravityScale);
            if (p.PosZ < 0)
            {
                if (p.Landing == ParticleSimLanding.Slide || p.Landing == ParticleSimLanding.Stop)
                {
                    p.PosZ = 0;
                }
                else if (p.Landing == ParticleSimLanding.Vanish)
                {
                    p.Alive = false;
                }
            }

            if (p.LifeTimeMs != 0)
            {
                float ratio = (float)t / p.LifeTimeMs;
                if (ratio < 0f) ratio = 0f;
                if (ratio > 1f) ratio = 1f;
                p.AlphaPercent = (int)(p.TransparentStart + (p.TransparentEnd - p.TransparentStart) * ratio);
            }
        }

        /// <summary>0x00e581e0: 单轴位移公式（Stop / StraightLine / Sin / Cos + 三种 ChangingType）。</summary>
        internal static float ComputeAxisDisplacement(ParticleSimAxis axis, int changingTermMs, ParticleSimChanging changingType, int t)
        {
            if (axis.Type == ParticleSimMoveType.Stop)
            {
                return 0f;
            }
            if (axis.Type == ParticleSimMoveType.StraightLine)
            {
                if (changingTermMs == 0)
                {
                    return (float)axis.Var1A * t * MsToSeconds;
                }
                if (changingType == ParticleSimChanging.KeepLastValue && t >= changingTermMs)
                {
                    return (axis.Var1A + (float)axis.Var1B) * Half * changingTermMs * MsToSeconds
                        + (t - changingTermMs) * (float)axis.Var1B * MsToSeconds;
                }
                if (changingType == ParticleSimChanging.PingPong)
                {
                    int c = t / changingTermMs;
                    int r = t % changingTermMs;
                    float full = c > 0
                        ? ((float)axis.Var1B + axis.Var1A) * Half * changingTermMs * MsToSeconds * c
                        : 0f;
                    bool even = (c & 1) == 0;
                    float a = even ? axis.Var1A : axis.Var1B;
                    float b = even ? axis.Var1B : axis.Var1A;
                    float vEnd = a + ((b - a) * r) / changingTermMs;
                    return full + (a + vEnd) * Half * r * MsToSeconds;
                }
                // KeepChanging 或 KeepLastValue 且 t < changingTermMs
                float v1 = axis.Var1A;
                float vEndLinear = v1 + (((float)axis.Var1B - v1) * t) / changingTermMs;
                return (v1 + vEndLinear) * Half * t * MsToSeconds;
            }

            // Sin (2) / Cos (3)
            double theta;
            float amp;
            if (changingTermMs == 0)
            {
                amp = axis.Var1A;
                theta = axis.Var2A == 0 ? 0.0 : (t * TwoPi) / axis.Var2A;
            }
            else if (changingType == ParticleSimChanging.KeepLastValue && t >= changingTermMs)
            {
                float avgPeriod = (axis.Var2A + (float)axis.Var2B) * Half;
                double part1 = SafeDiv(changingTermMs * TwoPi, avgPeriod);
                double part2 = axis.Var2B == 0 ? 0.0 : ((t - changingTermMs) * TwoPi) / axis.Var2B;
                theta = part1 + part2;
                amp = axis.Var1B;
            }
            else if (changingType == ParticleSimChanging.PingPong)
            {
                int c = t / changingTermMs;
                int r = t % changingTermMs;
                float avgPeriod = (axis.Var2A + (float)axis.Var2B) * Half;
                double baseTheta = c > 0 ? SafeDiv(changingTermMs * TwoPi, avgPeriod) * c : 0.0;
                bool even = (c & 1) == 0;
                float pStart = even ? axis.Var2A : axis.Var2B;
                float pEnd = even ? axis.Var2B : axis.Var2A;
                float aStart = even ? axis.Var1A : axis.Var1B;
                float aEnd = even ? axis.Var1B : axis.Var1A;
                float pMid = (pStart + (pStart + ((pEnd - pStart) * r) / changingTermMs)) * Half;
                theta = baseTheta + SafeDiv(r * TwoPi, pMid);
                amp = aStart + ((aEnd - aStart) * r) / changingTermMs;
            }
            else
            {
                float pStart = axis.Var2A;
                float pMid = (pStart + (pStart + (((float)axis.Var2B - pStart) * t) / changingTermMs)) * Half;
                theta = SafeDiv(t * TwoPi, pMid);
                float aStart = axis.Var1A;
                amp = aStart + (((float)axis.Var1B - aStart) * t) / changingTermMs;
            }

            double wave = axis.Type == ParticleSimMoveType.Sin ? Math.Sin(theta) : Math.Cos(theta);
            return (float)(wave * amp);
        }

        private static double SafeDiv(double num, double den)
        {
            return Math.Abs(den) < 1e-6 ? 0.0 : num / den;
        }

        /// <summary>0x00e58b7b..0x00e58d46: 固定顺序 rot[2](ZY) -> rot[1](XZ) -> rot[0](XY) 三轴平面旋转。</summary>
        internal static void ApplyRotation3D(ref float x, ref float y, ref float z, int rotXDeg, int rotYDeg, int rotZDeg)
        {
            if (rotZDeg % 360 != 0)
            {
                RotatePlane(ref z, ref y, rotZDeg);
            }
            if (rotYDeg % 360 != 0)
            {
                RotatePlane(ref x, ref z, rotYDeg);
            }
            if (rotXDeg % 360 != 0)
            {
                RotatePlane(ref x, ref y, rotXDeg);
            }
        }

        private static void RotatePlane(ref float a, ref float b, int degrees)
        {
            double radius = Math.Sqrt(a * a + b * b);
            double angle = Math.Atan2(b, a) + degrees * DegToRad;
            a = (float)(Math.Cos(angle) * radius);
            b = (float)(Math.Sin(angle) * radius);
        }

        private ParticleSprite BuildSprite(ParticleInstance p)
        {
            string imgPath = string.Empty;
            int frameIndex = -1;
            int frameOffX = 0;
            int frameOffY = 0;
            float scaleX = 1f;
            float scaleY = 1f;
            float rotRad = 0f;
            byte cr = 255, cg = 255, cb = 255;
            int tintAlpha = 255;
            int graphicEffect = AniGraphicEffect.None;

            if (p.ObjectArg == -1)
            {
                AniFile ani = string.IsNullOrEmpty(p.ObjectPath) ? null : _resolveAni?.Invoke(p.ObjectPath);
                if (ani != null && ani.Frames.Length > 0)
                {
                    if (!_timelines.TryGetValue(p.ObjectPath, out AniTimeline timeline))
                    {
                        timeline = new AniTimeline(ani);
                        _timelines[p.ObjectPath] = timeline;
                    }
                    AniSample s = timeline.Sample(p.AgeMs);
                    if (s.Frame >= 0)
                    {
                        AniFrame f0 = ani.Frames[s.Frame];
                        if (f0.ImageIndex >= 0 && f0.ImageIndex < ani.ImagePaths.Length)
                        {
                            imgPath = NormalizeSpritePath(ani.ImagePaths[f0.ImageIndex]);
                            frameIndex = f0.FrameIndex;
                        }

                        f0.TryGetScale(out scaleX, out scaleY);
                        f0.TryGetRotationRadians(out rotRad);
                        f0.TryGetTint(out cr, out cg, out cb, out byte a0);
                        tintAlpha = a0;
                        offXY(f0, out frameOffX, out frameOffY);

                        if (s.Interpolates)
                        {
                            AniFrame f1 = ani.Frames[s.NextFrame];
                            frameOffX = LerpInt(f0.X, f1.X, s.Progress);
                            frameOffY = LerpInt(f0.Y, f1.Y, s.Progress);

                            f1.TryGetScale(out float sx1, out float sy1);
                            scaleX = (float)(scaleX + (sx1 - scaleX) * s.Progress);
                            scaleY = (float)(scaleY + (sy1 - scaleY) * s.Progress);

                            f1.TryGetRotationRadians(out float rot1);
                            rotRad = (float)(rotRad + (rot1 - rotRad) * s.Progress);

                            f1.TryGetTint(out byte r1, out byte g1, out byte b1, out byte a1);
                            cr = LerpByte(cr, r1, s.Progress);
                            cg = LerpByte(cg, g1, s.Progress);
                            cb = LerpByte(cb, b1, s.Progress);
                            tintAlpha = LerpInt(a0, a1, s.Progress);
                        }

                        f0.TryGetGraphicEffect(out graphicEffect);
                    }
                }
            }
            else
            {
                imgPath = string.IsNullOrEmpty(p.ObjectPath) ? string.Empty : NormalizeSpritePath(p.ObjectPath);
                frameIndex = p.ObjectArg;
            }

            // 【读码定案】0x007fccc5: alphaPct < 100 时乘算并四舍五入，>= 100 不缩
            int finalAlpha = p.AlphaPercent >= 100
                ? ClampByte(tintAlpha)
                : (p.AlphaPercent <= 0 ? 0 : ClampByte((int)Math.Round(tintAlpha * p.AlphaPercent * 0.01, MidpointRounding.AwayFromZero)));

            bool mirrored = p.Mirror ^ (scaleX < 0f);
            SpriteBlend blend = graphicEffect == AniGraphicEffect.Additive ? SpriteBlend.Additive : SpriteBlend.Normal;

            return new ParticleSprite(
                p.ParticleId,
                p.AgeMs,
                p.PosX,
                p.PosY,
                p.PosZ,
                mirrored,
                p.AlphaPercent,
                p.ObjectPath,
                p.ObjectArg,
                imgPath,
                frameIndex,
                frameOffX,
                frameOffY,
                scaleX,
                scaleY,
                rotRad,
                cr,
                cg,
                cb,
                finalAlpha,
                graphicEffect,
                blend,
                p.Layer);
        }

        private static void offXY(AniFrame f, out int x, out int y)
        {
            x = f.X;
            y = f.Y;
        }

        private static string NormalizeSpritePath(string path)
        {
            string norm = PvfPath.Normalize(path);
            if (norm.Length == 0 || norm.StartsWith("sprite/", StringComparison.Ordinal))
            {
                return norm;
            }
            return "sprite/" + norm;
        }

        private static int IntLerp(int a, int b, int num, int den)
        {
            return den == 0 ? a : a + (int)(((long)(b - a) * num) / den);
        }

        private static int LerpInt(int a, int b, double t)
        {
            return (int)Math.Round(a + (b - a) * t, MidpointRounding.AwayFromZero);
        }

        private static byte LerpByte(byte a, byte b, double t)
        {
            return ClampByte(LerpInt(a, b, t));
        }

        private static byte ClampByte(int v)
        {
            return (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
        }

        private static int LowerBound(int[] arr, int maxIndexInclusive, int target)
        {
            int lo = 0;
            int hi = maxIndexInclusive;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (arr[mid] < target)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        private static int[] BuildCircleCdf()
        {
            var cdf = new int[181];
            cdf[0] = 0;
            for (int k = 1; k <= 180; k++)
            {
                cdf[k] = cdf[k - 1] + (int)(Math.Sin(k * DegToRad) * 1000.0);
            }
            return cdf;
        }

        private sealed class ParticleInstance
        {
            public int ParticleId { get; }
            public int StartX { get; set; }
            public int StartY { get; set; }
            public int StartZ { get; set; }
            public ParticleSimAxis X { get; }
            public ParticleSimAxis Y { get; }
            public ParticleSimAxis Z { get; }
            public int ChangingTermMs { get; }
            public ParticleSimChanging ChangingType { get; }
            public int Gravity { get; }
            public int RotX { get; }
            public int RotY { get; }
            public int RotZ { get; }
            public bool Mirror { get; }
            public int LifeTimeMs { get; }
            public ParticleSimLanding Landing { get; }
            public int TransparentStart { get; }
            public int TransparentEnd { get; }
            public ParticleSimLayer Layer { get; }
            public string ObjectPath { get; }
            public int ObjectArg { get; }

            public int AgeMs { get; set; }
            public int PosX { get; set; }
            public int PosY { get; set; }
            public int PosZ { get; set; }
            public int AlphaPercent { get; set; }
            public bool Alive { get; set; } = true;

            public ParticleInstance(
                int particleId,
                int startX,
                int startY,
                int startZ,
                ParticleSimAxis x,
                ParticleSimAxis y,
                ParticleSimAxis z,
                int changingTermMs,
                ParticleSimChanging changingType,
                int gravity,
                int rotX,
                int rotY,
                int rotZ,
                bool mirror,
                int lifeTimeMs,
                ParticleSimLanding landing,
                int transparentStart,
                int transparentEnd,
                ParticleSimLayer layer,
                string objectPath,
                int objectArg)
            {
                ParticleId = particleId;
                StartX = startX;
                StartY = startY;
                StartZ = startZ;
                X = x;
                Y = y;
                Z = z;
                ChangingTermMs = changingTermMs;
                ChangingType = changingType;
                Gravity = gravity;
                RotX = rotX;
                RotY = rotY;
                RotZ = rotZ;
                Mirror = mirror;
                LifeTimeMs = lifeTimeMs;
                Landing = landing;
                TransparentStart = transparentStart;
                TransparentEnd = transparentEnd;
                Layer = layer;
                ObjectPath = objectPath;
                ObjectArg = objectArg;
            }
        }
    }

    /// <summary>纯函数粒子仿真入口与 CPU 参考画布合成器（无 I/O、无 Unity 依赖）。</summary>
    public static class ParticleSimulation
    {
        /// <summary>运行固定帧数的确定性粒子仿真:第 0 步对应发射初刻 (t=0),后续每步推进 <paramref name="stepMs"/> 毫秒。</summary>
        public static ParticleSimulationResult Simulate(
            ParticleSimDef def,
            uint seed,
            int frames,
            int stepMs,
            ParticleSimVec3 emitterPos = default,
            bool facingLeft = false,
            int quality = 2,
            Func<string, AniFile> resolveAni = null)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            if (frames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frames));
            }
            if (stepMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stepMs));
            }
            if (frames == 0)
            {
                return new ParticleSimulationResult(Array.Empty<ParticleStepFrame>());
            }

            var emitter = new ParticleEmitter(def, seed, emitterPos, quality, resolveAni);
            var steps = new ParticleStepFrame[frames];
            for (int f = 0; f < frames; f++)
            {
                int dt = f == 0 ? 0 : stepMs;
                IReadOnlyList<ParticleSprite> sprites = emitter.Step(dt, emitterPos, facingLeft);
                steps[f] = new ParticleStepFrame(emitter.ElapsedMs, sprites);
            }
            return new ParticleSimulationResult(steps);
        }

        /// <summary>将某一步的粒子精灵列表画到 CPU 参考画布 <paramref name="canvas"/> 上。</summary>
        public static void RenderFrame(
            RgbaCanvas canvas,
            IReadOnlyList<ParticleSprite> sprites,
            ISpriteSource spriteSource,
            TownProjection projection)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));
            if (sprites == null) throw new ArgumentNullException(nameof(sprites));
            if (spriteSource == null) throw new ArgumentNullException(nameof(spriteSource));

            for (int i = 0; i < sprites.Count; i++)
            {
                ParticleSprite s = sprites[i];
                if (s.Alpha <= 0 || s.FrameIndex < 0 || string.IsNullOrEmpty(s.ImagePath))
                {
                    continue;
                }
                if (!spriteSource.TryGetFrame(s.ImagePath, s.FrameIndex, out SpriteFrame src) || src.Width == 0 || src.Height == 0)
                {
                    continue;
                }

                projection.TopLeft(s.WorldX, s.WorldY, s.WorldZ, s.FrameOffsetX, s.FrameOffsetY, src.OffsetX, src.OffsetY, src.Width, s.Mirrored, out int sx, out int sy);

                float absSx = Math.Abs(s.ScaleX);
                float absSy = Math.Abs(s.ScaleY);
                if (absSx <= 1e-4f || absSy <= 1e-4f)
                {
                    continue;
                }

                bool hasTransform = Math.Abs(absSx - 1f) > 1e-3f || Math.Abs(absSy - 1f) > 1e-3f || Math.Abs(s.RotationRadians) > 1e-3f;
                bool hasColorTint = s.ColorR != 255 || s.ColorG != 255 || s.ColorB != 255;

                if (!hasTransform && !hasColorTint)
                {
                    canvas.Draw(src, sx, sy, s.Mirrored, s.Blend, s.Alpha);
                }
                else
                {
                    SpriteFrame transformed = TransformSprite(src, s.Mirrored, absSx, absSy, s.RotationRadians, s.ColorR, s.ColorG, s.ColorB, out int deltaX, out int deltaY);
                    if (transformed != null)
                    {
                        canvas.Draw(transformed, sx + deltaX, sy + deltaY, false, s.Blend, s.Alpha);
                    }
                }
            }
        }

        /// <summary>生成多帧粒子的横向/网格连拍图画布（供离线查看与合成测试）。</summary>
        public static RgbaCanvas RenderContactSheet(
            ParticleSimulationResult simulation,
            ISpriteSource spriteSource,
            int cellWidth = 256,
            int cellHeight = 256,
            int columns = 4)
        {
            if (simulation == null) throw new ArgumentNullException(nameof(simulation));
            if (spriteSource == null) throw new ArgumentNullException(nameof(spriteSource));
            if (cellWidth <= 0 || cellHeight <= 0 || columns <= 0) throw new ArgumentOutOfRangeException(nameof(cellWidth));

            int count = simulation.Steps.Count;
            if (count == 0)
            {
                var empty = new RgbaCanvas(cellWidth, cellHeight);
                empty.Clear(22, 24, 30, 255);
                return empty;
            }

            int cols = count < columns ? count : columns;
            int rows = (count + cols - 1) / cols;
            var sheet = new RgbaCanvas(cols * cellWidth, rows * cellHeight);
            sheet.Clear(22, 24, 30, 255);

            var cell = new RgbaCanvas(cellWidth, cellHeight);
            var projection = new TownProjection(-(cellWidth / 2), (cellHeight * 2) / 3);

            for (int idx = 0; idx < count; idx++)
            {
                cell.Clear(28, 30, 38, 255);
                DrawAnchorCrosshair(cell, cellWidth / 2, (cellHeight * 2) / 3);
                RenderFrame(cell, simulation.Steps[idx].Sprites, spriteSource, projection);

                int col = idx % cols;
                int row = idx / cols;
                BlitCell(sheet, cell, col * cellWidth, row * cellHeight);
            }
            return sheet;
        }

        private static void DrawAnchorCrosshair(RgbaCanvas canvas, int cx, int cy)
        {
            for (int dx = -6; dx <= 6; dx++)
            {
                SetPixelSafe(canvas, cx + dx, cy, 65, 75, 95);
            }
            for (int dy = -6; dy <= 6; dy++)
            {
                SetPixelSafe(canvas, cx, cy + dy, 65, 75, 95);
            }
        }

        private static void SetPixelSafe(RgbaCanvas canvas, int x, int y, byte r, byte g, byte b)
        {
            if ((uint)x >= (uint)canvas.Width || (uint)y >= (uint)canvas.Height)
            {
                return;
            }
            int o = (y * canvas.Width + x) * 4;
            canvas.Pixels[o] = r;
            canvas.Pixels[o + 1] = g;
            canvas.Pixels[o + 2] = b;
            canvas.Pixels[o + 3] = 255;
        }

        private static void BlitCell(RgbaCanvas dst, RgbaCanvas cell, int dstX, int dstY)
        {
            int cw = cell.Width;
            int ch = cell.Height;
            for (int y = 0; y < ch; y++)
            {
                bool isBorder = y == 0 || y == ch - 1;
                int srcRow = y * cw * 4;
                int dstRow = ((dstY + y) * dst.Width + dstX) * 4;
                Buffer.BlockCopy(cell.Pixels, srcRow, dst.Pixels, dstRow, cw * 4);
                if (isBorder)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        int o = dstRow + x * 4;
                        dst.Pixels[o] = 52;
                        dst.Pixels[o + 1] = 58;
                        dst.Pixels[o + 2] = 72;
                    }
                }
                else
                {
                    dst.Pixels[dstRow] = 52;
                    dst.Pixels[dstRow + 1] = 58;
                    dst.Pixels[dstRow + 2] = 72;
                    int right = dstRow + (cw - 1) * 4;
                    dst.Pixels[right] = 52;
                    dst.Pixels[right + 1] = 58;
                    dst.Pixels[right + 2] = 72;
                }
            }
        }

        internal static SpriteFrame TransformSprite(
            SpriteFrame src,
            bool mirrored,
            float scaleX,
            float scaleY,
            float radians,
            byte tintR,
            byte tintG,
            byte tintB,
            out int deltaX,
            out int deltaY)
        {
            int sw = src.Width;
            int sh = src.Height;
            float hw = sw * 0.5f * scaleX;
            float hh = sh * 0.5f * scaleY;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);

            float halfOutW = Math.Abs(cos * hw) + Math.Abs(sin * hh);
            float halfOutH = Math.Abs(sin * hw) + Math.Abs(cos * hh);
            int dw = Math.Max(1, (int)Math.Ceiling(halfOutW * 2f));
            int dh = Math.Max(1, (int)Math.Ceiling(halfOutH * 2f));
            if (dw > 2048 || dh > 2048)
            {
                deltaX = 0;
                deltaY = 0;
                return null;
            }

            deltaX = (int)Math.Round(sw * 0.5f - dw * 0.5f, MidpointRounding.AwayFromZero);
            deltaY = (int)Math.Round(sh * 0.5f - dh * 0.5f, MidpointRounding.AwayFromZero);

            byte[] dst = new byte[dw * dh * 4];
            byte[] sp = src.Rgba;
            float cxDst = dw * 0.5f;
            float cyDst = dh * 0.5f;
            float cxSrc = sw * 0.5f;
            float cySrc = sh * 0.5f;

            for (int y = 0; y < dh; y++)
            {
                float dy = (y + 0.5f) - cyDst;
                for (int x = 0; x < dw; x++)
                {
                    float dx = (x + 0.5f) - cxDst;
                    float ux = (cos * dx + sin * dy) / scaleX + cxSrc;
                    float uy = (-sin * dx + cos * dy) / scaleY + cySrc;

                    int ix = (int)Math.Floor(ux);
                    int iy = (int)Math.Floor(uy);
                    if ((uint)ix >= (uint)sw || (uint)iy >= (uint)sh)
                    {
                        continue;
                    }
                    int sampleX = mirrored ? sw - 1 - ix : ix;
                    int so = (iy * sw + sampleX) * 4;
                    byte a = sp[so + 3];
                    if (a == 0)
                    {
                        continue;
                    }
                    int doff = (y * dw + x) * 4;
                    dst[doff] = tintR == 255 ? sp[so] : (byte)((sp[so] * tintR + 127) / 255);
                    dst[doff + 1] = tintG == 255 ? sp[so + 1] : (byte)((sp[so + 1] * tintG + 127) / 255);
                    dst[doff + 2] = tintB == 255 ? sp[so + 2] : (byte)((sp[so + 2] * tintB + 127) / 255);
                    dst[doff + 3] = a;
                }
            }
            return new SpriteFrame(dw, dh, 0, 0, dw, dh, dst);
        }
    }
}
