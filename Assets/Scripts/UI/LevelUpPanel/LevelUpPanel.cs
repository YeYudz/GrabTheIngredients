using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class LevelUpPanelData : IUIData
	{
		public BuffUpgradeConfig[] Buffs;
	}

	/// <summary>
	/// 升级选卡面板：展示 3 张升级 Buff 并上报选择结果。
	/// 纯显示层——Buff 的实际生效交给 UpgradeBuffSystem，面板不参与数值计算；
	/// 卡牌根不挂按钮，点击范围收敛到卡内 SelectButton，避免整卡误触。
	/// </summary>
	public partial class LevelUpPanel : UIPanel
	{
		private string[] mCurrentBuffIds = new string[3];

		/// <summary>键盘（A/D + 回车）当前指向的"待选择"卡（0/1/2，默认中间 1；-1 = 无可选项）</summary>
		private int mSelectedIndex = -1;

		/// <summary>每张卡的鎏金选中特效（运行时挂到 SkillCard_x 根节点，随面板一起销毁）</summary>
		private readonly BuffCardGildEffect[] mGildEffects = new BuffCardGildEffect[3];

		/// <summary>长按详情弹窗是否打开中：弹窗置顶时键盘不应再切换/确认背后的选卡</summary>
		private bool mDetailOpen;

		/// <summary>增益图标缓存（iconPath → Sprite）；每次升级都要刷 3 张 512×512 图，避免重复 Resources.Load</summary>
		private static readonly Dictionary<string, Sprite> s_iconCache = new();

		protected override void OnInit(IUIData data = null)
		{
			mData = data as LevelUpPanelData ?? new LevelUpPanelData();

			// 绑定 3 张卡片的 SelectButton 点击事件 + 长按看详情
			if (mCardSelectButtons != null)
			{
				for (int i = 0; i < mCardSelectButtons.Length; i++)
				{
					int index = i; // 捕获循环变量
					if (mCardSelectButtons[i] != null)
					{
						mCardSelectButtons[i].onClick.RemoveAllListeners();
						mCardSelectButtons[i].onClick.AddListener(() => OnSelectCard(index));
						AttachCardLongPress(index);
					}

					// 鎏金特效挂卡根（SelectButton 的父节点 = SkillCard_x）
					var cardRoot = mCardSelectButtons[i] != null ? mCardSelectButtons[i].transform.parent : null;
					mGildEffects[i] = BuffCardGildEffect.Ensure(cardRoot != null ? cardRoot.gameObject : null);
				}
			}
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as LevelUpPanelData ?? mData;

			// 打开时如果携带了Buff数据，立即刷新显示
			if (mData != null && mData.Buffs != null)
			{
				// 清理残留伤害飘字，避免遮挡 3选1 升级内容
				if (CombatFeedbackFactory.HasInstance)
					CombatFeedbackFactory.Instance.ClearAllDamageTexts();

				ShowBuffs(mData.Buffs);
			}
		}

		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this);
		}

		protected override void OnHide()
		{
		}

		protected override void OnClose()
		{
			mDetailOpen = false;
		}

		/// <summary>
		/// 键盘选卡：A/← 切到左边卡，D/→ 切到右边卡（环形轮转），回车确认当前待选择卡。
		/// 升级面板打开时游戏已暂停（timeScale=0），A/D 不会同时驱动角色移动。
		/// </summary>
		private void Update()
		{
			if (mDetailOpen) return; // 详情弹窗置顶时冻结键盘选卡

			if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow))
				MoveSelection(-1);
			else if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow))
				MoveSelection(1);

			if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
				ConfirmSelection();
		}

		/// <summary>向左/右（dir=-1/+1）切换待选择卡；空卡跳过，环形轮转</summary>
		private void MoveSelection(int dir)
		{
			for (int step = 1; step <= 3; step++)
			{
				int idx = ((mSelectedIndex + dir * step) % 3 + 3) % 3;
				if (idx == mSelectedIndex) break;
				if (IsCardSelectable(idx))
				{
					SetSelectedIndex(idx, true);
					return;
				}
			}
		}

		/// <summary>回车确认：与点击 SelectButton 完全同一条选择链路</summary>
		private void ConfirmSelection()
		{
			if (mSelectedIndex < 0 || mSelectedIndex >= mCurrentBuffIds.Length) return;
			OnSelectCard(mSelectedIndex);
		}

		/// <summary>该卡是否可被选中（本局抽到且未超上限的卡才会被填入数据）</summary>
		private bool IsCardSelectable(int index)
			=> index >= 0 && index < mCurrentBuffIds.Length && !string.IsNullOrEmpty(mCurrentBuffIds[index]);

		/// <summary>刷新鎏金高亮到 index 卡；animate=true 时播放切换音效</summary>
		private void SetSelectedIndex(int index, bool animate)
		{
			mSelectedIndex = index;

			for (int i = 0; i < mGildEffects.Length; i++)
			{
				if (mGildEffects[i] == null) continue;
				mGildEffects[i].SetGilded(i == index);
			}

			if (animate && AudioManager.HasInstance)
				AudioManager.Instance.PlaySfx(AssetKeys.SFX_POT_OPEN, 0.7f); // 清脆叮~：轻量切换反馈
		}

		/// <summary>初始待选择卡：默认中间（1），该位为空则取最近的可选卡</summary>
		private int PickInitialSelection()
		{
			if (IsCardSelectable(1)) return 1;
			if (IsCardSelectable(0)) return 0;
			if (IsCardSelectable(2)) return 2;
			return -1;
		}

		/// <summary>由业务层注入 3 个升级 Buff 配置，刷新卡片显示</summary>
		public void ShowBuffs(BuffUpgradeConfig[] buffs)
		{
			for (int i = 0; i < 3; i++)
			{
				bool hasBuff = buffs != null && i < buffs.Length && buffs[i] != null;

				if (hasBuff)
				{
					mCurrentBuffIds[i] = buffs[i].id;

					SetImage(mCardIcons, i, buffs[i].iconPath);       // SkillIcon
					SetImage(mCardRankIcons, i, buffs[i].rankIconPath); // RankIcon
					SetText(mCardNameTexts, i, buffs[i].buffName);    // NameText
					SetText(mCardDescTexts, i, buffs[i].description); // DescText

					SetButtonInteractable(mCardSelectButtons, i, true);
				}
				else
				{
					// 空卡片：清空默认内容并禁用按钮，避免显示 "Button" / "New Text"
					mCurrentBuffIds[i] = null;

					SetImage(mCardIcons, i, null);
					SetImage(mCardRankIcons, i, null);
					SetText(mCardNameTexts, i, string.Empty);
					SetText(mCardDescTexts, i, string.Empty);

					SetButtonInteractable(mCardSelectButtons, i, false);
				}
			}

			// 每轮选卡重置待选择高亮：默认中间那张（空则取最近可选卡）
			SetSelectedIndex(PickInitialSelection(), false);
		}

		/// <summary>点击第 index 张卡的 SelectButton：上报选择并关闭面板（业务层订阅 UpgradeSelected 事件）</summary>
		public void OnSelectCard(int index)
		{
			if (index < 0 || index >= mCurrentBuffIds.Length) return;
			if (string.IsNullOrEmpty(mCurrentBuffIds[index])) return;

			// UI 点击音效
			if (AudioManager.HasInstance)
			{
				AudioManager.Instance.PlayUiClick();
			}

			// 延迟一帧：先发事件让业务层处理，再关闭面板，避免在 onClick 栈中销毁面板
			StartCoroutine(DelaySelectAndClose(mCurrentBuffIds[index]));
		}

		// ===== 长按看详情 =====

		/// <summary>
		/// 卡牌根节点（SkillCard_x，非按钮）没有 Button，卡面 Image 为 raycastTarget，
		/// 所以按下卡面时事件会冒泡到卡根 → 挂在卡根上的长按照样能触发；
		/// SelectButton 自身实现了 IPointerDownHandler，会截住按钮区（180×60）内的按下，
		/// 故按钮也补一份长按（它自己的 Button 会被 LongPressTrigger 暂时置灰，吞掉这次单击，不会误选卡）。
		/// </summary>
		private void AttachCardLongPress(int index)
		{
			var btn = mCardSelectButtons[index];
			if (btn == null) return;

			AttachLongPress(btn.gameObject, index);

			var cardRoot = btn.transform.parent;
			if (cardRoot != null) AttachLongPress(cardRoot.gameObject, index);
		}

		private void AttachLongPress(GameObject go, int index)
		{
			if (go == null) return;

			var longPress = go.GetComponent<LongPressTrigger>();
			if (longPress == null) longPress = go.AddComponent<LongPressTrigger>();
			longPress.onLongPress = () => OpenCardDetail(index);
		}

		/// <summary>长按卡片：打开该增益卡的详情弹窗（与图鉴/状态页增益卡同一份工厂口径）</summary>
		private void OpenCardDetail(int index)
		{
			if (index < 0 || index >= mCurrentBuffIds.Length) return;

			var id = mCurrentBuffIds[index];
			if (string.IsNullOrEmpty(id)) return;

			var data = ItemDetailFactory.ForBuff(id);
			if (data == null) return;

			// 弹窗置顶期间冻结键盘选卡，关闭后恢复（面板可能被缓存复用，必须挂 OnClosed）
			mDetailOpen = true;
			var panel = UIKit.OpenPanel<ItemDetailPanel>(UILevel.Pop, data);
			panel?.OnClosed(() => mDetailOpen = false);
		}

		private IEnumerator DelaySelectAndClose(string buffId)
		{
			EventKit.Type.Send(new GameEvents.UpgradeSelected { BuffId = buffId });
			yield return null;
			CloseSelf();
		}

		// ===== 通用赋值 =====

		private static void SetText(Text[] arr, int i, string text)
		{
			if (arr != null && i < arr.Length && arr[i] != null)
				arr[i].text = text;
		}

		private static void SetImage(Image[] arr, int i, string path)
		{
			if (arr == null || i >= arr.Length || arr[i] == null) return;
			if (string.IsNullOrEmpty(path))
			{
				arr[i].gameObject.SetActive(false);
				return;
			}

			// 缓存命中直接复用；未命中才走一次 Resources.Load（找不到也缓存 null，避免反复尝试）
			if (!s_iconCache.TryGetValue(path, out var sprite))
			{
				sprite = Resources.Load<Sprite>(path);
				s_iconCache[path] = sprite;
			}

			if (sprite == null)
			{
				arr[i].gameObject.SetActive(false);
				return;
			}
			arr[i].sprite = sprite;
			arr[i].gameObject.SetActive(true);
		}

		private static void SetButtonInteractable(Button[] arr, int i, bool interactable)
		{
			if (arr != null && i < arr.Length && arr[i] != null)
				arr[i].interactable = interactable;
		}
	}
}
