using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata.Content.Players;
using PocketStrata.Content.Systems;

namespace PocketStrata
{
	// 模组入口：网络包分发
	public class PocketStrata : Mod
	{
		// 网络包类型（勿改已有编号，保持协议兼容）
		public enum PacketType : byte
		{
			SyncPocketVisualSession = 1,      // 会话开关、区域、锚点、结构名
			SyncPocketWorldViewPlayer = 2,    // 进入者视角 SeesPocketInterior
			SyncPocketSnapshot = 3,           // 旧版整包真实备份（保留兼容）
			RequestOpenPocketSession = 4,     // 客户端请求调试开会话
			RequestClosePocketSession = 5,    // 客户端请求关闭会话
			RequestOpenPocketSessionNamed = 6,// 旧版整包上传开会话（保留兼容）
			OpenSessionNamedBegin = 7,        // F9 分块上传：开始
			OpenSessionNamedChunk = 8,        // F9 分块上传：数据块
			OpenSessionNamedFinish = 9,       // F9 分块上传：结束
			SyncSnapshotChunk = 10,           // 真实备份分块
			SyncPocketTilesChunk = 11,        // 口袋地形分块
			SyncPocketTilesComplete = 12,     // 口袋分块接收完毕
			OpenSessionUploadFailed = 13,     // 上传失败提示
			OpenSessionServerAccepted = 14,   // 服务端已接受上传
			OpenSessionEnter = 15,            // 旧版进入通知（保留兼容）
		}

		// 根据包类型分发到各子系统
		public override void HandlePacket(BinaryReader reader, int whoAmI)
		{
			PacketType type = (PacketType)reader.ReadByte();
			switch (type)
			{
				case PacketType.SyncPocketVisualSession:
					PocketWorldVisualMaskSystem.ReceiveSessionModPacket(reader);
					break;
				case PacketType.SyncPocketSnapshot:
					PocketWorldVisualMaskSystem.ReceiveSnapshotModPacket(reader);
					break;
				case PacketType.SyncSnapshotChunk:
					PocketWorldChunkNetwork.ClientReceiveSnapshotChunk(reader);
					break;
				case PacketType.SyncPocketTilesChunk:
					PocketWorldChunkNetwork.ClientReceivePocketTilesChunk(reader);
					break;
				case PacketType.SyncPocketTilesComplete:
					PocketWorldVisualMaskSystem.ClientOnPocketTilesSyncComplete();
					break;
				case PacketType.SyncPocketWorldViewPlayer:
					byte pocketPlayerIndex = reader.ReadByte();
					if (pocketPlayerIndex < 255)
					{
						Player p = Main.player[pocketPlayerIndex];
						if (p != null && p.active)
							p.GetModPlayer<PocketWorldViewPlayer>().ReceiveSync(reader);
					}
					break;
				case PacketType.RequestOpenPocketSession:
					if (Main.netMode != NetmodeID.Server)
						break;
					if (whoAmI < 0 || whoAmI >= Main.maxPlayers || Main.player[whoAmI] == null || !Main.player[whoAmI].active)
						break;
					short px = reader.ReadInt16();
					short py = reader.ReadInt16();
					short pw = reader.ReadInt16();
					short ph = reader.ReadInt16();
					PocketWorldVisualMaskSystem.ServerOpenSession(new Rectangle(px, py, pw, ph), whoAmI);
					break;
				case PacketType.RequestClosePocketSession:
					if (Main.netMode != NetmodeID.Server)
						break;
					if (whoAmI < 0 || whoAmI >= Main.maxPlayers || Main.player[whoAmI] == null || !Main.player[whoAmI].active)
						break;
					PocketWorldVisualMaskSystem.ServerCloseSession();
					break;
				case PacketType.RequestOpenPocketSessionNamed:
					if (Main.netMode != NetmodeID.Server)
						break;
					if (whoAmI < 0 || whoAmI >= Main.maxPlayers || Main.player[whoAmI] == null || !Main.player[whoAmI].active)
						break;
					short nx = reader.ReadInt16();
					short ny = reader.ReadInt16();
					short nw = reader.ReadInt16();
					short nh = reader.ReadInt16();
					PocketWorldVisualMaskSystem.ReceiveOpenSessionNamedRequest(
						new Rectangle(nx, ny, nw, nh),
						reader,
						whoAmI);
					break;
				case PacketType.OpenSessionNamedBegin:
					if (Main.netMode != NetmodeID.Server || whoAmI < 0 || whoAmI >= Main.maxPlayers)
						break;
					PocketWorldChunkNetwork.ServerReceiveOpenBegin(reader, whoAmI);
					break;
				case PacketType.OpenSessionNamedChunk:
					if (Main.netMode != NetmodeID.Server || whoAmI < 0 || whoAmI >= Main.maxPlayers)
						break;
					PocketWorldChunkNetwork.ServerReceiveOpenChunk(reader, whoAmI);
					break;
				case PacketType.OpenSessionNamedFinish:
					if (Main.netMode != NetmodeID.Server || whoAmI < 0 || whoAmI >= Main.maxPlayers)
						break;
					PocketWorldChunkNetwork.ServerReceiveOpenFinish(reader, whoAmI);
					break;
				case PacketType.OpenSessionUploadFailed:
					if (Main.netMode != NetmodeID.MultiplayerClient)
						break;
					PocketStrataChat.NewText(reader.ReadString(), Microsoft.Xna.Framework.Color.OrangeRed);
					break;
				case PacketType.OpenSessionServerAccepted:
					if (Main.netMode != NetmodeID.MultiplayerClient)
						break;
					PocketStrataChat.NewText(reader.ReadString(), new Microsoft.Xna.Framework.Color(120, 220, 255));
					break;
				case PacketType.OpenSessionEnter:
					if (Main.netMode != NetmodeID.MultiplayerClient)
						break;
					PocketWorldVisualMaskSystem.ClientReceiveOpenSessionEnterLegacy(reader);
					break;
			}
		}
	}
}
