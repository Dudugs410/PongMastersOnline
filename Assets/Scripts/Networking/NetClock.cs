using Unity.Netcode;
using UnityEngine;

public class NetClock : NetworkBehaviour
{
    public static NetClock Instance { get; private set; }

    public float pingInterval = 0.1f;
    [Tooltip("No message from the other side for this long = connection problem.")]
    public float staleAfter = 0.6f;

    const int SampleCount = 20;
    readonly double[] sampleRtt = new double[SampleCount];
    readonly double[] sampleOffset = new double[SampleCount];
    int sampleIndex, samplesTaken;

    double offset;
    double lastPingSent;
    double lastHeardFromPeer;
    bool peerEverHeard;

    public static double Now =>
        Instance != null && Instance.IsSpawned && !Instance.IsServer
            ? FrameClock + Instance.offset
            : FrameClock;

    public static double DisplayNow =>
        Instance != null && Instance.IsSpawned && !Instance.IsServer && PeerIsSilent
            ? Instance.displayHeldAt
            : Now;

    double displayHeldAt;

    static int cachedFrame = -1;
    static double cachedTime;
    static double FrameClock
    {
        get
        {
            if (Time.frameCount != cachedFrame)
            {
                cachedFrame = Time.frameCount;
                cachedTime = Time.realtimeSinceStartupAsDouble;
            }
            return cachedTime;
        }
    }

    public static bool IsSynced => Instance != null && Instance.IsSpawned && (Instance.IsServer || Instance.samplesTaken > 0);

    public static float Rtt { get; private set; }

    public static float SilenceSeconds =>
        Instance == null || !Instance.peerEverHeard ? 0f
            : (float)(Time.realtimeSinceStartupAsDouble - Instance.lastHeardFromPeer);

    public static bool PeerIsSilent => SilenceSeconds > (Instance != null ? Instance.staleAfter : 0.6f);

    void Awake()
    {
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        samplesTaken = 0;
        sampleIndex = 0;
        offset = 0;
        Rtt = 0f;
        peerEverHeard = false;
    }

    public void ForgetPeer()
    {
        peerEverHeard = false;
    }

    void Update()
    {
        if (!IsSpawned || IsServer) return;

        if (!PeerIsSilent) displayHeldAt = Now;

        double real = Time.realtimeSinceStartupAsDouble;
        if (real - lastPingSent >= pingInterval)
        {
            lastPingSent = real;
            PingRpc(real, Rtt);
        }
    }

    [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Everyone)]
    void PingRpc(double clientTime, float clientRtt, RpcParams rpcParams = default)
    {
        lastHeardFromPeer = Time.realtimeSinceStartupAsDouble;
        peerEverHeard = true;
        Rtt = clientRtt;
        PongRpc(clientTime, Time.realtimeSinceStartupAsDouble,
                RpcTarget.Single(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable)]
    void PongRpc(double clientTime, double hostTime, RpcParams rpcParams)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        lastHeardFromPeer = now;
        peerEverHeard = true;

        double rtt = now - clientTime;
        sampleRtt[sampleIndex] = rtt;
        sampleOffset[sampleIndex] = hostTime + rtt * 0.5 - now;
        sampleIndex = (sampleIndex + 1) % SampleCount;
        samplesTaken = Mathf.Min(samplesTaken + 1, SampleCount);

        int best = 0;
        double rttSum = 0;
        for (int i = 0; i < samplesTaken; i++)
        {
            rttSum += sampleRtt[i];
            if (sampleRtt[i] < sampleRtt[best]) best = i;
        }
        Rtt = (float)(rttSum / samplesTaken);

        double target = sampleOffset[best];
        if (samplesTaken == 1 || System.Math.Abs(target - offset) > 0.25)
            offset = target;
        else
            offset += System.Math.Clamp(target - offset, -0.002, 0.002);
    }
}
