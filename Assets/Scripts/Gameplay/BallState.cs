using System;
using Unity.Netcode;
using UnityEngine;

public enum BallMode : byte
{
    Hidden,
    Idle,
    Moving,
    Frozen,
    Stuck
}

public struct BallState : INetworkSerializable
{
    public BallMode mode;
    public byte owner;
    public byte fastFrom;
    public byte hitSeq;
    public ushort version;
    public Vector2 pos;
    public Vector2 vel;
    public double time;
    public float baseSpeed;
    public float stuckOffset;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref mode);
        s.SerializeValue(ref owner);
        s.SerializeValue(ref fastFrom);
        s.SerializeValue(ref hitSeq);
        s.SerializeValue(ref version);
        s.SerializeValue(ref pos);
        s.SerializeValue(ref vel);
        s.SerializeValue(ref time);
        s.SerializeValue(ref baseSpeed);
        s.SerializeValue(ref stuckOffset);
    }

    public PlayerSide? Owner => owner == 0 ? null : (PlayerSide)owner;

    public void Simulate(double t, float limitY, out Vector2 p, out Vector2 v)
    {
        v = vel;
        if (mode != BallMode.Moving)
        {
            p = pos;
            return;
        }

        float dt = (float)Math.Max(0.0, t - time);
        p = pos + vel * dt;

        float span = 2f * limitY;
        float u = p.y + limitY;
        int bounces = Mathf.FloorToInt(u / span);
        float r = u - bounces * span;
        if ((bounces & 1) == 0)
        {
            p.y = r - limitY;
        }
        else
        {
            p.y = span - r - limitY;
            v.y = -v.y;
        }
    }

    public double TimeAtX(float x)
    {
        if (mode != BallMode.Moving || Mathf.Abs(vel.x) < 0.0001f) return double.PositiveInfinity;
        return time + (x - pos.x) / vel.x;
    }
}
