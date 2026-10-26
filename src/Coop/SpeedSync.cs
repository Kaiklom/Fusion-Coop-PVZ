using System;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Game speed, pause and difficulty: the host's values, always.
    //
    // Fast-forward (InGameBtn.SpeedTrigger), SlowTrigger, Board.Update hotkeys and every pausing menu all end in
    // Time.timeScale, so that value is synced directly. The game also puts each window's own saved speed back
    // at many moments (a level starting, a menu closing, ...); clients used to send such changes to the host as
    // their own, so the speed flipped between the players' settings. Now:
    // - Host: sends its timeScale (0 = paused) and difficulty on every change and once a second while on a board.
    // - Client: holds the host's timeScale every frame once its battle runs (after card selection, so the intro
    //   camera and the card chooser still work while the host is paused or fast). Its own speed buttons and
    //   pause do nothing.
    // - Difficulty: GameAPP.config.difficulty is the host's while in the room.
    // - The client's pause menu shows the host's speed and difficulty on greyed-out sliders that can't be moved.
    //   Those sliders write the saved settings (config.gameSpeed, config.difficulty), so the player's own values
    //   are kept and put back on leaving the room.
    internal static class SpeedSync
    {
        private static float _last = -1;      // host: last value sent
        private static int _lastDifficulty = int.MinValue;
        private static float _hostValue = -1; // client: the host's current timeScale (-1 = unknown)
        private static int _hostDifficulty = -1;
        private static int _ownDifficulty = -1; // client: the player's setting before the room's
        private static float _ownSpeed = -1;
        private static float _hostSpeed = -1; // client: the host's last speed that isn't a pause
        private static double _nextKeepalive, _nextNote;

        internal static bool HostPaused => Session.Role == Role.Client && _hostValue == 0f && BattleRunning;

        // On a board and past card selection.
        private static bool BattleRunning =>
            Board.Instance != null && (InitBoard.Instance == null || InitBoard.Instance.ready);

        internal static void Init()
        {
            Session.ClientGameHandler += pkt =>
            {
                if (pkt.Type != Msg.SpeedSet) return;
                var r = pkt.Reader();
                float v = r.ReadSingle();
                int difficulty = r.ReadInt32();
                if (!(v >= 0f && v <= 20f)) return;
                bool changed = Math.Abs(v - _hostValue) > 0.001f;
                bool pauseChanged = (v == 0f) != (_hostValue == 0f);
                _hostValue = v;
                if (v > 0f) _hostSpeed = v;
                if (pauseChanged && BattleRunning) Overlay.Toast(v == 0f ? "Host paused the game" : "Host resumed the game");
                if (changed) FusionCoopMod.Log.Msg($"[speed] host x{v:0.##}");
                if (difficulty != _hostDifficulty) FusionCoopMod.Log.Msg($"[speed] host difficulty {difficulty}");
                _hostDifficulty = difficulty;
            };
        }

        private static byte[] Build(float v, int difficulty) => Protocol.Build(Msg.SpeedSet, w =>
        {
            w.Write(v);
            w.Write(difficulty);
        });

        private static int HostDifficulty()
        {
            var b = Board.Instance;
            if (b != null && b.lockedDifficulty >= 0) return b.lockedDifficulty;
            return GameAPP.config != null ? GameAPP.config.difficulty : 0;
        }

        internal static void Update()
        {
            if (!Session.Ready)
            {
                _last = -1;
                _lastDifficulty = int.MinValue;
                _hostValue = -1;
                _hostDifficulty = -1;
                _hostSpeed = -1;
                RestoreSettings();
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
            int difficulty = HostDifficulty();
            bool changed = _last < 0 || Math.Abs(now - _last) >= 0.001f || difficulty != _lastDifficulty;
            if (!changed && Session.Now < _nextKeepalive) return;
            _nextKeepalive = Session.Now + 1.0;
            Session.SendToClients(Build(now, difficulty));
            if (changed) FusionCoopMod.Log.Msg($"[speed] host x{now:0.##}, difficulty {difficulty} -> clients");
            _last = now;
            _lastDifficulty = difficulty;
        }

        private static void ClientUpdate()
        {
            ApplyDifficulty();
            if (!BattleRunning || _hostValue < 0f) return;
            float now = Time.timeScale;
            if (Math.Abs(now - _hostValue) < 0.001f) return;
            Time.timeScale = _hostValue;
            if (Session.Now >= _nextNote)
            {
                // the game puts this window's own speed back now and then: only noted, at most every 10 s
                _nextNote = Session.Now + 10;
                FusionCoopMod.Log.Msg($"[speed] local x{now:0.##} replaced by the host's x{_hostValue:0.##}");
            }
            if (now <= 0f && _hostValue > 0f) Overlay.Toast("Only the host can pause");
        }

        private static void ApplyDifficulty()
        {
            var cfg = GameAPP.config;
            if (_hostDifficulty < 0 || cfg == null) return;
            if (_ownDifficulty < 0)
            {
                _ownDifficulty = cfg.difficulty;
                _ownSpeed = cfg.gameSpeed;
            }
            if (cfg.difficulty != _hostDifficulty) cfg.difficulty = _hostDifficulty;
        }

        private static void RestoreSettings()
        {
            if (_ownDifficulty < 0) return;
            var cfg = GameAPP.config;
            if (cfg != null)
            {
                cfg.difficulty = _ownDifficulty;
                if (_ownSpeed > 0f) cfg.gameSpeed = _ownSpeed;
            }
            FusionCoopMod.Log.Msg($"[speed] own difficulty {_ownDifficulty} and speed x{_ownSpeed:0.##} put back");
            _ownDifficulty = -1;
            _ownSpeed = -1;
        }

        // ---------- pause menu sliders on a client ----------

        private static bool ClientFollows => Session.Role == Role.Client && Session.Ready;

        private static void GreyOut(UnityEngine.UI.Slider slider)
        {
            slider.interactable = false;
            foreach (var img in slider.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                img.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            foreach (var t in slider.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true))
                t.alpha = 0.6f;
        }

        // The game's Update writes the slider's value into the setting and its text: set the host's value first.
        [HarmonyPatch(typeof(DifficultyMgr), "Update")]
        internal static class DifficultySliderPatch
        {
            private static void Prefix(DifficultyMgr __instance)
            {
                var slider = __instance.slider;
                if (!ClientFollows || _hostDifficulty < 0 || slider == null) return;
                if ((int)slider.value != _hostDifficulty) slider.value = _hostDifficulty;
            }

            private static void Postfix(DifficultyMgr __instance)
            {
                var slider = __instance.slider;
                if (ClientFollows && _hostDifficulty >= 0 && slider != null && slider.interactable) GreyOut(slider);
            }
        }

        [HarmonyPatch(typeof(GameSpeedMgr), "Update")]
        internal static class SpeedSliderPatch
        {
            private static void Prefix(GameSpeedMgr __instance)
            {
                var slider = __instance.slider;
                var gears = GameSpeedMgr.Gears;
                if (!ClientFollows || _hostSpeed <= 0f || slider == null || gears == null || gears.Count == 0) return;
                int best = 0;
                for (int i = 1; i < gears.Count; i++)
                    if (Math.Abs(gears[i] - _hostSpeed) < Math.Abs(gears[best] - _hostSpeed)) best = i;
                if ((int)slider.value != best) slider.value = best;
            }

            private static void Postfix(GameSpeedMgr __instance)
            {
                var slider = __instance.slider;
                if (ClientFollows && _hostSpeed > 0f && slider != null && slider.interactable) GreyOut(slider);
            }
        }

        // Closing a menu resets timeScale to this player's saved speed setting: put the host's back at once.
        [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.BackToGame))]
        internal static class BackToGamePatch
        {
            private static void Postfix()
            {
                if (Session.Role != Role.Client || !Session.Ready || _hostValue < 0f || !BattleRunning) return;
                Time.timeScale = _hostValue;
            }
        }
    }
}
