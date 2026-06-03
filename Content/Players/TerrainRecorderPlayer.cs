using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata;
using PocketStrata.Content.Items;

namespace PocketStrata.Content.Players
{
	// 地形采集记录器：维护框选格集合、拖拽与 F9 所选结构名
	public class TerrainRecorderPlayer : ModPlayer
	{
		public const int MaxSelectionTiles = 120_000;

		public enum SelectionCombineMode
		{
			Union,
			Subtract,
		}

		public readonly HashSet<Point> SelectedTiles = new HashSet<Point>();
		public SelectionCombineMode CombineMode = SelectionCombineMode.Union;
		public bool IsDragging;
		public Point DragStart;
		public Point DragEnd;
		public string SelectedStructureForF9;

		public bool HoldingRecorder =>
			Player.HeldItem != null
			&& !Player.HeldItem.IsAir
			&& Player.HeldItem.type == ModContent.ItemType<TerrainRecorderItem>();

		public Rectangle GetSelectionBounds()
		{
			if (SelectedTiles.Count == 0)
				return Rectangle.Empty;

			int minX = int.MaxValue;
			int minY = int.MaxValue;
			int maxX = int.MinValue;
			int maxY = int.MinValue;
			foreach (Point p in SelectedTiles)
			{
				if (p.X < minX) minX = p.X;
				if (p.Y < minY) minY = p.Y;
				if (p.X > maxX) maxX = p.X;
				if (p.Y > maxY) maxY = p.Y;
			}

			return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
		}

		public void ClearSelection()
		{
			SelectedTiles.Clear();
			IsDragging = false;
		}

		public void ApplyRectangleToSelection(Rectangle rect)
		{
			if (rect.Width <= 0 || rect.Height <= 0)
				return;

			var buffer = new List<Point>(rect.Width * rect.Height);
			for (int x = rect.X; x < rect.X + rect.Width; x++)
			{
				for (int y = rect.Y; y < rect.Y + rect.Height; y++)
				{
					if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
						continue;
					buffer.Add(new Point(x, y));
				}
			}

			if (CombineMode == SelectionCombineMode.Union)
			{
				foreach (Point p in buffer)
					SelectedTiles.Add(p);
			}
			else
			{
				foreach (Point p in buffer)
					SelectedTiles.Remove(p);
			}

			if (SelectedTiles.Count > MaxSelectionTiles)
			{
				SelectedTiles.Clear();
				PocketStrataChat.NewText("[PocketStrata] 选区过大，已清空（上限 " + MaxSelectionTiles + " 格）。", Color.Orange);
			}
		}

		public override void PostUpdate()
		{
			if (Player.whoAmI != Main.myPlayer || Main.gameMenu)
				return;
			if (!HoldingRecorder)
			{
				IsDragging = false;
				return;
			}

			if (!TryGetMouseTile(out Point mouseTile))
				return;

			bool mouseLeft = Main.mouseLeft && !Main.LocalPlayer.mouseInterface;
			if (mouseLeft && !IsDragging)
			{
				IsDragging = true;
				DragStart = mouseTile;
				DragEnd = mouseTile;
			}

			if (IsDragging)
			{
				DragEnd = mouseTile;
				if (!mouseLeft)
				{
					Rectangle rect = NormalizeRect(DragStart, DragEnd);
					ApplyRectangleToSelection(rect);
					IsDragging = false;
					string mode = CombineMode == SelectionCombineMode.Union ? "与(增加)" : "非(减少)";
					PocketStrataChat.NewText($"[PocketStrata] 选区更新（{mode}），当前 {SelectedTiles.Count} 格。输入 /ps save 名称 保存。", new Color(120, 220, 255));
				}
			}
		}

		private static bool TryGetMouseTile(out Point tile)
		{
			tile = default;
			if (Main.mouseX < 0 || Main.mouseY < 0)
				return false;
			tile = new Point(
				(int)(Main.MouseWorld.X / 16f),
				(int)(Main.MouseWorld.Y / 16f));
			return tile.X >= 0 && tile.Y >= 0 && tile.X < Main.maxTilesX && tile.Y < Main.maxTilesY;
		}

		public static Rectangle NormalizeRect(Point a, Point b)
		{
			int x1 = System.Math.Min(a.X, b.X);
			int y1 = System.Math.Min(a.Y, b.Y);
			int x2 = System.Math.Max(a.X, b.X);
			int y2 = System.Math.Max(a.Y, b.Y);
			return new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
		}

		public static Rectangle GetDragPreviewRect(TerrainRecorderPlayer mp) =>
			mp == null || !mp.IsDragging ? Rectangle.Empty : NormalizeRect(mp.DragStart, mp.DragEnd);
	}
}
