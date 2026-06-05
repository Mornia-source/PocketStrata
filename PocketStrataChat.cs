using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace PocketStrata
{
	// 模组消息出口；SuppressAllMessages 为 true 时不显示屏幕提示
	internal static class PocketStrataChat
	{
		// 暂时默认静默；/ps messages 可重新开启屏幕提示
		internal static bool SuppressAllMessages = true;

		public static void ToggleMessages(CommandCaller caller)
		{
			SuppressAllMessages = !SuppressAllMessages;
			string state = SuppressAllMessages ? "已关闭（仅保留 /ps 指令回复）" : "已开启";
			caller?.Reply($"[PocketStrata] 消息输出：{state}");
		}

		public static void NewText(string text, Color color)
		{
			if (SuppressAllMessages || string.IsNullOrEmpty(text))
				return;
			Main.NewText(text, color);
		}

		public static void NewText(string text) => NewText(text, Color.White);

		// 指令回复始终写入聊天，不受静默开关影响
		public static void Reply(CommandCaller caller, string text)
		{
			if (string.IsNullOrEmpty(text))
				return;
			caller?.Reply(text);
		}

		public static void ReplyLines(CommandCaller caller, params string[] lines)
		{
			if (caller == null || lines == null)
				return;
			for (int i = 0; i < lines.Length; i++)
			{
				if (!string.IsNullOrEmpty(lines[i]))
					caller.Reply(lines[i]);
			}
		}

		public static void LogSubWorldEvent(string summary, Color? color = null)
		{
			NewText("[PocketStrata] " + summary, color ?? new Color(120, 220, 255));
		}

		// 手持记录器鼠标旁提示；静默时清空
		public static void SetCursorItemIconText(Player player, string text)
		{
			if (player == null)
				return;
			player.cursorItemIconText = SuppressAllMessages ? string.Empty : text;
		}
	}
}
