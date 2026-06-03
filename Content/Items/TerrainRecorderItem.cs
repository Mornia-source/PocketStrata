using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata;
using PocketStrata.Content.Players;

namespace PocketStrata.Content.Items
{
	// 地形采集记录器：框选并集/差集，配合 /ps save 导出 .pstrata
	public class TerrainRecorderItem : ModItem
	{
		public override string Texture => "Terraria/Images/Item_850";

		public override void SetDefaults()
		{
			Item.width = 20;
			Item.height = 20;
			Item.useStyle = ItemUseStyleID.HoldUp;
			Item.useTime = 10;
			Item.useAnimation = 10;
			Item.holdStyle = ItemHoldStyleID.HoldFront;
			Item.noMelee = true;
			Item.value = 0;
			Item.rare = ItemRarityID.Green;
			Item.autoReuse = false;
		}

		public override void HoldItem(Player player)
		{
			if (player.whoAmI != Main.myPlayer)
				return;
			var mp = player.GetModPlayer<TerrainRecorderPlayer>();
			string mode = mp.CombineMode == TerrainRecorderPlayer.SelectionCombineMode.Union ? "与(增加)" : "非(减少)";
			player.cursorItemIconEnabled = true;
			player.cursorItemIconID = Type;
			if (Main.GameUpdateCount % 30 == 0)
				PocketStrataChat.SetCursorItemIconText(player, $"选区:{mp.SelectedTiles.Count} [{mode}] 左键拖拽 | 右键切换");
		}

		public override bool AltFunctionUse(Player player) => true;

		public override bool? UseItem(Player player)
		{
			if (player.whoAmI != Main.myPlayer)
				return true;

			var mp = player.GetModPlayer<TerrainRecorderPlayer>();
			if (player.altFunctionUse == 2)
			{
				mp.CombineMode = mp.CombineMode == TerrainRecorderPlayer.SelectionCombineMode.Union
					? TerrainRecorderPlayer.SelectionCombineMode.Subtract
					: TerrainRecorderPlayer.SelectionCombineMode.Union;
				string label = mp.CombineMode == TerrainRecorderPlayer.SelectionCombineMode.Union ? "与(增加)" : "非(减少)";
				PocketStrataChat.NewText("[PocketStrata] 选区模式：" + label, new Color(120, 220, 255));
			}

			return true;
		}
	}
}
