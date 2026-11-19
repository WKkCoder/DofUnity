using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:客户端 .ptl 全量加载函数 0x0131c0e0（默认复位 0x0113b1f0、枚举解析 0x01223a00、[object type] 判定 0x00e57c60、
// 创建与随机数 0x00e59570 / 0x00e59b20 / 0x00e57090）以及全库 4,438 个 .ptl 的普查统计。
// 注意:本文件与 E-103 投射物用的 ParticleDef 并列,不修改 ParticleDef 的任何公开成员与行为。
namespace Dnf.Game
{
    /// <summary>[rotation angle range type]:发射方向角的随机扰动形状（0x0131c0e0 写发射器 +0xa0;未知拼写保持默认）。</summary>
    public enum ParticleRotationRangeType
    {
        Rectangle = 0,
        Circle = 1,
    }

    /// <summary>[how to create object]:从 [object] 列表挑选粒子的顺序（0x0131c0e0 写发射器 +0xc0;客户端默认 1 = Randomize）。</summary>
    public enum ParticleHowToCreate
    {
        InOrder = 0,
        Randomize = 1,
    }

    /// <summary>[object type]:粒子实体种类（0x00e57c60: [animation] = 2、[passive object] = 0x21 = 33、其余拼写返回 0 = 不生成）。</summary>
    public enum ParticleObjectKind
    {
        Unknown = 0,
        Animation = 2,
        PassiveObject = 33,
    }

    /// <summary>[layer]:粒子所在渲染图层（0x01223a00 解析枚举,默认 Normal = 1）。</summary>
    public enum ParticleLayer
    {
        Close = 0,
        Normal = 1,
        Bottom = 2,
        CloseBack = 3,
        MiddleBack = 4,
        DistantBack = 5,
        Door = 6,
    }

    /// <summary>三维整数向量（[creation pos]、[rotation angle]、[create area size]、[create area rotation angle]）。</summary>
    public readonly struct ParticleVec3i : IEquatable<ParticleVec3i>
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public ParticleVec3i(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(ParticleVec3i other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) => obj is ParticleVec3i other && Equals(other);

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

        public static bool operator ==(ParticleVec3i left, ParticleVec3i right) => left.Equals(right);

        public static bool operator !=(ParticleVec3i left, ParticleVec3i right) => !left.Equals(right);
    }

