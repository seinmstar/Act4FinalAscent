//=============================================================================
// ScoreUtilityCalculateScorePatch.cs | Act4Placeholder - Slay the Spire 2 Mod
// EN: Patches both overloads of ScoreUtility.CalculateScore to append an Act 4 progression bonus to the player's final run score based on how far they advanced in Act 4.
// ZH: 补丁同时修改ScoreUtility.CalculateScore的两个重载，根据玩家在第四幕的推进程度为最终跑图分数追加进度奖励分。
//=============================================================================
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Act4Placeholder;

[HarmonyPatch]
internal static class ScoreUtilityCalculateScoreRunStatePatch
{
	static MethodBase TargetMethod()
	{
		// EN: Try new beta signature first (IRunState, ulong, bool), fall back to old (IRunState, bool).
		// ZH: 先尝试新测试版签名 (IRunState, ulong, bool)，回退至旧版 (IRunState, bool)。
		return AccessTools.Method(typeof(ScoreUtility), nameof(ScoreUtility.CalculateScore),
			new[] { typeof(IRunState), typeof(ulong), typeof(bool) })
			?? AccessTools.Method(typeof(ScoreUtility), nameof(ScoreUtility.CalculateScore),
			new[] { typeof(IRunState), typeof(bool) });
	}

	private static void Postfix(IRunState runState, bool won, ref int __result)
	{
		if (runState is RunState concreteRunState)
		{
			__result += ModSupport.GetAct4ProgressionBonus(concreteRunState, won);
		}
	}
}

[HarmonyPatch]
internal static class ScoreUtilityCalculateScoreSerializableRunPatch
{
	static MethodBase TargetMethod()
	{
		return AccessTools.Method(typeof(ScoreUtility), nameof(ScoreUtility.CalculateScore),
			new[] { typeof(SerializableRun), typeof(ulong), typeof(bool) })
			?? AccessTools.Method(typeof(ScoreUtility), nameof(ScoreUtility.CalculateScore),
			new[] { typeof(SerializableRun), typeof(bool) });
	}

	private static void Postfix(SerializableRun run, bool won, ref int __result)
	{
		__result += ModSupport.GetAct4ProgressionBonus(run, won);
	}
}
