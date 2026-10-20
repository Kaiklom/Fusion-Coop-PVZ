using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;

namespace FusionCoop.Coop
{
    // Shovel and glove (move) on owned plants.
    //
    // Ownership: while the local player shovels or picks up with the glove, plants owned by another
    // player are filtered out of Mouse.GetPlantsOnMouse, so the game simply finds nothing there.
    // Plants nobody placed (level pots, effect spawns) stay usable by everyone.
    //
    // Sync: the action runs locally as usual, then is sent to the other machines, which replay it with
    // the same game call (Die(ByShovel) / SetPlant with targetPlant). Client actions go through the host,
    // which applies and forwards them.
    internal static class ShovelGloveSync
    {
        private enum Ctx { None, Shovel, GlovePick }

        private static Ctx _ctx;
        private static Plant _shovelTarget;
        private static int _targetCol, _targetRow;
        private static PlantType _targetType;
        private static byte _targetOwner;

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool OwnedBySomeoneElse(Plant p)
        {
            byte owner = PlantSync.OwnerOf(p);
            return owner != 255 && owner != Session.LocalId;
        }

        [HarmonyPatch(typeof(Mouse), nameof(Mouse.GetPlantsOnMouse))]
        internal static class GetPlantsOnMousePatch
        {
            private static void Postfix(Il2CppSystem.Collections.Generic.List<Plant> __result)
            {
                if (_ctx == Ctx.None || __result == null || !Session.Ready) return;
                for (int i = __result.Count - 1; i >= 0; i--)
                    if (OwnedBySomeoneElse(__result[i])) __result.RemoveAt(i);

                if (_ctx == Ctx.Shovel && __result.Count > 0 && _shovelTarget == null)
                {
                    // Shovel.Use kills element 0.
                    var p = __result[0];
                    _shovelTarget = p;
                    _targetCol = p.thePlantColumn;
                    _targetRow = p.thePlantRow;
                    _targetType = p.thePlantType;
                    _targetOwner = PlantSync.OwnerOf(p);
                }
            }
        }

        // ---------- shovel ----------

        [HarmonyPatch(typeof(Shovel), nameof(Shovel.Use))]
        internal static class ShovelUsePatch
        {
            private static void Prefix()
            {
                _ctx = Ctx.Shovel;
                _shovelTarget = null;
            }

            private static void Postfix()
            {
                _ctx = Ctx.None;
                var target = _shovelTarget;
                _shovelTarget = null;
                if (target == null || !Session.Ready || PlantSync.Replaying) return;
                // dieMeansLose plants are skipped by the game's shovel.
                if (target.dieMeansLose) return;

                byte owner = _targetOwner == 255 ? Session.LocalId : _targetOwner;
                PlantSync.Owners.Remove(target.GetInstanceID());
                var frame = BuildRemoved(owner, _targetCol, _targetRow, _targetType);
                if (Session.Role == Role.Host) Session.SendToClients(frame);
                else Session.SendToHost(frame);
                FusionCoopMod.Log.Msg($"[shovel] P{Session.LocalId + 1} {_targetType} c{_targetCol} r{_targetRow} -> sent");
            }
        }

        // ---------- glove ----------

        [HarmonyPatch(typeof(Mouse), "TryToPickPlant")]
        internal static class TryToPickPlantPatch
        {
            private static void Prefix() => _ctx = Ctx.GlovePick;
            private static void Postfix() => _ctx = Ctx.None;
        }

        [HarmonyPatch(typeof(Mouse), "TryToSetPlantByGlove")]
        internal static class TryToSetPlantByGlovePatch
        {
            private static void Prefix(Mouse __instance, out (bool on, int col, int row, PlantType fromType, byte owner, PlantType placeType) __state)
            {
                var held = __instance.thePlantOnGlove;
                __state = held == null
                    ? (false, 0, 0, default, 255, default)
                    : (true, held.thePlantColumn, held.thePlantRow, held.thePlantType, PlantSync.OwnerOf(held), __instance.thePlantTypeOnMouse);
                PlantSync.BeginCapture();
            }

