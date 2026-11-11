using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// A ball's path is fully described by its BallState, so instead of streaming positions the host
// only sends a new state when something changes. Every player computes the position for the same
// shared clock (NetClock.Now), which keeps the client's view in step with the host's.
//
// Paddle hits are decided by the player who owns that paddle, using what they see right now.
// The client applies its own hit immediately (prediction) and tells the host, which rewinds the
// ball to the moment of the hit and continues from there.
//
// Color: white at the start of each point, then red/blue for whoever touched it last.
// The same script is used for the small extra balls (tick "isExtraBall" on that prefab).
public class Ball : NetworkBehaviour
{
    public static readonly List<Ball> All = new();

    public SpriteRenderer spriteRenderer;
    public float radius = 0.175f;

    [Tooltip("Tick this on the small extra-ball prefab used by the Multi Ball power-up.")]
    public bool isExtraBall;

    [Header("Serve")]
    public float startingSpeed = 8f;
    public float launchDelay = 1f;

    [Header("Paddle bounce")]
    public float maxBounceAngle = 50f;  // hitting the paddle's edge sends the ball out at this angle
    public float speedUpPerHit = 0.5f;
    public float maxSpeed = 18f;

    [Header("Catch power-up")]
    public float fastMultiplier = 2f;   // a shot ball travels this much faster until the other player hits it
    public float autoShootAfter = 3f;   // the holder fires automatically after this many seconds

    [Header("Move Ball power-up")]
    public float steerStrength = 10f;   // how fast the player can bend the ball
    public float maxSteerAngle = 60f;   // stops the ball from going straight up/down
    public float steerInterval = 0.05f;

    static readonly Color RedColor = Color.red;
    static readonly Color BlueColor = new(0.3f, 0.5f, 1f);

    readonly NetworkVariable<BallState> state = new();

    // Our own hit/shot, shown right away until the host confirms it.
    BallState predicted;
    bool hasPredicted;
    double predictedAt;

    // Smooths out corrections so the ball glides instead of teleporting.
    BallState shown;
    bool shownPredicted;
    Vector2 visualOffset;

    ushort decidedVersion = ushort.MaxValue;   // the paddle owner judges each path segment only once
    bool decidedPredicted;
    double nextSteerTime;

    public BallState State => hasPredicted ? predicted : state.Value;
    public PlayerSide? LastHitter => State.Owner;
    public bool InPlay => State.mode == BallMode.Moving || State.mode == BallMode.Stuck;
    public float LimitY => PongField.WallInnerY - radius;

    public Vector2 PositionAt(double t) => PositionOf(State, t);

    Vector2 PositionOf(BallState s, double t)
    {
        if (s.mode == BallMode.Stuck)
        {
            PlayerSide side = (PlayerSide)s.owner;
            PlayerPaddle paddle = PlayerPaddle.Get(side);
            float y = paddle != null ? paddle.DisplayY : 0f;
            return new Vector2(PongField.ContactX(side, radius), Mathf.Clamp(y + s.stuckOffset, -LimitY, LimitY));
        }

        s.Simulate(t, LimitY, out Vector2 p, out _);
        return p;
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        hasPredicted = false;
        visualOffset = Vector2.zero;
        decidedVersion = ushort.MaxValue;

        if (IsServer && !isExtraBall)
            SetState(new BallState { mode = BallMode.Idle, pos = Vector2.zero });

        shown = State;
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        hasPredicted = false;
        spriteRenderer.enabled = false;
    }

    // ---------- Host: changing the state ----------

    void SetState(BallState s)
    {
        s.version = (ushort)(state.Value.version + 1);
        state.Value = s;
    }

    public void StopBall()
    {
        if (!IsServer) return;
        SetState(new BallState
        {
            mode = BallMode.Idle,
            pos = Vector2.zero,
            hitSeq = (byte)(state.Value.hitSeq + 1)
        });
    }

