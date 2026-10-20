using System;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Game speed and pause. Fast-forward (InGameBtn.SpeedTrigger), SlowTrigger, Board.Update hotkeys and
    // every pausing menu all end in Time.timeScale, so that value is synced directly.
    //
    // - Host: broadcasts its value (including pause = 0) on every change and once a second while on a
    //   board, so a client that joins late or was choosing cards picks it up. The host's value wins.
    // - Client: speed changes (> 0) are sent to the host, which applies them and broadcasts.
    //   A client can't pause: when its game sets 0 (pause menu, card menus) the host's speed is put back.
    //   While the host is paused the client is held at 0 every frame.
    // - A client only follows once its own battle runs (after card selection), so the intro camera and
    //   the card chooser still work while the host is paused or fast.
    // GameAPP.config.gameSpeed is a saved setting and is left alone.
    internal static class SpeedSync
    {
        private static float _last = -1;      // last value this machine has seen/applied
        private static float _hostValue = -1; // client: the host's current timeScale (-1 = unknown)
        private static double _nextKeepalive;

        internal static bool HostPaused => Session.Role == Role.Client && _hostValue == 0f && BattleRunning;

        // On a board and past card selection.
        private static bool BattleRunning =>
            Board.Instance != null && (InitBoard.Instance == null || InitBoard.Instance.ready);

        internal static void Init()
        {
            Session.HostGameHandler += (from, pkt) =>
            {
                if (pkt.Type != Msg.SpeedSet || Board.Instance == null) return;
                float v = pkt.Reader().ReadSingle();
                if (!(v > 0f && v <= 20f)) return;
                if (Time.timeScale <= 0f)
                {
                    // Host is paused: the client can't unpause, tell it again.
                    Session.SendToPlayer(from, Build(0f));
                    return;
                }
                Time.timeScale = v;
                _last = v;
                Session.SendToClients(Build(v)); // the sender too, so a stale keepalive can't undo it
                FusionCoopMod.Log.Msg($"[speed] P{from + 1} set x{v:0.##}");
            };
            Session.ClientGameHandler += pkt =>
            {
                if (pkt.Type != Msg.SpeedSet) return;
                float v = pkt.Reader().ReadSingle();
                if (!(v >= 0f && v <= 20f)) return;
                bool changed = Math.Abs(v - _hostValue) > 0.001f;
                bool pauseChanged = (v == 0f) != (_hostValue == 0f);
                _hostValue = v;
                if (!BattleRunning) return;
                Time.timeScale = v;
                _last = v;
                if (pauseChanged) Overlay.Toast(v == 0f ? "Host paused the game" : "Host resumed the game");
                if (changed) FusionCoopMod.Log.Msg($"[speed] host x{v:0.##}");
            };
        }

        private static byte[] Build(float v) => Protocol.Build(Msg.SpeedSet, w => w.Write(v));

        internal static void Update()
        {
            if (!Session.Ready)
            {
                _last = -1;
                _hostValue = -1;
                return;
            }
            if (Session.Role == Role.Host) HostUpdate();
            else ClientUpdate();
        }

        private static void HostUpdate()
        {
            if (Board.Instance == null)
            {
                _last = -1;
                return;
            }
            float now = Time.timeScale;
            bool changed = _last < 0 || Math.Abs(now - _last) >= 0.001f;
            if (!changed && Session.Now < _nextKeepalive) return;
            _nextKeepalive = Session.Now + 1.0;
            Session.SendToClients(Build(now));
            if (changed) FusionCoopMod.Log.Msg($"[speed] host x{now:0.##} -> clients");
            _last = now;
        }

        private static void ClientUpdate()
        {
            if (!BattleRunning)
            {
                _last = -1;
                return;
            }
            float now = Time.timeScale;
            if (_hostValue < 0f)
            {
                _last = now; // nothing from the host yet
                return;
            }
            if (_hostValue == 0f)
            {
                // Host is paused: hold it whatever local menus do.
                if (now != 0f) Time.timeScale = 0f;
                _last = 0f;
                return;
            }
            if (_last < 0f)
            {
                // Battle just started here: take the host's speed.
                Time.timeScale = _hostValue;
                _last = _hostValue;
                return;
            }
            if (Math.Abs(now - _last) < 0.001f) return;
            if (now <= 0f)
            {
                // Local pause attempt: only the host can pause.
                Time.timeScale = _hostValue;
                _last = _hostValue;
                Overlay.Toast("Only the host can pause");
                return;
            }
            _last = now;
            Session.SendToHost(Build(now));
            FusionCoopMod.Log.Msg($"[speed] local x{now:0.##} -> host");
        }

        // Closing a menu resets timeScale to this player's saved speed setting. On a client that
        // would be sent to the host as a speed change, so put the host's value back first.
        [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.BackToGame))]
        internal static class BackToGamePatch
        {
            private static void Postfix()
            {
                if (Session.Role != Role.Client || !Session.Ready || _hostValue < 0f || !BattleRunning) return;
                Time.timeScale = _hostValue;
                _last = _hostValue;
            }
        }
    }
}
