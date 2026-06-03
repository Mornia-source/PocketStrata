using Terraria;
using Terraria.ModLoader;

namespace PocketStrata.Content.Systems
{
	// 批量写入 Main.tile 时抑制方块与墙掉落
	public sealed class PocketWorldTileDropGuardGlobalTile : GlobalTile
	{
		public override bool CanDrop(int i, int j, int type) =>
			!PocketWorldVisualMaskSystem.IsSuppressingTileDrops;

		public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem)
		{
			if (PocketWorldVisualMaskSystem.IsSuppressingTileDrops)
				noItem = true;
		}
	}

	public sealed class PocketWorldTileDropGuardGlobalWall : GlobalWall
	{
		public override bool Drop(int i, int j, int type, ref int dropType) =>
			!PocketWorldVisualMaskSystem.IsSuppressingTileDrops;
	}
}
