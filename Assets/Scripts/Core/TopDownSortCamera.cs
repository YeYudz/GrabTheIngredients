using UnityEngine;

[RequireComponent(typeof(Camera))]
public class TopDownSortCamera : MonoBehaviour
{
    // 俯视角：垂直轴 (0, 1, 0)，画面越靠下（Y 越小）越靠近相机 → 画在上层
    private static readonly Vector3 k_sortAxis = new Vector3(0f, 1f, 0f);

    private void Awake()
    {
        var cam = GetComponent<Camera>();
        cam.transparencySortMode = TransparencySortMode.CustomAxis;
        cam.transparencySortAxis = k_sortAxis;
    }
}
