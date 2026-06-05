using System.IO;
using Terraria;
using Terraria.ID;

namespace PocketStrata.Content.Systems
{
	// 单格瓦片快照（相对会话区左上角的 [dx, dy]）
	public struct PocketSnapCell
	{
		public ushort TileType;
		public ushort WallType;
		public short FrameX;
		public short FrameY;
		public bool HasTile;
		public bool HasWall;
		public byte TileColor;
		public byte WallColor;

		// 锤击形状 BlockType（半砖、斜面等）
		public byte BlockType;

		public bool IsActuated;
		public bool HasActuator;
		public byte TileFrameNumber;
		public byte WallFrameNumber;
		public short WallFrameX;
		public short WallFrameY;

		// 电线：bit0 红 bit1 绿 bit2 蓝 bit3 黄
		public byte WireFlags;

		public byte LiquidAmount;
		public byte LiquidType;

		// 涂层：bit0 方块隐身 bit1 墙隐身 bit2 方块全亮 bit3 墙全亮
		public byte CoatingFlags;

		// 运行时标记：该格快照有效（抓取或 MarkDenseContentForApply）
		public bool IsCaptured;

		public bool HasPlacedContent => HasTile || HasWall || LiquidAmount > 0;

		public const int V1CellByteSize = 11;
		public const int V2CellByteSize = 23;

		// 将数组中含方块/墙/液体的格标记为 IsCaptured
		public static void MarkDenseContentForApply(PocketSnapCell[,] cells)
		{
			if (cells == null)
				return;

			int w = cells.GetLength(0);
			int h = cells.GetLength(1);
			for (int x = 0; x < w; x++)
			{
				for (int y = 0; y < h; y++)
				{
					if (cells[x, y].HasPlacedContent)
						cells[x, y].IsCaptured = true;
				}
			}
		}

		public static PocketSnapCell FromTile(Tile t)
		{
			var cell = new PocketSnapCell
			{
				TileType = t.HasTile ? t.TileType : (ushort)0,
				WallType = t.WallType,
				FrameX = t.TileFrameX,
				FrameY = t.TileFrameY,
				HasTile = t.HasTile,
				HasWall = t.WallType > WallID.None,
				TileColor = t.TileColor,
				WallColor = t.WallColor,
				BlockType = (byte)t.BlockType,
				IsActuated = t.IsActuated,
				HasActuator = t.HasActuator,
				TileFrameNumber = (byte)t.TileFrameNumber,
				WallFrameNumber = (byte)t.WallFrameNumber,
				WallFrameX = (short)t.WallFrameX,
				WallFrameY = (short)t.WallFrameY,
				LiquidAmount = t.LiquidAmount,
				LiquidType = (byte)t.LiquidType,
			};

			byte wire = 0;
			if (t.RedWire) wire |= 1;
			if (t.GreenWire) wire |= 2;
			if (t.BlueWire) wire |= 4;
			if (t.YellowWire) wire |= 8;
			cell.WireFlags = wire;

			byte coat = 0;
			if (t.IsTileInvisible) coat |= 1;
			if (t.IsWallInvisible) coat |= 2;
			if (t.IsTileFullbright) coat |= 4;
			if (t.IsWallFullbright) coat |= 8;
			cell.CoatingFlags = coat;
			cell.IsCaptured = true;

			return cell;
		}

		public void ApplyToTile(int tx, int ty)
		{
			if (tx < 0 || ty < 0 || tx >= Main.maxTilesX || ty >= Main.maxTilesY)
				return;

			Tile t = Main.tile[tx, ty];
			WriteToTile(ref t);
		}

