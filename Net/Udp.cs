using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FarCoopMod.Net
{
    /// <summary>
    /// UDP transport, built like the networking of most real-time games:
    ///  - unreliable channel: snapshots / positions / input / ping. Lost = skipped (a newer one
    ///    is 20-33 ms behind), old or duplicate packets are dropped. No stalls.
    ///  - reliable ordered channel: joins, item list, take/drop, hose, buttons, bye. Numbered,
    ///    acknowledged (piggy-backed on every packet), re-sent until confirmed, delivered in order.
    ///  - many messages packed into one datagram (max ~1200 bytes, never IP-fragmented);
    ///    bigger messages are split into fragments and re-assembled.
    /// Only background threads here: no Unity / IL2CPP calls.
    /// </summary>
    internal static class UdpWire
    {
        public const byte Connect = 1, Accept = 2, Data = 3, Disconnect = 4;
        public const uint Magic = 0x46415243;          // "FARC"
        public const int MaxPacket = 1200;              // safe for every network path, VPNs included
        public const int EntryOverhead = 1 + 2 + 5 + 2; // flags + rseq + fragment info + length
        public const int HeaderSize = 1 + 2 + 2;        // kind + ack + useq

        public const byte FlagReliable = 1, FlagFragment = 2;

        public static bool SeqGreater(ushort a, ushort b) => (short)(a - b) > 0;
    }

    public sealed class UdpConnection : Connection
    {
        private readonly UdpEndpoint owner;
        internal readonly IPEndPoint Remote;
        public override string Transport => "UDP";

        // ---- sending (main thread enqueues, pump thread sends) ----
        private readonly ConcurrentQueue<byte[]> outQueue = new ConcurrentQueue<byte[]>();
        private sealed class Pending { public ushort Seq; public byte[] Entry; public long FirstSent, LastSent; public int Sends; }
        private readonly List<Pending> unacked = new List<Pending>();   // pump thread only
        private ushort nextReliableSeq;
        private ushort nextUnreliableSeq;
        private ushort nextFragmentId;
        private long lastSendTicks;

        // ---- receiving (socket thread) ----
        private ushort nextExpected;                                         // reliable: next seq to deliver
        private readonly Dictionary<ushort, byte[]> early = new Dictionary<ushort, byte[]>();
        private bool haveUseq;
        private ushort lastUseq;
        private volatile bool ackPending;
        private int remoteAck = -1;                                          // highest seq the peer confirmed (cumulative)
        private MemoryStream reliableFrag;                                   // reliable fragments arrive in order
        private sealed class Frag { public byte[][] Parts; public int Got; public long Born; }
        private readonly Dictionary<ushort, Frag> frags = new Dictionary<ushort, Frag>();

        // ---- stats ----
        private double srttMs = 50;
        public long Resends, LostUnreliable, PacketsIn, PacketsOut;
        public double RttMs => srttMs;
        public override string Stats => $"rtt={srttMs:0}ms resent={Interlocked.Read(ref Resends)} lost={Interlocked.Read(ref LostUnreliable)} unacked={unackedCount}";
        private volatile int unackedCount;

        internal UdpConnection(UdpEndpoint owner, IPEndPoint remote)
        {
            this.owner = owner;
            Remote = remote;
            RemoteAddress = remote.ToString();
            Touch();
        }

        public override void Send(byte[] frame)
        {
            if (Closed || frame == null || frame.Length == 0) return;
            outQueue.Enqueue(frame);
        }

        public override void Close(string reason = "closed")
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            CloseReason = reason;
            try { for (int i = 0; i < 3; i++) owner.SendRaw(new byte[] { UdpWire.Disconnect }, Remote); } catch { }
            owner.Forget(this);
        }

        internal void RemoteClosed(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            CloseReason = reason;
            owner.Forget(this);
        }

        // ================= pump thread =================

        private static long NowTicks => DateTime.UtcNow.Ticks;

        internal void Pump()
        {
            if (Closed) return;
            long now = NowTicks;
            var entries = new List<byte[]>();

            // 1) re-send reliable entries that were not confirmed in time
            long rto = TimeSpan.FromMilliseconds(Math.Max(40, Math.Min(500, srttMs * 1.5 + 20))).Ticks;
            int ra = Volatile.Read(ref remoteAck);
            lock (unacked)
            {
                if (ra >= 0) unacked.RemoveAll(p =>
                {
                    bool acked = !UdpWire.SeqGreater(p.Seq, (ushort)ra);
                    if (acked && p.Sends == 1)
                    {
                        double sample = TimeSpan.FromTicks(now - p.FirstSent).TotalMilliseconds;
                        srttMs = srttMs * 0.9 + sample * 0.1;
                    }
                    return acked;
                });
                foreach (var p in unacked)
                {
                    if (now - p.LastSent < rto) continue;
                    p.LastSent = now; p.Sends++;
                    Interlocked.Increment(ref Resends);
                    entries.Add(p.Entry);
                    if (p.Sends > 60) { Close("peer stopped confirming (timeout)"); return; }
                }
                unackedCount = unacked.Count;
            }

            // 2) new messages
            while (outQueue.TryDequeue(out var frame))
            {
                bool reliable = !IsUnreliable(frame[0]);
                int maxData = UdpWire.MaxPacket - UdpWire.HeaderSize - UdpWire.EntryOverhead;
                if (frame.Length <= maxData)
                {
                    entries.Add(MakeEntry(frame, 0, frame.Length, reliable, false, 0, 0, 0, now));
                }
                else
                {
                    int count = (frame.Length + maxData - 1) / maxData;
                    if (count > 255) continue;   // >300 KB: never happens for this game
                    ushort fid = nextFragmentId++;
                    for (int i = 0; i < count; i++)
                    {
                        int off = i * maxData;
                        entries.Add(MakeEntry(frame, off, Math.Min(maxData, frame.Length - off), reliable, true, fid, (byte)i, (byte)count, now));
                    }
                }
                Interlocked.Increment(ref FramesOut);
            }

            // 3) pack into datagrams (also sends a bare ack / keep-alive when needed)
            bool keepAlive = now - lastSendTicks > TimeSpan.FromMilliseconds(250).Ticks;
            if (entries.Count == 0 && !ackPending && !keepAlive) return;
            ackPending = false;
            var ms = new MemoryStream(UdpWire.MaxPacket);
            int idx = 0;
            do
            {
                ms.SetLength(0);
                ms.WriteByte(UdpWire.Data);
                ushort ack = (ushort)(nextExpected - 1);
                ms.WriteByte((byte)ack); ms.WriteByte((byte)(ack >> 8));
                ushort useq = nextUnreliableSeq++;
                ms.WriteByte((byte)useq); ms.WriteByte((byte)(useq >> 8));
                while (idx < entries.Count && ms.Length + entries[idx].Length <= UdpWire.MaxPacket)
                {
                    ms.Write(entries[idx], 0, entries[idx].Length);
                    idx++;
                }
                if (idx < entries.Count && ms.Length == UdpWire.HeaderSize) idx++;   // cannot happen (entries are sized), safety
                byte[] dgram = ms.ToArray();
                owner.SendRaw(dgram, Remote);
                Interlocked.Add(ref BytesOut, dgram.Length + 28);
                Interlocked.Increment(ref PacketsOut);
            } while (idx < entries.Count);
            lastSendTicks = now;
        }

        private byte[] MakeEntry(byte[] src, int off, int len, bool reliable, bool fragment, ushort fid, byte fidx, byte fcount, long now)
        {
            var ms = new MemoryStream(len + UdpWire.EntryOverhead);
            byte flags = 0;
            if (reliable) flags |= UdpWire.FlagReliable;
            if (fragment) flags |= UdpWire.FlagFragment;
            ms.WriteByte(flags);
            ushort seq = 0;
            if (reliable) { seq = nextReliableSeq++; ms.WriteByte((byte)seq); ms.WriteByte((byte)(seq >> 8)); }
            if (fragment) { ms.WriteByte((byte)fid); ms.WriteByte((byte)(fid >> 8)); ms.WriteByte(fidx); ms.WriteByte(fcount); }
            ms.WriteByte((byte)len); ms.WriteByte((byte)(len >> 8));
            ms.Write(src, off, len);
            var entry = ms.ToArray();
            if (reliable)
                lock (unacked) unacked.Add(new Pending { Seq = seq, Entry = entry, FirstSent = now, LastSent = now, Sends = 1 });
            return entry;
        }

        // ================= socket thread =================

        internal void OnDatagram(byte[] d, int len)
        {
            if (Closed || len < UdpWire.HeaderSize) return;
            Touch();
            Interlocked.Add(ref BytesIn, len + 28);
            Interlocked.Increment(ref PacketsIn);
            ushort ack = (ushort)(d[1] | (d[2] << 8));
            ushort useq = (ushort)(d[3] | (d[4] << 8));
            int ra = Volatile.Read(ref remoteAck);
            if (ra < 0 || UdpWire.SeqGreater(ack, (ushort)ra)) Volatile.Write(ref remoteAck, ack);

            // unreliable content of an old / duplicate datagram is skipped (reliable content never is)
            bool staleUnreliable = haveUseq && !UdpWire.SeqGreater(useq, lastUseq);
            if (!staleUnreliable)
            {
                if (haveUseq) { int gap = (ushort)(useq - lastUseq) - 1; if (gap > 0 && gap < 1000) Interlocked.Add(ref LostUnreliable, gap); }
                lastUseq = useq; haveUseq = true;
            }

            int p = UdpWire.HeaderSize;
            while (p < len)
            {
                byte flags = d[p++];
                bool reliable = (flags & UdpWire.FlagReliable) != 0;
                bool fragment = (flags & UdpWire.FlagFragment) != 0;
                ushort seq = 0, fid = 0; byte fidx = 0, fcount = 1;
                if (reliable) { if (p + 2 > len) return; seq = (ushort)(d[p] | (d[p + 1] << 8)); p += 2; }
                if (fragment) { if (p + 4 > len) return; fid = (ushort)(d[p] | (d[p + 1] << 8)); fidx = d[p + 2]; fcount = d[p + 3]; p += 4; }
                if (p + 2 > len) return;
                int n = d[p] | (d[p + 1] << 8); p += 2;
                if (n < 0 || p + n > len) return;
                var payload = new byte[n];
                Buffer.BlockCopy(d, p, payload, 0, n);
                p += n;

                if (reliable)
                {
                    ackPending = true;
                    // keep fragment info with the payload for in-order re-assembly
                    byte[] item = fragment ? Prefix(payload, fidx, fcount) : Prefix(payload, 0, 0);
                    ReliableArrived(seq, item);
                }
                else if (!staleUnreliable)
                {
                    if (!fragment) Deliver(payload);
                    else UnreliableFragment(fid, fidx, fcount, payload);
                }
            }
        }

        private static byte[] Prefix(byte[] payload, byte idx, byte count)
        {
            var r = new byte[payload.Length + 2];
            r[0] = idx; r[1] = count;
            Buffer.BlockCopy(payload, 0, r, 2, payload.Length);
            return r;
        }

        private void ReliableArrived(ushort seq, byte[] item)
        {
            if (seq == nextExpected)
            {
                DeliverReliable(item);
                nextExpected++;
                while (early.TryGetValue(nextExpected, out var next))
                {
                    early.Remove(nextExpected);
                    DeliverReliable(next);
                    nextExpected++;
                }
            }
            else if (UdpWire.SeqGreater(seq, nextExpected) && (ushort)(seq - nextExpected) < 4096)
            {
                early[seq] = item;   // arrived early: wait for the ones before it
            }
            // else: duplicate of something already delivered
        }

        private void DeliverReliable(byte[] item)
        {
            byte idx = item[0], count = item[1];
            if (count == 0) { Deliver(Slice(item, 2)); return; }
            if (idx == 0) reliableFrag = new MemoryStream();
            if (reliableFrag == null) return;
            reliableFrag.Write(item, 2, item.Length - 2);
            if (idx == count - 1) { Deliver(reliableFrag.ToArray()); reliableFrag = null; }
        }

        private void UnreliableFragment(ushort fid, byte idx, byte count, byte[] payload)
        {
            long now = NowTicks;
            if (frags.Count > 16)
            {
                var old = new List<ushort>();
                foreach (var kv in frags) if (now - kv.Value.Born > TimeSpan.FromSeconds(1).Ticks) old.Add(kv.Key);
                foreach (var k in old) frags.Remove(k);
            }
            if (!frags.TryGetValue(fid, out var f)) { f = new Frag { Parts = new byte[count][], Born = now }; frags[fid] = f; }
            if (idx >= f.Parts.Length || f.Parts[idx] != null) return;
            f.Parts[idx] = payload; f.Got++;
            if (f.Got < f.Parts.Length) return;
            frags.Remove(fid);
            var ms = new MemoryStream();
            foreach (var part in f.Parts) ms.Write(part, 0, part.Length);
            Deliver(ms.ToArray());
        }

        private static byte[] Slice(byte[] a, int from)
        {
            var r = new byte[a.Length - from];
            Buffer.BlockCopy(a, from, r, 0, r.Length);
            return r;
        }

        private void Deliver(byte[] frame)
        {
            if (frame.Length == 0) return;
            Interlocked.Increment(ref FramesIn);
            Incoming.Enqueue(frame);
        }
    }

    /// <summary>One UDP socket: the host's listening socket (many peers) or a client's socket (one peer).</summary>
    public sealed class UdpEndpoint
    {
        private readonly UdpClient socket;
        private readonly bool isHost;
        private readonly int protocol;
        private readonly Dictionary<string, UdpConnection> peers = new Dictionary<string, UdpConnection>();
        private volatile bool running = true;
        private readonly Action<Connection> onNewPeer;
        private readonly Action<string> onEvent;

        // client handshake
        private IPEndPoint server;
        private volatile bool accepted;

        private UdpEndpoint(UdpClient s, bool host, int protocol, Action<Connection> onNewPeer, Action<string> onEvent)
        {
            socket = s; isHost = host; this.protocol = protocol; this.onNewPeer = onNewPeer; this.onEvent = onEvent;
            try { socket.Client.ReceiveBufferSize = 1 << 20; socket.Client.SendBufferSize = 1 << 20; } catch { }
            // Windows: an ICMP "port unreachable" would otherwise kill Receive with ConnectionReset
            try { socket.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
            new Thread(ReceiveLoop) { IsBackground = true, Name = "FarCoop-UDP-RX" }.Start();
            new Thread(PumpLoop) { IsBackground = true, Name = "FarCoop-UDP-Pump" }.Start();
        }

        public static UdpEndpoint Host(int port, int protocol, Action<Connection> onNewPeer, Action<string> onEvent)
        {
            var s = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return new UdpEndpoint(s, true, protocol, onNewPeer, onEvent);
        }

        /// <summary>Client: blocks up to <paramref name="timeout"/> for the host to accept. Call from a worker thread.</summary>
        public static UdpEndpoint Join(string ip, int port, int protocol, TimeSpan timeout, Action<Connection> onNewPeer, Action<string> onEvent)
        {
            IPAddress addr;
            if (!IPAddress.TryParse(ip, out addr))
            {
                var list = Dns.GetHostAddresses(ip);
                addr = Array.Find(list, a => a.AddressFamily == AddressFamily.InterNetwork) ?? list[0];
            }
            var s = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var ep = new UdpEndpoint(s, false, protocol, onNewPeer, onEvent) { server = new IPEndPoint(addr, port) };
            var hello = new byte[9];
            hello[0] = UdpWire.Connect;
            WriteU32(hello, 1, UdpWire.Magic);
            WriteU32(hello, 5, (uint)protocol);
            DateTime end = DateTime.UtcNow + timeout;
            while (!ep.accepted && DateTime.UtcNow < end)
            {
                try { ep.SendRaw(hello, ep.server); } catch { }
                Thread.Sleep(200);
            }
            if (!ep.accepted)
            {
                ep.Stop();
                throw new TimeoutException($"no answer in {timeout.TotalSeconds:0}s (is the host running, the IP right and UDP port {port} allowed in the firewall?)");
            }
            return ep;
        }

        private static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        internal void SendRaw(byte[] d, IPEndPoint to)
        {
            if (!running) return;
            try { socket.Send(d, d.Length, to); } catch { }
        }

        internal void Forget(UdpConnection c)
        {
            lock (peers) peers.Remove(c.Remote.ToString());
            if (!isHost) Stop();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            UdpConnection[] all;
            lock (peers) { all = new UdpConnection[peers.Count]; peers.Values.CopyTo(all, 0); }
            foreach (var c in all) c.Close("stopped");
            try { socket.Close(); } catch { }
        }

        private void ReceiveLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                byte[] d;
                try { d = socket.Receive(ref from); }
                catch (SocketException) { if (!running) break; continue; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { if (running) onEvent?.Invoke("UDP receive error: " + e.Message); Thread.Sleep(50); continue; }
                if (d == null || d.Length == 0) continue;
                try { Handle(d, from); } catch (Exception e) { onEvent?.Invoke("UDP packet error: " + e.Message); }
            }
        }

        private void Handle(byte[] d, IPEndPoint from)
        {
            string key = from.ToString();
            UdpConnection c;
            lock (peers) peers.TryGetValue(key, out c);
            switch (d[0])
            {
                case UdpWire.Data:
                    c?.OnDatagram(d, d.Length);
                    break;
                case UdpWire.Connect:
                    if (!isHost || d.Length < 9 || ReadU32(d, 1) != UdpWire.Magic) return;
                    if (c == null)
                    {
                        c = new UdpConnection(this, from);
                        lock (peers) peers[key] = c;
                        onNewPeer(c);
                    }
                    SendRaw(new byte[] { UdpWire.Accept }, from);   // repeated CONNECTs just get the answer again
                    break;
                case UdpWire.Accept:
                    if (isHost || accepted || server == null || !from.Equals(server)) return;
                    c = new UdpConnection(this, from);
                    lock (peers) peers[key] = c;
                    accepted = true;
                    onNewPeer(c);
                    break;
                case UdpWire.Disconnect:
                    c?.RemoteClosed("closed by the other side");
                    break;
            }
        }

        private void PumpLoop()
        {
            var list = new List<UdpConnection>();
            while (running)
            {
                list.Clear();
                lock (peers) list.AddRange(peers.Values);
                foreach (var c in list)
                {
                    try { c.Pump(); } catch (Exception e) { onEvent?.Invoke("UDP send error: " + e.Message); }
                }
                Thread.Sleep(2);
            }
        }
    }
}
