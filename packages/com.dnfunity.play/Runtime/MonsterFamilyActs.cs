using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物的非出招动作族(M53,证据 E-146):客户端的怪物对象为每一族动作各存一个 .act 动作对象——
    /// 站立、行走、坐下、受击(两个)、倒地、翻倒、过程(proc,没有动画、一直在跑)、最后一幕(last,血量归零之后跑);
    /// 这些 .act 里同样有触发器(放被动对象、设速度、切招、开关别的触发器……),出招 .act 之外的这一半也要有人宿主。
    /// </summary>
    public enum MonsterActFamily : byte
    {
        Stay,
        Move,
        Sit,
        Damage1,
        Damage2,
        Down,
        Overturn,
        Last,
        Proc,
    }

    /// <summary>
    /// 一只怪物各动作族的 .act 程序(只装有触发器的;其余族没有可跑的东西)。程序与动作动画取自 <see cref="ActLibrary"/>,和被动对象的动作同一套。
    /// 数据一律从用户自备的 PVF 里读,本类不含原版内容。
    /// </summary>
    public sealed class MonsterFamilyActs
    {
        public const int FamilyCount = 9;

        private readonly PassiveAction[] _actions = new PassiveAction[FamilyCount];

        /// <summary>至少一族有触发器。</summary>
        public bool Any { get; private set; }

        /// <summary>
        /// 自定义动作列表(.mob 的 [etc action],下标就是脚本 [SET ACTION] [CUSTOM] n 的 n;M62-b,证据 E-159):读得出来的项各一个动作(带主动画与触发器程序,
        /// 没有触发器的也留着——它的动画要放),读不出来的项是 null;没写这个块或一项也读不出来时为 null。
        /// </summary>
        public PassiveAction[] Customs { get; private set; }

        /// <summary>至少有一个自定义动作。</summary>
        public bool HasCustoms => Customs != null;

        /// <summary><paramref name="family"/> 的动作;没写 .act 或没有触发器为 null。</summary>
        public PassiveAction Get(MonsterActFamily family)
        {
            return _actions[(int)family];
        }

        /// <summary>
        /// 按 .mob 里读出的各族路径从程序库取动作,同时预读它们会放出来的被动对象;没有一族带触发器时返回 null。
        /// 受击两族(damage 1/2)各自一个动作;动作的主动画取 .act 的第一个 [BASE ANI],没有时这一族的动画为空(照样跑触发器,帧号取 0)。
        /// </summary>
        public static MonsterFamilyActs Build(ActLibrary library, MonsterActPaths paths)
        {
            if (library == null || paths == null || !paths.Any)
            {
                return null;
            }
            var result = new MonsterFamilyActs();
            result.AddCustoms(library, paths.Etc);
            result.Add(library, MonsterActFamily.Stay, paths.Waiting);
            result.Add(library, MonsterActFamily.Move, paths.Move);
            result.Add(library, MonsterActFamily.Sit, paths.Sit);
            result.Add(library, MonsterActFamily.Damage1, paths.Damage1);
            result.Add(library, MonsterActFamily.Damage2, paths.Damage2);
            result.Add(library, MonsterActFamily.Down, paths.Down);
            result.Add(library, MonsterActFamily.Overturn, paths.Overturn);
            result.Add(library, MonsterActFamily.Last, paths.Last);
            result.Add(library, MonsterActFamily.Proc, paths.Proc);
            return result.Any || result.HasCustoms ? result : null;
        }

        private void AddCustoms(ActLibrary library, IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return;
            }
            var actions = new PassiveAction[paths.Count];
            bool any = false;
            for (int i = 0; i < actions.Length; i++)
            {
                PassiveAction action = string.IsNullOrEmpty(paths[i]) ? null : library.Action(paths[i], null);
                if (action == null)
                {
                    continue;
                }
                if (action.HasTriggers)
                {
                    library.Preload(action.Program);
                }
                actions[i] = action;
                any = true;
            }
            Customs = any ? actions : null;
        }

        private void Add(ActLibrary library, MonsterActFamily family, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            PassiveAction action = library.Action(path, null);
            if (action == null || !action.HasTriggers)
            {
                return;
            }
            library.Preload(action.Program);
            _actions[(int)family] = action;
            Any = true;
        }
    }
}
