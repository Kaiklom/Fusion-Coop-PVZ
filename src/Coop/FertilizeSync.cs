using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Fertilizer used on a plant.
    //
    // Sky drops are each window's own (like sun), so a fertilizer exists on one machine only.
    // Fertilize.Use(col, row) picks the plant in that cell and plays the pouring animation; its animation
    // event Fertilize.Upgrade then heals the plant or swaps it for its upgrade (wall-nut -> tall-nut, ...)
    // through CreatePlant.SetPlant. Clients block SetPlant calls the game makes on its own, so the old
    // plant was removed and nothing replaced it.
    // - Upgrade runs as a replay on every machine: SetPlant is allowed and nothing is broadcast, and the
    //   new plant keeps the old one's owner.
    // - When the player's own fertilizer finishes (Upgrade), it is sent (client -> host -> other
    //   clients); the others run the game's Upgrade on the same plant, so all change at the same moment.
    // - Fertilizer an Endoflame throws, or the zombie kind, runs on every machine already (the plant's
    //   attacks are synced) and is not sent.
    internal static class FertilizeSync
    {
        private static readonly HashSet<System.IntPtr> Replayed = new();
        private static readonly HashSet<System.IntPtr> Thrown = new(); // Endoflame's
        private static int _endoFire;

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
        }

        [HarmonyPatch(typeof(EndoFlame), "OnFire")]
        internal static class EndoFirePatch
        {
            private static void Prefix() => _endoFire++;

            private static System.Exception Finalizer(System.Exception __exception)
            {
                _endoFire--;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Fertilize), nameof(Fertilize.Use), typeof(int), typeof(int))]
        internal static class UsePatch
        {
            private static void Postfix(Fertilize __instance)
            {
                if (_endoFire <= 0 || __instance == null) return;
                if (Thrown.Count > 64) Thrown.Clear();
                Thrown.Add(__instance.Pointer);
            }
        }

        private struct UpgradeState
        {
            internal bool WasReplaying, Send;
            internal int Col, Row;
            internal PlantType Type;
            internal byte Owner;
            internal HashSet<int> Before;
        }

        [HarmonyPatch(typeof(Fertilize), "Upgrade")]
        internal static class UpgradePatch
        {
            private static void Prefix(Fertilize __instance, out UpgradeState __state)
            {
                __state = new UpgradeState { WasReplaying = PlantSync.Replaying, Col = -1 };
                PlantSync.Replaying = true;
                var target = __instance != null ? __instance.theTargetPlant : null;
                if (target == null) return;
                __state.Col = target.thePlantColumn;
                __state.Row = target.thePlantRow;
                __state.Type = target.thePlantType;
                __state.Owner = PlantSync.OwnerOf(target);
                __state.Before = CellPlants(__state.Col, __state.Row);
                var ptr = __instance.Pointer;
                __state.Send = Session.Ready && Board.Instance != null && !__instance.zombie &&
                               !Replayed.Contains(ptr) && !Thrown.Contains(ptr);
            }

            // Plants that are new in the cell (the upgrade) belong to the old plant's owner;
            // then the player's own use goes to the others.
            private static void Postfix(UpgradeState __state)
            {
                if (__state.Col < 0) return;
                if (__state.Owner != 255)
                {
                    var list = Lawnf.Get1x1Plants(__state.Col, __state.Row);
                    for (int i = 0; list != null && i < list.Count; i++)
                    {
                        var p = list[i];
                        if (p != null && !__state.Before.Contains(p.GetInstanceID())) PlantSync.SetOwner(p, __state.Owner);
                    }
                }
                if (!__state.Send) return;
                var msg = Build(__state.Col, __state.Row, __state.Type);
                if (Session.Role == Role.Host) Session.SendToClients(msg);
                else Session.SendToHost(msg);
                FusionCoopMod.Log.Msg($"[fertilize] P{Session.LocalId + 1} {__state.Type} c{__state.Col} r{__state.Row} -> sent, cell now {CellText(__state.Col, __state.Row)}");
            }

            private static System.Exception Finalizer(System.Exception __exception, Fertilize __instance, UpgradeState __state)
            {
                PlantSync.Replaying = __state.WasReplaying;
                if (__instance != null)
                {
                    Replayed.Remove(__instance.Pointer);
                    Thrown.Remove(__instance.Pointer);
                }
                return __exception;
            }
        }

        // "TallNut 4000/4000 + Pot 300/300": every plant in the cell, for the logs.
        internal static string CellText(int col, int row)
        {
            var list = Lawnf.Get1x1Plants(col, row);
            if (list == null || list.Count == 0) return "empty";
            var parts = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null) parts.Add($"{p.thePlantType} {p.thePlantHealth}/{p.thePlantMaxHealth}");
            }
            return parts.Count == 0 ? "empty" : string.Join(" + ", parts);
        }

        private static HashSet<int> CellPlants(int col, int row)
        {
            var set = new HashSet<int>();
            var list = Lawnf.Get1x1Plants(col, row);
            if (list == null) return set;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) set.Add(list[i].GetInstanceID());
            return set;
        }

        private static byte[] Build(int col, int row, PlantType type) =>
            Protocol.Build(Msg.FertilizeUsed, w =>
            {
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)type);
            });

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type != Msg.FertilizeUsed) return;
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            if (Replay(from, col, row, type)) Session.SendToClients(Build(col, row, type), from);
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.FertilizeUsed) return;
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            Replay(255, col, row, type);
        }

        // The game's own Upgrade on a fertilizer made here for it: no item prefab holds a Fertilize
        // (drops get theirs added at runtime), so a bare one is added to a new object at the plant.
        internal static bool Replay(byte from, int col, int row, PlantType type)
        {
            string who = from == 255 ? "host" : $"P{from + 1}";
            var plant = PlantSync.FindPlant(col, row, type);
            if (plant == null || plant.thePlantType != type)
            {
                FusionCoopMod.Log.Warning($"[fertilize] {who} {type} c{col} r{row}: no such plant here");
                return false;
            }
            var go = new GameObject("FusionCoop fertilizer");
            go.transform.position = plant.transform.position;
            Fertilize fert = null;
            try
            {
                fert = go.AddComponent<Fertilize>();
                fert.theTargetPlant = plant;
                Replayed.Add(fert.Pointer);
                fert.Upgrade();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[fertilize] {who} {type} c{col} r{row}: upgrade failed: {e.Message}");
            }
            finally
            {
                if (fert != null) Replayed.Remove(fert.Pointer);
                if (go != null) Object.Destroy(go);
            }
            FusionCoopMod.Log.Msg($"[fertilize] {who} {type} c{col} r{row} -> cell now {CellText(col, row)}");
            return true;
        }
    }
}
