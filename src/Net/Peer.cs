using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace FusionCoop.Net
{
    // One TCP connection. A background thread reads frames into the shared inbox;
    // everything else (handling, Unity calls) happens on the main thread.
    internal sealed class Peer
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<Packet> _inbox;
        private readonly object _sendLock = new();
        private volatile bool _closed;

        internal byte PlayerId;
        internal string Name = "";
        internal double RttMs = -1;
        internal bool Closed => _closed;
        internal string Remote { get; }

        internal Peer(TcpClient tcp, ConcurrentQueue<Packet> inbox)
        {
            _tcp = tcp;
            _tcp.NoDelay = true;
            _stream = tcp.GetStream();
            _inbox = inbox;
            Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
            new Thread(ReadLoop) { IsBackground = true, Name = "FusionCoop.Peer" }.Start();
        }

        internal void Send(byte[] frame)
        {
            if (_closed) return;
            try
            {
                lock (_sendLock) _stream.Write(frame, 0, frame.Length);
            }
            catch (Exception)
            {
                Close();
            }
        }

        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _tcp.Close(); } catch (Exception) { }
        }

        private void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (!_closed)
                {
                    ReadExact(header, 4);
                    int len = BitConverter.ToInt32(header, 0);
                    if (len < 1 || len > Protocol.MaxFrame) throw new InvalidDataException($"bad frame length {len}");
                    var frame = new byte[len];
                    ReadExact(frame, len);
                    var body = new byte[len - 1];
                    Buffer.BlockCopy(frame, 1, body, 0, body.Length);
                    _inbox.Enqueue(new Packet(this, (Msg)frame[0], body));
                }
            }
            catch (Exception)
            {
                // Socket closed or bad data: the session notices Closed on its next poll.
            }
            Close();
        }

        private void ReadExact(byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = _stream.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }
    }
}
