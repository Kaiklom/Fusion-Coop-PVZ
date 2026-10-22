using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Graves (night levels) are decided by the host.
    //
    // The game places graves at random (Board.SetGrave at the start, SupplyGrave on waves, all through
    // GridItem.SetGridItem), and a grave summons by Grave.SetZombie: a coroutine picks a random zombie
    // type, raises a preview of it out of the ground (CreateZombie.CreateZombiePreview), then makes
    // the real zombie. Clients:
    // - never make graves themselves; the host sends its grave list every second and they match it,
    // - don't damage or kill graves; the host sends each grave death and they play it,
    // - don't summon on their own; the host sends each summon with the zombie type, the client plays
    //   the rise with that type, and the real zombie arrives through the normal zombie sync.
    internal static class GraveSync
    {
        private static bool _replaying;
        private static Grave _summoning;           // host: grave whose SetZombie is running
        private static int _summonType = -1;       // host: preview type it picked
        private static int _replayType = -1;       // client: type to raise in a replayed summon
        private static double _nextState;

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;
        private static bool ClientBlocks => IsClient && !_replaying;

        // ---------- placement ----------

        [HarmonyPatch(typeof(GridItem), nameof(GridItem.SetGridItem))]
        internal static class SetGridItemPatch
        {
            // SetGridItem picks one of the grave prefabs at random: seed it from the cell on every
            // window so the host's grave and the client's copy look the same.
            private static bool Prefix(int theColumn, int theRow, GridItemType theType, out Random.State? __state)
            {
                __state = null;
                if (theType != GridItemType.Grave) return true;
                if (ClientBlocks) return false;
                __state = Random.state;
                Random.InitState(unchecked((int)((uint)(theColumn * 31 + theRow + 1) * 2654435761u)));
                return true;
            }

            private static System.Exception Finalizer(System.Exception __exception, Random.State? __state)
            {
                if (__state.HasValue) Random.state = __state.Value;
                return __exception;
            }
        }

        internal static void Update()
        {
            if (!IsHost || Board.Instance == null || Session.Now < _nextState) return;
            _nextState = Session.Now + 1;
            var graves = LiveGraves();
            Session.SendToClients(Protocol.Build(Msg.GraveState, w =>
            {
                w.Write((ushort)graves.Count);
                foreach (var g in graves)
                {
                    w.Write((sbyte)g.theItemColumn);
                    w.Write((sbyte)g.theItemRow);
                    w.Write((int)g.graveType);
                }
            }));
        }

        private static List<Grave> LiveGraves()
        {
            var list = new List<Grave>();
            var items = Board.Instance.griditemArray;
            if (items == null) return list;
            foreach (var item in items)
            {
                if (item == null || item.theItemType != GridItemType.Grave) continue;
                var g = item.TryCast<Grave>();
                if (g != null && !g.dying) list.Add(g);
            }
            return list;
        }

        private static Grave FindGrave(int col, int row)
        {
            var items = Board.Instance?.griditemArray;
            if (items == null) return null;
            foreach (var item in items)
            {
                if (item == null || item.theItemType != GridItemType.Grave) continue;
                if (item.theItemColumn != col || item.theItemRow != row) continue;
                var g = item.TryCast<Grave>();
                if (g != null && !g.dying) return g;
            }
            return null;
        }

        private static Grave MakeGrave(int col, int row, int graveType)
        {
            GridItem item = null;
            _replaying = true;
            try
            {
                item = GridItem.SetGridItem(col, row, GridItemType.Grave, (GraveType)graveType);
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[grave] place c{col} r{row} failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
            return item != null ? item.TryCast<Grave>() : null;
        }

        private static void ApplyState(System.IO.BinaryReader r)
        {
            int count = r.ReadUInt16();
            var want = new HashSet<(int, int)>();
            for (int i = 0; i < count; i++)
            {
                int col = r.ReadSByte(), row = r.ReadSByte(), type = r.ReadInt32();
                want.Add((col, row));
                if (FindGrave(col, row) == null) MakeGrave(col, row, type);
            }
            // Graves the host no longer has (a missed death message, or left from before the sync).
            foreach (var g in LiveGraves())
                if (!want.Contains((g.theItemColumn, g.theItemRow))) KillGrave(g);
        }

        // ---------- damage and death ----------

        [HarmonyPatch(typeof(Grave), nameof(Grave.TakeDamage))]
        internal static class TakeDamagePatch
        {
            private static bool Prefix() => !ClientBlocks;
        }

        [HarmonyPatch(typeof(Grave), nameof(Grave.Die))]
        internal static class DiePatch
        {
            private static bool Prefix(Grave __instance, out bool __state)
            {
                __state = __instance.dying;
                return !ClientBlocks;
            }

            private static void Postfix(Grave __instance, bool __state)
            {
                if (__state || !IsHost) return;
                int col = __instance.theItemColumn, row = __instance.theItemRow;
                Session.SendToClients(Protocol.Build(Msg.GraveDied, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                }));
            }
        }

        private static void KillGrave(Grave g)
        {
            _replaying = true;
            try
            {
                g.Die();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[grave] die failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }

        // ---------- summons ----------

        [HarmonyPatch(typeof(Grave), nameof(Grave.SetZombie))]
        internal static class SetZombiePatch
        {
            private static bool Prefix(Grave __instance, out Grave __state)
            {
                __state = _summoning;
                if (ClientBlocks) return false;
                if (IsHost)
                {
                    _summoning = __instance;
                    _summonType = -1;
                }
                return true;
            }

            private static System.Exception Finalizer(System.Exception __exception, Grave __instance, Grave __state)
            {
                if (IsHost && _summoning != null && _summonType >= 0)
                {
                    int col = __instance.theItemColumn, row = __instance.theItemRow;
                    int graveType = (int)__instance.graveType, type = _summonType;
                    Session.SendToClients(Protocol.Build(Msg.GraveSummon, w =>
                    {
                        w.Write((sbyte)col);
                        w.Write((sbyte)row);
                        w.Write(graveType);
                        w.Write(type);
                    }));
                }
                _summoning = __state;
                _summonType = -1;
                return __exception;
            }
        }

        // The summon coroutine's first step runs inside Grave.SetZombie and makes the rising preview.
        [HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.CreateZombiePreview))]
        internal static class PreviewPatch
        {
            private static void Prefix(ref ZombieType theZombieType)
            {
                if (_summoning != null) _summonType = (int)theZombieType;
                else if (_replayType >= 0) theZombieType = (ZombieType)_replayType;
            }
        }

        private static void ReplaySummon(int col, int row, int graveType, int type)
        {
            var g = FindGrave(col, row) ?? MakeGrave(col, row, graveType);
            if (g == null)
            {
                FusionCoopMod.Log.Warning($"[grave] no grave at c{col} r{row} to summon from");
                return;
            }
            _replaying = true;
            _replayType = type;
            try
            {
                g.SetZombie();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[grave] summon failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
                _replayType = -1;
            }
        }

        // ---------- client ----------

        private static void OnClientMessage(Packet pkt)
        {
            if (Board.Instance == null) return;
            switch (pkt.Type)
            {
                case Msg.GraveState:
                    ApplyState(pkt.Reader());
                    break;
                case Msg.GraveDied:
                {
                    var r = pkt.Reader();
                    int col = r.ReadSByte(), row = r.ReadSByte();
                    var g = FindGrave(col, row);
                    if (g != null) KillGrave(g);
                    break;
                }
                case Msg.GraveSummon:
                {
                    var r = pkt.Reader();
                    int col = r.ReadSByte(), row = r.ReadSByte(), graveType = r.ReadInt32(), type = r.ReadInt32();
                    ReplaySummon(col, row, graveType, type);
                    break;
                }
            }
        }
    }
}
