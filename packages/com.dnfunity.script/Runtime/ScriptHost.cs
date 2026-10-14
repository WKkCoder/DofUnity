using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Squirrel;

namespace Dnf.Script
{
    /// <summary>加载结果的汇总(`dnf sq-check` 与测试用)。</summary>
    public sealed class ScriptLoadSummary
    {
        public bool EntryOk { get; internal set; }

        public int LoadedScripts { get; internal set; }

        public int ScriptFiles { get; internal set; }

        public int States { get; internal set; }

        public int PassiveObjects { get; internal set; }

        public int Errors { get; internal set; }

        public int Missing { get; internal set; }
    }

    /// <summary>
    /// 脚本宿主:一个 Squirrel 虚拟机,加上原版引擎对脚本的加载约定(证据 E-035):
    /// 入口 sqr/loadstate.nut 用 sq_RunScript("相对 sqr/ 的路径") 运行各职业的 *_load_state.nut;它们调用
    /// IRDSQRCharacter.pushScriptFiles / pushState / pushPassiveObj(复仇者用 CNAvenger.* 的同名方法)登记内容,
    /// 登记的同时运行那份脚本(因为后面的 pushState 参数里直接用了头文件里才定义的常量)。
    /// 宿主不含任何原版内容:脚本都从 <see cref="IScriptSource"/>(用户自备的 Script.pvf)读取。
    /// </summary>
    public sealed class ScriptHost : IDisposable
    {
        public const string EntryScript = "loadstate.nut";

        /// <summary>定义 sq_InitFrameIndices 的脚本。它不在加载链里,谁在什么时候加载它没有证据(推断:引擎启动时自己加载)。</summary>
        public const string InitCharacterScript = "init_character.nut";

        /// <summary>CNAvenger 登记的职业编号:牧师(ENUM_CHARACTERJOB_PRIEST = 4);复仇者是牧师的转职。</summary>
        public const long PriestJob = 4;

        private readonly IScriptSource _source;
        private readonly List<ScriptRegistration> _registrations = new List<ScriptRegistration>();
        private readonly List<string> _loaded = new List<string>();
        private readonly HashSet<string> _loadedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _missing = new SortedSet<string>(StringComparer.Ordinal);
        private readonly List<string> _errors = new List<string>();
        private readonly SortedDictionary<string, int> _unknownNames = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ScriptFunction>> _definedFunctions = new Dictionary<string, List<ScriptFunction>>(StringComparer.OrdinalIgnoreCase);
        private readonly SqVm _vm = new SqVm();

        public ScriptHost(IScriptSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _vm.RegisterGlobal("sq_RunScript", c =>
            {
                if (!RunScript(c.String(0), out string error))
                {
                    throw new SqNativeException(error ?? "加载失败");
                }
                return c.ReturnNothing();
            });
            _vm.RegisterGlobal("isDebugMode", c => c.Return(false));
            DefineRegistrationTable("CNAvenger", false);
            DefineRegistrationTable("IRDSQRCharacter", true);
            _vm.SetMissingGlobalHandler(name =>
            {
                if (!StubUnknownFunctions || ScriptText.LooksLikeConstant(name))
                {
                    // 找不到的常量(或严格模式下的任何名字):记一次查找
                    _unknownNames[name] = _unknownNames.TryGetValue(name, out int n) ? n + 1 : 1;
                    return null;
                }
                // 替身函数:查找时登记名字,之后每次被调用计一次(Squirrel 会把替身缓存在根表里,不会再经过这里)
                if (!_unknownNames.ContainsKey(name))
                {
                    _unknownNames[name] = 0;
                }
                return c =>
                {
                    _unknownNames[name] = _unknownNames[name] + 1;
                    return c.ReturnNull();
                };
            });
        }

        /// <summary>
        /// 脚本向还没实现的全局名字要东西时:为真(默认)就给一个返回 null 的替身函数并记下名字(<see cref="UnknownNames"/>),
        /// 全大写的名字(常量)除外;为假就报"不存在"。
        /// </summary>
        public bool StubUnknownFunctions { get; set; } = true;

        public SqVm Vm
        {
            get { return _vm; }
        }

        public IReadOnlyList<ScriptRegistration> Registrations
        {
            get { return _registrations; }
        }

        /// <summary>按运行先后列出已运行的脚本(规范化路径)。</summary>
        public IReadOnlyList<string> LoadedScripts
        {
            get { return _loaded; }
        }

        /// <summary>请求过但数据里没有的脚本路径。</summary>
        public IReadOnlyCollection<string> MissingScripts
        {
            get { return _missing; }
        }

        /// <summary>加载过程里的错误(每项一行)。</summary>
        public IReadOnlyList<string> Errors
        {
            get { return _errors; }
        }

        /// <summary>脚本读过但没有定义的全局名字:替身函数记被调用的次数(只读不调用的记 0),找不到的常量记查找次数。</summary>
        public IReadOnlyDictionary<string, int> UnknownNames
        {
            get { return _unknownNames; }
        }

