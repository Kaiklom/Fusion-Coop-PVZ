using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FusionCoop.Net
{
    internal enum Role { None, Host, Client }

    internal sealed class RemoteCursor
    {
        internal CursorState State;
        internal double LastSeen;
        internal bool EverActive; // false until the player has had the mouse in their game window once
    }

    // Host/client session. Call Poll() once per frame from the main thread.
    internal static class Session
    {
        internal static Role Role { get; private set; }
        internal static byte LocalId { get; private set; }
        internal static string Status { get; private set; } = "offline";
        internal static double RttMs { get; private set; } = -1;
        internal static readonly Dictionary<byte, RemoteCursor> Cursors = new();
        internal static readonly Dictionary<byte, string> Names = new();

        private static readonly ConcurrentQueue<Packet> Inbox = new();
        private static readonly ConcurrentQueue<Peer> Accepted = new();
        private static readonly List<Peer> Peers = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static TcpListener _listener;
        private static Peer _host;
        private static string _localName = "Player";
        private static double _nextPing, _nextCursor, _nextCursorKeepalive;
        private static CursorState _lastSentCursor;
        private static volatile string _connectError;
        private static volatile Peer _connected;

        internal static double Now => Clock.Elapsed.TotalSeconds;
        internal static bool Active => Role != Role.None;

        internal static void StartHost(int port, string name)
        {
            Stop();
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Status = $"host failed: {e.Message}";
                _listener = null;
                return;
            }
            Role = Role.Host;
            LocalId = 0;
            _localName = name;
            Names[0] = name;
            Status = $"hosting on port {port}";
            var listener = _listener;
            new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "FusionCoop.Accept" }.Start();
        }

        internal static void Join(string ip, int port, string name)
        {
            Stop();
            Role = Role.Client;
            _localName = name;
            Status = $"connecting to {ip}:{port}...";
            new Thread(() =>
            {
                try
                {
                    var tcp = new TcpClient();
                    var ar = tcp.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        tcp.Close();
                        throw new TimeoutException("timed out");
                    }
                    tcp.EndConnect(ar);
                    _connected = new Peer(tcp, Inbox);
                }
                catch (Exception e)
                {
                    _connectError = e.Message;
                }
            }) { IsBackground = true, Name = "FusionCoop.Connect" }.Start();
        }

        internal static void Stop()
        {
            try { _listener?.Stop(); } catch (Exception) { }
            _listener = null;
            foreach (var p in Peers) p.Close();
            Peers.Clear();
            _host?.Close();
            _host = null;
            _connected = null;
            _connectError = null;
            _lastSentCursor = default;
            _nextCursorKeepalive = 0;
            while (Inbox.TryDequeue(out _)) { }
            while (Accepted.TryDequeue(out var p)) p.Close();
            Cursors.Clear();
            Names.Clear();
            Role = Role.None;
            RttMs = -1;
            Coop.PlantSync.Reset();
            Status = "offline";
        }

        private static void AcceptLoop(TcpListener listener)
        {
            try
            {
                while (true) Accepted.Enqueue(new Peer(listener.AcceptTcpClient(), Inbox));
            }
            catch (Exception)
            {
                // Listener stopped.
            }
        }

        internal static void Poll(in CursorState cursor)
        {
            if (Role == Role.None) return;

            if (Role == Role.Client && _host == null)
            {
                if (_connectError != null)
                {
                    var err = _connectError;
                    Stop();
                    Status = $"connect failed: {err}";
                    return;
                }
                if (_connected != null)
                {
                    _host = _connected;
                    _connected = null;
                    _host.Send(Protocol.Build(Msg.Hello, w => { w.Write(Protocol.Version); w.Write(_localName); }));
                    Status = "handshaking...";
                }
            }

            while (Accepted.TryDequeue(out var peer)) Peers.Add(peer);

            // Free the slots of dropped peers first so a quick reconnect gets its old player id back.
            DropClosedPeers();
            if (Role == Role.None) return;

            while (Inbox.TryDequeue(out var pkt))
            {
                try
                {
                    if (Role == Role.Host) HandleOnHost(pkt);
                    else HandleOnClient(pkt);
                }
                catch (Exception e)
                {
                    FusionCoopMod.Log.Warning($"bad {pkt.Type} from {pkt.From.Remote}: {e.Message}");
                }
            }

            DropClosedPeers();
            if (Role == Role.None) return;

            double now = Now;
            if (now >= _nextPing)
            {
                _nextPing = now + 1.0;
                var ping = Protocol.Build(Msg.Ping, w => w.Write(Clock.ElapsedTicks));
                if (Role == Role.Host) foreach (var p in Peers) { if (p.PlayerId != 0) p.Send(ping); }
                else _host?.Send(ping);
            }
            // Cursor: on change at most 20 Hz, plus a 1 s keepalive so late joiners see it.
            if (Ready && now >= _nextCursor && (!cursor.SameAs(_lastSentCursor) || now >= _nextCursorKeepalive))
            {
                _nextCursor = now + 0.05;
                _nextCursorKeepalive = now + 1.0;
                _lastSentCursor = cursor;
                var c = cursor;
                byte me = LocalId;
                Broadcast(Protocol.Build(Msg.Cursor, w => { w.Write(me); c.Write(w); }), null);
            }
        }

        internal static bool Ready => Role == Role.Host || (Role == Role.Client && LocalId != 0);

        // Gameplay messages (type >= Msg.GameFirst) go to these handlers; the byte is the sender's player id.
        internal static Action<byte, Packet> HostGameHandler;
        internal static Action<Packet> ClientGameHandler;

        internal static void SendToHost(byte[] frame) => _host?.Send(frame);

        internal static void SendToPlayer(byte id, byte[] frame)
        {
            foreach (var p in Peers)
                if (p.PlayerId == id) p.Send(frame);
        }

        // Host only: send to every joined client except player `exceptId` (255 = none).
        internal static void SendToClients(byte[] frame, byte exceptId = 255)
        {
            foreach (var p in Peers)
                if (p.PlayerId != 0 && p.PlayerId != exceptId) p.Send(frame);
        }

        // Host: send to every joined client except `except`. Client: send to host.
        private static void Broadcast(byte[] frame, Peer except)
        {
            if (Role == Role.Host)
            {
                foreach (var p in Peers)
                    if (p.PlayerId != 0 && p != except) p.Send(frame);
            }
            else
            {
                _host?.Send(frame);
            }
        }

        private static void HandleOnHost(Packet pkt)
        {
            var peer = pkt.From;
            var r = pkt.Reader();
            switch (pkt.Type)
            {
                case Msg.Hello:
                {
                    int version = r.ReadInt32();
                    string name = r.ReadString();
                    if (version != Protocol.Version)
                    {
                        Reject(peer, $"version mismatch (host {Protocol.Version}, you {version})");
                        return;
                    }
                    byte id = 0;
                    for (byte i = 1; i < Protocol.MaxPlayers; i++)
                        if (!Names.ContainsKey(i)) { id = i; break; }
                    if (id == 0)
                    {
                        Reject(peer, "room full");
                        return;
                    }
                    peer.PlayerId = id;
                    peer.Name = name;
                    Names[id] = name;
                    peer.Send(Protocol.Build(Msg.Welcome, w => w.Write(id)));
                    FusionCoopMod.Log.Msg($"P{id + 1} '{name}' joined from {peer.Remote}");
                    Status = $"hosting, {Names.Count} players";
                    break;
                }
                case Msg.Ping:
                {
                    long t = r.ReadInt64();
                    peer.Send(Protocol.Build(Msg.Pong, w => w.Write(t)));
                    break;
                }
                case Msg.Pong:
                    RttMs = TicksToMs(Clock.ElapsedTicks - r.ReadInt64());
                    break;
                case Msg.Cursor:
                {
                    if (peer.PlayerId == 0) return;
                    r.ReadByte(); // ignore claimed id, use the connection's
                    var c = CursorState.Read(r);
                    SetCursor(peer.PlayerId, c);
                    byte pid = peer.PlayerId;
                    Broadcast(Protocol.Build(Msg.Cursor, w => { w.Write(pid); c.Write(w); }), peer);
                    break;
                }
                default:
                    if (peer.PlayerId != 0 && pkt.Type >= Msg.GameFirst) HostGameHandler?.Invoke(peer.PlayerId, pkt);
                    break;
            }
        }

        private static void HandleOnClient(Packet pkt)
        {
            var r = pkt.Reader();
            switch (pkt.Type)
            {
                case Msg.Welcome:
                    LocalId = r.ReadByte();
                    Status = $"connected as P{LocalId + 1}";
                    FusionCoopMod.Log.Msg(Status);
                    break;
                case Msg.Reject:
                {
                    string reason = r.ReadString();
                    Stop();
                    Status = $"rejected: {reason}";
                    break;
                }
                case Msg.Ping:
                {
                    long t = r.ReadInt64();
                    _host?.Send(Protocol.Build(Msg.Pong, w => w.Write(t)));
                    break;
                }
                case Msg.Pong:
                    RttMs = TicksToMs(Clock.ElapsedTicks - r.ReadInt64());
                    break;
                case Msg.Cursor:
                {
                    byte id = r.ReadByte();
                    var c = CursorState.Read(r);
                    if (id != LocalId) SetCursor(id, c);
                    break;
                }
                case Msg.Leave:
                {
                    byte id = r.ReadByte();
                    Cursors.Remove(id);
                    Names.Remove(id);
                    break;
                }
                default:
                    if (pkt.Type >= Msg.GameFirst) ClientGameHandler?.Invoke(pkt);
                    break;
            }
        }

        private static void Reject(Peer peer, string reason)
        {
            peer.Send(Protocol.Build(Msg.Reject, w => w.Write(reason)));
            peer.Close();
        }

        private static void SetCursor(byte id, in CursorState s)
        {
            if (!Cursors.TryGetValue(id, out var c))
            {
                Cursors[id] = c = new RemoteCursor();
                FusionCoopMod.Log.Msg($"first cursor from P{id + 1}: ({s.X:0.00}, {s.Y:0.00}) cell c{s.Col} r{s.Row} active={s.Active}");
            }
            else if (c.State.Active != s.Active)
            {
                FusionCoopMod.Log.Msg($"P{id + 1} {(s.Active ? "back in game window" : "left game window")} at c{s.Col} r{s.Row}");
            }
            c.State = s;
            c.LastSeen = Now;
            c.EverActive |= s.Active;
        }

        private static void DropClosedPeers()
        {
            if (Role == Role.Client)
            {
                if (_host != null && _host.Closed)
                {
                    Stop();
                    Status = "disconnected from host";
                }
                return;
            }
            for (int i = Peers.Count - 1; i >= 0; i--)
            {
                var p = Peers[i];
                if (!p.Closed) continue;
                Peers.RemoveAt(i);
                if (p.PlayerId == 0) continue;
                byte id = p.PlayerId;
                Names.Remove(id);
                Cursors.Remove(id);
                FusionCoopMod.Log.Msg($"P{id + 1} left");
                Broadcast(Protocol.Build(Msg.Leave, w => w.Write(id)), null);
                Status = $"hosting, {Names.Count} players";
            }
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
