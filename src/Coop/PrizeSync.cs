using FusionCoop.Net;
using Il2CppGameLevel;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Winning a level is decided by the host.
    //
    // Every trophy comes from Lawnf.SetAward: the board's victory check (BoardVictory.OnUpdate calls it
    // directly, BoardVictory.Win too), and mini-games that win their own way (Bejeweled, 2048, bricks, ...).
    // Clicking it (PrizeMgr.Clicked) records the level as completed in the save (unlocks such as Challenge
    // fusions are read from those flags), plays the light-out, and then PrizeMgr.GoBack leaves for the
    // next level or menu. Clients:
    // - don't run the victory check and don't drop trophies themselves; each trophy the host's game drops
    //   is dropped at the same spot everywhere, and a real one also ends the client's board,
    // - can't click the trophy; the host's click is replayed, so every player's own save records the
    //   level (a level the client already had gives nothing new, as in the game),
    // - leave the trophy screen when the host does; the host's next level then arrives through LevelSync.
    internal static class PrizeSync
    {
        private static bool _replaying;
        internal static bool GoingBack; // client: replaying the host's GoBack

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;
        private static bool ClientBlocks => IsClient && !_replaying;

        [HarmonyPatch(typeof(BoardVictory), nameof(BoardVictory.Win))]
        internal static class WinPatch
        {
            private static bool Prefix() => !ClientBlocks;
        }

        // The victory check (BoardVictory.OnUpdate) ends the board (Board.over) and runs the board's victory
        // action: the trophy, or in Survival / Odyssey the next round (Board.EnterNextRound, the buff menu,
        // StartNextRound). Clients don't check by themselves: when the host's check passes it sends
        // VictoryFired and clients run the same OnUpdate with the check forced to pass, so they take the
        // same path (a trophy dropped in there is not sent separately).
        private static bool _forceVictory; // client: replaying the host's passed check
        private static bool _hostInVictory;

        [HarmonyPatch(typeof(BoardVictory), nameof(BoardVictory.OnUpdate))]
        internal static class VictoryUpdatePatch
        {
            private static bool Prefix(Board board, out bool __state)
            {
                __state = board != null && board.over;
                if (IsClient) return _forceVictory;
                _hostInVictory = true;
                return true;
            }

            private static void Postfix(Board board, bool __state)
            {
                if (!IsHost || board == null || __state || !board.over) return;
                Session.SendToClients(Protocol.Build(Msg.VictoryFired));
                FusionCoopMod.Log.Msg($"[prize] host's victory check passed (round {board.theCurrentSurvivalRound}/{board.theSurvivalMaxRound}) -> clients");
            }

            private static System.Exception Finalizer(System.Exception __exception)
            {
                _hostInVictory = false;
                return __exception;
            }
        }

        [HarmonyPatch]
        internal static class VictoryCheckPatch
        {
            // BoardVictory.Check itself is an empty body shared by ~400 methods: only the real ones.
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() => new System.Reflection.MethodBase[]
            {
                AccessTools.Method(typeof(DefaultVictory), "Check"),
                AccessTools.Method(typeof(CustomVictory), "Check"),
                AccessTools.Method(typeof(IZVictory), "Check"),
            };

            private static bool Prefix(ref bool __result)
            {
                if (!_forceVictory) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Lawnf), nameof(Lawnf.SetAward))]
        internal static class SetAwardPatch
        {
            private static bool Prefix(ref PrizeMgr __result)
            {
                if (!ClientBlocks) return true;
                __result = null;
                return false;
            }

            private static void Postfix(Board board, Vector2 position, bool killZombie, bool fake)
            {
                if (!IsHost || board == null || _hostInVictory) return; // clients drop it in their own OnUpdate
                Session.SendToClients(Protocol.Build(Msg.AwardDropped, w =>
                {
                    w.Write(position.x);
                    w.Write(position.y);
                    w.Write(killZombie);
                    w.Write(fake);
                }));
                FusionCoopMod.Log.Msg($"[prize] host dropped a {(fake ? "fake " : "")}trophy -> clients");
            }
        }

        [HarmonyPatch(typeof(PrizeMgr), "Click")]
        internal static class ClickPatch
        {
            private static bool Prefix()
            {
                if (!ClientBlocks) return true;
                Overlay.Toast("The host collects the trophy");
                return false;
            }
        }

        [HarmonyPatch(typeof(PrizeMgr), nameof(PrizeMgr.Clicked))]
        internal static class ClickedPatch
        {
            private static bool Prefix(PrizeMgr __instance, out bool __state)
            {
                __state = __instance.isClicked;
                return !ClientBlocks;
            }

            private static void Postfix(PrizeMgr __instance, bool __state)
            {
                if (__state || !IsHost || __instance.fake) return;
                Session.SendToClients(Protocol.Build(Msg.PrizeClicked));
                FusionCoopMod.Log.Msg("[prize] host collected the trophy -> clients");
            }
        }

        [HarmonyPatch(typeof(PrizeMgr), nameof(PrizeMgr.GoBack))]
        internal static class GoBackPatch
        {
            private static bool Prefix(PrizeMgr __instance)
            {
                if (ClientBlocks) return false; // wait for the host
                if (IsHost && !__instance.fake) Session.SendToClients(Protocol.Build(Msg.PrizeGoBack));
                return true;
            }
        }

        private static PrizeMgr FindPrize()
        {
            foreach (var p in Object.FindObjectsOfType<PrizeMgr>())
                if (p != null && !p.fake) return p;
            return null;
        }

        private static void Run(string what, System.Action action)
        {
            _replaying = true;
            try
            {
                action();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[prize] {what} failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            switch (pkt.Type)
            {
                case Msg.VictoryFired:
                {
                    var board = Board.Instance;
                    var victory = board != null ? board.boardVicotry : null;
                    if (victory == null)
                    {
                        FusionCoopMod.Log.Warning("[prize] host's victory check passed but this window has no board");
                        return;
                    }
                    _forceVictory = true;
                    try
                    {
                        Run("victory", () => victory.OnUpdate(board));
                    }
                    finally
                    {
                        _forceVictory = false;
                    }
                    FusionCoopMod.Log.Msg($"[prize] host's victory check passed here (round {board.theCurrentSurvivalRound}/{board.theSurvivalMaxRound})");
                    break;
                }
                case Msg.AwardDropped:
                {
                    var r = pkt.Reader();
                    var position = new Vector2(r.ReadSingle(), r.ReadSingle());
                    bool killZombie = r.ReadBoolean(), fake = r.ReadBoolean();
                    var board = Board.Instance;
                    if (board == null)
                    {
                        FusionCoopMod.Log.Warning("[prize] host dropped a trophy but this window has no board");
                        return;
                    }
                    Run("trophy", () =>
                    {
                        Lawnf.SetAward(board, position, killZombie, fake);
                        // a real trophy ends the board, as the host's victory check did (BoardVictory.Win)
                        if (fake || board.over) return;
                        board.over = true;
                        if (board.boardStatistics != null) board.boardStatistics.GameOver(GameResult.Victory);
                    });
                    FusionCoopMod.Log.Msg($"[prize] host dropped a {(fake ? "fake " : "")}trophy here");
                    break;
                }
                case Msg.PrizeClicked:
                {
                    var prize = FindPrize();
                    if (prize == null)
                    {
                        FusionCoopMod.Log.Warning("[prize] host collected the trophy but there is none here");
                        return;
                    }
                    if (!prize.isClicked) Run("collect", prize.Clicked);
                    break;
                }
                case Msg.PrizeGoBack:
                {
                    var prize = FindPrize();
                    if (prize == null) return;
                    GoingBack = true;
                    try
                    {
                        Run("leave", prize.GoBack);
                    }
                    finally
                    {
                        GoingBack = false;
                    }
                    break;
                }
            }
        }
    }
}
