using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata.Content.Players;

namespace PocketStrata.Content.Systems
{
	// 地形采集记录器：粒子绘制选区边界（含拖拽预览）
	public sealed class TerrainSelectionVisualSystem : ModSystem
	{
		private const int DustPerEdgeTile = 1;

		public override void PostUpdateEverything()
		{
			if (Main.dedServ || Main.gameMenu)
				return;

			Player pl = Main.LocalPlayer;
			if (pl == null || !pl.active)
				return;

			var mp = pl.GetModPlayer<TerrainRecorderPlayer>();
			if (!mp.HoldingRecorder && mp.SelectedTiles.Count == 0)
				return;

			if (mp.IsDragging)
			{
				Rectangle preview = TerrainRecorderPlayer.GetDragPreviewRect(mp);
				if (preview.Width > 0)
					SpawnEdgeParticles(preview, new Color(255, 220, 80));
			}

			if (mp.SelectedTiles.Count > 0)
				SpawnSelectionOutline(mp.SelectedTiles, new Color(80, 180, 255));
		}

		private static void SpawnSelectionOutline(HashSet<Point> tiles, Color color)
		{
			foreach (Point p in tiles)
			{
				if (!IsBorderTile(tiles, p))
					continue;
				SpawnCornerDust(p, color);
			}
		}

		private static bool IsBorderTile(HashSet<Point> tiles, Point p) =>
			!tiles.Contains(new Point(p.X - 1, p.Y))
			|| !tiles.Contains(new Point(p.X + 1, p.Y))
			|| !tiles.Contains(new Point(p.X, p.Y - 1))
			|| !tiles.Contains(new Point(p.X, p.Y + 1));

		private static void SpawnEdgeParticles(Rectangle rect, Color color)
		{
			for (int x = rect.X; x < rect.X + rect.Width; x++)
			{
				SpawnCornerDust(new Point(x, rect.Y), color);
				SpawnCornerDust(new Point(x, rect.Y + rect.Height - 1), color);
			}

			for (int y = rect.Y + 1; y < rect.Y + rect.Height - 1; y++)
			{
				SpawnCornerDust(new Point(rect.X, y), color);
				SpawnCornerDust(new Point(rect.X + rect.Width - 1, y), color);
			}
		}

		private static void SpawnCornerDust(Point tile, Color color)
		{
			if (tile.X < 0 || tile.Y < 0 || tile.X >= Main.maxTilesX || tile.Y >= Main.maxTilesY)
				return;

			Vector2 pos = new Vector2(tile.X * 16f + 8f, tile.Y * 16f + 8f);
			for (int i = 0; i < DustPerEdgeTile; i++)
			{
				int d = Dust.NewDust(pos, 4, 4, DustID.BlueTorch, 0f, 0f, 100, color, 1.1f);
				if (d >= 0 && d < Main.maxDust)
				{
					Dust dust = Main.dust[d];
					dust.noGravity = true;
					dust.velocity *= 0.15f;
					dust.fadeIn = 0.5f;
				}
			}
		}
	}
}
