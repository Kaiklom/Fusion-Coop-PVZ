using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Items a player uses on a plant. The dropped items themselves are each window's own (like fertilizer);
    // the use runs on every machine:
    // - Bucket.Use: buckets, helmets' base, mecha fragments, jack-in-the-boxes, pickaxes, ladders, ... - each
    //   plant in the cell under the mouse gets Plant.UseItem(type, item): a recipe turns it into another plant,
    //   SuperMachineNut heals, magnets store the item, HypnoMagnet summons, ...
    // - Helmet.Use: a tall-nut becomes a tall-nut football, a football tall-nut is repaired.
    // - Money.ReinforcePlant: the bean.
    // The player's own use runs as a replay (SetPlant allowed, nothing broadcast) and is sent (client -> host
    // -> other clients); the others make an item of that type and run the same call on the same cell
    // (the mouse cell is set for that call only). New plants in the cell keep the old plant's owner.
    // Fertilize (also a Bucket) is FertilizeSync's.
    internal static class ItemUseSync
    {
        private enum Kind : byte { Bucket, Helmet, Bean }

        private static int _depth;      // Helmet.Use and Bucket.Use may run inside each other
        private static bool _replaying; // running another player's use

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
        }

        private struct UseState
        {
            internal bool Track, Send, WasReplaying;
            internal Kind Kind;
            internal int Col, Row, Item;
            internal PlantType Plant;
            internal byte Owner;
            internal List<(int id, PlantType type)> Before;
        }

        private static void Begin(Kind kind, int item, int col, int row, PlantType plant, out UseState state)
        {
            state = new UseState { WasReplaying = PlantSync.Replaying };
            _depth++;
            if (_depth > 1 || !Session.Ready || Board.Instance == null) return;
            if (PlantSync.Replaying && !_replaying) return; // inside some other replay
            state.Track = true;
            state.Send = !_replaying;
            state.Kind = kind;
            state.Item = item;
            state.Col = col;
            state.Row = row;
            state.Plant = plant;
            state.Before = CellPlants(col, row);
            state.Owner = 255;
            foreach (var (id, _) in state.Before)
            {
                var p = FindById(col, row, id);
                if (p != null && PlantSync.OwnerOf(p) != 255) state.Owner = PlantSync.OwnerOf(p);
            }
            PlantSync.Replaying = true;
        }

        private static void End(UseState state)
        {
            _depth--;
            if (!state.Track) return;
            PlantSync.Replaying = state.WasReplaying;
            var made = new List<PlantType>();
            foreach (var a in CellPlants(state.Col, state.Row))
            {
                if (state.Before.Exists(b => b.id == a.id)) continue;
                made.Add(a.type);
                var p = FindById(state.Col, state.Row, a.id);
                if (p != null && state.Owner != 255) PlantSync.SetOwner(p, state.Owner);
            }
            string what = $"{state.Kind} {state.Item} c{state.Col} r{state.Row}, new: {(made.Count == 0 ? "-" : string.Join(",", made))}";
            if (!state.Send)
            {
                FusionCoopMod.Log.Msg($"[item] replayed {what}, cell now {FertilizeSync.CellText(state.Col, state.Row)}");
                return;
            }
            var msg = Build(state.Kind, state.Item, state.Col, state.Row, state.Plant, state.Owner);
            if (Session.Role == Role.Host) Session.SendToClients(msg);
            else Session.SendToHost(msg);
            FusionCoopMod.Log.Msg($"[item] P{Session.LocalId + 1} used {what} -> sent");
        }

        private static bool IsFertilizer(Bucket item) => item != null && item.TryCast<Fertilize>() != null;

        [HarmonyPatch(typeof(Bucket), nameof(Bucket.Use))]
        internal static class BucketUsePatch
        {
            private static void Prefix(Bucket __instance, out UseState __state)
            {
                var m = Mouse.Instance;
                if (IsFertilizer(__instance) || m == null)
                {
                    __state = new UseState();
                    _depth++;
                    return;
                }
                Begin(Kind.Bucket, (int)__instance.theBucketType, m.theMouseColumn, m.theMouseRow, PlantType.Nothing, out __state);
            }

            private static System.Exception Finalizer(System.Exception __exception, UseState __state)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Helmet), nameof(Helmet.Use))]
        internal static class HelmetUsePatch
        {
            private static void Prefix(Helmet __instance, out UseState __state)
            {
                var m = Mouse.Instance;
                if (m == null)
                {
                    __state = new UseState();
                    _depth++;
                    return;
                }
                Begin(Kind.Helmet, (int)__instance.theBucketType, m.theMouseColumn, m.theMouseRow, PlantType.Nothing, out __state);
            }

            private static System.Exception Finalizer(System.Exception __exception, UseState __state)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Money), nameof(Money.ReinforcePlant))]
        internal static class BeanPatch
        {
            private static void Prefix(Plant plant, out UseState __state)
            {
                if (plant == null)
                {
                    __state = new UseState();
                    _depth++;
                    return;
                }
                Begin(Kind.Bean, 0, plant.thePlantColumn, plant.thePlantRow, plant.thePlantType, out __state);
            }

            private static System.Exception Finalizer(System.Exception __exception, UseState __state)
            {
                End(__state);
                return __exception;
            }
        }

        private static List<(int id, PlantType type)> CellPlants(int col, int row)
        {
            var list = new List<(int, PlantType)>();
            var plants = Lawnf.Get1x1Plants(col, row);
            for (int i = 0; plants != null && i < plants.Count; i++)
                if (plants[i] != null) list.Add((plants[i].GetInstanceID(), plants[i].thePlantType));
            return list;
        }

        private static Plant FindById(int col, int row, int id)
        {
            var plants = Lawnf.Get1x1Plants(col, row);
            for (int i = 0; plants != null && i < plants.Count; i++)
                if (plants[i] != null && plants[i].GetInstanceID() == id) return plants[i];
            return null;
        }

        private static byte[] Build(Kind kind, int item, int col, int row, PlantType plant, byte owner) =>
            Protocol.Build(Msg.ItemUsed, w =>
            {
                w.Write((byte)kind);
                w.Write(item);
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)plant);
                w.Write(owner);
            });

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type != Msg.ItemUsed) return;
            var (kind, item, col, row, plant, owner) = Read(pkt);
            if (owner == 255) owner = from;
            if (Apply(from, kind, item, col, row, plant)) Session.SendToClients(Build(kind, item, col, row, plant, owner), from);
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.ItemUsed) return;
            var (kind, item, col, row, plant, _) = Read(pkt);
            Apply(255, kind, item, col, row, plant);
        }

        private static (Kind, int, int, int, PlantType, byte) Read(Packet pkt)
        {
            var r = pkt.Reader();
            var kind = (Kind)r.ReadByte();
            int item = r.ReadInt32();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var plant = (PlantType)r.ReadInt32();
            byte owner = r.ReadByte();
            return (kind, item, col, row, plant, owner);
        }

        private static bool Apply(byte from, Kind kind, int item, int col, int row, PlantType plant)
        {
            string who = from == 255 ? "host" : $"P{from + 1}";
            var board = Board.Instance;
            if (board == null) return false;
            _replaying = true;
            try
            {
                if (kind == Kind.Bean)
                {
                    var p = PlantSync.FindPlant(col, row, plant);
                    if (p == null || p.thePlantType != plant || Money.Instance == null)
                    {
                        FusionCoopMod.Log.Warning($"[item] {who} bean on {plant} c{col} r{row}: no such plant here");
                        return false;
                    }
                    Money.Instance.ReinforcePlant(p);
                    return true;
                }
                return UseItemAt(who, kind, (BucketType)item, col, row, board);
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[item] {who} {kind} {item} c{col} r{row}: replay failed: {e.Message}");
                return false;
            }
            finally
            {
                _replaying = false;
            }
        }

        // An item of that type, used with the mouse cell set to the player's cell for this call only.
        private static bool UseItemAt(string who, Kind kind, BucketType type, int col, int row, Board board)
        {
            var mouse = Mouse.Instance;
            var items = GameAPP.itemManager;
            if (mouse == null || items == null)
            {
                FusionCoopMod.Log.Warning($"[item] {who} {type} c{col} r{row}: no mouse or item manager");
                return false;
            }
            var cell = Lawnf.Get1x1Plants(col, row);
            Vector2 pos = cell != null && cell.Count > 0 && cell[0] != null ? (Vector2)cell[0].transform.position : Vector2.zero;
            var bucket = items.SetBucket(board, type, pos);
            if (bucket == null)
            {
                FusionCoopMod.Log.Warning($"[item] {who} {type} c{col} r{row}: could not make the item");
                return false;
            }
            int oldCol = mouse.theMouseColumn, oldRow = mouse.theMouseRow;
            try
            {
                bucket.m = mouse;
                mouse.theMouseColumn = col;
                mouse.theMouseRow = row;
                if (kind == Kind.Helmet)
                {
                    var helmet = bucket.TryCast<Helmet>();
                    if (helmet == null)
                    {
                        FusionCoopMod.Log.Warning($"[item] {who} {type} c{col} r{row}: the item is not a helmet");
                        return false;
                    }
                    helmet.Use();
                }
                else bucket.Use();
            }
            finally
            {
                mouse.theMouseColumn = oldCol;
                mouse.theMouseRow = oldRow;
                if (bucket != null) Object.Destroy(bucket.gameObject); // not taken by the plant
            }
            return true;
        }
    }
}
