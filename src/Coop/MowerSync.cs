using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Lawn mowers are started by the host.
    //
    // A mower starts when a zombie in its row touches it (Mower.OnTriggerStay2D, or StartMove).
    // On a client the puppet zombie trails the host a little and is often killed by the host's mower
    // before it reaches its own, so the client's mower stayed put. Clients now never start a mower
    // on their own: the host sends each start and the client starts the mower in that row. Zombies
    // a client's mower runs over are still killed by the host (puppets ignore local kills).
    internal static class MowerSync
    {
        private static bool _replaying;

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool ClientBattle =>
            Session.Role == Role.Client && Session.Ready && !_replaying && Board.Instance != null &&
            (InitBoard.Instance == null || InitBoard.Instance.ready);

        // Client: a mower that is not running yet may not start. Host: report a start.
        // __state = the mower was already running.
        private static bool BlockStart(Mower m, out bool state)
        {
            state = m.started;
            return m.started || !ClientBattle;
        }

        private static void ReportStart(Mower m, bool wasStarted)
        {
            if (wasStarted || !m.started || Session.Role != Role.Host || !Session.Ready) return;
            int row = m.theMowerRow;
            float x = m.transform.position.x;
            Session.SendToClients(Protocol.Build(Msg.MowerStarted, w =>
            {
                w.Write((sbyte)row);
                w.Write(x);
            }));
        }

        [HarmonyPatch(typeof(Mower), "OnTriggerStay2D")]
        internal static class TriggerPatch
        {
            private static bool Prefix(Mower __instance, out bool __state) => BlockStart(__instance, out __state);
            private static void Postfix(Mower __instance, bool __state) => ReportStart(__instance, __state);
        }

        [HarmonyPatch(typeof(Mower), nameof(Mower.StartMove))]
        internal static class StartMovePatch
        {
            private static bool Prefix(Mower __instance, out bool __state) => BlockStart(__instance, out __state);
            private static void Postfix(Mower __instance, bool __state) => ReportStart(__instance, __state);
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.MowerStarted || Board.Instance == null) return;
            var r = pkt.Reader();
            int row = r.ReadSByte();
            float x = r.ReadSingle();
            var mowers = Board.Instance.mowerArray;
            if (mowers == null) return;

            // The waiting mower in that row closest to the host's.
            Mower best = null;
            float bestDist = float.MaxValue;
            foreach (var m in mowers)
            {
                if (m == null || m.started || m.theMowerRow != row) continue;
                float d = Mathf.Abs(m.transform.position.x - x);
                if (d < bestDist)
                {
                    best = m;
                    bestDist = d;
                }
            }
            if (best == null)
            {
                FusionCoopMod.Log.Warning($"[mower] no waiting mower in row {row}");
                return;
            }
            _replaying = true;
            try
            {
                best.StartMove();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[mower] start failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }
    }
}
