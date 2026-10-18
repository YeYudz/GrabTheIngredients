using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GameUI
{
	/// <summary>
	/// UI 交互 tween 的缓动曲线集合。
	/// 输入 t 与输出均为 0~1 归一化进度（OutBack / OutElastic 会短暂越过 1，配合 LerpUnclamped 使用）。
	/// 纯静态函数，无状态，可自由组合到 Run 的 apply 里。
	/// </summary>
	public static class UiEase
	{
		public static float Linear(float t) => t;

		public static float InQuad(float t) => t * t;

		public static float OutQuad(float t) => 1f - (1f - t) * (1f - t);

		public static float InCubic(float t) => t * t * t;

		public static float OutCubic(float t)
		{
			float u = 1f - t;
			return 1f - u * u * u;
		}

		public static float InOutCubic(float t)
		{
			return t < 0.5f
				? 4f * t * t * t
				: 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
		}

		/// <summary>收尾回弹：适合弹窗/按钮弹出（先冲过目标再退回）。</summary>
		public static float OutBack(float t)
		{
			const float c1 = 1.70158f;
			const float c3 = c1 + 1f;
			float u = t - 1f;
			return 1f + c3 * u * u * u + c1 * u * u;
		}

		/// <summary>弹性收尾：适合强调性反馈（数值跳动、奖励弹出），抖动较明显。</summary>
		public static float OutElastic(float t)
		{
			if (t <= 0f) return 0f;
			if (t >= 1f) return 1f;
			const float c4 = 2f * Mathf.PI / 3f;
			return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
		}

		/// <summary>弹跳收尾：像球落地一样递减弹跳，适合掉落物/奖励。</summary>
		public static float OutBounce(float t)
		{
			const float n1 = 7.5625f;
			const float d1 = 2.75f;
			if (t < 1f / d1) return n1 * t * t;
			if (t < 2f / d1)
			{
				t -= 1.5f / d1;
				return n1 * t * t + 0.75f;
			}
			if (t < 2.5f / d1)
			{
				t -= 2.25f / d1;
				return n1 * t * t + 0.9375f;
			}
			t -= 2.625f / d1;
			return n1 * t * t + 0.984375f;
		}
	}

	/// <summary>
	/// 手写协程版 UI tween 工具（零第三方依赖，风格与项目现有 Mathf.Lerp 平滑一致）。
	/// 约定：
	/// - 一律使用 **非缩放时间**：暂停 / 结算 / 时停状态下 UI 反馈不能卡住；
	/// - 每次播放前由 Play(ref slot) 停掉同一槽位上的旧协程，快速连点不会互相打架；
	/// - 协程内所有节点引用都做 null 判断，格子被 Instantiate/Destroy 重建时不会报空引用。
	/// </summary>
	public static class UiTween
	{
		/// <summary>常用时长（秒）</summary>
		public const float Fast = 0.12f;
		public const float Normal = 0.2f;
		public const float Slow = 0.3f;

		/// <summary>
		/// 通用驱动：delay 后按 ease 把 t 从 0 推到 1，每帧回调 apply(easedT)。
		/// duration <= 0 时直接落到终态（apply(1)），保证调用方逻辑不因"没动画"而缺失收尾。
		/// </summary>
		public static IEnumerator Run(float duration, Action<float> apply,
			Func<float, float> ease = null, float delay = 0f, bool unscaled = true, Action onComplete = null)
		{
			if (apply == null) yield break;
			if (ease == null) ease = UiEase.OutCubic;

			if (duration <= 0f)
			{
				apply(ease(1f));
				onComplete?.Invoke();
				yield break;
			}

			if (delay > 0f)
			{
				float waited = 0f;
				while (waited < delay)
				{
					waited += unscaled ? Time.unscaledDeltaTime : Time.deltaTime;
					yield return null;
				}
			}

			float elapsed = 0f;
			while (elapsed < duration)
			{
				elapsed += unscaled ? Time.unscaledDeltaTime : Time.deltaTime;
				apply(ease(Mathf.Clamp01(elapsed / duration)));
				yield return null;
			}

			apply(ease(1f));   // 浮点累加可能恰好越过终点，强制补一帧终态
			onComplete?.Invoke();
		}

		/// <summary>在宿主上播放协程，并顺手停掉该槽位上一段动画（传字段进来即可）。</summary>
		public static void Play(MonoBehaviour host, ref Coroutine slot, IEnumerator routine)
		{
			if (host == null || routine == null) return;
			Stop(host, ref slot);
			if (!host.isActiveAndEnabled) return;   // 未激活的宿主起协程会抛异常
			slot = host.StartCoroutine(routine);
		}

		/// <summary>停掉槽位上的动画并清空引用。</summary>
		public static void Stop(MonoBehaviour host, ref Coroutine slot)
		{
			if (slot == null) return;
			if (host != null) host.StopCoroutine(slot);
			slot = null;
		}

		/// <summary>取节点上的 CanvasGroup，没有则补一个（透明度动画的唯一推荐做法）。</summary>
		public static CanvasGroup EnsureCanvasGroup(GameObject go)
		{
			if (go == null) return null;
			var group = go.GetComponent<CanvasGroup>();
			if (group == null) group = go.AddComponent<CanvasGroup>();
			return group;
		}

		/// <summary>弹入：缩放 fromScale → 1 同时透明度 0 → 1，适合面板打开。</summary>
		public static IEnumerator PopIn(RectTransform target, CanvasGroup group,
			float fromScale = 0.94f, float duration = Normal, Func<float, float> ease = null)
		{
			if (target == null) yield break;
			if (ease == null) ease = UiEase.OutCubic;

			Vector3 from = Vector3.one * fromScale;
			target.localScale = from;
			if (group != null) group.alpha = 0f;

			yield return Run(duration, t =>
			{
				if (target != null) target.localScale = Vector3.LerpUnclamped(from, Vector3.one, t);
				if (group != null) group.alpha = Mathf.Clamp01(t);
			}, ease);

			if (target != null) target.localScale = Vector3.one;
			if (group != null) group.alpha = 1f;
		}

		/// <summary>淡入淡出（只改 alpha）。</summary>
		public static IEnumerator Fade(CanvasGroup group, float from, float to,
			float duration = Normal, Func<float, float> ease = null, Action onComplete = null)
		{
			if (group == null) yield break;
			group.alpha = from;
			yield return Run(duration, t => { if (group != null) group.alpha = Mathf.LerpUnclamped(from, to, t); },
				ease, 0f, true, onComplete);
			if (group != null) group.alpha = to;
		}

		/// <summary>位移（anchoredPosition）+ 透明度同步变化，适合详情区/侧栏抽屉式展开收起。</summary>
		public static IEnumerator SlideFade(RectTransform target, CanvasGroup group,
			Vector2 from, Vector2 to, float fromAlpha = 0f, float toAlpha = 1f,
			float duration = Normal, Func<float, float> ease = null, Action onComplete = null)
		{
			if (target == null) yield break;
			target.anchoredPosition = from;
			if (group != null) group.alpha = fromAlpha;

			yield return Run(duration, t =>
			{
				if (target != null) target.anchoredPosition = Vector2.LerpUnclamped(from, to, t);
				if (group != null) group.alpha = Mathf.LerpUnclamped(fromAlpha, toAlpha, t);
			}, ease, 0f, true, onComplete);

			if (target != null) target.anchoredPosition = to;
			if (group != null) group.alpha = toAlpha;
		}

		/// <summary>脉冲：放大到 peakScale 再回弹到原尺寸，适合数值/金币变化等强调反馈。</summary>
		public static IEnumerator Pulse(RectTransform target, float peakScale = 1.12f,
			float duration = 0.24f, Func<float, float> ease = null)
		{
			if (target == null) yield break;
			if (ease == null) ease = UiEase.OutQuad;

			Vector3 baseScale = target.localScale;
			Vector3 peak = baseScale * peakScale;
			float up = duration * 0.35f;
			float down = duration - up;

			yield return Run(up, t =>
			{
				if (target != null) target.localScale = Vector3.LerpUnclamped(baseScale, peak, t);
			}, ease);

			yield return Run(down, t =>
			{
				if (target != null) target.localScale = Vector3.LerpUnclamped(peak, baseScale, t);
			}, UiEase.OutBack);

			if (target != null) target.localScale = baseScale;
		}

		/// <summary>
		/// 列表错峰弹入：整体淡入 + 每个目标 scale 从 fromScale 依次弹到 1。
		/// 单个协程驱动整批格子，格子数量多也不会生成大量协程；目标被销毁时自动跳过。
		/// </summary>
		public static IEnumerator StaggerPop(IList<GameObject> targets, CanvasGroup group,
			float fromScale = 0.86f, float itemDuration = 0.2f, float interval = 0.02f, float maxTotal = 0.45f)
		{
			int count = targets != null ? targets.Count : 0;

			// 格子很多时压缩间隔，避免整批动画拖太长
			if (count > 1)
			{
				float maxInterval = Mathf.Max(0f, (maxTotal - itemDuration) / (count - 1));
				if (interval > maxInterval) interval = maxInterval;
			}

			// 记录每个格子的原始缩放（母版可能不是 1），收尾时精确复位
			var baseScales = new List<Vector3>(count);
			for (int i = 0; i < count; i++)
			{
				var go = targets[i];
				var rt = go != null ? go.transform as RectTransform : null;
				baseScales.Add(rt != null ? rt.localScale : Vector3.one);
				if (rt != null) rt.localScale = baseScales[i] * fromScale;
			}

			float fadeDuration = Mathf.Min(Fast, itemDuration);
			float total = itemDuration + interval * Mathf.Max(0, count - 1);

			if (group != null) group.alpha = 0f;

			float elapsed = 0f;
			while (elapsed < total)
			{
				elapsed += Time.unscaledDeltaTime;

				for (int i = 0; i < count; i++)
				{
					var go = targets[i];
					if (go == null) continue;
					var rt = go.transform as RectTransform;
					if (rt == null) continue;
					float local = Mathf.Clamp01((elapsed - i * interval) / itemDuration);
					rt.localScale = Vector3.LerpUnclamped(baseScales[i] * fromScale, baseScales[i], UiEase.OutBack(local));
				}

				if (group != null) group.alpha = Mathf.Clamp01(elapsed / fadeDuration);
				yield return null;
			}

			for (int i = 0; i < count; i++)
			{
				var go = targets[i];
				if (go == null) continue;
				var rt = go.transform as RectTransform;
				if (rt != null) rt.localScale = baseScales[i];
			}
			if (group != null) group.alpha = 1f;
		}
	}
}
