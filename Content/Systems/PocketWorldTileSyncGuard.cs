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
			if (!ShouldGuardServerSend())
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			Player receiver = GetPlayer(whoAmi);
			if (IsInsider(receiver))
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			const int SectionWidthTiles = 200;
			const int SectionHeightTiles = 150;
			var sectionRect = new Rectangle(
				sectionX * SectionWidthTiles,
				sectionY * SectionHeightTiles,
				SectionWidthTiles,
				SectionHeightTiles);

			if (!TryGetSessionOverlap(sectionRect, out Rectangle overlap))
			{
				orig(whoAmi, sectionX, sectionY);
				return;
			}

			SendWithBackupSwap(whoAmi, overlap, () => orig(whoAmi, sectionX, sectionY));
		}

		private static void SendWithBackupSwap(int whoAmi, Rectangle overlap, System.Action send)
		{
			PocketSnapCell[,] backup = PocketWorldVisualMaskSystem.Snap;
			PocketSnapCell[,] pocket = PocketWorldVisualMaskSystem.PocketTiles;
			if (backup == null || pocket == null)
			{
				send();
				return;
			}

			PocketWorldVisualMaskSystem.ApplyTileArray(backup, overlap, refreshFrames: false);
			try
			{
				send();
			}
			finally
			{
				PocketWorldVisualMaskSystem.ApplyTileArray(pocket, overlap, refreshFrames: false);
			}
		}

		private static bool ShouldGuardServerSend()
		{
			if (Main.netMode != NetmodeID.Server && !Main.dedServ)
				return false;

			return PocketWorldVisualMaskSystem.SessionActive
				&& PocketWorldVisualMaskSystem.HasValidSessionArea;
		}

		private static bool TryGetSessionOverlap(Rectangle query, out Rectangle overlap)
		{
			overlap = Rectangle.Empty;
			if (!PocketWorldVisualMaskSystem.HasValidSessionArea)
				return false;

			Rectangle area = PocketWorldVisualMaskSystem.SessionTileArea;
			if (!query.Intersects(area))
				return false;

			overlap = Rectangle.Intersect(query, area);
			return overlap.Width > 0 && overlap.Height > 0;
		}

		private static Player GetPlayer(int whoAmi) =>
			whoAmi >= 0 && whoAmi < Main.maxPlayers ? Main.player[whoAmi] : null;

		private static bool IsInsider(Player receiver) =>
			receiver != null && receiver.active
			&& receiver.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior;
	}
}
