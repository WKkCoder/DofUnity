using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;

// 证据:E-130(.act 触发器与行为运行期;被动对象 .obj 的 [basic action]/[etc action] 由 .act 驱动,[CUSTOM] n 对应 [etc action] 下标是推断)。
namespace Dnf.Play
{
    /// <summary>
    /// 被动对象的一个动作:动作脚本(触发器与行为组)加它的基础动画。老式的 .obj 只有 [basic motion],没有 .act,这时程序是空的。
    /// 数据一律从用户自备的 PVF 里读,本类不含原版内容。
    /// </summary>
    public sealed class PassiveAction
    {
        public PassiveAction(ActProgram program, AniFile ani, string aniPath, HeroStrikeInfo? defaultStrike = null)
        {
            Program = program ?? ActProgram.Empty;
            Ani = ani != null && ani.Frames.Length > 0 ? ani : null;
            AniPath = aniPath;
            Timeline = Ani == null ? null : new AniTimeline(Ani);
            DefaultStrike = defaultStrike;
        }

        public ActProgram Program { get; }

        /// <summary>
        /// 这个动作 .act 自带的默认攻击信息(程序一级的 [DEFAULT ATTACKINFO],M69,E-167);没写或读不到是 null。
        /// 对象进入这个动作时盖过 .obj 的 [attack info](推断:客户端的程序默认在动作开始时套上)。
        /// </summary>
        public HeroStrikeInfo? DefaultStrike { get; }

        /// <summary>基础动画;读不到或没有帧时为 null(这个动作放不出来)。</summary>
        public AniFile Ani { get; }

        public string AniPath { get; }

        public AniTimeline Timeline { get; }

        /// <summary>有触发器可以跑(没有的动作不用建运行器)。</summary>
        public bool HasTriggers => Program.Triggers.Count > 0;
    }

    /// <summary>
    /// 一种被动对象的"配方":基础动作、[etc action] 里的各个自定义动作、攻击信息与消失条件。
    /// 和 <see cref="ProjectileTemplate"/> 不同,这里的对象由 .act 的触发器与行为驱动(速度、动作切换、攻击框开关、再生成别的对象),而不是 .ptl 的飞行定义。
    /// </summary>
    public sealed class PassiveTemplate
    {
        private readonly PassiveAction[] _etc;

        public PassiveTemplate(int index, Dnf.Game.PassiveObjectDef def, PassiveAction basic, PassiveAction[] etc, HeroStrikeInfo strike)
        {
            Index = index;
            Def = def ?? throw new ArgumentNullException(nameof(def));
            Basic = basic ?? throw new ArgumentNullException(nameof(basic));
            _etc = etc ?? Array.Empty<PassiveAction>();
            Strike = strike;
            TimeLimitMs = def.TimeLimitMs;
            DestroyOnAnimationEnd = def.DestroyOnAnimationEnd;
        }

        public int Index { get; }

        public Dnf.Game.PassiveObjectDef Def { get; }

        public PassiveAction Basic { get; }

        /// <summary>[etc action] 的动作个数。</summary>
        public int EtcCount => _etc.Length;

        /// <summary>[CUSTOM] n 对应的动作;越界或那一项读不出动画时为 null。</summary>
        public PassiveAction Etc(int n)
        {
            PassiveAction a = (uint)n < (uint)_etc.Length ? _etc[n] : null;
            return a != null && a.Ani != null ? a : null;
        }

        /// <summary>
        /// [etc action] 第 n 项,不看动画读得出读不出(越界或 .act 读不到为 null)。触发器开关板用它:
        /// 客户端的“某一族动作的触发器池”跟动画无关,脚本问起时只看这一项的 .act 有没有触发器(E-158)。
        /// </summary>
        public PassiveAction EtcAction(int n) => (uint)n < (uint)_etc.Length ? _etc[n] : null;

        /// <summary>.obj 的 [attack info]:对象的攻击框打中主角时用(没有时全是默认值)。</summary>
        public HeroStrikeInfo Strike { get; }

        /// <summary>[object destroy condition] 里 [time limite] 的毫秒数;没有为 -1。</summary>
        public int TimeLimitMs { get; }

        /// <summary>[object destroy condition] 含 [on end of animation]:动画播完(非循环)就消失。</summary>
        public bool DestroyOnAnimationEnd { get; }

