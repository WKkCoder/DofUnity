using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    public enum ObjectKind
    {
        Character,
        Monster,
        PassiveObject,
        Other,
    }

    /// <summary>方向常量(来自脚本头文件 dnf_enum_header.nut 的 ENUM_DIRECTION_*,其中 NEUTRAL = 4 已核对;左右上下的取值是常见约定)。</summary>
    public static class Direction
    {
        public const int Left = 0;
        public const int Right = 1;
        public const int Up = 2;
        public const int Down = 3;
        public const int Neutral = 4;
    }

    /// <summary>
    /// 世界里的一个对象:角色、怪物、被动对象等。位置是整数 (x, y, z):x 横向、y 纵深、z 高度(取向依据判定框数据,见 <see cref="Combat"/>)。
    /// 状态编号与技能子状态由脚本通过 sq_AddSetStatePacket / setSkillSubState 驱动。
    /// </summary>
    public class GameObject : NativeObject
    {
        public override string TypeName => "GameObject";

        public ObjectKind Kind { get; }
        public string Name { get; set; }

        /// <summary>职业编号(角色才有;-1 表示不是角色)。</summary>
        public int Job { get; internal set; } = -1;

        public CharacterDef Character { get; internal set; }

        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        /// <summary>朝向:<see cref="Dnf.Game.Direction.Left"/> 或 <see cref="Dnf.Game.Direction.Right"/>。</summary>
        public int Direction { get; set; } = Dnf.Game.Direction.Right;

        /// <summary>当前状态编号(0 是站立,其余见脚本头文件)。</summary>
        public int State { get; internal set; }

        public int SkillSubState { get; set; }

        /// <summary>当前状态在脚本里登记的信息(没有脚本的状态为 null)。</summary>
        public StateInfo StateInfo { get; internal set; }

        public GameObject Parent { get; set; }
        public GameObject Source { get; set; }

        /// <summary>是否由本机玩家控制(脚本里 isMyControlObject 的答案;单机里玩家角色为真)。</summary>
        public bool IsMyControl { get; set; }

        public bool Valid { get; internal set; } = true;

        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Mp { get; set; }
        public int MaxMp { get; set; }

        /// <summary>对象的高度(用于 getObjectHeight)。</summary>
        public int Height { get; set; } = 70;

        /// <summary>创建后经过的毫秒数(脚本里 sq_GetObjectTime)。</summary>
        public int AgeMs { get; internal set; }

        public IntVector DataVector { get; }
        public Dictionary<int, int> SkillLevels { get; } = new Dictionary<int, int>();
        public TimeEventSet TimeEvents { get; } = new TimeEventSet();
        public Animator Animator { get; } = new Animator();
        public AttackInfo CurrentAttackInfo { get; internal set; }
        public List<Appendage> Appendages { get; } = new List<Appendage>();

        /// <summary>受击框的固定范围(没有动画的假人用;为 null 时用当前动画帧里的受击框)。</summary>
        public Aabb? FixedDamageBox { get; set; }

        /// <summary>本次攻击已经命中过的对象编号(同一次攻击不重复命中;resetHitObjectList 清空)。</summary>
        public HashSet<long> HitObjects { get; } = new HashSet<long>();

        /// <summary>静态移动(sq_SetStaticMoveInfo):每秒速度与加速度,方向由 sq_SetMoveDirection 指定。</summary>
        public StaticMove Move { get; } = new StaticMove();

        private GameTimer _timer;

        /// <summary>对象自己的计时器(obj.getTimer());切换状态时若 isResetTimer 为真会重置。</summary>
        public GameTimer Timer => _timer ?? (_timer = new GameTimer(World));

        private readonly Dictionary<string, VarStore> _vars = new Dictionary<string, VarStore>(StringComparer.Ordinal);
        private VarStore _defaultVar;

        internal readonly Queue<StatePacket> Packets = new Queue<StatePacket>();

        /// <summary>玩家请求的技能编号(-1 表示没有请求);由 <see cref="GameWorld.RequestSkill"/> 设置、sq_IsUseSkill 读取。</summary>
        internal int RequestedSkill = -1;

        /// <summary>脚本禁用的技能指令(setSkillCommandEnable(skill, false))。</summary>
        internal readonly HashSet<int> DisabledSkillCommands = new HashSet<int>();

        public GameObject(GameWorld world, ObjectKind kind, string name)
        {
            World = world;
            Kind = kind;
            Name = name;
            DataVector = new IntVector();
            DataVector.World = world;
        }

        /// <summary>obj.getVar() 与 obj.getVar("名字"):没有则创建。</summary>
        public VarStore GetVar(string name)
        {
            if (name == null)
            {
                if (_defaultVar == null)
                {
                    _defaultVar = new VarStore { World = World };
                }
                return _defaultVar;
            }
            if (!_vars.TryGetValue(name, out VarStore store))
            {
                store = new VarStore { World = World };
                _vars.Add(name, store);
            }
            return store;
        }

        internal IEnumerable<VarStore> AllVarStores()
        {
            if (_defaultVar != null)
            {
                yield return _defaultVar;
            }
            foreach (VarStore v in _vars.Values)
            {
                yield return v;
            }
        }

        public int GetSkillLevel(int skillId)
        {
            return SkillLevels.TryGetValue(skillId, out int level) ? level : 0;
        }

        public override string ToString()
        {
            return Name + "#" + Id + "(" + X + "," + Y + "," + Z + ")";
        }
    }

    /// <summary>sq_AddSetStatePacket 发出的"切换状态"消息;数据数组是发出时的拷贝。</summary>
    internal sealed class StatePacket
    {
        public int State;
        public int Priority;
        public bool ResetTimer;
        public long[] Data;
    }

    /// <summary>
    /// 静态移动:脚本给出速度(每秒的距离)与加速度,引擎按朝向与方向在每帧推进。
    /// 参数含义依据 sq_SetStaticMoveInfo(0, 200, 100, false) 一类调用与名字推断,**未验证**。
    /// </summary>
    public sealed class StaticMove
    {
        public bool Active;
        public int SpeedX;
        public int SpeedY;
        public int AccelX;
        public int AccelY;
        public int DirectionX = Dnf.Game.Direction.Neutral;
        public int DirectionY = Dnf.Game.Direction.Neutral;

        /// <summary>不足一个整数单位的位移余量(逐帧累计,避免低速时被截成 0)。</summary>
        public float RemainderX;
        public float RemainderY;
    }
}
