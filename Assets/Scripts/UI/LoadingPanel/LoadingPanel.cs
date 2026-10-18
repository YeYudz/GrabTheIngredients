using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class LoadingPanelData : IUIData
	{
	}

	/// <summary>
	/// 加载过渡面板：全屏遮罩 + 进度条 + 阶段提示。
	/// 业务层经 GameEvents.LoadingProgress 上报真实进度，面板只做平滑追赶展示；
	/// 「目标值 / 显示值」分离正是为了避免进度突变造成跳变观感。
	/// </summary>
	public partial class LoadingPanel : UIPanel
	{
		[Header("标题 / 提示")]
		[SerializeField] private Text mTitleText;      // 标题（可选，不绑则不显示）
		[SerializeField] private Text mTipText;        // 阶段提示："正在加载配置…"

		[Header("进度条")]
		[SerializeField] private Image mProgressFill;  // 进度填充（Type=Filled / FillMethod=Horizontal）
		[SerializeField] private Text mProgressText;   // 百分比文本 "45%"

		/// <summary>平滑追赶速度（每秒推进的进度比例）</summary>
		private const float SMOOTH_SPEED = 1.6f;

		private float mTarget;   // 业务层上报的真实进度
		private float mShown;    // 当前显示进度（向 mTarget 平滑追赶）

		protected override void OnInit(IUIData data = null)
		{
			ResolveRefs();

			// 立即归零：预制体里 FillAmount 可能是 1，避免首帧闪出满条
			mTarget = 0f;
			mShown = 0f;
			Apply();

			EventKit.Type.Register<GameEvents.LoadingProgress>(OnProgress)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
		}

		protected override void OnOpen(IUIData data = null)
		{
			mTarget = 0f;
			mShown = 0f;
			Apply();
		}

		// ===== 引用解析（Inspector 已绑定的优先，缺失则按节点名查找） =====

		private void ResolveRefs()
		{
			if (mTitleText == null) mTitleText = FindComponent<Text>("TitleText");
			if (mTipText == null) mTipText = FindComponent<Text>("TipText");
			if (mProgressFill == null) mProgressFill = FindComponent<Image>("ProgressFill");
			if (mProgressText == null) mProgressText = FindComponent<Text>("ProgressText");
		}

		private T FindComponent<T>(string nodeName) where T : Component
		{
			var node = FindChild(transform, nodeName);
			return node != null ? node.GetComponent<T>() : null;
		}

		/// <summary>深度优先按名称查找子节点（不包含自身）</summary>
		private static Transform FindChild(Transform parent, string nodeName)
		{
			for (int i = 0; i < parent.childCount; i++)
			{
				var child = parent.GetChild(i);
				if (child.name == nodeName) return child;
				var found = FindChild(child, nodeName);
				if (found != null) return found;
			}
			return null;
		}

		// ===== 进度显示 =====

		private void OnProgress(GameEvents.LoadingProgress evt)
		{
			mTarget = Mathf.Clamp01(evt.Progress);
			if (mTipText != null && !string.IsNullOrEmpty(evt.Tip)) mTipText.text = evt.Tip;
		}

		private void Update()
		{
			// 用非缩放时间：即便 Time.timeScale=0 也能正常走条
			if (mShown >= mTarget) return;
			mShown = Mathf.MoveTowards(mShown, mTarget, SMOOTH_SPEED * Time.unscaledDeltaTime);
			Apply();
		}

		private void Apply()
		{
			if (mProgressFill != null) mProgressFill.fillAmount = mShown;
			if (mProgressText != null) mProgressText.text = $"{Mathf.RoundToInt(mShown * 100f)}%";
		}
	}
}
