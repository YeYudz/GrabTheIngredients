using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class RunResultPanelData : IUIData
	{
		public BattleStatsSnapshot Snapshot;   // 本局（整局）战斗统计快照
	}

	/// <summary>
	/// 通关结算面板：战绩 / 收获 / 属性三页签的只读展示。
	/// 与阵亡、阶段结算共用 ResultPanelBase，差异只在数据源与按钮语义——
	/// 返回主菜单时广播 RunResultConfirmed，由 GameManager 记录胜利，UI 不直接改存档。
	/// </summary>
	public partial class RunResultPanel : ResultPanelBase
	{
		protected override void OnInit(IUIData data = null)
		{
			mData = data as RunResultPanelData ?? new RunResultPanelData();

			ResolveRefs();
			BindButtons();
			BindTabs();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as RunResultPanelData ?? mData;
			RefreshContent();
		}

		// ===== 内容填充 =====

		private void RefreshContent()
		{
			var s = mData.Snapshot;
			if (s == null) s = new BattleStatsSnapshot();

			if (mTitleText != null) mTitleText.text = "通关结算 · 本局统计";

			ClearAllLists();
			BuildStats(s);
			BuildRewards(s);
			BuildAttrs(s);
		}

		/// <summary>战绩页：整局统计（名称 | 数值）；顶部先给通关提示 + 本局奖励行</summary>
		private void BuildStats(BattleStatsSnapshot s)
		{
			if (mStatsContent == null) return;

			AddListLabel(mStatsContent, "成功通关最终 BOSS！");
			AddRewardRows(s);
			AddRow(mStatsContent, mStatsRowMaster, "平均 DPS", $"{s.RunDps:F0}");
			AddRow(mStatsContent, mStatsRowMaster, "总伤害", $"{s.RunDamage:F0}");
			AddRow(mStatsContent, mStatsRowMaster, "总击杀", $"{s.RunKills}");
			AddRow(mStatsContent, mStatsRowMaster, "最大连杀", $"{s.RunMaxStreak}");
			AddRow(mStatsContent, mStatsRowMaster, "通关波次", $"{s.RunWavesCleared}");
			AddRow(mStatsContent, mStatsRowMaster, "本局用时", FormatTime(s.RunTime));
			AddRow(mStatsContent, mStatsRowMaster, "战场炒菜", $"{s.RunFieldCookCount} 次");
			AddRow(mStatsContent, mStatsRowMaster, "击败 BOSS", $"{s.BossesDefeated} 个");
			AddRow(mStatsContent, mStatsRowMaster, "最高等级", $"{s.MaxLevel}");
			if (s.RunCoins > 0) AddRow(mStatsContent, mStatsRowMaster, "本局获得钱币", $"{s.RunCoins}");

			AddListLabel(mStatsContent, "（本局收集食材将返还局外背包）", 22);
		}

		/// <summary>本局奖励（名值行）——挂在战绩页（收获页是物品格网格，容不下名值行）</summary>
		private void AddRewardRows(BattleStatsSnapshot s)
		{
			if (s.Rewards == null || s.Rewards.Count == 0) return;

			AddListLabel(mStatsContent, "【本局奖励】");
			foreach (var raw in s.Rewards)
			{
				SplitRewardLine(raw, out string name, out string value);
				AddRow(mStatsContent, mStatsRowMaster, name, value);
			}
		}

		/// <summary>收获页：整局炒出的成品菜 + 收集食材（物品格网格；空态用一个格子提示）</summary>
		private void BuildRewards(BattleStatsSnapshot s)
		{
			if (mRewardsContent == null) return;

			bool hasDishes = s.RunDishes != null && s.RunDishes.Count > 0;
			bool hasIngredients = s.RunIngredients != null && s.RunIngredients.Count > 0;
			if (!hasDishes && !hasIngredients)
			{
				AddEmptyItemCell(mRewardsContent, "（无）");
				return;
			}

			if (hasDishes)
				foreach (var dish in s.RunDishes)
					AddItemCell(mRewardsContent, DishIconPath(dish.recipeId),
						DishDisplayName(dish.recipeId, dish.star), dish.count);

			foreach (var ing in s.RunIngredients)
				AddItemCell(mRewardsContent, IngredientIconPath(ing.id), IngredientName(ing.id), ing.count);
		}

		/// <summary>属性页：通关时玩家全部属性（从 PlayerController 读取最终值，而非变化量）</summary>
		private void BuildAttrs(BattleStatsSnapshot s)
		{
			if (mAttrsContent == null) return;

			var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
			if (player == null)
			{
				AddListLabel(mAttrsContent, "（无法读取玩家属性）", 22);
				return;
			}

			AddRow(mAttrsContent, mAttrsRowMaster, "生命", $"{player.MaxHp:F0}");
			AddRow(mAttrsContent, mAttrsRowMaster, "攻击", $"{player.Attack:F0}");
			AddRow(mAttrsContent, mAttrsRowMaster, "物理防御", $"{player.PhysDef:F0}");
			AddRow(mAttrsContent, mAttrsRowMaster, "元素防御", $"{player.ElemDef:F0}");
			AddRow(mAttrsContent, mAttrsRowMaster, "移动速度", $"{player.MoveSpeed:F1}");
			AddRow(mAttrsContent, mAttrsRowMaster, "攻击速度", AttributeText.AttackSpeed(player));
			AddRow(mAttrsContent, mAttrsRowMaster, "闪避", $"{player.DodgeChance * 100f:F1}%");
			AddRow(mAttrsContent, mAttrsRowMaster, "暴击率", $"{player.CritChance * 100f:F1}%");
			AddRow(mAttrsContent, mAttrsRowMaster, "暴击伤害", $"{player.CritDamage * 100f:F0}%");
			AddRow(mAttrsContent, mAttrsRowMaster, "物理伤害加成", $"{player.PhysDamageBonus * 100f:F0}%");
			AddRow(mAttrsContent, mAttrsRowMaster, "元素伤害加成", $"{player.ElemDamageBonus * 100f:F0}%");
			AddRow(mAttrsContent, mAttrsRowMaster, "最大法力", $"{player.MaxMana:F0}");
		}

		// ===== 按钮 =====

		private void BindButtons()
		{
			// 返回主菜单：发 RunResultConfirmed（GameManager 记录胜利局 + 回主菜单）
			var menuButton = mMenuButton;
			if (menuButton == null) menuButton = FindComponent<Button>("MenuButton");
			if (menuButton != null)
			{
				menuButton.onClick.RemoveAllListeners();
				menuButton.onClick.AddListener(() =>
				{
					if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
					EventKit.Type.Send(new GameEvents.RunResultConfirmed());
				});
			}

			// 重新开始本局
			var restartButton = mRestartButton;
			if (restartButton == null) restartButton = FindComponent<Button>("RestartButton");
			if (restartButton != null)
			{
				restartButton.onClick.RemoveAllListeners();
				restartButton.onClick.AddListener(() =>
				{
					if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
					EventKit.Type.Send(new GameEvents.RestartRun());
				});
			}
		}
	}
}
