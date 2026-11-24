using System;
using System.Collections.Generic;
using Dnf.Play;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// <see cref="TownDemo"/> 的在线副本流接线(r18-dungeonflow):
    /// 仅在 <c>-dnfonline</c> 且已进入在线城镇(<c>_shell != null &amp;&amp; _onlineTown != null &amp;&amp; !_netDungeon</c>)时挂载
    /// <see cref="OnlineDungeonFlowController"/>;离线、<c>-dnfnetdungeon</c> 与无字体截图路径零改动。
    /// </summary>
    public sealed partial class TownDemo
    {
        private OnlineDungeonPermissionTable _onlineDungeonPermissions;
        private OnlineDungeonFlowController _onlineDungeon;

        private void AttachOnlineDungeonFlow(string worldMapPath, TownAtlas atlas, ITownWorld townWorld)
        {
            DisposeOnlineDungeon();
            if (_shell == null || _shell.Session == null || _onlineTown == null || _flow == null || _netDungeon)
            {
                return;
            }

            _onlineDungeonPermissions = new OnlineDungeonPermissionTable();
            OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
                _onlineDungeonPermissions,
                _assets,
                texts: WorldTexts.Shared,
                worldMapPath: worldMapPath);

            TownPlace? returnPlace = townWorld != null ? (TownPlace?)townWorld.Return : null;
            TownAtlas usedAtlas = _onlineAtlas ?? atlas;

            var options = new OnlineDungeonFlowOptions
            {
                Atlas = usedAtlas,
                ReturnPlace = returnPlace,
                Fonts = _fonts ?? OverlayArt.SharedFonts,
                Log = line => Debug.Log(line),
                NetLog = line => Debug.Log(line),
                OnDisconnected = () => _shell?.Flow?.NotifyDisconnected(),
                CurrentArea = () =>
                {
                    if (usedAtlas == null || _flow == null || !_flow.Place.HasValue)
                    {
                        return null;
                    }
                    TownPlace place = _flow.Place.Value;
                    return usedAtlas.GetArea(place.Town, place.Area);
                },
                HeroLevel = () => _onlineTown?.Profile?.Level ?? _progress?.Level ?? 1,
            };

            _onlineDungeon = OnlineDungeonFlowController.Create(
                _shell.Session,
                _shell.DungeonPermissions,
                _flow,
                policy,
                ctx => BuildOnlineDungeonRoom(ctx),
                options);
            AttachOnlineSettlement(usedAtlas);
        }

        private OnlineDungeonRoomBuildResult BuildOnlineDungeonRoom(OnlineDungeonRoomBuildContext context)
        {
            HeroProgress progress = HeroProgressForRun();
            OnlineDungeonRoomBuildResult result = OnlineDungeonRoomBuilder.Build(
                context,
                _assets,
                progress,
                _seed,
                !_noDamage,
                DungeonSession.SettlementDelayMs,
                line => Debug.Log(line));
            if (result.Succeeded)
            {
                _heroInput = true;
            }
            return result;
        }

        private void PollOnlineDungeon()
        {
            if (_onlineDungeon == null)
            {
                return;
            }
            if (_onlineTown == null || _flow == null || !ReferenceEquals(_onlineDungeon.Flow, _flow))
            {
                DisposeOnlineDungeon();
                return;
            }
            _onlineDungeon.Poll(OnlineNowMs());
        }

        private List<SceneSprite> BuildOnlineDungeonOverlay()
        {
            if (_onlineDungeon == null || _flow == null || _flow.Stage == GameStage.DungeonSelect)
            {
                return null;
            }
            return OnlineDungeonOverlay.Build(
                _menuArt,
                _onlineDungeon.ActiveNotice,
                (long)_clockMs,
                _flow.Stage == GameStage.Town);
        }

        private void PrepareOnlineDungeonForQuit()
        {
            _onlineDungeon?.PrepareForQuit();
        }

        private void DisposeOnlineDungeon()
        {
            DisposeOnlineSettlement();
            if (_onlineDungeon != null)
            {
                _onlineDungeon.Dispose();
                _onlineDungeon = null;
            }
            _onlineDungeonPermissions = null;
        }
    }
}
