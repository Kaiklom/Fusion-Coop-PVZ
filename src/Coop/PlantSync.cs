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
            internal int Col, Row;
        }

        private static readonly Dictionary<uint, Pending> PendingRequests = new();
        private static uint _nextSeq = 1;

        // Set while the local player's TryToSetPlantByCard runs, so SetPlant can report what it made.
        private static bool _capturing;
        private static Plant _captured;
        // Set while we replay a remote action, so nothing is re-sent.
        internal static bool Replaying;
        // Host: above 0 while plants leave the board in a way clients replay by itself (FreezeSync).
        internal static int SuppressDeaths;

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

        private static bool IsWalker(Plant p) => p != null && p.TryCast<Plantwalker>() != null;

        // The walking plant of that type in that row nearest to the column.
        private static Plant FindWalker(int row, PlantType type, int col)
        {
            var list = Board.Instance != null && Board.Instance.boardEntity != null ? Board.Instance.boardEntity.plantArray : null;
            if (list == null) return null;
            Plant best = null;
            int bestDist = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null || p.thePlantType != type || p.thePlantRow != row || !IsWalker(p)) continue;
                int d = System.Math.Abs(p.thePlantColumn - col);
                if (d < bestDist)
                {
                    best = p;
                    bestDist = d;
                }
            }
            return best;
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

        // Odyssey: from Board.TravelNextRound until the next round's board is up (LevelSync.NewRound). Each
        // window builds that board from its own save with SetPlant (lily pads, pots, the plants on them), so a
        // client must not block those, and the host doesn't send them as plants made during battle.
        private static double _roundBuildUntil;
        private static bool RoundBuilding => Session.Now < _roundBuildUntil;
        internal static void BeginRoundBuild() => _roundBuildUntil = Session.Now + 10;
        internal static void EndRoundBuild() => _roundBuildUntil = 0;

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
                    PendingRequests[seq] = new Pending { Plant = plant, SunPaid = paid, Col = col, Row = row };
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
                if (!BattleRunning || RoundBuilding) return true; // level setup before the battle stays local
                __result = null;
                return false;
            }

            private static void Postfix(Plant __result, int newColumn, int newRow, PlantType theSeedType)
            {
                if (__result == null) return;
                if (Session.Role == Role.Client) Touch(newColumn, newRow);
                if (_capturing)
                {
                    _captured = __result;
                    return;
                }
                if (_setPlantDepth != 1) return;
                if (Replaying || Session.Role != Role.Host || !Session.Ready || !BattleRunning || RoundBuilding) return;
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

        // ---------- plant health and board check ----------

        private static double _nextHealthSend;

        // Host, 5 Hz: every plant on the board (cell, type, owner, health).
        private static int _boardId;

        // Host: owners by (cell, type) on the current board. An Odyssey round builds a new board from the save,
        // so its plants are new objects: they take the owner of the same plant on the old board.
        private static readonly Dictionary<long, byte> LastOwners = new();
        private static readonly Dictionary<long, byte> CarryOwners = new();
        private static double _carryUntil;

        private static long PlantKey(int col, int row, PlantType type) => ((long)CellKey(col, row) << 32) | (uint)(int)type;

        internal static void Update()
        {
            // New board (level/restart/Odyssey round): the cell owners of the old one no longer apply.
            int bid = Board.Instance != null ? Board.Instance.GetInstanceID() : 0;
            if (bid != _boardId)
            {
                _boardId = bid;
                CellOwner.Clear();
                Mismatch.Clear();
                Touched.Clear();
                FixedAt.Clear();
                WalkerMismatch.Clear();
                _battleSince = 0;
                CarryOwners.Clear();
                if (Session.Role == Role.Host && bid != 0)
                {
                    foreach (var kv in LastOwners) CarryOwners[kv.Key] = kv.Value;
                    _carryUntil = Session.Now + 15;
                }
                LastOwners.Clear();
            }

            if (Session.Role != Role.Host || !Session.Ready || Session.Names.Count < 2) return;
            var board = Board.Instance;
            if (board == null || board.boardEntity == null || Session.Now < _nextHealthSend) return;
            _nextHealthSend = Session.Now + 0.2;
            var list = board.boardEntity.plantArray;
            if (list == null) return;
            var plants = new List<Plant>();
            bool carrying = CarryOwners.Count > 0 && Session.Now < _carryUntil;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                plants.Add(p);
                if (carrying && OwnerOf(p) == 255 &&
                    CarryOwners.Remove(PlantKey(p.thePlantColumn, p.thePlantRow, p.thePlantType), out var carried))
                    SetOwner(p, carried);
            }
            if (!carrying) CarryOwners.Clear();
            LastOwners.Clear();
            foreach (var p in plants)
            {
                byte o = OwnerOf(p);
                if (o != 255) LastOwners[PlantKey(p.thePlantColumn, p.thePlantRow, p.thePlantType)] = o;
            }
            bool battle = LevelSync.HostBattle && BattleRunning;
            Session.SendToClients(Protocol.Build(Msg.PlantState, w =>
            {
                w.Write(battle);
                w.Write((int)GameAPP.theBoardType);
                w.Write(GameAPP.theBoardLevel);
                w.Write((ushort)plants.Count);
                foreach (var p in plants)
                {
                    w.Write((sbyte)p.thePlantColumn);
                    w.Write((sbyte)p.thePlantRow);
                    w.Write((int)p.thePlantType);
                    w.Write(OwnerOf(p));
                    w.Write(p.thePlantHealth);
                    w.Write(p.thePlantMaxHealth);
                    w.Write(p.theShieldHealth);
                    w.Write(p.attributeCount);
                    w.Write(p.AttributeCountdown);
                    w.Write(p.AttributeCountdownMaximum);
                }
            }));
        }

        private struct HostPlant
        {
            internal int Col, Row;
            internal PlantType Type;
            internal byte Owner;
        }

        private static readonly List<HostPlant> HostPlants = new();

        // Client: damage through the game's TakeDamage (hit flash, effects), copy the host's numbers, then
        // redraw the damage look. Wall-nut-like plants pick their bitten sprite from health/maxHealth in
        // ReplaceSprite, which their own TakeDamage calls but the base Plant.TakeDamage we reach does not.
        // Then the board check (Reconcile).
        private static void ApplyPlantState(System.IO.BinaryReader r)
        {
            bool hostBattle = r.ReadBoolean();
            int boardType = r.ReadInt32(), boardLevel = r.ReadInt32();
            int count = r.ReadUInt16();
            HostPlants.Clear();
            for (int i = 0; i < count; i++)
            {
                int col = r.ReadSByte(), row = r.ReadSByte();
                var type = (PlantType)r.ReadInt32();
                byte owner = r.ReadByte();
                int health = r.ReadInt32(), maxHealth = r.ReadInt32(), shield = r.ReadInt32();
                int attrCount = r.ReadInt32();
                float countdown = r.ReadSingle(), countdownMax = r.ReadSingle();
                HostPlants.Add(new HostPlant { Col = col, Row = row, Type = type, Owner = owner });
                var p = FindPlant(col, row, type);
                if (p == null || p.thePlantType != type) continue;
                ApplyAttributes(p, attrCount, countdown, countdownMax);
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
                try
                {
                    p.ReplaceSprite();
                    p.UpdateText(); // the health bar and its numbers only follow the game's own health changes
                }
                catch (System.Exception e) { FusionCoopMod.Log.Warning($"[plant] sprite update failed: {e.Message}"); }
            }
            bool sameLevel = boardType == (int)GameAPP.theBoardType && boardLevel == GameAPP.theBoardLevel;
            if (hostBattle && sameLevel) Reconcile();
        }

        // The plant's own counters: attributeCount (e.g. the Apeacalypse minigun's energy from the damage it
        // takes) and the countdown to its special move (the blue bar under the health bar). A countdown runs
        // on both machines, so it is only corrected when it is clearly off.
        private static void ApplyAttributes(Plant p, int count, float countdown, float countdownMax)
        {
            try
            {
                if (p.attributeCount != count) p.attributeCount = count;
                if (countdownMax <= 0f) return;
                if (System.Math.Abs(p.AttributeCountdown - countdown) > 0.5f ||
                    System.Math.Abs(p.AttributeCountdownMaximum - countdownMax) > 0.01f)
                    p.SetAttributeCountdown(countdown, countdownMax);
            }
            catch (System.Exception e) { FusionCoopMod.Log.Warning($"[plant] counter update failed: {e.Message}"); }
        }

        // ---------- board check ----------
        // Client: a cell whose plants (types, counts, owners) stay different from the host's for a second is
        // made like the host's: extra plants are removed, missing ones placed (supports such as lily pads and
        // pots first, by retrying until nothing more fits), owners copied. Whatever made the boards differ
        // (a blocked plant effect, an Odyssey round built from each window's own save, two players on one
        // cell), the client's board comes back to the host's instead of staying wrong.
        // Cells something just happened in (planting, replays, deaths) wait, so messages on their way land first.
        private const double MismatchDelay = 1.0;  // a difference must last this long
        private const double TouchQuiet = 1.5;     // no plant made or killed in the cell for this long
        private const double FixCooldown = 3.0;    // after a fix, the cell waits this long before the next
        private const double BattleSettle = 2.0;   // nothing is fixed in a battle's first seconds

        private static readonly Dictionary<int, double> Mismatch = new(); // cell -> since when it differs
        private static readonly Dictionary<int, double> Touched = new();  // cell -> last plant made/killed there
        private static readonly Dictionary<int, double> FixedAt = new();  // cell -> last fix
        private static double _battleSince;

        private static void Touch(int col, int row) => Touched[CellKey(col, row)] = Session.Now;

        private sealed class Cell
        {
            internal int Col, Row;
            internal readonly List<HostPlant> Host = new();
            internal readonly List<Plant> Local = new();
        }

        private static readonly Dictionary<int, Cell> Cells = new();

        private static Cell CellAt(int col, int row)
        {
            int key = CellKey(col, row);
            if (!Cells.TryGetValue(key, out var c)) Cells[key] = c = new Cell { Col = col, Row = row };
            return c;
        }

        private static void Reconcile()
        {
            double now = Session.Now;
            var board = Board.Instance;
            if (!LevelSync.ClientInHostBattle || !BattleRunning || board == null || board.boardEntity == null)
            {
                _battleSince = 0;
                Mismatch.Clear();
                return;
            }
            if (_battleSince == 0) _battleSince = now;
            if (now - _battleSince < BattleSettle) return;
            var list = board.boardEntity.plantArray;
            if (list == null) return;

            Cells.Clear();
            LocalWalkers.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                if (IsWalker(p))
                {
                    WalkerTypes.Add(p.thePlantType);
                    LocalWalkers.Add(p);
                }
                else CellAt(p.thePlantColumn, p.thePlantRow).Local.Add(p);
            }
            foreach (var h in HostPlants)
                if (!WalkerTypes.Contains(h.Type)) CellAt(h.Col, h.Row).Host.Add(h);
            ReconcileWalkers(now);
            var waiting = new HashSet<int>();
            foreach (var pr in PendingRequests.Values) waiting.Add(CellKey(pr.Col, pr.Row));

            int fixedCells = 0;
            foreach (var kv in Cells)
            {
                int key = kv.Key;
                var cell = kv.Value;
                if (Same(cell))
                {
                    Mismatch.Remove(key);
                    continue;
                }
                if (!Mismatch.TryGetValue(key, out var since))
                {
                    Mismatch[key] = now;
                    continue;
                }
                if (now - since < MismatchDelay || waiting.Contains(key)) continue;
                if (Touched.TryGetValue(key, out var t) && now - t < TouchQuiet) continue;
                if (FixedAt.TryGetValue(key, out var f) && now - f < FixCooldown) continue;
                if (fixedCells >= 12) break; // the rest next time
                FixCell(cell);
                FixedAt[key] = now;
                Mismatch.Remove(key);
                fixedCells++;
            }
            // cells that are no longer on either board
            Dead.Clear();
            foreach (var key in Mismatch.Keys)
                if (!Cells.ContainsKey(key)) Dead.Add(key);
            foreach (var key in Dead) Mismatch.Remove(key);
        }

        // Walking plants change cells as they go, so they are compared per row and type instead: one the host no
        // longer has (it used itself up) and still walking here after a while is removed.
        private const double WalkerDelay = 2.0;
        private static readonly HashSet<PlantType> WalkerTypes = new();
        private static readonly List<Plant> LocalWalkers = new();
        private static readonly Dictionary<long, double> WalkerMismatch = new(); // (row, type) -> since when

        private static void ReconcileWalkers(double now)
        {
            if (LocalWalkers.Count == 0)
            {
                WalkerMismatch.Clear();
                return;
            }
            var groups = new Dictionary<long, List<Plant>>();
            foreach (var p in LocalWalkers)
            {
                long key = PlantKey(0, p.thePlantRow, p.thePlantType);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new List<Plant>();
                g.Add(p);
            }
            foreach (var kv in groups)
            {
                var first = kv.Value[0];
                int row = first.thePlantRow;
                var type = first.thePlantType;
                var hostCols = new List<int>();
                foreach (var h in HostPlants)
                    if (h.Row == row && h.Type == type) hostCols.Add(h.Col);
                int extra = kv.Value.Count - hostCols.Count;
                if (extra <= 0)
                {
                    WalkerMismatch.Remove(kv.Key);
                    continue;
                }
                if (!WalkerMismatch.TryGetValue(kv.Key, out var since))
                {
                    WalkerMismatch[kv.Key] = now;
                    continue;
                }
                if (now - since < WalkerDelay) continue;
                WalkerMismatch.Remove(kv.Key);
                // the ones farthest from any of the host's are extra
                kv.Value.Sort((a, b) => Dist(b, hostCols).CompareTo(Dist(a, hostCols)));
                for (int i = 0; i < extra; i++)
                {
                    var p = kv.Value[i];
                    Owners.Remove(p.GetInstanceID());
                    Replaying = true;
                    try { p.Die(Plant.DieReason.Default); }
                    catch (System.Exception e) { FusionCoopMod.Log.Warning($"[plant] fix: removing walker {type} failed: {e.Message}"); }
                    finally { Replaying = false; }
                }
                FusionCoopMod.Log.Msg($"[plant] fixed row {row}: removed {extra} walking {type} the host no longer has");
            }
        }

        private static int Dist(Plant p, List<int> cols)
        {
            int best = int.MaxValue;
            foreach (int c in cols) best = System.Math.Min(best, System.Math.Abs(p.thePlantColumn - c));
            return best;
        }

        // Same plant types (with counts) and owners in the cell on both boards.
        private static bool Same(Cell cell)
        {
            if (cell.Host.Count != cell.Local.Count) return false;
            var left = new List<HostPlant>(cell.Host);
            foreach (var p in cell.Local)
            {
                int i = left.FindIndex(h => h.Type == p.thePlantType);
                if (i < 0) return false;
                if (left[i].Owner != OwnerOf(p)) return false;
                left.RemoveAt(i);
            }
            return true;
        }

        private static void FixCell(Cell cell)
        {
            int col = cell.Col, row = cell.Row;
            var missing = new List<HostPlant>(cell.Host);
            var extra = new List<Plant>();
            foreach (var p in cell.Local)
            {
                int i = missing.FindIndex(h => h.Type == p.thePlantType);
                if (i < 0)
                {
                    extra.Add(p);
                    continue;
                }
                var h = missing[i];
                missing.RemoveAt(i);
                if (OwnerOf(p) == h.Owner) continue;
                if (h.Owner == 255) Owners.Remove(p.GetInstanceID());
                else SetOwner(p, h.Owner);
            }

            var removed = new List<string>();
            foreach (var p in extra)
            {
                if (p == null) continue;
                removed.Add(p.thePlantType.ToString());
                Owners.Remove(p.GetInstanceID());
                Replaying = true;
                try { p.Die(Plant.DieReason.Default); }
                catch (System.Exception e) { FusionCoopMod.Log.Warning($"[plant] fix: removing {p.thePlantType} failed: {e.Message}"); }
                finally { Replaying = false; }
            }

            // A plant on a lily pad or in a pot only fits once its support is there: retry while anything fits,
            // then place the rest freely (the host has them, so they belong there).
            var added = new List<string>();
            for (int pass = 0; pass < 4 && missing.Count > 0; pass++)
            {
                bool free = pass == 3;
                bool progress = false;
                for (int i = 0; i < missing.Count; i++)
                {
                    var h = missing[i];
                    Plant plant;
                    try { plant = Replay(col, row, h.Type, null, free); }
                    catch (System.Exception e)
                    {
                        FusionCoopMod.Log.Warning($"[plant] fix: placing {h.Type} c{col} r{row} failed: {e.Message}");
                        plant = null;
                    }
                    if (plant == null) continue;
                    if (h.Owner != 255) SetOwner(plant, h.Owner);
                    added.Add(plant.thePlantType == h.Type ? h.Type.ToString() : $"{h.Type}->{plant.thePlantType}");
                    missing.RemoveAt(i--);
                    progress = true;
                }
                if (!progress && pass < 2) pass = 2; // straight to the free pass
            }
            Touch(col, row);

            string msg = $"[plant] fixed c{col} r{row} to match the host";
            if (removed.Count > 0) msg += $": removed {string.Join(", ", removed)}";
            if (added.Count > 0) msg += $"{(removed.Count > 0 ? ";" : ":")} placed {string.Join(", ", added)}";
            if (missing.Count > 0) msg += $"; could not place {string.Join(", ", missing.ConvertAll(h => h.Type.ToString()))}";
            if (removed.Count == 0 && added.Count == 0 && missing.Count == 0) msg += ": owners";
            FusionCoopMod.Log.Msg(msg);
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
            // Plant messages need our board (the host may be building one while we are still in a menu).
            if (Board.Instance == null && (pkt.Type == Msg.PlantDone || pkt.Type == Msg.PlantSpawned ||
                pkt.Type == Msg.PlantState || pkt.Type == Msg.PlantDied || pkt.Type == Msg.PlantSnapshot)) return;
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
                    // Walking plants (Tumble lichen, ...) move by themselves: ours may be a cell or two away.
                    if (plant == null || plant.thePlantType != type) plant = FindWalker(row, type, col);
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
                        // Plants the game put down itself (tutorial lessons, level layouts) were placed with
                        // isFreeSet and may fail the normal checks here: the host has them, so place them freely.
                        var plant = Replay(col, row, type) ?? Replay(col, row, type, null, true);
                        if (plant == null)
                        {
                            FusionCoopMod.Log.Warning($"[plant] snapshot: could not place {type} c{col} r{row}");
                            continue;
                        }
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
                if (Session.Role == Role.Client && __instance != null) Touch(__instance.thePlantColumn, __instance.thePlantRow);
                if (Session.Role != Role.Host || !Session.Ready || Replaying || __instance == null) return true;
                if (!SendsDeath(reason) || Board.Instance == null || SuppressDeaths > 0) return true;
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
