using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Zombies are owned by the host; on clients they are puppets.
    //
    // Nearly all of the ~150 spawn sources (waves in BoardSpawner.SummonZombies, intro previews,
    // zombies that summon zombies, plants) go through CreateZombie.SetZombie / SetZombieWithMindControl.
    // - Host: every zombie spawned there gets a network id. 10 times a second it sends the wave counters
    //   and the full zombie list (id, type, row, position, health, armor, animator state, flags), and it
    //   sends a zombie's death the moment Zombie.Die runs.
    // - Client: its own spawns are blocked. It reconciles against the host's list: spawns missing zombies,
    //   eases them to the host's position, plays the host's animator state, and applies the host's damage
    //   through Zombie.TakeDamage so armor cracks/falls and heads drop the game's own way. Its zombies
    //   don't walk or eat on their own (MoveUpdate/AttackUpdate blocked), take no local damage and only
    //   die when the host says.
    // - Waves: the client's Board.NewZombieUpdate is blocked except for one run each time the host's wave
    //   number goes up, so the "huge wave" banner and sounds play locally; the counters are copied.
    // Sending full state (instead of only events) also covers late joins and lost timing.
    internal static class ZombieSync
    {
        private const float SendInterval = 0.1f;
        private const byte FlagDying = 1, FlagMind = 2;

        // Host
        private static uint _nextId = 1;
        private static readonly Dictionary<uint, Zombie> HostZombies = new();
        private static readonly Dictionary<int, uint> HostIds = new(); // instance id -> network id
        private static double _nextSend;

        // Client
        private sealed class Remote
        {
            internal Zombie Zombie;
            internal Vector2 Target;
            internal bool Killed;
        }

        private static readonly Dictionary<uint, Remote> ClientZombies = new();
        private static readonly HashSet<int> Puppets = new(); // instance ids of live mirrored zombies
        private static int _clientBoardId;
        private static int _hostWave = -1;
        private static bool _hostHuge, _hugeShownForNext;
        private static float _hostUntil, _hostCountdown;
        private static bool _allowWaveStep;
        internal static bool Replaying;

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        internal static void Reset()
        {
            HostZombies.Clear();
            HostIds.Clear();
            ClientZombies.Clear();
            Puppets.Clear();
            _clientBoardId = 0;
            _hostWave = -1;
            _hugeShownForNext = false;
            _allowWaveStep = false;
        }

        private static bool IsPuppet(Zombie z) => IsClient && z != null && Puppets.Contains(z.GetInstanceID());

        // ---------- spawning ----------

        private static void Track(Zombie z)
        {
            if (z == null || Session.Role != Role.Host) return;
            uint id = _nextId++;
            HostZombies[id] = z;
            HostIds[z.GetInstanceID()] = id;
        }

        [HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombie))]
        internal static class SetZombiePatch
        {
            private static bool Prefix(ref Zombie __result)
            {
                if (IsClient && !Replaying)
                {
                    __result = null;
                    return false;
                }
                return true;
            }

            private static void Postfix(Zombie __result) => Track(__result);
        }

        [HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombieWithMindControl))]
        internal static class SetZombieMindPatch
        {
            private static bool Prefix(ref Zombie __result)
            {
                if (IsClient && !Replaying)
                {
                    __result = null;
                    return false;
                }
                return true;
            }

            private static void Postfix(Zombie __result) => Track(__result);
        }

        // ---------- client puppets: no local AI, damage or death ----------

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.TakeDamage))]
        internal static class TakeDamagePatch
        {
            private static bool Prefix() => !(IsClient && !Replaying && Board.Instance != null);
        }

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.Die))]
        internal static class DiePatch
        {
            private static bool Prefix() => !(IsClient && !Replaying && Board.Instance != null);

            // Host: tell clients right away instead of waiting for the next state message.
            private static void Postfix(Zombie __instance)
            {
                if (Session.Role != Role.Host || !Session.Ready || __instance == null) return;
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint id)) return;
                Session.SendToClients(Protocol.Build(Msg.ZombieDied, w => w.Write(id)));
            }
        }

        [HarmonyPatch(typeof(Zombie), "MoveUpdate")]
        internal static class MoveUpdatePatch
        {
            private static bool Prefix(Zombie __instance) => !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(Zombie), "AttackUpdate")]
        internal static class AttackUpdatePatch
        {
            private static bool Prefix(Zombie __instance) => !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(Board), "NewZombieUpdate")]
        internal static class NewZombieUpdatePatch
        {
            private static bool Prefix()
            {
                if (!IsClient) return true;
                if (!_allowWaveStep) return false;
                _allowWaveStep = false;
                return true; // one run: the game advances the wave and shows its banner
            }

            // After the step (or nothing), the counters are the host's again.
            private static void Postfix(Board __instance)
            {
                if (!IsClient || _hostWave < 0) return;
                __instance.theWave = _hostWave;
                __instance.timeUntilNextWave = _hostUntil;
                __instance.isHugeWave = _hostHuge;
                __instance.hugeWaveCountDown = _hostCountdown;
            }
        }

        // ---------- host: send ----------

        private static double _nextReport;

        internal static void Update()
        {
            if (Session.Role == Role.Host) HostUpdate();
            else if (IsClient) ClientSmooth();
            if (Session.Ready && Board.Instance != null && Session.Now >= _nextReport)
            {
                _nextReport = Session.Now + 5;
                int alive = 0;
                if (Session.Role == Role.Host)
                {
                    foreach (var z in HostZombies.Values) if (z != null && z.theStatus != ZombieStatus.Dying) alive++;
                    FusionCoopMod.Log.Msg($"[zombie] host: {alive} alive / {HostZombies.Count} tracked, wave {Board.Instance.theWave}/{Board.Instance.theMaxWave}");
                }
                else
                {
                    foreach (var rz in ClientZombies.Values) if (rz.Zombie != null && !rz.Killed) alive++;
                    FusionCoopMod.Log.Msg($"[zombie] client: {alive} alive / {ClientZombies.Count} mirrored, wave {Board.Instance.theWave}/{Board.Instance.theMaxWave}");
                }
            }
        }

        private static readonly List<uint> Gone = new();

        private static void HostUpdate()
        {
            var board = Board.Instance;
            if (board == null)
            {
                HostZombies.Clear();
                HostIds.Clear();
                return;
            }
            if (!Session.Ready || Session.Names.Count < 2 || Session.Now < _nextSend) return;
            _nextSend = Session.Now + SendInterval;

            Gone.Clear();
            foreach (var kv in HostZombies)
                if (kv.Value == null) Gone.Add(kv.Key);
            foreach (var id in Gone) HostZombies.Remove(id);

            var frame = Protocol.Build(Msg.ZombieState, w =>
            {
                w.Write(board.theWave);
                w.Write(board.theMaxWave);
                w.Write(board.timeUntilNextWave);
                w.Write(board.isHugeWave);
                w.Write(board.hugeWaveCountDown);
                w.Write((ushort)HostZombies.Count);
                foreach (var kv in HostZombies)
                {
                    var z = kv.Value;
                    var p = z.transform.position;
                    byte flags = 0;
                    if (z.theStatus == ZombieStatus.Dying) flags |= FlagDying;
                    if (z.isMindControlled) flags |= FlagMind;
                    int animHash = 0;
                    float animTime = 0;
                    var anim = z.anim;
                    if (anim != null)
                    {
                        var st = anim.GetCurrentAnimatorStateInfo(0);
                        animHash = st.fullPathHash;
                        animTime = st.normalizedTime % 1f;
                    }
                    w.Write(kv.Key);
                    w.Write((int)z.theZombieType);
                    w.Write((sbyte)z.theZombieRow);
                    w.Write(p.x);
                    w.Write(p.y);
                    w.Write(z.theHealth);
                    w.Write(z.theFirstArmorHealth);
                    w.Write(z.theSecondArmorHealth);
                    w.Write(animHash);
                    w.Write(animTime);
                    w.Write(flags);
                }
            });
            Session.SendToClients(frame);
        }

        // ---------- client: reconcile ----------

        private static readonly HashSet<uint> Seen = new();
        private static readonly List<uint> Stale = new();

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type == Msg.ZombieDied)
            {
                uint id = pkt.Reader().ReadUInt32();
                if (ClientZombies.TryGetValue(id, out var dead)) Kill(dead);
                return;
            }
            if (pkt.Type != Msg.ZombieState) return;
            var board = Board.Instance;
            var cz = CreateZombie.Instance;
            if (board == null || cz == null) return;
            if (board.GetInstanceID() != _clientBoardId)
            {
                ClientZombies.Clear();
                Puppets.Clear();
                _clientBoardId = board.GetInstanceID();
                _hostWave = -1;
                _hugeShownForNext = false;
            }

            var r = pkt.Reader();
            ApplyWave(board, r.ReadInt32(), r.ReadInt32(), r.ReadSingle(), r.ReadBoolean(), r.ReadSingle());

            int count = r.ReadUInt16();
            Seen.Clear();
            for (int i = 0; i < count; i++)
            {
                uint id = r.ReadUInt32();
                var type = (ZombieType)r.ReadInt32();
                int row = r.ReadSByte();
                float x = r.ReadSingle(), y = r.ReadSingle();
                int health = r.ReadInt32(), armor1 = r.ReadInt32(), armor2 = r.ReadInt32();
                int animHash = r.ReadInt32();
                float animTime = r.ReadSingle();
                byte flags = r.ReadByte();
                bool dying = (flags & FlagDying) != 0;
                Seen.Add(id);

                if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null)
                {
                    if (dying) continue; // don't spawn something that is already dying
                    var z = Spawn(cz, row, type, x, (flags & FlagMind) != 0);
                    if (z == null) continue;
                    rz = new Remote { Zombie = z };
                    ClientZombies[id] = rz;
                    Puppets.Add(z.GetInstanceID());
                    var p = z.transform.position;
                    z.transform.position = new Vector3(x, y, p.z);
                }
                if (rz.Killed) continue;

                var zz = rz.Zombie;
                rz.Target = new Vector2(x, y);
                ApplyHealth(zz, health, armor1, armor2);
                ApplyAnim(zz, animHash, animTime);
                if (dying) Kill(rz);
            }

            // Zombies the host no longer has.
            Stale.Clear();
            foreach (var kv in ClientZombies)
                if (!Seen.Contains(kv.Key)) Stale.Add(kv.Key);
            foreach (var id in Stale)
            {
                var rz = ClientZombies[id];
                ClientZombies.Remove(id);
                if (rz.Zombie == null || rz.Killed) continue;
                Puppets.Remove(rz.Zombie.GetInstanceID());
                Object.Destroy(rz.Zombie.gameObject);
            }
        }

        // The game (Board.NewZombieUpdate) counts timeUntilNextWave down; for a huge wave it first sets
        // isHugeWave (siren + banner) and runs hugeWaveCountDown, then increments theWave and summons.
        // The client runs that code once at the host's moments so the banner and sounds play in time:
        // when the host's isHugeWave turns on, and when the host's wave goes up without a huge-wave warning.
        private static void ApplyWave(Board board, int wave, int maxWave, float untilNext, bool huge, float countdown)
        {
            board.theMaxWave = maxWave;
            if (_hostWave < 0)
            {
                // First message on this board: just take it.
                board.theWave = wave;
            }
            else if (huge && !_hostHuge)
            {
                board.theWave = wave;
                board.timeUntilNextWave = 0f;
                board.isHugeWave = false;
                _allowWaveStep = true;
                _hugeShownForNext = true;
            }
            else if (wave > _hostWave && board.theWave < wave)
            {
                if (_hugeShownForNext)
                {
                    _hugeShownForNext = false; // warning already played
                }
                else
                {
                    board.theWave = wave - 1;
                    board.timeUntilNextWave = 0f;
                    board.isHugeWave = false;
                    _allowWaveStep = true;
                }
            }
            _hostWave = wave;
            _hostHuge = huge;
            _hostUntil = untilNext;
            _hostCountdown = countdown;
            if (!_allowWaveStep)
            {
                board.theWave = wave;
                board.timeUntilNextWave = untilNext;
                board.isHugeWave = huge;
                board.hugeWaveCountDown = countdown;
            }
        }

        // Damage the puppet by what the host's zombie lost, through the game's own TakeDamage so armor
        // cracks, falls and lost heads happen like on the host; then copy the exact values.
        private static void ApplyHealth(Zombie z, int health, int armor1, int armor2)
        {
            int local = z.theHealth + z.theFirstArmorHealth + z.theSecondArmorHealth;
            int host = health + armor1 + armor2;
            if (host < local)
            {
                int dmg = local - host;
                Replay(() => z.TakeDamage(dmg, null, DamageType.Normal, (PlantType)(-1), false));
            }
            if (z == null) return;
            z.theHealth = health;
            z.theFirstArmorHealth = armor1;
            z.theSecondArmorHealth = armor2;
        }

        private static void ApplyAnim(Zombie z, int hash, float time)
        {
            var anim = z.anim;
            if (anim == null || hash == 0) return;
            if (anim.GetCurrentAnimatorStateInfo(0).fullPathHash == hash) return;
            if (!anim.HasState(0, hash)) return;
            anim.Play(hash, 0, time);
        }

        private static void Kill(Remote rz)
        {
            if (rz.Killed || rz.Zombie == null) return;
            rz.Killed = true;
            var z = rz.Zombie;
            Puppets.Remove(z.GetInstanceID());
            Replay(() => z.Die(0));
        }

        private static Zombie Spawn(CreateZombie cz, int row, ZombieType type, float x, bool mind)
        {
            Zombie z = null;
            Replay(() => z = mind
                ? cz.SetZombieWithMindControl(row, type, x, false)
                : cz.SetZombie(row, type, x, false));
            return z;
        }

        // Ease client zombies toward the host's positions every frame.
        private static void ClientSmooth()
        {
            if (ClientZombies.Count == 0 || Board.Instance == null) return;
            float t = Mathf.Clamp01(Time.unscaledDeltaTime * 12f);
            foreach (var rz in ClientZombies.Values)
            {
                var z = rz.Zombie;
                if (z == null || rz.Killed) continue;
                var p = z.transform.position;
                var target = new Vector3(rz.Target.x, rz.Target.y, p.z);
                // Snap when far off (spawn, teleports), ease otherwise.
                z.transform.position = (target - p).sqrMagnitude > 4f ? target : Vector3.Lerp(p, target, t);
            }
        }

        private static void Replay(System.Action action)
        {
            bool was = Replaying;
            Replaying = true;
            try
            {
                action();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Error($"[zombie] replay failed: {e.Message}");
            }
            finally
            {
                Replaying = was;
            }
        }
    }
}
