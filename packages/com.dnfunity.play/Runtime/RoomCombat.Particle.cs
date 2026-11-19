using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;

// r19-particle 阶段 3:.act [PARTICLE] 行为在房间战斗会话侧的最小加法接线。
// - 独立随机种子 _actParticleSeed，不碰 _actRandom / _monsterRandom / _actSoundSeed，既有战斗推演逐位不变。
// - 与 RoomCombat.MotionEvents.cs 同构：保留原始请求旁路（ActParticleObserver）与计数，同时驱动 Render 层 ParticleEffectPlayer 接入 Build 合成。
namespace Dnf.Play
{
    /// <summary>将 Game 层解析出的 <see cref="ParticleEffectDef"/> 映射为 Render 层纯数据规格 <see cref="ParticleSimDef"/>。</summary>
    public static class ParticleEffectMapper
    {
        public static ParticleSimDef ToSimDef(ParticleEffectDef def)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            var objects = new ParticleSimObject[def.Objects.Count];
            for (int i = 0; i < def.Objects.Count; i++)
            {
                ParticleObjectEntry e = def.Objects[i];
                objects[i] = new ParticleSimObject(e.Path, e.Arg);
            }
            return new ParticleSimDef(
                path: def.Path,
                creationPos: new ParticleSimVec3(def.CreationPos.X, def.CreationPos.Y, def.CreationPos.Z),
                x: ToSimAxis(def.X),
                y: ToSimAxis(def.Y),
                z: ToSimAxis(def.Z),
                changingTermMs: def.ChangingTermMs,
                changingType: (ParticleSimChanging)(int)def.ChangingType,
                gravity: def.Gravity,
                rotationAngle: new ParticleSimVec3(def.RotationAngle.X, def.RotationAngle.Y, def.RotationAngle.Z),
                lifeTimeMs: def.LifeTimeMs,
                landing: (ParticleSimLanding)(int)def.Landing,
                transparentStart: def.TransparentStart,
                transparentEnd: def.TransparentEnd,
                creationFrequency: def.CreationFrequency,
                maximumCreateNumber: def.MaximumCreateNumber,
                maximumCreateTimeMs: def.MaximumCreateTimeMs,
                createAreaSize: new ParticleSimVec3(def.CreateAreaSize.X, def.CreateAreaSize.Y, def.CreateAreaSize.Z),
                createAreaRotationAngle: new ParticleSimVec3(def.CreateAreaRotationAngle.X, def.CreateAreaRotationAngle.Y, def.CreateAreaRotationAngle.Z),
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

        private static ParticleSimAxis ToSimAxis(ParticleEffectAxis a) =>
            new ParticleSimAxis((ParticleSimMoveType)(int)a.Type, a.Var1A, a.Var1B, a.Var2A, a.Var2B);
    }

    public sealed partial class RoomCombatSession
    {
        /// <summary>单房间同时保活的 .act 粒子播放器硬上限（避免 lifeTime=0 永生粒子无限堆积）。</summary>
        public const int MaxActiveActParticles = 64;

        private uint _actParticleSeed = 2166136261u;
        private readonly List<ParticleEffectPlayer> _actParticles = new List<ParticleEffectPlayer>();
        private readonly Dictionary<string, ParticleSimDef> _ptlDefCache = new Dictionary<string, ParticleSimDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, AniFile> _ptlAniCache = new Dictionary<string, AniFile>(StringComparer.Ordinal);

        /// <summary>收到的 .act [PARTICLE] 请求总数。</summary>
        public int ActParticleRequests { get; private set; }

        /// <summary>成功实例化并产生至少 1 颗粒子的播放器总数。</summary>
        public int ActParticlesSpawned { get; private set; }

        /// <summary>找不到或无法解析 .ptl 脚本的请求次数。</summary>
        public int ActParticlesMissing { get; private set; }

        /// <summary>当前仍在房间中存活的粒子播放器个数。</summary>
        public int ActiveActParticles => _actParticles.Count;

        /// <summary>当前仍在房间中存活的粒子播放器（只读视图，供测试与外部观察）。</summary>
        public IReadOnlyList<ParticleEffectPlayer> ActParticles => _actParticles;

        /// <summary>
        /// .act [PARTICLE] 请求旁路（测试与外部观察用）：每条请求在解析与实例化之前原样回调一份；不挂不影响任何逻辑。
        /// </summary>
        public Action<ActParticleRequest> ActParticleObserver { get; set; }

