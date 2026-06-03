using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata;
using PocketStrata.Content.IO;
using PocketStrata.Content.Players;

namespace PocketStrata.Content.Systems
{
	// 口袋会话关闭原因（网络包与还原逻辑共用）
	public enum PocketSessionCloseReason : byte
	{
		Manual = 0,
		BossDeath = 1,
		AutoRestore = 2,
	}

	// 会话关闭包策略字段（协议兼容；客户端收到后一律直接还原真实地形）
	public enum PocketCloseAnimPolicy : byte
	{
		None = 0,
		LocalRequesterOnly = 1,
		AllInsiders = 2,
	}

	// 子世界会话与地形：服务端会话区 Main.tile 为口袋权威；进入者用 chunk + SendTileSquare 同步，旁观者保持真实地形
	public sealed class PocketWorldVisualMaskSystem : ModSystem
	{
		private static bool _sessionActive;
		private static Rectangle _sessionTileArea;
		private static int _sessionInsiderIndex = -1;
		private static int _suppressTileDropDepth;

		// 批量写 tile 期间为 true，供 GlobalTile 等抑制掉落
		public static bool IsSuppressingTileDrops => _suppressTileDropDepth > 0;

		// 开启会话前抓取的真实地形（关会话写回、向旁观者发 section 时临时套用）
		private static PocketSnapCell[,] _realBackup;

		// 口袋结构快照（服务端权威，并下发给进入者客户端）
		private static PocketSnapCell[,] _pocketTiles;

		public static bool SessionActive => _sessionActive;

		public static Rectangle SessionTileArea => _sessionTileArea;

		public static bool HasValidSessionArea => _sessionTileArea.Width > 0 && _sessionTileArea.Height > 0;

		public static PocketSnapCell[,] Snap => _realBackup;

		public static PocketSnapCell[,] PocketTiles => _pocketTiles;

		public static int SessionInsiderIndex => _sessionInsiderIndex;

		public static string CurrentSessionLabel => _currentSessionLabel ?? "结构";

		public static bool IsTileInsideSessionArea(int tileX, int tileY)
		{
			return _sessionActive && HasValidSessionArea && _sessionTileArea.Contains(tileX, tileY);
		}

		private static bool _lastF9Down;
		private static int _f9StructureRotateIndex;
		private static Point _sessionSpawnAnchorLocal;
		private static Queue<Rectangle> _serverChunkApplyQueue;
		private static int _serverChunkApplyCooldown;
		private static string _pendingClientStructureLabel;
		private static bool _pendingApplyPocketInterior;
		private static bool _clientPocketInteriorApplied;
		private static bool _clientPocketTilesSyncReady;
		private static PocketSnapCell[,] _clientOpenTerrainSnapshot;
		private static int _pendingEnterTriggerPlayer = -1;
		private static bool _serverPocketApplyComplete;
		private static bool _snapshotBroadcastComplete;
		private static string _currentSessionLabel = "结构";

		// 曾设为进入者的玩家 whoAmI，用于「全员离线后自动还原」判定
		private static readonly HashSet<int> _playersWhoEnteredSubWorld = new HashSet<int>();

		private const int ServerChunkApplyIntervalTicks = 2;

		public static Point SessionSpawnAnchorLocal => _sessionSpawnAnchorLocal;

		// 以玩家脚底中心格为锚，划定 width×height 的会话矩形（偶数宽时左右对称）
		public static Rectangle GetSessionRectAroundPlayerFeet(Player pl, int widthTiles, int heightTiles)
		{
			Vector2 feet = pl.Bottom;
			int cx = (int)(feet.X / 16f);
			int cy = (int)(feet.Y / 16f);
			return new Rectangle(cx - widthTiles / 2, cy - heightTiles / 2, widthTiles, heightTiles);
		}

		public override void Load()
		{
			On_WorldGen.KillTile += WorldGen_KillTile_SuppressDrops;
		}

		public override void Unload()
		{
			On_WorldGen.KillTile -= WorldGen_KillTile_SuppressDrops;
			_suppressTileDropDepth = 0;
			_realBackup = null;
			_pocketTiles = null;
			_sessionInsiderIndex = -1;
			CancelServerStagedApply();
			PocketWorldChunkNetwork.Reset();
		}

		private static void WorldGen_KillTile_SuppressDrops(
			On_WorldGen.orig_KillTile orig,
			int i,
			int j,
			bool fail,
			bool effectOnly,
			bool noItem)
		{
			if (IsSuppressingTileDrops)
				noItem = true;

			orig(i, j, fail, effectOnly, noItem);
		}

		public override void PostUpdateEverything()
		{
			PocketWorldChunkNetwork.ServerTickBroadcasts();
			PocketWorldChunkNetwork.ClientTickUploads();
			ServerTickStagedChunkApply();
			TryAutoRestoreWhenAllSubWorldPlayersOffline();

			if (Main.netMode == NetmodeID.MultiplayerClient && _pendingApplyPocketInterior)
				ClientApplyPocketInterior();

			if (Main.dedServ || Main.gameMenu)
				return;

			KeyboardState kb = Keyboard.GetState();
			bool f9 = kb.IsKeyDown(Keys.F9);
			if (f9 && !_lastF9Down)
				ToggleDebugSessionWithFeedback();
			_lastF9Down = f9;
		}