    // Main ball: back to the center, launched after a short delay.
    public void Serve()
    {
        if (!IsServer) return;

        Vector2 direction = new Vector2(Random.value >= 0.5f ? 1f : -1f, Random.Range(-1f, 1f)).normalized;
        SetState(new BallState
        {
            mode = BallMode.Moving,
            pos = Vector2.zero,
            vel = direction * startingSpeed,
            time = NetClock.Now + launchDelay,
            baseSpeed = startingSpeed,
            hitSeq = (byte)(state.Value.hitSeq + 1)
        });
    }

    public void LaunchExtra(Vector2 position, Vector2 velocity, PlayerSide owner)
    {
        if (!IsServer) return;
        SetState(new BallState
        {
            mode = BallMode.Moving,
            owner = (byte)owner,
            pos = position,
            vel = velocity,
            time = NetClock.Now,
            baseSpeed = velocity.magnitude
        });
    }

    public void RemoveFromGame()
    {
        if (IsServer && IsSpawned) NetworkObject.Despawn(true);
    }

    // Connection trouble: stop where we are.
    public void Freeze(double t)
    {
        if (!IsServer) return;
        BallState s = state.Value;
        if (s.mode != BallMode.Moving) return;

        s.Simulate(t, LimitY, out Vector2 p, out Vector2 v);
        s.pos = p;
        s.vel = v;
        s.time = t;
        s.mode = BallMode.Frozen;
        SetState(s);
    }

    // Carry on from the same spot at resumeAt. frozenFor pushes the Catch auto-shot timer back too.
    public void Unfreeze(double resumeAt, double frozenFor)
    {
        if (!IsServer) return;
        BallState s = state.Value;

        if (s.mode == BallMode.Frozen)
        {
            s.mode = BallMode.Moving;
            s.time = resumeAt;
            SetState(s);
        }
        else if (s.mode == BallMode.Stuck)
        {
            s.time += frozenFor;
            SetState(s);
        }
    }

    // ---------- Paddle hits (run by the paddle's owner) ----------

    public void CheckPaddle(PlayerPaddle paddle)
    {
        BallState s = State;
        PlayerSide side = paddle.Side;
        if (s.mode != BallMode.Moving || Mathf.Sign(s.vel.x) != PongField.TowardSide(side)) return;
        if (NetClock.PeerIsSilent) return;

        double now = NetClock.Now;
        double contactTime = s.TimeAtX(PongField.ContactX(side, radius));
        if (contactTime > now) return;   // hasn't reached the paddle yet

        // Judge each segment of the path once.
        if (decidedVersion == s.version && decidedPredicted == hasPredicted) return;
        decidedVersion = s.version;
        decidedPredicted = hasPredicted;

        if (contactTime < s.time - 0.0001) return;   // this segment starts behind the paddle
        if (now - contactTime > 0.3) return;         // learned about it too late; let the host call it

        float ballY = PositionOf(s, contactTime).y;
        float paddleY = paddle.DisplayY;
        float halfHeight = paddle.HalfHeight;
        if (Mathf.Abs(ballY - paddleY) > halfHeight + radius) return;   // missed

        bool stick = paddle.HasCatch;
        if (IsServer)
        {
            ApplyHit(paddle, contactTime, ballY, paddleY, halfHeight, stick);
        }
        else
        {
            Predict(MakeHit(s, side, contactTime, ballY, paddleY, halfHeight, stick));
            PaddleHitRpc(side, contactTime, ballY, paddleY, halfHeight, stick, s.hitSeq);
        }
    }

    // Catch power-up: fire the ball we're holding.
    public void TryShoot(PlayerPaddle paddle)
    {
        BallState s = State;
        if (s.mode != BallMode.Stuck || s.owner != (byte)paddle.Side) return;

        double now = NetClock.Now;
        if (IsServer)
        {
            SetState(MakeShot(s, paddle.Side, now, paddle.DisplayY, paddle.HalfHeight));
        }
        else
        {
            Predict(MakeShot(s, paddle.Side, now, paddle.DisplayY, paddle.HalfHeight));
            ShootRpc(paddle.Side, now, paddle.DisplayY, paddle.HalfHeight, s.hitSeq);
        }
    }

