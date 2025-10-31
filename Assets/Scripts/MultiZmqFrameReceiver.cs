// Assets/Scripts/MultiZmqFrameReceiver.cs (with auto-discovery support, no unsafe needed)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;
using System.Net;
using System.Net.Sockets;
using System.Text;

[Serializable]
public struct MultiFramePacket
{
    public int camId;

    public int width, height;

    // intrinsics
    public float fx, fy, cx, cy;
    public bool hasIntr;

    // pose / extrinsics
    public bool hasPose;
    public Matrix4x4 pose;

    // image/depth payload
    public byte[] rgbBytes;
    public byte[] depthBytes;

    public bool IsValid =>
        width > 0 &&
        height > 0 &&
        depthBytes != null &&
        depthBytes.Length >= (width * height * 2);
}

public class MultiZmqFrameReceiver : MonoBehaviour
{
    // ------------ NEW: Discovery settings ------------
    [Header("Discovery")]
    [Tooltip("If true, we broadcast DISCOVER_ZMQ_SERVER and use the reply IP instead of 'host'.")]
    public bool autoDiscoverServer = true;

    [Tooltip("UDP port used for discovery broadcast/reply. Must match sender's discovery_responder.")]
    public int discoveryPort = 5554;

    [Tooltip("How many times we try discovery at startup (1 try ~1s timeout).")]
    public int discoveryRetries = 3;

    [Tooltip("Manual fallback IP / last-resort IP if discovery is off or fails.")]
    public string host = "127.0.0.1";

    [Header("ZMQ Data Ports (one per camera)")]
    public int[] ports = new int[] { 5555, 5556 };

    [Header("Debug")]
    public bool logConnections = true;
    public bool logPackets = false;

    private class Worker
    {
        public string host;
        public int port;
        public Thread thread;
        public bool running;
        public PullSocket sock;
    }

    private ConcurrentDictionary<int, MultiFramePacket> latest = new();
    private readonly List<Worker> workers = new();

    // Packet header constants coming from the sender
    const uint MAGIC = 0xABCD1234;
    const ushort FLAG_POSE = 1;
    const ushort FLAG_INTR = 2;

    // -------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------
    void Start()
    {
        if (ports == null || ports.Length == 0)
        {
            Debug.LogError("[MultiZmqFrameReceiver] No ports configured.");
            enabled = false;
            return;
        }

        // Required by NetMQ for background threads (especially on Android/Quest)
        AsyncIO.ForceDotNet.Force();

        // 1) Figure out which IP to connect to
        string resolvedHost = ResolveServerHost();

        if (logConnections)
            Debug.Log($"[MultiZmqFrameReceiver] Using host {resolvedHost}");

        // 2) Spin up one worker per distinct port
        foreach (var p in ports.Distinct())
            StartWorker(resolvedHost, p);
    }

    void OnDestroy()
    {
        foreach (var w in workers.ToArray())
            StopWorker(w);

        NetMQConfig.Cleanup();
    }

    // -------------------------------------------------
    // Worker thread management
    // -------------------------------------------------
    private void StartWorker(string resolvedHost, int port)
    {
        var w = new Worker
        {
            host = resolvedHost,
            port = port,
            running = true
        };

        w.thread = new Thread(() => WorkerLoop(w))
        {
            IsBackground = true,
            Name = $"ZMQ_Rx_{port}"
        };

        workers.Add(w);
        w.thread.Start();
    }

    private void StopWorker(Worker w)
    {
        try { w.running = false; } catch { }
        try { w.sock?.Close(); } catch { }
        try { w.sock?.Dispose(); } catch { }
        try { w.thread?.Join(200); } catch { }
    }

