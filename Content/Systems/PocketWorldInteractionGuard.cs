using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata.Content.Players;

namespace PocketStrata.Content.Systems
{
	// 服务端：丢弃非进入者对会话区内瓦片操作的网络包
	public sealed class PocketWorldInteractionGuard : ModSystem
	{
		public override bool HijackGetData(ref byte messageType, ref BinaryReader reader, int playerNumber)
		{
			if (Main.netMode != NetmodeID.Server)
				return false;
			if (!PocketWorldVisualMaskSystem.SessionActive || !PocketWorldVisualMaskSystem.HasValidSessionArea)
				return false;
			if (playerNumber < 0 || playerNumber >= Main.maxPlayers)
				return false;

			Player sender = Main.player[playerNumber];
			if (sender == null || !sender.active)
				return false;
			if (sender.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior)
				return false;

			Rectangle zone = PocketWorldVisualMaskSystem.SessionTileArea;
			long pos = reader.BaseStream.Position;
			try
			{
				return messageType switch
				{
					MessageID.TileManipulation => TryPeekTileXYAfterAction(reader, zone),
					MessageID.PlaceObject => TryPeekInt16TileXY(reader, zone),
					MessageID.SyncTilePaintOrCoating => TryPeekInt16TileXY(reader, zone),
					MessageID.SyncWallPaintOrCoating => TryPeekInt16TileXY(reader, zone),
					MessageID.LiquidUpdate => TryPeekInt16TileXY(reader, zone),
					_ => false,
				};
			}
			catch
			{
				return false;
			}
			finally
			{
				reader.BaseStream.Position = pos;
			}
		}

		private static bool TryPeekTileXYAfterAction(BinaryReader reader, Rectangle zone)
		{
			_ = reader.ReadByte();
			int x = reader.ReadInt16();
			int y = reader.ReadInt16();
			return zone.Contains(new Point(x, y));
		}

		private static bool TryPeekInt16TileXY(BinaryReader reader, Rectangle zone)
		{
			int x = reader.ReadInt16();
			int y = reader.ReadInt16();
			return zone.Contains(new Point(x, y));
		}
	}
}
