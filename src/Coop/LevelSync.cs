using System.Collections.Generic;
using System.Reflection;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;

namespace FusionCoop.Coop
{
    // Level lifecycle follows the host.
    //
    // - Every Adventure entry (level button, retry, restart, next level after a win) goes through
    //   UIMgr.EnterGame; the host's call is replayed on clients. Clients can't enter levels themselves.
    // - Card selection is per player (each window's own chooser). The start button
    //   (StartGameBtn.OnMouseUp, which sets InitBoard.Instance.ready) only marks a player ready, the host
    //   included. When the last player is ready every window presses its start button. Host F12 forces it.
    // - Host lose (UIMgr.EnterLoseMenu) and host leaving to the main menu are replayed on clients.
    //   A client's own lose is ignored: only the host's board decides.
    // - Clients can't restart. A client going to the main menu leaves the room.
    // - Before following the host out of a level, a client tears its board down the way the game's
    //   restart does (ClearTheBoard, UIManager.PopAll, Board.Die); otherwise the old plants stay on screen.
    // - A client joining mid-level is sent into the host's current level.
    internal static class LevelSync
    {
        private struct LevelArgs
        {
            internal LevelType Type;
            internal int Number, Id;
            internal string Name;
        }

        // Host state
        private static bool _onLevel, _battleStarted;
        private static LevelArgs _current;
        internal static readonly HashSet<byte> ReadyPlayers = new();

        // Client state
        private static bool _hostStarted, _clientOnLevel, _snapshotAsked;
        private static byte _readyMask; // from the host

        private static StartGameBtn _startBtn; // last start button pressed on this machine
        private static bool _replaying;

        private static readonly MethodInfo StartBtnOnMouseUp = AccessTools.Method(typeof(StartGameBtn), "OnMouseUp");

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host;

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
            Session.PlayerJoined += id =>
            {
                if (!_onLevel) return;
                Session.SendToPlayer(id, BuildEnter(_current));
                if (_battleStarted) Session.SendToPlayer(id, Protocol.Build(Msg.BattleStart));
                else Session.SendToPlayer(id, BuildReady());
                FusionCoopMod.Log.Msg($"[level] sent current level {_current.Type} {_current.Number} to P{id + 1}");
            };
            Session.PlayerLeft += id =>
            {
                ReadyPlayers.Remove(id);
                if (!_onLevel || _battleStarted) return;
                Session.SendToClients(BuildReady());
                TryStartIfAllReady();
            };
        }

        internal static void Reset()
        {
            ReadyPlayers.Clear();
            _hostStarted = false;
            _clientOnLevel = false;
            _readyMask = 0;
        }

        private static byte[] BuildEnter(LevelArgs a) =>
            Protocol.Build(Msg.LevelEnter, w =>
            {
                w.Write((int)a.Type);
                w.Write(a.Number);
                w.Write(a.Id);
                w.Write(a.Name != null);
                if (a.Name != null) w.Write(a.Name);
            });

        // ---------- ready check ----------

        private static bool AllReady()
        {
            foreach (var id in Session.Names.Keys)
                if (!ReadyPlayers.Contains(id)) return false;
            return true;
        }

        private static byte[] BuildReady()
        {
            byte mask = 0;
            foreach (var id in ReadyPlayers) mask |= (byte)(1 << id);
            return Protocol.Build(Msg.ReadyState, w => w.Write(mask));
        }

        private static string Waiting()
        {
            string s = "";
            foreach (var id in Session.Names.Keys)
                if (!ReadyPlayers.Contains(id)) s += $"P{id + 1} ";
            return s.Trim();
        }

        // Host: start once everyone (host included) has pressed start.
        private static void TryStartIfAllReady()
        {
            if (!_onLevel || _battleStarted || !AllReady()) return;
            FusionCoopMod.Log.Msg("[level] everyone ready, starting");
            StartLocal();
        }

