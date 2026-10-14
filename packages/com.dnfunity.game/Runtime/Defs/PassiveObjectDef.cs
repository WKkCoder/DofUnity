using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 一个被动对象 .obj 的定义(技能放出的飞行物、特效体):基础动画、攻击信息与自定义动画/攻击信息表。
    /// 被动对象编号经 passiveobject/passiveobject.lst 找到 .obj;脚本回调名里的名字取自 .obj 的文件名(不区分大小写,见证据 E-036)。
    /// 观察:全库 6,867 个 .obj 里最常见的标签是 [layer]、[pass type]、[name]、[floating height]、[piercing power]、[basic action]、[attack info]、[basic motion];
    /// 这里只读取脚本世界用到的几项,其余标签不解释。
    /// </summary>
    public sealed class PassiveObjectDef : ObjectDef
    {
        public PassiveObjectDef(int id, string path, ScriptLine[] lines)
            : base(path, lines)
        {
            Id = id;
            int slash = path.LastIndexOf('/');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            BaseName = dot > 0 ? file.Substring(0, dot) : file;
        }

        public int Id { get; }

        /// <summary>文件名去掉扩展名(规范化后为小写),用来对应脚本里的 po_&lt;名字&gt; 回调。</summary>
        public string BaseName { get; }

        /// <summary>[name] 的取值(没有则为 null)。</summary>
        public string DisplayName => Tags.FirstString("[name]");

        /// <summary>[basic motion] 指向的动画路径(规范化);没有则 null。</summary>
        public string BasicMotionPath => MotionPath("[basic motion]");

        /// <summary>
        /// [object destroy condition] 里的 "[on end of animation]":动画播完(非循环)后对象自动消失。
        /// 观察:全库 .obj 里该行最常见的取值是 "[on end of animation]"(699)与 "[time limite]"(458,后跟毫秒数);另有 "[z pos equal zero]"(17)等,未实现。
        /// </summary>
        public bool DestroyOnAnimationEnd => DestroyConditionText().Contains("[on end of animation]");

        /// <summary>[object destroy condition] 里 "[time limite]" 后面的毫秒数;没有则 -1。</summary>
        public int TimeLimitMs
        {
            get
            {
                if (!Tags.TryGetLine("[object destroy condition]", out ScriptLine line))
                {
                    return -1;
                }
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (v[i].Kind == ScriptValueKind.String && v[i].Text == "[time limite]" && v[i + 1].IsNumber)
                    {
                        return v[i + 1].Int;
                    }
                }
                return -1;
            }
        }

        private string DestroyConditionText()
        {
            return string.Join("|", Tags.Strings("[object destroy condition]"));
        }

        /// <summary>[attack info] 指向的 .atk 路径(规范化);没有则 null。</summary>
        public string AttackInfoPath
        {
            get
            {
                // 写成空字符串表示"没有攻击信息"(例如 ATConcentrateBall.obj)
                string s = Tags.FirstString("[attack info]");
                return string.IsNullOrEmpty(s) ? null : PvfPath.ResolveRelativeTo(Path, s);
            }
        }
    }
}
