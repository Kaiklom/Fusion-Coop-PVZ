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
    internal static partial class ZombieSync
    {
        private const float SendInterval = 0.1f;
        private const byte FlagDying = 1, FlagMind = 2, FlagAttacking = 4;

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
        private static readonly Dictionary<int, uint> Puppets = new(); // instance id -> network id of live mirrored zombies
        private static int _clientBoardId;
        private static int _hostWave = -1;
        private static bool _hostHuge, _hugeShownForNext;
        private static float _hostUntil, _hostCountdown;
        private static bool _allowWaveStep;
        internal static bool Replaying;
        private static bool _killing; // client: replaying the host's zombie death

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
            Session.HostGameHandler += OnHostMessage;
        }

        internal static void Reset()
        {
            HostZombies.Clear();
            HostIds.Clear();
            ClientZombies.Clear();
            Puppets.Clear();
            Drivers.Clear();
            NotDrivers.Clear();
            _clientBoardId = 0;
            _hostWave = -1;
            _hugeShownForNext = false;
            _allowWaveStep = false;
        }

        private static bool IsPuppet(Zombie z) => IsClient && z != null && Puppets.ContainsKey(z.GetInstanceID());

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
                if (IsClient && (!Replaying || _killing))
                {
                    __result = null;
                    return false;
                }
                if (Session.Role == Role.Host) _spawningId = _nextId; // the id Track will give it
                return true;
            }

            private static void Postfix(Zombie __result)
            {
                if (Session.Role == Role.Host) _spawningId = 0;
                Track(__result);
            }
        }

        [HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombieWithMindControl))]
        internal static class SetZombieMindPatch
        {
            private static bool Prefix(ref Zombie __result)
            {
                if (IsClient && (!Replaying || _killing))
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
            private static bool Prefix(Zombie __instance, int theDamage, DamageType theDamageType)
            {
                if (!(IsClient && !Replaying && Board.Instance != null)) return true;
                // The local player's hammer: the host applies the hit to its zombie.
                if (_hammering && __instance != null && Puppets.TryGetValue(__instance.GetInstanceID(), out uint id))
                {
                    Session.SendToHost(Protocol.Build(Msg.ZombieHit, w =>
                    {
                        w.Write(id);
                        w.Write(theDamage);
                        w.Write((int)theDamageType);
                    }));
                    FusionCoopMod.Log.Msg($"[zombie] hammer hit #{id} for {theDamage}");
                }
                return false;
            }
        }

        // ---------- hammer ----------
        // Each window has its own hammer and cooldown (tools are not shared). A client's hammer swings
        // locally; the damage it deals to puppets is sent to the host, which damages its zombie.
        private static bool _hammering;

        [HarmonyPatch(typeof(Hammer), nameof(Hammer.Use))]
        internal static class HammerUsePatch
        {
            private static void Prefix() => _hammering = true;
            private static System.Exception Finalizer(System.Exception __exception)
            {
                _hammering = false;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Hammer), "AnimCrush")]
        internal static class HammerCrushPatch
        {
            private static void Prefix() => _hammering = true;
            private static System.Exception Finalizer(System.Exception __exception)
            {
                _hammering = false;
                return __exception;
            }
        }

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type != Msg.ZombieHit) return;
            var r = pkt.Reader();
            uint id = r.ReadUInt32();
            int damage = r.ReadInt32();
            var type = (DamageType)r.ReadInt32();
            if (!HostZombies.TryGetValue(id, out var z) || z == null || z.theStatus == ZombieStatus.Dying) return;
            try { z.TakeDamage(damage, null, type, (PlantType)(-1), false); }
            catch (System.Exception e) { FusionCoopMod.Log.Warning($"[zombie] P{from + 1} hammer hit failed: {e.Message}"); }
        }

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.Die))]
        internal static class DiePatch
        {
            private static bool Prefix() => !(IsClient && !Replaying && Board.Instance != null);

            // Host: tell clients right away instead of waiting for the next state message.
            private static void Postfix(Zombie __instance, int reason)
            {
                if (Session.Role != Role.Host || !Session.Ready || __instance == null) return;
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint id)) return;
                // Dying from a magnet pull (iron zombies) or a fire: sent with that event instead.
                if (_eventZombie != null && _eventZombie.Pointer == __instance.Pointer)
                {
                    _eventKilled = true;
                    return;
                }
                SendDied(id, reason);
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

        // Status effects (cold, freeze, butter) come from the host too: a client's own hit on a puppet
        // is ignored, and the host's effects are put on and taken off in ApplyEffects.
        [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetFreeze))]
        internal static class SetFreezePatch
        {
            private static bool Prefix(Zombie __instance) => Replaying || !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetCold))]
        internal static class SetColdPatch
        {
            private static bool Prefix(Zombie __instance) => Replaying || !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetMindControl))]
        internal static class SetMindControlPatch
        {
            private static bool Prefix(Zombie __instance) => Replaying || !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(Zombie), nameof(Zombie.Buttered))]
        internal static class ButteredPatch
        {
            private static bool Prefix(Zombie __instance) => Replaying || !IsPuppet(__instance);
        }

        // Magnet plants pulling metal items (armor, ladders, doors): the host decides, clients replay it
        // on the same puppet. Without this the host's lost armor reaches clients as damage and just falls.
        // ---------- ladders ----------
        // A ladder zombie touching a defensive plant starts its "put ladder" animation; at the animation
        // event (AnimSetLadder) the game puts a Ladder grid item on its ladderTarget plant. The host's is
        // replayed on clients by the same puppet; puppets don't start it on their own.

        [HarmonyPatch(typeof(LadderZombie), "OnTriggerEnter2D")]
        internal static class LadderEnterPatch
        {
            private static bool Prefix(LadderZombie __instance) => !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(LadderZombie), "OnTriggerStay2D")]
        internal static class LadderStayPatch
        {
            private static bool Prefix(LadderZombie __instance) => !IsPuppet(__instance);
        }

        [HarmonyPatch(typeof(LadderZombie), "AnimSetLadder")]
        internal static class SetLadderPatch
        {
            private static bool Prefix(LadderZombie __instance, out Plant __state)
            {
                var t = __instance.ladderTarget;
                // Only a plant without a ladder yet gets one.
                __state = t != null && t.Ladder == null ? t : null;
                return Replaying || !IsPuppet(__instance);
            }

            private static void Postfix(LadderZombie __instance, Plant __state)
            {
                if (Session.Role != Role.Host || !Session.Ready) return;
                var t = __state;
                if (t == null || t.Ladder == null) return;
                if (!HostIds.TryGetValue(__instance.GetInstanceID(), out uint id)) return;
                int col = t.thePlantColumn, row = t.thePlantRow;
                var type = t.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.LadderPlaced, w =>
                {
                    w.Write(id);
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                }));
            }
        }

        private static void ReplayLadder(Packet pkt)
        {
            var r = pkt.Reader();
            uint id = r.ReadUInt32();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            var plant = PlantSync.FindPlant(col, row, type);
            if (plant == null || plant.thePlantType != type)
            {
                FusionCoopMod.Log.Warning($"[zombie] ladder: no {type} at c{col} r{row}");
                return;
            }
            if (plant.Ladder != null) return;
            LadderZombie lz = null;
            if (ClientZombies.TryGetValue(id, out var rz) && rz.Zombie != null && !rz.Killed)
                lz = rz.Zombie.TryCast<LadderZombie>();
            if (lz != null)
            {
                lz.ladderTarget = plant;
                Replay(() => lz.AnimSetLadder());
            }
            // The zombie is gone here, or it did not place it: just put the ladder.
            if (plant.Ladder == null) Replay(() => GridItem.SetGridItem(col, row, GridItemType.Ladder, 0));
            FusionCoopMod.Log.Msg($"[zombie] ladder #{id} on {type} c{col} r{row}: {(plant.Ladder != null ? "ok" : "FAILED")}");
        }

        // Host: zombie a magnet is pulling / a fire is burning right now. Its death is sent with that
        // event instead of on its own, so clients replay the event before the death.
        private static Zombie _eventZombie;
        private static bool _eventKilled;

        [HarmonyPatch(typeof(Magnetshroom), "TryAttrackZombie")]
        internal static class MagnetAttractPatch
        {
            private static bool Prefix(Zombie zombie, ref bool __result, out Zombie __state)
            {
                __state = _eventZombie;
                if (Session.Role == Role.Host)
                {
                    _eventZombie = zombie;
                    _eventKilled = false;
                }
                if (Replaying || !IsPuppet(zombie)) return true;
                __result = false;
                return false;
            }

            private static void Postfix(Magnetshroom __instance, Zombie zombie, bool __result)
            {
                if (Session.Role != Role.Host || !Session.Ready || zombie == null) return;
                if (!HostIds.TryGetValue(zombie.GetInstanceID(), out uint id)) return;
                bool died = _eventKilled;
                _eventKilled = false;
                if (!__result)
                {
                    if (died) SendDied(id);
                    return;
                }
                int col = __instance.thePlantColumn, row = __instance.thePlantRow;
                var type = __instance.thePlantType;
                Session.SendToClients(Protocol.Build(Msg.MagnetAttract, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                    w.Write(id);
                    w.Write(died);
                }));
            }

            private static System.Exception Finalizer(System.Exception __exception, Zombie __state)
            {
                _eventZombie = __state;
                return __exception;
            }
        }

        private static void SendDied(uint id, int reason = 0) =>
            Session.SendToClients(Protocol.Build(Msg.ZombieDied, w =>
            {
                w.Write(id);
                w.Write(reason);
            }));

        private static void ReplayMagnet(Packet pkt)
        {
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            uint id = r.ReadUInt32();
            bool died = r.ReadBoolean();
            if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null || rz.Killed) return;
            try { PullOnClient(rz, col, row, type, id); }
            finally
            {
                // The host's zombie died from the pull: make sure ours is gone too.
                if (died) Kill(rz);
            }
        }

        private static void PullOnClient(Remote rz, int col, int row, PlantType type, uint id)
        {
            var plant = PlantSync.FindPlant(col, row, type);
            var magnet = plant != null && plant.thePlantType == type ? plant.TryCast<Magnetshroom>() : null;
            if (magnet == null)
            {
                FusionCoopMod.Log.Warning($"[zombie] magnet {type} c{col} r{row} not found for #{id}");
                return;
            }
            var z = rz.Zombie;
            bool ok = false;
            Replay(() => ok = magnet.TryAttrackZombie(z));
            FusionCoopMod.Log.Msg($"[zombie] magnet {type} c{col} r{row} pulled #{id}: {ok}");
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

        // LevelProgress.Update moves the bar towards theWave / theMaxWave (or the boss's health): down at once,
        // but up only a little each frame. A client that joins or rejoins mid-level gets the host's wave in one
        // go, so its bar would crawl up from 0 for a long while. Once per board, when the host's wave for this
        // board is known and waves have already passed, the bar is set full just before the game's update, which
        // then drops it to the right value in that frame. (Per board, not per bar: the bar may outlive a level.)
        private static int _snappedBoard;

        [HarmonyPatch(typeof(LevelProgress), "Update")]
        internal static class LevelProgressPatch
        {
            private static void Prefix(LevelProgress __instance)
            {
                var board = Board.Instance;
                if (!IsClient || board == null || _hostWave < 0 || board.GetInstanceID() != _clientBoardId) return;
                if (_snappedBoard == _clientBoardId || __instance.slider == null) return;
                _snappedBoard = _clientBoardId;
                if (_hostWave == 0) return; // here from the start: the bar follows on its own
                __instance.slider.value = __instance.slider.maxValue;
                FusionCoopMod.Log.Msg($"[zombie] progress bar caught up to wave {_hostWave}");
            }
        }

        // The game brings up the wave progress bar (and moves the level name) in NewZombieUpdate, on the step
        // from wave 0 to 1. A client that joins mid-level takes the host's wave without that step, so its bar
        // never came up. Once per board, past wave 0, the same is done here if the bar isn't up yet.
        private static int _barShownBoard;

        private static void EnsureWaveUi(Board board)
        {
            int id = board.GetInstanceID();
            if (board.theWave <= 0 || _barShownBoard == id) return;
            _barShownBoard = id;
            var ui = InGameUI.Instance;
            if (ui == null) return;
            var tag = board.boardTag;
            if (!tag.isLookStar && !tag.isBrick)
            {
                var bar = tag.playerShooting || tag.isTreasure ? ui.progress2 : ui.LevProgress;
                if (bar == null || bar.activeSelf) return; // the game did it
                bar.SetActive(true);
            }
            if (ui.LevelName2 != null) ui.LevelName2.gameObject.SetActive(false);
            if (ui.LevelName3 != null) ui.LevelName3.gameObject.SetActive(true);
            FusionCoopMod.Log.Msg($"[zombie] joined at wave {board.theWave}: wave progress bar brought up");
        }

        // The wave step (one run of NewZombieUpdate) doesn't happen when the game skips that code, e.g. while a
        // tutorial holds the waves back: then the client's wave would stay one behind. After a moment the
        // host's counters are simply taken.
        private static double _stepAskedAt;

        private static void CheckWaveStep()
        {
            if (!_allowWaveStep || Session.Now - _stepAskedAt < 1.5) return;
            _allowWaveStep = false;
            var board = Board.Instance;
            if (board == null || _hostWave < 0) return;
            board.theWave = _hostWave;
            board.timeUntilNextWave = _hostUntil;
            board.isHugeWave = _hostHuge;
            board.hugeWaveCountDown = _hostCountdown;
            FusionCoopMod.Log.Msg($"[zombie] wave step didn't run: took the host's wave {_hostWave}");
        }

        // ---------- host: send ----------

        private static double _nextReport;

        internal static void Update()
        {
            if (Session.Role == Role.Host) HostUpdate();
            else if (IsClient)
            {
                ClientSmooth();
                CheckWaveStep();
                if (Board.Instance != null && _hostWave >= 0 && !_allowWaveStep) EnsureWaveUi(Board.Instance);
            }
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
                    if (z.isAttacking) flags |= FlagAttacking;
                    int animHash = 0;
                    float animTime = 0, animSpeed = 1;
                    var anim = z.anim;
                    if (anim != null)
                    {
                        var st = anim.GetCurrentAnimatorStateInfo(0);
                        animHash = st.fullPathHash;
                        animTime = st.normalizedTime % 1f;
                        animSpeed = anim.speed;
                    }
                    w.Write(kv.Key);
                    w.Write((int)z.theZombieType);
                    w.Write((sbyte)z.theZombieRow);
                    w.Write(p.x);
                    w.Write(p.y);
                    w.Write((long)z.theHealth); // long since 4.0.5
                    w.Write(z.theFirstArmorHealth);
                    w.Write(z.theSecondArmorHealth);
                    w.Write(animHash);
                    w.Write(animTime);
                    w.Write(flags);
                    w.Write(EffectsOf(z));
                    w.Write(animSpeed);
                    w.Write(z.theSpeed);
                }
            });
            Session.SendToClients(frame);
        }

        // ---------- client: reconcile ----------

        private static readonly HashSet<uint> Seen = new();
        private static readonly List<uint> Stale = new();

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type == Msg.LadderPlaced)
            {
                ReplayLadder(pkt);
                return;
            }
            if (pkt.Type == Msg.MagnetAttract)
            {
                ReplayMagnet(pkt);
                return;
            }
            if (pkt.Type == Msg.ZombieDied)
            {
                var dr = pkt.Reader();
                uint id = dr.ReadUInt32();
                int reason = dr.ReadInt32();
                if (ClientZombies.TryGetValue(id, out var dead)) Kill(dead, reason);
                return;
            }
            if (HandleExtraMessage(pkt)) return;
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
                long health = r.ReadInt64();
                int armor1 = r.ReadInt32(), armor2 = r.ReadInt32();
                int animHash = r.ReadInt32();
                float animTime = r.ReadSingle();
                byte flags = r.ReadByte();
                byte effects = r.ReadByte();
                float animSpeed = r.ReadSingle();
                float moveSpeed = r.ReadSingle();
                bool dying = (flags & FlagDying) != 0;
                Seen.Add(id);

                if (!ClientZombies.TryGetValue(id, out var rz) || rz.Zombie == null)
                {
                    if (dying) continue; // don't spawn something that is already dying
                    var z = Spawn(cz, id, row, type, x, (flags & FlagMind) != 0);
                    if (z == null) continue;
                    rz = new Remote { Zombie = z };
                    ClientZombies[id] = rz;
                    Puppets[z.GetInstanceID()] = id;
                    var p = z.transform.position;
                    z.transform.position = new Vector3(x, y, p.z);
                }
                if (rz.Killed) continue;

                var zz = rz.Zombie;
                rz.Target = new Vector2(x, y);
                // The host's zombie changed lanes (MachineNut bounce, garlic, ...): row-based things such as
                // what it drops on death follow theZombieRow, not the position.
                if (row >= 0 && zz.theZombieRow != row && !dying) Replay(() => zz.ChangeRow(row, true));
                ApplyHealth(zz, health, armor1, armor2);
                if (dying)
                {
                    ApplyAnim(zz, animHash, animTime, animSpeed, (flags & FlagAttacking) != 0);
                    Kill(rz);
                    continue;
                }
                // Hypnotized on the host: the game's own mind control (look, direction, team).
                if ((flags & FlagMind) != 0 && !zz.isMindControlled) Replay(() => zz.SetMindControl(0));
                ApplyEffects(zz, effects);
                // After the effects: the host's animator speed wins over what they set.
                ApplyAnim(zz, animHash, animTime, animSpeed, (flags & FlagAttacking) != 0);
                ApplyMoveSpeed(zz, moveSpeed);
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
                _stepAskedAt = Session.Now;
                _hugeShownForNext = true;
            }
            else if (wave > _hostWave && board.theWave < wave)
            {
                board.theWave = wave - 1;
                board.timeUntilNextWave = 0f;
                board.isHugeWave = false;
                if (_hugeShownForNext)
                {
                    // The warning already played: run the step as if its countdown just ended, so the
                    // wave goes up without a second siren and the final-wave banner shows on the last
                    // wave. The game warns before waves 10, 20, ...; there, isHugeWave skips the siren
                    // and an overlong countdown wraps to 0 (= done).
                    _hugeShownForNext = false;
                    bool warnedWave = wave % 10 == 0;
                    board.isHugeWave = warnedWave;
                    board.hugeWaveCountDown = warnedWave ? 1e6f : 0f;
                }
                _allowWaveStep = true;
                _stepAskedAt = Session.Now;
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
        private static void ApplyHealth(Zombie z, long health, int armor1, int armor2)
        {
            long local = z.theHealth + z.theFirstArmorHealth + z.theSecondArmorHealth;
            long host = health + armor1 + armor2;
            if (host < local)
            {
                int dmg = (int)System.Math.Min(local - host, int.MaxValue);
                Replay(() => z.TakeDamage(dmg, null, DamageType.Normal, (PlantType)(-1), false));
            }
            if (z == null) return;
            z.theHealth = health;
            z.theFirstArmorHealth = armor1;
            z.theSecondArmorHealth = armor2;
        }

        // ---------- status effects ----------

        private const byte FxCold = 1, FxFreeze = 2, FxButter = 4;
        // Long enough to outlast the host's own effect; the host's state message takes it off.
        private const float EffectHold = 60f;

        private static byte EffectsOf(Zombie z)
        {
            byte fx = 0;
            if (z.HasBuff(EffectType.Cold)) fx |= FxCold;
            if (z.HasBuff(EffectType.Freeze) || z.HasBuff(EffectType.IceDoomFreeze)) fx |= FxFreeze;
            if (z.HasBuff(EffectType.Butter)) fx |= FxButter;
            return fx;
        }

        private static void ApplyEffects(Zombie z, byte host)
        {
            byte local = EffectsOf(z);
            if (local == host) return;
            Replay(() =>
            {
                if ((host & FxCold) != 0 && (local & FxCold) == 0) z.SetCold(EffectHold, 0, false);
                if ((host & FxFreeze) != 0 && (local & FxFreeze) == 0) z.SetFreeze(EffectHold, 0);
                if ((host & FxButter) != 0 && (local & FxButter) == 0) z.Buttered(EffectHold, true);
                if ((host & FxCold) == 0 && (local & FxCold) != 0) z.RemoveBuff(EffectType.Cold);
                if ((host & FxFreeze) == 0 && (local & FxFreeze) != 0)
                {
                    z.RemoveBuff(EffectType.Freeze);
                    z.RemoveBuff(EffectType.IceDoomFreeze);
                }
                if ((host & FxButter) == 0 && (local & FxButter) != 0) z.RemoveBuff(EffectType.Butter);
            });
        }

        // The walk animation's pace is the animator's "Speed" parameter, set from theSpeed (0 while frozen,
        // lower when cold) in Zombie.MoveUpdate, which puppets don't run.
        private static void ApplyMoveSpeed(Zombie z, float speed)
        {
            z.theSpeed = speed;
            if (z.anim != null) z.anim.SetFloat("Speed", speed);
        }

        // Eating is an attack state on layer 0 that the animator leaves again unless its isAttacking
        // parameter is on; puppets don't run AttackUpdate (which sets it), so the host's value is set here.
        private static readonly int IsAttackingParam = Animator.StringToHash("isAttacking");

        private static void ApplyAnim(Zombie z, int hash, float time, float speed, bool attacking)
        {
            var anim = z.anim;
            if (anim == null) return;
            if (anim.speed != speed) anim.speed = speed;
            if (z.hasIsAttackingParam && anim.GetBool(IsAttackingParam) != attacking) anim.SetBool(IsAttackingParam, attacking);
            if (hash == 0) return;
            if (anim.GetCurrentAnimatorStateInfo(0).fullPathHash == hash) return;
            if (!anim.HasState(0, hash)) return;
            anim.Play(hash, 0, time);
        }

        private static void Kill(Remote rz, int reason = 0)
        {
            if (rz.Killed || rz.Zombie == null) return;
            rz.Killed = true;
            var z = rz.Zombie;
            Puppets.Remove(z.GetInstanceID());
            if (z.theStatus == ZombieStatus.Dying) return; // already dying locally (magnet pull, fire)
            // Zombies a death lets out (a MachineNut's imps, ...) arrive from the host if its zombie let them out:
            // it may have died in a way that doesn't (a mower), while Die here always would.
            _killing = true;
            try
            {
                Replay(() => CallDie(z, reason));
            }
            finally
            {
                _killing = false;
            }
        }

        private static Zombie Spawn(CreateZombie cz, uint id, int row, ZombieType type, float x, bool mind)
        {
            Zombie z = null;
            _spawningId = id;
            try
            {
                Replay(() => z = mind
                    ? cz.SetZombieWithMindControl(row, type, x, false)
                    : cz.SetZombie(row, type, x, false));
            }
            finally { _spawningId = 0; }
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
                LayIceRoad(rz);
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
