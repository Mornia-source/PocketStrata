using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace PocketStrata.Content.Systems
{
	// 多人：结构分块上传、口袋/真实备份分块下发（32×32 格，单包约 23KB）
	internal static class PocketWorldChunkNetwork
	{
		public const int NetChunkWidth = 32;
		public const int NetChunkHeight = 32;

		private sealed class PendingStructureUpload
		{
			public Rectangle Area;
			public Point SpawnAnchor;
			public string StructureLabel;
			public PocketSnapCell[,] Cells;
			public int ExpectedChunks;
			public readonly HashSet<long> ReceivedChunkKeys = new HashSet<long>();
			public int TicksAlive;
			public const int TimeoutTicks = 1800;
		}

		private sealed class ClientStructureUpload
		{
			public Rectangle Area;
			public Point SpawnAnchor;
			public string StructureLabel;
			public PocketSnapCell[,] Cells;
			public Queue<Rectangle> PendingChunks;
		}

		private static readonly PendingStructureUpload[] PendingUploads = new PendingStructureUpload[256];

		private static Queue<Rectangle> _snapshotBroadcastQueue;
		private static int _snapshotBroadcastTarget = -1;
		private static int _snapshotBroadcastCooldown;

		private static ClientStructureUpload _clientUpload;

		private const int BroadcastIntervalTicks = 1;
		private const int ClientChunksPerTickBase = 2;
		private const int ClientChunksPerTickMax = 8;
		private const int MaxUploadAreaCells = 200_000;

		public static int CountNetworkChunks(Rectangle area)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return 0;
			int cols = (area.Width + NetChunkWidth - 1) / NetChunkWidth;
			int rows = (area.Height + NetChunkHeight - 1) / NetChunkHeight;
			return cols * rows;
		}

		public static IEnumerable<Rectangle> EnumerateNetworkChunks(Rectangle fullArea)
		{
			if (fullArea.Width <= 0 || fullArea.Height <= 0)
				yield break;

			int cols = (fullArea.Width + NetChunkWidth - 1) / NetChunkWidth;
			int rows = (fullArea.Height + NetChunkHeight - 1) / NetChunkHeight;
			for (int row = 0; row < rows; row++)
			{
				for (int col = 0; col < cols; col++)
				{
					int ox = col * NetChunkWidth;
					int oy = row * NetChunkHeight;
					int w = System.Math.Min(NetChunkWidth, fullArea.Width - ox);
					int h = System.Math.Min(NetChunkHeight, fullArea.Height - oy);
					yield return new Rectangle(fullArea.X + ox, fullArea.Y + oy, w, h);
				}
			}
		}

		public static void ClientSendOpenSessionChunked(
			Rectangle area,
			PocketSnapCell[,] pocketCells,
			Point spawnAnchor,
			string structureLabel)
		{
			CancelClientUpload();
			_clientUpload = new ClientStructureUpload
			{
				Area = area,
				SpawnAnchor = spawnAnchor,
				StructureLabel = structureLabel ?? "结构",
				Cells = pocketCells,
				PendingChunks = new Queue<Rectangle>(EnumerateNetworkChunks(area)),
			};

			SendOpenBeginPacket(area, spawnAnchor, _clientUpload.StructureLabel);
			PocketStrataChat.LogSubWorldEvent(
				$"开始分块上传「{_clientUpload.StructureLabel}」：{CountNetworkChunks(area)} 块",
				Color.Orange);
		}

		public static bool IsClientUploadInProgress() => _clientUpload != null;

		public static void ClientTickUploads()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || _clientUpload == null)
				return;

			int remaining = _clientUpload.PendingChunks.Count;
			int totalChunks = CountNetworkChunks(_clientUpload.Area);
			int chunksThisTick = remaining > totalChunks / 2
				? ClientChunksPerTickMax
				: ClientChunksPerTickBase;

			for (int i = 0; i < chunksThisTick && _clientUpload.PendingChunks.Count > 0; i++)
			{
				Rectangle chunk = _clientUpload.PendingChunks.Dequeue();
				SendOpenChunkPacket(_clientUpload.Area, _clientUpload.Cells, chunk);
			}

			if (_clientUpload.PendingChunks.Count > 0)
				return;

			SendOpenFinishPacket(_clientUpload.Area);
			PocketStrataChat.LogSubWorldEvent(
				$"「{_clientUpload.StructureLabel}」上传完毕，等待服务端生成子世界…",
				Color.Orange);
			_clientUpload = null;
		}

		public static void ServerReceiveOpenBegin(BinaryReader reader, int whoAmI)
		{
			if (whoAmI < 0 || whoAmI >= PendingUploads.Length)
				return;

			var area = ReadArea(reader);
			if (area.Width <= 0)
				return;

			if (area.Width * area.Height > MaxUploadAreaCells)
			{
				ReportUploadFailure(
					whoAmI,
					$"区域过大（{area.Width}×{area.Height}={area.Width * area.Height} 格，上限 {MaxUploadAreaCells} 格）");
				return;
			}

			int ax = reader.ReadInt16();
			int ay = reader.ReadInt16();
			string label = reader.ReadString();

			PendingUploads[whoAmI] = new PendingStructureUpload
			{
				Area = area,
				SpawnAnchor = new Point(ax, ay),
				StructureLabel = string.IsNullOrEmpty(label) ? "结构" : label,
				Cells = new PocketSnapCell[area.Width, area.Height],
				ExpectedChunks = CountNetworkChunks(area),
				TicksAlive = 0,
			};

			PocketStrataChat.LogSubWorldEvent(
				$"玩家 {whoAmI} 开始上传「{label}」{area.Width}×{area.Height}（{PendingUploads[whoAmI].ExpectedChunks} 块）",
				Color.LightGray);
		}

		public static void ServerReceiveOpenChunk(BinaryReader reader, int whoAmI)
		{
			PendingStructureUpload pending = GetPending(whoAmI);
			if (pending == null)
				return;

			var area = ReadArea(reader);
			if (!AreasEqual(area, pending.Area))
				return;

			int cx = reader.ReadInt16();
			int cy = reader.ReadInt16();
			int cw = reader.ReadByte();
			int ch = reader.ReadByte();
			if (cw <= 0 || ch <= 0)
				return;

			var chunk = new Rectangle(cx, cy, cw, ch);
			if (!area.Contains(chunk.X, chunk.Y))
				return;
			if (chunk.Right > area.Right || chunk.Bottom > area.Bottom)
				return;

			long key = ChunkKey(chunk.X, chunk.Y);
			if (!pending.ReceivedChunkKeys.Add(key))
				return;

			PocketSnapCell[,] cells = ReadChunkCells(reader, cw, ch);
			MergeChunkInto(pending.Cells, pending.Area, chunk, cells);
		}

		public static void ServerReceiveOpenFinish(BinaryReader reader, int whoAmI)
		{
			PendingStructureUpload pending = GetPending(whoAmI);
			if (pending == null)
			{
				ReportUploadFailure(whoAmI, "服务端未收到开始包或上传已过期，请重新按 F9");
				return;
			}

			var area = ReadArea(reader);
			if (!AreasEqual(area, pending.Area))
			{
				ReportUploadFailure(whoAmI, "结束包区域与开始不一致");
				return;
			}

			if (pending.ReceivedChunkKeys.Count < pending.ExpectedChunks)
			{
				ReportUploadFailure(
					whoAmI,
					$"结构块未收齐：{pending.ReceivedChunkKeys.Count}/{pending.ExpectedChunks}");
				return;
			}

			PendingUploads[whoAmI] = null;
			SendMessageToPlayer(
				whoAmI,
				global::PocketStrata.PocketStrata.PacketType.OpenSessionServerAccepted,
				$"[PocketStrata] 服务端已收到「{pending.StructureLabel}」，正在生成子世界…");

			PocketWorldVisualMaskSystem.ServerOpenSessionWithPocketTiles(
				pending.Area,
				pending.Cells,
				whoAmI,
				pending.SpawnAnchor,
				pending.StructureLabel);
		}

		public static void ServerBeginSnapshotBroadcast(int targetPlayer = -1)
		{
			if (!PocketWorldVisualMaskSystem.HasValidSessionArea || PocketWorldVisualMaskSystem.Snap == null)
			{
				_snapshotBroadcastQueue = new Queue<Rectangle>();
				return;
			}

			_snapshotBroadcastQueue = new Queue<Rectangle>(
				PocketWorldTileChunks.EnumerateFromSpawnAnchor(
					PocketWorldVisualMaskSystem.SessionTileArea,
					PocketWorldVisualMaskSystem.SessionSpawnAnchorLocal.X,
					PocketWorldVisualMaskSystem.SessionSpawnAnchorLocal.Y));
			_snapshotBroadcastTarget = targetPlayer;
			_snapshotBroadcastCooldown = 0;
		}

		public static void ServerTickBroadcasts()
		{
			if (Main.netMode != NetmodeID.Server)
				return;

			TickSnapshotBroadcast();
			TickUploadTimeouts();
		}

		private static void TickUploadTimeouts()
		{
			for (int i = 0; i < PendingUploads.Length; i++)
			{
				PendingStructureUpload pending = PendingUploads[i];
				if (pending == null)
					continue;

				pending.TicksAlive++;
				if (pending.TicksAlive > PendingStructureUpload.TimeoutTicks)
					ReportUploadFailure(i, "上传超时（30秒内未收齐分块），请重试");
			}
		}

		private static void TickSnapshotBroadcast()
		{
			if (_snapshotBroadcastQueue == null)
				return;

			if (_snapshotBroadcastCooldown > 0)
			{
				_snapshotBroadcastCooldown--;
				return;
			}

			if (_snapshotBroadcastQueue.Count == 0)
			{
				_snapshotBroadcastQueue = null;
				PocketWorldVisualMaskSystem.OnSnapshotBroadcastComplete();
				return;
			}

			Rectangle chunk = _snapshotBroadcastQueue.Dequeue();
			SendSnapshotChunkPacket(chunk, _snapshotBroadcastTarget);
			_snapshotBroadcastCooldown = BroadcastIntervalTicks;
		}

		// 向指定玩家下发全部真实备份 chunk
		public static void ServerSendSnapshotChunksToPlayer(int whoAmI)
		{
			if (Main.netMode != NetmodeID.Server || whoAmI < 0)
				return;
			if (!PocketWorldVisualMaskSystem.SessionActive || PocketWorldVisualMaskSystem.Snap == null)
				return;

			foreach (Rectangle chunk in PocketWorldTileChunks.EnumerateFromSpawnAnchor(
				PocketWorldVisualMaskSystem.SessionTileArea,
				PocketWorldVisualMaskSystem.SessionSpawnAnchorLocal.X,
				PocketWorldVisualMaskSystem.SessionSpawnAnchorLocal.Y))
			{
				SendSnapshotChunkPacket(chunk, whoAmI);
			}
		}

		// 向指定玩家下发全部口袋 chunk，末尾发 SyncPocketTilesComplete
		public static void ServerSendPocketTilesToPlayer(int whoAmI)
		{
			if (Main.netMode != NetmodeID.Server || whoAmI < 0)
				return;
			if (!PocketWorldVisualMaskSystem.SessionActive || PocketWorldVisualMaskSystem.PocketTiles == null)
				return;

			foreach (Rectangle chunk in PocketWorldTileChunks.EnumerateFromSpawnAnchor(
				PocketWorldVisualMaskSystem.SessionTileArea,
				PocketWorldVisualMaskSystem.SessionSpawnAnchorLocal.X,
				PocketWorldVisualMaskSystem.SessionSpawnAnchorLocal.Y))
			{
				SendPocketTilesChunkPacket(chunk, whoAmI);
			}

			SendPocketTilesCompletePacket(whoAmI);
		}

		// 客户端：合并 SyncSnapshotChunk 到 _realBackup
		public static void ClientReceiveSnapshotChunk(BinaryReader reader)
		{
			var sessionArea = ReadArea(reader);
			if (sessionArea.Width <= 0)
				return;

			if (!PocketWorldVisualMaskSystem.SessionActive
				|| !AreasEqual(PocketWorldVisualMaskSystem.SessionTileArea, sessionArea))
			{
				PocketWorldVisualMaskSystem.PrepareClientSnapshotBuffer(sessionArea);
			}

			int cx = reader.ReadInt16();
			int cy = reader.ReadInt16();
			int cw = reader.ReadByte();
			int ch = reader.ReadByte();
			var chunk = new Rectangle(cx, cy, cw, ch);
			PocketSnapCell[,] cells = ReadChunkCells(reader, cw, ch);
			PocketWorldVisualMaskSystem.MergeRealBackupChunk(chunk, cells);
		}

		// 客户端：合并 SyncPocketTilesChunk 到 _pocketTiles（包内带会话区头）
		public static void ClientReceivePocketTilesChunk(BinaryReader reader)
		{
			var sessionArea = ReadArea(reader);
			if (sessionArea.Width <= 0)
				return;

			if (!PocketWorldVisualMaskSystem.SessionActive
				|| !AreasEqual(PocketWorldVisualMaskSystem.SessionTileArea, sessionArea))
			{
				PocketWorldVisualMaskSystem.PrepareClientPocketBuffer(sessionArea);
			}

			PocketWorldVisualMaskSystem.ClientMarkPocketTilesSyncInProgress();

			int cx = reader.ReadInt16();
			int cy = reader.ReadInt16();
			int cw = reader.ReadByte();
			int ch = reader.ReadByte();
			var chunk = new Rectangle(cx, cy, cw, ch);
			PocketSnapCell[,] cells = ReadChunkCells(reader, cw, ch);
			PocketWorldVisualMaskSystem.CacheClientPocketChunk(cells, chunk);
		}

		public static void Reset()
		{
			for (int i = 0; i < PendingUploads.Length; i++)
				PendingUploads[i] = null;
			_snapshotBroadcastQueue = null;
			_snapshotBroadcastTarget = -1;
			_clientUpload = null;
		}

		private static void CancelClientUpload() => _clientUpload = null;

		private static PendingStructureUpload GetPending(int whoAmI)
		{
			if (whoAmI < 0 || whoAmI >= PendingUploads.Length)
				return null;
			return PendingUploads[whoAmI];
		}

		private static void SendMessageToPlayer(int whoAmI, global::PocketStrata.PocketStrata.PacketType type, string message)
		{
			if (Main.netMode != NetmodeID.Server || whoAmI < 0)
				return;

			ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
			p.Write((byte)type);
			p.Write(message);
			p.Send(whoAmI, -1);
		}

		private static void ReportUploadFailure(int whoAmI, string reason)
		{
			PendingUploads[whoAmI] = null;
			string msg = $"[PocketStrata] 子世界生成失败：{reason}";
			ModContent.GetInstance<global::PocketStrata.PocketStrata>().Logger.Warn($"Player {whoAmI}: {reason}");

			if (Main.netMode == NetmodeID.Server && whoAmI >= 0)
			{
				ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
				p.Write((byte)global::PocketStrata.PocketStrata.PacketType.OpenSessionUploadFailed);
				p.Write(msg);
				p.Send(whoAmI, -1);
			}
			else
			{
				PocketStrataChat.NewText(msg, Color.OrangeRed);
			}
		}

		private static long ChunkKey(int chunkX, int chunkY) =>
			((long)chunkX << 32) | (uint)chunkY;

		private static bool AreasEqual(Rectangle a, Rectangle b) =>
			a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

		private static Rectangle ReadArea(BinaryReader reader) =>
			new Rectangle(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());

		private static void SendOpenBeginPacket(Rectangle area, Point spawnAnchor, string label)
		{
			var mod = ModContent.GetInstance<global::PocketStrata.PocketStrata>();
			ModPacket begin = mod.GetPacket();
			begin.Write((byte)global::PocketStrata.PocketStrata.PacketType.OpenSessionNamedBegin);
			begin.Write((short)area.X);
			begin.Write((short)area.Y);
			begin.Write((short)area.Width);
			begin.Write((short)area.Height);
			begin.Write((short)spawnAnchor.X);
			begin.Write((short)spawnAnchor.Y);
			begin.Write(label ?? "结构");
			begin.Send();
		}

		private static void SendOpenChunkPacket(Rectangle area, PocketSnapCell[,] pocketCells, Rectangle chunk)
		{
			PocketSnapCell[,] cells = PocketWorldTileChunks.Extract(pocketCells, area, chunk);
			var mod = ModContent.GetInstance<global::PocketStrata.PocketStrata>();
			ModPacket pkt = mod.GetPacket();
			pkt.Write((byte)global::PocketStrata.PocketStrata.PacketType.OpenSessionNamedChunk);
			pkt.Write((short)area.X);
			pkt.Write((short)area.Y);
			pkt.Write((short)area.Width);
			pkt.Write((short)area.Height);
			pkt.Write((short)chunk.X);
			pkt.Write((short)chunk.Y);
			pkt.Write((byte)chunk.Width);
			pkt.Write((byte)chunk.Height);
			WriteChunkCells(pkt, cells, chunk.Width, chunk.Height);
			pkt.Send();
		}

		private static void SendOpenFinishPacket(Rectangle area)
		{
			var mod = ModContent.GetInstance<global::PocketStrata.PocketStrata>();
			ModPacket finish = mod.GetPacket();
			finish.Write((byte)global::PocketStrata.PocketStrata.PacketType.OpenSessionNamedFinish);
			finish.Write((short)area.X);
			finish.Write((short)area.Y);
			finish.Write((short)area.Width);
			finish.Write((short)area.Height);
			finish.Send();
		}

		private static void WriteChunkCells(BinaryWriter writer, PocketSnapCell[,] cells, int w, int h)
		{
			for (int dx = 0; dx < w; dx++)
			{
				for (int dy = 0; dy < h; dy++)
					PocketSnapCell.WriteV2(writer, cells[dx, dy]);
			}
		}

		private static PocketSnapCell[,] ReadChunkCells(BinaryReader reader, int w, int h)
		{
			var cells = new PocketSnapCell[w, h];
			for (int dx = 0; dx < w; dx++)
			{
				for (int dy = 0; dy < h; dy++)
					cells[dx, dy] = PocketSnapCell.ReadV2(reader);
			}
			return cells;
		}

		private static void MergeChunkInto(PocketSnapCell[,] target, Rectangle fullArea, Rectangle chunk, PocketSnapCell[,] cells)
		{
			int ox = chunk.X - fullArea.X;
			int oy = chunk.Y - fullArea.Y;
			for (int dx = 0; dx < chunk.Width; dx++)
			{
				for (int dy = 0; dy < chunk.Height; dy++)
					target[ox + dx, oy + dy] = cells[dx, dy];
			}
		}

		private static void SendSnapshotChunkPacket(Rectangle chunk, int targetPlayer)
		{
			if (PocketWorldVisualMaskSystem.Snap == null)
				return;

			Rectangle session = PocketWorldVisualMaskSystem.SessionTileArea;
			PocketSnapCell[,] cells = PocketWorldTileChunks.Extract(PocketWorldVisualMaskSystem.Snap, session, chunk);

			ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
			p.Write((byte)global::PocketStrata.PocketStrata.PacketType.SyncSnapshotChunk);
			p.Write((short)session.X);
			p.Write((short)session.Y);
			p.Write((short)session.Width);
			p.Write((short)session.Height);
			p.Write((short)chunk.X);
			p.Write((short)chunk.Y);
			p.Write((byte)chunk.Width);
			p.Write((byte)chunk.Height);
			WriteChunkCells(p, cells, chunk.Width, chunk.Height);
			p.Send(targetPlayer, -1);
		}

		private static void SendPocketTilesChunkPacket(Rectangle chunk, int targetPlayer)
		{
			if (PocketWorldVisualMaskSystem.PocketTiles == null)
				return;

			Rectangle session = PocketWorldVisualMaskSystem.SessionTileArea;
			PocketSnapCell[,] cells = PocketWorldTileChunks.Extract(PocketWorldVisualMaskSystem.PocketTiles, session, chunk);

			ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
			p.Write((byte)global::PocketStrata.PocketStrata.PacketType.SyncPocketTilesChunk);
			p.Write((short)session.X);
			p.Write((short)session.Y);
			p.Write((short)session.Width);
			p.Write((short)session.Height);
			p.Write((short)chunk.X);
			p.Write((short)chunk.Y);
			p.Write((byte)chunk.Width);
			p.Write((byte)chunk.Height);
			WriteChunkCells(p, cells, chunk.Width, chunk.Height);
			p.Send(targetPlayer, -1);
		}

		private static void SendPocketTilesCompletePacket(int targetPlayer)
		{
			ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
			p.Write((byte)global::PocketStrata.PocketStrata.PacketType.SyncPocketTilesComplete);
			p.Send(targetPlayer, -1);
		}
	}
}
