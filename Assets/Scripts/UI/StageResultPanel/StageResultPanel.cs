using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class StageResultPanelData : IUIData
	{
		public BattleStatsSnapshot Snapshot;
		public bool IsFinal;    // 最终BOSS（确认后进入整局结算）
		public string Title;    // 标题文案
	}

	/// <summary>
	/// 阶段结算面板：战绩 / 收获 / 属性提升三页签的只读展示。
	/// 与阵亡、整局结算共用 ResultPanelBase，差异只在数据源换成阶段快照、属性页展示 Delta* 变化量；
	/// 面板只广播 StageResultConfirmed，阶段推进由 GameManager 订阅处理。
	/// </summary>
	public partial class StageResultPanel : ResultPanelBase
	{
		[Header("底部按钮")]
		[SerializeField] private Button mContinueButton;   // 继续

		protected override void OnInit(IUIData data = null)
		{
			mData = data as StageResultPanelData ?? new StageResultPanelData();

			ResolveRefs();
			BindContinueButton();
			BindTabs();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as StageResultPanelData ?? mData;
			RefreshContent();
		}

		// ===== 内容填充 =====

		private void RefreshContent()
		{
			var s = mData.Snapshot;
			if (s == null) s = new BattleStatsSnapshot();

			if (mTitleText != null) mTitleText.text = string.IsNullOrEmpty(mData.Title) ? "阶段结算" : mData.Title;

			ClearAllLists();
			BuildStats(s);
			BuildRewards(s);
			BuildAttrs(s);
		}

		/// <summary>战绩页：本阶段统计（名称 | 数值）；顶部先列出本阶段奖励行</summary>
		private void BuildStats(BattleStatsSnapshot s)
		{
			if (mStatsContent == null) return;

			AddRewardRows(s);

			if (s.StageCount > 1)
				AddRow(mStatsContent, mStatsRowMaster, "阶段", $"{s.StageIndex}/{s.StageCount}");
			AddRow(mStatsContent, mStatsRowMaster, "平均 DPS", $"{s.StageDps:F0}");
			AddRow(mStatsContent, mStatsRowMaster, "总伤害", $"{s.StageDamage:F0}");
			AddRow(mStatsContent, mStatsRowMaster, "总击杀", $"{s.StageKills}");
			AddRow(mStatsContent, mStatsRowMaster, "最大连杀", $"{s.StageMaxStreak}");
			AddRow(mStatsContent, mStatsRowMaster, "本阶段用时", FormatTime(s.StageTime));
			AddRow(mStatsContent, mStatsRowMaster, "通关波次", $"{s.StageWavesCleared}");
			AddRow(mStatsContent, mStatsRowMaster, "战场炒菜", $"{s.StageFieldCookCount} 次");
			AddRow(mStatsContent, mStatsRowMaster, "BOSS 战品质",
				string.IsNullOrEmpty(s.BossQuality) ? "—" : s.BossQuality);
		}

		/// <summary>本阶段奖励（名值行）——挂在战绩页顶部（收获页是物品格网格，容不下名值行）</summary>
		private void AddRewardRows(BattleStatsSnapshot s)
		{
			if (s.Rewards == null || s.Rewards.Count == 0) return;

			AddListLabel(mStatsContent, "【本阶段奖励】");
			foreach (var raw in s.Rewards)
			{
				SplitRewardLine(raw, out string name, out string value);
				AddRow(mStatsContent, mStatsRowMaster, name, value);
			}
		}

		/// <summary>收获页：本阶段收集食材（物品格网格；空态用一个格子提示）</summary>
		private void BuildRewards(BattleStatsSnapshot s)
		{
			if (mRewardsContent == null) return;

			if (s.StageIngredients == null || s.StageIngredients.Count == 0)
			{
				AddEmptyItemCell(mRewardsContent, "（无）");
				return;
			}

			foreach (var ing in s.StageIngredients)
				AddItemCell(mRewardsContent, IngredientIconPath(ing.id), IngredientName(ing.id), ing.count);
		}

		/// <summary>属性页：本阶段属性提升（变化量）；无变化时给出提示</summary>
		private void BuildAttrs(BattleStatsSnapshot s)
		{
			if (mAttrsContent == null) return;

			int count = 0;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "攻击", s.DeltaAttack)) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "攻击速度", s.DeltaAttackSpeed, "%")) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "移动速度", s.DeltaMoveSpeed)) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "生命上限", s.DeltaMaxHp)) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "暴击率", s.DeltaCritChance, "%")) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "暴击伤害", s.DeltaCritDamage, "%")) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "物防", s.DeltaPhysDef)) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "元防", s.DeltaElemDef)) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "闪避", s.DeltaDodgeChance, "%")) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "物理伤害加成", s.DeltaPhysDamageBonus, "%")) count++;
			if (AddDeltaRow(mAttrsContent, mAttrsRowMaster, "元素伤害加成", s.DeltaElemDamageBonus, "%")) count++;

			if (count == 0) AddListLabel(mAttrsContent, "（本阶段无属性变化）", 22);
		}

		// ===== 按钮 =====

		private void BindContinueButton()
		{
			if (mContinueButton == null) return;
			mContinueButton.onClick.RemoveAllListeners();
			mContinueButton.onClick.AddListener(() =>
			{
				EventKit.Type.Send(new GameEvents.StageResultConfirmed
				{
					IsFinal = mData.IsFinal
				});
			});
		}
	}
}
