public static class PongField
{
    public const float WallInnerY = 4.75f;
    public const float WallThickness = 0.5f;
    public const float PaddleX = 7.5f;
    public const float PaddleHalfWidth = 0.15f;
    public const float GoalLineX = 9.3f;

    public static float ContactX(PlayerSide side, float ballRadius)
    {
        float face = PaddleX - PaddleHalfWidth - ballRadius;
        return side == PlayerSide.Red ? -face : face;
    }

    public static float TowardSide(PlayerSide side) => side == PlayerSide.Red ? -1f : 1f;
}
