using UnityEngine;

/// <summary>
/// 场地相机跟随：Lerp 平滑跟随玩家，开场直接贴合以免起步滑动；
/// 视野按 ArenaBounds 钳制，保证不越出竞技场而露出背景。
/// 依赖场景中存在 ArenaBounds 锚点，且相机 Tag 为 MainCamera。
/// </summary>
public class CameraFollow : MonoBehaviour
{
    [Header("目标（留空则自动取 GameManager.Player）")]
    public Transform target;

    [Header("视野")]
    [Tooltip("缩小后的正交视野（垂直半高）。比竞技场小 → 局部跟随效果")]
    public float orthoSize = 6f;

    [Tooltip("平滑跟随速度（越大越跟手）")]
    public float followSpeed = 5f;

    private Camera mCam;
    private bool mSnapped; // 开场瞬移只做一次

    private void Awake()
    {
        mCam = GetComponent<Camera>();
        if (mCam == null) return;

        if (mCam.orthographic)
        {
            mCam.orthographicSize = orthoSize;
        }
    }

    private void LateUpdate()
    {
        if (mCam == null) return;

        // 目标未指定 → 自动取玩家
        if (target == null && GameManager.Instance != null && GameManager.Instance.Player != null)
        {
            target = GameManager.Instance.Player.transform;
        }
        if (target == null) return;

        // 1. 平滑跟随（开场瞬移一次）
        Vector3 pos;
        if (!mSnapped)
        {
            pos = target.position;
        }
        else
        {
            pos = Vector3.Lerp(transform.position, target.position, followSpeed * Time.deltaTime);
        }

        // 2. 视野钳制在竞技场内（相机半宽/半高不能越出边界）
        var bounds = ArenaBounds.Current;
        if (bounds != null)
        {
            float camHalfH = mCam.orthographicSize;
            float camHalfW = camHalfH * mCam.aspect;
            pos.x = Mathf.Clamp(pos.x, bounds.MinX + camHalfW, bounds.MaxX - camHalfW);
            pos.y = Mathf.Clamp(pos.y, bounds.MinY + camHalfH, bounds.MaxY - camHalfH);
            mSnapped = true;
        }

        pos.z = transform.position.z; // 保持原有 Z
        transform.position = pos;
    }
}
