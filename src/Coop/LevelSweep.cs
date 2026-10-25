using System.Collections.Generic;
using System.Linq;
using FusionCoop.Net;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Testing (Sweep pref, e.g. "1-20" or "7,10,42"): the host plays through those Challenge levels by itself,
    // each for SweepSeconds: main menu, enter the level, press start for everyone after 12 s, then the next.
    // Every window with the pref set logs a "[sweep]" line every 5 s (level, board state, plants, zombies,
    // cards) so a script can compare host and client windows by timestamp.
    internal static class LevelSweep
    {
        internal static List<int> Levels = new();
        internal static int Seconds = 45;

        private enum Step { Idle, ToMenu, Enter, Playing, Done }

        private static Step _step = Step.Idle;
        private static int _index = -1;
        private static double _stepAt, _startAt;
        private static long _snapSlot;

        internal static bool Enabled => Levels.Count > 0;

        internal static void Parse(string spec)
        {
            Levels.Clear();
            foreach (var part in (spec ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var range = part.Trim().Split('-');
                if (!int.TryParse(range[0], out int a)) continue;
                int b = range.Length > 1 && int.TryParse(range[1], out int bb) ? bb : a;
                for (int n = a; n <= b; n++) Levels.Add(n);
            }
        }

        internal static void Update()
        {
            if (!Enabled) return;
            double now = Session.Now;
            // on the PC clock's 5 s marks, so two windows on one PC log at the same moment
            long slot = System.DateTime.Now.Ticks / (5 * System.TimeSpan.TicksPerSecond);
            if (slot != _snapSlot)
            {
                _snapSlot = slot;
                Snapshot();
            }
            if (Session.Role == Role.Client)
            {
                LevelSync.AutoReady();
                return;
            }
            if (Session.Role != Role.Host || Session.Names.Count < 2) return;
            switch (_step)
            {
                case Step.Idle:
                    _step = Step.ToMenu;
                    _stepAt = now + 3;
                    break;
                case Step.ToMenu:
                    if (now < _stepAt) break;
                    if (++_index >= Levels.Count)
                    {
                        _step = Step.Done;
                        ToMainMenu();
                        FusionCoopMod.Log.Msg("[sweep] done");
                        break;
                    }
                    ToMainMenu();
                    _step = Step.Enter;
                    _stepAt = now + 4;
                    break;
                case Step.Enter:
                    if (now < _stepAt) break;
                    int level = Levels[_index];
                    FusionCoopMod.Log.Msg($"[sweep] entering Challenge {level}");
                    try
                    {
                        UIMgr.EnterGame(LevelType.Challenge, level, -1, null);
                    }
                    catch (System.Exception e)
                    {
                        FusionCoopMod.Log.Warning($"[sweep] Challenge {level}: EnterGame failed: {e.Message}");
                    }
                    _step = Step.Playing;
                    _startAt = now + 12;
                    _stepAt = now + Seconds;
                    break;
                case Step.Playing:
                    if (_startAt > 0 && now >= _startAt)
                    {
                        _startAt = 0;
                        FusionCoopMod.Log.Msg("[sweep] forcing start");
                        LevelSync.ForceStart();
                    }
                    if (now >= _stepAt) _step = Step.ToMenu;
                    break;
            }
        }

        // As the pause menu's main-menu button does: the board goes first, or its zombies live on into the next level.
        private static void ToMainMenu()
        {
            var board = Board.Instance;
            if (board == null) return;
            board.Die();
            UIMgr.EnterMainMenu();
        }

        private static void Snapshot()
        {
            var board = Board.Instance;
            string head = $"[sweep] {(Session.Role == Role.Host ? "host" : "client")} type {(int)GameAPP.theBoardType} level {GameAPP.theBoardLevel}";
            if (board == null)
            {
                FusionCoopMod.Log.Msg($"{head} no-board");
                return;
            }
            var plants = new List<string>();
            var plist = board.boardEntity != null ? board.boardEntity.plantArray : null;
            for (int i = 0; plist != null && i < plist.Count; i++)
            {
                var p = plist[i];
                if (p != null) plants.Add($"{p.thePlantColumn}.{p.thePlantRow}.{(int)p.thePlantType}");
            }
            plants.Sort(System.StringComparer.Ordinal);
            var zombies = new List<string>();
            var zlist = board.zombieArray;
            for (int i = 0; zlist != null && i < zlist.Count; i++)
            {
                var z = zlist[i];
                if (z != null) zombies.Add($"{z.theZombieRow}.{(int)z.theZombieType}");
            }
            zombies.Sort(System.StringComparer.Ordinal);
            var cards = new List<int>();
            foreach (var c in Object.FindObjectsOfType<CardUI>())
                if (c != null) cards.Add((int)c.thePlantType);
            cards.Sort();
            bool ready = InitBoard.Instance == null || InitBoard.Instance.ready;
            FusionCoopMod.Log.Msg(
                $"{head} ready {(ready ? 1 : 0)} over {(board.over ? 1 : 0)} wave {board.theWave}/{board.theMaxWave} sun {board.theSun} " +
                $"zombies {zombies.Count} [{string.Join(" ", zombies)}] plants {plants.Count} [{string.Join(" ", plants)}] " +
                $"cards {cards.Count} [{string.Join(" ", cards)}]");
        }
    }
}
