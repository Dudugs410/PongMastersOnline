using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Where a paddle was, which way it was moving and when. The other player predicts the paddle
// from this, so it only needs re-sending when the direction changes.
public struct PaddleMotion : INetworkSerializable
{
    public float y;
    public sbyte dir;
    public double time;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref y);
        s.SerializeValue(ref dir);
        s.SerializeValue(ref time);
    }
}

// One prefab is used for both players:
// the host's paddle is RED on the left, the client's paddle is BLUE on the right.
// Each player moves their own paddle instantly and judges hits on it themselves.
public class PlayerPaddle : NetworkBehaviour
{
    public float moveSpeed = 8f;
    public float baseHeight = 2f;
    public SpriteRenderer spriteRenderer;

    [Header("Power-ups")]
    public float speedBoostMultiplier = 1.6f;
    public float growMultiplier = 1.3f;

    static readonly Color RedColor = Color.red;
    static readonly Color BlueColor = new(0.3f, 0.5f, 1f);

    public static PlayerPaddle Red { get; private set; }
    public static PlayerPaddle Blue { get; private set; }
    public static PlayerPaddle Get(PlayerSide side) => side == PlayerSide.Red ? Red : Blue;

    // Written by the owner (this player).
    readonly NetworkVariable<FixedString64Bytes> playerName = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    readonly NetworkVariable<PaddleMotion> motion = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // Written by the host. Stored as "ends at" times on the shared clock, so picking a power-up
    // up again just moves the end time forward (resets the duration, never stacks).
    readonly NetworkVariable<double> speedBoostEndTime = new(0);
    readonly NetworkVariable<double> moveBallEndTime = new(0);
    readonly NetworkVariable<double> growEndTime = new(0);
    readonly NetworkVariable<bool> hasCatch = new(false);

    float ownerY;          // owner: the real position
    float shownTarget;     // others: last predicted position, to smooth corrections
    float visualOffset;
    PaddleMotion shownMotion;

    public bool IsRedPlayer => OwnerClientId == NetworkManager.ServerClientId;
    public PlayerSide Side => IsRedPlayer ? PlayerSide.Red : PlayerSide.Blue;

    public string DisplayName
    {
        get
        {
            string n = playerName.Value.ToString();
            if (!string.IsNullOrEmpty(n)) return n;
            return IsRedPlayer ? "Red" : "Blue";
        }
    }

    // -1 = down, 0 = still, 1 = up. Read by the host to steer balls.
    public int MoveInput => motion.Value.dir;

    // Where this player's screen shows the paddle right now.
    public float DisplayY { get; private set; }

    public float HalfHeight => baseHeight * (HasGrow ? growMultiplier : 1f) * 0.5f;
    public float YLimit => PongField.WallInnerY - HalfHeight;
    public float Speed => moveSpeed * (HasSpeedBoost ? speedBoostMultiplier : 1f);

    double Now => NetClock.Now;
    float TimeLeft(NetworkVariable<double> endTime) =>
        IsSpawned ? Mathf.Max(0f, (float)(endTime.Value - Now)) : 0f;

    public float SpeedBoostTimeLeft => TimeLeft(speedBoostEndTime);
    public float MoveBallTimeLeft => TimeLeft(moveBallEndTime);
    public float GrowTimeLeft => TimeLeft(growEndTime);

    public bool HasSpeedBoost => SpeedBoostTimeLeft > 0f;
    public bool HasMoveBall => MoveBallTimeLeft > 0f;
    public bool HasGrow => GrowTimeLeft > 0f;
    public bool HasCatch => hasCatch.Value;

    public override void OnNetworkSpawn()
    {
        spriteRenderer.color = IsRedPlayer ? RedColor : BlueColor;

        if (IsRedPlayer) Red = this;
        else Blue = this;

        ownerY = 0f;
        DisplayY = 0f;
        visualOffset = 0f;
        ApplyTransform();

        if (IsOwner)
        {
            playerName.Value = new FixedString64Bytes(NetworkMenu.LocalPlayerName);
            motion.Value = new PaddleMotion { y = 0f, dir = 0, time = Now };
        }
    }

    public override void OnNetworkDespawn()
    {
        if (Red == this) Red = null;
        if (Blue == this) Blue = null;
    }

    // ---------- Power-ups (host only) ----------

    public void GrantSpeedBoost(float duration)
    {
        if (IsServer) speedBoostEndTime.Value = Now + duration;
    }

    public void GrantMoveBall(float duration)
    {
        if (IsServer) moveBallEndTime.Value = Now + duration;
    }

    public void GrantGrow(float duration)
    {
        if (IsServer) growEndTime.Value = Now + duration;
    }

