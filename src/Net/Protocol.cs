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
    }

    internal static class Protocol
    {
        internal const int Version = 6;
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