            private static void Postfix(Mouse __instance, (bool on, int col, int row, PlantType fromType, byte owner, PlantType placeType) __state)
            {
                var placed = PlantSync.EndCapture();
                if (!__state.on || placed == null || !Session.Ready || PlantSync.Replaying) return;

                byte owner = __state.owner == 255 ? Session.LocalId : __state.owner;
                if (__state.owner != 255) PlantSync.SetOwner(placed, owner);
                int toCol = placed.thePlantColumn, toRow = placed.thePlantRow;
                var frame = BuildMoved(owner, __state.col, __state.row, __state.fromType, toCol, toRow, __state.placeType);
                if (Session.Role == Role.Host) Session.SendToClients(frame);
                else Session.SendToHost(frame);
                FusionCoopMod.Log.Msg($"[glove] P{Session.LocalId + 1} {__state.fromType} c{__state.col} r{__state.row} -> c{toCol} r{toRow} sent");
            }
        }

        // ---------- network ----------

        private static byte[] BuildRemoved(byte owner, int col, int row, PlantType type) =>
            Protocol.Build(Msg.PlantRemoved, w =>
            {
                w.Write(owner);
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)type);
            });

        private static byte[] BuildMoved(byte owner, int fromCol, int fromRow, PlantType fromType, int toCol, int toRow, PlantType placeType) =>
            Protocol.Build(Msg.PlantMoved, w =>
            {
                w.Write(owner);
                w.Write((sbyte)fromCol);
                w.Write((sbyte)fromRow);
                w.Write((int)fromType);
                w.Write((sbyte)toCol);
                w.Write((sbyte)toRow);
                w.Write((int)placeType);
            });

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type != Msg.PlantRemoved && pkt.Type != Msg.PlantMoved) return;
            if (Apply(pkt, from))
                Session.SendToClients(Protocol.Build(pkt.Type, w => w.Write(pkt.Body)), from);
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type == Msg.PlantRemoved || pkt.Type == Msg.PlantMoved) Apply(pkt, 255);
        }

        // Replays a remote shovel/move. `from` is the sending player on the host (checked against
        // the owner), 255 on clients (the host already checked).
        private static bool Apply(Packet pkt, byte from)
        {
            var r = pkt.Reader();
            byte owner = r.ReadByte();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            string who = $"P{owner + 1}";

            var plant = PlantSync.FindPlant(col, row, type);
            if (plant == null)
            {
                FusionCoopMod.Log.Warning($"[{pkt.Type}] {who} {type} c{col} r{row}: no plant here");
                return false;
            }
            byte realOwner = PlantSync.OwnerOf(plant);
            if (from != 255 && realOwner != 255 && realOwner != from)
            {
                FusionCoopMod.Log.Warning($"[{pkt.Type}] P{from + 1} tried to touch P{realOwner + 1}'s {type} c{col} r{row}: refused");
                return false;
            }

            if (pkt.Type == Msg.PlantRemoved)
            {
                PlantSync.Owners.Remove(plant.GetInstanceID());
                PlantSync.Replaying = true;
                try { plant.Die(Plant.DieReason.ByShovel); }
                finally { PlantSync.Replaying = false; }
                FusionCoopMod.Log.Msg($"[shovel] replay {who} {type} c{col} r{row}");
                return true;
            }

            int toCol = r.ReadSByte(), toRow = r.ReadSByte();
            var placeType = (PlantType)r.ReadInt32();
            var moved = PlantSync.Replay(toCol, toRow, placeType, plant);
            if (moved == null)
            {
                FusionCoopMod.Log.Warning($"[glove] replay {who} {type} c{col} r{row} -> c{toCol} r{toRow} FAILED");
                return false;
            }
            if (realOwner != 255) PlantSync.SetOwner(moved, realOwner);
            FusionCoopMod.Log.Msg($"[glove] replay {who} {type} c{col} r{row} -> c{toCol} r{toRow}");
            return true;
        }
    }
}
