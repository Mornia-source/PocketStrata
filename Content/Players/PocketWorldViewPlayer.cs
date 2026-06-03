using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata.Content.Systems;

namespace PocketStrata.Content.Players
{
	// 进入者视角（SeesPocketInterior）：可改口袋区方块；旁观者客户端仍显示真实地形
	public class PocketWorldViewPlayer : ModPlayer
	{
		public bool SeesPocketInterior;

		public override void OnEnterWorld()
		{
			if (Player.whoAmI == Main.myPlayer)
				PocketWorldVisualMaskSystem.NotifyEnteredWorldFromLocalPlayer();

			if (Main.netMode == NetmodeID.Server
				&& PocketWorldVisualMaskSystem.SessionActive
				&& SeesPocketInterior)
			{
				PocketWorldVisualMaskSystem.ServerPushInsiderPocketData(Player.whoAmI);
			}
		}

		public override bool CanUseItem(Item item)
		{
			if (SeesPocketInterior)
				return true;
			if (!PocketWorldVisualMaskSystem.SessionActive || !PocketWorldVisualMaskSystem.HasValidSessionArea)
				return true;

			int mouseX = (int)(Main.MouseWorld.X / 16f);
			int mouseY = (int)(Main.MouseWorld.Y / 16f);
			Rectangle area = PocketWorldVisualMaskSystem.SessionTileArea;
			if (!area.Contains(new Point(mouseX, mouseY)))
				return true;

			if (item.pick > 0 || item.axe > 0 || item.hammer > 0)
				return false;
			if (item.createTile >= 0 || item.createWall >= 0)
				return false;

			return true;
		}

		public override void CopyClientState(ModPlayer targetCopy)
		{
			var other = (PocketWorldViewPlayer)targetCopy;
			other.SeesPocketInterior = SeesPocketInterior;
		}

		public override void SendClientChanges(ModPlayer clientPlayer)
		{
			var old = (PocketWorldViewPlayer)clientPlayer;
			if (old.SeesPocketInterior != SeesPocketInterior)
				RequestSync();
		}

		public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
		{
			ModPacket packet = Mod.GetPacket();
			packet.Write((byte)global::PocketStrata.PocketStrata.PacketType.SyncPocketWorldViewPlayer);
			packet.Write((byte)Player.whoAmI);
			packet.Write(SeesPocketInterior);
			packet.Send(toWho, fromWho);
		}

		private void RequestSync()
		{
			if (Main.netMode == NetmodeID.SinglePlayer)
				return;
			SyncPlayer(-1, -1, false);
		}

		// 同步 SeesPocketInterior；进入则 ClientApply，退出则还原真实地形
		public void ReceiveSync(BinaryReader reader)
		{
			bool wasInsider = SeesPocketInterior;
			SeesPocketInterior = reader.ReadBoolean();

			if (Main.netMode == NetmodeID.Server)
			{
				if (SeesPocketInterior && !wasInsider)
					PocketWorldVisualMaskSystem.ServerPushInsiderPocketData(Player.whoAmI);
				return;
			}

			if (Player != Main.LocalPlayer)
				return;

			if (SeesPocketInterior)
			{
				PocketWorldVisualMaskSystem.ClientApplyPocketInterior();
				if (wasInsider)
					return;
			}
			else if (!wasInsider)
			{
				return;
			}

			if (!PocketWorldVisualMaskSystem.SessionActive
				|| !PocketWorldVisualMaskSystem.HasValidSessionArea)
				return;

			PocketWorldVisualMaskSystem.ClientRestoreInsiderExit(
				PocketWorldVisualMaskSystem.SessionTileArea);
		}

		// 服务端设置进入者标记并下发口袋数据，再广播 SyncPocketWorldViewPlayer
		internal void ServerSetSeesPocketInterior(bool value)
		{
			if (SeesPocketInterior == value)
				return;

			SeesPocketInterior = value;

			if (value && Main.netMode == NetmodeID.Server)
				PocketWorldVisualMaskSystem.ServerPushInsiderPocketData(Player.whoAmI);

			if (Main.netMode != NetmodeID.SinglePlayer)
				SyncPlayer(-1, Player.whoAmI, false);
		}
	}
}
