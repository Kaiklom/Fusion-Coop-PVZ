using System;
using FusionCoop.Net;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FusionCoop.FusionCoopMod), "FusionCoop", "0.6.0", "Crispy DoDo Bird")]
[assembly: MelonGame(null, "PlantsVsZombiesRH")]
[assembly: HarmonyDontPatchAll]

namespace FusionCoop
{
    public sealed class FusionCoopMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static int Port => _port.Value;

        private static MelonPreferences_Entry<string> _name;
        private static MelonPreferences_Entry<string> _lastIp;
        private static MelonPreferences_Entry<int> _port;
        private static MelonPreferences_Entry<bool> _probes;
        private static MelonPreferences_Entry<string> _autoStart;
        private static bool _autoStarted;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;

            var cat = MelonPreferences.CreateCategory("FusionCoop");
            _name = cat.CreateEntry("PlayerName", Environment.UserName, "Name shown to other players");
            _lastIp = cat.CreateEntry("LastIp", "127.0.0.1", "Host IP to join (Radmin VPN address, e.g. 26.x.x.x)");
            _port = cat.CreateEntry("Port", Protocol.DefaultPort, "TCP port");
            _probes = cat.CreateEntry("ProbeLog", true, "Log game calls (plant/sun/shovel) for research");
            _autoStart = cat.CreateEntry("AutoStart", "", "Testing: 'host' or 'join' starts the session automatically");
            Overlay.IpDraft = _lastIp.Value;

            Coop.PlantSync.Init();
            Coop.ShovelGloveSync.Init();
            Coop.SpeedSync.Init();
            PatchAll(new[]
            {
                typeof(Coop.PlantSync.TryToSetPlantByCardPatch),
                typeof(Coop.PlantSync.SetPlantCapture),
                typeof(Coop.ShovelGloveSync.GetPlantsOnMousePatch),
                typeof(Coop.ShovelGloveSync.ShovelUsePatch),
                typeof(Coop.ShovelGloveSync.TryToPickPlantPatch),
                typeof(Coop.ShovelGloveSync.TryToSetPlantByGlovePatch),
                typeof(Coop.SpeedSync.BackToGamePatch),
            });
            if (_probes.Value) PatchProbes();
        }

        private void PatchProbes()
        {
            PatchAll(new[]
            {
                typeof(Probe.SetPlantProbe),
                typeof(Probe.UseSunProbe),
                typeof(Probe.GetSunProbe),
                typeof(Probe.ShovelProbe),
                typeof(Probe.CoinSunDieProbe),
                typeof(Probe.ClickCardProbe),
                typeof(Probe.TryToSetPlantByCardProbe),
                typeof(Probe.CoinSunMoveProbe),
            });
        }

        // Patch each class on its own so one bad signature does not stop the rest.
        private void PatchAll(Type[] types)
        {
            foreach (var type in types)
            {
                try
                {
                    HarmonyInstance.CreateClassProcessor(type).Patch();
                    Log.Msg($"patched {type.Name}");
                }
                catch (Exception e)
                {
                    Log.Error($"patch {type.Name} failed: {e.Message}");
                }
            }
        }

        public override void OnUpdate()
        {
            if (!_autoStarted && Time.realtimeSinceStartup > 5f)
            {
                _autoStarted = true;
                if (_autoStart.Value == "host") Session.StartHost(_port.Value, _name.Value);
                else if (_autoStart.Value == "join") Session.Join(_lastIp.Value, _port.Value, _name.Value);
            }
            HandleKeys();
            Session.Poll(ReadLocalCursor());
            Coop.SpeedSync.Update();
        }

        private static Net.CursorState _localCursor = new() { Col = -1, Row = -1 };

        // Live pointer while this window is focused and the mouse is inside it;
        // otherwise the last in-window position, marked inactive.
        private static Net.CursorState ReadLocalCursor()
        {
            var mp = Input.mousePosition;
            var cam = Camera.main;
            bool inWindow = mp.x >= 0 && mp.y >= 0 && mp.x < Screen.width && mp.y < Screen.height;
            if (cam == null || !Application.isFocused || !inWindow)
            {
                _localCursor.Active = false;
                return _localCursor;
            }

            var w = cam.ScreenToWorldPoint(mp);
            _localCursor.X = w.x;
            _localCursor.Y = w.y;
            _localCursor.Active = true;
            var mouse = Il2Cpp.Mouse.Instance;
            if (mouse != null && Il2Cpp.Board.Instance != null)
            {
                _localCursor.Col = (sbyte)Math.Clamp(mouse.theMouseColumn, -1, 127);
                _localCursor.Row = (sbyte)Math.Clamp(mouse.theMouseRow, -1, 127);
            }
            else
            {
                _localCursor.Col = -1;
                _localCursor.Row = -1;
            }
            return _localCursor;
        }

        public override void OnGUI() => Overlay.Draw();

        public override void OnApplicationQuit() => Session.Stop();

        private static void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.F8)) Overlay.PanelOpen = !Overlay.PanelOpen;
            if (!Overlay.PanelOpen) return;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                Session.StartHost(_port.Value, _name.Value);
            }
            else if (Input.GetKeyDown(KeyCode.F10))
            {
                _lastIp.Value = Overlay.IpDraft;
                MelonPreferences.Save();
                Session.Join(Overlay.IpDraft, _port.Value, _name.Value);
            }
            else if (Input.GetKeyDown(KeyCode.F11))
            {
                Session.Stop();
            }

            // IP editing: digits (top row or keypad), '.', Backspace.
            for (int i = 0; i <= 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i) || Input.GetKeyDown(KeyCode.Keypad0 + i))
                    Overlay.IpDraft += (char)('0' + i);
            }
            if (Input.GetKeyDown(KeyCode.Period) || Input.GetKeyDown(KeyCode.KeypadPeriod)) Overlay.IpDraft += '.';
            if (Input.GetKeyDown(KeyCode.Backspace) && Overlay.IpDraft.Length > 0)
                Overlay.IpDraft = Overlay.IpDraft.Substring(0, Overlay.IpDraft.Length - 1);
            if (Overlay.IpDraft.Length > 21) Overlay.IpDraft = Overlay.IpDraft.Substring(0, 21);
        }
    }
}
