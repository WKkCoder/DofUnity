using System;
using System.Collections.Generic;
using System.Threading;
using Dnf.Play;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// TownDemo 的在线城镇一侧(r18-onlinetown):
    /// 把 <see cref="OnlineTownController"/> 接到 Unity 生命周期与 <see cref="TownDemo"/> 流程上,
    /// 不改离线存档/单机流程(<c>_shell == null</c> 或未进在线城镇时所有钩子都是空操作)。
    /// </summary>
    public sealed partial class TownDemo
    {
        private bool _onlineAreaSync = true;                  // -dnfonlineareas on|off(默认开):停掉服务端 1/38 切区同步时的应急退回开关
        private OnlineCharacterSelectOptions _onlineSelectOptions;
        private TownAtlas _onlineAtlas;
        private OnlineTownController _onlineTown;

        private static long OnlineNowMs()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        }

        private static bool TryParseOnlineSwitch(string value, out bool enabled)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "1":
                case "on":
                case "true":
                    enabled = true;
                    return true;
                case "0":
                case "off":
                case "false":
                    enabled = false;
                    return true;
                default:
                    enabled = true;
                    return false;
            }
        }

        /// <summary>进在线城镇(<see cref="StartOnlineGame"/> 或 <see cref="ResumeOnlineGame"/>):每次按新会话、新选中角色重建流程与控制器。</summary>
        private void BeginOnlineTownController()
        {
            DisposeOnlineTown();
            if (_shell == null || _shell.Flow == null || _shell.Flow.SelectedCharacter == null)
            {
                return;
            }
            if (_onlineAtlas == null && !_legacyTown && _assets != null)
            {
                _onlineAtlas = TownAtlas.Open(_assets);
            }
            if (_onlineAtlas != null)
            {
                _onlineTown = OnlineTownController.Create(
                    _shell,
                    _onlineAtlas,
                    _onlineSelectOptions?.Titles,
                    _onlineAreaSync,
                    line => Debug.Log(line));
            }
        }

        /// <summary>造完旧流程、清理上一段角色残余状态后开跑在线城镇流程。</summary>
        private void ResetOnlineTownGameState()
        {
            _frozenFlow = null;
            _frozenBot = null;
            _frozenPaused = false;
            _paused = false;
            _flow = null;
            _flowBot = null;
            _session = null;
            _progress = null;
            _avatars = null;
            _questBook = null;
            _questTried = false;
            _questHud = null;
            _townHud = null;
            _townHudTried = false;
            _bagWorn = default;
            _bagWornVersion = -1;
            _bagWornHero = null;
            _shownSerial = -1;
            _townHudSerial = -1;
            _questSeenSerial = -1;
            _questMarksSerial = -1;
            _platesSerial = -1;
            _shopSeenSerial = -1;
        }

        private ITownWorld WrapOnlineTownWorld(ITownWorld townWorld)
        {
            return _onlineTown != null ? _onlineTown.WrapWorld(townWorld) : townWorld;
        }

        private HeroProgress CreateOnlineHeroProgress()
        {
            if (_onlineTown == null || _assets == null)
            {
                return null;
            }
            OnlineItemSyncData initialItems = _onlineTown.BuildInitialItemSyncData();
            HeroProgress progress = HeroProgress.CreateForOnlineHero(_assets, _onlineTown.Profile, initialItems);
            if (progress != null)
            {
                _progress = progress;
                _onlineTown.BindOnlineItems(progress, _assets, SyncHeroLook, applyInitial: false);
                CreateAvatars();
            }
            return progress;
        }

        private void RecordOnlineSafePosition()
        {
            if (_flow != null && _flow.Stage == GameStage.Town)
            {
                _onlineTown?.RecordBeforeStep();
            }
        }

        private bool IsOnlineAreaChangePending()
        {
            return _flow != null && _flow.Stage == GameStage.Town && _onlineTown != null && _onlineTown.IsAreaChangePending;
        }

        private void PollOnlineTown()
        {
            if (_onlineTown != null && _flow != null && _flow.Stage == GameStage.Town)
            {
                _onlineTown.Poll(OnlineNowMs());
            }
        }

        private List<SceneSprite> BuildOnlineTownOverlay()
        {
            if (_onlineTown == null || _flow == null || _flow.Stage != GameStage.Town)
            {
                return null;
            }
            return OnlineTownOverlay.Build(_menuArt, _onlineTown.Profile, _onlineTown.CurrentPrompt, (long)_clockMs);
        }

        private void ReturnToOnlineCharacterSelect()
        {
            if (_onlineTown != null)
            {
                _onlineTown.ReturnToCharacterSelect();
            }
            else
            {
                _shell?.ReturnToCharacterSelect();
            }
        }

        private void QuitOnlineAware(int exitCode = 0)
        {
            if (_onlineTown != null)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800)))
                    {
                        _onlineTown.QuitGameAsync(cts.Token).Wait(900);
                    }
                }
                catch (Exception)
                {
                }
                DisposeOnlineTown();
            }
            else if (_shell != null)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800)))
                    {
                        _shell.DisconnectForQuitAsync(cts.Token).Wait(900);
                    }
                }
                catch (Exception)
                {
                }
            }
            Quit(exitCode);
        }

        private void DisposeOnlineTown()
        {
            if (_onlineTown != null)
            {
                _onlineTown.Dispose();
                _onlineTown = null;
            }
        }
    }
}
