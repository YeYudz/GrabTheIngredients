/// <summary>
/// 运行中状态视图：面向 UI 的只读 DTO，承载当前生效的限时增益 / 减益。
/// 由效果层产出、状态面板只读消费，不含任何业务逻辑。
/// </summary>
public sealed class StatusRuntimeView
{
    /// <summary>状态名（如“护盾”“移动速度”）</summary>
    public string Name;

    /// <summary>状态描述（效果说明 / 当前数值）</summary>
    public string Desc;

    /// <summary>图标 Resources 路径（空 = 无图标，UI 隐藏图标节点）</summary>
    public string IconPath;

    /// <summary>剩余秒数（0 = 按次数/条件生效，UI 显示“持续”）</summary>
    public float Remain;

    /// <summary>是否为减益（false = 增益）</summary>
    public bool IsDebuff;
}
