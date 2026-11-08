using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Plants frozen into ice blocks (FreezedPlant): the host decides.
    //
    // FreezedPlant.FreezePlant swaps a plant for an ice block (a GridItem holding a still copy of the plant and
    // its saved data); breaking the block (FreezedPlant.DieEvent -> SetEntity) puts the plant back. Freezing
    // comes from the snow lawn's blizzard (SnowMap.Freeze picks rows at random, on its own timer in each
    // window, also started by penguin zombies), drown projectiles and snow football zombies. Each window used to
    // freeze its own plants, and a client's block never broke (puppet zombies don't hit it), so its plant stayed
    // a still picture.
    // - Host: each freeze is sent before it happens (cell, plant type, fire), and each broken block.
    // - Client: its own freezes are blocked and its blocks take no damage; it freezes and breaks the host's.
    // - Ice blocks a level starts with (Board.SetFreezedPlant, snow level strategies and event nodes) are put
    //   at random cells with random plants in each window: clients make none themselves; the host sends its
    //   blocks every second (cell, plant type) and clients match them, as with graves (GraveSync).
    internal static class FreezeSync
    {
        private static bool _replaying;
        private static double _nextState;
        private static readonly Dictionary<(int, int), int> ExtraSeen = new(); // client: cell -> states it was extra in

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsHost => Session.Role == Role.Host && Session.Ready;

        private static bool ClientBattle =>
            Session.Role == Role.Client && Session.Ready && !_replaying && Board.Instance != null &&
            (InitBoard.Instance == null || InitBoard.Instance.ready);

        [HarmonyPatch(typeof(FreezedPlant), nameof(FreezedPlant.FreezePlant))]
        internal static class FreezePatch
        {
            private static bool Prefix(Plant plant, bool fire, ref FreezedPlant __result, out bool __state)
            {
                __state = false;
                if (ClientBattle)
                {
                    __result = null;
                    return false;
                }
                if (!IsHost || plant == null || Board.Instance == null) return true;
                int col = plant.thePlantColumn, row = plant.thePlantRow;
                var type = plant.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.PlantFrozen, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                    w.Write(fire);
                }));
                FusionCoopMod.Log.Msg($"[freeze] host froze {type} c{col} r{row}{(fire ? " (fire)" : "")} -> clients");
                // the plant leaves the board inside FreezePlant: that is not a death for clients to replay
                PlantSync.SuppressDeaths++;
                __state = true;
                return true;
            }

            private static System.Exception Finalizer(System.Exception __exception, bool __state)
            {
                if (__state) PlantSync.SuppressDeaths--;
                return __exception;
            }
        }

        // Clients make ice blocks only for the host's (replays and IceState).
        [HarmonyPatch(typeof(GridItem), nameof(GridItem.SetGridItem))]
        internal static class SetIceBlockPatch
        {
            private static bool Prefix(GridItemType theType) =>
                theType != GridItemType.IceBlock || !(Session.Role == Role.Client && Session.Ready && !_replaying);
        }

        // ---------- the host's ice blocks ----------

        internal static void Update()
        {
            if (!IsHost || Board.Instance == null || Session.Now < _nextState) return;
            _nextState = Session.Now + 1;
            var blocks = Blocks();
            Session.SendToClients(Protocol.Build(Msg.IceState, w =>
            {
                w.Write((ushort)blocks.Count);
                foreach (var b in blocks)
                {
                    w.Write((sbyte)b.theItemColumn);
                    w.Write((sbyte)b.theItemRow);
                    w.Write((int)b.thePlantType);
                }
            }));
        }

        private static List<FreezedPlant> Blocks()
        {
            var list = new List<FreezedPlant>();
            var items = Board.Instance != null ? Board.Instance.griditemArray : null;
            if (items == null) return list;
            foreach (var item in items)
            {
                if (item == null || item.theItemType != GridItemType.IceBlock) continue;
                var b = item.TryCast<FreezedPlant>();
                if (b != null && b.health > 0) list.Add(b);
            }
            return list;
        }

        private static FreezedPlant BlockAt(int col, int row)
        {
            foreach (var b in Blocks())
                if (b.theItemColumn == col && b.theItemRow == row) return b;
            return null;
        }

        private static void ApplyState(System.IO.BinaryReader r)
        {
            int count = r.ReadUInt16();
            var want = new HashSet<(int, int)>();
            for (int i = 0; i < count; i++)
            {
                int col = r.ReadSByte(), row = r.ReadSByte();
                var type = (PlantType)r.ReadInt32();
                want.Add((col, row));
                if (BlockAt(col, row) != null) continue;
                FreezedPlant made = null;
                Replay(() =>
                {
                    var item = GridItem.SetGridItem(col, row, GridItemType.IceBlock, default);
                    made = item != null ? item.TryCast<FreezedPlant>() : null;
                    if (made != null) made.InitFreezedPlant(type);
                });
                FusionCoopMod.Log.Msg($"[freeze] host's ice block ({type}) c{col} r{row} -> {(made != null ? "made" : "FAILED")}");
            }
            // Blocks the host doesn't have (two states in a row, so a break on its way lands first) go quietly:
            // breaking them would let a plant out.
            var board = Board.Instance;
            foreach (var b in Blocks())
            {
                var cell = (b.theItemColumn, b.theItemRow);
                if (want.Contains(cell))
                {
                    ExtraSeen.Remove(cell);
                    continue;
                }
                ExtraSeen[cell] = ExtraSeen.TryGetValue(cell, out var n) ? n + 1 : 1;
                if (ExtraSeen[cell] < 2) continue;
                ExtraSeen.Remove(cell);
                FusionCoopMod.Log.Msg($"[freeze] ice block c{cell.Item1} r{cell.Item2} the host doesn't have: removed");
                board.griditemArray.Remove(b);
                Object.Destroy(b.gameObject);
            }
        }

        [HarmonyPatch(typeof(FreezedPlant), nameof(FreezedPlant.TakeDamage))]
        internal static class IceDamagePatch
        {
            private static bool Prefix() => !ClientBattle;
        }

        [HarmonyPatch(typeof(FreezedPlant), "DieEvent")]
        internal static class IceBrokenPatch
        {
            private static void Prefix(FreezedPlant __instance)
            {
                if (!IsHost || __instance == null || Board.Instance == null) return;
                int col = __instance.theItemColumn, row = __instance.theItemRow;
                Session.SendToClients(Protocol.Build(Msg.IceBroken, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                }));
                FusionCoopMod.Log.Msg($"[freeze] host's ice block c{col} r{row} broke -> clients");
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.PlantFrozen && pkt.Type != Msg.IceBroken && pkt.Type != Msg.IceState) return;
            if (Board.Instance == null) return;
            var r = pkt.Reader();
            if (pkt.Type == Msg.IceState)
            {
                ApplyState(r);
                return;
            }
            int col = r.ReadSByte(), row = r.ReadSByte();
            if (pkt.Type == Msg.PlantFrozen)
            {
                var type = (PlantType)r.ReadInt32();
                bool fire = r.ReadBoolean();
                var plant = PlantSync.FindPlant(col, row, type);
                if (plant == null || plant.thePlantType != type)
                {
                    FusionCoopMod.Log.Warning($"[freeze] no {type} at c{col} r{row} to freeze");
                    return;
                }
                FreezedPlant ice = null;
                Replay(() => ice = FreezedPlant.FreezePlant(plant, fire));
                FusionCoopMod.Log.Msg($"[freeze] froze {type} c{col} r{row} with the host -> {(ice != null ? "ok" : "FAILED")}");
                return;
            }
            FreezedPlant block = null;
            foreach (var item in Object.FindObjectsOfType<FreezedPlant>())
                if (item != null && item.theItemColumn == col && item.theItemRow == row) block = item;
            if (block == null)
            {
                FusionCoopMod.Log.Warning($"[freeze] no ice block at c{col} r{row} to break");
                return;
            }
            Replay(() => block.Die());
            FusionCoopMod.Log.Msg($"[freeze] ice block c{col} r{row} broke with the host");
        }

        // The plant it lets out is made with SetPlant, which clients only run for replays.
        private static void Replay(System.Action action)
        {
            bool was = PlantSync.Replaying;
            _replaying = true;
            PlantSync.Replaying = true;
            try { action(); }
            catch (System.Exception e) { FusionCoopMod.Log.Warning($"[freeze] replay failed: {e.Message}"); }
            finally
            {
                _replaying = false;
                PlantSync.Replaying = was;
            }
        }
    }
}
