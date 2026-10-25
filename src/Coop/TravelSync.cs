using System.Collections.Generic;
using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Odyssey (travel) buffs: the host chooses for everyone.
    //
    // A run's state is TravelMgr.Instance.data (TravelData): buffs, curses, investments, unlocked travel
    // plants, per-plant multipliers, difficulty. Between rounds Board.ShowChoice opens TravelBuffMenu (then
    // TravelStore); the invest menu opens at the start. Each window rolls its own options.
    // - Clients can't pick, refresh or buy in those menus; their menus close when the host's do.
    // - What the host gets from a menu (TravelMgr.GetNormalBuff / GetUltiBuff / GetDebuff / GetInvestBuff /
    //   UnlockPlant) is replayed on clients, so the side effects (sun, light, card costs, plant damage) happen too.
    // - The host's whole TravelData is sent when the battle starts and whenever a menu closes; clients take it.
    internal static class TravelSync
    {
        private enum Grant : byte { Normal, Ulti, Debuff, Invest, Unlock }
        private enum Menu : byte { Buff, Store, Invest }

        private static int _hostMenu;    // host: inside a menu action
        private static bool _replaying;  // client: running the host's grant / close
        private static bool _nextRound;  // client: running the host's TravelNextRound
        private static readonly double[] PendingClose = new double[3]; // client: host closed it, ours wasn't open yet

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;
        private static bool ClientBlocks => IsClient && !_replaying;

        private static bool Refuse()
        {
            if (!ClientBlocks) return true;
            Overlay.Toast("The host chooses the buffs");
            return false;
        }

        // ---------- client: no own choices ----------

        [HarmonyPatch(typeof(TravelBuffMenu), nameof(TravelBuffMenu.SelectBuff))]
        internal static class BuffSelectPatch
        {
            private static bool Prefix() => Refuse() && HostMenuEnter();
            private static System.Exception Finalizer(System.Exception __exception) => HostMenuLeave(__exception);
        }

        [HarmonyPatch(typeof(TravelBuffMenu), nameof(TravelBuffMenu.GiveUpBuff))]
        internal static class BuffGiveUpPatch
        {
            private static bool Prefix() => Refuse();
        }

        [HarmonyPatch(typeof(TravelStore), nameof(TravelStore.SelectBuff))]
        internal static class StoreSelectPatch
        {
            private static bool Prefix() => Refuse() && HostMenuEnter();
            private static System.Exception Finalizer(System.Exception __exception) => HostMenuLeave(__exception);
        }

        [HarmonyPatch(typeof(TravelStore), nameof(TravelStore.RefreshBuff))]
        internal static class StoreRefreshPatch
        {
            private static bool Prefix() => Refuse();
        }

        [HarmonyPatch(typeof(TravelInvestMenu), nameof(TravelInvestMenu.SelectBuff))]
        internal static class InvestSelectPatch
        {
            private static bool Prefix() => Refuse() && HostMenuEnter();
            private static System.Exception Finalizer(System.Exception __exception) => HostMenuLeave(__exception);
        }

        [HarmonyPatch(typeof(TravelInvestMenu), nameof(TravelInvestMenu.RefreshBuff))]
        internal static class InvestRefreshPatch
        {
            private static bool Prefix() => Refuse();
        }

        [HarmonyPatch(typeof(TravelRefresh), "OnMouseUpAsButton")]
        internal static class RefreshButtonPatch
        {
            private static bool Prefix() => Refuse();

            // The new options go out from RefeshOptions (OptionsPatch); the count left goes here.
            private static void Postfix(TravelRefresh __instance, bool __runOriginal)
            {
                if (!__runOriginal || !IsHost || __instance == null) return;
                int left = __instance.refreshTimes;
                bool inBuffMenu = __instance.GetComponentInParent<TravelBuffMenu>() != null;
                Session.SendToClients(Protocol.Build(Msg.TravelRerolls, w =>
                {
                    w.Write(inBuffMenu);
                    w.Write(left);
                }));
                FusionCoopMod.Log.Msg($"[travel] host rerolled ({left} left) -> clients");
            }
        }

        private static void ReceiveRerolls(Packet pkt)
        {
            var r = pkt.Reader();
            bool inBuffMenu = r.ReadBoolean();
            int left = r.ReadInt32();
            foreach (var b in Object.FindObjectsOfType<TravelRefresh>())
            {
                if (b == null || (b.GetComponentInParent<TravelBuffMenu>() != null) != inBuffMenu) continue;
                b.SetRefrashTimes(left);
                FusionCoopMod.Log.Msg($"[travel] host rerolled ({left} left)");
                return;
            }
        }

        private static bool HostMenuEnter()
        {
            _hostMenu++;
            return true;
        }

        private static System.Exception HostMenuLeave(System.Exception e)
        {
            if (_hostMenu > 0) _hostMenu--;
            return e;
        }

        // ---------- grants ----------

        private static void SendGrant(Grant kind, int id, bool upgrade)
        {
            if (!IsHost || _hostMenu <= 0) return;
            Session.SendToClients(Protocol.Build(Msg.TravelGrant, w =>
            {
                w.Write((byte)kind);
                w.Write(id);
                w.Write(upgrade);
            }));
            FusionCoopMod.Log.Msg($"[travel] host got {kind} {id}{(upgrade ? " (upgrade)" : "")} -> clients");
        }

        [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetNormalBuff))]
        internal static class GetNormalPatch
        {
            private static void Postfix(AdvBuff buff) => SendGrant(Grant.Normal, (int)buff, false);
        }

        [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetUltiBuff))]
        internal static class GetUltiPatch
        {
            private static void Postfix(UltiBuff buff, bool upgrade) => SendGrant(Grant.Ulti, (int)buff, upgrade);
        }

        [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetDebuff))]
        internal static class GetDebuffPatch
        {
            private static void Postfix(TravelDebuff buff) => SendGrant(Grant.Debuff, (int)buff, false);
        }

        [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetInvestBuff))]
        internal static class GetInvestPatch
        {
            private static void Postfix(InvestBuff buff) => SendGrant(Grant.Invest, (int)buff, false);
        }

        [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.UnlockPlant))]
        internal static class UnlockPatch
        {
            private static void Postfix(TravelUnlocks unlock) => SendGrant(Grant.Unlock, (int)unlock, false);
        }

        private static void ReplayGrant(Packet pkt)
        {
            var r = pkt.Reader();
            var kind = (Grant)r.ReadByte();
            int id = r.ReadInt32();
            bool upgrade = r.ReadBoolean();
            var mgr = TravelMgr.Instance;
            if (mgr == null) return;
            _replaying = true;
            try
            {
                switch (kind)
                {
                    case Grant.Normal: mgr.GetNormalBuff((AdvBuff)id); break;
                    case Grant.Ulti: mgr.GetUltiBuff((UltiBuff)id, upgrade); break;
                    case Grant.Debuff: mgr.GetDebuff((TravelDebuff)id); break;
                    case Grant.Invest: mgr.GetInvestBuff((InvestBuff)id); break;
                    case Grant.Unlock: mgr.UnlockPlant((TravelUnlocks)id); break;
                }
                FusionCoopMod.Log.Msg($"[travel] host's {kind} {id} applied");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[travel] {kind} {id} failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }

        // ---------- menus close with the host's ----------

        private static void HostClosed(Menu menu)
        {
            if (!IsHost) return;
            SendSnapshot();
            Session.SendToClients(Protocol.Build(Msg.TravelMenuClosed, w => w.Write((byte)menu)));
            FusionCoopMod.Log.Msg($"[travel] host closed {menu} -> clients");
        }

        [HarmonyPatch(typeof(TravelBuffMenu), nameof(TravelBuffMenu.Quit))]
        internal static class BuffQuitPatch
        {
            private static bool Prefix() => !ClientBlocks || Hint();
            private static void Postfix() => HostClosed(Menu.Buff);
        }

        [HarmonyPatch(typeof(TravelStore), nameof(TravelStore.Exit))]
        internal static class StoreExitPatch
        {
            private static bool Prefix() => !ClientBlocks || Hint();
            private static void Postfix() => HostClosed(Menu.Store);
        }

        [HarmonyPatch(typeof(TravelInvestMenu), nameof(TravelInvestMenu.Quit))]
        internal static class InvestQuitPatch
        {
            private static bool Prefix() => !ClientBlocks || Hint();
            private static void Postfix() => HostClosed(Menu.Invest);
        }

        private static bool Hint()
        {
            Overlay.Toast("The host closes this menu");
            return false;
        }

        // A menu that opens after the host already closed it closes right away.
        [HarmonyPatch(typeof(TravelBuffMenu), "Start")]
        internal static class BuffStartPatch
        {
            private static void Postfix(TravelBuffMenu __instance) => ClosePending(Menu.Buff, () => __instance.Quit());
        }

        [HarmonyPatch(typeof(TravelStore), "Start")]
        internal static class StoreStartPatch
        {
            private static void Postfix(TravelStore __instance) => ClosePending(Menu.Store, () => __instance.Exit());
        }

        [HarmonyPatch(typeof(TravelInvestMenu), "Start")]
        internal static class InvestStartPatch
        {
            private static void Postfix(TravelInvestMenu __instance) => ClosePending(Menu.Invest, () => __instance.Quit());
        }

        private static void ClosePending(Menu menu, System.Action close)
        {
            if (!IsClient || PendingClose[(int)menu] <= 0 || Session.Now - PendingClose[(int)menu] > 15) return;
            PendingClose[(int)menu] = 0;
            RunClose(menu, close);
        }

        private static void RunClose(Menu menu, System.Action close)
        {
            _replaying = true;
            try
            {
                close();
                FusionCoopMod.Log.Msg($"[travel] closed {menu} with the host");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[travel] closing {menu} failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }

        private static void ReplayClose(Packet pkt)
        {
            var menu = (Menu)pkt.Reader().ReadByte();
            switch (menu)
            {
                case Menu.Buff:
                {
                    var m = Object.FindObjectOfType<TravelBuffMenu>();
                    if (m != null) RunClose(menu, () => m.Quit());
                    else PendingClose[(int)menu] = Session.Now;
                    break;
                }
                case Menu.Store:
                {
                    var m = Object.FindObjectOfType<TravelStore>();
                    if (m != null) RunClose(menu, () => m.Exit());
                    else PendingClose[(int)menu] = Session.Now;
                    break;
                }
                case Menu.Invest:
                {
                    var m = Object.FindObjectOfType<TravelInvestMenu>();
                    if (m != null) RunClose(menu, () => m.Quit());
                    else PendingClose[(int)menu] = Session.Now;
                    break;
                }
            }
        }

        // ---------- next round ----------
        // StartNextRound either goes on in the same board or (config.realEndless off) saves the board, kills it
        // and enters the level again from that save. Every window would load its own save, so in co-op the
        // round always goes on in place.
        [HarmonyPatch(typeof(Board), nameof(Board.StartNextRound))]
        internal static class StartNextRoundPatch
        {
            private static void Prefix(out int __state)
            {
                __state = -1;
                var cfg = GameAPP.config;
                if (!Session.Ready || cfg == null || cfg.realEndless) return;
                __state = 0;
                cfg.realEndless = true;
            }

            private static System.Exception Finalizer(System.Exception __exception, int __state)
            {
                if (__state == 0 && GameAPP.config != null) GameAPP.config.realEndless = false;
                return __exception;
            }
        }

        // ---------- next round's lawn ----------
        // The next round picks a random lawn (MapData_cs.GetRandomTravelSceneType): seeded by level and round,
        // every window picks the same one; the global random state is put back afterwards.
        [HarmonyPatch]
        internal static class SceneSeedPatch
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var m in typeof(MapData_cs).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (m.Name == nameof(MapData_cs.GetRandomTravelSceneType)) yield return m;
            }

            private static void Prefix(out Random.State __state)
            {
                __state = Random.state;
                if (!Session.Ready) return;
                var data = TravelMgr.Instance != null ? TravelMgr.Instance.data : null;
                int round = data != null ? data.temp_round : 0;
                Random.InitState(unchecked(GameAPP.theBoardLevel * 7919 + round * 104729 + 17));
            }

            private static System.Exception Finalizer(System.Exception __exception, Random.State __state)
            {
                if (Session.Ready) Random.state = __state;
                return __exception;
            }
        }

        // ---------- the buff menu shows the host's options ----------
        // Each window rolls its own options (TravelBuffMenu.RefeshOptions): the host's are sent and a
        // client's menu shows those (its own buttons can't be clicked anyway).
        private static readonly List<(string type, int value)> HostOptions = new();

        [HarmonyPatch(typeof(TravelBuffMenu), nameof(TravelBuffMenu.RefeshOptions))]
        internal static class OptionsPatch
        {
            private static void Postfix(TravelBuffMenu __instance)
            {
                if (IsClient)
                {
                    if (!_replaying) ShowHostOptions(__instance);
                    return;
                }
                if (!IsHost || __instance == null || __instance.options == null) return;
                var list = new List<(string, int)>();
                foreach (var o in __instance.options)
                {
                    var b = o != null ? o.buff : null;
                    list.Add(b == null ? ("", 0) : (b.GetIl2CppType().AssemblyQualifiedName, System.Runtime.InteropServices.Marshal.ReadInt32(Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(b.Pointer))));
                }
                Session.SendToClients(Protocol.Build(Msg.TravelOptions, w =>
                {
                    w.Write((byte)list.Count);
                    foreach (var (t, v) in list)
                    {
                        w.Write(t);
                        w.Write(v);
                    }
                }));
                FusionCoopMod.Log.Msg($"[travel] host's buff options: {string.Join(", ", list.ConvertAll(o => o.Item2.ToString()))} -> clients");
            }
        }

        private static void ReceiveOptions(Packet pkt)
        {
            var r = pkt.Reader();
            HostOptions.Clear();
            for (int n = r.ReadByte(); n > 0; n--) HostOptions.Add((r.ReadString(), r.ReadInt32()));
            // every buff menu up (an earlier one may still be around)
            foreach (var menu in Object.FindObjectsOfType<TravelBuffMenu>())
                if (menu != null) ShowHostOptions(menu);
        }

        private static void ShowHostOptions(TravelBuffMenu menu)
        {
            if (menu == null || menu.options == null || HostOptions.Count == 0) return;
            _replaying = true;
            try
            {
                for (int i = 0; i < menu.options.Count && i < HostOptions.Count; i++)
                {
                    var (typeName, value) = HostOptions[i];
                    var option = menu.options[i];
                    if (option == null || typeName == "") continue;
                    var type = Il2CppSystem.Type.GetType(typeName);
                    if (type == null) continue;
                    // as RefeshOptions does on a reroll: drop the shown plant and set it again with the new buff
                    // (SetBuff only redraws the plant when asked to)
                    bool redraw = option.show != null;
                    if (redraw)
                    {
                        Object.Destroy(option.show);
                        option.show = null;
                    }
                    option.set = false;
                    option.SetBuff(Il2CppSystem.Enum.ToObject(type, value), redraw);
                }
                FusionCoopMod.Log.Msg($"[travel] showing the host's buff options ({HostOptions.Count} for {menu.options.Count} windows, menu {menu.GetInstanceID()})");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[travel] showing the host's options failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }

        // ---------- jigsaw pieces ----------
        // After the buff menus a jigsaw piece follows the mouse; a click puts it on a lawn cell (that cell's
        // BoardGrid.jigsaws, Board.SaveJiasawToList), then a click on the screen goes on (Board.TravelNextRound).
        // The host does both; clients don't handle input there and place the host's piece, then go on with it.
        [HarmonyPatch(typeof(JigsawManager), "Update")]
        internal static class JigsawInputPatch
        {
            private static bool Prefix() => !IsClient;
        }

        [HarmonyPatch(typeof(Board), nameof(Board.SaveJiasawToList))]
        internal static class JigsawPlacedPatch
        {
            private static void Prefix()
            {
                if (!IsHost) return;
                var clip = JigsawManager.Instance != null ? JigsawManager.Instance.jigsawOnMouse : null;
                if (clip == null) return;
                int col = clip.theColumn, row = clip.theRow;
                var type = clip.jigsawType;
                Session.SendToClients(Protocol.Build(Msg.JigsawPlaced, w =>
                {
                    w.Write((sbyte)col);
                    w.Write((sbyte)row);
                    w.Write((int)type);
                }));
                FusionCoopMod.Log.Msg($"[travel] host put a {type} jigsaw on c{col} r{row} -> clients");
            }
        }

        [HarmonyPatch(typeof(Board), nameof(Board.TravelNextRound))]
        internal static class TravelNextRoundPatch
        {
            private static bool Prefix()
            {
                // only the host's: closing a menu with the host (_replaying) also leads here, and a second
                // TravelNextRound would build a second board and card chooser
                if (IsClient) return _nextRound;
                if (IsHost)
                {
                    Session.SendToClients(Protocol.Build(Msg.TravelNextRound));
                    FusionCoopMod.Log.Msg("[travel] host goes on to the next round -> clients");
                }
                return true;
            }
        }

        private static void ReplayJigsaw(Packet pkt)
        {
            var r = pkt.Reader();
            int col = r.ReadSByte(), row = r.ReadSByte();
            var type = (JigsawType)r.ReadInt32();
            var board = Board.Instance;
            var grid = board != null && board.gridSystem != null ? board.gridSystem.GetGrid(col, row) : null;
            if (grid == null || grid.jigsaws == null)
            {
                FusionCoopMod.Log.Warning($"[travel] no lawn cell c{col} r{row} for the host's jigsaw");
                return;
            }
            grid.jigsaws.Add(type);
            board.SaveJiasawToList();
            var jm = JigsawManager.Instance;
            if (jm != null && jm.jigsawOnMouse != null)
            {
                Object.Destroy(jm.jigsawOnMouse.gameObject);
                jm.jigsawOnMouse = null;
            }
            FusionCoopMod.Log.Msg($"[travel] host's {type} jigsaw put on c{col} r{row}");
        }

        private static void ReplayNextRound()
        {
            var board = Board.Instance;
            if (board == null) return;
            _replaying = true;
            _nextRound = true;
            try
            {
                board.TravelNextRound();
                if (JigsawManager.Instance != null) JigsawManager.Instance.enabled = false;
                FusionCoopMod.Log.Msg("[travel] next round with the host");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[travel] next round failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
                _nextRound = false;
            }
        }

        // ---------- the whole TravelData ----------

        // Host: when the battle starts (LevelSync) and after each menu.
        internal static void SendSnapshot()
        {
            if (!IsHost) return;
            var data = TravelMgr.Instance != null ? TravelMgr.Instance.data : null;
            if (data == null) return;
            Session.SendToClients(Protocol.Build(Msg.TravelData, w =>
            {
                WriteList(w, data.unlockedPlants, v => (int)v);
                WriteList(w, data.advBuffs, v => (int)v);
                WriteList(w, data.travelDebuffs, v => (int)v);
                WriteList(w, data.unlockedWeaks, v => (int)v);
                WriteList(w, data.investmentBuffs, v => (int)v);
                WriteList(w, data.ultiBuffs, v => (int)v);
                WriteList(w, data.ultiBuffs_lv2, v => (int)v);
                WriteList(w, data.investBuffs, v => (int)v);
                WriteDict(w, data.Speed_plant);
                WriteDict(w, data.Damage_plant);
                WriteDict(w, data.Health_plant);
                w.Write(data.difficulty);
                w.Write(data.shieldCount);
                w.Write(data.extraRefreshCount);
                w.Write(data.curseStoreVisitCount);
                w.Write(data.moreLuckyStrike);
                w.Write(data.damageAmplification);
                w.Write(data.pointsBonus);
                w.Write(data.usePoint);
                w.Write(data.hideActive);
                w.Write(data.ArmorPiercing);
                w.Write(data.jalaMore);
                w.Write((int)data.routeBossType);
                w.Write((int)data.travelDifficulty);
                w.Write((int)data.travelProcess);
                w.Write(data.invest_challenge);
                w.Write(data.temp_round);
                w.Write(data.curseMode);
                w.Write(data.map);
                w.Write(Board.Instance != null ? Board.Instance.thePoints : -1f); // the store's points
            }));
            FusionCoopMod.Log.Msg($"[travel] sent the host's travel data ({data.advBuffs?.Count ?? 0} buffs, {data.ultiBuffs?.Count ?? 0} ultimates, {data.travelDebuffs?.Count ?? 0} curses)");
        }

        private static void ApplySnapshot(Packet pkt)
        {
            var data = TravelMgr.Instance != null ? TravelMgr.Instance.data : null;
            if (data == null)
            {
                FusionCoopMod.Log.Warning("[travel] host's travel data arrived but there is none here");
                return;
            }
            var r = pkt.Reader();
            ReadList(r, data.unlockedPlants, v => (TravelUnlocks)v);
            ReadList(r, data.advBuffs, v => (AdvBuff)v);
            ReadList(r, data.travelDebuffs, v => (TravelDebuff)v);
            ReadList(r, data.unlockedWeaks, v => (PlantType)v);
            ReadList(r, data.investmentBuffs, v => (InvestBuff)v);
            ReadList(r, data.ultiBuffs, v => (UltiBuff)v);
            ReadList(r, data.ultiBuffs_lv2, v => (UltiBuff)v);
            ReadList(r, data.investBuffs, v => (InvestBuff)v);
            ReadDict(r, data.Speed_plant);
            ReadDict(r, data.Damage_plant);
            ReadDict(r, data.Health_plant);
            data.difficulty = r.ReadInt32();
            data.shieldCount = r.ReadInt32();
            data.extraRefreshCount = r.ReadInt32();
            data.curseStoreVisitCount = r.ReadInt32();
            data.moreLuckyStrike = r.ReadSingle();
            data.damageAmplification = r.ReadSingle();
            data.pointsBonus = r.ReadSingle();
            data.usePoint = r.ReadBoolean();
            data.hideActive = r.ReadBoolean();
            data.ArmorPiercing = r.ReadSingle();
            data.jalaMore = r.ReadSingle();
            data.routeBossType = (ZombieType)r.ReadInt32();
            data.travelDifficulty = (TravelDifficulty)r.ReadInt32();
            data.travelProcess = (TravelProcess)r.ReadInt32();
            data.invest_challenge = r.ReadBoolean();
            data.temp_round = r.ReadInt32();
            data.curseMode = r.ReadBoolean();
            data.map = r.ReadInt32();
            float points = r.ReadSingle();
            if (points >= 0 && Board.Instance != null) Board.Instance.thePoints = points;
            try { TravelMgr.Instance.UpdateSynergies(); }
            catch (System.Exception e) { FusionCoopMod.Log.Warning($"[travel] synergy update failed: {e.Message}"); }
            FusionCoopMod.Log.Msg($"[travel] took the host's travel data ({data.advBuffs?.Count ?? 0} buffs, {data.ultiBuffs?.Count ?? 0} ultimates, {data.travelDebuffs?.Count ?? 0} curses)");
        }

        private static void WriteList<T>(System.IO.BinaryWriter w, Il2CppSystem.Collections.Generic.List<T> list, System.Func<T, int> id)
        {
            int n = list != null ? list.Count : 0;
            w.Write((ushort)n);
            for (int i = 0; i < n; i++) w.Write(id(list[i]));
        }

        private static void ReadList<T>(System.IO.BinaryReader r, Il2CppSystem.Collections.Generic.List<T> list, System.Func<int, T> make)
        {
            int n = r.ReadUInt16();
            list?.Clear();
            for (int i = 0; i < n; i++)
            {
                var v = make(r.ReadInt32());
                list?.Add(v);
            }
        }

        private static void WriteDict(System.IO.BinaryWriter w, Il2CppSystem.Collections.Generic.Dictionary<PlantType, float> dict)
        {
            var items = new List<(int, float)>();
            if (dict != null)
                foreach (var kv in dict) items.Add(((int)kv.Key, kv.Value));
            w.Write((ushort)items.Count);
            foreach (var (k, v) in items)
            {
                w.Write(k);
                w.Write(v);
            }
        }

        private static void ReadDict(System.IO.BinaryReader r, Il2CppSystem.Collections.Generic.Dictionary<PlantType, float> dict)
        {
            int n = r.ReadUInt16();
            dict?.Clear();
            for (int i = 0; i < n; i++)
            {
                var k = (PlantType)r.ReadInt32();
                float v = r.ReadSingle();
                if (dict != null) dict[k] = v;
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            switch (pkt.Type)
            {
                case Msg.TravelGrant: ReplayGrant(pkt); break;
                case Msg.TravelMenuClosed: ReplayClose(pkt); break;
                case Msg.TravelData: ApplySnapshot(pkt); break;
                case Msg.JigsawPlaced: ReplayJigsaw(pkt); break;
                case Msg.TravelNextRound: ReplayNextRound(); break;
                case Msg.TravelOptions: ReceiveOptions(pkt); break;
                case Msg.TravelRerolls: ReceiveRerolls(pkt); break;
            }
        }
    }
}
