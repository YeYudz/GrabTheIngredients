//------------------------------------------------------------------------------
// FieldCookingPanel 字段定义（画中画炒菜 UI：右上角小窗 + 横幅）
// 绑定由预制体约定；缺失时由 ValidateBindings 告警定位，不阻断运行
//------------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	public partial class FieldCookingPanel
	{
		[SerializeField]
		private FieldCookingPanelData mData;

		public FieldCookingPanelData Data => mData;

		[Header("右上角 - 画中画小窗")]
		[SerializeField] private GameObject mWindowRoot; // 小窗容器根节点（含背板，整体可点击颠勺）
		[SerializeField] private Image mWindowBoard;     // 小窗背板 Image（颠勺闪光/漏点变红动画载体）
		[SerializeField] private Text mTitleText;        // 菜名标题
		[SerializeField] private GameObject mHintRoot;   // 颠勺提示区域（高亮食材/锅铲+箭头，窗口期显示）
		[SerializeField] private Image mHintImage;       // 高亮图形（窗口期内闪烁）
		[SerializeField] private Image mDishIcon;        // 菜品图标（可选：未绑定则跳过，按 CookingRecipeConfig.iconPath 加载）
		[SerializeField] private Text mStatusText;       // 状态提示（"等待颠勺时机…"/"颠勺！"）
		[SerializeField] private Text mComboText;        // combo 计数（左下角）

		[Header("右上角 - 炒菜步骤舞台")]
		[SerializeField] private GameObject mWokStage;   // 舞台容器（步骤主图+特效层的父级）
		[SerializeField] private Image mStepImage;       // 步骤主图（锅/食材，飞入/颠锅动画载体）
		[SerializeField] private RectTransform mFxLayer; // 特效层（油流/水滴/颗粒临时图，代码生成销毁）
		[SerializeField] private Image mTimerBar;        // 总时长倒计时条（Fill = Horizontal）

		[Header("中心 - 横幅")]
		[SerializeField] private Text mBannerText;       // 小窗区域的大字横幅（提示/判定/失败）

		[Header("中心 - 结算菜品卡（图标展示，替代纯文字）")]
		[Tooltip("结算菜品卡的条目母版：把 Project 窗口里的 Resources/Prefabs/UI/StatusBagItem.prefab 拖进来即可自定义\n（不要拖场景里的实例；留空则运行时按同路径自动加载）")]
		[SerializeField] private GameObject mBagItemTemplate; // 背包条目母版（StatusBagItem）；留空按 Resources/Prefabs/UI/StatusBagItem 加载

		protected override void ClearUIComponents()
		{
			mWindowRoot = null;
			mWindowBoard = null;
			mTitleText = null;
			mHintRoot = null;
			mHintImage = null;
			mDishIcon = null;
			mStatusText = null;
			mComboText = null;
			mWokStage = null;
			mStepImage = null;
			mFxLayer = null;
			mTimerBar = null;
			mBannerText = null;
			mBagItemTemplate = null;
		}
	}
}