		private static void ToggleDebugSessionWithFeedback()
		{
			Player pl = Main.LocalPlayer;
			if (pl == null || !pl.active)
				return;

			if (IsLocalClientPocketSessionOpen())
			{
				if (Main.netMode == NetmodeID.SinglePlayer)
				{
					ServerCloseSession();
					PocketStrataChat.NewText("[pocketview] F9：口袋已关闭（单机）。", Color.LightGray);
				}
				else
				{
					ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
					p.Write((byte)global::PocketStrata.PocketStrata.PacketType.RequestClosePocketSession);
					p.Send();
					PocketStrataChat.NewText("[pocketview] F9：已请求关闭口袋…", Color.LightGray);
				}

				return;
			}

			TryApplySelectedStructure(pl);
		}

		// F9：应用已选 .pstrata，或轮换目录中的下一个结构
		public static void TryApplySelectedStructure(Player pl)
		{
			if (pl == null || !pl.active)
				return;

			if (Main.netMode == NetmodeID.MultiplayerClient && PocketWorldChunkNetwork.IsClientUploadInProgress())
			{
				PocketStrataChat.NewText("[PocketStrata] 仍在向服务端上传结构，请稍候…", Color.Orange);
				return;
			}

			var recorder = pl.GetModPlayer<TerrainRecorderPlayer>();
			string structureId = recorder.SelectedStructureForF9;
			if (string.IsNullOrEmpty(structureId))
			{
				var list = PocketStructureLibrary.ListStructureIds();
				if (list.Count == 0)
				{
					PocketStrataChat.NewText("[PocketStrata] 尚无 .pstrata 结构。手持地形采集记录器框选后 /ps save 名称。", Color.Orange);
					PocketStrataChat.NewText("[PocketStrata] 目录: " + PocketStructureLibrary.StructuresDirectory, Color.Gray);
					return;
				}

				structureId = list[_f9StructureRotateIndex % list.Count];
				_f9StructureRotateIndex++;
				recorder.SelectedStructureForF9 = structureId;
				PocketStrataChat.NewText("[PocketStrata] F9 选用结构: " + structureId + "（/ps select 可固定）", new Color(120, 220, 255));
			}

			if (!PocketStructureLibrary.TryLoad(structureId, out PocketStructureData data, out string loadError))
			{
				PocketStrataChat.NewText("[PocketStrata] " + loadError, Color.OrangeRed);
				return;
			}

			Rectangle area = data.GetPlacementRectAroundPlayerFeet(pl);
			RequestOpenSessionWithPocket(pl, area, data.Cells, structureId, new Point(data.SpawnAnchorX, data.SpawnAnchorY));
		}

		public static void RequestOpenSessionWithPocket(
			Player pl,
			Rectangle area,
			PocketSnapCell[,] pocketCells,
			string structureId = null,
			Point? spawnAnchorLocal = null)
		{
			if (pl == null || !pl.active || area.Width <= 0 || area.Height <= 0 || pocketCells == null)
				return;

			Point anchor = spawnAnchorLocal ?? new Point(area.Width / 2, area.Height / 2);
			string label = string.IsNullOrEmpty(structureId) ? "未命名结构" : structureId;

			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				ServerOpenSessionWithPocketTiles(area, pocketCells, pl.whoAmI, anchor, label);
				return;
			}

			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				_clientOpenTerrainSnapshot = CaptureTileArray(area);
				_pocketTiles = PocketStructureFile.CloneCells(pocketCells);
				_pendingClientStructureLabel = label;
				PocketWorldChunkNetwork.ClientSendOpenSessionChunked(area, pocketCells, anchor, label);
				return;
			}