        /// <summary>
        /// 按 .obj 的编号配出模板;.obj 读不到,或基础动作没有动画(.act 没有 [BASE ANI] 又没有 [basic motion])时返回 null(这一个对象放不出来)。
        /// </summary>
        public static PassiveTemplate Load(DnfAssets assets, GameData data, ActLibrary library, int index)
        {
            Dnf.Game.PassiveObjectDef def = data.GetPassiveObject(index);
            if (def == null)
            {
                return null;
            }
            PassiveAction basic = library.Action(def.BasicActionPath, def.BasicMotionPath);
            if (basic == null || basic.Ani == null)
            {
                return null;
            }
            string[] etcPaths = def.EtcActionPaths;
            var etc = new PassiveAction[etcPaths.Length];
            for (int i = 0; i < etc.Length; i++)
            {
                etc[i] = etcPaths[i] == null ? null : library.Action(etcPaths[i], null);
            }
            return new PassiveTemplate(index, def, basic, etc, HeroStrikeInfo.Read(data, def.AttackInfoPath));
        }
    }

    /// <summary>
    /// .act 程序库:按 PVF 路径读出、编译并缓存动作脚本(读不到的路径记为 null,不再重读),再按被动对象编号配出 <see cref="PassiveTemplate"/>。
    /// 一个房间会话一份;读文件只在第一次用到时发生(进房间时给怪物的出招动作预读一遍,见 <see cref="Preload"/>)。
    /// </summary>
    public sealed class ActLibrary
    {
        /// <summary>预读被动对象时顺着"对象再生成对象"往下走的最深层数(再深的留到第一次用到时读)。</summary>
        public const int PreloadDepth = 3;

        private readonly DnfAssets _assets;
        private readonly GameData _data;
        private readonly Dictionary<string, ActProgram> _programs = new Dictionary<string, ActProgram>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, PassiveTemplate> _templates = new Dictionary<int, PassiveTemplate>();
        private readonly HashSet<int> _summonCodes = new HashSet<int>();
        private readonly Dictionary<string, PassiveAction> _swapActions = new Dictionary<string, PassiveAction>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HeroStrikeInfo?> _strikes = new Dictionary<string, HeroStrikeInfo?>(StringComparer.OrdinalIgnoreCase);

        public ActLibrary(DnfAssets assets, GameData data)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// 预读过的程序里 [SUMMON MONSTER] 写到的怪物编号(随机选的编号一并收,E-134);房间会话照这个表把召唤物的模型提前读好。
        /// 随预读增长,遍历前请复制。
        /// </summary>
        public IReadOnlyCollection<int> SummonCodes => _summonCodes;

