// Field measurements shared by the game code and the scene builder (world units).
public static class PongField
{
    public const float WallInnerY = 4.75f;      // inside edge of the top/bottom walls
    public const float WallThickness = 0.5f;
    public const float PaddleX = 7.5f;          // paddles sit at -PaddleX (red) and +PaddleX (blue)
    public const float PaddleHalfWidth = 0.15f;
    public const float GoalLineX = 9.3f;        // a ball past this line is a point

    // The x the ball's center is at when it touches that side's paddle.
    public static float ContactX(PlayerSide side, float ballRadius)
    {
        float face = PaddleX - PaddleHalfWidth - ballRadius;
        return side == PlayerSide.Red ? -face : face;
    }

    // Direction (+1 / -1) a ball must travel along x to reach that side.
    public static float TowardSide(PlayerSide side) => side == PlayerSide.Red ? -1f : 1f;
}