        // Host hotkey: start without waiting.
        internal static void ForceStart()
        {
            if (!IsHost || !_onLevel || _battleStarted) return;
            FusionCoopMod.Log.Msg($"[level] host forced start (waiting for: {Waiting()})");
            StartLocal();
        }

        // Press this machine's start button for real (or set the flag it sets when it was never pressed).
        private static void StartLocal()
        {
            Replay(() =>
            {
                if (_startBtn != null && StartBtnOnMouseUp != null)
                {
                    StartBtnOnMouseUp.Invoke(_startBtn, null);
                }
                else if (InitBoard.Instance != null)
                {
                    StartGameBtn.SaveCards();
                    InitBoard.Instance.ready = true;
                }
            });
            if (IsHost) AnnounceStartIfReady();
        }

        private static void AnnounceStartIfReady()
        {
            if (!_onLevel || _battleStarted || InitBoard.Instance == null || !InitBoard.Instance.ready) return;
            _battleStarted = true;
            Session.SendToClients(Protocol.Build(Msg.BattleStart));
            FusionCoopMod.Log.Msg("[level] battle started -> clients");
        }

        // Client, every frame: once our battle runs, ask the host for the plants already placed.
        // (On a normal synchronised start the host has none yet, so this costs nothing.)
        internal static void Update()
        {
            if (!IsClient || !_clientOnLevel || !_hostStarted || _snapshotAsked) return;
            if (InitBoard.Instance == null || !InitBoard.Instance.ready || Board.Instance == null) return;
            _snapshotAsked = true;
            PlantSync.RequestSnapshot();
        }

        // Status line for the overlay, null when not choosing cards.
        internal static string ReadyText()
        {
            bool choosing = IsHost ? _onLevel && !_battleStarted : IsClient && _clientOnLevel && !_hostStarted;
            if (!choosing) return null;
            string s = "ready: ";
            foreach (var id in Session.Names.Keys)
            {
                bool ready = IsHost ? ReadyPlayers.Contains(id) : (_readyMask & (1 << id)) != 0;
                s += $"P{id + 1} {(ready ? "OK" : "...")}   ";
            }
            return s + (IsHost ? "  [F12] force start" : "");
        }

        // Same teardown as PauseMenu_Btn.Restart before leaving a board.
        private static void TearDownBoard()
        {
            var board = Board.Instance;
            if (board == null) return;
            try
            {
                board.ClearTheBoard();
                GameAPP.UIManager?.PopAll();
                board.Die();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Error($"[level] board teardown failed: {e.Message}");
            }
        }

        // ---------- patches ----------

