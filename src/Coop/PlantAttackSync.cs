using System.Collections.Generic;
using System.Reflection;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Plant attacks are decided by the host.
    //
    // A plant's PlantShootUpdate waits for a countdown that is reset to a random length, then (if it sees
    // a target) starts its attack by setting an animator trigger ("shoot", or e.g. "shoot2" for the
    // corn-pult's butter, picked at random) or bool; the animation then throws the projectile. Every
    // window would time and pick differently, so the host's choices are shown everywhere: the host sends
    // each animator change made inside PlantShootUpdate, clients skip their own update and apply the
    // host's. Clients' projectiles don't change puppet zombies anyway: damage and effects come from the host.
    //
    // Only versions whose visible work is animator triggers/bools are taken over. Left local for now:
    // LaserUmbrella, CaltropKelp_water (attack directly), HypnoCattailGirl (Spine), UltimateBigSniper
    // (aim angle), UltimateGatlingBlover (interval state), ScaredFume (Animator.Play).
    internal static class PlantAttackSync
    {
        // Classes whose own PlantShootUpdate is taken over. Overrides that call the base version
        // (MelonFume, SuperSeaShroom, ThreeMine, UltimateDoomScared) are covered through it.
        private static readonly string[] ShootClasses =
        {
            "Plant", "Thrower", "Cornpult", "SquashCorn", "AbyssSwordStar", "AllPeater", "BigGatling",
            "Cactus", "EndoFlameGirl", "LanternPea", "LanternStarFruit", "NutFume", "SeaCactus",
            "SniperPea", "StarScaredyShroom", "SuperSnowGatling", "SuperThreeGatling", "ThornsAloes",
            "ThreeSpike", "UltimateBigGatling", "UltimateFume", "UltimateHypno", "UltimatePoisonFume",
            "UltimateStar", "UltimateWinterMelon", "WaterBallista",
        };

        private const byte KindTrigger = 0, KindBoolFalse = 1, KindBoolTrue = 2;

        private static Plant _shooter; // host: the plant whose PlantShootUpdate is running

        // Host: last bool sent per (plant, name), so per-frame SetBool calls are sent on change
        // (and again after a while, for plants a client placed late).
        private static readonly Dictionary<(int, string), (bool value, double at)> SentBools = new();

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        internal static void Reset() => SentBools.Clear();

        private static bool ClientBattle =>
            Session.Role == Role.Client && Session.Ready && Board.Instance != null &&
            (InitBoard.Instance == null || InitBoard.Instance.ready);

        [HarmonyPatch]
        internal static class ShootUpdatePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in ShootClasses)
                {
                    var type = typeof(Plant).Assembly.GetType("Il2Cpp." + name);
                    var m = type != null ? AccessTools.DeclaredMethod(type, "PlantShootUpdate") : null;
                    if (m != null) yield return m;
                    else FusionCoopMod.Log.Warning($"[attack] {name}.PlantShootUpdate not found");
                }
            }

            // __state keeps the outer plant when an override calls the base version.
            private static bool Prefix(Plant __instance, out Plant __state)
            {
                __state = _shooter;
                if (ClientBattle) return false;
                if (Session.Role == Role.Host && Session.Ready) _shooter = __instance;
                return true;
            }

            private static System.Exception Finalizer(System.Exception __exception, Plant __state)
            {
                _shooter = __state;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), typeof(string))]
        internal static class SetTriggerPatch
        {
            private static void Prefix(Animator __instance, string name)
            {
                var p = _shooter;
                if (p == null || p.anim == null || p.anim.Pointer != __instance.Pointer) return;
                Send(p, name, KindTrigger);
            }
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetBool), typeof(string), typeof(bool))]
        internal static class SetBoolPatch
        {
            private static void Prefix(Animator __instance, string name, bool value)
            {
                var p = _shooter;
                if (p == null || p.anim == null || p.anim.Pointer != __instance.Pointer) return;
                var key = (p.GetInstanceID(), name);
                double now = Session.Now;
                if (SentBools.TryGetValue(key, out var last) && last.value == value && now - last.at < 2) return;
                SentBools[key] = (value, now);
                Send(p, name, value ? KindBoolTrue : KindBoolFalse);
            }
        }

        private static void Send(Plant p, string name, byte kind)
        {
            int col = p.thePlantColumn, row = p.thePlantRow;
            var type = p.thePlantType;
            Session.SendToClients(Protocol.Build(Msg.PlantShoot, w =>
            {
                w.Write((sbyte)col);
                w.Write((sbyte)row);
                w.Write((int)type);
                w.Write(kind);
                w.Write(name ?? "");
            }));
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.PlantShoot || Board.Instance == null) return;
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (PlantType)r.ReadInt32();
            byte kind = r.ReadByte();
            string name = r.ReadString();
            var p = PlantSync.FindPlant(col, row, type);
            if (p == null || p.thePlantType != type || p.anim == null || name.Length == 0) return;
            if (kind != KindTrigger)
            {
                p.anim.SetBool(name, kind == KindBoolTrue);
                return;
            }
            p.anim.SetTrigger(name);
            // SquashCorn shows its butter when it picks the butter throw.
            if (name == "shoot2")
            {
                var squash = p.TryCast<SquashCorn>();
                if (squash != null && squash.butter != null) squash.butter.SetActive(true);
            }
        }
    }
}
