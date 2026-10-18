using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 空间网格：把全图遍历 O(N) 降为局部查询 O(k)。
/// 敌人按坐标注册到固定格子，索敌 / 近战 / AOE 只检查中心点周围若干格，再做精确距离剔除。
/// ⚠ 仅作索引、不持有引用所有权：池回收或销毁时必须 Unregister，否则会命中已回收对象。
/// </summary>
public static class SpatialGrid
{
    /// <summary>格子边长（单位：世界单位）。越大查询越快但粗筛更粗，越小越精确但字典更大</summary>
    private const float CELL_SIZE = 8f;

    /// <summary>格子坐标 → 格子内敌人列表</summary>
    private static readonly Dictionary<Vector2Int, List<EnemySimple>> Cells =
        new Dictionary<Vector2Int, List<EnemySimple>>();

    /// <summary>敌人 → 所在格子坐标（快速定位，避免重复注册/跨格检测）</summary>
    private static readonly Dictionary<EnemySimple, Vector2Int> EnemyCell =
        new Dictionary<EnemySimple, Vector2Int>();

    /// <summary>注册敌人（OnEnable 调用）</summary>
    public static void Register(EnemySimple enemy)
    {
        if (enemy == null || EnemyCell.ContainsKey(enemy)) return;
        var cell = GetCell(enemy.transform.position);
        AddToCell(cell, enemy);
        EnemyCell[enemy] = cell;
    }

    /// <summary>注销敌人（OnDisable/销毁前调用）</summary>
    public static void Unregister(EnemySimple enemy)
    {
        if (enemy == null || !EnemyCell.TryGetValue(enemy, out var cell)) return;
        if (Cells.TryGetValue(cell, out var list))
        {
            list.Remove(enemy);
            if (list.Count == 0) Cells.Remove(cell);
        }
        EnemyCell.Remove(enemy);
    }

    /// <summary>敌人移动后更新格子（跨格才迁移，Update 每帧调用开销极小）</summary>
    public static void UpdatePosition(EnemySimple enemy)
    {
        if (enemy == null || !EnemyCell.TryGetValue(enemy, out var oldCell)) return;
        var newCell = GetCell(enemy.transform.position);
        if (newCell == oldCell) return;

        if (Cells.TryGetValue(oldCell, out var oldList))
        {
            oldList.Remove(enemy);
            if (oldList.Count == 0) Cells.Remove(oldCell);
        }
        AddToCell(newCell, enemy);
        EnemyCell[enemy] = newCell;
    }

    /// <summary>
    /// 获取半径内的敌人（中心格子 + 周围 range 个格子粗筛，再做精确距离剔除）
    /// result 由调用方传入复用，避免每次分配 List
    /// </summary>
    public static void GetInRadius(Vector3 center, float radius, List<EnemySimple> result)
    {
        result.Clear();
        if (radius <= 0f) return;

        var centerCell = GetCell(center);
        int range = Mathf.CeilToInt(radius / CELL_SIZE);
        float radiusSq = radius * radius;

        for (int x = -range; x <= range; x++)
        {
            for (int y = -range; y <= range; y++)
            {
                var cell = new Vector2Int(centerCell.x + x, centerCell.y + y);
                if (!Cells.TryGetValue(cell, out var list)) continue;

                foreach (var enemy in list)
                {
                    if (enemy == null || !enemy.isActiveAndEnabled) continue;
                    // 精确距离剔除（格子只是粗筛）
                    if (((Vector2)(enemy.transform.position - center)).sqrMagnitude <= radiusSq)
                    {
                        result.Add(enemy);
                    }
                }
            }
        }
    }

    /// <summary>清空全部网格（场景切换/对象池整体清理时调用）</summary>
    public static void Clear()
    {
        Cells.Clear();
        EnemyCell.Clear();
    }

    private static Vector2Int GetCell(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / CELL_SIZE),
            Mathf.FloorToInt(worldPos.y / CELL_SIZE));
    }

    private static void AddToCell(Vector2Int cell, EnemySimple enemy)
    {
        if (!Cells.TryGetValue(cell, out var list))
        {
            list = new List<EnemySimple>();
            Cells[cell] = list;
        }
        list.Add(enemy);
    }
}
