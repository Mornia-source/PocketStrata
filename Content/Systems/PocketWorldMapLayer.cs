using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.Map;
using Terraria.ModLoader;
using PocketStrata.Content.Players;

namespace PocketStrata.Content.Systems
{
	// 全地图层：仅进入者在地图上标出子世界会话矩形
	public sealed class PocketWorldMapLayer : ModMapLayer
	{
		public override void Draw(ref MapOverlayDrawContext ctx, ref string text)
		{
			if (!PocketWorldVisualMaskSystem.SessionActive || !PocketWorldVisualMaskSystem.HasValidSessionArea)
				return;

			Player local = Main.LocalPlayer;
			if (local == null || !local.active)
				return;

			if (!local.GetModPlayer<PocketWorldViewPlayer>().SeesPocketInterior)
				return;

			Rectangle area = PocketWorldVisualMaskSystem.SessionTileArea;
			DrawInsiderMapOverlay(ref ctx, area);
		}

		// 瓦片矩形转地图视口像素矩形并绘制边框
		private static void DrawInsiderMapOverlay(ref MapOverlayDrawContext ctx, Rectangle tileArea)
		{
			Texture2D pixel = TextureAssets.MagicPixel?.Value;
			if (pixel == null)
				return;

			SpriteBatch sb = Main.spriteBatch;
			if (sb == null)
				return;

			Vector2 mapPos = ctx.MapPosition;
			float ms = ctx.MapScale;
			Vector2 off = ctx.MapOffset;

			float x0 = off.X + (tileArea.X - mapPos.X) * ms;
			float y0 = off.Y + (tileArea.Y - mapPos.Y) * ms;
			int rw = (int)Math.Ceiling(tileArea.Width * ms);
			int rh = (int)Math.Ceiling(tileArea.Height * ms);
			if (rw < 1 || rh < 1)
				return;

			var dst = new Rectangle((int)Math.Floor(x0), (int)Math.Floor(y0), rw, rh);

			if (ctx.ClippingRectangle.HasValue)
			{
				dst = Rectangle.Intersect(dst, ctx.ClippingRectangle.Value);
				if (dst.Width <= 0 || dst.Height <= 0)
					return;
			}

			var color = new Color(60, 120, 255, 80);
			try
			{
				sb.Draw(pixel, dst, color);
			}
			catch
			{
			}
		}
	}
}
