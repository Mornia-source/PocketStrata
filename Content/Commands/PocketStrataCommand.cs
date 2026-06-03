using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using PocketStrata;
using PocketStrata.Content.IO;
using PocketStrata.Content.Players;
using PocketStrata.Content.Systems;

namespace PocketStrata.Content.Commands
{
	// 聊天命令 /ps：保存、列出、选择与应用 .pstrata 结构
	public sealed class PocketStrataCommand : ModCommand
	{
		private const string UsageText =
			"/ps save <名称> | /ps list | /ps select <名称> | /ps apply <名称> | /ps clear | /ps folder | /ps messages";

		public override string Command => "ps";

		public override CommandType Type => CommandType.Chat;

		public override string Usage => UsageText;

		public override string Description => "口袋地层：保存/列出/选择建筑结构 (.pstrata)，F9 应用已选结构。";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (args.Length == 0)
			{
				ReplyUsage(caller);
				return;
			}

			string sub = args[0].ToLowerInvariant();
			switch (sub)
			{
				case "save":
					CmdSave(caller, args);
					break;
				case "list":
					CmdList(caller);
					break;
				case "select":
					CmdSelect(caller, args);
					break;
				case "apply":
					CmdApply(caller, args);
					break;
				case "clear":
					CmdClear(caller);
					break;
				case "folder":
					CmdFolder(caller);
					break;
				case "messages":
				case "msg":
					PocketStrataChat.ToggleMessages(caller);
					break;
				default:
					ReplyUsage(caller);
					break;
			}
		}

		private static void ReplyUsage(CommandCaller caller)
		{
			PocketStrataChat.Reply(caller, UsageText);
			PocketStrataChat.NewText("[PocketStrata] " + UsageText, Color.Gray);
		}

		private static void CmdSave(CommandCaller caller, string[] args)
		{
			if (args.Length < 2)
			{
				PocketStrataChat.Reply(caller, "用法: /ps save <名称>");
				return;
			}

			Player player = caller.Player;
			var mp = player.GetModPlayer<TerrainRecorderPlayer>();
			if (mp.SelectedTiles.Count == 0)
			{
				PocketStrataChat.Reply(caller, "当前没有选区。手持地形采集记录器左键拖拽框选。");
				return;
			}

			Rectangle bounds = mp.GetSelectionBounds();
			if (bounds.Width <= 0 || bounds.Height <= 0)
			{
				PocketStrataChat.Reply(caller, "选区无效。");
				return;
			}

			string name = string.Join("_", args.Skip(1));
			PocketStructureData data = PocketStructureFile.CaptureFromWorld(bounds, mp.SelectedTiles, player);
			if (data == null)
			{
				PocketStrataChat.Reply(caller, "捕获失败。");
				return;
			}

			if (!PocketStructureLibrary.TrySave(name, data, out string error))
			{
				PocketStrataChat.Reply(caller, "保存失败: " + error);
				return;
			}

			mp.SelectedStructureForF9 = PocketStructureLibrary.SanitizeFileName(name);
			string path = PocketStructureLibrary.GetFilePath(name);
			PocketStrataChat.ReplyLines(caller,
				$"已保存结构 \"{name}\"",
				$"  尺寸: {bounds.Width}×{bounds.Height}，选区 {mp.SelectedTiles.Count} 格",
				$"  初始点: [{data.SpawnAnchorX},{data.SpawnAnchorY}]",
				$"  文件: {path}",
				$"  F9 已选: {mp.SelectedStructureForF9}");
		}

		private static void CmdList(CommandCaller caller)
		{
			var ids = PocketStructureLibrary.ListStructureIds();
			if (ids.Count == 0)
			{
				PocketStrataChat.Reply(caller, $"目录为空: {PocketStructureLibrary.StructuresDirectory}");
				return;
			}

			var mp = caller.Player.GetModPlayer<TerrainRecorderPlayer>();
			var sb = new StringBuilder();
			sb.AppendLine($"共 {ids.Count} 个结构 (.pstrata):");
			for (int i = 0; i < ids.Count; i++)
				sb.AppendLine($"  [{i + 1}] {ids[i]}");

			if (!string.IsNullOrEmpty(mp.SelectedStructureForF9))
				sb.AppendLine($"当前 F9 已选: {mp.SelectedStructureForF9}");

			sb.AppendLine($"消息输出: {(PocketStrataChat.SuppressAllMessages ? "关闭" : "开启")}（/ps messages 切换）");

			PocketStrataChat.Reply(caller, sb.ToString().TrimEnd());
		}

		private static void CmdSelect(CommandCaller caller, string[] args)
		{
			if (args.Length < 2)
			{
				PocketStrataChat.Reply(caller, "用法: /ps select <名称>");
				return;
			}

			string name = string.Join("_", args.Skip(1));
			string id = PocketStructureLibrary.SanitizeFileName(name);
			if (!PocketStructureLibrary.TryLoad(id, out PocketStructureData data, out string error))
			{
				PocketStrataChat.Reply(caller, error);
				return;
			}

			caller.Player.GetModPlayer<TerrainRecorderPlayer>().SelectedStructureForF9 = id;
			PocketStrataChat.ReplyLines(caller,
				$"F9 将应用结构 \"{id}\"",
				$"  尺寸: {data.Width}×{data.Height}，初始点 [{data.SpawnAnchorX},{data.SpawnAnchorY}]",
				$"  也可: /ps apply {id}");
		}

		private static void CmdApply(CommandCaller caller, string[] args)
		{
			if (args.Length < 2)
			{
				PocketStrataChat.Reply(caller, "用法: /ps apply <名称>");
				return;
			}

			string name = string.Join("_", args.Skip(1));
			string id = PocketStructureLibrary.SanitizeFileName(name);
			caller.Player.GetModPlayer<TerrainRecorderPlayer>().SelectedStructureForF9 = id;
			PocketWorldVisualMaskSystem.TryApplySelectedStructure(caller.Player);
			PocketStrataChat.Reply(caller, $"正在应用结构「{id}」（与 F9 相同）。");
		}

		private static void CmdClear(CommandCaller caller)
		{
			caller.Player.GetModPlayer<TerrainRecorderPlayer>().ClearSelection();
			PocketStrataChat.Reply(caller, "选区已清空。");
		}

		private static void CmdFolder(CommandCaller caller)
		{
			string dir = PocketStructureLibrary.StructuresDirectory;
			PocketStrataChat.Reply(caller, "结构目录: " + dir);
			try
			{
				System.Diagnostics.Process.Start("explorer.exe", dir);
			}
			catch
			{
			}
		}
	}
}
