using System;
using Dnf.Data;
using Dnf.Squirrel;

// 证据:每个接口的状态与依据见 docs/native-audit.tsv(E-046、E-048)。
namespace Dnf.Game
{
    /// <summary>
    /// 动画对象的原生接口:取当前动画、创建独立动画、帧号与时间的查询、速度与跳帧;
    /// 纯表现的接口(图像缩放、着色、图层叠加)只记数。时间单位都是毫秒,帧号从 0 起。
    /// </summary>
    public sealed partial class GameWorld
    {
        /// <summary>每次 <see cref="Step"/> 推进的毫秒数(sq_AnimationProc 等独立动画的推进量)。</summary>
        internal int StepMs { get; private set; }

        /// <summary>对象当前动画的脚本视图;对象没有动画时返回 null。</summary>
        internal AnimationObject AnimationOf(GameObject o)
        {
            if (o.Animator.Clip == null)
            {
                return null;
            }
            if (o.CurrentAnimationView == null)
            {
                o.CurrentAnimationView = new AnimationObject(o.Animator, true);
                Handle(o.CurrentAnimationView);
            }
            return o.CurrentAnimationView;
        }

        /// <summary>创建一个独立的动画对象(自己的播放器);路径读不到时仍返回对象,但没有片段。</summary>
        internal AnimationObject CreateAnimation(string path)
        {
            var animator = new Animator();
            animator.Set(path == null ? null : Data.GetAnimation(path), path);
            var ani = new AnimationObject(animator, false);
            Transient(ani);
            return ani;
        }

        private static string ResolveForObject(GameObject o, string relative)
        {
            return o.Def == null ? PvfPath.Normalize(relative) : PvfPath.ResolveRelativeTo(o.Def.Path, relative);
        }

        /// <summary>参数 i(或没有参数时的 this)是动画对象时取它的播放器;是游戏对象时取对象自己的播放器。</summary>
        private Animator AnimatorOf(NativeCall c, int i)
        {
            NativeObject o = c.Has(i) ? c.Arg<NativeObject>(i) : c.Self;
            if (o is AnimationObject a)
            {
                return a.Animator;
            }
            return o is GameObject g ? g.Animator : null;
        }

        private static int CurrentTimeMs(Animator a)
        {
            if (a == null || a.Clip == null)
            {
                return 0;
            }
            return (int)Math.Min(a.TotalPlayedMs, int.MaxValue);
        }

