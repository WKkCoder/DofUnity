using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf sound-check [--bot [--dungeon …] [--seconds N] [--seed N]] [--json &lt;文件&gt;]`:声音数据与触发的检查(证据 E-089、E-090)。
///   1. audio.xml 的各类条目数、文件是否在 SoundPacks / Music 里、是不是 Ogg(头四个字节 OggS)、RANDOM 与 GROUP 能不能展开到文件、没被引用的文件数;
///   2. 全库脚本里的声音引用:.map 的 [sound]、.mob 的五种声音标签、.atk 的 [hit wav]、.chr 的 [weapon wav]、.ani 帧上的声音,各自能不能在 audio.xml 里找到;
///   3. 代码里写死的鬼剑士语音等标签是否在 audio.xml 里;
///   4. 带 --bot:让自动机打一座副本,按时间顺序收集战斗会话发出的声音提示,统计各种类的次数、每个提示能不能解析到存在的文件。
/// 只输出数量与标签名(标识符),不输出任何声音内容;没有任何录音可对照,所以这里验证的是"数据对得上、触发有发出",不是"听起来对"。
/// </summary>
internal static class SoundCheck
{
    private sealed class RefStats
    {
        private readonly SoundLibrary _lib;
        public long Files;
        public long Refs;
        public readonly HashSet<string> Distinct = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>audio.xml 里没有这个标签。</summary>
        public readonly Dictionary<string, long> Missing = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>标签在 audio.xml 里有,但它能到达的文件一个也不在 SoundPacks/Music 里。</summary>
        public readonly Dictionary<string, long> NoFile = new(StringComparer.OrdinalIgnoreCase);

        public RefStats(SoundLibrary lib)
        {
            _lib = lib;
        }

        public void Add(string key)
        {
            Refs++;
            Distinct.Add(key);
            if (!_lib.Catalog.Contains(key))
            {
                Missing[key] = Missing.TryGetValue(key, out long n) ? n + 1 : 1;
            }
            else if (!CanPlay(_lib, key))
            {
                NoFile[key] = NoFile.TryGetValue(key, out long n) ? n + 1 : 1;
            }
        }

        public long MissingRefs => Missing.Values.Sum();

        public long NoFileRefs => NoFile.Values.Sum();
    }

    private static readonly Dictionary<string, bool> PlayableCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标签能到达的文件里至少有一个在包里或 Music 目录里。</summary>
    private static bool CanPlay(SoundLibrary lib, string key)
    {
        lock (PlayableCache)
        {
            if (PlayableCache.TryGetValue(key, out bool known))
            {
                return known;
            }
            bool ok = lib.Catalog.CollectFiles(key).Any(lib.Exists);
            PlayableCache[key] = ok;
            return ok;
        }
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        var total = Stopwatch.StartNew();
        using SoundLibrary lib = SoundLibrary.Open(loc.DnfDir);
        AudioCatalog catalog = lib.Catalog;
        var summary = new SortedDictionary<string, object>(StringComparer.Ordinal);

        if (!lib.HasCatalog)
        {
            Console.WriteLine("数据目录里没有 audio.xml,没有什么可检查的。");
            return 2;
        }

        CheckCatalog(lib, catalog, summary);
        CheckScripts(loc, lib, summary);
        CheckConstants(catalog, summary);
        if (opts.Bot)
        {
            CheckCombat(loc, lib, opts, summary);
        }

        Console.WriteLine();
        Console.WriteLine("用时 " + total.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒");
        if (opts.JsonFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile))!);
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("已写出 -> " + opts.JsonFile);
        }
        return 0;
    }

    // ------------------------------------------------------------------ 1. 目录与文件

    private static void CheckCatalog(SoundLibrary lib, AudioCatalog catalog, SortedDictionary<string, object> summary)
    {
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (AudioEntry e in catalog.Entries)
        {
            string k = e.Kind.ToString();
            kinds[k] = kinds.TryGetValue(k, out int n) ? n + 1 : 1;
        }
        Console.WriteLine("== audio.xml ==");
        Console.WriteLine("条目 " + catalog.Count + "(" + string.Join(" / ", kinds.Select(kv => kv.Key + " " + kv.Value)) + ");重复 ID " + catalog.DuplicateCount
            + "(先到先得),不认识的元素 " + catalog.UnknownElementCount);
        Console.WriteLine("文件来源:SoundPacks " + lib.PackedCount + " 项,Music 目录 " + lib.LooseCount + " 个");
        summary["catalog_entries"] = catalog.Count;
        summary["catalog_kinds"] = kinds;
        summary["catalog_duplicates"] = catalog.DuplicateCount;
        summary["catalog_unknown_elements"] = catalog.UnknownElementCount;
        summary["packed_files"] = lib.PackedCount;
        summary["loose_files"] = lib.LooseCount;

        // 有文件的条目(EFFECT、AMBIENT、MUSIC):文件在不在、是不是 Ogg
        int leaves = 0, present = 0, missingFile = 0, oggOk = 0, oggBad = 0, noFile = 0;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var missingSamples = new List<string>();
        foreach (AudioEntry e in catalog.Entries)
        {
            if (e.Kind != AudioKind.Effect && e.Kind != AudioKind.Ambient && e.Kind != AudioKind.Music)
            {
                continue;
            }
            leaves++;
            if (string.IsNullOrWhiteSpace(e.File))
            {
                noFile++;
                continue;
            }
            string key = SoundLibrary.Normalize(e.File);
            referenced.Add(key);
            if (!lib.Exists(key))
            {
                missingFile++;
                if (missingSamples.Count < 5)
                {
                    missingSamples.Add(e.Id);
                }
                continue;
            }
            present++;
            if (!checkedFiles.TryGetValue(key, out bool ok))
            {
                ok = IsOgg(lib, key);
                checkedFiles[key] = ok;
            }
            if (ok)
            {
                oggOk++;
            }
            else
            {
                oggBad++;
            }
        }
        Console.WriteLine("有文件的条目 " + leaves + ":没写文件 " + noFile + ",文件存在 " + present + "(Ogg 头合格 " + oggOk + ",不合格 " + oggBad + "),文件缺失 " + missingFile
            + (missingSamples.Count > 0 ? "(例:" + string.Join("、", missingSamples) + ")" : string.Empty));
        summary["leaf_entries"] = leaves;
        summary["leaf_no_file"] = noFile;
        summary["leaf_file_present"] = present;
        summary["leaf_ogg_ok"] = oggOk;
        summary["leaf_ogg_bad"] = oggBad;
        summary["leaf_file_missing"] = missingFile;

        // RANDOM 与 GROUP:展开后能不能得到至少一个存在的文件
        int composite = 0, expandedAll = 0, expandedSome = 0, expandedNone = 0;
        foreach (AudioEntry e in catalog.Entries)
        {
            if (e.Kind != AudioKind.Random && e.Kind != AudioKind.Group)
            {
                continue;
            }
            composite++;
            List<string> files = catalog.CollectFiles(e.Id);       // 每一项都算,不只是这一次抽到的
            int exist = 0;
            foreach (string file in files)
            {
                string key = SoundLibrary.Normalize(file);
                referenced.Add(key);
                if (lib.Exists(key))
                {
                    exist++;
                }
            }
            if (files.Count > 0 && exist == files.Count)
            {
                expandedAll++;
            }
            else if (exist > 0)
            {
                expandedSome++;
            }
            else
            {
                expandedNone++;
            }
        }
        Console.WriteLine("RANDOM/GROUP 条目 " + composite + ":展开后文件全在 " + expandedAll + ",只在一部分 " + expandedSome + ",一个也没有 " + expandedNone);
        summary["composite_entries"] = composite;
        summary["composite_all_present"] = expandedAll;
        summary["composite_some_present"] = expandedSome;
        summary["composite_none_present"] = expandedNone;

        // 没被任何条目引用的文件
        int orphans = 0;
        foreach (string name in lib.AllFiles())
        {
            if (!referenced.Contains(name))
            {
                orphans++;
            }
        }
        Console.WriteLine("包里与 Music 目录里没被任何条目引用的文件 " + orphans + " 个(共 " + (lib.PackedCount + lib.LooseCount) + ")");
        summary["unreferenced_files"] = orphans;
    }

    private static bool IsOgg(SoundLibrary lib, string key)
    {
        if (lib.TryGetLoosePath(key, out string? path))
        {
            using FileStream f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return f.Read(head) == 4 && head[0] == (byte)'O' && head[1] == (byte)'g' && head[2] == (byte)'g' && head[3] == (byte)'S';
        }
        return lib.TryRead(key, out byte[]? data) && data!.Length >= 4 && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S';
    }

    // ------------------------------------------------------------------ 2. 脚本里的引用

    private static void CheckScripts(DnfLocation loc, SoundLibrary lib, SortedDictionary<string, object> summary)
    {
        Console.WriteLine();
        Console.WriteLine("== 脚本里的声音引用(能不能在 audio.xml 里找到) ==");
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        PvfArchive pvf = assets.Pvf;
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));

        var maps = new RefStats(lib);
        var mobs = new Dictionary<string, RefStats>(StringComparer.Ordinal)
        {
            ["appear"] = new RefStats(lib), ["damage"] = new RefStats(lib), ["die"] = new RefStats(lib), ["attack"] = new RefStats(lib), ["ambient"] = new RefStats(lib),
        };
        var atks = new RefStats(lib);
        var weapons = new RefStats(lib);
        long chrFiles = 0, chrWithTable = 0, weaponBlocks = 0, weaponEmptyBlocks = 0, mapFiles = 0, mobFiles = 0, atkFiles = 0, atkLoaderMismatch = 0;
        var mismatchSamples = new List<string>();
        long atkTwice = 0;

        foreach (PvfEntry e in pvf.Entries)
        {
            string path = e.Path;
            if (path.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            {
                ScriptLine[]? doc = assets.ReadScript(path);
                if (doc == null)
                {
                    continue;
                }
                mapFiles++;
                List<string> sounds = RoomMap.ReadSounds(doc);
                if (sounds.Count > 0)
                {
                    maps.Files++;
                }
                foreach (string s in sounds)
                {
                    maps.Add(s);
                }
            }
            else if (path.EndsWith(".mob", StringComparison.OrdinalIgnoreCase))
            {
                ScriptLine[]? doc = assets.ReadScript(path);
                if (doc == null)
                {
                    continue;
                }
                mobFiles++;
                var def = new MonsterDef(0, path, doc);
                void Mob(string name, string? key)
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        mobs[name].Add(key);
                    }
                }
                Mob("appear", def.AppearSound);
                Mob("damage", def.DamageSound);
                Mob("die", def.DieSound);
                Mob("attack", def.AttackSound);
                Mob("ambient", def.AmbientSound);
            }
            else if (path.EndsWith(".atk", StringComparison.OrdinalIgnoreCase))
            {
                byte[] raw = pvf.Read(e);
                if (!TokenScript.IsTokenScript(raw))
                {
                    continue;
                }
                atkFiles++;
                AttackInfoData data = AttackInfoLoader.Load(new ScriptCursor(TokenScript.Parse(raw), strings), out AttackInfoLoadReport loadReport);
                // 与按行读脚本的结果交叉核对:加载器读到的 [hit wav] 应该与"脚本里最后一个写了字符串的 [hit wav]"一致
                // (同一个标签出现两次时后一次覆盖前一次,与客户端逐个标签赋值的读法一致;全库有 42 个 .atk 写了两次)
                ScriptLine[]? doc = assets.ReadScript(path);
                string? lastRaw = null;
                int hitWavLines = 0;
                if (doc != null)
                {
                    foreach (ScriptLine line in doc)
                    {
                        if (line.TagIs("[hit wav]"))
                        {
                            hitWavLines++;
                            lastRaw = line.FirstString() ?? lastRaw;
                        }
                    }
                }
                if (hitWavLines > 1)
                {
                    atkTwice++;
                }
                string fromLoader = data.HitWav ?? string.Empty;
                if (!string.Equals(fromLoader, lastRaw ?? string.Empty, StringComparison.Ordinal))
                {
                    atkLoaderMismatch++;
                    if (mismatchSamples.Count < 4)
                    {
                        mismatchSamples.Add(path + "(加载器 " + (fromLoader.Length == 0 ? "空" : fromLoader) + ",逐行最后一个 " + (lastRaw ?? "无") + ",[hit wav] 共 " + hitWavLines
                            + " 行;加载器在游离标记处停下 " + loadReport.StoppedAtStrayToken + ",跳过的模块节 " + loadReport.SkippedModuleSections + ")");
                    }
                }
                if (fromLoader.Length > 0)
                {
                    atks.Files++;
                    atks.Add(fromLoader);
                }
            }
            else if (path.EndsWith(".chr", StringComparison.OrdinalIgnoreCase))
            {
                ScriptLine[]? doc = assets.ReadScript(path);
                if (doc == null)
                {
                    continue;
                }
                chrFiles++;
                var def = new CharacterDef(0, path, doc);
                if (def.WeaponSounds.Length > 0)
                {
                    chrWithTable++;
                    weapons.Files++;
                }
                foreach (WeaponSound w in def.WeaponSounds)
                {
                    weaponBlocks++;
                    if (w.IsEmpty)
                    {
                        weaponEmptyBlocks++;
                        continue;
                    }
                    foreach (string? key in new[] { w.SwingA, w.SwingB, w.HitA, w.HitB })
                    {
                        if (!string.IsNullOrEmpty(key))
                        {
                            weapons.Add(key);
                        }
                    }
                }
            }
        }

        Print(".map [sound]", maps, mapFiles + " 张地图,其中 " + maps.Files + " 张有 [sound]");
        foreach (KeyValuePair<string, RefStats> kv in mobs)
        {
            Print(".mob [" + kv.Key + " sound]", kv.Value, mobFiles + " 个 .mob");
        }
        Print(".atk [hit wav]", atks, atkFiles + " 个 .atk,有 [hit wav] " + atks.Files + " 个(其中写了两次以上的 " + atkTwice + " 个,后一次覆盖前一次);加载器与逐行读取不一致 " + atkLoaderMismatch + " 个");
        foreach (string sample in mismatchSamples)
        {
            Console.WriteLine("    不一致的例子:" + sample);
        }
        Print(".chr [weapon wav]", weapons, chrFiles + " 个 .chr,有这张表的 " + chrWithTable + " 个,共 " + weaponBlocks + " 块,其中空块 " + weaponEmptyBlocks);

        summary["map_sound_refs"] = maps.Refs;
        summary["map_sound_missing_refs"] = maps.MissingRefs;
        summary["map_sound_distinct"] = maps.Distinct.Count;
        summary["map_sound_missing_distinct"] = maps.Missing.Count;
        foreach (KeyValuePair<string, RefStats> kv in mobs)
        {
            summary["mob_" + kv.Key + "_refs"] = kv.Value.Refs;
            summary["mob_" + kv.Key + "_missing_refs"] = kv.Value.MissingRefs;
        }
        summary["atk_hit_wav_refs"] = atks.Refs;
        summary["atk_hit_wav_missing_refs"] = atks.MissingRefs;
        summary["atk_hit_wav_loader_mismatch"] = atkLoaderMismatch;
        summary["atk_hit_wav_written_twice"] = atkTwice;
        summary["map_sound_nofile_refs"] = maps.NoFileRefs;
        summary["atk_hit_wav_nofile_refs"] = atks.NoFileRefs;
        summary["chr_weapon_nofile_refs"] = weapons.NoFileRefs;
        foreach (KeyValuePair<string, RefStats> kv in mobs)
        {
            summary["mob_" + kv.Key + "_nofile_refs"] = kv.Value.NoFileRefs;
        }
        summary["chr_weapon_blocks"] = weaponBlocks;
        summary["chr_weapon_empty_blocks"] = weaponEmptyBlocks;
        summary["chr_weapon_refs"] = weapons.Refs;
        summary["chr_weapon_missing_refs"] = weapons.MissingRefs;

        // .ani 帧上的声音:全库并行扫描
        var aniStats = new RefStats(lib);
        long aniFiles = 0, aniWithSound = 0, aniFailed = 0;
        var gate = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) };
        Parallel.For(0, pvf.Count, options, () => (Files: 0L, With: 0L, Failed: 0L, Keys: new List<string>()), (i, _, local) =>
        {
            PvfEntry e = pvf.Entries[i];
            if (!e.Path.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }
            byte[] data = pvf.Read(e);
            if (data.Length < 8 || (data[0] == 0xB0 && data[1] == 0xD0) || data[0] == (byte)'[')
            {
                return local;
            }
            AniFile ani;
            try
            {
                ani = AniFile.Parse(data);
            }
            catch (DnfFormatException)
            {
                return (local.Files, local.With, local.Failed + 1, local.Keys);
            }
            bool any = false;
            foreach (AniFrame f in ani.Frames)
            {
                if (f.TrySoundKey(out string key))
                {
                    local.Keys.Add(key);
                    any = true;
                }
            }
            return (local.Files + 1, local.With + (any ? 1 : 0), local.Failed, local.Keys);
        }, local =>
        {
            lock (gate)
            {
                aniFiles += local.Files;
                aniWithSound += local.With;
                aniFailed += local.Failed;
                foreach (string key in local.Keys)
                {
                    aniStats.Add(key);
                }
            }
        });
        aniStats.Files = aniWithSound;
        Print(".ani 帧上的声音", aniStats, aniFiles + " 个二进制 .ani(解析失败 " + aniFailed + "),有帧声音的 " + aniWithSound + " 个");
        summary["ani_files"] = aniFiles;
        summary["ani_with_sound"] = aniWithSound;
        summary["ani_sound_refs"] = aniStats.Refs;
        summary["ani_sound_missing_refs"] = aniStats.MissingRefs;
        summary["ani_sound_nofile_refs"] = aniStats.NoFileRefs;
    }

    private static void Print(string name, RefStats r, string context)
    {
        Console.WriteLine(name + ":" + context);
        Console.WriteLine("    引用 " + r.Refs + " 次,不同标签 " + r.Distinct.Count + " 个;audio.xml 里找不到的 " + r.MissingRefs + " 次(不同标签 " + r.Missing.Count + " 个)"
            + (r.Missing.Count > 0 ? ",例:" + string.Join("、", r.Missing.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(4).Select(kv => kv.Key)) : string.Empty));
        Console.WriteLine("    找得到但文件一个也不在的 " + r.NoFileRefs + " 次(不同标签 " + r.NoFile.Count + " 个)"
            + (r.NoFile.Count > 0 ? ",例:" + string.Join("、", r.NoFile.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(4).Select(kv => kv.Key)) : string.Empty));
    }

    // ------------------------------------------------------------------ 3. 代码里写死的标签

    private static void CheckConstants(AudioCatalog catalog, SortedDictionary<string, object> summary)
    {
        Console.WriteLine();
        Console.WriteLine("== 代码里写死的标签(鬼剑士的语音与技能音效、冷却完成提示) ==");
        var keys = new (string Name, string Key)[]
        {
            ("Attack", SwordmanVoice.Attack), ("JumpAttack", SwordmanVoice.JumpAttack), ("Hurt", SwordmanVoice.Hurt), ("Die", SwordmanVoice.Die),
            ("Jump", SwordmanVoice.Jump), ("Backstep", SwordmanVoice.Backstep), ("Thrust", SwordmanVoice.Thrust), ("NoMana", SwordmanVoice.NoMana),
            ("CoolTime", SwordmanVoice.CoolTime), ("CommandPet", SwordmanVoice.CommandPet), ("Throw", SwordmanVoice.Throw),
            ("UpperSlash", SwordmanVoice.UpperSlash), ("JumpStart", SwordmanVoice.JumpStart), ("JumpEnd", SwordmanVoice.JumpEnd), ("AirSword", SwordmanVoice.AirSword),
            ("CooldownEnd", RoomCombatSession.CooldownEndSound),
        };
        int found = 0;
        foreach ((string name, string key) in keys)
        {
            bool ok = catalog.Contains(key);
            found += ok ? 1 : 0;
            string kind = ok && catalog.TryGet(key, out AudioEntry? e) ? e!.Kind.ToString() : "-";
            Console.WriteLine("    " + (ok ? "有 " : "缺 ") + key.PadRight(18) + kind.PadRight(8) + name);
        }
        Console.WriteLine("共 " + keys.Length + " 个,audio.xml 里有 " + found + " 个");
        summary["constants_total"] = keys.Length;
        summary["constants_found"] = found;
    }

    // ------------------------------------------------------------------ 4. 自动机打一座副本,收集声音提示

    private static void CheckCombat(DnfLocation loc, SoundLibrary lib, Options opts, SortedDictionary<string, object> summary)
    {
        Console.WriteLine();
        Console.WriteLine("== 自动机打 " + opts.Dungeon + ",收集声音提示 ==");
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        DungeonSession session = DungeonSession.Create(assets, opts.Dungeon, opts.Seed, opts.RandomLayout, opts.HeroWeapon, heroVitals: !opts.NoDamage);
        var bot = new DungeonBot(opts.Explore);
        double seconds = opts.SecondsGiven ? opts.Seconds : 900;
        double frameMs = CombatScript.StepMs;
        double t = 0;
        var cues = new List<(double T, SoundCue Cue)>();
        var scratch = new List<SoundCue>();
        long drained = 0;
        while (t < seconds * 1000.0 && !session.Finished && !session.HeroDead)
        {
            bot.Advance(session, frameMs);
            t += frameMs;
            scratch.Clear();
            session.DrainSounds(scratch);
            foreach (SoundCue c in scratch)
            {
                cues.Add((t / 1000.0, c));
            }
            drained += scratch.Count;
        }
        Console.WriteLine("模拟 " + (t / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " 秒:" + (session.BossCleared ? "首领清空" : session.HeroDead ? "主角死亡" : "未走完")
            + ",命中 " + session.Hits + ",击杀 " + session.Kills + ",进过 " + session.RoomsEntered + " 个房间;收到声音提示 " + drained + " 条(队列累计 " + session.Sounds.TotalCount + ")");

        var byKind = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach ((double _, SoundCue c) in cues)
        {
            byKind[c.Kind.ToString()] = byKind.TryGetValue(c.Kind.ToString(), out long n) ? n + 1 : 1;
            string id = c.Kind + ":" + c.Key;
            byKey[id] = byKey.TryGetValue(id, out long m) ? m + 1 : 1;
        }
        Console.WriteLine("按种类:" + string.Join(" / ", byKind.Select(kv => kv.Key + " " + kv.Value)));

        // 每个不同的提示:能不能解析到存在的文件
        int distinct = 0, playable = 0;
        long cuesPlayable = 0;
        var unresolved = new List<string>();
        foreach (KeyValuePair<string, long> kv in byKey)
        {
            distinct++;
            string key = kv.Key.Substring(kv.Key.IndexOf(':') + 1);
            bool ok = kv.Key.StartsWith("RoomEnter:", StringComparison.Ordinal) || CanPlay(lib, key);   // 房间标记不是声音,不用解析
            if (ok)
            {
                playable++;
                cuesPlayable += kv.Value;
            }
            else
            {
                unresolved.Add(kv.Key + "×" + kv.Value);
            }
        }
        Console.WriteLine("不同的提示 " + distinct + " 种,能解析到存在的文件的 " + playable + " 种(覆盖 " + cuesPlayable + "/" + drained + " 条);解析不到的:"
            + (unresolved.Count == 0 ? "无" : string.Join("、", unresolved.Take(12)) + (unresolved.Count > 12 ? " ……共 " + unresolved.Count + " 种" : string.Empty)));

        Console.WriteLine("出现最多的提示:" + string.Join("、", byKey.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(16).Select(kv => kv.Key + "×" + kv.Value)));
        Console.WriteLine("最先的 24 条:");
        foreach ((double ct, SoundCue c) in cues.Take(24))
        {
            Console.WriteLine("    " + ct.ToString("F2", CultureInfo.InvariantCulture).PadLeft(7) + "s  " + c);
        }
        summary["combat_seconds"] = Math.Round(t / 1000.0, 1);
        summary["combat_cues"] = drained;
        summary["combat_cue_kinds"] = byKind;
        summary["combat_distinct_cues"] = distinct;
        summary["combat_playable_cues"] = playable;
    }
}
