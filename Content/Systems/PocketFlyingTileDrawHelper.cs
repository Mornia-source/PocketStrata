using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.ID;

namespace PocketStrata.Content.Systems
{
	// 绘制过渡动画方块/墙：飞行位置光照采样 + 落点插值 + 落盘前 alpha 缓冲
	internal static class PocketFlyingTileDrawHelper
	{
		internal const float FadeOutStart = 0.88f;

		public static void DrawCell(
			SpriteBatch spriteBatch,
			PocketSnapCell cell,
			Vector2 worldCenter,
			float scale,
			float rotation,
			float alpha,
			int tileX,
			int tileY,
			float enterProgress = 0f)
		{
			if (alpha <= 0.01f || scale <= 0.01f)
				return;

			float commitAlpha = alpha;
			if (enterProgress > FadeOutStart)
			{
				float t = (enterProgress - FadeOutStart) / (1f - FadeOutStart);
				commitAlpha *= MathHelper.Clamp(1f - t * t, 0f, 1f);
			}

			if (commitAlpha <= 0.01f)
				return;

			Vector2 screenCenter = worldCenter - Main.screenPosition;
			byte alphaByte = (byte)(255f * Math.Clamp(commitAlpha, 0f, 1f));
			Vector2 landCenter = new Vector2(tileX * 16f + 8f, tileY * 16f + 8f);

			if (cell.HasWall && cell.WallType > WallID.None)
			{
				Color wallFlyLight = SampleWallLight(cell, worldCenter);
				Color wallLandLight = SampleWallLight(cell, landCenter);
				Color wallLight = LerpColor(wallFlyLight, wallLandLight, enterProgress);
				wallLight.A = alphaByte;
				DrawWall(spriteBatch, cell, screenCenter, scale, rotation, wallLight);
			}

			if (!cell.HasTile)
				return;

			Tile scratch = default;
			cell.WriteToTile(ref scratch);
			Texture2D tileTex = Main.instance.TilesRenderer.GetTileDrawTexture(scratch, tileX, tileY);
			if (tileTex == null)
				tileTex = TextureAssets.Tile[cell.TileType].Value;

			Color flyLight = SampleTileLight(cell, scratch, tileX, tileY, worldCenter);
			Color landLight = SampleTileLight(cell, scratch, tileX, tileY, landCenter);
			Color tileLight = LerpColor(flyLight, landLight, enterProgress);
			tileLight.A = alphaByte;

			Rectangle tileFrame = new Rectangle(cell.FrameX, cell.FrameY, 16, 16);
			Vector2 origin = tileFrame.Size() * 0.5f;
			spriteBatch.Draw(
				tileTex,
				screenCenter,
				tileFrame,
				tileLight,
				rotation,
				origin,
				scale,
				SpriteEffects.None,
				0f);
		}

		private static void DrawWall(
			SpriteBatch spriteBatch,
			PocketSnapCell cell,
			Vector2 screenCenter,
			float scale,
			float rotation,
			Color wallLight)
		{
			Texture2D wallTex = ResolveWallTexture(cell);
			int wallFrameY = cell.WallFrameY + Main.wallFrame[cell.WallType] * 180;
			Rectangle wallFrame = new Rectangle(cell.WallFrameX, wallFrameY, 32, 32);

			Vector2 wallDrawPos = screenCenter - new Vector2(16f, 16f);
			spriteBatch.Draw(
				wallTex,
				wallDrawPos,
				wallFrame,
				wallLight,
				rotation,
				Vector2.Zero,
				scale,
				SpriteEffects.None,
				0f);
		}

		private static Texture2D ResolveWallTexture(PocketSnapCell cell)
		{
			Texture2D wallTex = TextureAssets.Wall[cell.WallType].Value;
			Texture2D painted = Main.instance.TilePaintSystem.TryGetWallAndRequestIfNotReady(
				cell.WallType,
				cell.WallColor);
			return painted ?? wallTex;
		}

		private static Color SampleTileLight(
			PocketSnapCell cell,
			Tile scratch,
			int tileX,
			int tileY,
			Vector2 worldCenter)
		{
			if (IsTileFullbright(cell, scratch))
				return Color.White;

			int sampleX = Utils.Clamp((int)(worldCenter.X / 16f), 1, Main.maxTilesX - 2);
			int sampleY = Utils.Clamp((int)(worldCenter.Y / 16f), 1, Main.maxTilesY - 2);

			Color light = Lighting.GetColor(sampleX, sampleY);
			return Main.instance.TilesRenderer.DrawTiles_GetLightOverride(
				tileY,
				tileX,
				scratch,
				cell.TileType,
				cell.FrameX,
				cell.FrameY,
				light);
		}

		private static Color SampleWallLight(PocketSnapCell cell, Vector2 worldCenter)
		{
			if (IsWallFullbright(cell))
				return Color.White;

			int sampleX = Utils.Clamp((int)(worldCenter.X / 16f), 1, Main.maxTilesX - 2);
			int sampleY = Utils.Clamp((int)(worldCenter.Y / 16f), 1, Main.maxTilesY - 2);
			return Lighting.GetColor(sampleX, sampleY);
		}

		private static Color LerpColor(Color a, Color b, float t)
		{
			t = MathHelper.Clamp(t, 0f, 1f);
			return new Color(
				(int)(a.R + (b.R - a.R) * t),
				(int)(a.G + (b.G - a.G) * t),
				(int)(a.B + (b.B - a.B) * t));
		}

		private static bool IsTileFullbright(PocketSnapCell cell, Tile scratch)
		{
			return (cell.CoatingFlags & 4) != 0 || scratch.IsTileFullbright;
		}

		private static bool IsWallFullbright(PocketSnapCell cell)
		{
			if ((cell.CoatingFlags & 8) != 0)
				return true;

			return Main.wallLight[cell.WallType];
		}
	}
}
