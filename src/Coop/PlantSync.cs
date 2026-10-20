using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Player planting across machines.
    //
    // The planting player's own game runs Mouse.TryToSetPlantByCard as usual (cost, x2 duplicate cost,
    // cooldown, fusion), so it feels instant and each window's sun counter is that player's wallet.
    // - Host plants: broadcast PlantDone, clients replay with CreatePlant.SetPlant.
    // - Client plants: send PlantRequest; the host replays it. SetPlant returns null when the cell
    //   can't take the plant, then the host answers PlantResult(false) and the client undoes it.
    internal static class PlantSync
    {
        internal sealed class Owned
        {
            internal Plant Plant;
            internal byte Owner;
        }

        // InstanceID -> owner, for the P1/P2 labels.
        internal static readonly Dictionary<int, Owned> Owners = new();

        private sealed class Pending
        {
            internal Plant Plant;
            internal int SunPaid;
        }

        private static readonly Dictionary<uint, Pending> PendingRequests = new();
        private static uint _nextSeq = 1;

        // Set while the local player's TryToSetPlantByCard runs, so SetPlant can report what it made.
        private static bool _capturing;
        private static Plant _captured;
        // Set while we replay a remote action, so nothing is re-sent.
        internal static bool Replaying;

        internal static void BeginCapture()
        {
            _capturing = true;
            _captured = null;
        }

        internal static Plant EndCapture()
        {
            _capturing = false;
            var p = _captured;
            _captured = null;
            return p;
        }

        // Owner of a plant, or 255 when no player placed it (level setup, plant effects).
        internal static byte OwnerOf(Plant plant) =>
            plant != null && Owners.TryGetValue(plant.GetInstanceID(), out var o) ? o.Owner : (byte)255;

        // A plant at (col,row), preferring the given type.
        internal static Plant FindPlant(int col, int row, PlantType type)
        {
            var list = Lawnf.Get1x1Plants(col, row);
            if (list == null) return null;
            Plant any = null;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                if (p.thePlantType == type) return p;
                any ??= p;
            }
            return any;
        }

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
            // A player who leaves hands their plants to the host, so the host can still shovel/move them.
            Session.PlayerLeft += id =>
            {
                int n = Reassign(id, 0);
                Session.SendToClients(Protocol.Build(Msg.OwnersReassigned, w => { w.Write(id); w.Write((byte)0); }));
                if (n > 0) FusionCoopMod.Log.Msg($"[plant] P{id + 1} left: {n} plants now belong to P1");
            };
        }

        private static int Reassign(byte from, byte to)
        {
            int n = 0;
            foreach (var o in Owners.Values)
            {
                if (o.Owner != from) continue;
                o.Owner = to;
                n++;
            }
            return n;
        }

        // Host: every live player-placed plant, for a client whose battle started late.
        private static byte[] BuildSnapshot()
        {
            var live = new List<Owned>();
            foreach (var o in Owners.Values)
                if (o.Plant != null) live.Add(o);
            return Protocol.Build(Msg.PlantSnapshot, w =>
            {
                w.Write((ushort)live.Count);
                foreach (var o in live)
                {
                    w.Write(o.Owner);
                    w.Write((sbyte)o.Plant.thePlantColumn);
                    w.Write((sbyte)o.Plant.thePlantRow);
                    w.Write((int)o.Plant.thePlantType);
                }
            });
        }

        internal static void Reset()
        {
            Owners.Clear();
            PendingRequests.Clear();
        }

        internal static void SetOwner(Plant plant, byte owner)
        {
            if (plant == null) return;
            Owners[plant.GetInstanceID()] = new Owned { Plant = plant, Owner = owner };
        }

        // ---------- local planting ----------

        [HarmonyPatch(typeof(Mouse), "TryToSetPlantByCard")]
        internal static class TryToSetPlantByCardPatch
        {
            private static void Prefix(Mouse __instance, out (PlantType type, int col, int row, int sun) __state)
            {
                int sun = Board.Instance != null ? Board.Instance.theSun : 0;
                __state = (__instance.thePlantTypeOnMouse, __instance.theMouseColumn, __instance.theMouseRow, sun);
                BeginCapture();
            }

            private static void Postfix((PlantType type, int col, int row, int sun) __state)
            {
                var plant = EndCapture();
                if (plant == null || Replaying || !Session.Ready) return;

                int paid = Board.Instance != null ? __state.sun - Board.Instance.theSun : 0;
                var (type, col, row, _) = __state;

                if (Session.Role == Role.Host)
                {
                    SetOwner(plant, 0);
                    Session.SendToClients(BuildDone(0, col, row, type));
                    FusionCoopMod.Log.Msg($"[plant] P1 {type} c{col} r{row} -> clients");
                }
                else
                {
                    uint seq = _nextSeq++;
                    PendingRequests[seq] = new Pending { Plant = plant, SunPaid = paid };
                    SetOwner(plant, Session.LocalId);
                    Session.SendToHost(Protocol.Build(Msg.PlantRequest, w =>
                    {
                        w.Write(seq);
                        w.Write((sbyte)col);
                        w.Write((sbyte)row);
                        w.Write((int)type);
                    }));
                    FusionCoopMod.Log.Msg($"[plant] request #{seq} {type} c{col} r{row} paid {paid}");
                }
            }
        }

        [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.SetPlant))]
        internal static class SetPlantCapture
        {
            private static void Postfix(Plant __result)
            {
                if (_capturing && __result != null) _captured = __result;
            }
        }

        // ---------- network ----------

        private static byte[] BuildDone(byte owner, int col, int row, PlantType type) =>
            Protocol.Build(Msg.PlantDone, w =>
            {
                w.Write(owner);
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)type);
            });

        // Same call the game makes: plain planting, or a glove move when target is the plant being moved.
        internal static Plant Replay(int col, int row, PlantType type, Plant target = null)
        {
            var cp = CreatePlant.Instance;
            if (cp == null || Board.Instance == null) return null;
            Replaying = true;
            try
            {
                return cp.SetPlant(col, row, type, target, default, false, true, null);
            }
            finally
            {
                Replaying = false;
            }
        }

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type == Msg.SnapshotRequest)
            {
                var snap = BuildSnapshot();
                Session.SendToPlayer(from, snap);
                FusionCoopMod.Log.Msg($"[plant] sent plant snapshot to P{from + 1}");
                return;
            }
            if (pkt.Type != Msg.PlantRequest) return;
            var r = pkt.Reader();
            uint seq = r.ReadUInt32();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();

            var plant = Replay(col, row, type);
            bool ok = plant != null;
            Session.SendToPlayer(from, Protocol.Build(Msg.PlantResult, w => { w.Write(seq); w.Write(ok); }));
            FusionCoopMod.Log.Msg($"[plant] P{from + 1} #{seq} {type} c{col} r{row} -> {(ok ? "accepted" : "rejected")}");
            if (!ok) return;
            SetOwner(plant, from);
            Session.SendToClients(BuildDone(from, col, row, type), from);
        }

        private static void OnClientMessage(Packet pkt)
        {
            var r = pkt.Reader();
            switch (pkt.Type)
            {
                case Msg.PlantResult:
                {
                    uint seq = r.ReadUInt32();
                    bool ok = r.ReadBoolean();
                    if (!PendingRequests.Remove(seq, out var pending) || ok) return;
                    // Host could not place it: take ours back and refund.
                    if (pending.Plant != null)
                    {
                        Owners.Remove(pending.Plant.GetInstanceID());
                        pending.Plant.Die(Plant.DieReason.Default);
                    }
                    if (pending.SunPaid > 0 && Board.Instance != null) Board.Instance.GetSun(pending.SunPaid, false);
                    FusionCoopMod.Log.Msg($"[plant] request #{seq} rejected by host, refunded {pending.SunPaid}");
                    break;
                }
                case Msg.PlantDone:
                {
                    byte owner = r.ReadByte();
                    int col = r.ReadSByte(), row = r.ReadSByte();
                    var type = (PlantType)r.ReadInt32();
                    var plant = Replay(col, row, type);
                    SetOwner(plant, owner);
                    FusionCoopMod.Log.Msg($"[plant] replay P{owner + 1} {type} c{col} r{row} -> {(plant != null ? "ok" : "FAILED")}");
                    break;
                }
                case Msg.OwnersReassigned:
                {
                    byte from = r.ReadByte(), to = r.ReadByte();
                    Reassign(from, to);
                    break;
                }
                case Msg.PlantSnapshot:
                {
                    int count = r.ReadUInt16(), made = 0;
                    for (int i = 0; i < count; i++)
                    {
                        byte owner = r.ReadByte();
                        int col = r.ReadSByte(), row = r.ReadSByte();
                        var type = (PlantType)r.ReadInt32();
                        // Skip plants we already have (replayed after our start), planting again would fuse them.
                        var existing = FindPlant(col, row, type);
                        if (existing != null && existing.thePlantType == type)
                        {
                            SetOwner(existing, owner);
                            continue;
                        }
                        var plant = Replay(col, row, type);
                        if (plant == null) continue;
                        SetOwner(plant, owner);
                        made++;
                    }
                    FusionCoopMod.Log.Msg($"[plant] snapshot: {made}/{count} plants placed");
                    break;
                }
            }
        }

        // Client: ask for the host's plants once our battle is running after a late start.
        internal static void RequestSnapshot() => Session.SendToHost(Protocol.Build(Msg.SnapshotRequest));

        // ---------- labels ----------

        private static readonly List<int> Dead = new();

        internal static void DrawLabels(Camera cam, Color[] colors)
        {
            Dead.Clear();
            foreach (var kv in Owners)
            {
                var p = kv.Value.Plant;
                if (p == null)
                {
                    Dead.Add(kv.Key);
                    continue;
                }
                var sp = cam.WorldToScreenPoint(p.transform.position);
                float gx = sp.x, gy = Screen.height - sp.y;
                var old = GUI.color;
                GUI.color = colors[kv.Value.Owner % colors.Length];
                GUI.Label(new Rect(gx - 12, gy - 6, 40, 22), $"P{kv.Value.Owner + 1}");
                GUI.color = old;
            }
            foreach (var id in Dead) Owners.Remove(id);
        }
    }
}