    public void GrantCatch()
    {
        if (IsServer) hasCatch.Value = true;
    }

    public bool TryConsumeCatch()
    {
        if (!IsServer || !hasCatch.Value) return false;
        hasCatch.Value = false;
        return true;
    }

    // After a pause, push the timers back so nobody loses power-up time.
    public void ExtendEffects(double seconds)
    {
        if (!IsServer) return;
        double now = Now;
        if (speedBoostEndTime.Value > now) speedBoostEndTime.Value += seconds;
        if (moveBallEndTime.Value > now) moveBallEndTime.Value += seconds;
        if (growEndTime.Value > now) growEndTime.Value += seconds;
    }

    public void ClearEffects()
    {
        if (!IsServer) return;
        speedBoostEndTime.Value = 0;
        moveBallEndTime.Value = 0;
        growEndTime.Value = 0;
        hasCatch.Value = false;
    }

    // ---------- Every frame ----------

    void Update()
    {
        if (!IsSpawned) return;

        if (IsOwner) OwnerUpdate();
        else RemoteUpdate();

        ApplyTransform();
    }

    void OwnerUpdate()
    {
        int direction = ReadInput();

        ownerY = Mathf.Clamp(ownerY + direction * Speed * Time.deltaTime, -YLimit, YLimit);
        DisplayY = ownerY;

        // Only send when the other player's prediction would be wrong.
        float theirGuess = Predict(motion.Value, Now);
        if (direction != motion.Value.dir || Mathf.Abs(theirGuess - ownerY) > 0.02f)
            motion.Value = new PaddleMotion { y = ownerY, dir = (sbyte)direction, time = Now };

        // We judge hits on our own paddle, using exactly what we see.
        foreach (Ball ball in Ball.All.ToArray())
            ball.CheckPaddle(this);

        // Catch power-up: Space fires the held ball (or it fires itself after a few seconds).
        if (GameManager.Instance == null || !GameManager.Instance.IsPlaying) return;

        bool fire = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
        foreach (Ball ball in Ball.All.ToArray())
        {
            if (!ball.IsStuckTo(Side, out double since)) continue;
            if (fire || NetworkMenu.BotMode || Now - since >= ball.autoShootAfter)
                ball.TryShoot(this);
        }
    }

    int ReadInput()
    {
        if (NetworkMenu.BotMode) return BotInput();
        if (Keyboard.current == null) return 0;

        int direction = 0;
        if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) direction += 1;
        if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) direction -= 1;
        return direction;
    }

    // Testing aid (-bot on the command line): follow the nearest ball heading our way.
    int BotInput()
    {
        float bestTime = float.MaxValue;
        float targetY = 0f;
        double now = Now;

        foreach (Ball ball in Ball.All)
        {
            BallState s = ball.State;
            if (s.mode != BallMode.Moving || Mathf.Sign(s.vel.x) != PongField.TowardSide(Side)) continue;

            double arrive = s.TimeAtX(PongField.ContactX(Side, ball.radius));
            if (arrive < now || arrive - now > bestTime) continue;

            bestTime = (float)(arrive - now);
            targetY = ball.PositionAt(arrive).y;
        }

        float diff = targetY - ownerY;
        if (Mathf.Abs(diff) < 0.15f) return 0;
        return diff > 0 ? 1 : -1;
    }

    void RemoteUpdate()
    {
        float target = Predict(motion.Value, NetClock.DisplayNow);

        // New info arrived: glide from where we were showing it instead of snapping.
        PaddleMotion m = motion.Value;
        if (m.time != shownMotion.time || m.y != shownMotion.y || m.dir != shownMotion.dir)
        {
            visualOffset += shownTarget - target;
            if (Mathf.Abs(visualOffset) > 1.5f) visualOffset = 0f;
            shownMotion = m;
        }

        shownTarget = target;
        visualOffset *= Mathf.Exp(-15f * Time.deltaTime);
        DisplayY = Mathf.Clamp(target + visualOffset, -YLimit, YLimit);
    }

    // Where the paddle is at time t if it kept moving the same way.
    float Predict(PaddleMotion m, double t)
    {
        float dt = (float)System.Math.Max(0.0, t - m.time);
        return Mathf.Clamp(m.y + m.dir * Speed * dt, -YLimit, YLimit);
    }

    void ApplyTransform()
    {
        float x = IsRedPlayer ? -PongField.PaddleX : PongField.PaddleX;
        transform.position = new Vector3(x, DisplayY, 0f);
        transform.localScale = new Vector3(PongField.PaddleHalfWidth * 2f, HalfHeight * 2f, 1f);
    }
}