			// 开服主机：本地直接开会话，不走客户端上传包
			if (Main.netMode == NetmodeID.Server && !Main.dedServ)
			{
				ServerOpenSessionWithPocketTiles(area, pocketCells, pl.whoAmI, anchor, label);
			}
		}

		public override void OnWorldLoad()
		{
			SetSessionState(false, Rectangle.Empty);
		}

		public static void NotifyEnteredWorldFromLocalPlayer()
		{
			if (Main.dedServ)
				return;
			PocketStrataChat.NewText(
				"[PocketStrata] F9 应用 .pstrata 结构；/ps list /ps select；/ps messages 可关闭提示。",
				new Color(120, 220, 255));
		}

		public override void OnWorldUnload()
		{
			// 兜底：PreSaveAndQuit 未走时（如强关进程），卸载前仍尝试还原
			TryAutoRestorePocketTerrainBeforeWorldPersist();
		}

		// 单人「保存并退出」写盘前：未关闭会话则先还原真实地形
		public override void PreSaveAndQuit()
		{
			TryAutoRestorePocketTerrainBeforeWorldPersist();
		}

		// 单人：持久化前自动还原未关闭的口袋区，避免 .twld 写入口袋方块
		private static void TryAutoRestorePocketTerrainBeforeWorldPersist()
		{
			if (!_sessionActive || !HasValidSessionArea || _realBackup == null)
				return;

			if (Main.netMode != NetmodeID.SinglePlayer)
				return;

			ServerRestoreSessionWithoutAnimation(PocketSessionCloseReason.AutoRestore);
		}

		// 多人：全部进入者离线后会话仍开着时，服务端自动写回真实备份
		private static void TryAutoRestoreWhenAllSubWorldPlayersOffline()
		{
			if (!_sessionActive || !HasValidSessionArea || _realBackup == null)
				return;

			if (Main.netMode != NetmodeID.Server)
				return;

			if (_playersWhoEnteredSubWorld.Count == 0)
				return;

			for (int i = 0; i < Main.maxPlayers; i++)
			{
				if (!_playersWhoEnteredSubWorld.Contains(i))
					continue;

				if (Main.player[i] != null && Main.player[i].active)
					return;
			}

			ServerRestoreSessionWithoutAnimation(PocketSessionCloseReason.AutoRestore);
		}

		// 记录该玩家曾为进入者（ServerSetSeesPocketInterior(true) 时调用）
		internal static void RecordPlayerEnteredSubWorld(int playerIndex)
		{
			if (playerIndex >= 0 && playerIndex < Main.maxPlayers)
				_playersWhoEnteredSubWorld.Add(playerIndex);
		}

		private static void ClearSubWorldPlayerTracking()
		{
			_playersWhoEnteredSubWorld.Clear();
		}

		public override void ClearWorld()
		{
			_pocketTiles = null;
			_realBackup = null;
			_sessionInsiderIndex = -1;
			_pendingApplyPocketInterior = false;
			_clientPocketInteriorApplied = false;
			_clientPocketTilesSyncReady = false;
			_clientOpenTerrainSnapshot = null;
			_pendingEnterTriggerPlayer = -1;
			_serverPocketApplyComplete = false;
			_snapshotBroadcastComplete = false;
			ClearSubWorldPlayerTracking();
			PocketWorldChunkNetwork.Reset();
			SetSessionState(false, Rectangle.Empty);
		}

		// 子世界 Boss 被击败时关闭会话（由外部模组调用）
		public static void NotifySessionBossDefeated()
		{
			if (!_sessionActive || !HasValidSessionArea)
				return;

			if (Main.netMode == NetmodeID.MultiplayerClient)
				return;

			ServerCloseSession(PocketSessionCloseReason.BossDeath);
		}

		// 客户端：将 SyncPocketTilesChunk 合并进 _pocketTiles
		internal static void CacheClientPocketChunk(PocketSnapCell[,] chunkCells, Rectangle chunkArea)
		{
			if (Main.netMode == NetmodeID.Server || chunkCells == null || !HasValidSessionArea)
				return;

			if (_pocketTiles == null
				|| _pocketTiles.GetLength(0) != _sessionTileArea.Width
				|| _pocketTiles.GetLength(1) != _sessionTileArea.Height)
			{
				_pocketTiles = new PocketSnapCell[_sessionTileArea.Width, _sessionTileArea.Height];
			}

			MergeCellsIntoArray(_pocketTiles, _sessionTileArea, chunkArea, chunkCells);
		}

		// 客户端：收到快照 chunk 前初始化会话区与 _realBackup 数组
		internal static void PrepareClientSnapshotBuffer(Rectangle sessionArea)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || sessionArea.Width <= 0)
				return;

			SetSessionState(true, sessionArea);
			_realBackup = new PocketSnapCell[sessionArea.Width, sessionArea.Height];
		}

		// 口袋 chunk 可能早于会话包到达，先建立会话区与 _pocketTiles 数组
		internal static void PrepareClientPocketBuffer(Rectangle sessionArea)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || sessionArea.Width <= 0)
				return;

			if (!SessionActive || _sessionTileArea != sessionArea)
				SetSessionState(true, sessionArea);

			if (_pocketTiles == null
				|| _pocketTiles.GetLength(0) != sessionArea.Width
				|| _pocketTiles.GetLength(1) != sessionArea.Height)
			{
				_pocketTiles = new PocketSnapCell[sessionArea.Width, sessionArea.Height];
			}
		}

		// 服务端口袋已写入 Main.tile 后，向指定玩家 SendTileSquare（绕过 section 已发送标记）
		internal static void ServerTrySendPocketTileSquareToPlayer(int whoAmI)
		{
			if (Main.netMode != NetmodeID.Server || whoAmI < 0 || !SessionActive || !HasValidSessionArea)
				return;
			if (_pocketTiles == null || _serverChunkApplyQueue != null)
				return;

			NetMessage.SendTileSquare(
				whoAmI,
				_sessionTileArea.X,
				_sessionTileArea.Y,
				_sessionTileArea.Width,
				_sessionTileArea.Height);
		}

		private static bool ClientPocketTilesHasPlacedContent()
		{
			if (_pocketTiles == null || !HasValidSessionArea)
				return false;

			int w = _pocketTiles.GetLength(0);
			int h = _pocketTiles.GetLength(1);
			for (int x = 0; x < w; x++)
			{
				for (int y = 0; y < h; y++)
				{
					if (_pocketTiles[x, y].HasPlacedContent)
						return true;
				}
			}

			return false;
		}

		internal static void MergeRealBackupChunk(Rectangle chunkArea, PocketSnapCell[,] chunkCells)
		{
			if (chunkCells == null || !HasValidSessionArea)
				return;

			if (_realBackup == null
				|| _realBackup.GetLength(0) != _sessionTileArea.Width
				|| _realBackup.GetLength(1) != _sessionTileArea.Height)
			{
				_realBackup = new PocketSnapCell[_sessionTileArea.Width, _sessionTileArea.Height];
			}

			MergeCellsIntoArray(_realBackup, _sessionTileArea, chunkArea, chunkCells);
		}

		private static void MergeCellsIntoArray(
			PocketSnapCell[,] target,
			Rectangle fullArea,
			Rectangle chunkArea,
			PocketSnapCell[,] chunkCells)
		{
			int ox = chunkArea.X - fullArea.X;
			int oy = chunkArea.Y - fullArea.Y;
			for (int dx = 0; dx < chunkArea.Width; dx++)
			{
				for (int dy = 0; dy < chunkArea.Height; dy++)
					target[ox + dx, oy + dy] = chunkCells[dx, dy];
			}
		}

		public override void NetSend(BinaryWriter writer)
		{
			writer.Write(_sessionActive);
			writer.Write((byte)0);
			writer.Write((short)_sessionTileArea.X);
			writer.Write((short)_sessionTileArea.Y);
			writer.Write((short)_sessionTileArea.Width);
			writer.Write((short)_sessionTileArea.Height);
			writer.Write((short)_sessionSpawnAnchorLocal.X);
			writer.Write((short)_sessionSpawnAnchorLocal.Y);
			writer.Write(_sessionActive ? (_currentSessionLabel ?? "结构") : string.Empty);

			bool hasBackup = _realBackup != null
				&& _sessionActive
				&& HasValidSessionArea
				&& _realBackup.GetLength(0) == _sessionTileArea.Width
				&& _realBackup.GetLength(1) == _sessionTileArea.Height;

			writer.Write(hasBackup);
			// 多人真实备份走 SyncSnapshotChunk 分块，此处仅单机写入整包
			if (hasBackup && Main.netMode == NetmodeID.SinglePlayer)
				WriteSnapshotCells(writer, _sessionTileArea, _realBackup);
		}

		public override void NetReceive(BinaryReader reader)
		{
			ReadSession(reader, out _);
			bool hasBackup = reader.ReadBoolean();
			if (hasBackup && HasValidSessionArea)
			{
				if (Main.netMode == NetmodeID.SinglePlayer)
					ReadSnapshotCellsInto(reader, _sessionTileArea, assignToRealBackup: true);
				else
					_realBackup = new PocketSnapCell[_sessionTileArea.Width, _sessionTileArea.Height];
			}
			else
				_realBackup = null;
		}

		// 处理 SyncPocketVisualSession（会话开关、区域、结构名；关闭时还原客户端地形）
		public static void ReceiveSessionModPacket(BinaryReader reader)
		{
			bool wasActive = _sessionActive;
			Rectangle area = _sessionTileArea;

			ReadSession(reader, out byte closePolicyByte);

			if (Main.netMode == NetmodeID.MultiplayerClient && _sessionActive
				&& Main.LocalPlayer?.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior == true)
				ClientApplyPocketInterior();

			if (Main.netMode == NetmodeID.MultiplayerClient && wasActive && !_sessionActive)
			{
				ClientRestoreRealTerrainOnSessionClose(area);
				ClearClientSessionFlags();
			}
		}

		public static void ReceiveSnapshotModPacket(BinaryReader reader)
		{
			short x = reader.ReadInt16();
			short y = reader.ReadInt16();
			short w = reader.ReadInt16();
			short h = reader.ReadInt16();
			if (w <= 0 || h <= 0)
			{
				_realBackup = null;
				return;
			}

			var area = new Rectangle(x, y, w, h);
			if (!_sessionActive || _sessionTileArea != area)
				return;

			ReadSnapshotCellsInto(reader, area, assignToRealBackup: true);
		}

		private static void ReadSession(BinaryReader reader, out byte closePolicyWhenInactive)
		{
			bool active = reader.ReadBoolean();
			closePolicyWhenInactive = reader.ReadByte();
			short x = reader.ReadInt16();
			short y = reader.ReadInt16();
			short w = reader.ReadInt16();
			short h = reader.ReadInt16();
			short ax = reader.ReadInt16();
			short ay = reader.ReadInt16();
			if (!active || w <= 0 || h <= 0)
			{
				SetSessionState(false, Rectangle.Empty);
				reader.ReadString();
				return;
			}

			var newRect = new Rectangle(x, y, w, h);
			if (!_sessionActive || _sessionTileArea != newRect)
				_realBackup = null;
			SetSessionState(true, newRect);
			_sessionSpawnAnchorLocal = new Point(ax, ay);
			string label = reader.ReadString();
			if (!string.IsNullOrEmpty(label))
				_pendingClientStructureLabel = label;
		}

		public static void SetSessionState(bool active, Rectangle tileArea)
		{
			if (!active || tileArea.Width <= 0 || tileArea.Height <= 0)
			{
				_sessionActive = false;
				_sessionTileArea = Rectangle.Empty;
				_sessionInsiderIndex = -1;
				_realBackup = null;
				_sessionSpawnAnchorLocal = Point.Zero;
				CancelServerStagedApply();
				ClearSubWorldPlayerTracking();
				return;
			}

			_sessionActive = true;
			_sessionTileArea = tileArea;
			ClearSubWorldPlayerTracking();
		}

		// 抓取 Main.tile 矩形区域为 PocketSnapCell 数组
		public static PocketSnapCell[,] CaptureTileArray(Rectangle area)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return null;

			var arr = new PocketSnapCell[area.Width, area.Height];
			for (int dx = 0; dx < area.Width; dx++)
			{
				for (int dy = 0; dy < area.Height; dy++)
				{
					int tx = area.X + dx;
					int ty = area.Y + dy;
					if (tx < 0 || ty < 0 || tx >= Main.maxTilesX || ty >= Main.maxTilesY)
						continue;

					Tile t = Main.tile[tx, ty];
					arr[dx, dy] = PocketSnapCell.FromTile(t);
				}
			}

			return arr;
		}

		// 客户端进入者：将 _pocketTiles 写入 Main.tile（数据未就绪时设 pending，下帧重试）
		public static void ClientApplyPocketInterior()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			if (Main.LocalPlayer?.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior != true)
			{
				_pendingApplyPocketInterior = true;
				return;
			}

			if (!SessionActive || !HasValidSessionArea)
			{
				_pendingApplyPocketInterior = true;
				return;
			}

			bool tilesReady = _pocketTiles != null
				&& _pocketTiles.GetLength(0) == _sessionTileArea.Width
				&& _pocketTiles.GetLength(1) == _sessionTileArea.Height;

			if (!tilesReady || !ClientPocketTilesHasPlacedContent())
			{
				_pendingApplyPocketInterior = true;
				return;
			}

			_pendingApplyPocketInterior = false;

			ApplyTileArray(_pocketTiles, _sessionTileArea, refreshFrames: true);

			if (!_clientPocketInteriorApplied)
			{
				string label = _pendingClientStructureLabel ?? "结构";
				NotifySubWorldOpened(label, _sessionTileArea, _sessionSpawnAnchorLocal);
				_pendingClientStructureLabel = null;
				_clientPocketInteriorApplied = true;
			}
		}

		// 客户端取消进入者视角：用真实备份还原 Main.tile
		public static void ClientRestoreInsiderExit(Rectangle area)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || area.Width <= 0)
				return;

			PocketSnapCell[,] realBackup = GetClientRestoreBackup(area);
			if (realBackup == null)
				return;

			_clientPocketInteriorApplied = false;
			_pendingApplyPocketInterior = false;
			ApplyTileArray(realBackup, area, refreshFrames: true);
		}

		// 兼容旧版 OpenSessionEnter 包：只读结构名并触发 ClientApply（会话区以 SyncPocketVisualSession 为准）
		public static void ClientReceiveOpenSessionEnterLegacy(BinaryReader reader)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			string label = reader.ReadString();
			_ = reader.ReadInt16();
			_ = reader.ReadInt16();
			_ = reader.ReadInt16();
			_ = reader.ReadInt16();
			_ = reader.ReadInt16();
			_ = reader.ReadInt16();

			if (!string.IsNullOrEmpty(label))
				_pendingClientStructureLabel = label;

			ClientApplyPocketInterior();
		}

		private static bool IsLocalClientPocketSessionOpen()
		{
			if (_sessionActive)
				return true;
			return Main.netMode == NetmodeID.MultiplayerClient
				&& Main.LocalPlayer?.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior == true;
		}

		private static void ClearClientSessionFlags()
		{
			_pendingApplyPocketInterior = false;
			_clientPocketInteriorApplied = false;
			_clientPocketTilesSyncReady = false;
			_clientOpenTerrainSnapshot = null;
			_pendingClientStructureLabel = null;
		}

		private static PocketSnapCell[,] GetClientRestoreBackup(Rectangle area)
		{
			if (_realBackup != null
				&& _realBackup.GetLength(0) == area.Width
				&& _realBackup.GetLength(1) == area.Height)
			{
				return _realBackup;
			}

			if (_clientOpenTerrainSnapshot != null
				&& _clientOpenTerrainSnapshot.GetLength(0) == area.Width
				&& _clientOpenTerrainSnapshot.GetLength(1) == area.Height)
			{
				return _clientOpenTerrainSnapshot;
			}

			return null;
		}

		// 客户端：收到会话关闭包后还原真实地形
		private static void ClientRestoreRealTerrainOnSessionClose(Rectangle area)
		{
			if (area.Width <= 0)
				return;

			PocketSnapCell[,] realBackup = GetClientRestoreBackup(area);
			if (realBackup != null)
				ApplyTileArray(realBackup, area, refreshFrames: true);
		}

		// 客户端：口袋 chunk 全部收齐（SyncPocketTilesComplete）
		internal static void ClientOnPocketTilesSyncComplete()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			_clientPocketTilesSyncReady = true;

			if (_pendingApplyPocketInterior
				|| Main.LocalPlayer?.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior == true)
				ClientApplyPocketInterior();
		}

		// 收到新的口袋 chunk 时标记同步未完成
		internal static void ClientMarkPocketTilesSyncInProgress() => _clientPocketTilesSyncReady = false;

		// 在抑制方块/墙掉落的前提下执行委托
		public static void RunWithoutTileDrops(Action action)
		{
			if (action == null)
				return;

			_suppressTileDropDepth++;
			try
			{
				action();
			}
			finally
			{
				_suppressTileDropDepth--;
			}
		}

		// 单格快照写入 Main.tile（抑制掉落）
		public static void ApplySnapCell(int tx, int ty, PocketSnapCell c)
		{
			RunWithoutTileDrops(() => ApplySnapCellCore(tx, ty, c));
		}

		internal static void ApplySnapCellCore(int tx, int ty, PocketSnapCell c)
		{
			c.ApplyToTile(tx, ty);
		}

		// 刷新区域内方块自动拼帧
		internal static void RefreshTileFramesForArea(Rectangle area)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return;

			int pad = 1;
			int x1 = System.Math.Max(0, area.X - pad);
			int y1 = System.Math.Max(0, area.Y - pad);
			int x2 = System.Math.Min(Main.maxTilesX - 1, area.X + area.Width + pad - 1);
			int y2 = System.Math.Min(Main.maxTilesY - 1, area.Y + area.Height + pad - 1);

			for (int x = x1; x <= x2; x++)
			{
				for (int y = y1; y <= y2; y++)
				{
					Tile t = Main.tile[x, y];
					if (!t.HasTile)
						continue;
					if (Main.tileFrameImportant[t.TileType])
						continue;
					WorldGen.TileFrame(x, y);
				}
			}
		}

		// 快照数组写入 Main.tile 矩形（抑制掉落，可选刷新帧）
		public static void ApplyTileArray(PocketSnapCell[,] arr, Rectangle area, bool refreshFrames = true)
		{
			if (arr == null || area.Width <= 0 || area.Height <= 0)
				return;
			if (arr.GetLength(0) != area.Width || arr.GetLength(1) != area.Height)
				return;

			RunWithoutTileDrops(() =>
			{
				for (int dx = 0; dx < area.Width; dx++)
				{
					for (int dy = 0; dy < area.Height; dy++)
					{
						int tx = area.X + dx;
						int ty = area.Y + dy;
						ApplySnapCellCore(tx, ty, arr[dx, dy]);
					}
				}

				if (refreshFrames)
					RefreshTileFramesForArea(area);
			});
		}

		internal static void NotifySubWorldOpened(string structureLabel, Rectangle area, Point anchorLocal)
		{
			PocketStrataChat.LogSubWorldEvent(
				$"子世界已生成：「{structureLabel}」 {area.Width}×{area.Height} @ ({area.X},{area.Y})，初始点 [{anchorLocal.X},{anchorLocal.Y}]",
				Color.LightGreen);
		}

		private static void WriteSnapshotCells(BinaryWriter writer, Rectangle area, PocketSnapCell[,] snap)
		{
			for (int dx = 0; dx < area.Width; dx++)
			{
				for (int dy = 0; dy < area.Height; dy++)
					PocketSnapCell.WriteV2(writer, snap[dx, dy]);
			}
		}

		private static PocketSnapCell[,] ReadSnapshotCellsFromReader(BinaryReader reader, Rectangle area)
		{
			int w = area.Width;
			int h = area.Height;
			if (w <= 0 || h <= 0)
				return null;

			var cells = new PocketSnapCell[w, h];
			for (int dx = 0; dx < w; dx++)
			{
				for (int dy = 0; dy < h; dy++)
					cells[dx, dy] = PocketSnapCell.ReadV2(reader);
			}

			return cells;
		}

		private static void ReadSnapshotCellsInto(BinaryReader reader, Rectangle area, bool assignToRealBackup)
		{
			PocketSnapCell[,] cells = ReadSnapshotCellsFromReader(reader, area);
			if (assignToRealBackup)
				_realBackup = cells;
		}

		public static void ReceiveOpenSessionNamedRequest(Rectangle tileArea, BinaryReader reader, int triggerPlayerIndex)
		{
			if (tileArea.Width <= 0 || tileArea.Height <= 0)
				return;

			PocketSnapCell[,] cells = ReadSnapshotCellsFromReader(reader, tileArea);
			if (cells == null)
				return;

			PocketStructureData.ComputeSpawnAnchor(Main.player[triggerPlayerIndex], tileArea, out int ax, out int ay);
			ServerOpenSessionWithPocketTiles(tileArea, cells, triggerPlayerIndex, new Point(ax, ay));
		}

		// 用口袋快照开启会话（F9 / 分块上传完成后，服务端权威）
		public static void ServerOpenSessionWithPocketTiles(
			Rectangle tileArea,
			PocketSnapCell[,] pocketSource,
			int triggerPlayerIndex,
			Point? spawnAnchorLocal = null,
			string structureLabel = null)
		{
			if (tileArea.Width <= 0 || tileArea.Height <= 0 || pocketSource == null)
				return;
			if (pocketSource.GetLength(0) != tileArea.Width || pocketSource.GetLength(1) != tileArea.Height)
			{
				PocketStrataChat.LogSubWorldEvent(
					$"子世界数据尺寸不匹配：结构 {pocketSource.GetLength(0)}×{pocketSource.GetLength(1)}，区域 {tileArea.Width}×{tileArea.Height}",
					Color.OrangeRed);
				return;
			}

			_sessionSpawnAnchorLocal = spawnAnchorLocal ?? new Point(tileArea.Width / 2, tileArea.Height / 2);
			_realBackup = CaptureTileArray(tileArea);
			_pocketTiles = PocketStructureFile.CloneCells(pocketSource);
			SetSessionState(true, tileArea);
			_currentSessionLabel = string.IsNullOrEmpty(structureLabel) ? "结构" : structureLabel;
			_sessionInsiderIndex = triggerPlayerIndex;

			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				string label = structureLabel ?? "结构";
				ApplyTileArray(_pocketTiles, tileArea);
				NotifySubWorldOpened(label, tileArea, _sessionSpawnAnchorLocal);
				FinishOpenSessionNetworking(triggerPlayerIndex, tileArea, sendTileSquare: false);
				return;
			}

			if (Main.netMode == NetmodeID.Server)
			{
				BroadcastSessionPacket();
				PocketWorldChunkNetwork.ServerBeginSnapshotBroadcast(-1);

				if (PocketWorldTileChunks.CountChunks(tileArea) > 1)
					BeginServerStagedPocketApply(triggerPlayerIndex);
				else
					ApplyTileArray(_pocketTiles, tileArea);

				FinishOpenSessionNetworking(triggerPlayerIndex, tileArea, sendTileSquare: false);
				ScheduleEnterSubWorldAfterServerApply(triggerPlayerIndex);
				return;
			}

			ApplyTileArray(_pocketTiles, tileArea);
			FinishOpenSessionNetworking(triggerPlayerIndex, tileArea, sendTileSquare: false);
		}

		// 等待服务端分块写盘与快照广播完成后，向进入者 SendTileSquare
		private static void ScheduleEnterSubWorldAfterServerApply(int triggerPlayerIndex)
		{
			_pendingEnterTriggerPlayer = triggerPlayerIndex;
			_serverPocketApplyComplete = _serverChunkApplyQueue == null;
			_snapshotBroadcastComplete = false;
			TryFlushPendingEnterSubWorld();
		}

		internal static void OnSnapshotBroadcastComplete()
		{
			if (Main.netMode != NetmodeID.Server)
				return;

			_snapshotBroadcastComplete = true;
			TryFlushPendingEnterSubWorld();
		}

		private static void TryFlushPendingEnterSubWorld()
		{
			if (_pendingEnterTriggerPlayer < 0)
				return;
			if (!_serverPocketApplyComplete || !_snapshotBroadcastComplete)
				return;

			FlushPendingEnterSubWorld();
		}

		// 服务端口袋落盘完成：向所有进入者推送 TileSquare
		private static void FlushPendingEnterSubWorld()
		{
			if (_pendingEnterTriggerPlayer < 0)
				return;

			_pendingEnterTriggerPlayer = -1;
			_serverPocketApplyComplete = false;
			_snapshotBroadcastComplete = false;

			if (!HasValidSessionArea || _pocketTiles == null)
				return;

			for (int i = 0; i < Main.maxPlayers; i++)
			{
				Player pl = Main.player[i];
				if (pl == null || !pl.active)
					continue;
				if (!pl.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior)
					continue;
				ServerTrySendPocketTileSquareToPlayer(i);
			}
		}

		// 大区域：分帧将口袋快照写入服务端 Main.tile
		private static void BeginServerStagedPocketApply(int triggerPlayerIndex)
		{
			_serverChunkApplyQueue = new Queue<Rectangle>(
				PocketWorldTileChunks.EnumerateFromSpawnAnchor(
					_sessionTileArea,
					_sessionSpawnAnchorLocal.X,
					_sessionSpawnAnchorLocal.Y));

			_serverChunkApplyCooldown = 0;
			ApplyNextServerChunk(triggerPlayerIndex);
		}

		private static void ServerTickStagedChunkApply()
		{
			if (Main.netMode != NetmodeID.Server || _serverChunkApplyQueue == null)
				return;

			if (_serverChunkApplyQueue.Count == 0)
			{
				_serverChunkApplyQueue = null;
				_serverPocketApplyComplete = true;
				TryFlushPendingEnterSubWorld();
				return;
			}

			if (_serverChunkApplyCooldown > 0)
			{
				_serverChunkApplyCooldown--;
				return;
			}

			ApplyNextServerChunk(_sessionInsiderIndex);
			_serverChunkApplyCooldown = ServerChunkApplyIntervalTicks;
		}

		private static void ApplyNextServerChunk(int triggerPlayerIndex)
		{
			if (_serverChunkApplyQueue == null || _serverChunkApplyQueue.Count == 0 || !HasValidSessionArea || _pocketTiles == null)
				return;

			Rectangle chunk = _serverChunkApplyQueue.Dequeue();
			PocketSnapCell[,] cells = PocketWorldTileChunks.Extract(_pocketTiles, _sessionTileArea, chunk);
			ApplyTileArray(cells, chunk);
		}

		private static void CancelServerStagedApply()
		{
			_serverChunkApplyQueue = null;
			_serverChunkApplyCooldown = 0;
			_pendingEnterTriggerPlayer = -1;
			_serverPocketApplyComplete = false;
			_snapshotBroadcastComplete = false;
		}

		// 调试：以当前区域即时抓取为口袋（/pocketview session on）
		public static void ServerOpenSession(Rectangle tileArea, int triggerPlayerIndex)
		{
			if (tileArea.Width <= 0 || tileArea.Height <= 0)
				return;

			PocketStructureData.ComputeSpawnAnchor(Main.player[triggerPlayerIndex], tileArea, out int ax, out int ay);
			_sessionSpawnAnchorLocal = new Point(ax, ay);
			_realBackup = CaptureTileArray(tileArea);

			if (_pocketTiles == null
				|| _pocketTiles.GetLength(0) != tileArea.Width
				|| _pocketTiles.GetLength(1) != tileArea.Height)
			{
				_pocketTiles = CaptureTileArray(tileArea);
			}

			ApplyTileArray(_pocketTiles, tileArea);
			SetSessionState(true, tileArea);
			_sessionInsiderIndex = triggerPlayerIndex;
			FinishOpenSessionNetworking(triggerPlayerIndex, tileArea);
		}

		// 广播会话状态、设置进入者，并可选向触发者 SendTileSquare
		private static void FinishOpenSessionNetworking(int triggerPlayerIndex, Rectangle tileArea, bool sendTileSquare = true)
		{
			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				Main.LocalPlayer.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior = true;
				RecordPlayerEnteredSubWorld(Main.myPlayer);
				return;
			}

			if (sendTileSquare && triggerPlayerIndex >= 0 && triggerPlayerIndex < Main.maxPlayers)
				NetMessage.SendTileSquare(triggerPlayerIndex, tileArea.X, tileArea.Y, tileArea.Width, tileArea.Height);

			BroadcastSessionPacket();
			// 真实备份由 SyncSnapshotChunk 分块下发，不在此重复发包

			for (int i = 0; i < Main.maxPlayers; i++)
			{
				Player p = Main.player[i];
				if (p == null || !p.active)
					continue;
				p.GetModPlayer<PocketWorldViewPlayer>().ServerSetSeesPocketInterior(i == triggerPlayerIndex);
			}
		}

		// 立即还原会话区 Main.tile 为真实备份并清理状态（存档退出、全员离线等）
		public static void ServerRestoreSessionWithoutAnimation(PocketSessionCloseReason reason = PocketSessionCloseReason.AutoRestore)
		{
			if (!_sessionActive || !HasValidSessionArea || _realBackup == null)
				return;

			Rectangle closed = _sessionTileArea;
			PocketSnapCell[,] realSnap = _realBackup;
			ApplyTileArray(realSnap, closed);
			ClearSessionStateAfterClose();

			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				Main.LocalPlayer.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior = false;
				return;
			}

			NotifyMultiplayerSessionClosed(closed, (byte)PocketCloseAnimPolicy.None);
		}

		// 关闭会话（仅服务端/单机）；多人客户端应发 RequestClosePocketSession
		public static void ServerCloseSession(PocketSessionCloseReason reason = PocketSessionCloseReason.Manual)
		{
			if (!_sessionActive || !HasValidSessionArea)
				return;

			_pocketTiles = CaptureTileArray(_sessionTileArea);
			Rectangle closed = _sessionTileArea;
			PocketSnapCell[,] realSnap = _realBackup;

			PocketCloseAnimPolicy closePolicy = reason == PocketSessionCloseReason.BossDeath
				? PocketCloseAnimPolicy.AllInsiders
				: PocketCloseAnimPolicy.LocalRequesterOnly;

			if (realSnap != null)
				ApplyTileArray(realSnap, closed);

			ClearSessionStateAfterClose();

			if (Main.netMode == NetmodeID.SinglePlayer)
			{
				Main.LocalPlayer.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior = false;
				return;
			}

			NotifyMultiplayerSessionClosed(closed, (byte)closePolicy, sendTileSquare: realSnap != null);
		}

		private static void ClearSessionStateAfterClose()
		{
			SetSessionState(false, Rectangle.Empty);
			_realBackup = null;
			_pocketTiles = null;
			_sessionInsiderIndex = -1;
			_currentSessionLabel = "结构";
			ClearSubWorldPlayerTracking();
		}

		private static void NotifyMultiplayerSessionClosed(Rectangle closed, byte closePolicy, bool sendTileSquare = true)
		{
			if (sendTileSquare)
				NetMessage.SendTileSquare(-1, closed.X, closed.Y, closed.Width, closed.Height);

			BroadcastSessionPacket(closePolicy);
			SetAllPlayersPocketInterior(false);
		}

		private static void SetAllPlayersPocketInterior(bool seesInterior)
		{
			for (int i = 0; i < Main.maxPlayers; i++)
			{
				Player p = Main.player[i];
				if (p == null || !p.active)
					continue;
				p.GetModPlayer<PocketWorldViewPlayer>().ServerSetSeesPocketInterior(seesInterior);
			}
		}

		private static void BroadcastSessionPacket(byte closePolicyWhenInactive = 0)
		{
			if (Main.netMode == NetmodeID.SinglePlayer)
				return;

			ModPacket p = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
			p.Write((byte)global::PocketStrata.PocketStrata.PacketType.SyncPocketVisualSession);
			p.Write(_sessionActive);
			p.Write(closePolicyWhenInactive);
			p.Write((short)_sessionTileArea.X);
			p.Write((short)_sessionTileArea.Y);
			p.Write((short)_sessionTileArea.Width);
			p.Write((short)_sessionTileArea.Height);
			p.Write((short)_sessionSpawnAnchorLocal.X);
			p.Write((short)_sessionSpawnAnchorLocal.Y);
			p.Write(_sessionActive ? (_currentSessionLabel ?? "结构") : string.Empty);
			p.Send();
		}

		// 向进入者下发口袋 chunk、真实备份 chunk，并在服务端已落盘后 SendTileSquare
		internal static void ServerPushInsiderPocketData(int whoAmI)
		{
			if (whoAmI < 0 || Main.netMode != NetmodeID.Server)
				return;
			if (!SessionActive || !HasValidSessionArea)
				return;

			RecordPlayerEnteredSubWorld(whoAmI);
			PocketWorldChunkNetwork.ServerSendPocketTilesToPlayer(whoAmI);
			PocketWorldChunkNetwork.ServerSendSnapshotChunksToPlayer(whoAmI);
			ServerTrySendPocketTileSquareToPlayer(whoAmI);
		}
	}
}
