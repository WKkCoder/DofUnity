using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 战斗支持类原生接口:范围判断、自动瞄准的占位、技能威力别名、声音与镜头的占位,以及静态表 CNRDObject(按角度求坐标)。
    /// 没有音频、镜头与地图系统,这些只记数;能给出"中性答案"的(自动瞄准没有目标)按中性答案返回。
    /// </summary>
    public sealed partial class GameWorld
    {
        private void RegisterSupportNatives()
        {
            const NativeForm M = NativeForm.Member;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            // ---- 范围判断
            DefineFree("sq_IsinMapArea", I,
                "目标是否在对象前方的矩形里 (对象, 目标, 前方起点, 前方终点, y 半宽):x 沿对象朝向量,高度不比较;没有地图,不检查可走区域", c =>
                {
                    GameObject a = c.Arg<GameObject>(0);
                    GameObject b = c.Arg<GameObject>(1);
                    if (a == null || b == null)
                    {
                        return c.Return(false);
                    }
                    int dx = (b.X - a.X) * Forward(a);
                    int lo = Math.Min(c.Int32(2), c.Int32(3));
                    int hi = Math.Max(c.Int32(2), c.Int32(3));
                    return c.Return(dx >= lo && dx <= hi && Math.Abs(b.Y - a.Y) <= Math.Abs(c.Int32(4)));
                });

            // ---- 自动瞄准:没有目标时的中性答案
            DefineFree("sq_GetShootingHorizonAngle", T, "自动瞄准的水平角 (对象, y, z, 搜索角, 最大距离):没有实现,返回 0(朝正前方)", c => c.Return(0f));
            DefineFree("sq_GetShootingVerticalAngle", T, "自动瞄准的垂直角:没有实现,返回 0", c => c.Return(0f));
            Define("sq_getAttractAimPosX", T, "吸引瞄准点的 x (x, y, 标志):没有实现,原样返回给定的 x", M, c => c.Return(c.Int32(0)));
            Define("sq_getAttractAimPosY", T, "吸引瞄准点的 y (x, y, 标志):没有实现,原样返回给定的 y", M, c => c.Return(c.Int32(1)));

            // ---- 技能威力与攻击倍率
            Define("sq_GetSkillPower", I, "技能威力 (技能, 列):[level info] 第 列 列在技能当前等级的值", M, c =>
                c.Return(PowerFromLevelTable(c.SelfAs<GameObject>(), c.Int32(0), c.Int32(1), -1, 1f)));
            Define("applyBasicAttackUp", T, "把普通攻击强化套到攻击信息上 (攻击信息, 状态):没有实现", M, c => c.ReturnNothing());

            // ---- 技能等级与属性换算
            Define("isOverSkillLevel", I, "技能等级是否不低于给定值 (技能, 等级):按脚本注释'特定等级以上'推断为 >=", M, c =>
                c.Return(c.SelfAs<GameObject>().GetSkillLevel(c.Int32(0)) >= c.Int32(1)));
            DefineFree("sq_GetAbilityConvertRate", T, "能力值换算成属性的比率 (对象, 换算表类型):没有换算表,返回 1.0", c => c.Return(1f));
            Define("sq_AddHpMaxUp", T, "在依附体名下挂一个最大生命提升 (名字, 对象, 来源, 有效毫秒, 提升值, ……):只返回占位对象,不改最大生命", M, c =>
            {
                Appendage a = c.SelfAs<Appendage>();
                var up = new ServiceObject("HpMaxUp");
                Transient(up, LooseChangeStatusLifetimeMs);
                a.HpMaxUps[c.Str(0)] = up;
                return c.Return(up);
            });
            Define("sq_getHpMaxUp", T, "依附体名下按名字挂的最大生命提升;没有为 null", M, c =>
                c.Return(c.SelfAs<Appendage>().HpMaxUps.TryGetValue(c.Str(0), out ServiceObject up) ? up : null));
            Define("setCustomRotate", I, "设置自定义旋转 (是否生效, 角度):只记录,画面不转;角度单位未确认", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                o.ApplyCustomRotate = c.Bool(0);
                o.CustomRotateAngle = c.Float(1);
                return c.ReturnNothing();
            });
            DefineFree("sq_SetCustomRotate", I, "设置自定义旋转并启用 (对象, 角度):只记录", c =>
            {
                if (c.Arg<GameObject>(0) is GameObject o)
                {
                    o.ApplyCustomRotate = true;
                    o.CustomRotateAngle = c.Float(1);
                }
                return c.ReturnNothing();
            });
            Define("getCustomRotateAngle", I, "自定义旋转的角度", M, c => c.Return(c.SelfAs<GameObject>().CustomRotateAngle));
            Define("isApplyCustomRotate", I, "自定义旋转是否生效", M, c => c.Return(c.SelfAs<GameObject>().ApplyCustomRotate));
            Define("setEventOnStart", I, "计时器:周期事件是否在起点就触发一次 (是否)", M, c =>
            {
                c.SelfAs<GameTimer>().EventOnStart = c.Bool(0);
                return c.ReturnNothing();
            });
            Define("setState", T, "向依附体发一个状态消息 (标志编号, 整数向量):没有对应的回调约定,只记数", M, c => c.ReturnNothing());
            DefineFree("sq_SendMessage", I, "给对象发消息 (对象, 消息类型, 参数 1, 参数 2):只记录最近一次的参数 1(OBJECT_MESSAGE_GHOST 等的效果宿主没有实现)", c =>
            {
                if (c.Arg<GameObject>(0) is GameObject o)
                {
                    o.ObjectMessages[c.Int32(1)] = c.Int32(2);
                }
                return c.ReturnNothing();
            });
            DefineFree("sq_PostDelayedMessage", I, "延时给对象发消息 (对象, 消息类型, 参数 1, 参数 2, 延时毫秒):到点后同 sq_SendMessage", c =>
            {
                GameObject o = c.Arg<GameObject>(0);
                int type = c.Int32(1);
                int value = c.Int32(2);
                if (o != null)
                {
                    PostDelayed(c.Int32(4), () => o.ObjectMessages[type] = value);
                }
                return c.ReturnNothing();
            });
            Define("sq_LoadSkillEffectAni", I, "预载一个技能用的特效动画 (技能, 路径):只记录到 PreloadedSkillEffects,脚本里用的时候仍按路径创建", M, c =>
            {
                int skill = c.Int32(0);
                if (!PreloadedSkillEffects.TryGetValue(skill, out List<string> list))
                {
                    list = new List<string>();
                    PreloadedSkillEffects[skill] = list;
                }
                list.Add(c.Str(1));
                return c.ReturnNothing();
            });
            DefineFree("sq_AddMessage", I, "弹出一条界面提示 (提示编号):只记录到 UiMessages", c =>
            {
                UiMessages.Add(c.Int32(0));
                return c.ReturnNothing();
            });
            DefineFree("sq_IsValidActiveStatus", T, "对象是否处于某个主动状态(冰冻、定身等) (对象, 状态):没有主动状态系统,恒为假", c => c.Return(false));
            Define("getWeaponSubType", T, "装备的武器子类型:没有装备系统,返回 -1(与 WEAPON_SUBTYPE_* 都不相等)", M, c => c.Return(-1));
            Define("startCantUseSkillWarning", T, "界面上闪'不能使用技能'的警告:没有界面", M, c => c.ReturnNothing());
            Define("addCustomAnimation", T, "给对象追加一个自定义动画 (动画):只记数", M, c => c.ReturnNothing());
            Define("flushSetStatePacket", I, "立刻处理对象排队中的状态切换", M, c =>
            {
                DrainPackets(c.SelfAs<GameObject>());
                return c.ReturnNothing();
            });
            Define("getPassiveObjectIndex", I, "被动对象自己的编号(.obj 在 .lst 里的编号)", M, c => c.Return(c.SelfAs<GameObject>().PassiveId));
            foreach (string n in new[] { "getSkillManager", "getCurrentSkillTree", "getCommandChecker" })
            {
                Define(n, T, "技能管理器 / 技能树 / 指令检查器(AI 角色用):没有这些系统,返回一个占位对象,对它调用的方法只记数", M, c =>
                {
                    var service = new ServiceObject(n);
                    Transient(service, LooseChangeStatusLifetimeMs);
                    return c.Return(service);
                });
            }
            foreach (string n in new[] { "setCommandChecker", "addAllKeyCommand", "flushCommandEnable" })
            {
                Define(n, T, "AI 角色的指令系统:没有实现,只记数", M, c => c.ReturnNothing());
            }
            Define("endSkillCoolTime", T, "立刻结束一个技能的冷却 (技能):没有冷却系统,只记数", M, c => c.ReturnNothing());
            Define("getLastAlsObject", T, "动画上最后一个自动图层对象:没有图层系统,返回 null", M, c => c.ReturnNull());
            Define("sq_bottomcreateFlash", T, "在底层创建闪屏:没有屏幕特效", M, c => c.ReturnNothing());

            // ---- 声音、镜头、受击表现:只记数
            Define("playSound", T, "播放声音 (名字, 编号, ……):没有音频", M, c => c.ReturnNothing());
            Define("sq_SetObjectParticlePos", T, "粒子位置微调:没有粒子系统", M, c => c.ReturnNothing());
            Define("sq_setSuperArmor", T, "设置霸体:只记数", M, c => c.ReturnNothing());
            Define("sq_SetMaxHitCounterPerObject", T, "限制每个目标被一次攻击命中的次数:只记数", M, c => c.ReturnNothing());
            DefineFree("sq_SetPause", T, "暂停对象 (对象, 类型, 毫秒):没有实现", c => c.ReturnNothing());
            DefineFree("sq_MoveToAppendageForce", T, "把目标拽向依附体来源 (目标, 对象, 来源, 距离……):没有实现,目标不动", c => c.ReturnNothing());
            foreach (string n in new[] { "sq_setXScrollStart", "sq_setXScrollStop" })
            {
                Define(n, T, "镜头横向卷动的开始与停止:没有镜头", M, c => c.ReturnNothing());
            }
            Define("sq_isMyControlObject", I, "同 isMyControlObject", M, c => c.Return(c.Self is GameObject o && o.IsMyControl));
            Define("getDefaultAnimation", I, "对象的默认动画(.obj / .chr 的 [basic motion])的独立动画对象;没有为 null", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                string path = o.Def?.MotionPath("[basic motion]"); // 已按定义文件所在目录解析成 PVF 路径
                return c.Return(path == null ? null : CreateAnimation(path));
            });

            // ---- 静态表 CNRDObject:按角度求坐标(角度单位是度,推断)
            SqNativeTable t = Vm.DefineTable("CNRDObject", true);
            DefineStatic(t, "getAngleDistanceXPos", I, "x + cos(角度) * 半径,朝左时取反 (x, 角度, 半径, 朝向)", c =>
            {
                double rad = c.Float(1) * Math.PI / 180.0;
                double sign = c.IntOr(3, Direction.Right) == Direction.Left ? -1.0 : 1.0;
                return c.Return((long)Math.Round(c.Float(0) + Math.Cos(rad) * c.Float(2) * sign));
            });
            DefineStatic(t, "getAngleDistanceYPos", I, "y + sin(角度) * 半径 (y, 角度, 半径, 标志);标志的含义未知,忽略", c =>
            {
                double rad = c.Float(1) * Math.PI / 180.0;
                return c.Return((long)Math.Round(c.Float(0) + Math.Sin(rad) * c.Float(2)));
            });
            DefineStatic(t, "getAngle", I, "从 (x0, y0) 指向 (x1, y1) 的角度,度,0 到 360 (x0, y0, x1, y1, 标志);标志的含义未知,忽略", c =>
            {
                double deg = Math.Atan2(c.Float(3) - c.Float(1), c.Float(2) - c.Float(0)) * 180.0 / Math.PI;
                return c.Return((float)(deg < 0 ? deg + 360.0 : deg));
            });
        }
    }
}
