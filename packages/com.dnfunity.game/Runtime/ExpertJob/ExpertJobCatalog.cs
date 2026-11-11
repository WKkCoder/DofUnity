using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码:装载入口 initExpertJobScript 0x0896d830 → CExpertJobList::init 0x0896d8ec(character 目录下的 expertjob.lst:编号 + 脚本路径,路径相对 character 目录;
// 编号就是副职业种类),ImportExpertJobEtcScript 0x0896ae21(同目录的 expertjob.etc),LoadDisJoint 0x0847300c(etc 目录下的 disjoint.etc)。
namespace Dnf.Game
{
    /// <summary>副职业目录:各种类的脚本、等级上限表、旧版分解表与收购价倍率。读不到的部分是空/缺省,不抛异常。</summary>
    public sealed class ExpertJobCatalog
    {
        private readonly Dictionary<ExpertJobKind, ExpertJobScript> _scripts = new Dictionary<ExpertJobKind, ExpertJobScript>();

        public ExpertJobCatalog(ExpertJobEtc etc, IEnumerable<ExpertJobScript> scripts, LegacyDisjointScript legacy, ItemPriceRates priceRates)
        {
            Etc = etc ?? ExpertJobEtc.Empty;
            Legacy = legacy ?? new LegacyDisjointScript();
            PriceRates = priceRates ?? ItemPriceRates.Neutral;
            if (scripts != null)
            {
                foreach (ExpertJobScript s in scripts)
                {
                    if (s != null && ExpertJobKinds.IsJob(s.Kind))
                    {
                        _scripts[s.Kind] = s;
                    }
                }
            }
        }

        public ExpertJobEtc Etc { get; }

        public LegacyDisjointScript Legacy { get; }

        public ItemPriceRates PriceRates { get; }

        /// <summary>目录里有脚本的种类。</summary>
        public IReadOnlyCollection<ExpertJobKind> Kinds => _scripts.Keys;

        /// <summary>某个种类的脚本;没有返回 null。</summary>
        public ExpertJobScript Get(ExpertJobKind kind)
        {
            return _scripts.TryGetValue(kind, out ExpertJobScript s) ? s : null;
        }

        /// <summary>装载过程中读到的全部错误(等级表与各脚本),每条前面带来源。</summary>
        public List<string> Errors()
        {
            var list = new List<string>();
            foreach (string e in Etc.Errors)
            {
                list.Add("expertjob.etc: " + e);
            }
            foreach (KeyValuePair<ExpertJobKind, ExpertJobScript> pair in _scripts)
            {
                foreach (string e in pair.Value.Errors)
                {
                    list.Add(pair.Key + ": " + e);
                }
            }
            return list;
        }

        /// <summary>从游戏数据装载。列表里的编号 1 到 4 之外的项读进来也不留(服务端以编号为键存,但种类只认 1 到 4;构造函数只收真正的种类);路径相对 <see cref="ExpertJobKinds.Folder"/> 目录解析。</summary>
        public static ExpertJobCatalog Read(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            var scripts = new List<ExpertJobScript>();
            Dictionary<int, string> list = data.ReadList(ExpertJobKinds.ListPath);
            if (list != null)
            {
                foreach (KeyValuePair<int, string> pair in list)
                {
                    string path = PvfPath.ResolveRelativeTo(ExpertJobKinds.Folder + "/", pair.Value);
                    ScriptLine[] lines = data.ReadScript(path);
                    if (lines != null)
                    {
                        scripts.Add(ExpertJobScript.Parse((ExpertJobKind)pair.Key, lines));
                    }
                }
            }
            ExpertJobEtc etc = ExpertJobEtc.Parse(data.ReadScript(ExpertJobKinds.EtcPath));
            LegacyDisjointScript legacy = LegacyDisjointScript.Parse(data.ReadScript(LegacyDisjointScript.Path));
            ItemPriceRates rates = ItemPriceRates.Read(data);
            return new ExpertJobCatalog(etc, scripts, legacy, rates);
        }
    }
}
