using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;

// 宠物脚本(.cre)里与"城镇里看得见、跟得上"有关的字段。状态层(Dnf.Game 的 Pets)只读成长、喂养与神器格,不读这些;
// 这里另读一份,不改状态层。
// 观察(本机真实数据逐只核对,只记个数与形状,数值不入库):每只宠物脚本都有 [basic motion](相对脚本所在目录的 .ani 路径,全部能解析到动画文件),
// [walk motion] 与 [run motion] 有的写了路径、有的写成空字符串、有的没有这一项;[floating height] 与 [move speed](五个整数)每只都有;
// [character span] 只有一部分宠物写了,写了的都是负数。
// 推断(没有读到客户端里对应的代码):[character span] 是宠物相对主角的水平停靠距离(负数 = 主角背后);[move speed] 的五个数与主角的速度表同一个排法
// (横向直走、横向斜走、纵向直走、纵向斜走、另一项);[floating height] 是离地高度。本文件只把它们读出来,怎么用见 PetFollower。
namespace Dnf.Play
{
    /// <summary>城镇里宠物的动作类别。</summary>
    public enum PetMotion
    {
        /// <summary>站着不动(.cre 的 [basic motion])。</summary>
        Stand = 0,

        /// <summary>在追主角(.cre 的 [walk motion];没有就用站立动画)。</summary>
        Walk = 1,

        /// <summary>离主角很远时快追(.cre 的 [run motion];没有就退回 [walk motion],再没有就用站立动画)。</summary>
        Run = 2,
    }

    /// <summary>一只宠物的外观与移动参数(来自 .cre,读不到的项是缺省:没有动画、高度 0、没写停靠距离、没写速度)。</summary>
    public sealed class PetLook
    {
        public const string BasicMotionTag = "[basic motion]";
        public const string WalkMotionTag = "[walk motion]";
        public const string RunMotionTag = "[run motion]";
        public const string FloatingHeightTag = "[floating height]";
        public const string CharacterSpanTag = "[character span]";
        public const string MoveSpeedTag = "[move speed]";

        /// <summary><c>[move speed]</c> 的整数个数(五个才认,别的个数当没写)。</summary>
        public const int MoveSpeedCount = 5;

        private readonly int[] _moveSpeed;

        public PetLook(int petId, string scriptPath, string basicAni, string walkAni, string runAni, int floatingHeight, int? characterSpan, IReadOnlyList<int> moveSpeed)
        {
            PetId = petId;
            ScriptPath = scriptPath ?? string.Empty;
            BasicAni = string.IsNullOrEmpty(basicAni) ? null : basicAni;
            WalkAni = string.IsNullOrEmpty(walkAni) ? null : walkAni;
            RunAni = string.IsNullOrEmpty(runAni) ? null : runAni;
            FloatingHeight = floatingHeight;
            CharacterSpan = characterSpan;
            _moveSpeed = new int[moveSpeed != null && moveSpeed.Count == MoveSpeedCount ? MoveSpeedCount : 0];
            for (int i = 0; i < _moveSpeed.Length; i++)
            {
                _moveSpeed[i] = moveSpeed[i];
            }
        }

        /// <summary>宠物编号(<c>creature.lst</c> 里的键)。</summary>
        public int PetId { get; }

        /// <summary>宠物脚本(.cre)的完整路径。</summary>
        public string ScriptPath { get; }

        /// <summary>站立动画的完整路径(已按 .cre 所在目录解析);没写或写成空字符串是 null。</summary>
        public string BasicAni { get; }

        /// <summary>行走动画的完整路径;没写、写成空字符串是 null。</summary>
        public string WalkAni { get; }

        /// <summary>奔跑动画的完整路径;没写、写成空字符串是 null。</summary>
        public string RunAni { get; }

        /// <summary>离地高度(<c>[floating height]</c>,世界 z;没写是 0)。</summary>
        public int FloatingHeight { get; }

        /// <summary>停靠距离(<c>[character span]</c>,推断为沿主角朝向的水平偏移,负数在背后);没写是 null。</summary>
        public int? CharacterSpan { get; }