        private void RegisterAnimationNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeForm M = NativeForm.Member;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            // ---- 取得与创建
            foreach (string n in new[] { "sq_GetCurrentAnimation", "getCurrentAnimation", "sq_GetCurrentAni", "sq_getCurrentAni" })
            {
                Define(n, I, "对象当前动画的视图(随对象的动画一起变);没有动画为 null", B, c => c.Return(c.Self is GameObject o ? AnimationOf(o) : null));
            }
            Define("getCustomAnimation", I, "按 [etc motion] 下标创建一个独立的动画对象(被动对象脚本里 getCustomAnimation(0) 等,与 CUSTOM_ANI_* 同一张表)", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                return c.Return(CreateAnimation(o.Def?.CustomAnimationPath(c.Int32(0))));
            });
            Define("sq_CreateCNRDAnimation", I, "创建一个独立的动画对象 (路径,相对角色或被动对象的定义文件所在目录)", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                return c.Return(CreateAnimation(ResolveForObject(o, c.Str(0))));
            });
            DefineFree("sq_CreateAnimation", I, "创建一个独立的动画对象 (前缀, 路径):路径从数据根目录算起;前缀在已见的调用里都是空串,忽略", c =>
                c.Return(CreateAnimation(PvfPath.Normalize(c.Str(1)))));
            Define("sq_AddStateLayerAnimation", T, "给对象的状态层加一层动画 (层, 动画, x, y):只记数,不参与逻辑", M, c => c.ReturnNothing());

            // ---- 帧与时间
            DefineAny("sq_GetFrameStartTime", I, "第 frame 帧开始的时刻(毫秒,原速) (动画, 帧)", c =>
                c.Return(AnimatorOf(c, 0) is Animator a ? a.FrameStartMs(c.Int32(1)) : 0));
            Define("GetFrameStartTime", I, "同 sq_GetFrameStartTime (帧)", M, c => c.Return(c.SelfAs<AnimationObject>().Animator.FrameStartMs(c.Int32(0))));
            Define("getDelaySum", I,
                "动画总时长(毫秒,原速):getDelaySum(是否按速度折算) 取全部帧;getDelaySum(起, 止) 取 [起, 止) 帧的延时之和。折算参数的含义是推断(脚本里全是 false)", M, c =>
                {
                    Animator a = c.SelfAs<AnimationObject>().Animator;
                    if (c.ArgCount >= 2)
                    {
                        return c.Return(a.DelaySumMs(c.Int32(0), c.Int32(1)));
                    }
                    return c.Return(a.TotalMs);
                });
            foreach (string n in new[] { "GetCurrentFrameIndex" })
            {
                Define(n, I, "动画当前帧号", M, c => c.Return(c.SelfAs<AnimationObject>().Animator.FrameIndex));
            }
            foreach (string n in new[] { "sq_GetCurrentFrameIndex", "sq_ani_GetCurrentFrameIndex", "sq_GetAnimationFrameIndex" })
            {
                DefineAny(n, I, "动画当前帧号:sq_X(动画或对象) 或 obj.sq_X(动画)", c => c.Return(AnimatorOf(c, 0) is Animator a ? a.FrameIndex : 0));
            }
            DefineAny("sq_GetCurrentTime", I, "动画已播放的时间(毫秒,按帧延时累计,不含循环回绕) (动画)", c => c.Return(CurrentTimeMs(AnimatorOf(c, 0))));
            Define("getCurrentTime", I, "同 sq_GetCurrentTime", M, c => c.Return(CurrentTimeMs(c.SelfAs<AnimationObject>().Animator)));
            foreach (string n in new[] { "setCurrentFrameWithChildLayer", "setCurrentFrame" })
            {
                Define(n, I, "跳到指定帧并从那里继续播放(帧号越界取端点)", M, c =>
                {
                    c.SelfAs<AnimationObject>().Animator.SetFrame(c.Int32(0));
                    return c.ReturnNothing();
                });
            }
            Define("setSpeedRate", I, "播放速度,百分比(100.0 为原速,200.0 为两倍):依据 setSpeedRate(200.0) 一类调用推断", M, c =>
            {
                c.SelfAs<AnimationObject>().Animator.SpeedPermille = (int)System.Math.Round(c.Float(0) * 10f);
                return c.ReturnNothing();
            });
            DefineAny("sq_SetAnimationSpeedRate", I, "播放速度,百分比 (动画, 速度):同 setSpeedRate", c =>
            {
                if (AnimatorOf(c, 0) is Animator a)
                {
                    a.SpeedPermille = (int)System.Math.Round(c.Float(1) * 10f);
                }
                return c.ReturnNothing();
            });
            DefineAny("sq_IsEnd", I, "动画是否已播完 (动画)", c => c.Return(AnimatorOf(c, 0) is Animator a && a.Ended));
            DefineAny("sq_ani_IsEnd", I, "动画是否已播完 (动画)", c => c.Return(AnimatorOf(c, 0) is Animator a && a.Ended));
            DefineAny("sq_AnimationProc", I, "推进一个独立动画一帧(本次世界推进的毫秒数);对象自己的动画不用调用 (动画)", c =>
            {
                if (c.Arg<AnimationObject>(0) is AnimationObject a && !a.SharedWithObject)
                {
                    a.Animator.Advance(StepMs, null, null);
                }
                return c.ReturnNothing();
            });
            Define("Proc", I, "推进一个独立动画一帧(同 sq_AnimationProc)", M, c =>
            {
                AnimationObject a = c.SelfAs<AnimationObject>();
                if (!a.SharedWithObject)
                {
                    a.Animator.Advance(StepMs, null, null);
                }
                return c.ReturnNothing();
            });
            Define("setEnd", T, "设置动画的强制结束标志:只记录", M, c =>
            {
                c.SelfAs<AnimationObject>().ForcedEnd = c.Bool(0);
                return c.ReturnNothing();
            });
            Define("isLoop", I, "动画是否循环", M, c => c.Return(c.SelfAs<AnimationObject>().Animator.Clip != null && c.SelfAs<AnimationObject>().Animator.Clip.Loops));

            // ---- 只画不算的特效对象
            DefineFree("sq_CreatePooledObject", I, "用动画对象创建特效对象 (动画, 播完是否自动销毁):脚本随后设置位置与朝向,再用 sq_AddObject 交给世界", c =>
                c.Return(CreateDrawOnly(null, c.Arg<AnimationObject>(0)?.Animator.Path, c.BoolOr(1, true), false)));
            Define("sq_createCNRDPooledObject", I, "同 sq_CreatePooledObject,初始位置取这个对象的 (动画, 播完是否自动销毁)", M, c =>
                c.Return(CreateDrawOnly(c.SelfAs<GameObject>(), c.Arg<AnimationObject>(0)?.Animator.Path, c.BoolOr(1, true), false)));
            DefineFree("sq_CreateDrawOnlyObject", I, "直接创建并加入一个特效对象 (父对象, 动画路径, 绘制层, 播完是否自动销毁):位置与朝向取父对象的;路径从数据根目录算起", c =>
                c.Return(CreateDrawOnly(c.Arg<GameObject>(0), PvfPath.Normalize(c.Str(1)), c.BoolOr(3, true), true)));
            Define("sq_AddObject", I, "把创建好的特效对象交给世界 (子对象[, 类型, 标志]):父对象是调用者", B, c =>
            {
                AddToWorld(c.Arg<GameObject>(0), c.Self as GameObject);
                return c.ReturnNothing();
            });
            Define("sq_createCNRDAnimation", I, "同 sq_CreateCNRDAnimation", M, c =>
                c.Return(CreateAnimation(ResolveForObject(c.SelfAs<GameObject>(), c.Str(0)))));
            foreach (string n in new[] { "sq_GetCustomAni", "sq_getCustomAni" })
            {
                Define(n, I, "同 getCustomAnimation (下标)", B, c => c.Return(CreateAnimation(c.SelfAs<GameObject>().Def?.CustomAnimationPath(c.Int32(0)))));
            }
            DefineFree("sq_GetDelaySum", I, "动画总时长(毫秒,原速) (动画)", c => c.Return(c.Arg<AnimationObject>(0) is AnimationObject a ? a.Animator.TotalMs : 0));
            DefineFree("sq_GetAniFrameNumber", I, "动画的帧数 (动画)", c => c.Return(c.Arg<AnimationObject>(0)?.Animator.Clip?.Frames.Length ?? 0));
            DefineFree("sq_SetAnimationFrameIndex", I, "跳到指定帧 (动画, 帧)", c =>
            {
                c.Arg<AnimationObject>(0)?.Animator.SetFrame(c.Int32(1));
                return c.ReturnNothing();
            });
            DefineFree("sq_SetAnimationCurrentTimeByFrame", I, "把动画设到第 frame 帧的起点 (动画, 帧, ……)", c =>
            {
                c.Arg<AnimationObject>(0)?.Animator.SetFrame(c.Int32(1));
                return c.ReturnNothing();
            });
            DefineAny("sq_Rewind", I, "让动画回到第一帧重新播放 (动画或对象)", c =>
            {
                AnimatorOf(c, 0)?.SetFrame(0);
                return c.ReturnNothing();
            });
            DefineFree("sq_GetAniRealImageSize", T, "动画图像的实际宽度 (动画, 朝向):读不到图像尺寸,返回固定的 100", c => c.Return(100));
            Define("sq_IsKeyFrameFlag", I, "当前帧的关键帧标志是不是这个值", M, c =>
            {
                GameObject o = c.SelfAs<GameObject>();
                AniFrame f = o.Animator.CurrentFrame;
                return c.Return(f != null && f.TryGetKeyFrameFlag(out int flag) && flag == c.Int32(0));
            });

            // ---- 纯表现:只记数
            foreach (string n in new[]
            {
                "setImageRate", "setImageRateFromOriginal", "setImageRateFromOriginalOnlyChild", "setEffectLayer", "setRGBA",
                "setAutoLayerWorkAnimationAddSizeRate", "setAttackBoundingBoxSizeRate", "applyBoundingBoxRate", "setFrameDelay", "setCurrentEffectNoChild",
                "resizeWithChild", "resize", "setNeverApplyAnotherPlayersEffectAlphaRate", "setIsApplyAnotherPlayersEffectAlphaRate",
            })
            {
                Define(n, T, "动画的表现类设置(图像缩放、着色、图层叠加、判定框缩放):只记数,不影响逻辑", M, c => c.ReturnNothing());
            }
            Define("addLayerAnimation", I, "把另一个动画叠在这个动画上作图层 (图层编号, 动画, 是否在前):图层随宿主一起推进,宿主换动画时丢掉;图层编号与前后顺序只影响绘制,不记录", M, c =>
            {
                if (c.Arg<AnimationObject>(1) is AnimationObject layer)
                {
                    c.SelfAs<AnimationObject>().Animator.AddLayer(layer.Animator);
                }
                return c.ReturnNothing();
            });
            Define("getDelayBySpeedRate", I, "按当前播放速度折算后的延时 (毫秒)", M, c =>
            {
                Animator a = c.SelfAs<AnimationObject>().Animator;
                return c.Return((int)(c.Int(0) * 1000L / System.Math.Max(1, a.SpeedPermille)));
            });
        }
    }
}