        [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.EnterGame))]
        internal static class EnterGamePatch
        {
            private static bool Prefix()
            {
                if (IsClient && !_replaying)
                {
                    Overlay.Toast("Only the host chooses the level");
                    return false;
                }
                return true;
            }

            private static void Postfix(LevelType levelType, int levelNumber, int id, string name)
            {
                if (!IsHost) return;
                _current = new LevelArgs { Type = levelType, Number = levelNumber, Id = id, Name = name };
                _onLevel = true;
                _battleStarted = false;
                _startBtn = null;
                ReadyPlayers.Clear();
                Session.SendToClients(BuildEnter(_current));
                FusionCoopMod.Log.Msg($"[level] host entered {levelType} {levelNumber} (id {id}) -> clients");
            }
        }

        [HarmonyPatch(typeof(StartGameBtn), "OnMouseUp")]
        internal static class StartButtonPatch
        {
            private static bool Prefix(StartGameBtn __instance)
            {
                if (_replaying) return true;
                _startBtn = __instance;
                if (IsHost && _onLevel && !_battleStarted)
                {
                    if (ReadyPlayers.Add(0)) Session.SendToClients(BuildReady());
                    if (AllReady()) return true;
                    Overlay.Toast($"Ready - waiting for {Waiting()} to choose cards");
                    return false;
                }
                if (IsClient && _clientOnLevel && !_hostStarted)
                {
                    Session.SendToHost(Protocol.Build(Msg.BattleReady));
                    Overlay.Toast("Ready - waiting for the others to choose cards");
                    return false;
                }
                return true;
            }

            private static void Postfix()
            {
                if (IsHost) AnnounceStartIfReady();
            }
        }

        [HarmonyPatch(typeof(PauseMenu_Btn), "Restart")]
        internal static class RestartPatch
        {
            private static bool Prefix()
            {
                if (!IsClient) return true;
                Overlay.Toast("Only the host can restart");
                return false;
            }
        }

        [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.EnterLoseMenu))]
        internal static class EnterLoseMenuPatch
        {
            private static bool Prefix()
            {
                // Clients don't lose on their own; the host's board decides.
                return !(IsClient && !_replaying && Board.Instance != null);
            }

            private static void Postfix(string reason)
            {
                if (!IsHost || !_onLevel) return;
                Session.SendToClients(Protocol.Build(Msg.LevelLose, w => w.Write(reason ?? "")));
                FusionCoopMod.Log.Msg($"[level] host lost ({reason}) -> clients");
            }
        }

        [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.EnterMainMenu))]
        internal static class EnterMainMenuPatch
        {
            private static void Prefix()
            {
                // A client quitting the level to the main menu leaves the room.
                if (IsClient && !_replaying && _clientOnLevel)
                {
                    FusionCoopMod.Log.Msg("[level] client went to the main menu: leaving the room");
                    Session.Stop();
                    Overlay.Toast("You left the room");
                }
            }

            private static void Postfix()
            {
                if (!IsHost || !_onLevel) return;
                _onLevel = false;
                _battleStarted = false;
                _startBtn = null;
                ReadyPlayers.Clear();
                Session.SendToClients(Protocol.Build(Msg.LevelExit));
                FusionCoopMod.Log.Msg("[level] host left to main menu -> clients");
            }
        }

        // ---------- network ----------

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type != Msg.BattleReady || !_onLevel || _battleStarted) return;
            if (!ReadyPlayers.Add(from)) return;
            Overlay.Toast($"P{from + 1} is ready");
            FusionCoopMod.Log.Msg($"[level] P{from + 1} ready");
            Session.SendToClients(BuildReady());
            TryStartIfAllReady();
        }

        private static void OnClientMessage(Packet pkt)
        {
            var r = pkt.Reader();
            switch (pkt.Type)
            {
                case Msg.LevelEnter:
                {
                    var type = (LevelType)r.ReadInt32();
                    int number = r.ReadInt32(), id = r.ReadInt32();
                    string name = r.ReadBoolean() ? r.ReadString() : null;
                    _hostStarted = false;
                    _clientOnLevel = true;
                    _snapshotAsked = false;
                    _readyMask = 0;
                    _startBtn = null;
                    FusionCoopMod.Log.Msg($"[level] host entered {type} {number} (id {id}), following");
                    Replay(() =>
                    {
                        TearDownBoard();
                        UIMgr.EnterGame(type, number, id, name);
                    });
                    break;
                }
                case Msg.ReadyState:
                    _readyMask = r.ReadByte();
                    break;
                case Msg.BattleStart:
                    _hostStarted = true;
                    FusionCoopMod.Log.Msg("[level] host started the battle");
                    StartLocal();
                    break;
                case Msg.LevelLose:
                {
                    string reason = r.ReadString();
                    FusionCoopMod.Log.Msg($"[level] host lost ({reason})");
                    Replay(() => UIMgr.EnterLoseMenu(reason));
                    break;
                }
                case Msg.LevelExit:
                    _hostStarted = false;
                    _clientOnLevel = false;
                    FusionCoopMod.Log.Msg("[level] host left to main menu");
                    if (Board.Instance != null)
                    {
                        Replay(() =>
                        {
                            TearDownBoard();
                            UIMgr.EnterMainMenu();
                        });
                    }
                    break;
            }
        }

        private static void Replay(System.Action action)
        {
            bool was = _replaying;
            _replaying = true;
            try
            {
                action();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Error($"[level] replay failed: {e}");
            }
            finally
            {
                _replaying = was;
            }
        }
    }
}
