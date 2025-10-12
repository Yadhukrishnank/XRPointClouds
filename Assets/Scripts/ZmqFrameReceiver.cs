using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;

[Serializable]
public struct FramePacket
{
    public int camId;                 // 0 = A, 1 = B, etc.
    public int width, height;
    public byte[] rgbBytes;           // JPEG/PNG bytes
    public byte[] depthBytes;         // z16 (H*W*2, little-endian)
    public float fx, fy, cx, cy;
    public float cullMin, cullMax, xCull, yCull;
    public Matrix4x4 pose;            // world_from_camera
    public ulong timestampUs;

    public bool IsValid => rgbBytes != null && depthBytes != null && width > 0 && height > 0;
}

/// <summary>
/// One-port receiver that de-multiplexes frames from multiple cameras
/// and keeps the latest per camera in memory.
/// </summary>
public class ZmqFrameReceiver : MonoBehaviour
{
    [Header("Discovery & Connection")]
    public bool autoDiscoverServer = true;
    public string manualServerIp = "127.0.0.1";
    public int discoveryPort = 5556;
    public int dataPort = 5555;

    [Header("Parsing Options")]
    [Tooltip("If the sender wrote pose row-major, transpose here to match HLSL mul(_PoseMatrix, v).")]
    public bool transposeIncomingPose = true;

    [Header("Logging")]
    public bool logDiscovery = true;
    public bool logConnection = true;
    public bool logFirstPacketPerCam = true;

    private Thread listenerThread;
    private volatile bool isRunning;
    private PullSocket subSocket;

    // latest frame per cameraId
    private readonly ConcurrentDictionary<int, FramePacket> _latest = new();
    // optional FIFO
    private readonly ConcurrentQueue<FramePacket> _queue = new();

    // background→main-thread log pipe
    private readonly ConcurrentQueue<string> _logQ = new();

    // first packet marker
    private readonly ConcurrentDictionary<int, bool> _seenCam = new();

    // -------- Public API --------
    public bool TryGetLatest(int camId, out FramePacket packet) => _latest.TryGetValue(camId, out packet);

    public FramePacket[] GetAllLatest() => _latest.Values.ToArray();

    public bool TryGetLatest(out FramePacket packet)
    {
        if (_queue.TryDequeue(out packet))
        {
            while (_queue.TryDequeue(out var newer)) packet = newer;
            return true;
        }
        packet = default;
        return false;
    }

    // -------- Unity lifecycle --------
    void Start()
    {
        isRunning = true;
        listenerThread = new Thread(ZmqListener) { IsBackground = true };
        listenerThread.Start();
    }

    void Update()
    {
        while (_logQ.TryDequeue(out var l)) Debug.Log(l);
    }

    void OnDestroy()
    {
        isRunning = false;
        try { subSocket?.Close(); subSocket?.Dispose(); } catch { }
        try { if (listenerThread != null && listenerThread.IsAlive) listenerThread.Join(200); } catch { }
    }

    private void BGLog(string msg) => _logQ.Enqueue($"[ZMQ {DateTime.Now:HH:mm:ss.fff}] {msg}");

