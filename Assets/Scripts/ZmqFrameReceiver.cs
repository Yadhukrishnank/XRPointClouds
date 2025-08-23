using System;
using System.Collections.Concurrent;
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
    public int width, height;
    public byte[] rgbBytes;
    public byte[] depthBytes;
    public float fx, fy, cx, cy;
    public float cullMin, cullMax, xCull, yCull;
    public bool IsValid => rgbBytes != null && depthBytes != null && width > 0 && height > 0;
}

public class ZmqFrameReceiver : MonoBehaviour
{
    [Header("Discovery & Connection")]
    public bool autoDiscoverServer = true;
    public string manualServerIp = "127.0.0.1";
    public int discoveryPort = 5556;
    public int dataPort = 5555;

    [Header("Diagnostics")]
    public bool logConnection = false; // default to NO logs

    private Thread listenerThread;
    private volatile bool isRunning;
    private PullSocket subSocket;
    private readonly ConcurrentQueue<FramePacket> queue = new ConcurrentQueue<FramePacket>();

    public bool TryGetLatest(out FramePacket packet)
    {
        if (queue.TryDequeue(out packet))
        {
            while (queue.TryDequeue(out var newer)) packet = newer;
            return true;
        }
        packet = default;
        return false;
    }

    void Start()
    {
        isRunning = true;
        listenerThread = new Thread(ZmqListener) { IsBackground = true };
        listenerThread.Start();
    }

    void OnDestroy()
    {
        isRunning = false;

        try
        {
            subSocket?.Close();
            subSocket?.Dispose();
        }
        catch (Exception e)
        {
            if (logConnection) Debug.LogWarning("[ZMQ] Error closing socket: " + e);
        }

        if (listenerThread != null && listenerThread.IsAlive)
            listenerThread.Join(500);

        NetMQConfig.Cleanup();
    }

    private string FindServer(int timeoutMs = 1000)
    {
        string discoveredIp = null;
        using (var client = new UdpClient())
        {
            client.EnableBroadcast = true;
            client.Client.ReceiveTimeout = timeoutMs;

            IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            byte[] request = Encoding.ASCII.GetBytes("DISCOVER_ZMQ_SERVER");
            client.Send(request, request.Length, broadcastEp);

            try
            {
                IPEndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] response = client.Receive(ref senderEp);
                string msg = Encoding.ASCII.GetString(response);

                if (msg.StartsWith("ZMQ_SERVER_HERE"))
                {
                    discoveredIp = senderEp.Address.ToString();
                    if (logConnection) Debug.Log("[ZMQ] Server found: " + discoveredIp);
                }
            }
            catch (SocketException) { /* timeout */ }
        }
        return discoveredIp;
    }

    private void ZmqListener()
    {
        AsyncIO.ForceDotNet.Force();

        string serverIp = manualServerIp;

        if (autoDiscoverServer)
        {
            bool found = false;
            while (!found && isRunning)
            {
                serverIp = FindServer();
                if (string.IsNullOrEmpty(serverIp))
                {
                    if (logConnection) Debug.LogWarning("[ZMQ] No server found, retrying…");
                    Thread.Sleep(1000);
                    continue;
                }
                found = true;
            }
        }

        if (string.IsNullOrEmpty(serverIp))
        {
            if (logConnection) Debug.LogError("[ZMQ] Server IP is empty. Aborting listener.");
            return;
        }

        using (subSocket = new PullSocket($">tcp://{serverIp}:{dataPort}"))
        {
            if (logConnection) Debug.Log($"[ZMQ] Connected to tcp://{serverIp}:{dataPort}");
            while (isRunning)
            {
                try
                {
                    // drain to latest frame to reduce latency
                    byte[] lastMsg = null;
                    while (subSocket.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(5), out var msg))
                        lastMsg = msg;

                    if (lastMsg == null)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int offset = 0;

                    // 1) size
                    if (lastMsg.Length < offset + 8) continue;
                    int w = BitConverter.ToInt32(lastMsg, offset); offset += 4;
                    int h = BitConverter.ToInt32(lastMsg, offset); offset += 4;

                    // 2) rgb length + bytes
                    if (lastMsg.Length < offset + 4) continue;
                    int rgbLen = BitConverter.ToInt32(lastMsg, offset); offset += 4;
                    if (lastMsg.Length < offset + rgbLen) continue;
                    var rgb = new byte[rgbLen];
                    Buffer.BlockCopy(lastMsg, offset, rgb, 0, rgbLen);
                    offset += rgbLen;

                    // 3) depth length + bytes
                    if (lastMsg.Length < offset + 4) continue;
                    int depthLen = BitConverter.ToInt32(lastMsg, offset); offset += 4;
                    if (lastMsg.Length < offset + depthLen) continue;
                    var depth = new byte[depthLen];
                    Buffer.BlockCopy(lastMsg, offset, depth, 0, depthLen);
                    offset += depthLen;

                    // 4) intrinsics
                    if (lastMsg.Length < offset + 16) continue;
                    float fx = BitConverter.ToSingle(lastMsg, offset); offset += 4;
                    float fy = BitConverter.ToSingle(lastMsg, offset); offset += 4;
                    float cx = BitConverter.ToSingle(lastMsg, offset); offset += 4;
                    float cy = BitConverter.ToSingle(lastMsg, offset); offset += 4;

                    // 5) culling
                    if (lastMsg.Length < offset + 16) continue;
                    float cullMin = BitConverter.ToSingle(lastMsg, offset); offset += 4;
                    float cullMax = BitConverter.ToSingle(lastMsg, offset); offset += 4;
                    float xCull   = BitConverter.ToSingle(lastMsg, offset); offset += 4;
                    float yCull   = BitConverter.ToSingle(lastMsg, offset); offset += 4;

                    var packet = new FramePacket
                    {
                        width = w,
                        height = h,
                        rgbBytes = rgb,
                        depthBytes = depth,
                        fx = fx, fy = fy, cx = cx, cy = cy,
                        cullMin = cullMin, cullMax = cullMax, xCull = xCull, yCull = yCull
                    };

                    queue.Enqueue(packet);
                }
                catch (Exception ex)
                {
                    if (logConnection) Debug.LogError("[ZMQ] Listener fault: " + ex.Message);
                    break;
                }
            }
        }
    }
}
