using Dnf.Script;

namespace Dnf.Game
{
    /// <summary>一个状态在脚本里的登记(来自 pushState):回调函数名 = 回调前缀 + "_" + <see cref="Suffix"/>。</summary>
    public sealed class StateInfo
    {
        public string Module { get; }
        public int Job { get; }
        public int State { get; }
        public int Skill { get; }
        public string Suffix { get; }
        public string ScriptPath { get; }

        public StateInfo(ScriptRegistration r)
        {
            Module = r.Table;
            Job = (int)r.Job;
            State = (int)r.Id;
            Skill = (int)r.Skill;
            Suffix = r.Suffix;
            ScriptPath = r.ResolvedPath;
        }

        public bool IsSkill => Skill >= 0;

        public override string ToString()
        {
            return Suffix + "(job " + Job + ", state " + State + ", skill " + Skill + ")";
        }
    }
}
