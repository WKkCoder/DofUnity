using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>命中区域:攻击框与受击框相交的那一块。脚本在 onAttack 里用 sq_GetCenterXPos(boundingBox) 等取它的中心来放命中特效。</summary>
    public sealed class BoundingBox : NativeObject
    {
        public BoundingBox(Aabb box)
        {
            Box = box;
        }

        public Aabb Box { get; }

        public override string TypeName => "BoundingBox";
    }

    /// <summary>
    /// 对象的种类判断、敌我、目标查找、对象管理器与舞台一类"服务"对象的最小实现。
    /// 没有地图与界面:对象管理器只提供对象列表,舞台与主控件只回答"没有鼠标按下"。
    /// 查找的几何(范围取向、距离)是依据名字与调用处推断的,**未验证**。
    /// </summary>
    public sealed partial class GameWorld
    {
        private ServiceObject _objectManager;
        private ServiceObject _stage;
        private ServiceObject _mainControl;
        private ServiceObject _map;

        private ServiceObject Service(ref ServiceObject field, string kind)
        {
            if (field == null)
            {
                field = new ServiceObject(kind);
                Handle(field);
            }
            return field;
        }

        /// <summary>
        /// 对象种类码(脚本头文件 OBJECTTYPE_*,观察):每一级占 4 位,低位是上层类型——
        /// COLLISION 1、ACTIVE 17、CHARACTER 273、MONSTER 529、PASSIVE 33、DRAWONLY 2。
        /// </summary>
        internal static int ObjectTypeCode(GameObject o)
        {
            switch (o.Kind)
            {
                case ObjectKind.Character:
                    return 273;
                case ObjectKind.Monster:
                    return 529;
                case ObjectKind.PassiveObject:
                    return 33;
                default:
                    return 2;
            }
        }

        /// <summary>isObjectType 的判断(推断):type 的每一级都要与对象的种类码一致,0(最上层的 OBJECT)对所有对象成立。</summary>
        internal static bool IsObjectType(GameObject o, int type)
        {
            if (type == 0)
            {
                return true;
            }
            int mask = 0;
            for (int t = type; t != 0; t >>= 4)
            {
                mask = (mask << 4) | 0xF;
            }
            return (ObjectTypeCode(o) & mask) == type;
        }

        private IEnumerable<GameObject> EnemiesOf(GameObject self)
        {
            foreach (GameObject o in _objects)
            {
                if (o.Valid && o.Hp > 0 && (o.Kind == ObjectKind.Character || o.Kind == ObjectKind.Monster) && !ReferenceEquals(o, self) && IsEnemy(self, o))
                {
                    yield return o;
                }
            }
        }

        private static int Forward(GameObject o)
        {
            return o.Direction == Direction.Left ? -1 : 1;
        }

        /// <summary>在一个盒子范围里找离 from 最近的敌人:x 在 [x0, x1],纵深差不超过 yRange,高度差不超过 zRange。</summary>
        private GameObject FindEnemyInBox(GameObject from, int x0, int x1, int yRange, int zRange, GameObject exclude)
        {
            int lo = Math.Min(x0, x1);
            int hi = Math.Max(x0, x1);
            GameObject best = null;
            long bestDistance = long.MaxValue;
            foreach (GameObject o in EnemiesOf(from))
            {
                if (ReferenceEquals(o, exclude) || o.X < lo || o.X > hi || Math.Abs(o.Y - from.Y) > yRange || Math.Abs(o.Z - from.Z) > zRange)
                {
                    continue;
                }
                long d = Math.Abs((long)o.X - from.X);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = o;
                }
            }
            return best;
        }

        private List<GameObject> CollisionObjects()
        {
            var list = new List<GameObject>();
            foreach (GameObject o in _objects)
            {
                if (o.Valid && IsObjectType(o, 1))
                {
                    list.Add(o);
                }
            }
            return list;
        }

        private GameObject MyControlObject()
        {
            foreach (GameObject o in _objects)
            {
                if (o.Valid && o.Kind == ObjectKind.Character && o.IsMyControl)
                {
                    return o;
                }
            }
            return null;
        }

        private void RegisterQueryNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeForm M = NativeForm.Member;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            // ---- 种类、敌我、状态
            Define("isObjectType", I, "对象是不是这种类型(OBJECTTYPE_*,层级编码:每一级 4 位)", M, c => c.Return(c.Self is GameObject o && IsObjectType(o, c.Int32(0))));
            Define("isEnemy", I, "与另一个对象是不是敌人:角色一方与怪物一方互为敌人(被动对象跟随发起者)", M, c =>
                c.Return(c.Self is GameObject a && c.Arg<GameObject>(0) is GameObject b && IsEnemy(a, b)));
            foreach (string n in new[] { "getTeam", "sq_GetTeam" })
            {
                Define(n, I, "对象所属的一方:角色 0、怪物 1(被动对象跟随发起者)", B, c => c.Return(c.Self is GameObject o ? (Side(o) == 2 ? 1 : 0) : 0));
            }
            Define("isInDamagableState", I, "能不能被攻击者打到:对象有效且生命值大于 0(其余状态限制没有实现)", M, c => c.Return(c.Self is GameObject o && o.Valid && o.Hp > 0));
            Define("isMessage", T, "对象是不是正在显示对话:总是否", M, c => c.Return(false));
            foreach (string n in new[] { "isInBattle", "sq_IsInBattle" })
            {
                Define(n, T, "对象是否在战斗中:有效就算", B, c => c.Return(c.Self is GameObject o && o.Valid));
            }
            foreach (string n in new[] { "isBoss", "sq_IsBoss", "isChampion", "isCommonChampion", "isSuperChampion", "sq_IsNamed", "sq_IsHellMonster", "isGhostMode", "isMissileObject" })
            {
                Define(n, T, "怪物的类别判断:没有怪物类别数据,总是否", B, c => c.Return(false));
            }
            foreach (string n in new[] { "sq_IsMyCharacter", "sq_isMyCharacter", "isMyCharacter" })
            {
                Define(n, I, "是不是本机玩家的角色", B, c => c.Return(c.Self is GameObject o && o.Kind == ObjectKind.Character && o.IsMyControl));
            }
            Define("getMp", I, "当前魔法值", M, c => c.Return(c.SelfAs<GameObject>().Mp));
            Define("getHpMax", I, "生命值上限", M, c => c.Return(c.SelfAs<GameObject>().MaxHp));
            Define("getMpMax", I, "魔法值上限", M, c => c.Return(c.SelfAs<GameObject>().MaxMp));
            Define("setHp", I, "直接设置生命值(限制在 0 到上限)", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                o.Hp = Math.Max(0, Math.Min(o.MaxHp, c.Int32(0)));
                return c.ReturnNothing();
            });
            Define("sq_SendSetHpPacket", I, "设置生命值 (值):单机里立刻生效", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                o.Hp = Math.Max(0, Math.Min(o.MaxHp, c.Int32(0)));
                return c.ReturnNothing();
            });
            Define("sendSetMpPacket", I, "设置魔法值 (值):单机里立刻生效", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                o.Mp = Math.Max(0, Math.Min(o.MaxMp, c.Int32(0)));
                return c.ReturnNothing();
            });
            Define("setObjectHeight", I, "设置对象的高度", M, c =>
            {
                c.SelfAs<GameObject>().Height = c.Int32(0);
                return c.ReturnNothing();
            });
            Define("sendDestroyPacket", I, "销毁对象(单机里在当前回调结束后处理)", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                QueueWorldPacket(() => Destroy(o));
                return c.ReturnNothing();
            });
            Define("sq_GetStateTimer", I, "当前状态已经过的毫秒数(自上次进入状态起)", M, c => c.Return(c.SelfAs<GameObject>().StateTimeMs));
            Define("sq_getStateTimer", I, "同 sq_GetStateTimer", M, c => c.Return(c.SelfAs<GameObject>().StateTimeMs));
            Define("sq_GetParentState", I, "父对象的当前状态编号;没有父对象为 -1", M, c => c.Return(c.SelfAs<GameObject>().Parent?.State ?? -1));
            Define("sq_GetParentSkillSubState", I, "父对象的技能子状态;没有父对象为 0", M, c => c.Return(c.SelfAs<GameObject>().Parent?.SkillSubState ?? 0));
            foreach (string n in new[] { "sq_GetSkillSubState" })
            {
                Define(n, I, "对象的技能子状态", M, c => c.Return(c.SelfAs<GameObject>().SkillSubState));
            }
            DefineAny("sq_SetSkillSubState", I, "设置对象的技能子状态 (对象, 值):成员写法 obj.sq_SetSkillSubState(obj, 值) 与全局写法实参相同", c =>
            {
                GameObject o = c.Arg<GameObject>(0) ?? c.Self as GameObject;
                if (o != null)
                {
                    o.SkillSubState = c.Int32(c.Has(1) ? 1 : 0);
                }
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_SetShake", "sq_setShake" })
            {
                DefineAny(n, I, "震屏 (对象, 强度, 持续毫秒):成员写法 obj.sq_setShake(obj, ...) 与全局写法实参相同;只发出事件", c =>
                {
                    Events.Shake(c.Arg<GameObject>(0) ?? c.Self as GameObject, c.Int32(1), c.Int32(2));
                    return c.ReturnNothing();
                });
            }
            Define("getXDistance", I, "沿自己朝向到另一个对象的 x 距离:对方在前方为正", M, c =>
            {
                GameObject a = c.SelfAs<GameObject>();
                GameObject b = c.Arg<GameObject>(0);
                return c.Return(b == null ? 0 : (b.X - a.X) * Forward(a));
            });
            DefineFree("sq_IsDownKey", I, "某个热键此刻是否按住 (对象, 键, 标志):标志忽略", c => c.Return(Input.IsDown(c.Int32(1))));
            DefineFree("sq_IsEnterCommand", T, "玩家此刻是否输入了第 i 号指令 (对象, 编号):没有指令输入,总是否", c => c.Return(false));
            DefineFree("sq_SetKeyxEnable", T, "启用/禁用第 i 号指令键 (对象, 编号, 是否):只记数", c => c.ReturnNothing());
            Define("startRapidInput", I, "开始统计连打次数(清零)", M, c =>
            {
                c.SelfAs<GameObject>().RapidInputCount = 0;
                return c.ReturnNothing();
            });
            Define("addRapidInput", I, "连打次数加一", M, c =>
            {
                c.SelfAs<GameObject>().RapidInputCount++;
                return c.ReturnNothing();
            });
            Define("getRapidInputFrequency", I, "连打次数", M, c => c.Return(c.SelfAs<GameObject>().RapidInputCount));
            Define("isEnd", I, "计时器:是否已到上限;依附体:是否已结束(失效或超过有效时间)", M, c =>
            {
                if (c.Self is GameTimer t)
                {
                    return c.Return(t.IsEnd);
                }
                if (c.Self is Appendage a)
                {
                    return c.Return(!a.Valid || (a.ValidTimeMs >= 0 && a.AgeMs >= a.ValidTimeMs));
                }
                return c.Return(false);
            });
            Define("Restart", T, "粒子发生器重新开始:占位", M, c => c.ReturnNothing());
            DefineFree("sq_getNewAttackInfoPacket", I, "创建一个空的攻击信息(随后由脚本逐项填字段)", c =>
            {
                var ai = new AttackInfo { Source = "packet" };
                Transient(ai);
                return c.Return(ai);
            });
            DefineFree("sq_SetfRotateAngle", T, "设置动画的旋转角:只记数", c => c.ReturnNothing());
            foreach (string n in new[] { "sq_ChangeDrawLayer", "sq_SetEnumDrawLayer", "sq_SetSpeedToMoveParticle" })
            {
                DefineFree(n, T, "绘制层与粒子速度:只记数", c => c.ReturnNothing());
            }
            DefineFree("sq_GetMoveParent", T, "取跟随移动的父对象:没有实现,返回 null", c => c.ReturnNull());
            DefineFree("sq_AddDrawOnlyAniFromParent", I, "在父对象身上放一个只画不算的动画 (父对象, 动画路径, x, y, z):x 向前、朝左时镜像;返回这个特效对象", c =>
            {
                GameObject parent = c.Arg<GameObject>(0);
                GameObject fx = CreateDrawOnly(parent, PvfPath.Normalize(c.Str(1)), true, true);
                if (parent != null)
                {
                    fx.X = parent.X + Forward(parent) * c.Int32(2);
                    fx.Y = parent.Y + c.Int32(3);
                    fx.Z = parent.Z + c.Int32(4);
                }
                return c.Return(fx);
            });
            Define("isCurrentCutomAniIndex", I, "当前动画是不是 [etc motion] 的第 i 个(接口名拼写照原样)", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                string path = o.Def?.CustomAnimationPath(c.Int32(0));
                return c.Return(path != null && string.Equals(path, o.Animator.Path, StringComparison.Ordinal));
            });
            Define("isCurrentAnimationIndex", I, "当前动画是不是 [etc motion] 的第 i 个", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                string path = o.Def?.CustomAnimationPath(c.Int32(0));
                return c.Return(path != null && string.Equals(path, o.Animator.Path, StringComparison.Ordinal));
            });
            DefineFree("setCurrentAnimationFromCutomIndex", I, "按 [etc motion] 的下标设置当前动画 (对象, 下标)(接口名拼写照原样)", c =>
            {
                GameObject o = c.Arg<GameObject>(0);
                if (o != null)
                {
                    o.HitObjects.Clear();
                    SetAnimation(o, o.Def?.CustomAnimationPath(c.Int32(1)));
                }
                return c.ReturnNothing();
            });
            DefineFree("sq_IsSameAni", I, "两个动画对象是不是同一个动画(按动画文件)", c =>
            {
                AnimationObject a = c.Arg<AnimationObject>(0);
                AnimationObject b = c.Arg<AnimationObject>(1);
                return c.Return(a != null && b != null && string.Equals(a.Animator.Path, b.Animator.Path, StringComparison.Ordinal));
            });

            // ---- 对象的标识与转换
            DefineFree("sq_GetObjectId", I, "对象在世界里的编号 (对象)", c => c.Return(c.Arg<GameObject>(0)?.Id ?? 0L));
            DefineFree("sq_GetObjectByObjectId", I, "按编号找对象 (参照对象, 编号);已失效或不存在为 null", c =>
            {
                return c.Return(_byId.TryGetValue(c.Int(1), out NativeObject o) && o is GameObject g && g.Valid ? g : null);
            });
            DefineFree("sq_GetCNRDObjectToCollisionObject", I, "把对象当成碰撞对象用:角色、怪物、被动对象返回自己,否则 null", c =>
                c.Return(c.Arg<GameObject>(0) is GameObject o && IsObjectType(o, 1) ? o : null));
            DefineFree("sq_GetCNRDObjectToCharacter", I, "把对象当成角色用:是角色返回自己,否则 null", c =>
                c.Return(c.Arg<GameObject>(0) is GameObject o && o.Kind == ObjectKind.Character ? o : null));
            DefineFree("sq_GetCNRDObjectToAICharacter", T, "把对象当成 AI 角色用:没有 AI 角色,总是 null", c => c.ReturnNull());
            DefineFree("sq_GetCNRDObjectToFlashScreen", T, "把对象当成闪屏对象用:没有,总是 null", c => c.ReturnNull());
            DefineFree("sq_GetMyControlObject", I, "本机玩家控制的角色", c => c.Return(MyControlObject()));
            DefineFree("sq_GetMyMasterCharacter", I, "本机玩家的主角色", c => c.Return(MyControlObject()));
            DefineFree("sq_GetMyControlObjectSize", I, "本机玩家控制的对象个数", c => c.Return(MyControlObject() == null ? 0 : 1));
            DefineFree("sq_SetMyControlCharacter", T, "切换本机控制的角色:占位", c => c.ReturnNothing());

            // ---- 目标查找
            DefineFree("sq_FindTarget", I,
                "找最近的敌人 (对象, 前方偏移, x 范围, y 范围, z 范围):以对象前方 偏移 处为中心,x 在 ±x 范围内、纵深差与高度差不超过范围;没有为 null", c =>
                {
                    GameObject self = c.Arg<GameObject>(0);
                    if (self == null)
                    {
                        return c.ReturnNull();
                    }
                    int cx = self.X + Forward(self) * c.Int32(1);
                    return c.Return(FindEnemyInBox(self, cx - c.Int32(2), cx + c.Int32(2), c.Int32(3), c.Int32(4), null));
                });
            Define("sq_FindFirstTarget", I, "找前方最近的敌人 (起点 x, 终点 x, y 范围, z 范围):x 相对对象、朝向为正", M, c =>
            {
                GameObject self = c.SelfAs<GameObject>();
                int f = Forward(self);
                return c.Return(FindEnemyInBox(self, self.X + f * c.Int32(0), self.X + f * c.Int32(1), c.Int32(2), c.Int32(3), null));
            });
            Define("sq_FindNextTarget", I, "在上一个目标附近找下一个敌人 (上一个目标, x 范围, z 范围)", M, c =>
            {
                GameObject self = c.SelfAs<GameObject>();
                GameObject prev = c.Arg<GameObject>(0);
                if (prev == null)
                {
                    return c.ReturnNull();
                }
                return c.Return(FindEnemyInBox(self, prev.X - c.Int32(1), prev.X + c.Int32(1), 200, c.Int32(2), prev));
            });
            DefineFree("sq_FindShootingTarget", T, "按射击角度找目标:没有实现,总是 null", c => c.ReturnNull());
            Define("getCollisionObjectNumber", I, "对象管理器:碰撞对象(角色、怪物、被动对象)的个数", M, c => c.Return(CollisionObjects().Count));
            Define("getCollisionObject", I, "对象管理器:第 i 个碰撞对象(按创建顺序);越界为 null", M, c =>
            {
                List<GameObject> list = CollisionObjects();
                int i = c.Int32(0);
                return c.Return((uint)i < (uint)list.Count ? list[i] : null);
            });
            Define("getCollisionObjectIndex", I, "对象管理器:对象在碰撞对象列表里的下标;不在为 -1", M, c => c.Return(CollisionObjects().IndexOf(c.Arg<GameObject>(0))));
            DefineFree("sq_GetDistanceObject", I, "两个对象的水平距离 (甲, 乙, 是否含纵深):只比 x 差,含纵深时按直线距离", c =>
            {
                GameObject a = c.Arg<GameObject>(0);
                GameObject b = c.Arg<GameObject>(1);
                if (a == null || b == null)
                {
                    return c.Return(0);
                }
                double dx = a.X - b.X;
                double dy = c.BoolOr(2, false) ? a.Y - b.Y : 0;
                return c.Return((int)Math.Sqrt(dx * dx + dy * dy));
            });
            DefineFree("sq_GetCenterXPos", I, "命中区域的中心 x (区域)", c => c.Return(c.Arg<BoundingBox>(0) is BoundingBox b ? (b.Box.MinX + b.Box.MaxX) / 2 : 0));
            DefineFree("sq_GetCenterYPos", I, "命中区域的中心 y (区域)", c => c.Return(c.Arg<BoundingBox>(0) is BoundingBox b ? (b.Box.MinY + b.Box.MaxY) / 2 : 0));
            DefineFree("sq_GetCenterZPos", I, "命中区域的中心 z (区域)", c => c.Return(c.Arg<BoundingBox>(0) is BoundingBox b ? (b.Box.MinZ + b.Box.MaxZ) / 2 : 0));

            // ---- 对象管理器、舞台、主控件、鼠标(没有界面与地图,只给最小答案)
            Define("getObjectManager", I, "对象管理器(服务对象)", M, c => c.Return(Service(ref _objectManager, "ObjectManager")));
            DefineFree("sq_GetObjectManagerStage", I, "舞台(服务对象) (对象)", c => c.Return(Service(ref _stage, "Stage")));
            Define("getMainControl", I, "舞台的主控件(服务对象)", M, c => c.Return(Service(ref _mainControl, "MainControl")));
            DefineFree("sq_GetMap", I, "地图(服务对象) (对象):没有地图数据", c => c.Return(Service(ref _map, "Map")));
            Define("getFieldXPos", I, "屏幕 x 换算成场地 x (屏幕 x, 图层):没有摄像机,原样返回", M, c => c.Return(c.Int32(0) + Input.CameraX));
            Define("getFieldYPos", I, "屏幕 y 换算成场地纵深 y (屏幕 y, 高度, 图层):没有摄像机,原样返回", M, c => c.Return(c.Int32(0)));
            Define("getFieldZPos", I, "屏幕 y 换算成场地高度 z (屏幕 y, 纵深 y, 图层):没有摄像机,原样返回", M, c => c.Return(c.Int32(0)));
            foreach (string n in new[] { "IsLBDown", "IsLBUp", "IsRBDown", "IsRBUp" })
            {
                bool left = n[2] == 'L';
                bool down = n.EndsWith("Down", StringComparison.Ordinal);
                Define(n, I, "鼠标按键状态(主控件)", M, c => c.Return(down && (left ? Input.MouseLeftDown : Input.MouseRightDown)));
            }
            SqNativeTable mouse = Vm.DefineTable("IMouse", true);
            DefineStatic(mouse, "GetXPos", I, "鼠标的屏幕 x", c => c.Return(Input.MouseX));
            DefineStatic(mouse, "GetYPos", I, "鼠标的屏幕 y", c => c.Return(Input.MouseY));
            DefineStatic(mouse, "IsLBDown", I, "鼠标左键是否按住", c => c.Return(Input.MouseLeftDown));
            DefineStatic(mouse, "IsRBDown", I, "鼠标右键是否按住", c => c.Return(Input.MouseRightDown));
        }
    }
}
