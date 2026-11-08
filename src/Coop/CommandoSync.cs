using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;

namespace FusionCoop.Coop
{
    // Commando ultimates: the host's roll decides.
    //
    // A Commando's normal shot (Shoot1, from the "shoot" animation the host already decides) rolls a small
    // chance to start its ultimate instead. Each window rolled for itself, so ultimates came at different times.
    // The methods that start an ultimate run on the host only and are replayed on clients:
    // - SuperSnowGatling.SuperShoot(timer): Buckshot, Blizzard, Pea-storm, Brainstorm, Pea-nut, Peatato
    //   (SuperGatlingPeaMine's own version calls it) and Drone Commandos,
    // - SuperGatlingGloom.KeepOneShooting(count): Melancholy Commando.
    // A client's own roll that hits only loses that one shot.
    // Two roll inside a method and only set a field, so the roll itself is steered:
    // - Tri-phoon (SuperThreeGatling.Shoot1): roll 0..99 < _p starts the 5 s barrage (timer = 5). Clients shoot
    //   with _p = 0 (never); the host's ultimate is replayed by one Shoot1 with _p = 1000 (always).
    // - Flurry (SuperGatlingFume.StartShooting): the roll sets super for that attack. Clients set it from the
    //   host's message (which may come just before or after their own StartShooting).
    // - Pea-nut (SuperNutShooter.Shoot1): its own roll (Random.Range(0, 100) against a fixed chance) starts the
    //   barrage that lets giant wall-nuts out (Shoot2 plants them; clients get those as the host's plants).
    //   The roll is the first random number Shoot1 draws, so it is steered through Random's seed: clients
    //   shoot from a seed whose first roll is 99 (never), the host's ultimate is replayed from one whose
    //   first roll is 0 (always); Random's state is put back after.
    // Dynamite Commando has no class of its own: its prefab uses one of the above (the log names its type).
    internal static class CommandoSync
    {
        private const byte KindSuperShoot = 0, KindGloom = 1, KindThree = 2, KindFlurry = 3, KindNut = 4;

        // Seeds whose first Random.Range(0, 100) is 99 / 0, found once.
        private static int _neverSeed = -1, _alwaysSeed = -1;

        private static void FindSeeds()
        {
            if (_neverSeed >= 0 && _alwaysSeed >= 0) return;
            var saved = UnityEngine.Random.state;
            for (int seed = 1; seed < 100000 && (_neverSeed < 0 || _alwaysSeed < 0); seed++)
            {
                UnityEngine.Random.InitState(seed);
                int roll = UnityEngine.Random.Range(0, 100);
                if (roll == 99 && _neverSeed < 0) _neverSeed = seed;
                if (roll == 0 && _alwaysSeed < 0) _alwaysSeed = seed;
            }
            UnityEngine.Random.state = saved;
        }

        // client: Flurry Commandos the host said are super, by instance id -> when
        private static readonly System.Collections.Generic.Dictionary<int, double> FlurryPending = new();

        private static int _depth;       // host: nested calls (PeaMine's SuperShoot calls the base one)
        private static bool _replaying;  // client: running the host's

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;

        private static bool Before(Plant p, byte kind, float arg)
        {
            if (IsClient) return _replaying;
            _depth++;
            if (IsHost && _depth == 1 && p != null && Board.Instance != null) Send(p, kind, arg);
            return true;
        }

        private static void After()
        {
            if (!IsClient && _depth > 0) _depth--;
        }

        [HarmonyPatch]
        internal static class SuperShootPatch
        {
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.DeclaredMethod(typeof(SuperSnowGatling), nameof(SuperSnowGatling.SuperShoot), new[] { typeof(float) });
                yield return AccessTools.DeclaredMethod(typeof(SuperGatlingPeaMine), nameof(SuperGatlingPeaMine.SuperShoot), new[] { typeof(float) });
            }

            private static bool Prefix(Plant __instance, float timer, out bool __state)
            {
                __state = !IsClient;
                return Before(__instance, KindSuperShoot, timer);
            }

            private static System.Exception Finalizer(System.Exception __exception, bool __state)
            {
                if (__state) After();
                return __exception;
            }
        }

        [HarmonyPatch(typeof(SuperGatlingGloom), nameof(SuperGatlingGloom.KeepOneShooting))]
        internal static class GloomPatch
        {
            private static bool Prefix(Plant __instance, int count, out bool __state)
            {
                __state = !IsClient;
                return Before(__instance, KindGloom, count);
            }

