using System;
using System.IO;
using System.Text;

namespace FusionCoop.Net
{
    // Wire format: [int32 length][byte type][payload], little-endian, length counts type + payload.
    internal enum Msg : byte
    {
        Hello = 1,   // client -> host: int protocol, string name
        Welcome = 2, // host -> client: byte playerId
        Reject = 3,  // host -> client: string reason
        Ping = 4,    // long ticks (sender clock)
        Pong = 5,    // long ticks (echoed)
        Cursor = 6,  // byte playerId, CursorState
        Leave = 7,   // host -> clients: byte playerId

        GameFirst = 32,
        // client -> host: uint seq, sbyte col, sbyte row, int plantType (card type held when planting)
        PlantRequest = 32,
        // host -> requesting client: uint seq, bool accepted
        PlantResult = 33,
        // host -> clients: byte owner, sbyte col, sbyte row, int plantType; receivers replay it
        PlantDone = 34,
        // both ways (client -> host, host -> clients): byte owner, sbyte col, sbyte row, int plantType
        PlantRemoved = 35,
        // both ways: byte owner, sbyte fromCol, sbyte fromRow, int fromType, sbyte toCol, sbyte toRow, int placeType
        PlantMoved = 36,
        // host -> clients: float timeScale (0 = paused); client -> host: float timeScale (> 0)
        SpeedSet = 37,
        // host -> clients: int levelType, int levelNumber, int id, bool hasName, [string name]
        LevelEnter = 38,
        // host -> clients: (empty) host left the level for the main menu
        LevelExit = 39,
        // host -> clients: string reason
        LevelLose = 40,
        // client -> host: (empty) cards chosen, ready to start
        BattleReady = 41,
        // host -> clients: (empty) host pressed start
        BattleStart = 42,
        // host -> clients: byte readyMask (bit n = player n has chosen cards)
        ReadyState = 43,
        // client -> host: (empty) my battle started late (rejoin), send me the placed plants
        SnapshotRequest = 44,
        // host -> one client: ushort count, then count x (byte owner, sbyte col, sbyte row, int plantType)
        PlantSnapshot = 45,
        // host -> clients: byte fromOwner, byte toOwner (a player left; their plants go to the host)
        OwnersReassigned = 46,
        // client -> host: (empty) I went back to the main menu but stay in the room
        LeaveLevel = 47,
        // client -> host: (empty) put me back into your current level
        RejoinLevel = 48,
        // host -> clients, 10 Hz: int wave, int maxWave, float timeUntilNextWave, bool isHugeWave,
        // float hugeWaveCountDown, ushort count, then
        // count x (uint id, int type, sbyte row, float x, float y, long health, int armor1, int armor2,
        // int animatorStateHash, float animatorTime, byte flags[1 dying, 2 mind controlled, 4 attacking],
        // byte effects[1 cold, 2 freeze, 4 butter], float animatorSpeed, float moveSpeed)
        ZombieState = 49,
        // host -> clients: uint zombie id, int reason (Zombie.Die ran on the host)
        ZombieDied = 50,
        // host -> clients: sbyte col, sbyte row, int plantType, int dieReason (eaten, crushed, used up...)
        PlantDied = 51,
        // host -> clients: byte owner (255 none), sbyte col, sbyte row, int requestedType, int resultType
        // (a plant the game created on its own during battle: presents, plant effects; the requested
        // type may fuse with the plant already in the cell into resultType)
        PlantSpawned = 52,
        // host -> clients, 5 Hz: ushort count, then
        // count x (sbyte col, sbyte row, int type, int health, int maxHealth, int shield)
        PlantState = 53,
        // client -> host: uint zombie id, int damage, int damageType (the client's hammer hit a puppet)
        ZombieHit = 54,
        // host -> clients: sbyte col, sbyte row, int plantType, byte kind[0 trigger, 1 bool false,
        // 2 bool true], string name (an animator change from a plant's PlantShootUpdate: the host
        // decides when plants attack and which attack, e.g. corn-pult kernel or butter)
        PlantShoot = 55,
        // host -> clients: sbyte col, sbyte row, int plantType, uint zombie id, bool zombieDied (a magnet
        // plant pulled that zombie's metal item: armor, ladder, door, or the whole iron zombie)
        MagnetAttract = 56,
        // host -> clients: uint zombie id, sbyte col, sbyte row, int plantType (a ladder zombie put its
        // ladder on that plant)
        LadderPlaced = 57,
        // host -> clients: uint new zombie id, uint summoner id, float x, int row (a Dancing Zombie
        // summoned a backup dancer, which rises from the ground)
        ZombieSummoned = 58,
        // host -> clients: uint zombie id, int damage, int damageType, bool died (fire burned a zombie;
        // dying from it leaves ash)
        ZombieCharred = 59,
        // host -> clients: sbyte row, float x (a lawn mower started; clients start theirs in that row)
        MowerStarted = 60,
        // host -> clients, 1 Hz: ushort count, then count x (sbyte col, sbyte row, int graveType)
        GraveState = 61,
        // host -> clients: sbyte col, sbyte row (that grave was destroyed)
        GraveDied = 62,
        // host -> clients: sbyte col, sbyte row, int graveType, int zombieType (a zombie rises from that grave)
        GraveSummon = 63,
        // host -> clients: (float x, float y, bool killZombie, bool fake) the host's game dropped a trophy
        // (Lawnf.SetAward: normal wins, mini-game wins, fake trophies)
        AwardDropped = 64,
        // host -> clients: (empty) the host collected the trophy (PrizeMgr.Clicked)
        PrizeClicked = 65,
        // host -> clients: (empty) the host leaves the trophy screen (PrizeMgr.GoBack)
        PrizeGoBack = 66,
        // either way: (sbyte col, sbyte row, int plantType) a player used a fertilizer on that plant;
        // the host replays a client's use and passes it on to the other clients
        FertilizeUsed = 67,
        // host -> clients: (uint zombieId, sbyte col, sbyte row, int plantType) a zombie bit that plant
        // (Zombie.AttackEffect: flash and chomp sound)
        ZombieBite = 68,
        // either way: (byte kind 0 bucket / 1 helmet / 2 bean, int bucketType, sbyte col, sbyte row, int plantType
        // (bean), byte owner) a player used an item on that cell; the host passes a client's on
        ItemUsed = 69,
    }