		// 写入 Tile 引用（绘制飞行精灵时用临时 Tile 取纹理）
		internal void WriteToTile(ref Tile t)
		{
			t.ClearEverything();

			if (HasWall)
			{
				t.WallType = WallType;
				t.WallColor = WallColor;
				t.WallFrameNumber = WallFrameNumber;
				t.WallFrameX = WallFrameX;
				t.WallFrameY = WallFrameY;
				if ((CoatingFlags & 2) != 0) t.IsWallInvisible = true;
				if ((CoatingFlags & 8) != 0) t.IsWallFullbright = true;
			}

			if (LiquidAmount > 0)
			{
				t.LiquidAmount = LiquidAmount;
				t.LiquidType = LiquidType;
			}

			byte wire = WireFlags;
			t.RedWire = (wire & 1) != 0;
			t.GreenWire = (wire & 2) != 0;
			t.BlueWire = (wire & 4) != 0;
			t.YellowWire = (wire & 8) != 0;

			if (!HasTile)
				return;

			t.HasTile = true;
			t.TileType = TileType;
			t.TileFrameX = FrameX;
			t.TileFrameY = FrameY;
			t.TileColor = TileColor;
			t.TileFrameNumber = TileFrameNumber;
			t.BlockType = (BlockType)BlockType;
			t.IsActuated = IsActuated;
			t.HasActuator = HasActuator;
			if ((CoatingFlags & 1) != 0) t.IsTileInvisible = true;
			if ((CoatingFlags & 4) != 0) t.IsTileFullbright = true;
		}

		public static void WriteV1(BinaryWriter writer, PocketSnapCell c)
		{
			writer.Write(c.TileType);
			writer.Write(c.WallType);
			writer.Write(c.FrameX);
			writer.Write(c.FrameY);
			byte flags = (byte)((c.HasTile ? 1 : 0) | (c.HasWall ? 2 : 0));
			writer.Write(flags);
			writer.Write(c.TileColor);
			writer.Write(c.WallColor);
		}

		public static void WriteV2(BinaryWriter writer, PocketSnapCell c)
		{
			WriteV1(writer, c);
			writer.Write(c.BlockType);
			byte act = (byte)((c.IsActuated ? 1 : 0) | (c.HasActuator ? 2 : 0));
			writer.Write(act);
			writer.Write(c.TileFrameNumber);
			writer.Write(c.WallFrameNumber);
			writer.Write(c.WallFrameX);
			writer.Write(c.WallFrameY);
			writer.Write(c.WireFlags);
			writer.Write(c.LiquidAmount);
			writer.Write(c.LiquidType);
			writer.Write(c.CoatingFlags);
		}

		public static PocketSnapCell ReadV1(BinaryReader reader)
		{
			ushort tt = reader.ReadUInt16();
			ushort wt = reader.ReadUInt16();
			short fx = reader.ReadInt16();
			short fy = reader.ReadInt16();
			byte flags = reader.ReadByte();
			byte tCol = reader.ReadByte();
			byte wCol = reader.ReadByte();
			return new PocketSnapCell
			{
				TileType = tt,
				WallType = wt,
				FrameX = fx,
				FrameY = fy,
				HasTile = (flags & 1) != 0,
				HasWall = (flags & 2) != 0,
				TileColor = tCol,
				WallColor = wCol,
				BlockType = (byte)Terraria.ID.BlockType.Solid,
			};
		}

		public static PocketSnapCell ReadV2(BinaryReader reader)
		{
			PocketSnapCell c = ReadV1(reader);
			c.BlockType = reader.ReadByte();
			byte act = reader.ReadByte();
			c.IsActuated = (act & 1) != 0;
			c.HasActuator = (act & 2) != 0;
			c.TileFrameNumber = reader.ReadByte();
			c.WallFrameNumber = reader.ReadByte();
			c.WallFrameX = reader.ReadInt16();
			c.WallFrameY = reader.ReadInt16();
			c.WireFlags = reader.ReadByte();
			c.LiquidAmount = reader.ReadByte();
			c.LiquidType = reader.ReadByte();
			c.CoatingFlags = reader.ReadByte();
			c.IsCaptured = true;
			return c;
		}
	}
}
