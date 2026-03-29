//=============================================================================
// ArchitectJudgmentPower.cs | Act4Placeholder - Slay the Spire 2 Mod
// EN: Phase 3 Judgment threshold marker on the Architect. Starts at 20 stacks
//     (= 20% HP threshold); counts down as damage is dealt. When total damage
//     this turn exceeds 20% of the Architect's Max HP, all player buffs are
//     cleansed. Logic in Act4ArchitectBossMechanics.cs.
// ZH: 第三阶段建筑师的「审判」阈值标记，起始20层（=20% HP阈值），随伤害积累递减。
//     本回合伤害超过建筑师最大HP的20%时，清除所有玩家的增益。逻辑见Act4ArchitectBossMechanics.cs。
//=============================================================================
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

namespace Act4Placeholder;

internal sealed class ArchitectJudgmentPower : PowerModel
{
	public override PowerType Type => PowerType.Buff;

	public override PowerStackType StackType => PowerStackType.Counter;
}
