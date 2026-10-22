using FusionCoop.Net;
using Il2CppGameLevel;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Winning a level is decided by the host.
    //
    // The board's victory check (BoardVictory.Win) drops the trophy (Lawnf.SetAward -> PrizeMgr); clicking
    // it (PrizeMgr.Clicked) records the level as completed in the save, unlocks what it unlocks, plays the
    // light-out, and then PrizeMgr.GoBack leaves for the next level or menu. Clients:
    // - don't win on their own; the host's win drops the trophy everywhere,
    // - can't click the trophy; the host's click is replayed, so every player's own save records the
    //   level (a level the client already had gives nothing new, as in the game),
    // - leave the trophy screen when the host does; the host's next level then arrives through LevelSync.
    internal static class PrizeSync
    {
        private static bool _replaying;
        private static int _wonBoard; // host: board instance that already sent its win
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

            private static void Postfix()
            {
                var board = Board.Instance;
                if (!IsHost || board == null || board.GetInstanceID() == _wonBoard) return;
                _wonBoard = board.GetInstanceID();
                Session.SendToClients(Protocol.Build(Msg.LevelWon));
                FusionCoopMod.Log.Msg("[prize] host won -> clients");
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
                case Msg.LevelWon:
                {
                    var victory = Board.Instance != null ? Board.Instance.boardVicotry : null;
                    if (victory == null)
                    {
                        FusionCoopMod.Log.Warning("[prize] host won but this window has no board");
                        return;
                    }
                    Run("win", victory.Win);
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
