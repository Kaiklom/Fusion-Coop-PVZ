using System;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Game speed and pause. Fast-forward (InGameBtn.SpeedTrigger), SlowTrigger, Board.Update hotkeys and
    // every pausing menu all end in Time.timeScale, so that value is synced directly while on a board.
    //
    // - Host: any change (including pause = 0) is broadcast. The host's value always wins.
    // - Client: speed changes (> 0) are sent to the host, which applies and forwards them.
    //   A client can't pause: when its game sets 0 (pause menu, card menus) the host's speed is put
    //   back next frame, so the menu opens but the game keeps running. While the host is paused,
    //   the client stays at 0.
    // GameAPP.config.gameSpeed is a saved setting and is left alone.
    internal static class SpeedSync
    {
        private static float _last = -1;     // last value this machine has seen/applied
        private static float _hostValue = -1; // client: the host's current timeScale (-1 = unknown)
        internal static bool HostPaused => Session.Role == Role.Client && _hostValue == 0f;

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
                Session.SendToClients(Build(v), from);
                FusionCoopMod.Log.Msg($"[speed] P{from + 1} set x{v:0.##}");
            };
            Session.ClientGameHandler += pkt =>
            {
                if (pkt.Type != Msg.SpeedSet || Board.Instance == null) return;
                float v = pkt.Reader().ReadSingle();
                if (!(v >= 0f && v <= 20f)) return;
                bool pauseChanged = (v == 0f) != (_hostValue == 0f);
                _hostValue = v;
                Time.timeScale = v;
                _last = v;
                if (pauseChanged) Overlay.Toast(v == 0f ? "Host paused the game" : "Host resumed the game");
                FusionCoopMod.Log.Msg($"[speed] host x{v:0.##}");
            };
        }

        private static byte[] Build(float v) => Protocol.Build(Msg.SpeedSet, w => w.Write(v));

        internal static void Update()
        {
            if (!Session.Ready || Board.Instance == null)
            {
                _last = -1;
                _hostValue = -1;
                return;
            }
            float now = Time.timeScale;

            if (Session.Role == Role.Host)
            {
                if (_last >= 0 && Math.Abs(now - _last) < 0.001f) return;
                _last = now; // first frame on a board announces the speed too
                Session.SendToClients(Build(now));
                FusionCoopMod.Log.Msg($"[speed] host x{now:0.##} -> clients");
                return;
            }

            // Client
            if (_last < 0)
            {
                _last = now; // wait for the host's value
                return;
            }
            if (Math.Abs(now - _last) < 0.001f) return;

            if (_hostValue == 0f)
            {
                // Host is paused: keep it paused whatever local menus do.
                Time.timeScale = 0f;
                _last = 0f;
                return;
            }
            if (now <= 0f)
            {
                // Local pause attempt: only the host can pause.
                if (_hostValue > 0f)
                {
                    Time.timeScale = _hostValue;
                    _last = _hostValue;
                    Overlay.Toast("Only the host can pause");
                }
                else
                {
                    _last = now;
                }
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
                if (Session.Role != Role.Client || !Session.Ready || _hostValue < 0f) return;
                Time.timeScale = _hostValue;
                _last = _hostValue;
            }
        }
    }
}
