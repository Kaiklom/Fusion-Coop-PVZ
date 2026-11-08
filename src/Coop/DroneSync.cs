using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;

namespace FusionCoop.Coop
{
    // Drone Commando (SuperGatlingBlover, Odyssey): every 3 s it looks through the board's plant list for a
    // Commando (SuperSnowGatling) without a wingman, takes the first one (commando.followed = drone,
    // drone.targetPlant = commando), leaves the lawn grid (Plant.RemoveFromList) and flies along with it.
    // The plant list's order differs per window, so drones picked different Commandos. The host picks: clients
    // skip their own search, and the host's choice (the drone's cell, the Commando's cell and type) is replayed.
    internal static class DroneSync
    {
        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;
        private static bool _replaying;

        [HarmonyPatch(typeof(SuperGatlingBlover), "SearchUpdate")]
        internal static class SearchPatch
        {
            private static bool Prefix() => !IsClient;
        }

        // Host: the drone leaves the grid right after it picked its Commando.
        [HarmonyPatch(typeof(Plant), "RemoveFromList")]
        internal static class LeaveGridPatch
        {
            private static void Prefix(Plant __instance)
            {
                if (!IsHost || __instance == null || __instance.targetPlant == null) return;
                if (__instance.TryCast<SuperGatlingBlover>() == null) return;
                var target = __instance.targetPlant;
                int col = __instance.thePlantColumn, row = __instance.thePlantRow;
                var type = __instance.thePlantType;
                int tcol = target.thePlantColumn, trow = target.thePlantRow;
                var ttype = target.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.DroneAttach, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                    w.Write((sbyte)tcol);
                    w.Write((sbyte)trow);
                    w.Write((int)ttype);
                }));
                FusionCoopMod.Log.Msg($"[drone] host's {type} c{col} r{row} follows {ttype} c{tcol} r{trow} -> clients");
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.DroneAttach || Board.Instance == null) return;
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            int tcol = r.ReadSByte(), trow = r.ReadSByte();
            var ttype = (PlantType)r.ReadInt32();
            var drone = PlantSync.FindPlant(col, row, type);
            var target = PlantSync.FindPlant(tcol, trow, ttype);
            var blover = drone != null && drone.thePlantType == type ? drone.TryCast<SuperGatlingBlover>() : null;
            var commando = target != null && target.thePlantType == ttype ? target.TryCast<SuperSnowGatling>() : null;
            if (blover == null || commando == null)
            {
                FusionCoopMod.Log.Warning($"[drone] can't follow the host: {type} c{col} r{row} {(blover != null ? "ok" : "missing")}, " +
                                          $"{ttype} c{tcol} r{trow} {(commando != null ? "ok" : "missing")}");
                return;
            }
            bool was = PlantSync.Replaying;
            _replaying = true;
            PlantSync.Replaying = true;
            try
            {
                commando.followed = blover;
                blover.targetPlant = commando;
                blover.RemoveFromList();
                FusionCoopMod.Log.Msg($"[drone] {type} c{col} r{row} follows {ttype} c{tcol} r{trow} with the host");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[drone] following failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
                PlantSync.Replaying = was;
            }
        }
    }
}
