using System;
using System.Collections.Generic;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 输入与按键的状态(脚本里 sq_IsKeyDown、sq_GetInputDirection 读的东西)。键值是脚本头文件里的 OPTION_HOTKEY_*:
    /// 上 0、左 1、下 2、右 3、攻击 4(X)、跳跃 5(C)、技能 6(Z)、技能2 7(空格)(观察,dnf_enum_header.nut)。
    /// </summary>
    public sealed class InputState
    {
        public const int HotkeyUp = 0;
        public const int HotkeyLeft = 1;
        public const int HotkeyDown = 2;
        public const int HotkeyRight = 3;
        public const int HotkeyAttack = 4;
        public const int HotkeyJump = 5;
        public const int HotkeySkill = 6;

        private readonly HashSet<int> _down = new HashSet<int>();

        /// <summary>鼠标位置(屏幕坐标)与按键;创造者类职业的技能靠鼠标指定位置。</summary>
        public int MouseX { get; set; }

        public int MouseY { get; set; }
        public bool MouseLeftDown { get; set; }
        public bool MouseRightDown { get; set; }

        /// <summary>摄像机的水平位置(屏幕 x 换算成场地 x 时加上它)。</summary>
        public int CameraX { get; set; }

        public void Set(int hotkey, bool down)
        {
            if (down)
            {
                _down.Add(hotkey);
            }
            else
            {
                _down.Remove(hotkey);
            }
        }

        public bool IsDown(int hotkey)
        {
            return _down.Contains(hotkey);
        }

        public void Clear()
        {
            _down.Clear();
        }
    }

    /// <summary>零散的原生接口:技能对象、施法时间、按键输入、模块类型、占位的表现类接口。</summary>
    public sealed partial class GameWorld
    {
        /// <summary>玩家的按键状态(本机玩家控制的角色读它)。</summary>
        public InputState Input { get; } = new InputState();

        /// <summary>脚本里 sq_GetCurrentModuleType() 的答案;默认 3(MODULE_TYPE_MAIN_GAME,一般地下城)。</summary>
        public int ModuleType { get; set; } = 3;

        private SkillObject SkillObjectOf(GameObject owner, int skillId)
        {
            GameObject chr = SkillOwner(owner);
            if (chr.GetSkillLevel(skillId) <= 0)
            {
                return null;
            }
            var skill = new SkillObject(chr, skillId);
            Transient(skill);
            return skill;
        }

        private void RegisterMiscNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeForm M = NativeForm.Member;
            const NativeForm G = NativeForm.Global;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            // ---- 技能与角色的查询
            Define("sq_GetSkill", I, "技能对象 (角色, 技能):已学才有,否则 null", G, c => c.Return(SkillObjectOf(c.SelfAs<GameObject>(), c.Int32(0))));
            Define("setSealActiveFunction", T, "技能对象:封印/解封主动功能,只记录", M, c =>
            {
                c.SelfAs<SkillObject>().SealActiveFunction = c.Bool(0);
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "isSealActiveFunction", "isSealFunction" })
            {
                Define(n, I, "技能对象:主动功能是否被封印", M, c => c.Return(c.SelfAs<SkillObject>().SealActiveFunction));
            }
            Define("isInCoolTime", T, "技能对象:是否在冷却中(冷却没有实现,总是否)", M, c => c.Return(false));
            Define("resetCurrentCoolTime", T, "技能对象:重置冷却(冷却没有实现)", M, c => c.ReturnNothing());
            Define("setCommandEnable", T, "技能对象:启用/禁用指令(同 setSkillCommandEnable 的对象写法)", M, c =>
            {
                SkillObject s = c.SelfAs<SkillObject>();
                if (c.Bool(0))
                {
                    s.Owner.DisabledSkillCommands.Remove(s.SkillId);
                }
                else
                {
                    s.Owner.DisabledSkillCommands.Add(s.SkillId);
                }
                return c.ReturnNothing();
            });
            Define("isExcutableState", I, "技能对象:当前状态是否在 [executable states] 里 (角色;接口名里的拼写照原样)", M, c =>
            {
                SkillObject s = c.SelfAs<SkillObject>();
                GameObject who = c.Arg<GameObject>(0) ?? s.Owner;
                SkillDef def = Data.GetSkill(SkillOwner(who).Job, s.SkillId);
                return c.Return(def == null || def.ExecutableStates.Length == 0 || Array.IndexOf(def.ExecutableStates, who.State) >= 0);
            });
            Define("sq_getJob", I, "角色的职业编号 (角色);传入 null 返回 -1。客户端 0x005aee70:取对象的'角色信息'(虚表槽 0x738)里偏移 0 的整数(E-046)", G, c =>
            {
                GameObject o = ObjOrNull(c);
                return c.Return(o == null ? -1 : SkillOwner(o).Job);
            });
            Define("sq_getGrowType", I,
                "角色的成长类型(转职/觉醒方向,编号含义按职业各自定义:GROW_TYPE_*) (角色);传入 null 返回 -1。客户端 0x005aee50:与 sq_getJob 读同一份'角色信息'(虚表槽 0x738),取偏移 0xc 的整数(E-046);"
                + "我们的角色没有觉醒信息,取对象的 GrowType 属性(默认 0 = 职业的基础类型)", G, c =>
                {
                    GameObject o = ObjOrNull(c);
                    return c.Return(o == null ? -1 : SkillOwner(o).GrowType);
                });
            Define("sq_GetCastTime", I, "技能的施法时间(毫秒) (角色, 技能, 等级):.skl 的 [casting time] 两个数按等级线性插值(推断)", G, c =>
            {
                GameObject chr = SkillOwner(c.SelfAs<GameObject>());
                SkillDef def = chr.Job < 0 ? null : Data.GetSkill(chr.Job, c.Int32(0));
                return c.Return(def == null ? 0 : def.Interpolate(def.CastingTime, c.Int32(1)));
            });
            Define("sq_StartDrawCastGauge", T, "显示施法条 (对象, 毫秒, 是否):只记数", G, c => c.ReturnNothing());
            Define("sq_EndDrawCastGauge", T, "取消施法条 (对象)", G, c => c.ReturnNothing());
            DefineFree("sq_GetCurrentModuleType", I, "当前游戏模块的类型(MODULE_TYPE_*),默认 3 = 一般地下城", c => c.Return(ModuleType));
            foreach (string n in new[] { "getThrowElement", "getThrowIndex", "getDustParticleType", "getAttackIndex" })
            {
                Define(n, T, "角色的投掷属性/普攻段数等运行时状态:没有实现,返回 0", M, c => c.Return(0));
            }

            Define("sq_SetStaticSpeedInfo", T, "设置速度类型的静态速度信息 (类型, 类型, 倍率, 倍率, 倍率, 倍率):只记录,速度系统还没有", M, c => c.ReturnNothing());
            DefineFree("sq_SetFrameDelayTime", I, "改写动画某一帧的延时 (动画, 帧, 毫秒):只作用于这个动画对象的播放器", c =>
            {
                c.Arg<AnimationObject>(0)?.Animator.SetFrameDelay(c.Int32(1), c.Int32(2));
                return c.ReturnNothing();
            });
            DefineFree("sq_isPVPMode", I, "是不是决斗场模式", c => c.Return(PvpMode));
            Define("callBackAllObject", T, "对所有某类对象调用回调 (对象, 事件, 类型):没有实现", M, c => c.ReturnNothing());
            Define("getMyPassiveObjectCount", I, "这个对象创建的、指定编号的有效被动对象个数 (编号)", M, c =>
            {
                GameObject creator = c.SelfAs<GameObject>();
                int id = c.Int32(0);
                int n = 0;
                foreach (GameObject o in _objects)
                {
                    if (o.Valid && o.Kind == ObjectKind.PassiveObject && o.PassiveId == id && ReferenceEquals(o.Parent, creator))
                    {
                        n++;
                    }
                }
                return c.Return(n);
            });
            DefineFree("sq_GetPassiveObjectByState", I, "按编号与状态找创建者名下的被动对象 (创建者, 被动对象编号, 状态);没有为 null", c =>
            {
                GameObject creator = c.Arg<GameObject>(0);
                int id = c.Int32(1);
                int state = c.Int32(2);
                foreach (GameObject o in _objects)
                {
                    if (o.Valid && o.Kind == ObjectKind.PassiveObject && o.PassiveId == id && o.State == state && ReferenceEquals(o.Parent, creator))
                    {
                        return c.Return(o);
                    }
                }
                return c.ReturnNull();
            });
            foreach (string n in new[] { "sq_AddSkillLoad", "sq_RemoveSkillLoad", "sq_RemoveMoveParticle", "sq_removeAttractAimPointMark", "sq_AddAimPointMark", "sq_ZStop" })
            {
                Define(n, T, "占位:技能充能、移动粒子、瞄准标记、纵向停止、旋转", M, c => c.ReturnNothing());
            }
            Define("sq_GetSkillLoad", T, "技能充能层数:没有实现,返回 0", M, c => c.Return(0));
            Define("sq_GetAimPosX", T, "瞄准点的 x (x, y, 标志):没有鼠标瞄准,原样返回 x", M, c => c.Return(c.Int32(0)));
            foreach (string n in new[] { "isDownSkillLastKey", "sq_IsEnterSkillLastKeyUnits" })
            {
                Define(n, I, "技能的最后一个键是否仍按住(蓄力):没有按键输入时为否", M, c => c.Return(Input.IsDown(InputState.HotkeySkill)));
            }
            DefineFree("sq_SetZVelocity", T, "设置纵向速度 (对象, 速度, 加速度):没有纵向物理,只记录", c => c.ReturnNothing());
            DefineFree("sq_moveWithParent", T, "让子对象跟随父对象移动 (子, 父):占位", c => c.ReturnNothing());
            DefineFree("sq_SetVelocity", T, "设置某个轴的速度 (对象, 轴 0/1, 速度):原版的速度单位未知,这里当作每秒的距离记录", c =>
            {
                GameObject o = c.Arg<GameObject>(0);
                if (o != null)
                {
                    int speed = (int)Math.Round(Math.Abs(c.Float(2)));
                    int dir = c.Float(2) < 0 ? Direction.Left : Direction.Right;
                    if (c.Int32(1) == 0)
                    {
                        o.Move.SpeedX = speed;
                        o.Move.DirectionX = speed == 0 ? Direction.Neutral : dir;
                    }
                    else if (c.Int32(1) == 1)
                    {
                        o.Move.SpeedY = speed;
                        o.Move.DirectionY = speed == 0 ? Direction.Neutral : (dir == Direction.Left ? Direction.Up : Direction.Down);
                    }
                    o.Move.Active = true;
                }
                return c.ReturnNothing();
            });
            DefineFree("sq_GetVelocity", T, "读取某个轴的速度 (对象, 轴):没有纵向物理,返回 0", c => c.Return(0f));

            // ---- 输入
            DefineFree("sq_IsKeyDown", I, "某个热键(OPTION_HOTKEY_*)此刻是否按住 (键, 子类型);子类型忽略", c => c.Return(Input.IsDown(c.Int32(0))));
            Define("sq_GetInputDirection", I,
                "当前方向键输入 (轴):轴 0 横向,返回 ENUM_DIRECTION 的左/右/NEUTRAL;轴 1 纵向,返回上/下/NEUTRAL;左右同按取 NEUTRAL", B, c =>
                {
                    bool horizontal = c.Int32(0) == 0;
                    bool negative = Input.IsDown(horizontal ? InputState.HotkeyLeft : InputState.HotkeyUp);
                    bool positive = Input.IsDown(horizontal ? InputState.HotkeyRight : InputState.HotkeyDown);
                    if (negative == positive)
                    {
                        return c.Return(Direction.Neutral);
                    }
                    return c.Return(horizontal ? (negative ? Direction.Left : Direction.Right) : (negative ? Direction.Up : Direction.Down));
                });
            Define("sq_IsEnterSkill", I, "玩家此刻是否在用这个技能的快捷键:是返回快捷键编号(这里 0),否返回 -1(依据 != -1 的用法推断)", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                return c.Return(o.RequestedSkill >= 0 && o.RequestedSkill == c.Int32(0) ? 0 : -1);
            });

            // ---- 表现类:只记数,不影响逻辑
            foreach (string n in new[]
            {
                "sq_addFlashScreen", "sq_setFullScreenEffect", "sq_createAttackObjectWithPath", "sq_EffectLayerAppendage", "sq_EffectLayerAppendageOnlyBody", "sq_SetApplyConversionSkill",
                "sq_SetChangeStatusIntoAttackInfo", "sq_setCustomHitEffectFileName", "sq_DrawSpecificFrameEffect_SIMPLE", "sq_drawCurrentFrame",
                "sq_AddParticleObject", "sq_AddObjectParticleCreater", "sq_SetMoveParticle", "sq_CreateParticleByGlobal", "sq_SetAimPointMarkPosition",
            })
            {
                Define(n, T, "表现类接口:只记数", B, c => c.ReturnNothing());
            }
            DefineFree("sq_ToRadian", I, "角度转弧度", c => c.Return((float)(c.Float(0) * Math.PI / 180.0)));
            DefineFree("sq_RGBA", T, "颜色:打包成 (a<<24|r<<16|g<<8|b);格式未验证", c =>
                c.Return(((c.Int(3) & 255) << 24) | ((c.Int(0) & 255) << 16) | ((c.Int(1) & 255) << 8) | (c.Int(2) & 255)));
        }
    }
}
