using System;

/// <summary>
/// 存档版本号（语义化 major.minor.patch）。
/// 版本历史按时间排列，每个版本对应 Migrations/ 下的一个迁移脚本。
/// </summary>
public static class SaveVersion
{
    /// <summary>当前存档版本</summary>
    public const string CURRENT_VERSION = "1.3.0";

    /// <summary>旧版无版本号的裸存档（MetaSaveData.json）视为该版本，用于首次迁移</summary>
    public const string LEGACY_VERSION = "0.0.0";

    /// <summary>当前 Unix 时间戳（秒）</summary>
    public static long NowUnix()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    /// <summary>
    /// 语义化版本比较：v1 &lt; v2 返回负数，相等返回 0，v1 &gt; v2 返回正数
    /// 无法解析的段视为 0，保证任意字符串都能比较而不抛异常
    /// </summary>
    public static int Compare(string v1, string v2)
    {
        string[] p1 = (v1 ?? "").Split('.');
        string[] p2 = (v2 ?? "").Split('.');
        int len = System.Math.Max(p1.Length, p2.Length);
        for (int i = 0; i < len; i++)
        {
            int a = i < p1.Length && int.TryParse(p1[i], out int x) ? x : 0;
            int b = i < p2.Length && int.TryParse(p2[i], out int y) ? y : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }
}
