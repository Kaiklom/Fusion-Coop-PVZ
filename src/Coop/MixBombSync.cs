using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Jicamagic (MixBomb): put on the middle of a recipe row (left, middle, right) it kills the three and drops
    // the titan's card (Lawnf.SetDroppedCard); otherwise it gives its own card back. In the Jicamagic level it
    // levels up three identical plants instead.
    //
    // Only the host runs it (MixBomb.AttributeEvent), so the plants die once and are synced from there. A card
    // it drops belongs to whoever placed the Jicamagic: the host's own drop stays, a client's is sent to that
    // client and dropped only on its window.
    internal static class MixBombSync
    {
        private static byte _owner = 255; // host: who placed the Jicamagic being run

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;

        [HarmonyPatch(typeof(MixBomb), "AttributeEvent")]
        internal static class AttributePatch
        {
            private static bool Prefix(MixBomb __instance)
            {
                if (IsClient)
                {
                    // the rest plays out here through plant sync; a plant using itself up dies on each
                    // window by itself (its death isn't sent), as the game's first step here does
                    __instance.Die((Plant.DieReason)2);
                    return false;
                }
                if (!IsHost) return true;
                byte owner = PlantSync.OwnerOf(__instance);
                if (owner == 255) owner = PlantSync.Requester;
                _owner = owner == 255 ? (byte)0 : owner;
                return true;
            }

            private static System.Exception Finalizer(System.Exception __exception)
            {
                _owner = 255;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Lawnf), nameof(Lawnf.SetDroppedCard))]
        internal static class DropPatch
        {
            private static bool Prefix(Vector2 position, PlantType theSeedType, ref DroppedCard __result)
            {
                if (_owner == 255 || _owner == Session.LocalId) return true;
                byte to = _owner;
                Session.SendToPlayer(to, Protocol.Build(Msg.MixCardDrop, w =>
                {
                    w.Write(position.x);
                    w.Write(position.y);
                    w.Write((int)theSeedType);
                }));
                FusionCoopMod.Log.Msg($"[mix] P{to + 1}'s Jicamagic dropped {theSeedType} -> P{to + 1}");
                __result = null;
                return false;
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.MixCardDrop) return;
            var r = pkt.Reader();
            var position = new Vector2(r.ReadSingle(), r.ReadSingle());
            var type = (PlantType)r.ReadInt32();
            if (Board.Instance == null) return;
            try
            {
                Lawnf.SetDroppedCard(position, type, 0);
                FusionCoopMod.Log.Msg($"[mix] our Jicamagic dropped {type}");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[mix] dropping {type} failed: {e.Message}");
            }
        }
    }
}
