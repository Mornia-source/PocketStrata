using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace PocketStrata.Content.Systems
{
	// 客户端：子世界出现/消失 — 方块飞入落位 / 从结构中分离飞出，烟雾固定在结构占地地面
	public sealed class PocketWorldTileTransitionSystem : ModSystem
	{
		private const float RowStaggerSeconds = 0.13f;
		private const float ExitAnimSeconds = 0.42f;
		private const float EnterLeadSeconds = 0.14f;
		private const float EnterAnimSeconds = 0.68f;
		private const float ExitTravelPixels = 58f;
		private const float EnterStartBelowPixels = 76f;
		private const float EnterHorizontalSpreadPixels = 36f;
		private const float MaxRadialDelaySeconds = 0.22f;

		private sealed class FlyPair
		{
			public int TileX;
			public int TileY;
			public int LocalDy;
			public PocketSnapCell FromCell;
			public PocketSnapCell ToCell;
			public float Delay;
			public float Hash;
			public bool Started;
			public bool Finished;
			public bool HiddenTile;
			public bool HiddenWall;
			public bool Committed;
		}

		private readonly List<FlyPair> _pairs = new List<FlyPair>();
		private readonly HashSet<Point> _hiddenTiles = new HashSet<Point>();

		private Rectangle _area;
		private float _elapsed;
		private Action _onComplete;
		private bool _active;
		private bool _opening;
		private Vector2 _structureCenterWorld;
		private int _contentMinRow = -1;
		private int _contentMaxRow = -1;
		private float _structureGroundWorldY;
		private int _lastRiseDustFrame = -1;

		public static float CameraShake { get; private set; }

		public static bool IsTransitionActive => Instance?._active == true;

		public static bool IsTileHidden(int tileX, int tileY) =>
			Instance != null && Instance._hiddenTiles.Contains(new Point(tileX, tileY));

		private static PocketWorldTileTransitionSystem Instance;

		public override void Load() => Instance = this;

		public override void Unload()
		{
			ClearTransition();
			Instance = null;
		}

		public override void ModifyScreenPosition()
		{
			if (CameraShake <= 0.05f)
				return;

			Main.screenPosition += new Vector2(
				Main.rand.NextFloat(-CameraShake, CameraShake),
				Main.rand.NextFloat(-CameraShake, CameraShake));
		}

		public override void PostUpdateEverything()
		{
			if (CameraShake > 0.05f)
				CameraShake *= _active ? 0.9f : 0.75f;

			if (!_active || Main.dedServ)
				return;

			_elapsed += 1f / 60f;
			UpdatePairs();

			if (IsSmokeEffectsActive())
				SpawnStructureFrontierSmoke();

			if (!HasPendingWork())
				FinishTransition();
		}

		// PostDrawTiles：SpriteBatch 乘色光照，与 DrawSingleTile 一致
		public override void PostDrawTiles()
		{
			if (!_active || Main.dedServ || Main.gameMenu || _pairs.Count == 0)
				return;

			Main.spriteBatch.Begin(
				SpriteSortMode.Immediate,
				BlendState.AlphaBlend,
				SamplerState.PointClamp,
				DepthStencilState.None,
				Main.Rasterizer,
				null,
				Main.GameViewMatrix.TransformationMatrix);

			try
			{
				DrawFlyTiles(Main.spriteBatch);
			}
			finally
			{
				Main.spriteBatch.End();
			}
		}

		public static bool TryBeginTransition(
			Rectangle area,
			PocketSnapCell[,] fromCells,
			PocketSnapCell[,] toCells,
			Action onComplete,
			bool opening = true)
		{
			if (!CanPlayClientTransition())
				return false;

			if (area.Width <= 0 || fromCells == null || toCells == null)
				return false;

			if (fromCells.GetLength(0) != area.Width || fromCells.GetLength(1) != area.Height)
				return false;

			if (toCells.GetLength(0) != area.Width || toCells.GetLength(1) != area.Height)
				return false;

			Instance ??= ModContent.GetInstance<PocketWorldTileTransitionSystem>();
			if (Instance._active)
				return false;

			Player pl = Main.LocalPlayer;
			if (pl == null || !pl.active)
				return false;

			Instance._area = area;
			Instance._onComplete = onComplete;
			Instance._opening = opening;
			Instance._elapsed = 0f;
			Instance._lastRiseDustFrame = -1;
			Instance._pairs.Clear();
			Instance._hiddenTiles.Clear();
			CameraShake = 0f;

			PocketSnapCell[,] structureCells = opening ? toCells : fromCells;
			ComputeStructureRowBounds(structureCells, area, out Instance._contentMinRow, out Instance._contentMaxRow);
			Instance._structureGroundWorldY = Instance.ComputeStructureGroundWorldY();
			Instance._structureCenterWorld = new Vector2(
				(area.X + area.Width * 0.5f) * 16f,
				(area.Y + (Instance._contentMinRow + Instance._contentMaxRow + 1f) * 0.5f) * 16f);

			for (int dx = 0; dx < area.Width; dx++)
			{
				for (int dy = 0; dy < area.Height; dy++)
				{
					PocketSnapCell from = fromCells[dx, dy];
					PocketSnapCell to = toCells[dx, dy];
					if (!ShouldAnimateCell(from, to))
						continue;

					int tx = area.X + dx;
					int ty = area.Y + dy;
					float hash = Hash01(tx, ty);
					float delay = Instance.ComputeCellDelay(ty, hash);

					Instance._pairs.Add(new FlyPair
					{
						TileX = tx,
						TileY = ty,
						LocalDy = dy,
						FromCell = from,
						ToCell = to,
						Delay = delay,
						Hash = hash,
					});
				}
			}

			if (Instance._pairs.Count == 0)
				return false;

			Instance._active = true;
			CameraShake = 1.5f;
			return true;
		}

		private static void ComputeStructureRowBounds(
			PocketSnapCell[,] cells,
			Rectangle area,
			out int minRow,
			out int maxRow)
		{
			minRow = area.Height;
			maxRow = -1;

			for (int dx = 0; dx < area.Width; dx++)
			{
				for (int dy = 0; dy < area.Height; dy++)
				{
					if (!cells[dx, dy].HasPlacedContent)
						continue;

					if (dy < minRow)
						minRow = dy;

					if (dy > maxRow)
						maxRow = dy;
				}
			}

			if (maxRow < 0)
			{
				minRow = 0;
				maxRow = area.Height - 1;
			}
		}

		private float ComputeCellDelay(int ty, float hash)
		{
			int localDy = ty - _area.Y;
			float radial = Vector2.Distance(
				new Vector2((_area.X + _area.Width * 0.5f) * 16f, ty * 16f),
				_structureCenterWorld) * 0.00035f * MaxRadialDelaySeconds;

			// localDy 小=结构上端，大=结构下端（地面侧）
			if (_opening)
			{
				// 出现：从下往上，底行先动
				float rowFromStructureBottom = _contentMaxRow - localDy;
				float rowDelay = Math.Max(0f, rowFromStructureBottom) * RowStaggerSeconds;
				return rowDelay + radial + hash * 0.04f;
			}

			// 消失：从上往下，顶行先动
			float rowFromStructureTop = localDy - _contentMinRow;
			float sinkDelay = Math.Max(0f, rowFromStructureTop) * RowStaggerSeconds;
			return sinkDelay + radial + hash * 0.04f;
		}

		private static bool CanPlayClientTransition()
		{
			if (Main.dedServ)
				return false;

			return Main.netMode == NetmodeID.SinglePlayer
				|| Main.netMode == NetmodeID.MultiplayerClient;
		}

		private static bool ShouldAnimateCell(PocketSnapCell from, PocketSnapCell to)
		{
			if (!from.HasPlacedContent && !to.HasPlacedContent)
				return false;

			return from.HasPlacedContent || to.HasPlacedContent;
		}

		// 结构占地底边地面（内容最底行的下沿），全程固定
		private float ComputeStructureGroundWorldY()
		{
			return (_area.Y + _contentMaxRow + 1) * 16f - 2f;
		}

		private bool IsSmokeEffectsActive()
		{
			if (!_active)
				return false;

			for (int i = 0; i < _pairs.Count; i++)
			{
				if (!_pairs[i].Finished)
					return true;
			}

			return _elapsed < GetPairTimelineEnd() + 0.35f;
		}

		private float GetPairTimelineEnd()
		{
			float end = 0f;
			for (int i = 0; i < _pairs.Count; i++)
				end = Math.Max(end, _pairs[i].Delay + EnterLeadSeconds + EnterAnimSeconds);

			return end;
		}

		private void SpawnStructureFrontierSmoke()
		{
			if (_area.Width <= 0)
				return;

			int frame = (int)(_elapsed * 60f);
			if (frame == _lastRiseDustFrame)
				return;

			_lastRiseDustFrame = frame;

			float groundY = _structureGroundWorldY;
			float left = _area.X * 16f;
			float width = _area.Width * 16f;

			for (int k = 0; k < 12; k++)
			{
				Vector2 pos = new Vector2(
					left + Main.rand.NextFloat(0f, width),
					groundY + Main.rand.NextFloat(-3f, 2f));

				int d = Dust.NewDust(
					pos,
					10,
					3,
					DustID.Smoke,
					Main.rand.NextFloat(-0.4f, 0.4f),
					Main.rand.NextFloat(-0.08f, 0.08f),
					110,
					new Color(190, 170, 140),
					Main.rand.NextFloat(1.1f, 1.9f));

				if (d >= Main.maxDust)
					continue;

				Main.dust[d].noGravity = true;
				Main.dust[d].velocity *= 0.2f;
			}

			if (frame % 14 == 0 && HasPendingWork())
				CameraShake = Math.Min(CameraShake + 1.2f, 7f);
		}

		private void DrawFlyTiles(SpriteBatch sb)
		{
			foreach (FlyPair pair in _pairs)
			{
				if (!pair.Started || pair.Finished || pair.Committed)
					continue;

				float localTime = _elapsed - pair.Delay;
				if (localTime <= 0f)
					continue;

				Vector2 targetCenter = new Vector2(pair.TileX * 16f + 8f, pair.TileY * 16f + 8f);
				float exitProgress = MathHelper.Clamp(localTime / ExitAnimSeconds, 0f, 1f);
				float enterProgress = MathHelper.Clamp((localTime - EnterLeadSeconds) / EnterAnimSeconds, 0f, 1f);

				DrawExitPair(sb, pair, targetCenter, exitProgress);
				DrawEnterPair(sb, pair, targetCenter, enterProgress);
			}
		}

		private void UpdatePairs()
		{
			for (int i = 0; i < _pairs.Count; i++)
			{
				FlyPair pair = _pairs[i];
				if (pair.Finished)
					continue;

				if (!pair.Started && _elapsed >= pair.Delay)
					BeginPair(pair);

				if (!pair.Started)
					continue;

				float localTime = _elapsed - pair.Delay;
				float enterProgress = MathHelper.Clamp(
					(localTime - EnterLeadSeconds) / EnterAnimSeconds,
					0f,
					1f);

				if (!pair.Committed)
				{
					bool shouldCommit = pair.ToCell.HasPlacedContent
						? enterProgress >= PocketFlyingTileDrawHelper.FadeOutStart
						: localTime >= ExitAnimSeconds;

					if (shouldCommit)
						CommitPair(pair);
				}

				if (localTime >= EnterLeadSeconds + EnterAnimSeconds)
					FinishPair(pair);
			}
		}

		private void FinishPair(FlyPair pair)
		{
			if (pair.Finished)
				return;

			CommitPair(pair);
			pair.Finished = true;
		}

		private void CommitPair(FlyPair pair)
		{
			if (pair.Committed)
				return;

			RestorePairVisual(pair);
			PocketWorldVisualMaskSystem.ApplySnapCell(pair.TileX, pair.TileY, pair.ToCell);
			pair.Committed = true;
		}

		private void BeginPair(FlyPair pair)
		{
			pair.Started = true;
			_hiddenTiles.Add(new Point(pair.TileX, pair.TileY));

			if (pair.TileX < 0 || pair.TileY < 0
				|| pair.TileX >= Main.maxTilesX || pair.TileY >= Main.maxTilesY)
				return;

			Tile t = Main.tile[pair.TileX, pair.TileY];
			if (t.HasTile && !t.IsTileInvisible)
			{
				t.IsTileInvisible = true;
				pair.HiddenTile = true;
			}

			if (t.WallType > WallID.None && !t.IsWallInvisible)
			{
				t.IsWallInvisible = true;
				pair.HiddenWall = true;
			}
		}

		private void RestorePairVisual(FlyPair pair)
		{
			if (pair.TileX < 0 || pair.TileY < 0
				|| pair.TileX >= Main.maxTilesX || pair.TileY >= Main.maxTilesY)
				return;

			Tile t = Main.tile[pair.TileX, pair.TileY];
			if (pair.HiddenTile)
				t.IsTileInvisible = false;

			if (pair.HiddenWall)
				t.IsWallInvisible = false;

			pair.HiddenTile = false;
			pair.HiddenWall = false;
		}

		private bool HasPendingWork()
		{
			for (int i = 0; i < _pairs.Count; i++)
			{
				if (!_pairs[i].Finished)
					return true;
			}

			return false;
		}

		private void FinishTransition()
		{
			for (int i = 0; i < _pairs.Count; i++)
			{
				FlyPair pair = _pairs[i];
				if (!pair.Committed)
					CommitPair(pair);
			}

			Action complete = _onComplete;
			ClearTransition();
			complete?.Invoke();
		}

		private void ClearTransition()
		{
			for (int i = 0; i < _pairs.Count; i++)
			{
				FlyPair pair = _pairs[i];
				if (!pair.Committed)
					RestorePairVisual(pair);
			}

			_active = false;
			_pairs.Clear();
			_hiddenTiles.Clear();
			_onComplete = null;
			_area = Rectangle.Empty;
			_elapsed = 0f;
			_lastRiseDustFrame = -1;
			_contentMinRow = -1;
			_contentMaxRow = -1;
			_structureGroundWorldY = 0f;
			CameraShake = 0f;
		}

		private void DrawExitPair(SpriteBatch sb, FlyPair pair, Vector2 targetCenter, float progress)
		{
			if (!pair.FromCell.HasPlacedContent || progress <= 0f)
				return;

			Vector2 worldPos = ComputeExitWorldPos(pair, targetCenter, progress);
			float eased = EaseIn(progress);
			float scale = MathHelper.Lerp(1f, 0.55f, eased);
			float alpha = MathHelper.Lerp(1f, 0f, EaseOut(MathHelper.Clamp(progress * 1.15f, 0f, 1f)));
			float rotation = (pair.Hash - 0.5f) * 0.35f + eased * (pair.Hash - 0.5f) * 1.4f;

			PocketFlyingTileDrawHelper.DrawCell(
				sb,
				pair.FromCell,
				worldPos,
				scale,
				rotation,
				alpha,
				pair.TileX,
				pair.TileY,
				enterProgress: 0f);
		}

		private void DrawEnterPair(SpriteBatch sb, FlyPair pair, Vector2 targetCenter, float progress)
		{
			if (pair.Committed || !pair.ToCell.HasPlacedContent || progress <= 0f)
				return;

			Vector2 worldPos = ComputeEnterWorldPos(pair, targetCenter, progress);
			float eased = EaseOut(progress);
			float scale = MathHelper.Lerp(0.72f, 1f, eased);
			float alpha = MathHelper.Lerp(0f, 1f, EaseIn(MathHelper.Clamp(progress * 1.08f, 0f, 1f)));
			float rotation = (pair.Hash - 0.5f) * 0.55f * (1f - eased);

			PocketFlyingTileDrawHelper.DrawCell(
				sb,
				pair.ToCell,
				worldPos,
				scale,
				rotation,
				alpha,
				pair.TileX,
				pair.TileY,
				enterProgress: progress);
		}

		private Vector2 ComputeExitWorldPos(FlyPair pair, Vector2 targetCenter, float progress)
		{
			Vector2 away = targetCenter - _structureCenterWorld;
			if (away.LengthSquared() < 4f)
				away = new Vector2((pair.Hash - 0.5f) * 1.6f, -1f);

			away.Normalize();
			float travel = ExitTravelPixels * (0.75f + pair.Hash * 0.55f);
			Vector2 lift = new Vector2(0f, -18f - pair.Hash * 22f);
			Vector2 wobble = new Vector2((pair.Hash - 0.5f) * 10f * progress, 0f);
			Vector2 offset = away * travel * EaseIn(progress) + lift * EaseIn(progress) + wobble;
			return targetCenter + offset;
		}

		private Vector2 ComputeEnterWorldPos(FlyPair pair, Vector2 targetCenter, float progress)
		{
			float eased = EaseOut(progress);
			float below = EnterStartBelowPixels + pair.Hash * 28f;
			float side = (pair.Hash - 0.5f) * EnterHorizontalSpreadPixels * (1f - eased * 0.65f);
			Vector2 start = targetCenter + new Vector2(side, below);
			return Vector2.Lerp(start, targetCenter, eased);
		}

		private static float Hash01(int x, int y)
		{
			uint h = (uint)(x * 73856093 ^ y * 19349663);
			h ^= h >> 16;
			h *= 2246822507u;
			return (h & 0xFFFF) / 65535f;
		}

		private static float EaseIn(float t) => t * t;

		private static float EaseOut(float t)
		{
			float inv = 1f - t;
			return 1f - inv * inv;
		}
	}
}
