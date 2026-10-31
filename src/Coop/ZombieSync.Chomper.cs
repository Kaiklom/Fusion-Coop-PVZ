using System.Collections.Generic;
using System.Reflection;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Chompers bite and chew when the host's chomper does.
    //
    // Chomper.ChomperSearchZombie (each FixedUpdate while idle, and from its range trigger) picks a zombie
    // in reach, sets targetZombie, the "bite" trigger and the Chomper_bite status; the bite animation's
    // event (BiteEvent) then eats it (Chomp: Die, chew countdown) or, for a gargantuar, hurts it and goes
    // back. Chewing ends when the plant's attribute countdown runs out. Every window searched and counted
    // on its own, so a client's chomper could chew while the host's stood still, or still chew (a hitch
    // loses game time) when the host's had swallowed and bitten the next zombie.
    // - Host: after a search that picked a zombie, sends the plant, its animator state before the bite,
    //   the zombie's network id, the status and the triggers it set.
    // - Client: doesn't search; on the host's bite it first leaves whatever it is still doing (a chew the
    //   host has finished), then targets that puppet and sets the same triggers, so its own BiteEvent eats
    //   (or goes back) in step. The zombie's death still comes from the host; if it arrives before the
    //   client's bite lands, it waits for that bite (at most BiteWait) so the bite still finds it alive.
    // - Correction: twice a second the host sends every chomper's status, animator state and chew
    //   countdown. A client chomper chewing with the host takes the host's countdown; one that differs in
    //   two snapshots in a row (a missed event, a rejoin mid-chew) is set to the host's state.
    //
    // Not hooked: AttributeEvent (the countdown's end) - the game calls it every frame while the countdown
    // is out, and its code is shared with other plants.
    internal static partial class ZombieSync
    {
        private const double BiteWait = 1.5;

        private static Plant _searching; // host: the chomper whose search is running
        private static readonly List<string> SearchTriggers = new();

        // Client: zombie pointer -> the chomper the host made bite it.
        private static readonly Dictionary<System.IntPtr, Plant> BiteTargets = new();
        // Client: chompers the host made bite whose BiteEvent hasn't run yet.
        private static readonly HashSet<System.IntPtr> BitePending = new();

        private sealed class Eaten
        {
            internal Remote Rz;
            internal int Reason;
            internal double Until;
        }

        // Client: zombies the host says died while this window's chomper is still in its bite.
        private static readonly Dictionary<System.IntPtr, Eaten> WaitingBite = new();

        private static bool ClientBattle =>
            IsClient && Board.Instance != null && (InitBoard.Instance == null || InitBoard.Instance.ready);

        private static void ResetChompers()
        {
            _searching = null;
            BiteTargets.Clear();
            BitePending.Clear();
            WaitingBite.Clear();
            ChomperMismatch.Clear();
        }

        // ---------- bite ----------

        [HarmonyPatch]
        internal static class ChomperSearchPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.DeclaredMethod(typeof(Chomper), nameof(Chomper.ChomperSearchZombie));
                yield return AccessTools.DeclaredMethod(typeof(SuperChomper), nameof(SuperChomper.ChomperSearchZombie));
            }

            private static bool Prefix(Plant __instance, out (Plant outer, System.IntPtr target, PlantStatus status, int anim) __state)
            {
                __state = (_searching, System.IntPtr.Zero, PlantStatus.Default, 0);
                if (!Session.Ready || __instance == null) return true;
                if (ClientBattle) return false;
                if (Session.Role != Role.Host) return true;
                var a = __instance.anim;
                __state = (_searching, PtrOf(__instance.targetZombie), __instance.theStatus,
                    a != null ? a.GetCurrentAnimatorStateInfo(0).fullPathHash : 0);
                _searching = __instance;
                SearchTriggers.Clear();
                return true;
            }

            private static System.Exception Finalizer(System.Exception __exception, Plant __instance,
                (Plant outer, System.IntPtr target, PlantStatus status, int anim) __state)
            {
                if (_searching == null) return __exception;
                if (__exception == null && __instance != null && _searching.Pointer == __instance.Pointer)
                {
                    var z = __instance.targetZombie;
                    if (z != null && (z.Pointer != __state.target || __instance.theStatus != __state.status) &&
                        HostIds.TryGetValue(z.GetInstanceID(), out uint id))
                    {
                        SendChomperBite(__instance, __state.anim, id);
                        ChompLog($"host {Where(__instance)} bites #{id}");
                    }
                }
                _searching = __state.outer;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), typeof(string))]
        internal static class ChomperTriggerPatch
        {
            private static void Prefix(Animator __instance, string name)
            {
                var p = _searching;
                if (p == null || p.anim == null || p.anim.Pointer != __instance.Pointer) return;
                SearchTriggers.Add(name);
            }
        }

        private static System.IntPtr PtrOf(Zombie z) => z == null ? System.IntPtr.Zero : z.Pointer;

        private static void SendChomperBite(Plant p, int animBefore, uint zombieId)
        {
            int col = p.thePlantColumn, row = p.thePlantRow;
            var type = p.thePlantType;
            int status = (int)p.theStatus;
            var triggers = SearchTriggers.ToArray();
            Session.SendToClients(Protocol.Build(Msg.ChomperBite, w =>
            {
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)type);
                w.Write(animBefore);
                w.Write(zombieId);
                w.Write(status);
                w.Write((byte)triggers.Length);
                foreach (var t in triggers) w.Write(t ?? "");
            }));
        }

        private static void ReplayChomperBite(Packet pkt)
        {
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            int animBefore = r.ReadInt32();
            uint id = r.ReadUInt32();
            var status = (PlantStatus)r.ReadInt32();
            int n = r.ReadByte();
            var triggers = new string[n];
            for (int i = 0; i < n; i++) triggers[i] = r.ReadString();

            if (Board.Instance == null) return;
            var p = PlantSync.FindPlant(col, row, type);
            if (p == null || p.thePlantType != type || p.anim == null) return;
            if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null)
            {
                ChompLog($"client: {Where(p)} - zombie #{id} not here");
                return;
            }
            var anim = p.anim;
            var z = rz.Zombie;
            // Still chewing or swallowing what the host has finished: leave it, so the bite starts now.
            bool caughtUp = animBefore != 0 && anim.GetCurrentAnimatorStateInfo(0).fullPathHash != animBefore &&
                            anim.HasState(0, animBefore);
            if (caughtUp) anim.Play(animBefore, 0, 0f);
            ChompLog($"client: {Where(p)} bites #{id}{(caughtUp ? " (caught up)" : "")}{(rz.Killed ? " (already killed)" : "")}");
            p.targetZombie = z;
            p.theStatus = status;
            foreach (var t in triggers)
                if (t.Length > 0) anim.SetTrigger(t);
            BiteTargets[z.Pointer] = p;
            BitePending.Add(p.Pointer);
        }

        // Client: the bite landed (it ate the zombie, or went back without it).
        [HarmonyPatch]
        internal static class ChomperBiteEventPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(Chomper), typeof(BigChomper), typeof(DoomChomper), typeof(HypnoChomper),
                             typeof(IceChomper), typeof(SeaChomper), typeof(SuperChomper), typeof(UltimateChomper) })
                {
                    var m = AccessTools.DeclaredMethod(type, "BiteEvent");
                    if (m != null) yield return m;
                    else FusionCoopMod.Log.Warning($"[chomper] {type.Name}.BiteEvent not found");
                }
            }

            private static void Postfix(Plant __instance)
            {
                if (BitePending.Count > 0 && __instance != null) BitePending.Remove(__instance.Pointer);
            }
        }

        // Client, from Kill: the host's zombie died, but this window's chomper hasn't landed the bite the
        // host made it start. Keep the zombie (still a puppet) until the bite eats it.
        private static bool WaitForBite(Remote rz, int reason)
        {
            var z = rz.Zombie;
            if (!BiteTargets.TryGetValue(z.Pointer, out var p)) return false;
            if (p == null || !BitePending.Contains(p.Pointer))
            {
                BiteTargets.Remove(z.Pointer);
                return false;
            }
            WaitingBite[z.Pointer] = new Eaten { Rz = rz, Reason = reason, Until = Session.Now + BiteWait };
            return true;
        }

        // Client, from Zombie.Die: a waiting zombie may die when the chomper's own bite kills it.
        private static bool BiteMayKill(Zombie z) => WaitingBite.Count > 0 && z != null && WaitingBite.ContainsKey(z.Pointer);

        private static void BiteKilled(Zombie z)
        {
            if (z == null) return;
            WaitingBite.Remove(z.Pointer);
            BiteTargets.Remove(z.Pointer);
            Puppets.Remove(z.GetInstanceID());
        }

        // Client: zombies whose chomper bite landed without eating them (or took too long) die the usual way.
        private static readonly List<System.IntPtr> Expired = new();

        private static void CheckWaitingBites()
        {
            if (WaitingBite.Count == 0) return;
            Expired.Clear();
            foreach (var kv in WaitingBite)
            {
                bool done = kv.Value.Rz.Zombie == null || Session.Now >= kv.Value.Until;
                // The bite landed without eating it (a gargantuar, a SuperChomper's damage bite, ...).
                if (!done && BiteTargets.TryGetValue(kv.Key, out var p))
                    done = p == null || !BitePending.Contains(p.Pointer);
                if (done) Expired.Add(kv.Key);
            }
            foreach (var ptr in Expired)
            {
                var e = WaitingBite[ptr];
                WaitingBite.Remove(ptr);
                BiteTargets.Remove(ptr);
                if (e.Rz.Zombie != null) KillNow(e.Rz.Zombie, e.Reason);
            }
        }

        // ---------- correction ----------

        private const float ChomperStateInterval = 0.5f;
        private const float CountdownSlack = 0.5f; // seconds a client's chew countdown may differ from the host's
        private static double _nextChomperState;
        private static readonly List<Plant> StateChompers = new();
        // Client: plant pointer -> snapshots in a row its chomper differed from the host's
        private static readonly Dictionary<System.IntPtr, int> ChomperMismatch = new();

        private static void SendChomperStates()
        {
            var board = Board.Instance;
            if (board == null || !Session.Ready || Session.Names.Count < 2 || Session.Now < _nextChomperState) return;
            _nextChomperState = Session.Now + ChomperStateInterval;
            var list = board.boardEntity != null ? board.boardEntity.plantArray : null;
            if (list == null) return;
            StateChompers.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null && p.anim != null && p.TryCast<Chomper>() != null) StateChompers.Add(p);
            }
            if (StateChompers.Count == 0) return;
            Session.SendToClients(Protocol.Build(Msg.ChomperState, w =>
            {
                w.Write((ushort)StateChompers.Count);
                foreach (var p in StateChompers)
                {
                    var st = p.anim.GetCurrentAnimatorStateInfo(0);
                    w.Write((sbyte)p.thePlantColumn);
                    w.Write((sbyte)p.thePlantRow);
                    w.Write((int)p.thePlantType);
                    w.Write((int)p.theStatus);
                    w.Write(st.fullPathHash);
                    w.Write(st.normalizedTime % 1f);
                    w.Write(p.attributeCountdown);
                }
            }));
        }

        private static void ReplayChomperState(Packet pkt)
        {
            var r = pkt.Reader();
            int count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                int col = r.ReadSByte(), row = r.ReadSByte();
                var type = (PlantType)r.ReadInt32();
                var status = (PlantStatus)r.ReadInt32();
                int hash = r.ReadInt32();
                float time = r.ReadSingle();
                float countdown = r.ReadSingle();
                if (Board.Instance == null) continue;
                var p = PlantSync.FindPlant(col, row, type);
                if (p == null || p.thePlantType != type || p.anim == null) continue;
                var anim = p.anim;
                var key = p.Pointer;
                bool sameAnim = anim.GetCurrentAnimatorStateInfo(0).fullPathHash == hash ||
                                (anim.IsInTransition(0) && anim.GetNextAnimatorStateInfo(0).fullPathHash == hash);
                if (p.theStatus == status && sameAnim)
                {
                    ChomperMismatch.Remove(key);
                    // Chewing on both: count down with the host.
                    if (countdown > 0 && p.attributeCountdown > 0 && Mathf.Abs(p.attributeCountdown - countdown) > CountdownSlack)
                        p.SetAttributeCountdown(countdown, -1f);
                    continue;
                }
                ChomperMismatch.TryGetValue(key, out int seen);
                if (++seen < 2)
                {
                    ChomperMismatch[key] = seen;
                    continue;
                }
                ChomperMismatch.Remove(key);
                ChompLog($"client: {Where(p)} was {p.theStatus}, host's is {status}: set to the host's");
                p.theStatus = status;
                if (status == PlantStatus.Default) p.targetZombie = null;
                // A bite this window never landed won't land now.
                BitePending.Remove(key);
                if (!sameAnim && hash != 0 && anim.HasState(0, hash)) anim.Play(hash, 0, time);
                if (countdown > 0) p.SetAttributeCountdown(countdown, -1f);
            }
        }

        private static string Where(Plant p) => p == null ? "?" : $"{p.thePlantType} c{p.thePlantColumn} r{p.thePlantRow}";

        private static void ChompLog(string msg) => FusionCoopMod.Log.Msg($"[chomper] {msg}");
    }
}
