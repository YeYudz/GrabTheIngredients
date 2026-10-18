using UnityEngine;

/// <summary>
/// 竞技场边界锚点：作为单一矩形数据源，同时供相机钳制与玩家移动钳制，
/// 避免两处各写一份边界而出现「画面露出背景外」或「玩家越界」。场景内唯一。
/// </summary>
public class ArenaBounds : MonoBehaviour
{
    /// <summary>当前生效的竞技场边界（场景内唯一，OnEnable 注册）</summary>
    public static ArenaBounds Current { get; private set; }

    [Header("竞技场范围（世界单位）")]
    public Vector2 center = Vector2.zero;
    public float halfWidth = 16f;   // 总宽 = halfWidth × 2
    public float halfHeight = 9f;   // 总高 = halfHeight × 2

    public float MinX => center.x - halfWidth;
    public float MaxX => center.x + halfWidth;
    public float MinY => center.y - halfHeight;
    public float MaxY => center.y + halfHeight;

    private void OnEnable() => Current = this;

    private void OnDisable()
    {
        if (Current == this) Current = null;
    }
}