    private void WorkerLoop(Worker w)
    {
        try
        {
            using (var sock = new PullSocket())
            {
                w.sock = sock;

                string addr = $"tcp://{w.host}:{w.port}";
                sock.Options.ReceiveHighWatermark = 2;
                sock.Connect(addr);

                if (logConnections)
                    Debug.Log($"[MultiZmqFrameReceiver] CONNECT PULL {addr}");

                while (w.running)
                {
                    if (!sock.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100), out var msg))
                        continue;

                    ParsePacketBytes(msg, out var pkt);

                    if (pkt.IsValid)
                        latest.AddOrUpdate(pkt.camId, pkt, (k, old) => pkt);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MultiZmqFrameReceiver] Worker {w.port} error: {ex}");
        }
    }

    // -------------------------------------------------
    // Packet parsing (unchanged except for using class log flags)
    // -------------------------------------------------
    private static float ReadFloatLE(byte[] b, int offset) => BitConverter.ToSingle(b, offset);

    private void ParsePacketBytes(byte[] msg, out MultiFramePacket pkt)
    {
        pkt = default;

        if (msg == null || msg.Length < 36)
            return;

        int o = 0;

        uint magic = BitConverter.ToUInt32(msg, o); o += 4;
        if (magic != MAGIC)
            return;

        ushort version = BitConverter.ToUInt16(msg, o); o += 2;
        ushort flags = BitConverter.ToUInt16(msg, o); o += 2;
        int camId = BitConverter.ToInt32(msg, o); o += 4;

        // timestamp (us) but we don't use it yet
        ulong ts_us = BitConverter.ToUInt64(msg, o); o += 8;

        int width = BitConverter.ToInt32(msg, o); o += 4;
        int height = BitConverter.ToInt32(msg, o); o += 4;
        int rgbLen = BitConverter.ToInt32(msg, o); o += 4;
        int depthLen = BitConverter.ToInt32(msg, o); o += 4;

        // intrinsics -----------------
        float fx = 0, fy = 0, cx = 0, cy = 0;
        bool hasIntr = (flags & FLAG_INTR) != 0;
        if (hasIntr)
        {
            if (o + 16 > msg.Length) return;
            fx = ReadFloatLE(msg, o); o += 4;
            fy = ReadFloatLE(msg, o); o += 4;
            cx = ReadFloatLE(msg, o); o += 4;
            cy = ReadFloatLE(msg, o); o += 4;
        }

        // rgb / depth images ----------
        if (o + rgbLen + depthLen > msg.Length)
            return;

        var rgb = new byte[rgbLen];
        Buffer.BlockCopy(msg, o, rgb, 0, rgbLen);
        o += rgbLen;

        var depth = new byte[depthLen];
        Buffer.BlockCopy(msg, o, depth, 0, depthLen);
        o += depthLen;

        // pose / extrinsics -----------
        bool hasPose = (flags & FLAG_POSE) != 0;
        Matrix4x4 pose = Matrix4x4.identity;
        if (hasPose)
        {
            if (o + 64 > msg.Length) return;

            // Sender is row-major:
            // [ m00 m01 m02 m03
            //   m10 m11 m12 m13
            //   m20 m21 m22 m23
            //   m30 m31 m32 m33 ]
            // Unity's Matrix4x4 stores by columns, so assign explicitly.
            pose.m00 = ReadFloatLE(msg, o + 0); pose.m01 = ReadFloatLE(msg, o + 4);
            pose.m02 = ReadFloatLE(msg, o + 8); pose.m03 = ReadFloatLE(msg, o + 12);
            pose.m10 = ReadFloatLE(msg, o + 16); pose.m11 = ReadFloatLE(msg, o + 20);
            pose.m12 = ReadFloatLE(msg, o + 24); pose.m13 = ReadFloatLE(msg, o + 28);
            pose.m20 = ReadFloatLE(msg, o + 32); pose.m21 = ReadFloatLE(msg, o + 36);
            pose.m22 = ReadFloatLE(msg, o + 40); pose.m23 = ReadFloatLE(msg, o + 44);
            pose.m30 = ReadFloatLE(msg, o + 48); pose.m31 = ReadFloatLE(msg, o + 52);
            pose.m32 = ReadFloatLE(msg, o + 56); pose.m33 = ReadFloatLE(msg, o + 60);
            o += 64;
        }

        if (logPackets)
        {
            Debug.Log(
                $"[MultiZmqFrameReceiver] cam={camId} {width}x{height} rgb={rgbLen} depth={depthLen} intr={hasIntr} pose={hasPose}"
            );
        }

        pkt = new MultiFramePacket
        {
            camId = camId,
            width = width,
            height = height,
            fx = fx,
            fy = fy,
            cx = cx,
            cy = cy,
            hasIntr = hasIntr,
            hasPose = hasPose,
            pose = pose,
            rgbBytes = rgb,
            depthBytes = depth
        };
    }

    // -------------------------------------------------
    // Public accessors (unchanged)
    // -------------------------------------------------
    public int[] ActiveCameraIds() => latest.Keys.OrderBy(k => k).ToArray();

    public bool TryGetLatest(int camId, out MultiFramePacket pkt) =>
        latest.TryGetValue(camId, out pkt);

    public List<MultiFramePacket> SnapshotAll() =>
        latest.Values.OrderBy(v => v.camId).ToList();

    // -------------------------------------------------
    // Auto-discovery helpers
    // -------------------------------------------------

    // Broadcast "DISCOVER_ZMQ_SERVER" over UDP and listen for "ZMQ_SERVER_HERE"
    // Returns the discovered IP string on success, or null on timeout.
    private string DiscoverServerOnce(int timeoutMs)
    {
        using (var client = new UdpClient())
        {
            client.EnableBroadcast = true;
            client.Client.ReceiveTimeout = timeoutMs;

            IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            byte[] req = Encoding.ASCII.GetBytes("DISCOVER_ZMQ_SERVER");

            try
            {
                // 1) broadcast the query
                client.Send(req, req.Length, broadcastEp);

                // 2) wait for first reply
                IPEndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] resp = client.Receive(ref senderEp); // throws on timeout

                string msg = Encoding.ASCII.GetString(resp).Trim();

                if (msg.StartsWith("ZMQ_SERVER_HERE"))
                {
                    string foundIp = senderEp.Address.ToString();
                    if (logConnections)
                        Debug.Log("[MultiZmqFrameReceiver] Discovery got reply from " + foundIp);
                    return foundIp;
                }
            }
            catch (SocketException)
            {
                // timeout / no response this round
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MultiZmqFrameReceiver] Discovery error: " + ex);
            }
        }

        return null;
    }

    // Try discoveryRetries times (each ~1s). If all fail, fall back to manual host.
    private string ResolveServerHost()
    {
        if (!autoDiscoverServer)
        {
            if (logConnections)
                Debug.Log("[MultiZmqFrameReceiver] autoDiscoverServer = false, using manual host=" + host);
            return host;
        }

        for (int i = 0; i < discoveryRetries; i++)
        {
            string ip = DiscoverServerOnce(timeoutMs: 1000);
            if (!string.IsNullOrEmpty(ip))
                return ip;

            if (logConnections)
                Debug.LogWarning("[MultiZmqFrameReceiver] No discovery reply yet, retry " + (i + 1) + "/" + discoveryRetries);

            Thread.Sleep(250);
        }

        if (logConnections)
            Debug.LogWarning("[MultiZmqFrameReceiver] Discovery failed, falling back to manual host=" + host);

        return host;
    }
}
