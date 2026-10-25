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

        // MachineNut picks plain / fire / ice in InitType (from Start) with Random.Range: seeded the same way.
        [HarmonyPatch(typeof(MachineNutZombie), "InitType")]
        internal static class MachineNutTypePatch
        {
            private static void Prefix(MachineNutZombie __instance, out Random.State __state)
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

        // A MachineNut bounces off a strong plant into another row; puppets don't bounce on their own,
        // the host's row arrives with the zombie state (ChangeRow there). The hit plant's flash is sent.
        [HarmonyPatch(typeof(MachineNutZombie), "OnTriggerStay2D")]
        internal static class MachineNutBouncePatch
        {
            private static bool Prefix(MachineNutZombie __instance, out int __state)
            {
                __state = __instance != null ? __instance.theZombieRow : -1;
                return Replaying || !IsPuppet(__instance);
            }

            private static void Postfix(MachineNutZombie __instance, Collider2D collision, int __state)
            {
                if (Session.Role != Role.Host || !Session.Ready || __instance == null || collision == null) return;
                if (__instance.theZombieRow == __state) return; // no bounce this time
                var plant = collision.GetComponent<Plant>();
                if (plant != null) SendFlash(plant);
            }
        }

        // ---------- garlic ----------
        // Zombie.EatGarlic (the plant flashes, a sound, then DeLayGarliced: the stinky face and a lane
        // change) comes from the zombie's own attack, which puppets don't run: the host sends it.
        // A puppet doesn't change lanes on its own (Garliced picks a random free lane); the host's lane
        // arrives with the zombie state.
        // Only methods with a body of their own are patched: the zombies that ignore garlic (MachineNut,
        // bosses, ...) declare an empty EatGarlic, and IL2CPP folds every empty method into one native
        // function (~1300 of them), so patching one patches them all and crashes the game.
        private static int _garlicDepth;

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.EatGarlic))]
        internal static class EatGarlicPatch
        {
            private static bool Prefix(Zombie __instance, out float __state)
            {
                __state = __instance != null ? __instance.garlicSpeed : 0f;
                _garlicDepth++;
                return Replaying || !IsPuppet(__instance);
            }

            private static void Postfix(Zombie __instance, object[] __args, float __state)
            {
                if (_garlicDepth != 1 || Session.Role != Role.Host || !Session.Ready || __instance == null) return;
                if (__state == 0f || __instance.garlicSpeed != 0f) return; // it didn't eat (already stinky, dying, ...)
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint id)) return;
                var plant = __args.Length > 0 ? __args[0] as Plant : null;
                float time = __args.Length > 1 && __args[1] is float f ? f : 1f;
                bool sound = __args.Length <= 2 || !(__args[2] is bool b) || b;
                Session.SendToClients(Protocol.Build(Msg.ZombieGarlic, w =>
                {
                    w.Write(id);
                    w.Write((sbyte)(plant != null ? plant.thePlantColumn : -1));
                    w.Write((sbyte)(plant != null ? plant.thePlantRow : -1));
                    w.Write(plant != null ? (int)plant.thePlantType : -1);
                    w.Write(time);
                    w.Write(sound);
                }));
            }

            private static System.Exception Finalizer(System.Exception __exception)
            {
                _garlicDepth--;
                return __exception;
            }
        }

        [HarmonyPatch]
        internal static class ChangeRowPatch
        {
            // Jackbox_a's ChangeRow is the same native function as PogoZombie's.
            private static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
            {
                AccessTools.Method(typeof(Zombie), nameof(Zombie.ChangeRow)),
                AccessTools.Method(typeof(PogoZombie), nameof(PogoZombie.ChangeRow)),
                AccessTools.Method(typeof(LegionSniperZombie), nameof(LegionSniperZombie.ChangeRow)),
            };

            private static bool Prefix(Zombie __instance) => Replaying || !IsPuppet(__instance);
        }

        private static void ReplayGarlic(Packet pkt)
        {
            var r = pkt.Reader();
            uint id = r.ReadUInt32();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            float time = r.ReadSingle();
            bool sound = r.ReadBoolean();
            if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null || rz.Killed) return;
            Plant plant = null;
            if (col >= 0)
            {
                plant = PlantSync.FindPlant(col, row, type);
                if (plant != null && plant.thePlantType != type) plant = null;
            }
            var z = rz.Zombie;
            try { Replay(() => CallDeclared(z, "EatGarlic", new[] { typeof(Plant), typeof(float), typeof(bool) }, new object[] { plant, time, sound })); }
            catch (System.Exception e) { FusionCoopMod.Log.Warning($"[zombie] garlic replay failed: {e.Message}"); }
        }

        // ---------- plant flashes the game makes outside a bite ----------

        private static void SendFlash(Plant plant)
        {
            Session.SendToClients(Protocol.Build(Msg.PlantFlash, w =>
            {
                w.Write((sbyte)plant.thePlantColumn);
                w.Write((sbyte)plant.thePlantRow);
                w.Write((int)plant.thePlantType);
            }));
        }

        private static void ReplayFlash(Packet pkt)
        {
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            var plant = PlantSync.FindPlant(col, row, type);
            if (plant != null && plant.thePlantType == type) plant.FlashOnce();
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

        // Same for any other method a zombie class may redeclare (EatGarlic, ...).
        private static readonly Dictionary<string, MethodInfo> DeclaredByClass = new();

        private static void CallDeclared(Zombie z, string name, System.Type[] sig, object[] args)
        {
            var asm = typeof(Zombie).Assembly;
            Il2CppSystem.Type t = z.GetIl2CppType();
            string key = (t != null ? t.FullName : "?") + "::" + name;
            if (!DeclaredByClass.TryGetValue(key, out var found))
            {
                for (int depth = 0; t != null && depth < 12 && found == null; depth++, t = t.BaseType)
                {
                    var managed = asm.GetType("Il2Cpp." + t.FullName);
                    if (managed != null)
                        found = managed.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, sig, null);
                    if (t.Name == "Zombie") break;
                }
                DeclaredByClass[key] = found;
            }
            if (found == null) throw new System.MissingMethodException(key);
            var wrapper = System.Activator.CreateInstance(found.DeclaringType, z.Pointer);
            try { found.Invoke(wrapper, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        // ---------- bites ----------
        // Each bite's plant flash and chomp sound come from Zombie.AttackEffect, which AttackUpdate calls;
        // puppets don't run AttackUpdate, so the host sends every bite and the puppet plays it.
        [HarmonyPatch(typeof(Zombie), "AttackEffect")]
        internal static class AttackEffectPatch
        {
            private static void Postfix(Zombie __instance, Plant plant)
            {
                if (Session.Role != Role.Host || !Session.Ready || __instance == null || plant == null) return;
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint id)) return;
                int col = plant.thePlantColumn, row = plant.thePlantRow;
                var type = plant.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.ZombieBite, w =>
                {
                    w.Write(id);
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                }));
            }
        }

        private static void ReplayBite(Packet pkt)
        {
            var r = pkt.Reader();
            uint id = r.ReadUInt32();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null || rz.Killed) return;
            var plant = PlantSync.FindPlant(col, row, type);
            if (plant == null || plant.thePlantType != type) return;
            var z = rz.Zombie;
            try { Replay(() => z.AttackEffect(plant)); }
            catch (System.Exception e) { FusionCoopMod.Log.Warning($"[zombie] bite replay failed: {e.Message}"); }
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
                case Msg.ZombieBite:
                    ReplayBite(pkt);
                    return true;
                case Msg.ZombieGarlic:
                    ReplayGarlic(pkt);
                    return true;
                case Msg.PlantFlash:
                    ReplayFlash(pkt);
                    return true;
                default:
                    return false;
            }
        }
    }
}
