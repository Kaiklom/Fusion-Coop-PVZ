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

        // The check itself also ends the board (Board.over) before it drops the trophy.
        [HarmonyPatch(typeof(BoardVictory), nameof(BoardVictory.OnUpdate))]
        internal static class VictoryUpdatePatch
        {
            private static bool Prefix() => !IsClient;
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
                if (!IsHost || board == null) return;
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
