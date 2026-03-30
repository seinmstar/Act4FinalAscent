//=============================================================================
// ClientLoadJoinSyncPatch.cs | Act4Placeholder - Slay the Spire 2 Mod
// EN: Appends brutal-mode flag, weakest-buff player IDs, book-choice bitmask, and per-player damage
//     contributions to ClientLoadJoinResponseMessage so that reconnecting clients receive the host's
//     authoritative Act 4 state and don't diverge on IsBrutalAct4 / HasAct4WeakestBuff / weakest-player
//     determination / stolen-tome checks that read host-only local files.
// ZH: 将残暴模式标志、最弱增益玩家ID、以及偷书选择位掩码附加到ClientLoadJoinResponseMessage，
//     使重连客户端能接收主机权威的第四幕状态，避免在读取仅主机拥有的本地文件时产生分歧。
//=============================================================================
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace Act4Placeholder;

/// <summary>
/// EN: HOST side - after the normal payload has been written, append a version-tagged Act4 block:
///     [ushort 0xA4C4] [bool isBrutal] [int(8) count] [ulong id] * count [int(8) bookBitmask]
///     [int(8) damagePlayerCount] ([ulong netId] [long damage]) * damagePlayerCount
/// ZH: 主机侧——在正常负载写入完毕后，附加一个带版本标记的Act4数据块（含伤害贡献数据）。
/// </summary>
[HarmonyPatch(typeof(ClientLoadJoinResponseMessage), nameof(ClientLoadJoinResponseMessage.Serialize))]
internal static class ClientLoadJoinResponseMessageSerializePatch
{
	private static void Postfix(ref ClientLoadJoinResponseMessage __instance, PacketWriter writer)
	{
		try
		{
			bool isBrutal = ModSupport.GetBrutalFlagForRun(__instance.serializableRun);
			List<ulong> ids = ModSupport.GetWeakestBuffPlayerIdsForRun(__instance.serializableRun);
			int bookChoiceBitmask = ModSupport.GetBookChoiceBitmaskForRun(__instance.serializableRun);
			// Magic sentinel lets the client skip this block if the host is on an older mod version.
			writer.WriteUShort(0xA4C4);
			writer.WriteBool(isBrutal);
			writer.WriteInt(ids.Count, 8);
			foreach (ulong id in ids)
			{
				writer.WriteULong(id);
			}
			writer.WriteInt(bookChoiceBitmask & 0xFF, 8);
			// EN: Append per-player damage contribution totals so clients can seed their tracking on rejoin.
			// ZH: 附加每位玩家的伤害贡献总量，使客户端在重连时可以直接使用。
			Dictionary<ulong, long> damageContributions = ModSupport.GetDamageContributionsForRun(__instance.serializableRun);
			writer.WriteInt(damageContributions.Count, 8);
			foreach (KeyValuePair<ulong, long> kv in damageContributions)
			{
				writer.WriteULong(kv.Key);
				writer.WriteLong(kv.Value);
			}
			Log.Info($"[Act4Placeholder][JoinSync] Serialized Act4 join state: startTime={__instance.serializableRun.StartTime} brutal={isBrutal} weakestCount={ids.Count} bookBitmask={bookChoiceBitmask} damagePlayerCount={damageContributions.Count}", 1);
		}
		catch (System.Exception ex)
		{
			Log.Warn($"[Act4Placeholder][JoinSync] Failed to serialize Act4 join state: {ex.Message}", 1);
			// Never crash the join handshake. Clients will fall back to local-file reads.
		}
	}
}

/// <summary>
/// EN: CLIENT side - after the normal payload has been read, attempt to read the version-tagged
///     Act4 block. Stores data in ModSupport pending statics so RestoreAct4FlagsFromSave can
///     consume them when RunState.FromSerializable fires.
/// ZH: 客户端侧——在正常负载读取完毕后，尝试读取带版本标记的Act4数据块，
///     将数据存入ModSupport待处理静态字段，供RunState.FromSerializable触发时的RestoreAct4FlagsFromSave使用。
/// </summary>
[HarmonyPatch(typeof(ClientLoadJoinResponseMessage), nameof(ClientLoadJoinResponseMessage.Deserialize))]
internal static class ClientLoadJoinResponseMessageDeserializePatch
{
	private static void Postfix(ref ClientLoadJoinResponseMessage __instance, PacketReader reader)
	{
		try
		{
			int bitsRemaining = reader.Buffer.Length * 8 - reader.BitPosition;
			if (bitsRemaining < 16)
			{
				return;
			}
			ushort magic = reader.ReadUShort();
			if (magic != 0xA4C4)
			{
				return;
			}
			bool isBrutal = reader.ReadBool();
			int count = reader.ReadInt(8);
			var ids = new List<ulong>(count);
			for (int i = 0; i < count; i++)
			{
				ids.Add(reader.ReadULong());
			}
			int? bookChoiceBitmask = null;
			bitsRemaining = reader.Buffer.Length * 8 - reader.BitPosition;
			if (bitsRemaining >= 8)
			{
				bookChoiceBitmask = reader.ReadInt(8);
			}
			// EN: Read per-player damage contribution totals (added after book bitmask).
			//     Gracefully skip if the host is running an older mod version without this data.
			// ZH: 读取每位玩家的伤害贡献总量（在偷书位掩码之后添加）。若主机版本较旧则跳过。
			Dictionary<ulong, long>? damageContributions = null;
			bitsRemaining = reader.Buffer.Length * 8 - reader.BitPosition;
			if (bitsRemaining >= 8)
			{
				int damagePlayerCount = reader.ReadInt(8);
				if (damagePlayerCount > 0)
				{
					damageContributions = new Dictionary<ulong, long>(damagePlayerCount);
					for (int i = 0; i < damagePlayerCount; i++)
					{
						ulong netId = reader.ReadULong();
						long damage = reader.ReadLong();
						damageContributions[netId] = damage;
					}
				}
			}
			ModSupport.SetPendingJoinSyncState(__instance.serializableRun.StartTime, isBrutal, ids, bookChoiceBitmask, damageContributions);
			Log.Info($"[Act4Placeholder][JoinSync] Deserialized Act4 join state: startTime={__instance.serializableRun.StartTime} brutal={isBrutal} weakestCount={ids.Count} bookBitmask={(bookChoiceBitmask.HasValue ? bookChoiceBitmask.Value.ToString() : "none")} damagePlayerCount={damageContributions?.Count ?? 0}", 1);
		}
		catch (System.Exception ex)
		{
			Log.Warn($"[Act4Placeholder][JoinSync] Failed to deserialize Act4 join state: {ex.Message}", 1);
			// Gracefully degrade if the host is running an older mod version without this block.
		}
	}
}