    public bool IsStuckTo(PlayerSide side, out double stuckSince)
    {
        BallState s = State;
        stuckSince = s.time;
        return s.mode == BallMode.Stuck && s.owner == (byte)side;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PaddleHitRpc(PlayerSide side, double t, float ballY, float paddleY, float halfHeight, bool stick,
                      byte fromHitSeq, RpcParams rpcParams = default)
    {
        PlayerPaddle paddle = PlayerPaddle.Get(side);
        if (paddle == null || paddle.OwnerClientId != rpcParams.Receive.SenderClientId) return;

        BallState s = state.Value;
        if (s.mode != BallMode.Moving || s.hitSeq != fromHitSeq) { RejectHit("ball state changed"); return; }
        if (Mathf.Sign(s.vel.x) != PongField.TowardSide(side)) { RejectHit("ball going the other way"); return; }

        // Light sanity checks: the hit must be recent and match where we think the ball was.
        double now = NetClock.Now;
        if (t > now + 0.1 || t < s.time - 0.5) { RejectHit($"bad time ({now - t:0.000}s ago)"); return; }
        if (Mathf.Abs(PositionOf(s, t).y - ballY) > 0.75f) { RejectHit("ball wasn't there"); return; }

        ApplyHit(paddle, t, ballY, paddleY, halfHeight, stick);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ShootRpc(PlayerSide side, double t, float paddleY, float halfHeight, byte fromHitSeq,
                  RpcParams rpcParams = default)
    {
        PlayerPaddle paddle = PlayerPaddle.Get(side);
        if (paddle == null || paddle.OwnerClientId != rpcParams.Receive.SenderClientId) return;

        BallState s = state.Value;
        if (s.mode != BallMode.Stuck || s.owner != (byte)side || s.hitSeq != fromHitSeq) return;

        double now = NetClock.Now;
        SetState(MakeShot(s, side, System.Math.Clamp(t, s.time, now), paddleY, halfHeight));
    }

    static void RejectHit(string why) => Debug.Log("[Pong] Rejected client hit: " + why);

    void ApplyHit(PlayerPaddle paddle, double t, float ballY, float paddleY, float halfHeight, bool stick)
    {
        if (stick && !paddle.TryConsumeCatch()) stick = false;
        SetState(MakeHit(state.Value, paddle.Side, t, ballY, paddleY, halfHeight, stick));
    }

    BallState MakeHit(BallState s, PlayerSide side, double t, float ballY, float paddleY, float halfHeight, bool stick)
    {
        s.hitSeq++;
        s.owner = (byte)side;
        s.fastFrom = 0;
        s.time = t;
        s.pos = new Vector2(PongField.ContactX(side, radius), ballY);

        if (stick)
        {
            s.mode = BallMode.Stuck;
            s.vel = Vector2.zero;
            s.stuckOffset = Mathf.Clamp(ballY - paddleY, -halfHeight, halfHeight);
            return s;
        }

        // Classic Pong: where the ball hits the paddle decides the angle it leaves at.
        s.mode = BallMode.Moving;
        s.baseSpeed = Mathf.Min(s.baseSpeed + speedUpPerHit, maxSpeed);
        s.vel = BounceVelocity(side, (ballY - paddleY) / halfHeight, s.baseSpeed);
        return s;
    }

    BallState MakeShot(BallState s, PlayerSide side, double t, float paddleY, float halfHeight)
    {
        s.hitSeq++;
        s.mode = BallMode.Moving;
        s.owner = (byte)side;
        s.fastFrom = (byte)side;
        s.time = t;
        s.pos = new Vector2(PongField.ContactX(side, radius), Mathf.Clamp(paddleY + s.stuckOffset, -LimitY, LimitY));
        s.baseSpeed = Mathf.Min(s.baseSpeed + speedUpPerHit, maxSpeed);
        s.vel = BounceVelocity(side, s.stuckOffset / halfHeight, s.baseSpeed * fastMultiplier);
        return s;
    }

    Vector2 BounceVelocity(PlayerSide side, float offset, float speed)
    {
        float angle = Mathf.Clamp(offset, -1f, 1f) * maxBounceAngle * Mathf.Deg2Rad;
        float awayFromPaddle = -PongField.TowardSide(side);
        return new Vector2(Mathf.Cos(angle) * awayFromPaddle, Mathf.Sin(angle)) * speed;
    }

    void Predict(BallState next)
    {
        predicted = next;
        hasPredicted = true;
        predictedAt = Time.realtimeSinceStartupAsDouble;
    }

    // ---------- Every frame ----------

    void Update()
    {
        if (!IsSpawned) return;

        // Drop our prediction once the host has caught up with it (or ignored it).
        if (hasPredicted)
        {
            int ahead = (sbyte)(predicted.hitSeq - state.Value.hitSeq);
            bool timedOut = Time.realtimeSinceStartupAsDouble - predictedAt > 1.0;
            if (timedOut) Debug.Log("[Pong] The host never confirmed our hit");
            if (ahead <= 0 || timedOut || state.Value.mode == BallMode.Frozen || state.Value.mode == BallMode.Idle)
                hasPredicted = false;
        }

        if (IsServer) ServerUpdate();
    }

    void ServerUpdate()
    {
        if (GameManager.Instance == null || !GameManager.Instance.IsPlaying) return;

        BallState s = state.Value;
        double now = NetClock.Now;

        // Move Ball power-up: whoever's color the ball has can bend it up/down.
        if (s.mode == BallMode.Moving && s.owner != 0 && now >= s.time && now >= nextSteerTime)
        {
            PlayerPaddle paddle = PlayerPaddle.Get((PlayerSide)s.owner);
            if (paddle != null && paddle.HasMoveBall && paddle.MoveInput != 0)
            {
                nextSteerTime = now + steerInterval;

                s.Simulate(now, LimitY, out Vector2 p, out Vector2 v);
                float speed = v.magnitude;
                v.y += paddle.MoveInput * steerStrength * steerInterval;

                // Don't let the ball turn too vertical.
                float maxY = Mathf.Tan(maxSteerAngle * Mathf.Deg2Rad) * Mathf.Abs(v.x);
                v.y = Mathf.Clamp(v.y, -maxY, maxY);

                s.pos = p;
                s.vel = v.normalized * speed;   // keep the same speed
                s.time = now;
                SetState(s);
            }
        }

        // Catch power-up: if the holder never fires (or their message got lost), fire for them.
        if (s.mode == BallMode.Stuck && now - s.time > autoShootAfter + 1.5)
        {
            PlayerPaddle paddle = PlayerPaddle.Get((PlayerSide)s.owner);
            if (paddle != null)
                SetState(MakeShot(s, paddle.Side, now, paddle.DisplayY, paddle.HalfHeight));
        }
    }

    void LateUpdate()
    {
        BallState s = State;
        bool visible = IsSpawned && s.mode != BallMode.Hidden;
        spriteRenderer.enabled = visible;
        if (!visible) return;

        double now = NetClock.DisplayNow;
        Vector2 target = PositionOf(s, now);

        // A new path arrived: keep showing the ball where it was and glide onto the new path.
        if (s.version != shown.version || s.hitSeq != shown.hitSeq || hasPredicted != shownPredicted)
        {
            if (shown.mode == BallMode.Moving && s.mode == BallMode.Moving)
            {
                visualOffset += PositionOf(shown, now) - target;
                if (visualOffset.magnitude > 2.5f) visualOffset = Vector2.zero;
            }
            else
            {
                visualOffset = Vector2.zero;
            }

            shown = s;
            shownPredicted = hasPredicted;
        }

        visualOffset *= Mathf.Exp(-12f * Time.deltaTime);
        transform.position = target + visualOffset;

        Color color = s.owner switch
        {
            1 => RedColor,
            2 => BlueColor,
            _ => Color.white
        };

        // A Catch shot flickers so the other player knows it's coming in fast.
        if (s.fastFrom != 0 && s.mode == BallMode.Moving && Mathf.Repeat(Time.time * 12f, 1f) > 0.5f)
            color = Color.white;

        spriteRenderer.color = color;
    }
}
