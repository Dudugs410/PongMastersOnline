using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class PowerUpManager : NetworkBehaviour
{
    public static PowerUpManager Instance { get; private set; }

    [Header("Prefabs (add both to the NetworkManager's Network Prefabs list)")]
    public PowerUpBox boxPrefab;
    public Ball extraBallPrefab;

    [Header("Spawning")]
    public Vector2 spawnArea = new(5f, 3.5f);
    public float minDistanceFromCenter = 1.5f;
    public float minSpawnDelay = 5f;
    public float maxSpawnDelay = 10f;
    public int maxBoxes = 2;

    [Header("Effects")]
    public float speedBoostDuration = 10f;
    public float moveBallDuration = 10f;
    public float growDuration = 10f;
    public float extraBallAngle = 20f;
    [Range(0.1f, 1f)] public float extraBallSpeed = 0.6f;
    public int maxExtraBalls = 3;

    readonly List<PowerUpBox> boxes = new();
    readonly List<Ball> extraBalls = new();
    float nextSpawnTime;

    void Awake()
    {
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        boxes.Clear();
        extraBalls.Clear();
        ScheduleNextSpawn();
    }

    public override void OnNetworkDespawn()
    {
        boxes.Clear();
        extraBalls.Clear();
    }

    void Update()
    {
        if (!IsServer || !IsSpawned) return;

        boxes.RemoveAll(b => b == null || !b.IsSpawned);
        extraBalls.RemoveAll(b => b == null || !b.IsSpawned);

        GameManager gm = GameManager.Instance;
        if (gm == null || !gm.IsPlaying)
        {
            nextSpawnTime += Time.deltaTime;
            return;
        }

        foreach (PowerUpBox box in boxes.ToArray())
        {
            if (box.Tick(Time.deltaTime))
            {
                Despawn(box);
                continue;
            }
            CheckCollection(box);
        }

        if (boxes.Count < maxBoxes && Time.time >= nextSpawnTime)
        {
            SpawnBox();
            ScheduleNextSpawn();
        }
    }

    void ScheduleNextSpawn()
    {
        nextSpawnTime = Time.time + Random.Range(minSpawnDelay, maxSpawnDelay);
    }

    void SpawnBox()
    {
        Vector2 pos;
        int tries = 0;
        do
        {
            pos = new Vector2(Random.Range(-spawnArea.x, spawnArea.x),
                              Random.Range(-spawnArea.y, spawnArea.y));
            tries++;
        }
        while (pos.magnitude < minDistanceFromCenter && tries < 20);

        PowerUpBox box = Instantiate(boxPrefab, pos, Quaternion.identity);
        box.NetworkObject.Spawn(true);
        box.Type = (PowerUpType)Random.Range(0, PowerUpBox.TypeCount);
        boxes.Add(box);
    }

    void CheckCollection(PowerUpBox box)
    {
        double now = NetClock.Now;
        foreach (Ball ball in Ball.All)
        {
            if (ball.State.mode != BallMode.Moving || ball.LastHitter == null) continue;
            if (!box.Touches(ball.PositionAt(now), ball.radius)) continue;

            Collect(box, ball);
            return;
        }
    }

    void Collect(PowerUpBox box, Ball ball)
    {
        PlayerSide side = ball.LastHitter.Value;
        PlayerPaddle paddle = PlayerPaddle.Get(side);

        switch (box.Type)
        {
            case PowerUpType.MultiBall:
                SpawnExtraBall(ball, side);
                break;

            case PowerUpType.SpeedUp:
                if (paddle != null) paddle.GrantSpeedBoost(speedBoostDuration);
                break;

            case PowerUpType.MoveBall:
                if (paddle != null) paddle.GrantMoveBall(moveBallDuration);
                break;

            case PowerUpType.Grow:
                if (paddle != null) paddle.GrantGrow(growDuration);
                break;

            case PowerUpType.Catch:
                if (paddle != null) paddle.GrantCatch();
                break;
        }

        Despawn(box);
    }

    void SpawnExtraBall(Ball source, PlayerSide side)
    {
        if (extraBalls.Count >= maxExtraBalls) return;

        double now = NetClock.Now;
        source.State.Simulate(now, source.LimitY, out Vector2 position, out Vector2 velocity);
        if (velocity.sqrMagnitude < 0.01f) return;

        float angle = position.y > 0f ? -extraBallAngle : extraBallAngle;
        if (velocity.x < 0f) angle = -angle;
        Vector2 direction = Quaternion.Euler(0f, 0f, angle) * velocity.normalized;
        float speed = source.State.baseSpeed * extraBallSpeed;

        Ball extra = Instantiate(extraBallPrefab, position, Quaternion.identity);
        extra.NetworkObject.Spawn(true);
        extra.LaunchExtra(position, direction * speed, side);
        extraBalls.Add(extra);
    }

    void Despawn(PowerUpBox box)
    {
        boxes.Remove(box);
        if (box.IsSpawned) box.NetworkObject.Despawn(true);
    }

    public void RemoveExtraBall(Ball ball)
    {
        extraBalls.Remove(ball);
        ball.RemoveFromGame();
    }

    public void ClearExtraBalls()
    {
        foreach (Ball b in new List<Ball>(extraBalls))
            if (b != null) b.RemoveFromGame();
        extraBalls.Clear();
    }

    public void ClearAll()
    {
        if (!IsServer) return;

        ClearExtraBalls();

        foreach (PowerUpBox box in new List<PowerUpBox>(boxes))
            if (box != null && box.IsSpawned) box.NetworkObject.Despawn(true);
        boxes.Clear();

        if (PlayerPaddle.Red != null) PlayerPaddle.Red.ClearEffects();
        if (PlayerPaddle.Blue != null) PlayerPaddle.Blue.ClearEffects();

        ScheduleNextSpawn();
    }
}
