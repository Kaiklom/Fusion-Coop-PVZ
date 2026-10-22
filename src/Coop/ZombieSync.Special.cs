using System.Collections.Generic;
using System.Reflection;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Zombie kinds with their own behavior: random looks at spawn, fire deaths, Dancing Zombie's backup
    // dancers, Zomboni ice and vehicles' own Die.
    internal static partial class ZombieSync
    {
        // Network id of the zombie being created right now (host: the id Track will give; client: the
        // host's id), so spawn-time randomness can be seeded the same on every machine.
        private static uint _spawningId;

        private static uint IdOf(Zombie z)
        {
            if (z == null) return 0;
            int iid = z.GetInstanceID();
            if (Session.Role == Role.Host && HostIds.TryGetValue(iid, out uint h)) return h;
            if (Session.Role == Role.Client && Puppets.TryGetValue(iid, out uint c)) return c;
            return _spawningId;
        }

        // ---------- random looks: Gargantuar weapons ----------
        // SetWeapon picks the weapon with Random.Range. Seeded by the network id, every machine picks
        // the same one; the global random state is put back afterwards.
        [HarmonyPatch(typeof(Gargantuar), "SetWeapon")]
        internal static class SetWeaponPatch
        {
            private static void Prefix(Gargantuar __instance, out Random.State __state)
            {
                __state = Random.state;
                uint id = Session.Ready ? IdOf(__instance) : 0;
                if (id != 0) Random.InitState(unchecked((int)(id * 2654435761u)));
            }

            private static System.Exception Finalizer(System.Exception __exception, Random.State __state)
            {
                Random.state = __state;
                return __exception;
            }
        }

        // ---------- fire: ash deaths ----------
        // Fire plants call Zombie.Charred: heavy damage, and a zombie that dies from it turns to ash.
        // Clients' own fire on puppets is ignored; the host's burn is replayed, then its death.
        [HarmonyPatch(typeof(Zombie), nameof(Zombie.Charred))]
        internal static class CharredPatch
        {
            private static bool Prefix(Zombie __instance, out Zombie __state)
            {
                __state = _eventZombie;
                if (Session.Role == Role.Host)
                {
                    _eventZombie = __instance;
                    _eventKilled = false;
                }
                return Replaying || !IsPuppet(__instance);
            }

            private static void Postfix(Zombie __instance, int damage, DamageType damageType)
            {
                if (Session.Role != Role.Host || !Session.Ready || __instance == null) return;
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint id)) return;
                bool died = _eventKilled;
                _eventKilled = false;
                Session.SendToClients(Protocol.Build(Msg.ZombieCharred, w =>
                {
                    w.Write(id);
                    w.Write(damage);
                    w.Write((int)damageType);
                    w.Write(died);
                }));
            }

            private static System.Exception Finalizer(System.Exception __exception, Zombie __state)
            {
                _eventZombie = __state;
                return __exception;
            }
        }

        private static void ReplayCharred(Packet pkt)
        {
            var r = pkt.Reader();
            uint id = r.ReadUInt32();
            int damage = r.ReadInt32();
            var type = (DamageType)r.ReadInt32();
            bool died = r.ReadBoolean();
            if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null || rz.Killed) return;
            var z = rz.Zombie;
            try { Replay(() => z.Charred(damage, (PlantType)(-1), false, type)); }
            finally
            {
                if (died) Kill(rz);
            }
        }

        // ---------- Dancing Zombie's backup dancers ----------
        // ZombieJackson.AnimSummon (an animation event) calls ZombieJackson.SetZombie(x, row) per dancer,
        // which makes the zombie rise from the ground. The host sends each one; the client's puppet
        // summons it the same way and it takes the host's id. Puppets don't summon on their own.
        private static ZombieJackson _summoner;

        [HarmonyPatch(typeof(ZombieJackson), "AnimSummon")]
        internal static class JacksonAnimSummonPatch
        {
            private static bool Prefix(ZombieJackson __instance) => Replaying || !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(ZombieJackson), "SetZombie")]
        internal static class JacksonSummonPatch
        {
            private static void Prefix(ZombieJackson __instance, out ZombieJackson __state)
            {
                __state = _summoner;
                _summoner = __instance;
            }

            private static void Postfix(ZombieJackson __instance, GameObject __result, float x, int row)
            {
                if (Session.Role != Role.Host || !Session.Ready || __result == null) return;
                var z = __result.GetComponent<Zombie>();
                if (z == null || !HostIds.TryGetValue(z.GetInstanceID(), out uint id)) return;
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint summoner)) return;
                Session.SendToClients(Protocol.Build(Msg.ZombieSummoned, w =>
                {
                    w.Write(id);
                    w.Write(summoner);
                    w.Write(x);
                    w.Write(row);
                }));
            }

            private static System.Exception Finalizer(System.Exception __exception, ZombieJackson __state)
            {
                _summoner = __state;
                return __exception;
            }
        }

        private static void ReplaySummon(Packet pkt)
        {
            var r = pkt.Reader();
            uint id = r.ReadUInt32();
            uint summoner = r.ReadUInt32();
            float x = r.ReadSingle();
            int row = r.ReadInt32();
            if (ClientZombies.TryGetValue(id, out var known) && known.Zombie != null) return;
            if (!ClientZombies.TryGetValue(summoner, out var sz) || sz.Zombie == null) return;
            var jackson = sz.Zombie.TryCast<ZombieJackson>();
            if (jackson == null) return;
            GameObject go = null;
            _spawningId = id;
            try { Replay(() => go = jackson.SetZombie(x, row)); }
            finally { _spawningId = 0; }
            var z = go != null ? go.GetComponent<Zombie>() : null;
            if (z == null)
            {
                FusionCoopMod.Log.Warning($"[zombie] dancer #{id} from #{summoner} could not be summoned");
                return; // the next state message spawns it plainly
            }
            ClientZombies[id] = new Remote { Zombie = z, Target = z.transform.position };
            Puppets[z.GetInstanceID()] = id;
        }

        // ---------- Zomboni ice ----------
        // DriverZombie.PositionUpdate (part of movement, which puppets don't run) stretches the lane's
        // ice road behind it; puppets do that part every frame.
        private static readonly Dictionary<int, DriverZombie> Drivers = new();
        private static readonly HashSet<int> NotDrivers = new();

        private static void LayIceRoad(Remote rz)
        {
            var z = rz.Zombie;
            int iid = z.GetInstanceID();
            if (NotDrivers.Contains(iid)) return;
            if (!Drivers.TryGetValue(iid, out var d))
            {
                d = z.TryCast<DriverZombie>();
                if (d == null)
                {
                    NotDrivers.Add(iid);
                    return;
                }
                Drivers[iid] = d;
            }
            try { d.CreateIceRoad(); }
            catch (System.Exception e)
            {
                NotDrivers.Add(iid);
                FusionCoopMod.Log.Warning($"[zombie] ice road failed: {e.Message}");
            }
        }

        // ---------- the zombie's own Die ----------
        // Several zombie classes (vehicles, Kirov, submarines, ...) declare their own Die(int) as a new
        // method instead of overriding Zombie.Die, so calling Die on a Zombie reference skips their
        // cleanup (a dead Zomboni stayed on the lawn). Call the most derived declared Die instead.
        private static readonly Dictionary<string, MethodInfo> DieByClass = new();

        private static void CallDie(Zombie z, int reason)
        {
            var m = FindDie(z);
            if (m == null)
            {
                z.Die(reason);
                return;
            }
            var wrapper = System.Activator.CreateInstance(m.DeclaringType, z.Pointer);
            try { m.Invoke(wrapper, new object[] { reason }); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        private static MethodInfo FindDie(Zombie z)
        {
            var asm = typeof(Zombie).Assembly;
            Il2CppSystem.Type t = z.GetIl2CppType();
            string first = t != null ? t.FullName : null;
            if (first != null && DieByClass.TryGetValue(first, out var cached)) return cached;
            MethodInfo found = null;
            for (int depth = 0; t != null && depth < 12; depth++, t = t.BaseType)
            {
                if (t.Name == "Zombie") break;
                var managed = asm.GetType("Il2Cpp." + t.FullName);
                if (managed == null) continue;
                var m = managed.GetMethod("Die", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(int) }, null);
                if (m == null) continue;
                found = m;
                break;
            }
            if (first != null) DieByClass[first] = found;
            return found;
        }

        private static bool HandleExtraMessage(Packet pkt)
        {
            switch (pkt.Type)
            {
                case Msg.ZombieSummoned:
                    ReplaySummon(pkt);
                    return true;
                case Msg.ZombieCharred:
                    ReplayCharred(pkt);
                    return true;
                default:
                    return false;
            }
        }
    }
}
