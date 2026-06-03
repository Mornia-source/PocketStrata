using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using PocketStrata.Content.Systems;

namespace PocketStrata.Content.IO
{
	// .pstrata 建筑结构二进制格式
	public sealed class PocketStructureData
	{
		public const int FileVersion = 3;
		public const string Magic = "PSTR";
		public const string Extension = ".pstrata";

		public string DisplayName = string.Empty;
		public int Width;
		public int Height;
		public PocketSnapCell[,] Cells = null;

		// 保存时玩家脚相对结构左上角 [0,0] 的偏移（生成锚点）
		public int SpawnAnchorX;
		public int SpawnAnchorY;

		public Rectangle ToTileRectangle(int worldAnchorX, int worldAnchorY) =>
			new Rectangle(worldAnchorX, worldAnchorY, Width, Height);

		// 以当前玩家脚为锚，计算结构放置的世界格矩形
		public Rectangle GetPlacementRectAroundPlayerFeet(Player pl)
		{
			Point feet = GetPlayerFeetTile(pl);
			int ax = System.Math.Clamp(SpawnAnchorX, 0, System.Math.Max(0, Width - 1));
			int ay = System.Math.Clamp(SpawnAnchorY, 0, System.Math.Max(0, Height - 1));
			return new Rectangle(feet.X - ax, feet.Y - ay, Width, Height);
		}

		public static Point GetPlayerFeetTile(Player pl)
		{
			Vector2 feet = pl.Bottom;
			return new Point((int)(feet.X / 16f), (int)(feet.Y / 16f));
		}

		public static void ComputeSpawnAnchor(Player pl, Rectangle bounds, out int anchorX, out int anchorY)
		{
			Point feet = GetPlayerFeetTile(pl);
			anchorX = feet.X - bounds.X;
			anchorY = feet.Y - bounds.Y;
			if (bounds.Width > 0)
				anchorX = System.Math.Clamp(anchorX, 0, bounds.Width - 1);
			if (bounds.Height > 0)
				anchorY = System.Math.Clamp(anchorY, 0, bounds.Height - 1);
		}

		public static (int X, int Y) DefaultSpawnAnchor(int width, int height) =>
			(width / 2, height / 2);
	}

	public static class PocketStructureFile
	{
		public static void Write(string path, PocketStructureData data)
		{
			if (data == null || data.Cells == null || data.Width <= 0 || data.Height <= 0)
				throw new ArgumentException("无效结构数据。");

			string dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			using var fs = File.Create(path);
			using var writer = new BinaryWriter(fs);
			writer.Write(Encoding.ASCII.GetBytes(PocketStructureData.Magic));
			writer.Write(PocketStructureData.FileVersion);
			byte[] nameBytes = Encoding.UTF8.GetBytes(data.DisplayName ?? string.Empty);
			if (nameBytes.Length > ushort.MaxValue)
				Array.Resize(ref nameBytes, ushort.MaxValue);
			writer.Write((ushort)nameBytes.Length);
			writer.Write(nameBytes);
			writer.Write((short)data.Width);
			writer.Write((short)data.Height);
			writer.Write((short)data.SpawnAnchorX);
			writer.Write((short)data.SpawnAnchorY);
			WriteCells(writer, data.Cells, data.Width, data.Height);
		}

		public static PocketStructureData Read(string path)
		{
			using var fs = File.OpenRead(path);
			using var reader = new BinaryReader(fs);
			var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
			if (magic != PocketStructureData.Magic)
				throw new InvalidDataException("不是有效的 .pstrata 文件。");

			int version = reader.ReadInt32();
			if (version < 1 || version > PocketStructureData.FileVersion)
				throw new InvalidDataException($"不支持的 .pstrata 版本: {version}");

			int nameLen = reader.ReadUInt16();
			string name = nameLen > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(nameLen)) : string.Empty;
			int w = reader.ReadInt16();
			int h = reader.ReadInt16();
			if (w <= 0 || h <= 0)
				throw new InvalidDataException("结构尺寸无效。");

			int anchorX;
			int anchorY;
			if (version >= 3)
			{
				anchorX = reader.ReadInt16();
				anchorY = reader.ReadInt16();
				anchorX = System.Math.Clamp(anchorX, 0, w - 1);
				anchorY = System.Math.Clamp(anchorY, 0, h - 1);
			}
			else
			{
				(anchorX, anchorY) = PocketStructureData.DefaultSpawnAnchor(w, h);
			}

			return new PocketStructureData
			{
				DisplayName = name,
				Width = w,
				Height = h,
				SpawnAnchorX = anchorX,
				SpawnAnchorY = anchorY,
				Cells = ReadCells(reader, w, h, version),
			};
		}

		public static PocketStructureData CaptureFromWorld(Rectangle area, HashSet<Point> mask, Player anchorPlayer)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return null;

			int spawnX = area.Width / 2;
			int spawnY = area.Height / 2;
			if (anchorPlayer != null && anchorPlayer.active)
				PocketStructureData.ComputeSpawnAnchor(anchorPlayer, area, out spawnX, out spawnY);

			var cells = new PocketSnapCell[area.Width, area.Height];
			for (int dx = 0; dx < area.Width; dx++)
			{
				for (int dy = 0; dy < area.Height; dy++)
				{
					int tx = area.X + dx;
					int ty = area.Y + dy;
					var p = new Point(tx, ty);
					if (mask != null && !mask.Contains(p))
						continue;

					if (tx < 0 || ty < 0 || tx >= Main.maxTilesX || ty >= Main.maxTilesY)
						continue;

					Tile t = Main.tile[tx, ty];
					cells[dx, dy] = PocketSnapCell.FromTile(t);
				}
			}

			return new PocketStructureData
			{
				DisplayName = string.Empty,
				Width = area.Width,
				Height = area.Height,
				SpawnAnchorX = spawnX,
				SpawnAnchorY = spawnY,
				Cells = cells,
			};
		}

		public static PocketSnapCell[,] CloneCells(PocketSnapCell[,] src)
		{
			if (src == null)
				return null;
			int w = src.GetLength(0);
			int h = src.GetLength(1);
			var copy = new PocketSnapCell[w, h];
			for (int x = 0; x < w; x++)
			{
				for (int y = 0; y < h; y++)
					copy[x, y] = src[x, y];
			}
			return copy;
		}

		private static void WriteCells(BinaryWriter writer, PocketSnapCell[,] snap, int w, int h)
		{
			for (int dx = 0; dx < w; dx++)
			{
				for (int dy = 0; dy < h; dy++)
					PocketSnapCell.WriteV2(writer, snap[dx, dy]);
			}
		}

		private static PocketSnapCell[,] ReadCells(BinaryReader reader, int w, int h, int version)
		{
			var cells = new PocketSnapCell[w, h];
			for (int dx = 0; dx < w; dx++)
			{
				for (int dy = 0; dy < h; dy++)
				{
					cells[dx, dy] = version >= 2
						? PocketSnapCell.ReadV2(reader)
						: PocketSnapCell.ReadV1(reader);
				}
			}
			return cells;
		}
	}
}