    // -------- Discovery --------
    private string FindServer(int timeoutMs = 1000)
    {
        string discoveredIp = null;
        using (var client = new UdpClient())
        {
            client.EnableBroadcast = true;
            client.Client.ReceiveTimeout = timeoutMs;

            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            byte[] request = Encoding.ASCII.GetBytes("DISCOVER_ZMQ_SERVER");
            try
            {
                if (logDiscovery) BGLog($"DISCOVERY → *:{discoveryPort}");
                client.Send(request, request.Length, broadcastEp);

                IPEndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] response = client.Receive(ref senderEp); // throws on timeout
                string msg = Encoding.ASCII.GetString(response);

                if (msg.StartsWith("ZMQ_SERVER_HERE"))
                {
                    discoveredIp = senderEp.Address.ToString();
                    if (logDiscovery) BGLog($"DISCOVERY ✓ {discoveredIp}");
                }
            }
            catch (SocketException) { /* timeout */ }
        }
        return discoveredIp;
    }

    // -------- Listener --------
    private void ZmqListener()
    {
        AsyncIO.ForceDotNet.Force();

        string serverIp = string.IsNullOrWhiteSpace(manualServerIp) ? null : manualServerIp;
        if (autoDiscoverServer)
        {
            bool found = false;
            while (!found && isRunning)
            {
                serverIp = FindServer();
                if (string.IsNullOrEmpty(serverIp)) { Thread.Sleep(1000); continue; }
                found = true;
            }
        }
        if (string.IsNullOrEmpty(serverIp)) { _logQ.Enqueue("[ZMQ] ERROR: No server IP. Listener stops."); return; }

        using (subSocket = new PullSocket())
        {
            subSocket.Options.ReceiveHighWatermark = 3;
            subSocket.Connect($"tcp://{serverIp}:{dataPort}");
            if (logConnection) BGLog($"CONNECT → tcp://{serverIp}:{dataPort}");

            while (isRunning)
            {
                try
                {
                    if (!subSocket.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(2), out var msg))
                    { Thread.Sleep(1); continue; }

                    if (TryParseMultiplexPacket(msg, out var packet))
                    {
                        _latest[packet.camId] = packet;
                        _queue.Enqueue(packet);

                        if (logFirstPacketPerCam && !_seenCam.ContainsKey(packet.camId))
                        {
                            _seenCam[packet.camId] = true;
                            BGLog($"PACKET(cam {packet.camId}) {packet.width}x{packet.height} | rgb={packet.rgbBytes?.Length ?? 0}B depth={packet.depthBytes?.Length ?? 0}B ts={packet.timestampUs}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    BGLog($"ERROR: listener loop: {ex.Message}");
                    Thread.Sleep(50);
                }
            }
        }
    }

    // -------- Parser (one-port multiplexed) --------
    //
    // Binary layout (little-endian):
    // [camId:1][flags:1][reserved:2]
    // [width:4][height:4]
    // [fx:4][fy:4][cx:4][cy:4]
    // [zMin:4][zMax:4][xCull:4][yCull:4]
    // [pose:16*4 floats (row-major from sender)]
    // [timestampUs:int64]
    // [rgbLen:int32][rgbBytes]
    // [depthLen:int32][depthBytes]
    //
    private bool TryParseMultiplexPacket(byte[] buf, out FramePacket packet)
    {
        packet = default;
        if (buf == null || buf.Length < 8) return false;
        int o = 0;
        bool Need(int n) => (o + n) <= buf.Length;

        if (!Need(4)) return false;
        int camId = buf[o]; o += 1;
        o += 1; // flags
        o += 2; // reserved

        if (!Need(8)) return false;
        int w = BitConverter.ToInt32(buf, o); o += 4;
        int h = BitConverter.ToInt32(buf, o); o += 4;

        if (!Need(16)) return false;
        float fx = BitConverter.ToSingle(buf, o); o += 4;
        float fy = BitConverter.ToSingle(buf, o); o += 4;
        float cx = BitConverter.ToSingle(buf, o); o += 4;
        float cy = BitConverter.ToSingle(buf, o); o += 4;

        if (!Need(16)) return false;
        float zmin = BitConverter.ToSingle(buf, o); o += 4;
        float zmax = BitConverter.ToSingle(buf, o); o += 4;
        float xCull = BitConverter.ToSingle(buf, o); o += 4;
        float yCull = BitConverter.ToSingle(buf, o); o += 4;

        if (!Need(64)) return false;
        Matrix4x4 pose = new Matrix4x4();
        pose.m00 = BitConverter.ToSingle(buf, o); o += 4;  pose.m01 = BitConverter.ToSingle(buf, o); o += 4;  pose.m02 = BitConverter.ToSingle(buf, o); o += 4;  pose.m03 = BitConverter.ToSingle(buf, o); o += 4;
        pose.m10 = BitConverter.ToSingle(buf, o); o += 4;  pose.m11 = BitConverter.ToSingle(buf, o); o += 4;  pose.m12 = BitConverter.ToSingle(buf, o); o += 4;  pose.m13 = BitConverter.ToSingle(buf, o); o += 4;
        pose.m20 = BitConverter.ToSingle(buf, o); o += 4;  pose.m21 = BitConverter.ToSingle(buf, o); o += 4;  pose.m22 = BitConverter.ToSingle(buf, o); o += 4;  pose.m23 = BitConverter.ToSingle(buf, o); o += 4;
        pose.m30 = BitConverter.ToSingle(buf, o); o += 4;  pose.m31 = BitConverter.ToSingle(buf, o); o += 4;  pose.m32 = BitConverter.ToSingle(buf, o); o += 4;  pose.m33 = BitConverter.ToSingle(buf, o); o += 4;
        if (transposeIncomingPose) pose = pose.transpose;

        if (!Need(8)) return false;
        ulong ts = BitConverter.ToUInt64(buf, o); o += 8;

        if (!Need(4)) return false;
        int rgbLen = BitConverter.ToInt32(buf, o); o += 4;
        if (!Need(rgbLen)) return false;
        byte[] rgb = new byte[rgbLen]; Buffer.BlockCopy(buf, o, rgb, 0, rgbLen); o += rgbLen;

        if (!Need(4)) return false;
        int depthLen = BitConverter.ToInt32(buf, o); o += 4;
        if (!Need(depthLen)) return false;
        byte[] depth = new byte[depthLen]; Buffer.BlockCopy(buf, o, depth, 0, depthLen); o += depthLen;

        packet = new FramePacket
        {
            camId = camId,
            width = w, height = h,
            rgbBytes = rgb, depthBytes = depth,
            fx = fx, fy = fy, cx = cx, cy = cy,
            cullMin = zmin, cullMax = zmax, xCull = xCull, yCull = yCull,
            pose = pose, timestampUs = ts
        };
        return true;
    }
}
