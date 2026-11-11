using System;
using Unity.Netcode;
using UnityEngine;

public enum BallMode : byte
{
    Hidden,   // not in the game
    Idle,     // sitting still at pos (between matches)
    Moving,   // travelling from pos with vel since time (waits at pos if time is in the future)
    Frozen,   // paused at pos because of a connection problem
    Stuck     // held on a paddle by the Catch power-up
}

// Everything needed to know where a ball is at ANY moment. Instead of streaming positions,
// the host only sends a new state when something changes (a hit, a serve, a steer...).
// Both players compute the position from it for the same clock, so they see the same thing.
public struct BallState : INetworkSerializable
{
    public BallMode mode;
    public byte owner;        // last player to hit it: 0 nobody (white), 1 red, 2 blue
    public byte fastFrom;     // side that fired it with Catch (travels at 2x until the other side hits it)
    public byte hitSeq;       // +1 on every paddle hit / catch / shot, so stale hit reports can be ignored
    public ushort version;    // +1 on every change
    public Vector2 pos;
    public Vector2 vel;
    public double time;       // when the ball was at pos (shared server clock)
    public float baseSpeed;   // speed without the Catch 2x
    public float stuckOffset; // Stuck: ball y relative to the paddle center

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

    // Where a moving ball is at time t, bouncing off the top and bottom walls.
    // limitY = how far the ball's center can go from the middle (wall minus radius).
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

        // Unfold the wall bounces: y travels on a line, then gets folded back into the field.
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

    // When a moving ball's center reaches x (can be in the past or future; +inf if never).
    public double TimeAtX(float x)
    {
        if (mode != BallMode.Moving || Mathf.Abs(vel.x) < 0.0001f) return double.PositiveInfinity;
        return time + (x - pos.x) / vel.x;
    }
}
