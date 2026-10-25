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

        // Host: the player whose placement is being replayed (255 outside that).
        internal static byte Requester = 255;

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

        // Client: drop presents left in a cell (their opening is the host's).
        private static void RemovePresents(int col, int row)
        {
            var list = Lawnf.Get1x1Plants(col, row);
            if (list == null) return;
            var presents = new List<Plant>();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null && p.TryCast<Present>() != null) presents.Add(p);
            }
            foreach (var p in presents)
            {
                Owners.Remove(p.GetInstanceID());
                Replaying = true;
                try { p.Die(Plant.DieReason.Default); }
                finally { Replaying = false; }
            }
        }

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
            // A player who leaves hands their plants to the host, so the host can still shovel/move them.
            Session.PlayerLeft += HandOverToHost;
        }

        // Host: a player left the room or the level; their plants become the host's.
        internal static void HandOverToHost(byte id)
        {
            int n = Reassign(id, 0);
            Session.SendToClients(Protocol.Build(Msg.OwnersReassigned, w => { w.Write(id); w.Write((byte)0); }));
            if (n > 0) FusionCoopMod.Log.Msg($"[plant] P{id + 1} left: {n} plants now belong to P1");
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
            CellOwner.Clear();
        }

        // Last player who placed a plant in each cell: plants the game makes there later (presents,
        // fusion results from effects) belong to them.
        private static readonly Dictionary<int, byte> CellOwner = new();
        private static int CellKey(int col, int row) => col * 64 + row;

        internal static void SetOwner(Plant plant, byte owner)
        {
            if (plant == null) return;
            Owners[plant.GetInstanceID()] = new Owned { Plant = plant, Owner = owner };
            CellOwner[CellKey(plant.thePlantColumn, plant.thePlantRow)] = owner;
        }

        private static bool BattleRunning =>
            Board.Instance != null && (InitBoard.Instance == null || InitBoard.Instance.ready);

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
                    ShareGiftSun(type, 0);
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

        // Every SetPlant call. Player actions (_capturing) and replays are handled by their own messages.
        // Anything else during battle is the game making a plant on its own (present opening, plant
        // effects): on the host it is sent to clients with the cell's owner; on clients it is blocked,
        // because random results would differ — the host's result arrives instead.
        // SetPlant calls itself for fusions, so only the outermost call is sent, with the type it was
        // asked for: the client's SetPlant then fuses with the plant in the cell the same way.
        private static int _setPlantDepth;

        [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.SetPlant))]
        internal static class SetPlantCapture
        {
            private static bool Prefix(ref Plant __result)
            {
                _setPlantDepth++;
                if (Session.Role != Role.Client || !Session.Ready || _capturing || Replaying) return true;
                if (!BattleRunning) return true; // level setup before the battle stays local
                __result = null;
                return false;
            }

            private static void Postfix(Plant __result, int newColumn, int newRow, PlantType theSeedType)
            {
                if (__result == null) return;
                if (_capturing)
                {
                    _captured = __result;
                    return;
                }
                if (_setPlantDepth != 1) return;
                if (Replaying || Session.Role != Role.Host || !Session.Ready || !BattleRunning) return;
                int col = newColumn, row = newRow;
                byte owner = CellOwner.TryGetValue(CellKey(col, row), out var o) ? o : (byte)255;
                if (owner != 255) SetOwner(__result, owner);
                var result = __result.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.PlantSpawned, w =>
                {
                    w.Write(owner);
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)theSeedType);
                    w.Write((int)result);
                }));
            }

            private static System.Exception Finalizer(System.Exception __exception)
            {
                _setPlantDepth--;
                return __exception;
            }
        }

        // Clients: a present opens on the host and the result arrives as PlantSpawned. Opening it locally
        // would only fail (SetPlant is blocked) and drop a card instead.
        private static bool PresentOpensLocally() =>
            !(Session.Role == Role.Client && Session.Ready && !Replaying && BattleRunning);

        [HarmonyPatch(typeof(Present), "RandomPlant")]
        internal static class PresentRandomPatch
        {
            private static bool Prefix() => PresentOpensLocally();
        }

        [HarmonyPatch(typeof(Present), nameof(Present.SuperRandomPlant))]
        internal static class PresentSuperRandomPatch
        {
            private static bool Prefix() => PresentOpensLocally();
        }

        [HarmonyPatch(typeof(Present), "HappyRandomPlant")]
        internal static class PresentHappyRandomPatch
        {
            private static bool Prefix() => PresentOpensLocally();
        }

        // Some fusions unlock by winning a Challenge level (CreatePlant.Lim, read by MixFail). A fusion
        // another player made is replayed even if this machine's save hasn't unlocked it, so the boards match.
        [HarmonyPatch(typeof(CreatePlant), "Lim")]
        internal static class MixLimitPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!Replaying || !Session.Ready) return true;
                __result = false;
                return false;
            }
        }

        // ---------- plant health ----------

        private static double _nextHealthSend;

        // Host, 5 Hz: health of every plant on the board.
        private static int _boardId;

        internal static void Update()
        {
            // New board (level/restart): the cell owners of the old one no longer apply.
            int bid = Board.Instance != null ? Board.Instance.GetInstanceID() : 0;
            if (bid != _boardId)
            {
                _boardId = bid;
                CellOwner.Clear();
            }

            if (Session.Role != Role.Host || !Session.Ready || Session.Names.Count < 2) return;
            var board = Board.Instance;
            if (board == null || board.boardEntity == null || Session.Now < _nextHealthSend) return;
            _nextHealthSend = Session.Now + 0.2;
            var list = board.boardEntity.plantArray;
            if (list == null) return;
            var plants = new List<Plant>();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null) plants.Add(p);
            }
            Session.SendToClients(Protocol.Build(Msg.PlantState, w =>
            {
                w.Write((ushort)plants.Count);
                foreach (var p in plants)
                {
                    w.Write((sbyte)p.thePlantColumn);
                    w.Write((sbyte)p.thePlantRow);
                    w.Write((int)p.thePlantType);
                    w.Write(p.thePlantHealth);
                    w.Write(p.thePlantMaxHealth);
                    w.Write(p.theShieldHealth);
                }
            }));
        }

        // Client: damage through the game's TakeDamage (hit flash, effects), copy the host's numbers, then
        // redraw the damage look. Wall-nut-like plants pick their bitten sprite from health/maxHealth in
        // ReplaceSprite, which their own TakeDamage calls but the base Plant.TakeDamage we reach does not.
        private static void ApplyPlantState(System.IO.BinaryReader r)
        {
            int count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                int col = r.ReadSByte(), row = r.ReadSByte();
                var type = (PlantType)r.ReadInt32();
                int health = r.ReadInt32(), maxHealth = r.ReadInt32(), shield = r.ReadInt32();
                var p = FindPlant(col, row, type);
                if (p == null || p.thePlantType != type) continue;
                int local = p.thePlantHealth + p.theShieldHealth;
                int host = health + shield;
                bool changed = host != local || p.thePlantMaxHealth != maxHealth;
                if (host < local && host > 0)
                {
                    int dmg = local - host;
                    Replaying = true;
                    try { p.TakeDamage(dmg, null, DamageType.Normal, (PlantType)(-1), false); }
                    catch (System.Exception e) { FusionCoopMod.Log.Warning($"[plant] damage replay failed: {e.Message}"); }
                    finally { Replaying = false; }
                }
                if (p == null) continue;
                p.thePlantMaxHealth = maxHealth;
                p.thePlantHealth = health;
                p.theShieldHealth = shield;
                if (!changed) continue;
                try { p.ReplaceSprite(); }
                catch (System.Exception e) { FusionCoopMod.Log.Warning($"[plant] sprite update failed: {e.Message}"); }
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
        // free = isFreeSet, which skips the game's own checks (for moves the host already accepted).
        internal static Plant Replay(int col, int row, PlantType type, Plant target = null, bool free = false)
        {
            var cp = CreatePlant.Instance;
            if (cp == null || Board.Instance == null) return null;
            Replaying = true;
            try
            {
                return cp.SetPlant(col, row, type, target, default, free, true, null);
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

            Requester = from;
            Plant plant;
            try
            {
                plant = Replay(col, row, type);
            }
            finally
            {
                Requester = 255;
            }
            bool ok = plant != null;
            Session.SendToPlayer(from, Protocol.Build(Msg.PlantResult, w => { w.Write(seq); w.Write(ok); }));
            FusionCoopMod.Log.Msg($"[plant] P{from + 1} #{seq} {type} c{col} r{row} -> {(ok ? "accepted" : "rejected")}");
            if (!ok) return;
            SetOwner(plant, from);
            Session.SendToClients(BuildDone(from, col, row, type), from);
            ShareGiftSun(type, from);
        }

        // Zombie giftbox (PresentZombie): its card costs -100, so the player who places it gets 100 sun. The
        // zombie it lets out comes for everyone, so everyone else gets the 100 too (user's choice).
        private const int GiftSun = 100;

        private static void ShareGiftSun(PlantType type, byte placer)
        {
            if (type != PlantType.PresentZombie) return;
            Session.SendToClients(Protocol.Build(Msg.GiftboxSun, w => w.Write(placer)));
            if (placer != Session.LocalId && Board.Instance != null) Board.Instance.GetSun(GiftSun, false);
            FusionCoopMod.Log.Msg($"[plant] P{placer + 1} placed a zombie giftbox: +{GiftSun} sun for everyone else");
        }

        private static void OnClientMessage(Packet pkt)
        {
            var r = pkt.Reader();
            switch (pkt.Type)
            {
                case Msg.GiftboxSun:
                {
                    byte placer = r.ReadByte();
                    if (placer == Session.LocalId || Board.Instance == null) break;
                    Board.Instance.GetSun(GiftSun, false);
                    FusionCoopMod.Log.Msg($"[plant] P{placer + 1} placed a zombie giftbox: +{GiftSun} sun");
                    break;
                }
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
                case Msg.PlantSpawned:
                {
                    byte owner = r.ReadByte();
                    int col = r.ReadSByte(), row = r.ReadSByte();
                    var asked = (PlantType)r.ReadInt32();
                    var result = (PlantType)r.ReadInt32();
                    var plant = FindPlant(col, row, result);
                    if (plant == null || plant.thePlantType != result)
                    {
                        // The opened present must not be in the cell when its result is planted.
                        RemovePresents(col, row);
                        plant = Replay(col, row, asked);
                        if (plant == null && asked != result) plant = Replay(col, row, result);
                    }
                    if (plant != null && owner != 255) SetOwner(plant, owner);
                    FusionCoopMod.Log.Msg($"[plant] host made {asked}->{result} c{col} r{row} -> {(plant != null ? plant.thePlantType.ToString() : "FAILED")}");
                    break;
                }
                case Msg.PlantState:
                    ApplyPlantState(r);
                    break;
                case Msg.PlantDied:
                {
                    int col = r.ReadSByte(), row = r.ReadSByte();
                    var type = (PlantType)r.ReadInt32();
                    var reason = (Plant.DieReason)r.ReadInt32();
                    var plant = FindPlant(col, row, type);
                    // Exact type only: after a fusion the cell holds a different plant that must live.
                    if (plant == null || plant.thePlantType != type) break;
                    Owners.Remove(plant.GetInstanceID());
                    Replaying = true;
                    try
                    {
                        // Run over or smashed: the game's own flattening, which then kills the plant.
                        if (reason == Plant.DieReason.Crash || reason == Plant.DieReason.CrashInWater)
                        {
                            plant.Crashed(0, 0, null);
                            if (plant != null && plant.thePlantHealth > 0 && !plant.isCrashed) plant.Die(reason);
                        }
                        else plant.Die(reason);
                    }
                    catch (System.Exception e)
                    {
                        FusionCoopMod.Log.Warning($"[plant] death replay failed: {e.Message}");
                    }
                    finally { Replaying = false; }
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

        // ---------- deaths decided by the host ----------

        // Client zombies are puppets and never eat, so plant deaths come from the host.
        // Deaths handled elsewhere (fusion, level-up, shovel, hiding) are not sent.
        private static bool SendsDeath(Plant.DieReason reason) =>
            reason != Plant.DieReason.ByMix && reason != Plant.DieReason.ByDisMix &&
            reason != Plant.DieReason.ByLevelUp && reason != Plant.DieReason.ByShovel &&
            reason != Plant.DieReason.Hid;

        // Deaths zombies cause by reaching a plant without eating it (Gargantuar smash, vehicles, ...).
        private static bool ZombieCaused(Plant.DieReason reason) =>
            reason == Plant.DieReason.Crash || reason == Plant.DieReason.CrashInWater ||
            reason == Plant.DieReason.Wheel || reason == Plant.DieReason.BySteal ||
            reason == Plant.DieReason.ByFreeze;

        // Client during battle: plants only lose health or die from zombies when the host says so.
        // Puppet zombies still play the host's animations, and some of those hurt plants on their own
        // (Gargantuar smash, catapult balls, vehicles running over plants); those hits are dropped here.
        // Player actions (shovel, fusion, glove, refunds) and plants using themselves up stay local.
        private static bool ClientGuard => Session.Role == Role.Client && Session.Ready && !Replaying && BattleRunning;

        [HarmonyPatch(typeof(Plant), nameof(Plant.TakeDamage))]
        internal static class ClientPlantDamagePatch
        {
            private static bool Prefix() => !ClientGuard;
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.Crashed))]
        internal static class ClientPlantCrashedPatch
        {
            private static bool Prefix() => !ClientGuard;
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.Die))]
        internal static class PlantDiePatch
        {
            private static bool Prefix(Plant __instance, Plant.DieReason reason)
            {
                if (ClientGuard && ZombieCaused(reason)) return false;
                if (Session.Role != Role.Host || !Session.Ready || Replaying || __instance == null) return true;
                if (!SendsDeath(reason) || Board.Instance == null) return true;
                int col = __instance.thePlantColumn, row = __instance.thePlantRow;
                var type = __instance.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.PlantDied, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                    w.Write((int)reason);
                }));
                return true;
            }
        }

        // ---------- labels ----------

        private static readonly List<int> Dead = new();

        // Forget plants that are gone.
        internal static void PruneOwners()
        {
            Dead.Clear();
            foreach (var kv in Owners)
                if (kv.Value.Plant == null) Dead.Add(kv.Key);
            foreach (var id in Dead) Owners.Remove(id);
        }
    }
}