            private static System.Exception Finalizer(System.Exception __exception, bool __state)
            {
                if (__state) After();
                return __exception;
            }
        }

        private static void Send(Plant p, byte kind, float arg)
        {
            int col = p.thePlantColumn, row = p.thePlantRow;
            var type = p.thePlantType;
            Session.SendToClients(Protocol.Build(Msg.CommandoUltimate, w =>
            {
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)type);
                w.Write(kind);
                w.Write(arg);
            }));
            FusionCoopMod.Log.Msg($"[commando] host's {type} c{col} r{row} ultimate -> clients");
        }

        [HarmonyPatch(typeof(SuperThreeGatling), nameof(SuperThreeGatling.Shoot1))]
        internal static class TriphoonPatch
        {
            private static void Prefix(SuperThreeGatling __instance, out (float timer, int chance, bool steered) __state)
            {
                __state = (__instance.timer, __instance._p, false);
                if (!IsClient || _replaying) return;
                __instance._p = 0;
                __state.steered = true;
            }

            private static void Postfix(SuperThreeGatling __instance, (float timer, int chance, bool steered) __state)
            {
                if (IsHost && __instance.timer == 5f && __state.timer != 5f) Send(__instance, KindThree, 5f);
            }

            private static System.Exception Finalizer(System.Exception __exception, SuperThreeGatling __instance, (float timer, int chance, bool steered) __state)
            {
                if (__state.steered && __instance != null) __instance._p = __state.chance;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(SuperNutShooter), nameof(SuperNutShooter.Shoot1))]
        internal static class PeanutPatch
        {
            private static void Prefix(SuperNutShooter __instance, out (float timer, UnityEngine.Random.State random, bool steered) __state)
            {
                __state = (__instance.timer, default, false);
                if (!IsClient) return;
                FindSeeds();
                __state.random = UnityEngine.Random.state;
                __state.steered = true;
                UnityEngine.Random.InitState(_replaying ? _alwaysSeed : _neverSeed);
            }

            private static void Postfix(SuperNutShooter __instance, (float timer, UnityEngine.Random.State random, bool steered) __state)
            {
                if (IsHost && __instance.timer == 5f && __state.timer != 5f) Send(__instance, KindNut, 5f);
            }

            private static System.Exception Finalizer(System.Exception __exception, (float timer, UnityEngine.Random.State random, bool steered) __state)
            {
                if (__state.steered) UnityEngine.Random.state = __state.random;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(SuperGatlingFume), nameof(SuperGatlingFume.StartShooting))]
        internal static class FlurryPatch
        {
            private static void Postfix(SuperGatlingFume __instance)
            {
                if (__instance == null) return;
                if (IsHost)
                {
                    if (__instance.super) Send(__instance, KindFlurry, 0f);
                    return;
                }
                if (!IsClient) return;
                int id = __instance.GetInstanceID();
                bool hostSaid = FlurryPending.TryGetValue(id, out var at) && Session.Now - at < 0.5;
                FlurryPending.Remove(id);
                __instance.super = hostSaid;
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.CommandoUltimate || Board.Instance == null) return;
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            byte kind = r.ReadByte();
            float arg = r.ReadSingle();
            var p = DroneSync.FindShooter(col, row, type);
            if (p == null)
            {
                FusionCoopMod.Log.Warning($"[commando] no {type} at c{col} r{row} for the host's ultimate");
                return;
            }
            _replaying = true;
            try
            {
                if (kind == KindNut)
                {
                    p.TryCast<SuperNutShooter>()?.Shoot1();
                }
                else if (kind == KindThree)
                {
                    var three = p.TryCast<SuperThreeGatling>();
                    if (three != null)
                    {
                        int chance = three._p;
                        three._p = 1000;
                        try { three.Shoot1(); }
                        finally { three._p = chance; }
                    }
                }
                else if (kind == KindFlurry)
                {
                    var flurry = p.TryCast<SuperGatlingFume>();
                    if (flurry != null)
                    {
                        flurry.super = true;
                        FlurryPending[flurry.GetInstanceID()] = Session.Now;
                    }
                }
                else if (kind == KindGloom)
                {
                    var gloom = p.TryCast<SuperGatlingGloom>();
                    if (gloom != null) gloom.KeepOneShooting((int)arg);
                }
                else
                {
                    var peaMine = p.TryCast<SuperGatlingPeaMine>();
                    if (peaMine != null) peaMine.SuperShoot(arg);
                    else p.TryCast<SuperSnowGatling>()?.SuperShoot(arg);
                }
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[commando] ultimate replay failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }
    }
}