    internal static class Protocol
    {
        internal const int Version = 28;
        internal const int DefaultPort = 43096;
        internal const int MaxPlayers = 4;
        internal const int MaxFrame = 64 * 1024;

        internal static byte[] Build(Msg type, Action<BinaryWriter> body = null)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(0); // length placeholder
            w.Write((byte)type);
            body?.Invoke(w);
            w.Flush();
            var buf = ms.ToArray();
            BitConverter.TryWriteBytes(buf.AsSpan(0, 4), buf.Length - 4);
            return buf;
        }
    }

    // Where a player's pointer is. When the player's window is unfocused or the mouse is outside it,
    // Active is false and X/Y keep the last in-window position.
    internal struct CursorState
    {
        internal float X, Y;   // world space
        internal sbyte Col, Row; // board cell under the pointer, -1 when not on a board
        internal bool Active;

        internal void Write(BinaryWriter w)
        {
            w.Write(X);
            w.Write(Y);
            w.Write(Col);
            w.Write(Row);
            w.Write(Active);
        }

        internal static CursorState Read(BinaryReader r) => new()
        {
            X = r.ReadSingle(),
            Y = r.ReadSingle(),
            Col = r.ReadSByte(),
            Row = r.ReadSByte(),
            Active = r.ReadBoolean(),
        };

        internal bool SameAs(in CursorState o) =>
            X == o.X && Y == o.Y && Col == o.Col && Row == o.Row && Active == o.Active;
    }

    internal readonly struct Packet
    {
        internal readonly Peer From;
        internal readonly Msg Type;
        internal readonly byte[] Body;

        internal Packet(Peer from, Msg type, byte[] body)
        {
            From = from;
            Type = type;
            Body = body;
        }

        internal BinaryReader Reader() => new(new MemoryStream(Body), Encoding.UTF8);
    }
}
