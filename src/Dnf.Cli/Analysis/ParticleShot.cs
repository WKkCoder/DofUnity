using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf particle-shot --ptl &lt;路径&gt; [--frames N --step-ms M --out 目录]`:
/// 读取指定 .ptl 粒子定义与对应 .ani / .img 资源，使用 <see cref="ParticleSimulation"/> 跑确定性多帧仿真，
/// 输出单张网格连拍 PNG（供离线目视核验；图像只写到指定目录，不入库）。
/// </summary>
internal static class ParticleShot
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (string.IsNullOrWhiteSpace(opts.PtlPath))
        {
            throw new UserError("particle-shot 需要 --ptl <PVF 里的 .ptl 路径>");
        }
        int frames = opts.Frames;
        if (frames < 1 || frames > 256)
        {
            throw new UserError("--frames 需要 1 到 256 的整数");
        }
        int stepMs = opts.StepMs;
        if (stepMs < 0 || stepMs > 10000)
        {
            throw new UserError("--step-ms 需要 0 到 10000 的整数毫秒");
        }

        string normalizedPtl = PvfPath.Normalize(opts.PtlPath);
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        ScriptLine[]? lines = assets.ReadScript(normalizedPtl);
        if (lines == null)
        {
            throw new UserError("Script.pvf 里找不到或无法解析 .ptl: " + normalizedPtl);
        }

        ParticleEffectDef def = ParticleEffectDef.Parse(normalizedPtl, lines);
        ParticleSimDef simDef = ToSimDef(def);

        bool facingLeft = string.Equals(opts.Mirror, "on", StringComparison.Ordinal);
        uint seed = unchecked((uint)opts.Seed);
        var resolver = new PtlAssetResolver(assets, normalizedPtl);

        var emitterOffset = new ParticleSimVec3(-def.CreationPos.X, -def.CreationPos.Y, 0);
        ParticleSimulationResult sim = ParticleSimulation.Simulate(
            simDef,
            seed,
            frames,
            stepMs,
            emitterPos: emitterOffset,
            facingLeft: facingLeft,
            quality: 2,
            resolveAni: resolver.ResolveAni);

        int columns = frames < 4 ? frames : 4;
        RgbaCanvas sheet = ParticleSimulation.RenderContactSheet(sim, resolver, cellWidth: 256, cellHeight: 256, columns: columns);

        string outPath = ResolveOutputPath(loc.OutDir, opts.PngFile, normalizedPtl);
        string? outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }
        PngWriter.Write(outPath, sheet.Width, sheet.Height, sheet.Pixels);

        int peakSprites = 0;
        long totalSpriteInstances = 0;
        int resolvedSpriteFrames = 0;
        foreach (ParticleStepFrame step in sim.Steps)
        {
            if (step.Sprites.Count > peakSprites)
            {
                peakSprites = step.Sprites.Count;
            }
            totalSpriteInstances += step.Sprites.Count;
            foreach (ParticleSprite sprite in step.Sprites)
            {
                if (!string.IsNullOrEmpty(sprite.ImagePath)
                    && sprite.FrameIndex >= 0
                    && resolver.TryGetFrame(sprite.ImagePath, sprite.FrameIndex, out _))
                {
                    resolvedSpriteFrames++;
                }
            }
        }

        Console.WriteLine(
            $"frames={sim.Steps.Count} stepMs={stepMs} seed={seed} mirror={(facingLeft ? "on" : "off")} " +
            $"kind={def.ObjectType} objects={def.Objects.Count} peakSprites={peakSprites} " +
            $"totalInstances={totalSpriteInstances} resolvedFrames={resolvedSpriteFrames} size={sheet.Width}x{sheet.Height}");
        Console.WriteLine("已写 " + outPath);
        return 0;
    }

    internal static ParticleSimDef ToSimDef(ParticleEffectDef def)
    {
        if (def == null)
        {
            throw new ArgumentNullException(nameof(def));
        }

        var objects = new ParticleSimObject[def.Objects.Count];
        for (int i = 0; i < def.Objects.Count; i++)
        {
            ParticleObjectEntry entry = def.Objects[i];
            objects[i] = new ParticleSimObject(entry.Path, entry.Arg);
        }

        return new ParticleSimDef(
            path: def.Path,
            creationPos: ToSimVec3(def.CreationPos),
            x: ToSimAxis(def.X),
            y: ToSimAxis(def.Y),
            z: ToSimAxis(def.Z),
            changingTermMs: def.ChangingTermMs,
            changingType: (ParticleSimChanging)(int)def.ChangingType,
            gravity: def.Gravity,
            rotationAngle: ToSimVec3(def.RotationAngle),
            lifeTimeMs: def.LifeTimeMs,
            landing: (ParticleSimLanding)(int)def.Landing,
            transparentStart: def.TransparentStart,
            transparentEnd: def.TransparentEnd,
            creationFrequency: def.CreationFrequency,
            maximumCreateNumber: def.MaximumCreateNumber,
            maximumCreateTimeMs: def.MaximumCreateTimeMs,
            createAreaSize: ToSimVec3(def.CreateAreaSize),
            createAreaRotationAngle: ToSimVec3(def.CreateAreaRotationAngle),
            rotationAngleRangeType: (ParticleSimRotationRange)(int)def.RotationAngleRangeType,
            rotationAngleVar0: def.RotationAngleVar0,
            rotationAngleVar1: def.RotationAngleVar1,
            randomRange: def.RandomRange,
            createPosCorrect: def.CreatePosCorrect,
            displayEffectRateLow: def.DisplayEffectRateLow,
            displayEffectRateMid: def.DisplayEffectRateMid,
            displayEffectRateHigh: def.DisplayEffectRateHigh,
            howToCreateObject: (ParticleSimHowToCreate)(int)def.HowToCreateObject,
            objectType: (ParticleSimObjectKind)(int)def.ObjectType,
            layer: (ParticleSimLayer)(int)def.Layer,
            objects: objects);
    }

    private static ParticleSimVec3 ToSimVec3(ParticleVec3i v) => new(v.X, v.Y, v.Z);

    private static ParticleSimAxis ToSimAxis(ParticleEffectAxis a) =>
        new((ParticleSimMoveType)(int)a.Type, a.Var1A, a.Var1B, a.Var2A, a.Var2B);

    private static string ResolveOutputPath(string outDir, string? pngFile, string ptlPath)
    {
        if (!string.IsNullOrWhiteSpace(pngFile))
        {
            return pngFile;
        }
        string stem = Path.GetFileNameWithoutExtension(ptlPath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "particle";
        }
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
        }
        return Path.Combine(outDir, stem + "_shot.png");
    }

    private sealed class PtlAssetResolver : ISpriteSource
    {
        private readonly DnfAssets _assets;
        private readonly string _ptlPath;
        private readonly string _ptlDir;

        public PtlAssetResolver(DnfAssets assets, string ptlPath)
        {
            _assets = assets;
            _ptlPath = ptlPath;
            int slash = ptlPath.LastIndexOf('/');
            _ptlDir = slash > 0 ? ptlPath.Substring(0, slash) : string.Empty;
        }

        public AniFile? ResolveAni(string relOrAbsPath)
        {
            if (string.IsNullOrWhiteSpace(relOrAbsPath))
            {
                return null;
            }
            string relResolved = PvfPath.ResolveRelativeTo(_ptlPath, relOrAbsPath);
            AniFile? ani = _assets.LoadAni(relResolved);
            if (ani != null)
            {
                return ani;
            }
            string norm = PvfPath.Normalize(relOrAbsPath);
            if (!string.Equals(norm, relResolved, StringComparison.Ordinal))
            {
                ani = _assets.LoadAni(norm);
                if (ani != null)
                {
                    return ani;
                }
            }
            if (!norm.StartsWith("sprite/", StringComparison.Ordinal))
            {
                ani = _assets.LoadAni("sprite/" + norm);
                if (ani != null)
                {
                    return ani;
                }
            }
            return null;
        }

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            if (_assets.TryGetFrame(imageName, frame, out sprite))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(_ptlDir) && imageName.StartsWith("sprite/", StringComparison.Ordinal))
            {
                string withoutPrefix = imageName.Substring("sprite/".Length);
                string inPtlDir = PvfPath.ResolveRelativeTo(_ptlPath, withoutPrefix);
                string candidate = inPtlDir.StartsWith("sprite/", StringComparison.Ordinal) ? inPtlDir : "sprite/" + inPtlDir;
                if (!string.Equals(candidate, imageName, StringComparison.Ordinal)
                    && _assets.TryGetFrame(candidate, frame, out sprite))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
