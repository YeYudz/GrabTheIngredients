using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 对象池工厂（基于 YokiFrame.PoolKit）：子弹、敌人、掉落物等高频对象的复用，
/// 消除运行时 Instantiate/Destroy 造成的 GC 峰值，是同屏百级敌人保持帧率的前提。
/// </summary>
public static class PoolFactory
{
    /// <summary>按预制体路径索引的对象池（路径 = 唯一 key）</summary>
    private static readonly Dictionary<string, ObjectPool<GameObject>> Pools = new();

    /// <summary>从对象池取对象（首次自动创建池 + 实例化）</summary>
    public static GameObject Get(string prefabPath, Vector3 position, Quaternion rotation)
    {
        var pool = GetOrCreatePool(prefabPath);

        GameObject go = pool.Allocate();
        go.transform.SetPositionAndRotation(position, rotation);
        return go;
    }

    /// <summary>归还对象到对象池（隐藏 + 回收）</summary>
    public static void Release(string prefabPath, GameObject go)
    {
        if (!Pools.TryGetValue(prefabPath, out var pool))
        {
            // 池不存在（异常情况）直接销毁
            Object.Destroy(go);
            return;
        }

        go.SetActive(false);
        pool.Recycle(go);
    }

    /// <summary>预热指定预制体的池（进入战斗的加载页调用，避免正式战斗时 Instantiate 卡顿）</summary>
    public static void Prewarm(string prefabPath, int count)
    {
        var pool = GetOrCreatePool(prefabPath);

        // 缓存已足够则跳过（分帧预热时同一路径会被重复调用）
        int missing = count - pool.CurCount;
        if (missing <= 0) return;

        // 必须一次性借出全部缺口、再统一归还：
        // 对象池是栈结构，"取一个还一个"每次都会弹回同一个实例，永远无法真正扩充缓存
        var allocated = new List<GameObject>(missing);
        for (int i = 0; i < missing; i++)
        {
            allocated.Add(pool.Allocate());
        }
        foreach (var go in allocated)
        {
            pool.Recycle(go);
        }
    }

    /// <summary>当前池内已缓存（可复用）的对象数量；池不存在返回 0。用于预热结果自检/日志</summary>
    public static int GetCachedCount(string prefabPath)
        => Pools.TryGetValue(prefabPath, out var pool) ? pool.CurCount : 0;

    private static ObjectPool<GameObject> GetOrCreatePool(string prefabPath)
    {
        if (Pools.TryGetValue(prefabPath, out var pool))
            return pool;

        // 创建池：factory 从 Resources 加载预制体并实例化
        pool = PoolKit.Create<GameObject>(
            factory: () =>
            {
                var prefab = Resources.Load<GameObject>(prefabPath);
                if (prefab == null)
                {
                    LogKit.Error($"[PoolFactory] 找不到预制体: {prefabPath}");
                    return new GameObject($"Missing:{prefabPath}");
                }
                var go = Object.Instantiate(prefab);
                // 自动挂载池生命周期标记（归还时触发）
                var marker = go.GetComponent<Poolable>();
                if (marker == null)
                    marker = go.AddComponent<Poolable>();
                marker.PoolKey = prefabPath;
                return go;
            },
            onAllocated: go => go.SetActive(true),
            onRecycled: go =>
            {
                var marker = go.GetComponent<Poolable>();
                if (marker != null) marker.OnRecycled();
                go.SetActive(false);
            });

        Pools.Add(prefabPath, pool);
        return pool;
    }

    /// <summary>释放所有池（场景切换时调用）</summary>
    public static void ClearAll()
    {
        foreach (var pool in Pools.Values)
            pool.Dispose();
        Pools.Clear();

        // 一并清空序列帧的跨实例共享缓存。
        // 池内对象是被 SetActive(false) 复用的（PoolKit 的 Dispose 不会 Destroy GameObject），
        // 其 SequenceFrameAnimator.OnDestroy 不会随 ClearAll 执行，靠引用计数无法收敛；
        // 若不在此显式清理，切场景后缓存里会留下无主的 Sprite。
        SequenceFrameAnimator.ClearSharedFrames();
    }
}

/// <summary>
/// 池化对象生命周期标记：挂载在从池取出的 GameObject 上
/// 提供 OnAllocated（取出）/ OnRecycled（归还）钩子，供各对象重置状态
/// </summary>
public class Poolable : MonoBehaviour
{
    /// <summary>所属池 key（用于自动归还）</summary>
    public string PoolKey { get; set; }

    /// <summary>取出时回调（子类或事件覆盖）</summary>
    public System.Action OnAllocatedCallback { get; set; }

    /// <summary>归还时回调</summary>
    public System.Action OnRecycledCallback { get; set; }

    public void OnAllocated()
    {
        OnAllocatedCallback?.Invoke();
    }

    public void OnRecycled()
    {
        OnRecycledCallback?.Invoke();
    }

    /// <summary>便捷：将自身归还对象池</summary>
    public void RecycleSelf()
    {
        if (!string.IsNullOrEmpty(PoolKey))
        {
            PoolFactory.Release(PoolKey, gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
