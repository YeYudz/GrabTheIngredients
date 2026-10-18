using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 梯形 Image：顶边按 TopScale 向 TopCenterX 收窄（顶窄底宽），为音游滑轨提供仿透视。
/// 以 BaseMeshEffect 改写顶点实现，透视免于单独出图；TopScale=1 时退化为普通矩形。
/// 顶边按最大 y 识别，Simple 填充模式下顶点流最规整（Sliced/Tiled 会重复顶点）。
/// </summary>
[RequireComponent(typeof(Image))]
public class TrapezoidImage : BaseMeshEffect
{
    [Tooltip("顶部宽度比例 0~1（1=矩形，越小顶收得越窄）")]
    [SerializeField] private float m_TopScale = 1f;

    [Tooltip("顶部收缩中心（局部 x 坐标，0=自身中心；透视收拢时设为消失点的局部 x）")]
    [SerializeField] private float m_TopCenterX = 0f;

    // 顶点流缓存（静态复用，避免每帧重建网格产生 GC）
    private static readonly List<UIVertex> s_VertexStream = new List<UIVertex>(64);

    /// <summary>顶部宽度比例（0~1），修改后立即重建顶点</summary>
    public float TopScale
    {
        get => m_TopScale;
        set
        {
            m_TopScale = Mathf.Clamp01(value);
            if (graphic != null) graphic.SetVerticesDirty();
        }
    }

    /// <summary>顶部收缩中心（局部 x 坐标），修改后立即重建顶点</summary>
    public float TopCenterX
    {
        get => m_TopCenterX;
        set
        {
            m_TopCenterX = value;
            if (graphic != null) graphic.SetVerticesDirty();
        }
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!isActiveAndEnabled || vh.currentVertCount == 0) return;
        if (m_TopScale >= 0.999f) return;

        // VertexHelper 无单点读写 API，取三角形流处理后整流回写
        s_VertexStream.Clear();
        vh.GetUIVertexStream(s_VertexStream);

        // 先求矩形顶边 y（顶点位于该 y 的视为顶部顶点）
        float maxY = float.MinValue;
        for (int i = 0; i < s_VertexStream.Count; i++)
        {
            if (s_VertexStream[i].position.y > maxY) maxY = s_VertexStream[i].position.y;
        }

        // 顶部顶点向收缩中心收窄（x' = c + (x - c) × TopScale）
        for (int i = 0; i < s_VertexStream.Count; i++)
        {
            var v = s_VertexStream[i];
            if (!Mathf.Approximately(v.position.y, maxY)) continue;
            v.position.x = m_TopCenterX + (v.position.x - m_TopCenterX) * m_TopScale;
            s_VertexStream[i] = v;
        }

        vh.Clear();
        vh.AddUIVertexTriangleStream(s_VertexStream);
    }
}
