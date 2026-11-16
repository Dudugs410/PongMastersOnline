using TMPro;
using Unity.Netcode;
using UnityEngine;

public enum PowerUpType : byte { MultiBall, SpeedUp, MoveBall, Grow, Catch }

// A box in the middle of the field. When a ball passes through it, the player who
// last touched that ball (the ball's color) gets the power-up. PowerUpManager does the checking.
public class PowerUpBox : NetworkBehaviour
{
    public const int TypeCount = 5;

    public SpriteRenderer spriteRenderer;
    public TMP_Text label;            // optional: a TextMeshPro child showing a short label
    public float lifetime = 15f;      // box disappears if nobody takes it
    public float halfSize = 0.45f;

    [Header("Visuals")]
    public Color multiBallColor = new(1f, 0.85f, 0.1f);   // yellow
    public Color speedUpColor = new(0.2f, 1f, 0.3f);      // green
    public Color moveBallColor = new(0.9f, 0.3f, 1f);     // purple
    public Color growColor = new(1f, 0.55f, 0.1f);        // orange
    public Color catchColor = new(0.2f, 0.9f, 1f);        // cyan

    [Header("Icons (optional: replace the colored box and label)")]
    public SpriteRenderer iconRenderer;
    public Sprite multiBallIcon;
    public Sprite speedUpIcon;
    public Sprite moveBallIcon;
    public Sprite growIcon;
    public Sprite catchIcon;

    readonly NetworkVariable<PowerUpType> type = new(PowerUpType.MultiBall);

    public PowerUpType Type
    {
        get => type.Value;
        set { if (IsServer) type.Value = value; }
    }

    public bool Collected { get; private set; }
    float age;   // only counts while the game is actually being played

    public override void OnNetworkSpawn()
    {
        type.OnValueChanged += OnTypeChanged;
        ApplyVisuals(type.Value);
        age = 0f;
        Collected = false;
    }

    public override void OnNetworkDespawn()
    {
        type.OnValueChanged -= OnTypeChanged;
    }

    // Host: called every frame while playing. Returns true once the box has expired.
    public bool Tick(float deltaTime)
    {
        age += deltaTime;
        return age > lifetime;
    }

    public bool Touches(Vector2 ballPos, float ballRadius)
    {
        Vector2 d = ballPos - (Vector2)transform.position;
        float reach = halfSize + ballRadius;
        return Mathf.Abs(d.x) <= reach && Mathf.Abs(d.y) <= reach;
    }

    public void MarkCollected() => Collected = true;

    void OnTypeChanged(PowerUpType previous, PowerUpType current) => ApplyVisuals(current);

    void ApplyVisuals(PowerUpType t)
    {
        (Color color, string text, Sprite icon) = t switch
        {
            PowerUpType.MultiBall => (multiBallColor, "x2", multiBallIcon),
            PowerUpType.SpeedUp => (speedUpColor, "SPD", speedUpIcon),
            PowerUpType.MoveBall => (moveBallColor, "AIM", moveBallIcon),
            PowerUpType.Grow => (growColor, "BIG", growIcon),
            _ => (catchColor, "CATCH", catchIcon)
        };

        // With an icon, show only the icon; otherwise the colored box with its label.
        bool hasIcon = icon != null && iconRenderer != null;
        if (iconRenderer)
        {
            iconRenderer.sprite = icon;
            iconRenderer.enabled = hasIcon;
        }

        spriteRenderer.enabled = !hasIcon;
        spriteRenderer.color = color;
        if (label)
        {
            label.text = text;
            label.enabled = !hasIcon;
        }
    }
}
