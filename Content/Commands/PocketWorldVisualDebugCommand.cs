using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using PocketStrata;
using PocketStrata.Content.Players;
using PocketStrata.Content.Systems;

namespace PocketStrata.Content.Commands
{
	// 调试命令 /pocketview：开关会话、切换进入者视角
	public sealed class PocketWorldVisualDebugCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;

		// 命令名与聊天一致，避免全角斜杠或大小写无响应
		public override string Command => "pocketview";

		public override string Usage => "/pocketview session on|off [w] [h] | /pocketview insider on|off";

		public override string Description => "调试 Plan B：会话由服务端改写 Main.tile；多人客户端通过请求包触发；丢包/CanUseItem 拦截。";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (args.Length == 0)
			{
				PocketStrataChat.Reply(caller,"用法: " + Usage);
				PocketStrataChat.NewText("[pocketview] " + Usage, Color.Gray);
				return;
			}

			Player player = caller.Player;
			if (player == null || !player.active)
			{
				PocketStrataChat.Reply(caller,"无效玩家。");
				return;
			}

			string mode = args[0].ToLowerInvariant();
			if (mode == "session")
			{
				if (args.Length < 2)
				{
					PocketStrataChat.Reply(caller,"缺少 on/off。");
					return;
				}

				string sub = args[1].ToLowerInvariant();
				if (sub == "off")
				{
					if (Main.netMode == NetmodeID.MultiplayerClient)
					{
						ModPacket pkt = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
						pkt.Write((byte)global::PocketStrata.PocketStrata.PacketType.RequestClosePocketSession);
						pkt.Send();
						PocketStrataChat.Reply(caller,"已向服务端请求关闭口袋会话。");
						PocketStrataChat.NewText("[pocketview] 已请求关闭会话…", Color.LightGray);
					}
					else
					{
						PocketWorldVisualMaskSystem.ServerCloseSession();
						PocketStrataChat.Reply(caller,"口袋会话已关闭（已尝试还原真实瓦片）。");
						PocketStrataChat.NewText("[pocketview] 会话已关闭。", Color.LightGray);
					}

					return;
				}

				if (sub != "on")
				{
					PocketStrataChat.Reply(caller,"第二参数应为 on 或 off。");
					return;
				}

				int w = 32;
				int h = 24;
				if (args.Length >= 4 && int.TryParse(args[2], out int tw) && int.TryParse(args[3], out int th))
				{
					w = System.Math.Clamp(tw, 4, 400);
					h = System.Math.Clamp(th, 4, 400);
				}

				var area = PocketWorldVisualMaskSystem.GetSessionRectAroundPlayerFeet(player, w, h);
				if (Main.netMode == NetmodeID.MultiplayerClient)
				{
					ModPacket pkt = ModContent.GetInstance<global::PocketStrata.PocketStrata>().GetPacket();
					pkt.Write((byte)global::PocketStrata.PocketStrata.PacketType.RequestOpenPocketSession);
					pkt.Write((short)area.X);
					pkt.Write((short)area.Y);
					pkt.Write((short)area.Width);
					pkt.Write((short)area.Height);
					pkt.Send();
					string reqMsg = $"已向服务端请求开启口袋（{area.Width}x{area.Height}）。进入者应为: {player.name}。切换视角: /pocketview insider on|off";
					PocketStrataChat.Reply(caller,reqMsg);
					PocketStrataChat.NewText("[pocketview] " + reqMsg, Color.LightGreen);
				}
				else
				{
					PocketWorldVisualMaskSystem.ServerOpenSession(area, player.whoAmI);
					string msg = $"口袋会话已开（Plan B），区域格: {area.X},{area.Y} {area.Width}x{area.Height}。进入者: {player.name}。切换视角: /pocketview insider on|off";
					PocketStrataChat.Reply(caller,msg);
					PocketStrataChat.NewText("[pocketview] " + msg, Color.LightGreen);
				}

				return;
			}

			if (mode == "insider")
			{
				if (args.Length < 2)
				{
					PocketStrataChat.Reply(caller,"缺少 on/off。");
					return;
				}

				bool on = args[1].ToLowerInvariant() == "on";
				player.GetModPlayer<PocketWorldViewPlayer>().ServerSetSeesPocketInterior(on);
				string insiderMsg = on ? "进入者（口袋瓦片可交互）" : "旁观者（遮罩 + 不可改口袋区）";
				PocketStrataChat.Reply(caller,"当前玩家：" + insiderMsg + "。");
				PocketStrataChat.NewText("[pocketview] " + insiderMsg, on ? Color.SkyBlue : Color.Lavender);
				return;
			}

			PocketStrataChat.Reply(caller,"未知子命令。用法: " + Usage);
		}
	}
}
