using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 武器轨道视图：管理近战轴枢的生成、旋转与渲染，挂在玩家下随移动自动跟随。
/// 业务层只算角度、每帧经 ApplyPose 下发，使表现与判定共用同一份姿态数据。
/// 美术约定：武器 sprite 的 pivot 在柄部、刃朝 +Y，按 1 世界单位总长绘制。
/// </summary>
public class WeaponOrbitView : MonoBehaviour
{
    /// <summary>轨道根节点名（场景中手动创建；缺失时自动创建保底）</summary>
    private const string kOrbitRootName = "WeaponOrbitRoot";

    private Transform mRoot;

    /// <summary>近战武器轴枢（与 AutoAttack 的近战列表按索引对齐；无视觉时为 null）</summary>
    private readonly List<Transform> mPivots = new();

    /// <summary>刃身半宽默认值（世界单位；视觉缺失或读取失败时的判定回退）</summary>
    private const float kDefaultBladeHalfWidth = 0.15f;

    /// <summary>刃身半宽缓存（世界单位，与 mPivots 按索引对齐；OBB 命中盒判定用，配置 meleeBladeWidth>0 时业务层优先用配置）</summary>
    private readonly List<float> mBladeHalfWidths = new();

    /// <summary>
    /// 确保轨道根节点存在：优先使用场景中手动创建的同名子节点，找不到才自动创建保底
    /// </summary>
    public void EnsureRoot()
    {
        if (mRoot != null) return;

        var found = transform.Find(kOrbitRootName);
        if (found != null)
        {
            mRoot = found;
            return;
        }

        // 保底：自动创建（手动创建的优先，这里仅在场景漏配时兜底）
        var go = new GameObject(kOrbitRootName);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        mRoot = go.transform;
        LogKit.Warning("[WeaponOrbitView] 场景未找到 WeaponOrbitRoot，已自动创建保底（建议手动补建）");
    }

    /// <summary>
    /// 按近战武器视觉路径列表重建轴枢（顺序与 AutoAttack 近战列表一致）
    /// 路径为空或加载失败时不生成视觉（判定不受影响），仅告警
    /// </summary>
    public void Rebuild(List<string> meleePrefabPaths)
    {
        EnsureRoot();

        // 清掉旧轴枢（轴枢全程挂在 OrbitRoot 下，随装备变化整组重建）
        for (int i = 0; i < mPivots.Count; i++)
            if (mPivots[i] != null) Destroy(mPivots[i].gameObject);
        mPivots.Clear();
        mBladeHalfWidths.Clear();

        if (meleePrefabPaths == null) return;

        for (int i = 0; i < meleePrefabPaths.Count; i++)
        {
            var path = meleePrefabPaths[i];
            if (string.IsNullOrEmpty(path))
            {
                LogKit.Warning($"[WeaponOrbitView] 近战武器 {i} 未配置 meleePrefabPath，暂无转圈视觉（判定正常）");
                mPivots.Add(null);
                mBladeHalfWidths.Add(kDefaultBladeHalfWidth);
                continue;
            }

            var prefab = Resources.Load<GameObject>(path);
            if (prefab == null)
            {
                LogKit.Warning($"[WeaponOrbitView] 近战武器视觉预制体加载失败: {path}（判定正常）");
                mPivots.Add(null);
                mBladeHalfWidths.Add(kDefaultBladeHalfWidth);
                continue;
            }

            var pivot = Instantiate(prefab, mRoot);
            pivot.name = $"WeaponPivot_{i}";
            pivot.transform.localPosition = Vector3.zero;
            mPivots.Add(pivot.transform);

            // 缓存刃身半宽：必须在首次 ApplyPose 拉伸缩放前测量（避免拉伸后取值失真）。
            // 用 sprite.vertices（网格顶点，贴合图片轮廓）经 renderer 变换取 X 向全宽/2 推导真实半宽；
            // 不用 bounds.extents.x——它是 AABB，斜画刀刃会把"刃长水平投影+刃宽"误当半宽，判定严重偏肥
            var renderer = pivot.GetComponentInChildren<SpriteRenderer>();
            float halfWidth = renderer != null && renderer.sprite != null
                ? MeasureBladeHalfWidth(renderer, pivot.transform)
                : kDefaultBladeHalfWidth;
            mBladeHalfWidths.Add(halfWidth);
        }
    }

    /// <summary>
    /// 应用第 meleeIndex 把近战武器的姿态：角度（度）、轴枢半径、刃长（sprite 沿刃长方向拉伸，不做整体缩放）
    /// </summary>
    public void ApplyPose(int meleeIndex, float angleDeg, float orbitRadius, float bladeLength)
    {
        if (meleeIndex < 0 || meleeIndex >= mPivots.Count) return;
        var pivot = mPivots[meleeIndex];
        if (pivot == null) return;

        // 轴枢位置 = 极坐标（随角度绕角色旋转；角色移动由父子层级自动跟随）
        float rad = angleDeg * Mathf.Deg2Rad;
        pivot.localPosition = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f) * orbitRadius;

        // sprite 刃朝 +Y 绘制：angle=0（+X 方向）时需旋转 -90° 使刃指向外
        pivot.localRotation = Quaternion.Euler(0f, 0f, angleDeg - 90f);

        // 刃长渲染：沿刃长方向（本地 Y）拉伸至实际刃长，保持柄宽不变（美术按 1 世界单位长度绘制）
        var scale = pivot.localScale;
        scale.y = Mathf.Max(0.1f, bladeLength);
        pivot.localScale = scale;
    }

    /// <summary>
    /// 测量刃身半宽（世界单位）：把 sprite 网格顶点经 renderer 变换到轴枢本地空间，
    /// 取 X 向（刃宽方向，刃朝 +Y）全宽的一半。轴枢本地 X 缩放链即世界单位，与判定坐标一致
    /// </summary>
    private static float MeasureBladeHalfWidth(SpriteRenderer renderer, Transform pivot)
    {
        var verts = renderer.sprite.vertices;
        if (verts == null || verts.Length == 0) return kDefaultBladeHalfWidth;

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        for (int i = 0; i < verts.Length; i++)
        {
            // 顶点：sprite 本地 → 世界 → 轴枢本地（经子节点旋转后刃身对齐 +Y，X 即纯宽度方向）
            Vector3 local = pivot.InverseTransformPoint(renderer.transform.TransformPoint(verts[i]));
            if (local.x < minX) minX = local.x;
            if (local.x > maxX) maxX = local.x;
        }

        float halfWidth = (maxX - minX) * 0.5f;
        return halfWidth > 0f ? halfWidth : kDefaultBladeHalfWidth;
    }

    /// <summary>获取第 meleeIndex 把武器的刃身半宽（世界单位，OBB 命中盒判定用）</summary>
    public float GetBladeHalfWidth(int meleeIndex)
    {
        if (meleeIndex < 0 || meleeIndex >= mBladeHalfWidths.Count) return kDefaultBladeHalfWidth;
        return mBladeHalfWidths[meleeIndex];
    }
}