    /// <summary>[object] 块里的一对 (资源路径, 整数参数):arg == -1 表示完整 .ani 动画,arg &gt;= 0 表示 .img 帧号或被动对象编号。</summary>
    public readonly struct ParticleObjectEntry : IEquatable<ParticleObjectEntry>
    {
        public string Path { get; }
        public int Arg { get; }

        public ParticleObjectEntry(string path, int arg)
        {
            Path = path ?? string.Empty;
            Arg = arg;
        }

        public bool IsAni => Arg == -1;

        public bool Equals(ParticleObjectEntry other) => string.Equals(Path, other.Path, StringComparison.Ordinal) && Arg == other.Arg;

        public override bool Equals(object obj) => obj is ParticleObjectEntry other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Path != null ? Path.GetHashCode() : 0) * 397) ^ Arg;
            }
        }
    }

    /// <summary>
    /// 视觉粒子体系下单个轴（X/Y/Z）的运动定义（0x0131c0e0 写基结构 +0x0c..+0x41）。
    /// 注意:在 0x0131c0e0 中,[changing term] 与 [changing type] 落在全局共享偏移 +0x42 / +0x46,并非分轴独立。
    /// </summary>
    public readonly struct ParticleEffectAxis
    {
        public ParticleMoveType Type { get; }
        public int Var1A { get; }
        public int Var1B { get; }
        public int Var2A { get; }
        public int Var2B { get; }

        public ParticleEffectAxis(ParticleMoveType type, int var1A, int var1B, int var2A, int var2B)
        {
            Type = type;
            Var1A = var1A;
            Var1B = var1B;
            Var2A = var2A;
            Var2B = var2B;
        }
    }

    /// <summary>
    /// 完整的 .ptl 粒子特效与发射器定义（依据客户端 0x0131c0e0 全部 32 个英文开标签及默认值;保留未读字段）。
    /// 不修改既有 <see cref="ParticleDef"/>,保证 E-103 怪物投射物零回归。
    /// </summary>
    public sealed class ParticleEffectDef
    {
        public string Path { get; }
        public ParticleVec3i CreationPos { get; }
        public ParticleEffectAxis X { get; }
        public ParticleEffectAxis Y { get; }
        public ParticleEffectAxis Z { get; }

        /// <summary>全局共享的变化周期时长（毫秒,0x0131c0e0 写 +0x42,默认 0）。</summary>
        public int ChangingTermMs { get; }

        /// <summary>全局共享的变化方式（0x0131c0e0 写 +0x46,默认 KeepChanging）。</summary>
        public ParticleChanging ChangingType { get; }

        /// <summary>重力加速度（px/s^2,正值向下,默认 0）。</summary>
        public int Gravity { get; }

        /// <summary>三轴初始发射旋转角（度,先后按 Z/Y/X 对应平面旋转）。</summary>
        public ParticleVec3i RotationAngle { get; }

        /// <summary>粒子寿命（毫秒）;0 表示不按时间自动死亡（0x00e58b00）。</summary>
        public int LifeTimeMs { get; }

        /// <summary>触地处理（默认 Pierce = 0）。</summary>
        public ParticleLanding Landing { get; }

        /// <summary>起始与结束不透明度百分比（默认 100, 100）。</summary>
        public int TransparentStart { get; }
        public int TransparentEnd { get; }

        /// <summary>保留字段（0x0131c0e0 写入 +0x67/+0x6b/+0x6f,客户端渲染未读）。</summary>
        public int AttackBetweenTimeMs { get; }
        public int ExpansionRate { get; }
        public int AttackBonusRate { get; }

        /// <summary>每秒发射粒子数（Hz）;-1 表示单次爆发 [maximum create number] 颗（0x00e59b20）。</summary>
        public float CreationFrequency { get; }

        public int MaximumCreateNumber { get; }
        public int MaximumCreateTimeMs { get; }
        public ParticleVec3i CreateAreaSize { get; }
        public ParticleVec3i CreateAreaRotationAngle { get; }
        public ParticleRotationRangeType RotationAngleRangeType { get; }
        public int RotationAngleVar0 { get; }
        public int RotationAngleVar1 { get; }

        /// <summary>速度/振幅/周期/寿命等随机扰动幅度（单位 0.5%,最大 200）。</summary>
        public int RandomRange { get; }

        /// <summary>移动发射器连发时是否在前后帧位置间插值起步坐标（[create pos correct]）。</summary>
        public bool CreatePosCorrect { get; }

        public int DisplayEffectRateLow { get; }
        public int DisplayEffectRateMid { get; }
        public int DisplayEffectRateHigh { get; }
        public ParticleHowToCreate HowToCreateObject { get; }
        public ParticleObjectKind ObjectType { get; }
        public string ObjectTypeRaw { get; }
        public ParticleLayer Layer { get; }
        public IReadOnlyList<ParticleObjectEntry> Objects { get; }
        public string StartSound { get; }

        public ParticleEffectDef(
            string path,
            ParticleVec3i creationPos,
            ParticleEffectAxis x,
            ParticleEffectAxis y,
            ParticleEffectAxis z,
            int changingTermMs,
            ParticleChanging changingType,
            int gravity,
            ParticleVec3i rotationAngle,
            int lifeTimeMs,
            ParticleLanding landing,
            int transparentStart,
            int transparentEnd,
            int attackBetweenTimeMs,
            int expansionRate,
            int attackBonusRate,
            float creationFrequency,
            int maximumCreateNumber,
            int maximumCreateTimeMs,
            ParticleVec3i createAreaSize,
            ParticleVec3i createAreaRotationAngle,
            ParticleRotationRangeType rotationAngleRangeType,
            int rotationAngleVar0,
            int rotationAngleVar1,
            int randomRange,
            bool createPosCorrect,
            int displayEffectRateLow,
            int displayEffectRateMid,
            int displayEffectRateHigh,
            ParticleHowToCreate howToCreateObject,
            ParticleObjectKind objectType,
            string objectTypeRaw,
            ParticleLayer layer,
            IReadOnlyList<ParticleObjectEntry> objects,
            string startSound)
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
            AttackBetweenTimeMs = attackBetweenTimeMs;
            ExpansionRate = expansionRate;
            AttackBonusRate = attackBonusRate;
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
            ObjectTypeRaw = objectTypeRaw ?? "[animation]";
            Layer = layer;
            Objects = objects ?? Array.Empty<ParticleObjectEntry>();
            StartSound = startSound ?? string.Empty;
        }

        /// <summary>按画质档位（0 低 / 1 中 / 2 高）返回 [display effect rate] 百分比。</summary>
        public int DisplayEffectRate(int quality)
        {
            switch (quality)
            {
                case 0: return DisplayEffectRateLow;
                case 1: return DisplayEffectRateMid;
                default: return DisplayEffectRateHigh;
            }
        }

        /// <summary>解析一条 .ptl 标记流文档;未知标签与旧版韩文遗留标签按客户端 0x0131c0e0 逻辑跳过。</summary>
        public static ParticleEffectDef Parse(string path, ScriptLine[] lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            var creationPos = new ParticleVec3i(0, 0, 0);
            var moveType = new ParticleMoveType[3];
            var v1a = new int[3];
            var v1b = new int[3];
            var v2a = new int[3];
            var v2b = new int[3];
            int axis = 0;

            int changingTermMs = 0;
            ParticleChanging changingType = ParticleChanging.KeepChanging;
            int gravity = 0;
            var rotationAngle = new ParticleVec3i(0, 0, 0);
            int lifeTimeMs = 0;
            ParticleLanding landing = ParticleLanding.Pierce;
            int transparentStart = 100;
            int transparentEnd = 100;
            int attackBetweenTimeMs = 1000;
            int expansionRate = 1000;
            int attackBonusRate = 0;

            float creationFrequency = 0f;
            int maxCreateNumber = 0;
            int maxCreateTimeMs = 0;
            var createAreaSize = new ParticleVec3i(0, 0, 0);
            var createAreaRot = new ParticleVec3i(0, 0, 0);
            ParticleRotationRangeType rangeType = ParticleRotationRangeType.Rectangle;
            int rotVar0 = 0;
            int rotVar1 = 0;
            int randomRange = 0;
            bool createPosCorrect = false;
            int rateLow = 100;
            int rateMid = 100;
            int rateHigh = 100;
            ParticleHowToCreate howToCreate = ParticleHowToCreate.Randomize;
            ParticleObjectKind objectType = ParticleObjectKind.Animation;
            string objectTypeRaw = "[animation]";
            ParticleLayer layer = ParticleLayer.Normal;
            var objects = new List<ParticleObjectEntry>();
            string startSound = string.Empty;

            int depth = 0;
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    continue;
                }
                string tag = line.Tag.ToLowerInvariant();
                if (tag == "[object]")
                {
                    if (depth == 0)
                    {
                        ReadObjectEntries(line, objects);
                    }
                    depth++;
                    continue;
                }
                if (tag == "[/object]")
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                    continue;
                }
                if (depth > 0)
                {
                    continue;
                }

                switch (tag)
                {
                    case "[creation pos]":
                        ReadVec3(line, ref creationPos);
                        break;
                    case "[x axis]":
                        axis = 0;
                        break;
                    case "[y axis]":
                        axis = 1;
                        break;
                    case "[z axis]":
                        axis = 2;
                        break;
                    case "[move type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[stop]":
                                moveType[axis] = ParticleMoveType.Stop;
                                break;
                            case "[straight line]":
                                moveType[axis] = ParticleMoveType.StraightLine;
                                break;
                            case "[sin]":
                                moveType[axis] = ParticleMoveType.Sin;
                                break;
                            case "[cos]":
                                moveType[axis] = ParticleMoveType.Cos;
                                break;
                        }
                        break;
                    case "[move variable 1]":
                        ReadIntAt(line, 0, ref v1a[axis]);
                        ReadIntAt(line, 1, ref v1b[axis]);
                        break;
                    case "[move variable 2]":
                        ReadIntAt(line, 0, ref v2a[axis]);
                        ReadIntAt(line, 1, ref v2b[axis]);
                        break;
                    case "[changing term]":
                        ReadIntAt(line, 0, ref changingTermMs);
                        break;
                    case "[changing type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[keep changing]":
                                changingType = ParticleChanging.KeepChanging;
                                break;
                            case "[keep last value]":
                                changingType = ParticleChanging.KeepLastValue;
                                break;
                            case "[ping pong]":
                                changingType = ParticleChanging.PingPong;
                                break;
                        }
                        break;
                    case "[gravity]":
                        ReadIntAt(line, 0, ref gravity);
                        break;
                    case "[rotation angle]":
                        ReadVec3(line, ref rotationAngle);
                        break;
                    case "[life time]":
                        ReadIntAt(line, 0, ref lifeTimeMs);
                        break;
                    case "[landing type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[pierce]":
                                landing = ParticleLanding.Pierce;
                                break;
                            case "[slide]":
                                landing = ParticleLanding.Slide;
                                break;
                            case "[stop]":
                                landing = ParticleLanding.Stop;
                                break;
                            case "[vanish]":
                                landing = ParticleLanding.Vanish;
                                break;
                        }
                        break;
                    case "[transparent value]":
                        ReadIntAt(line, 0, ref transparentStart);
                        ReadIntAt(line, 1, ref transparentEnd);
                        break;
                    case "[attack between time]":
                        ReadIntAt(line, 0, ref attackBetweenTimeMs);
                        break;
                    case "[expansion rate]":
                        ReadIntAt(line, 0, ref expansionRate);
                        break;
                    case "[attack bonus rate]":
                        ReadIntAt(line, 0, ref attackBonusRate);
                        break;
                    case "[creation frequency]":
                        ReadFloatAt(line, 0, ref creationFrequency);
                        break;
                    case "[maximum create number]":
                        ReadIntAt(line, 0, ref maxCreateNumber);
                        break;
                    case "[maximum create time]":
                        ReadIntAt(line, 0, ref maxCreateTimeMs);
                        break;
                    case "[create area size]":
                        ReadVec3(line, ref createAreaSize);
                        break;
                    case "[create area rotation angle]":
                        ReadVec3(line, ref createAreaRot);
                        break;
                    case "[rotation angle range type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[rectangle]":
                                rangeType = ParticleRotationRangeType.Rectangle;
                                break;
                            case "[circle]":
                                rangeType = ParticleRotationRangeType.Circle;
                                break;
                        }
                        break;
                    case "[rotation angle variable]":
                        ReadIntAt(line, 0, ref rotVar0);
                        ReadIntAt(line, 1, ref rotVar1);
                        break;
                    case "[random range]":
                        ReadIntAt(line, 0, ref randomRange);
                        break;
                    case "[create pos correct]":
                    {
                        int flag = 0;
                        if (ReadIntAt(line, 0, ref flag))
                        {
                            createPosCorrect = flag != 0;
                        }
                        break;
                    }
                    case "[display effect rate]":
                        ReadIntAt(line, 0, ref rateLow);
                        ReadIntAt(line, 1, ref rateMid);
                        ReadIntAt(line, 2, ref rateHigh);
                        break;
                    case "[how to create object]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[in order]":
                                howToCreate = ParticleHowToCreate.InOrder;
                                break;
                            case "[randomize]":
                                howToCreate = ParticleHowToCreate.Randomize;
                                break;
                        }
                        break;
                    case "[object type]":
                    {
                        string raw = line.FirstString();
                        if (raw != null)
                        {
                            objectTypeRaw = raw;
                            switch (raw.ToLowerInvariant())
                            {
                                case "[animation]":
                                    objectType = ParticleObjectKind.Animation;
                                    break;
                                case "[passive object]":
                                    objectType = ParticleObjectKind.PassiveObject;
                                    break;
                                default:
                                    objectType = ParticleObjectKind.Unknown;
                                    break;
                            }
                        }
                        break;
                    }
                    case "[layer]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[close]":
                                layer = ParticleLayer.Close;
                                break;
                            case "[normal]":
                                layer = ParticleLayer.Normal;
                                break;
                            case "[bottom]":
                                layer = ParticleLayer.Bottom;
                                break;
                            case "[closeback]":
                                layer = ParticleLayer.CloseBack;
                                break;
                            case "[middleback]":
                                layer = ParticleLayer.MiddleBack;
                                break;
                            case "[distantback]":
                                layer = ParticleLayer.DistantBack;
                                break;
                        }
                        break;
                    case "[start sound]":
                    {
                        string sound = line.FirstString();
                        if (sound != null)
                        {
                            startSound = sound;
                        }
                        break;
                    }
                }
            }

            return new ParticleEffectDef(
                path,
                creationPos,
                new ParticleEffectAxis(moveType[0], v1a[0], v1b[0], v2a[0], v2b[0]),
                new ParticleEffectAxis(moveType[1], v1a[1], v1b[1], v2a[1], v2b[1]),
                new ParticleEffectAxis(moveType[2], v1a[2], v1b[2], v2a[2], v2b[2]),
                changingTermMs,
                changingType,
                gravity,
                rotationAngle,
                lifeTimeMs,
                landing,
                transparentStart,
                transparentEnd,
                attackBetweenTimeMs,
                expansionRate,
                attackBonusRate,
                creationFrequency,
                maxCreateNumber,
                maxCreateTimeMs,
                createAreaSize,
                createAreaRot,
                rangeType,
                rotVar0,
                rotVar1,
                randomRange,
                createPosCorrect,
                rateLow,
                rateMid,
                rateHigh,
                howToCreate,
                objectType,
                objectTypeRaw,
                layer,
                objects.ToArray(),
                startSound);
        }

        private static void ReadObjectEntries(ScriptLine line, List<ParticleObjectEntry> into)
        {
            ScriptValue[] vals = line.Values;
            int i = 0;
            while (i < vals.Length)
            {
                if (vals[i].Kind != ScriptValueKind.String)
                {
                    i++;
                    continue;
                }
                string entryPath = vals[i].Text ?? string.Empty;
                int j = i + 1;
                while (j < vals.Length && !IsNumeric(vals[j]))
                {
                    if (vals[j].Kind == ScriptValueKind.String)
                    {
                        break;
                    }
                    j++;
                }
                if (j < vals.Length && IsNumeric(vals[j]))
                {
                    into.Add(new ParticleObjectEntry(entryPath, ToInt(vals[j])));
                    i = j + 1;
                }
                else
                {
                    i++;
                }
            }
        }

        private static void ReadVec3(ScriptLine line, ref ParticleVec3i target)
        {
            int x = target.X;
            int y = target.Y;
            int z = target.Z;
            ReadIntAt(line, 0, ref x);
            ReadIntAt(line, 1, ref y);
            ReadIntAt(line, 2, ref z);
            target = new ParticleVec3i(x, y, z);
        }

        private static bool ReadIntAt(ScriptLine line, int numericSlot, ref int target)
        {
            int idx = 0;
            foreach (ScriptValue v in line.Values)
            {
                if (!IsNumeric(v))
                {
                    continue;
                }
                if (idx == numericSlot)
                {
                    target = ToInt(v);
                    return true;
                }
                idx++;
            }
            return false;
        }

        private static void ReadFloatAt(ScriptLine line, int numericSlot, ref float target)
        {
            int idx = 0;
            foreach (ScriptValue v in line.Values)
            {
                if (!IsNumeric(v))
                {
                    continue;
                }
                if (idx == numericSlot)
                {
                    target = v.IsNumber ? v.Int : v.Float;
                    return;
                }
                idx++;
            }
        }

        private static bool IsNumeric(ScriptValue v) => v.IsNumber || v.Kind == ScriptValueKind.Float;

        private static int ToInt(ScriptValue v) => v.IsNumber ? v.Int : ScriptCursor.TruncateToInt(v.Float);
    }
}
