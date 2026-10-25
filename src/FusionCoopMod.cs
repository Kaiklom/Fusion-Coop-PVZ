using System;
using FusionCoop.Net;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FusionCoop.FusionCoopMod), "FusionCoop", "0.11.0", "Crispy DoDo Bird")]
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
        private static MelonPreferences_Entry<int> _autoLevel;
        private static MelonPreferences_Entry<string> _sweep;
        private static MelonPreferences_Entry<int> _sweepSeconds;
        private static MelonPreferences_Entry<bool> _testTools;
        private static double _autoLevelAt = -1, _autoForceAt = -1;
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
            _autoLevel = cat.CreateEntry("AutoLevel", 0, "Testing: host enters this Adventure level once a client joins and starts it; clients auto-ready (0 = off)");
            _sweep = cat.CreateEntry("Sweep", "", "Testing: Challenge levels the host plays through, e.g. '1-20' (both windows log [sweep] lines; empty = off)");
            _sweepSeconds = cat.CreateEntry("SweepSeconds", 45, "Testing: seconds per level in the sweep");
            Coop.LevelSweep.Parse(_sweep.Value);
            Coop.LevelSweep.Seconds = _sweepSeconds.Value;
            _testTools = cat.CreateEntry("TestTools", true, "Testing: F5/F6 pick a zombie, 9 (host) spawns it in the lane under the cursor, F4 +1000 sun");
            Coop.TestTools.Enabled = _testTools.Value;
            Overlay.IpDraft = _lastIp.Value;

            Coop.PlantSync.Init();
            Coop.ShovelGloveSync.Init();
            Coop.SpeedSync.Init();
            Coop.LevelSync.Init();
            Coop.ZombieSync.Init();
            Coop.PlantAttackSync.Init();
            Coop.MowerSync.Init();
            Coop.GraveSync.Init();
            Coop.PrizeSync.Init();
            Coop.FertilizeSync.Init();
            PatchAll(new[]
            {
                typeof(Coop.PlantSync.TryToSetPlantByCardPatch),
                typeof(Coop.PlantSync.SetPlantCapture),
                typeof(Coop.ShovelGloveSync.GetPlantsOnMousePatch),
                typeof(Coop.ShovelGloveSync.ShovelUsePatch),
                typeof(Coop.ShovelGloveSync.TryToPickPlantPatch),
                typeof(Coop.ShovelGloveSync.TryToSetPlantByGlovePatch),
                typeof(Coop.SpeedSync.BackToGamePatch),
                typeof(Coop.LevelSync.EnterGamePatch),
                typeof(Coop.LevelSync.StartButtonPatch),
                typeof(Coop.LevelSync.RestartPatch),
                typeof(Coop.LevelSync.EnterLoseMenuPatch),
                typeof(Coop.LevelSync.EnterMainMenuPatch),
                typeof(Coop.ZombieSync.SetZombiePatch),
                typeof(Coop.ZombieSync.SetZombieMindPatch),
                typeof(Coop.ZombieSync.TakeDamagePatch),
                typeof(Coop.ZombieSync.DiePatch),
                typeof(Coop.ZombieSync.MoveUpdatePatch),
                typeof(Coop.ZombieSync.AttackUpdatePatch),
                typeof(Coop.ZombieSync.NewZombieUpdatePatch),
                typeof(Coop.PlantSync.PlantDiePatch),
                typeof(Coop.PlantSync.PresentRandomPatch),
                typeof(Coop.PlantSync.PresentSuperRandomPatch),
                typeof(Coop.PlantSync.PresentHappyRandomPatch),
                typeof(Coop.PlantSync.MixLimitPatch),
                typeof(Coop.ZombieSync.SetFreezePatch),
                typeof(Coop.ZombieSync.SetColdPatch),
                typeof(Coop.ZombieSync.ButteredPatch),
                typeof(Coop.ZombieSync.SetMindControlPatch),
                typeof(Coop.ZombieSync.HammerUsePatch),
                typeof(Coop.ZombieSync.HammerCrushPatch),
                typeof(Coop.ZombieSync.MagnetAttractPatch),
                typeof(Coop.ZombieSync.LadderEnterPatch),
                typeof(Coop.ZombieSync.LadderStayPatch),
                typeof(Coop.ZombieSync.SetLadderPatch),
                typeof(Coop.ZombieSync.SetWeaponPatch),
                typeof(Coop.ZombieSync.MachineNutTypePatch),
                typeof(Coop.ZombieSync.MachineNutBouncePatch),
                typeof(Coop.ZombieSync.CharredPatch),
                typeof(Coop.ZombieSync.AttackEffectPatch),
                typeof(Coop.ZombieSync.JacksonSummonPatch),
                typeof(Coop.ZombieSync.JacksonAnimSummonPatch),
                typeof(Coop.PlantSync.ClientPlantDamagePatch),
                typeof(Coop.PlantSync.ClientPlantCrashedPatch),
                typeof(Coop.PlantAttackSync.ShootUpdatePatch),
                typeof(Coop.PlantAttackSync.SetTriggerPatch),
                typeof(Coop.PlantAttackSync.SetBoolPatch),
                typeof(Coop.MowerSync.TriggerPatch),
                typeof(Coop.MowerSync.StartMovePatch),
                typeof(Coop.GraveSync.SetGridItemPatch),
                typeof(Coop.GraveSync.TakeDamagePatch),
                typeof(Coop.GraveSync.DiePatch),
                typeof(Coop.GraveSync.SetZombiePatch),
                typeof(Coop.GraveSync.PreviewPatch),
                typeof(Coop.PrizeSync.WinPatch),
                typeof(Coop.PrizeSync.VictoryUpdatePatch),
                typeof(Coop.PrizeSync.SetAwardPatch),
                typeof(Coop.PrizeSync.ClickPatch),
                typeof(Coop.PrizeSync.ClickedPatch),
                typeof(Coop.PrizeSync.GoBackPatch),
                typeof(Coop.FertilizeSync.EndoFirePatch),
                typeof(Coop.FertilizeSync.UsePatch),
                typeof(Coop.FertilizeSync.UpgradePatch),
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
            if (_autoLevel.Value > 0) AutoLevelTest();
            Coop.LevelSweep.Update();
            Coop.SpeedSync.Update();
            Coop.LevelSync.Update();
            Coop.ZombieSync.Update();
            Coop.PlantSync.Update();
            Coop.GraveSync.Update();
        }

        // Testing: host enters level AutoLevel 3 s after a client joins, forces the start 25 s later.
        private static void AutoLevelTest()
        {
            if (Session.Role == Role.Client)
            {
                Coop.LevelSync.AutoReady();
                return;
            }
            if (Session.Role != Role.Host || Session.Names.Count < 2) return;
            double now = Session.Now;
            if (_autoLevelAt < 0) _autoLevelAt = now + 3;
            else if (_autoLevelAt > 0 && now >= _autoLevelAt)
            {
                _autoLevelAt = 0;
                _autoForceAt = now + 25;
                Log.Msg($"[test] entering Adventure {_autoLevel.Value}");
                Il2Cpp.UIMgr.EnterGame(Il2Cpp.LevelType.Advanture, _autoLevel.Value, -1, null);
            }
            else if (_autoForceAt > 0 && now >= _autoForceAt)
            {
                _autoForceAt = 0;
                Log.Msg("[test] forcing start");
                Coop.LevelSync.ForceStart();
            }
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
            if (Input.GetKeyDown(KeyCode.F12)) Coop.LevelSync.ForceStart();
            Coop.TestTools.HandleKeys();
            // In-room actions work with the panel hidden; the rejoin prompt shows on screen anyway.
            if (Input.GetKeyDown(KeyCode.F10) && Coop.LevelSync.CanRejoin)
            {
                Coop.LevelSync.RequestRejoin();
                return;
            }
            // Session setup and IP typing only with the panel open, so digits don't edit the IP mid-game.
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

            // IP editing: digits (top row or keypad), '.', Backspace. Off in battle with test keys on (9 spawns).
            if (Coop.TestTools.Enabled && Coop.TestTools.InBattle) return;
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
