// Assets/Scripts/MultiZmqFrameReceiver.cs (safe, no 'unsafe' required)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;

[Serializable]
public struct MultiFramePacket
{
    public int camId;
    public int width, height;
    public float fx, fy, cx, cy;
    public bool hasIntr;
    public bool hasPose;
    public Matrix4x4 pose;
    public byte[] rgbBytes;
    public byte[] depthBytes;
    public bool IsValid => width > 0 && height > 0 && depthBytes != null && depthBytes.Length >= (width * height * 2);
}

public class MultiZmqFrameReceiver : MonoBehaviour
{
    [Header("Connection")]
    public string host = "127.0.0.1";
    public int[] ports = new int[] { 5555, 5556 };

    [Header("Debug")]
    public bool logConnections = true;
    public bool logPackets = false;

    private class Worker
    {
        public int port;
        public Thread thread;
        public bool running;
        public PullSocket sock;
    }

    private ConcurrentDictionary<int, MultiFramePacket> latest = new();
    private readonly List<Worker> workers = new();

    const uint MAGIC = 0xABCD1234;
    const ushort FLAG_POSE = 1;
    const ushort FLAG_INTR = 2;

    void Start()
    {
        if (ports == null || ports.Length == 0) { Debug.LogError("[MultiZmqFrameReceiver] No ports configured."); enabled = false; return; }
        AsyncIO.ForceDotNet.Force();
        foreach (var p in ports.Distinct()) StartWorker(p);
    }

    void OnDestroy()
    {
        foreach (var w in workers.ToArray()) StopWorker(w);
        NetMQConfig.Cleanup();
    }

    private void StartWorker(int port)
    {
        var w = new Worker { port = port, running = true };
        w.thread = new Thread(() => WorkerLoop(w)) { IsBackground = true, Name = $"ZMQ_Rx_{port}" };
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
                string addr = $"tcp://{host}:{w.port}";
                sock.Options.ReceiveHighWatermark = 2;
                sock.Connect(addr);
                if (logConnections) Debug.Log($"[MultiZmqFrameReceiver] CONNECT PULL {addr}");

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

    private static float ReadFloatLE(byte[] b, int offset) => BitConverter.ToSingle(b, offset);

    private void ParsePacketBytes(byte[] msg, out MultiFramePacket pkt)
    {
        pkt = default;
        if (msg == null || msg.Length < 36) return;
        int o = 0;

        uint magic = BitConverter.ToUInt32(msg, o); o += 4;
        if (magic != MAGIC) return;

        ushort version = BitConverter.ToUInt16(msg, o); o += 2;
        ushort flags = BitConverter.ToUInt16(msg, o); o += 2;
        int camId = BitConverter.ToInt32(msg, o); o += 4;
        ulong ts_us = BitConverter.ToUInt64(msg, o); o += 8;

        int width = BitConverter.ToInt32(msg, o); o += 4;
        int height = BitConverter.ToInt32(msg, o); o += 4;
        int rgbLen = BitConverter.ToInt32(msg, o); o += 4;
        int depthLen = BitConverter.ToInt32(msg, o); o += 4;

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

        if (o + rgbLen + depthLen > msg.Length) return;

        var rgb = new byte[rgbLen];
        Buffer.BlockCopy(msg, o, rgb, 0, rgbLen);
        o += rgbLen;

        var depth = new byte[depthLen];
        Buffer.BlockCopy(msg, o, depth, 0, depthLen);
        o += depthLen;

        bool hasPose = (flags & FLAG_POSE) != 0;
        Matrix4x4 pose = Matrix4x4.identity;
        if (hasPose)
        {
            if (o + 64 > msg.Length) return;
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
            Debug.Log($"[MultiZmqFrameReceiver] cam={camId} {width}x{height} rgb={rgbLen} depth={depthLen} intr={hasIntr} pose={hasPose}");

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

    public int[] ActiveCameraIds() => latest.Keys.OrderBy(k => k).ToArray();
    public bool TryGetLatest(int camId, out MultiFramePacket pkt) => latest.TryGetValue(camId, out pkt);
    public List<MultiFramePacket> SnapshotAll() => latest.Values.OrderBy(v => v.camId).ToList();
}
