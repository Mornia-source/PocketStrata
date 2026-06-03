using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace PocketStrata.Content.Systems
{
	// 大区域分块（64×64）；枚举顺序：锚点块优先，再向左右对称展开
	internal static class PocketWorldTileChunks
	{
		public const int ChunkWidth = 64;
		public const int ChunkHeight = 64;

		public static int CountChunks(Rectangle area)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return 0;

			int cols = (area.Width + ChunkWidth - 1) / ChunkWidth;
			int rows = (area.Height + ChunkHeight - 1) / ChunkHeight;
			return cols * rows;
		}

		// 枚举分块：锚点块 → 水平对称外扩 → 同列上下补齐
		public static IEnumerable<Rectangle> EnumerateFromSpawnAnchor(Rectangle fullArea, int anchorLocalX, int anchorLocalY)
		{
			if (fullArea.Width <= 0 || fullArea.Height <= 0)
				yield break;

			anchorLocalX = System.Math.Clamp(anchorLocalX, 0, fullArea.Width - 1);
			anchorLocalY = System.Math.Clamp(anchorLocalY, 0, fullArea.Height - 1);

			int cols = (fullArea.Width + ChunkWidth - 1) / ChunkWidth;
			int rows = (fullArea.Height + ChunkHeight - 1) / ChunkHeight;
			int anchorCol = anchorLocalX / ChunkWidth;
			int anchorRow = anchorLocalY / ChunkHeight;

			var yielded = new HashSet<long>();

			if (TryGetChunkRect(fullArea, cols, rows, anchorCol, anchorRow, yielded, out Rectangle anchorChunk))
				yield return anchorChunk;

			int maxHoriz = System.Math.Max(anchorCol, cols - 1 - anchorCol);
			for (int h = 1; h <= maxHoriz; h++)
			{
				for (int r = 0; r < rows; r++)
				{
					if (TryGetChunkRect(fullArea, cols, rows, anchorCol - h, r, yielded, out Rectangle left))
						yield return left;
					if (TryGetChunkRect(fullArea, cols, rows, anchorCol + h, r, yielded, out Rectangle right))
						yield return right;
				}
			}

			int maxVert = System.Math.Max(anchorRow, rows - 1 - anchorRow);
			for (int v = 1; v <= maxVert; v++)
			{
				if (TryGetChunkRect(fullArea, cols, rows, anchorCol, anchorRow - v, yielded, out Rectangle up))
					yield return up;
				if (TryGetChunkRect(fullArea, cols, rows, anchorCol, anchorRow + v, yielded, out Rectangle down))
					yield return down;
			}
		}

		private static bool TryGetChunkRect(
			Rectangle fullArea,
			int cols,
			int rows,
			int col,
			int row,
			HashSet<long> yielded,
			out Rectangle chunk)
		{
			chunk = Rectangle.Empty;
			if (col < 0 || col >= cols || row < 0 || row >= rows)
				return false;

			long key = ((long)col << 32) | (uint)row;
			if (!yielded.Add(key))
				return false;

			int ox = col * ChunkWidth;
			int oy = row * ChunkHeight;
			int w = System.Math.Min(ChunkWidth, fullArea.Width - ox);
			int h = System.Math.Min(ChunkHeight, fullArea.Height - oy);
			chunk = new Rectangle(fullArea.X + ox, fullArea.Y + oy, w, h);
			return true;
		}

		public static PocketSnapCell[,] Extract(PocketSnapCell[,] source, Rectangle fullArea, Rectangle chunk)
		{
			var result = new PocketSnapCell[chunk.Width, chunk.Height];
			if (source == null)
				return result;

			for (int dx = 0; dx < chunk.Width; dx++)
			{
				for (int dy = 0; dy < chunk.Height; dy++)
				{
					int sx = chunk.X - fullArea.X + dx;
					int sy = chunk.Y - fullArea.Y + dy;
					if (sx >= 0 && sy >= 0 && sx < source.GetLength(0) && sy < source.GetLength(1))
						result[dx, dy] = source[sx, sy];
				}
			}

			return result;
		}
	}
}