        /// <summary>读到并编好的程序个数(审计与测试用)。</summary>
        public int ProgramCount
        {
            get
            {
                int n = 0;
                foreach (ActProgram p in _programs.Values)
                {
                    if (p != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>配出的被动对象模板个数。</summary>
        public int TemplateCount
        {
            get
            {
                int n = 0;
                foreach (PassiveTemplate t in _templates.Values)
                {
                    if (t != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>要了但配不出来的被动对象个数(.obj 缺失或没有基础动画)。</summary>
        public int MissingTemplateCount
        {
            get
            {
                int n = 0;
                foreach (PassiveTemplate t in _templates.Values)
                {
                    if (t == null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>读出并编译 <paramref name="path"/> 的 .act;路径为空、读不到时返回 null。结果缓存。</summary>
        public ActProgram Program(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (_programs.TryGetValue(path, out ActProgram cached))
            {
                return cached;
            }
            ActProgram program = null;
            ScriptLine[] lines = _data.ReadScript(path);
            if (lines != null)
            {
                program = ActProgram.Compile(path, ActParser.Parse(lines));
            }
            _programs[path] = program;
            return program;
        }

        /// <summary>
        /// 一个动作:.act 的程序加它的主动画(.act 里第一个 [BASE ANI],路径相对 .act 所在目录);
        /// .act 没有主动画时用 <paramref name="motionPath"/>(老式 [basic motion])。两边都没有时返回 null。
        /// </summary>
        public PassiveAction Action(string actPath, string motionPath)
        {
            ActProgram program = Program(actPath);
            string aniPath = program?.BaseAniPath != null ? PvfPath.ResolveRelativeTo(actPath, program.BaseAniPath) : motionPath;
            if (aniPath == null)
            {
                return program == null ? null : new PassiveAction(program, null, null, DefaultStrike(program));
            }
            return new PassiveAction(program, _assets.LoadAni(aniPath), aniPath, DefaultStrike(program));
        }

        /// <summary>
        /// 一份 .atk 的命中信息(M69,E-167;[CHANGE ATTACKINFO] 与程序默认攻击信息用):<paramref name="path"/> 已是 PVF 规范路径(见 <see cref="PvfPath.ResolveRelativeTo"/>)。
        /// 路径为空或文件读不到返回 null(也缓存,不重试)——不同于 <see cref="HeroStrikeInfo.Read"/> 读不到给默认值,调用方要分得清"读不到就什么都不做"。
        /// </summary>
        public HeroStrikeInfo? Strike(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (_strikes.TryGetValue(path, out HeroStrikeInfo? cached))
            {
                return cached;
            }
            HeroStrikeInfo? strike = _data.GetAttackInfo(path)?.Data == null ? (HeroStrikeInfo?)null : HeroStrikeInfo.Read(_data, path);
            _strikes[path] = strike;
            return strike;
        }

        /// <summary>
        /// 程序一级 [DEFAULT ATTACKINFO] 指的攻击信息(路径相对程序自己所在的目录,E-167);程序没写、路径解析不出或文件读不到时为 null。
        /// </summary>
        public HeroStrikeInfo? DefaultStrike(ActProgram program)
        {
            if (program?.DefaultAttackInfo == null || program.Path == null)
            {
                return null;
            }
            return Strike(PvfPath.ResolveRelativeTo(program.Path, program.DefaultAttackInfo));
        }

        /// <summary>
        /// [CHANGE ACTION FILE] 换进来的动作(M57,E-151):<paramref name="actPath"/> 已是 PVF 规范路径(见 <see cref="PvfPath.ResolveRelativeTo"/>)。
        /// 结果缓存(读不到也记 null 不重试),所以预读过的换文件第一次用到时不再读盘。.act 没有主动画时动作只带程序,动画是 null。
        /// </summary>
        public PassiveAction SwapAction(string actPath)
        {
            if (string.IsNullOrEmpty(actPath))
            {
                return null;
            }
            if (_swapActions.TryGetValue(actPath, out PassiveAction cached))
            {
                return cached;
            }
            PassiveAction action = Action(actPath, null);
            _swapActions[actPath] = action;
            return action;
        }

        /// <summary>被动对象 <paramref name="index"/> 的模板;配不出来返回 null(也缓存,不重试)。</summary>
        public PassiveTemplate Passive(int index)
        {
            if (_templates.TryGetValue(index, out PassiveTemplate cached))
            {
                return cached;
            }
            PassiveTemplate template = PassiveTemplate.Load(_assets, _data, this, index);
            _templates[index] = template;
            return template;
        }

        /// <summary>
        /// 预读 <paramref name="program"/> 的行为里写死了编号的 [CREATE PASSIVEOBJECT](随机选的编号一并读),
        /// 并顺着这些对象自己的动作往下读 <see cref="PreloadDepth"/> 层:免得第一次放出来的那一帧才去读文件。
        /// </summary>
        public void Preload(ActProgram program, int depth = 0)
        {
            if (program == null || depth >= PreloadDepth)
            {
                return;
            }
            DefaultStrike(program);      // 程序自带的默认攻击信息一并读好(M69)
            foreach (ActBehaviorGroup group in program.Behaviors)
            {
                foreach (ActBehavior b in group.Items)
                {
                    if (b.Kind == ActBehaviorKind.ChangeAttackInfo && b.FilePath != null)
                    {
                        // 脚本运行中要换上的攻击信息:预先读好,第一次换的那一帧不读盘(M69)
                        Strike(PvfPath.ResolveRelativeTo(program.Path, b.FilePath));
                        continue;
                    }
                    if (b.Kind == ActBehaviorKind.ChangeActionFile && b.FilePath != null)
                    {
                        // 脚本运行中要换进来的动作文件:预先读好(动画一并载入),并顺着它自己的脚本往下读(M57)
                        Preload(SwapAction(PvfPath.ResolveRelativeTo(program.Path, b.FilePath))?.Program, depth + 1);
                        continue;
                    }
                    if (b.Kind == ActBehaviorKind.Summon && b.Index != null)
                    {
                        foreach (int code in b.Index.Candidates())
                        {
                            _summonCodes.Add(code);
                        }
                        continue;
                    }
                    if (b.Kind != ActBehaviorKind.CreatePassive || b.Index == null)
                    {
                        continue;
                    }
                    foreach (int index in b.Index.Candidates())
                    {
                        if (_templates.ContainsKey(index))
                        {
                            continue;
                        }
                        PassiveTemplate t = Passive(index);
                        if (t == null)
                        {
                            continue;
                        }
                        Preload(t.Basic.Program, depth + 1);
                        for (int n = 0; n < t.EtcCount; n++)
                        {
                            Preload(t.Etc(n)?.Program, depth + 1);
                        }
                    }
                }
            }
        }
    }
}