        /// <summary><c>[move speed]</c> 的五个整数;没写或个数不对是空表。</summary>
        public IReadOnlyList<int> MoveSpeed => _moveSpeed;

        /// <summary>三个动画里至少有一个能用。</summary>
        public bool HasAppearance => BasicAni != null || WalkAni != null || RunAni != null;

        /// <summary>
        /// 某个动作该用的动画路径:奔跑没有就退回行走,行走没有就退回站立;站立没有就是 null(这只宠物画不出来)。
        /// 退回的次序是本项目自己的选择(原版怎么退没有样本)。
        /// </summary>
        public string AniOf(PetMotion motion)
        {
            if (motion == PetMotion.Run && RunAni != null)
            {
                return RunAni;
            }
            if (motion != PetMotion.Stand && WalkAni != null)
            {
                return WalkAni;
            }
            return BasicAni;
        }
    }

    /// <summary>宠物脚本里外观与移动字段的读取(只读这一小部分标签,别的标签忽略)。</summary>
    public static class PetLookScript
    {
        /// <summary>
        /// 读一份已经按标签拆好的宠物脚本。<paramref name="scriptPath"/> 是脚本自己的完整路径,动画路径按它所在的目录解析;
        /// <paramref name="lines"/> 为 null(脚本读不到)时得到一份什么都没有的外观。同一个标签写了几次以最后一次为准。
        /// </summary>
        public static PetLook Parse(ScriptLine[] lines, int petId, string scriptPath)
        {
            string basic = null;
            string walk = null;
            string run = null;
            int height = 0;
            int? span = null;
            List<int> speed = null;
            if (lines != null)
            {
                foreach (ScriptLine line in lines)
                {
                    if (line.TagIs(PetLook.BasicMotionTag))
                    {
                        basic = Resolve(scriptPath, line.FirstString());
                    }
                    else if (line.TagIs(PetLook.WalkMotionTag))
                    {
                        walk = Resolve(scriptPath, line.FirstString());
                    }
                    else if (line.TagIs(PetLook.RunMotionTag))
                    {
                        run = Resolve(scriptPath, line.FirstString());
                    }
                    else if (line.TagIs(PetLook.FloatingHeightTag))
                    {
                        double? value = line.FirstNumber();
                        height = value.HasValue ? (int)value.Value : 0;
                    }
                    else if (line.TagIs(PetLook.CharacterSpanTag))
                    {
                        double? value = line.FirstNumber();
                        span = value.HasValue ? (int)value.Value : (int?)null;
                    }
                    else if (line.TagIs(PetLook.MoveSpeedTag))
                    {
                        speed = line.Ints();
                    }
                }
            }
            return new PetLook(petId, scriptPath, basic, walk, run, height, span, speed);
        }

        private static string Resolve(string scriptPath, string relative)
        {
            return string.IsNullOrEmpty(relative) ? null : PvfPath.ResolveRelativeTo(scriptPath ?? string.Empty, relative);
        }
    }

    /// <summary>
    /// 按宠物定义取外观(读 <see cref="PetDef.Path"/> 指向的宠物脚本,结果按宠物编号缓存)。
    /// 脚本读不到时给一份"没有外观"的 <see cref="PetLook"/>(宠物照样存在、照样能跟,只是画不出来),不抛异常。
    /// </summary>
    public sealed class PetLookCatalog
    {
        private readonly GameData _data;
        private readonly Dictionary<int, PetLook> _looks = new Dictionary<int, PetLook>();

        public PetLookCatalog(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>宠物的外观;<paramref name="def"/> 为 null 返回 null。</summary>
        public PetLook Get(PetDef def)
        {
            if (def == null)
            {
                return null;
            }
            if (_looks.TryGetValue(def.Id, out PetLook cached))
            {
                return cached;
            }
            ScriptLine[] lines = string.IsNullOrEmpty(def.Path) ? null : _data.ReadScript(def.Path);
            PetLook look = PetLookScript.Parse(lines, def.Id, def.Path);
            _looks[def.Id] = look;
            return look;
        }
    }
}