        private void ReceiveActParticle(ActParticleRequest request)
        {
            if (request == null)
            {
                return;
            }
            ActParticleRequests++;
            ActParticleObserver?.Invoke(request);

            ParticleSimDef simDef = LoadActParticleDef(request);
            if (simDef == null)
            {
                ActParticlesMissing++;
                return;
            }

            uint seed = unchecked((uint)ClientRandom.Next(ref _actParticleSeed));
            var worldPos = new ParticleSimVec3(
                (int)Math.Floor(request.WorldX),
                (int)Math.Floor(request.WorldY),
                (int)Math.Round(request.WorldZ));

            string ptlPath = simDef.Path;
            var player = new ParticleEffectPlayer(
                simDef,
                seed,
                worldPos,
                request.FacingLeft,
                oneShot: true,
                quality: 2,
                resolveAni: rel => ResolveActParticleAni(ptlPath, rel));

            if (player.Finished)
            {
                return;
            }
            if (_actParticles.Count >= MaxActiveActParticles)
            {
                _actParticles.RemoveAt(0);
            }
            _actParticles.Add(player);
            ActParticlesSpawned++;
        }

        private ParticleSimDef LoadActParticleDef(ActParticleRequest request)
        {
            string resolved = request.ResolvedPath;
            if (_ptlDefCache.TryGetValue(resolved, out ParticleSimDef cached))
            {
                return cached;
            }

            string loadedPath = resolved;
            ScriptLine[] lines = _assets.ReadScript(resolved);
            if (lines == null)
            {
                string direct = PvfPath.Normalize(request.Path);
                if (!string.Equals(direct, resolved, StringComparison.Ordinal))
                {
                    lines = _assets.ReadScript(direct);
                    if (lines != null)
                    {
                        loadedPath = direct;
                    }
                }
            }

            ParticleSimDef simDef = lines == null
                ? null
                : ParticleEffectMapper.ToSimDef(ParticleEffectDef.Parse(loadedPath, lines));
            _ptlDefCache[resolved] = simDef;
            return simDef;
        }

        private AniFile ResolveActParticleAni(string ptlPath, string relOrAbsPath)
        {
            if (string.IsNullOrEmpty(relOrAbsPath))
            {
                return null;
            }
            string cacheKey = ptlPath + "|" + relOrAbsPath;
            if (_ptlAniCache.TryGetValue(cacheKey, out AniFile cached))
            {
                return cached;
            }

            string relResolved = PvfPath.ResolveRelativeTo(ptlPath, relOrAbsPath);
            AniFile ani = _assets.LoadAni(relResolved);
            if (ani == null)
            {
                string norm = PvfPath.Normalize(relOrAbsPath);
                if (!string.Equals(norm, relResolved, StringComparison.Ordinal))
                {
                    ani = _assets.LoadAni(norm);
                }
                if (ani == null && !norm.StartsWith("sprite/", StringComparison.Ordinal))
                {
                    ani = _assets.LoadAni("sprite/" + norm);
                }
            }
            _ptlAniCache[cacheKey] = ani;
            return ani;
        }

        private void UpdateActParticles(double deltaMs)
        {
            if (_actParticles.Count == 0 || deltaMs <= 0.0)
            {
                return;
            }
            for (int i = _actParticles.Count - 1; i >= 0; i--)
            {
                _actParticles[i].Advance(deltaMs);
                if (_actParticles[i].Finished)
                {
                    _actParticles.RemoveAt(i);
                }
            }
        }

        private void AddActParticleSprites(List<SceneSprite> into, TownProjection projection)
        {
            if (_actParticles.Count == 0)
            {
                return;
            }
            for (int i = 0; i < _actParticles.Count; i++)
            {
                ParticleEffectPlayer player = _actParticles[i];
                var source = new ParticleSpriteResolver(_assets, player.Path);
                player.AppendSceneSprites(source, projection, into);
            }
        }

        private sealed class ParticleSpriteResolver : ISpriteSource
        {
            private readonly DnfAssets _assets;
            private readonly string _ptlPath;
            private readonly bool _hasDir;

            public ParticleSpriteResolver(DnfAssets assets, string ptlPath)
            {
                _assets = assets;
                _ptlPath = ptlPath ?? string.Empty;
                _hasDir = _ptlPath.LastIndexOf('/') > 0;
            }

            public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
            {
                if (_assets.TryGetFrame(imageName, frame, out sprite))
                {
                    return true;
                }
                if (_hasDir && imageName.StartsWith("sprite/", StringComparison.Ordinal))
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

        private sealed partial class ActWorld
        {
            public void SpawnParticle(ActParticleRequest request)
            {
                _session.ReceiveActParticle(request);
            }
        }
    }
}
