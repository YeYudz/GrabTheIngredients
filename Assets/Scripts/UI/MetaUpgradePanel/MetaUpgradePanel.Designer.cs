//------------------------------------------------------------------------------
// MetaUpgradePanel.Designer.cs
// 字段声明；未绑定时由运行时代码按同构布局自动构建
//------------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	public partial class MetaUpgradePanel
	{
		[SerializeField]
		private MetaUpgradePanelData mData;

		public MetaUpgradePanelData Data => mData;

		[Header("标题 / 钱币")]
		[SerializeField] private Text mTitleText;
		[SerializeField] private Text mCoinText;

		[Header("列表")]
		[SerializeField] private Transform mListContent;   // 列表 Content（VerticalLayoutGroup）
		[SerializeField] private GameObject mRowTemplate;  // 行母版（仅隐藏不销毁；空则代码构建）

		[Header("按钮")]
		[SerializeField] private Button mBackButton;

		protected override void ClearUIComponents()
		{
			mTitleText = null;
			mCoinText = null;
			mListContent = null;
			mRowTemplate = null;
			mBackButton = null;
		}
	}
}
