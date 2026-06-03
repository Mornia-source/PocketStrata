using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata.Content.Players;

namespace PocketStrata.Content.Systems
{
	// 服务端 SendSection：旁观者看到真实地形，进入者看到口袋 Main.tile
	public sealed class PocketWorldTileSyncGuard : ModSystem
	{
		public override void Load()
		{
			On_NetMessage.SendSection += NetMessage_SendSection;
		}

		public override void Unload()
		{
			On_NetMessage.SendSection -= NetMessage_SendSection;
		}

		private static void NetMessage_SendSection(On_NetMessage.orig_SendSection orig, int whoAmi, int sectionX, int sectionY)
		{
			if (Main.netMode != NetmodeID.Server && !Main.dedServ)
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			if (!PocketWorldVisualMaskSystem.SessionActive
				|| !PocketWorldVisualMaskSystem.HasValidSessionArea)
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			Player receiver = whoAmi >= 0 && whoAmi < Main.maxPlayers ? Main.player[whoAmi] : null;
			bool isInsider = receiver != null && receiver.active
				&& receiver.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior;

			if (isInsider)
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			Rectangle area = PocketWorldVisualMaskSystem.SessionTileArea;
			PocketSnapCell[,] backup = PocketWorldVisualMaskSystem.Snap;
			PocketSnapCell[,] pocket = PocketWorldVisualMaskSystem.PocketTiles;

			const int SectionWidthTiles = 200;
			const int SectionHeightTiles = 150;
			var sectionRect = new Rectangle(
				sectionX * SectionWidthTiles,
				sectionY * SectionHeightTiles,
				SectionWidthTiles,
				SectionHeightTiles);

			if (backup == null || pocket == null || !sectionRect.Intersects(area))
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			PocketWorldVisualMaskSystem.ApplyTileArray(backup, area);
			try
			{
				orig(whoAmi, sectionX, sectionY);
			}
			finally
			{
				PocketWorldVisualMaskSystem.ApplyTileArray(pocket, area);
			}
		}
	}
}
