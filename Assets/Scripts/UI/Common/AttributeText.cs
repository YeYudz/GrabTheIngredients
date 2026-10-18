using UnityEngine;

namespace GameUI
{
	/// <summary>
	/// 属性文案格式化（UI 层唯一口径）。
	///
	/// 存在的意义：攻速有两条独立通道 —— 平坦基础攻速（PlayerController.AttackSpeed）
	/// 与倍率加成（PlayerController.AttackSpeedBonus），而局内所有攻速增益只写后者。
	/// 历史上状态面板/结算面板直接读 AttackSpeed，导致攻速卡生效了但 UI 永远显示 2.00。
	/// 这里把「取哪个值 + 怎么显示」收敛到一处，避免再出现双份真相。
	/// </summary>
	public static class AttributeText
	{
		/// <summary>
		/// 攻击速度文案：最终值（次/秒），有加成时追加 "(+45%)" 便于一眼确认增益已生效。
		/// 无加成时只输出数值，与其它属性行的观感保持一致。
		/// </summary>
		public static string AttackSpeed(PlayerController player)
		{
			if (player == null) return "-";

			string value = player.FinalAttackSpeed.ToString("F2");
			if (Mathf.Approximately(player.AttackSpeedBonus, 0f)) return value;

			string sign = player.AttackSpeedBonus > 0f ? "+" : "";
			return $"{value}  ({sign}{player.AttackSpeedBonus * 100f:F0}%)";
		}
	}
}