        /// <summary>一份脚本里用 `function 名(参数)` 定义的函数(只有已运行的脚本有,按规范化路径查)。</summary>
        public bool TryGetDefinedFunctions(string resolvedPath, out List<ScriptFunction> functions)
        {
            return _definedFunctions.TryGetValue(resolvedPath, out functions);
        }

        /// <summary>运行入口脚本,再运行 init_character.nut。返回入口脚本是否成功。</summary>
        public ScriptLoadSummary LoadAll()
        {
            bool ok = RunScript(EntryScript, out string error);
            if (!ok)
            {
                _errors.Add(EntryScript + ": " + FirstLine(error));
            }
            if (!RunScript(InitCharacterScript, out string error2))
            {
                _errors.Add(InitCharacterScript + ": " + FirstLine(error2));
            }
            return Summarize(ok);
        }

        public ScriptLoadSummary Summarize(bool entryOk)
        {
            int files = 0;
            int states = 0;
            int passives = 0;
            foreach (ScriptRegistration r in _registrations)
            {
                switch (r.Kind)
                {
                    case RegistrationKind.ScriptFile: files++; break;
                    case RegistrationKind.State: states++; break;
                    default: passives++; break;
                }
            }
            return new ScriptLoadSummary
            {
                EntryOk = entryOk,
                LoadedScripts = _loaded.Count,
                ScriptFiles = files,
                States = states,
                PassiveObjects = passives,
                Errors = _errors.Count,
                Missing = _missing.Count,
            };
        }

        /// <summary>
        /// 运行一份脚本(路径相对 sqr/,大小写与 '\' 不敏感)。同一份脚本再次请求时仍会再运行(原版的行为未知;加载链里没有重复)。
        /// </summary>
        public bool RunScript(string relativeToSqr, out string error)
        {
            string path = PvfPath.Normalize("sqr/" + relativeToSqr);
            if (!_source.TryRead(path, out byte[] data))
            {
                _missing.Add(path);
                error = "PVF 里没有 " + path;
                return false;
            }
            if (_loadedSet.Add(path))
            {
                _loaded.Add(path);
                _definedFunctions[path] = ScriptText.DefinedFunctionSignatures(data);
            }
            return _vm.TryRun(path, data, out error);
        }

        /// <summary>保证一份脚本至少运行过一次(外部脚本在使用时才按路径加载,例如 Appendage 脚本)。</summary>
        public bool EnsureScript(string relativeToSqr, out string error)
        {
            string path = PvfPath.Normalize("sqr/" + relativeToSqr);
            if (_loadedSet.Contains(path))
            {
                error = null;
                return true;
            }
            return RunScript(relativeToSqr, out error);
        }

        private void DefineRegistrationTable(string name, bool generic)
        {
            SqNativeTable table = _vm.DefineTable(name, true);
            table.Method("pushScriptFiles", c =>
            {
                Register(RegistrationKind.ScriptFile, name, c.String(0), null, generic ? -1 : PriestJob, 0, 0);
                return c.ReturnNothing();
            });
            if (generic)
            {
                // IRDSQRCharacter.pushState(职业, 路径, 后缀, 状态, 技能)
                table.Method("pushState", c =>
                {
                    Register(RegistrationKind.State, name, c.String(1), c.String(2), c.Int(0), c.Int(3), c.Int(4));
                    return c.ReturnNothing();
                });
                table.Method("pushPassiveObj", c =>
                {
                    Register(RegistrationKind.PassiveObject, name, c.String(0), null, -1, c.Int(1), 0);
                    return c.ReturnNothing();
                });
            }
            else
            {
                // CNAvenger.pushState(路径, 后缀, 状态, 技能):职业是固定的
                table.Method("pushState", c =>
                {
                    Register(RegistrationKind.State, name, c.String(0), c.String(1), PriestJob, c.Int(2), c.Int(3));
                    return c.ReturnNothing();
                });
                table.Method("pushPassiveObj", c =>
                {
                    Register(RegistrationKind.PassiveObject, name, c.String(0), null, PriestJob, c.Int(1), 0);
                    return c.ReturnNothing();
                });
            }
        }

        private void Register(RegistrationKind kind, string table, string path, string suffix, long job, long id, long skill)
        {
            string resolved = PvfPath.Normalize("sqr/" + path);
            _registrations.Add(new ScriptRegistration(_registrations.Count, kind, table, path, resolved, suffix, job, id, skill));
            if (_loadedSet.Contains(resolved))
            {
                return;
            }
            if (!RunScript(path, out string error))
            {
                _errors.Add(resolved + ": " + FirstLine(error));
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string t = text.TrimStart('\n', '\r');
            int i = t.IndexOf('\n');
            return i < 0 ? t : t.Substring(0, i);
        }

        public void Dispose()
        {
            _vm.Dispose();
        }
    }
}
