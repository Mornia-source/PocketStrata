using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.Graphics;
using Terraria.ID;

namespace PocketStrata.Content.Systems
{
	// 绘制过渡动画中的方块/墙精灵（TileBatch 四角光照 + 引擎染色纹理）
	internal static class PocketFlyingTileDrawHelper
	{
		public static void DrawCell(
			SpriteBatch spriteBatch,
			PocketSnapCell cell,
			Vector2 worldCenter,
			float scale,
			float rotation,
			float alpha,
			int tileX,
			int tileY)
		{
			_ = spriteBatch;
			if (alpha <= 0.01f || scale <= 0.01f)
				return;

			Vector2 screenCenter = worldCenter - Main.screenPosition;
			byte alphaByte = (byte)(255f * Math.Clamp(alpha, 0f, 1f));
			int lightX = ResolveLightTileX(worldCenter, tileX);
			int lightY = ResolveLightTileY(worldCenter, tileY);

			if (cell.HasWall && cell.WallType > WallID.None)
				DrawWall(cell, screenCenter, scale, rotation, alphaByte, lightX, lightY);

			if (!cell.HasTile)
				return;

			Tile scratch = default;
			cell.WriteToTile(ref scratch);
			Texture2D tileTex = Main.instance.TilesRenderer.GetTileDrawTexture(scratch, lightX, lightY);
			if (tileTex == null)
				tileTex = TextureAssets.Tile[cell.TileType].Value;

			Rectangle tileFrame = new Rectangle(cell.FrameX, cell.FrameY, 16, 16);
			VertexColors tileLight = SampleTileVertexColors(cell, scratch, lightX, lightY);
			ApplyAlpha(ref tileLight, alphaByte);

			Vector2 origin = tileFrame.Size() * 0.5f;
			float drawW = tileFrame.Width * scale;
			float drawH = tileFrame.Height * scale;
			Vector4 destination = new Vector4(
				screenCenter.X - origin.X * scale,
				screenCenter.Y - origin.Y * scale,
				drawW,
				drawH);

			Main.tileBatch.Draw(
				tileTex,
				destination,
				tileFrame,
				tileLight,
				origin * scale,
				SpriteEffects.None,
				rotation);
		}

		private static void DrawWall(
			PocketSnapCell cell,
			Vector2 screenCenter,
			float scale,
			float rotation,
			byte alphaByte,
			int lightX,
			int lightY)
		{
			Texture2D wallTex = ResolveWallTexture(cell);
			int wallFrameY = cell.WallFrameY + Main.wallFrame[cell.WallType] * 180;
			Rectangle wallFrame = new Rectangle(cell.WallFrameX, wallFrameY, 32, 32);
			VertexColors wallLight = SampleWallVertexColors(cell, lightX, lightY);
			ApplyAlpha(ref wallLight, alphaByte);

			Vector2 wallDrawPos = screenCenter - new Vector2(16f, 16f);
			float drawW = wallFrame.Width * scale;
			float drawH = wallFrame.Height * scale;
			Vector4 destination = new Vector4(wallDrawPos.X, wallDrawPos.Y, drawW, drawH);

			Main.tileBatch.Draw(
				wallTex,
				destination,
				wallFrame,
				wallLight,
				Vector2.Zero,
				SpriteEffects.None,
				rotation);
		}

		private static Texture2D ResolveWallTexture(PocketSnapCell cell)
		{
			Texture2D wallTex = TextureAssets.Wall[cell.WallType].Value;
			Texture2D painted = Main.instance.TilePaintSystem.TryGetWallAndRequestIfNotReady(
				cell.WallType,
				cell.WallColor);
			return painted ?? wallTex;
		}

		private static VertexColors SampleTileVertexColors(PocketSnapCell cell, Tile scratch, int lightX, int lightY)
		{
			if (IsTileFullbright(cell, scratch))
				return new VertexColors(Color.White);

			if (Lighting.NotRetro)
			{
				Lighting.GetCornerColors(lightX, lightY, out VertexColors vertices, 1f);
				Color center = Main.instance.TilesRenderer.DrawTiles_GetLightOverride(
					lightY,
					lightX,
					scratch,
					cell.TileType,
					cell.FrameX,
					cell.FrameY,
					Lighting.GetColor(lightX, lightY));
				BlendVertexColorsToward(ref vertices, center, 0.35f);
				return vertices;
			}

			Color tileLight = Main.instance.TilesRenderer.DrawTiles_GetLightOverride(
				lightY,
				lightX,
				scratch,
				cell.TileType,
				cell.FrameX,
				cell.FrameY,
				Lighting.GetColor(lightX, lightY));
			return new VertexColors(tileLight);
		}

		private static VertexColors SampleWallVertexColors(PocketSnapCell cell, int lightX, int lightY)
		{
			if (IsWallFullbright(cell))
				return new VertexColors(Color.White);

			if (Lighting.NotRetro)
			{
				Lighting.GetCornerColors(lightX, lightY, out VertexColors vertices, 1f);
				return vertices;
			}

			return new VertexColors(Lighting.GetColor(lightX, lightY));
		}

		private static bool IsTileFullbright(PocketSnapCell cell, Tile scratch)
		{
			return (cell.CoatingFlags & 4) != 0 || scratch.IsTileFullbright;
		}

		private static bool IsWallFullbright(PocketSnapCell cell)
		{
			if ((cell.CoatingFlags & 8) != 0)
				return true;

			return cell.WallType == WallID.None || Main.wallLight[cell.WallType];
		}

		private static int ResolveLightTileX(Vector2 worldCenter, int fallbackX)
		{
			int lx = (int)(worldCenter.X / 16f);
			if (lx < 0 || lx >= Main.maxTilesX)
				lx = fallbackX;

			return Math.Clamp(lx, 0, Main.maxTilesX - 1);
		}

		private static int ResolveLightTileY(Vector2 worldCenter, int fallbackY)
		{
			int ly = (int)(worldCenter.Y / 16f);
			if (ly < 0 || ly >= Main.maxTilesY)
				ly = fallbackY;

			return Math.Clamp(ly, 0, Main.maxTilesY - 1);
		}

		private static void BlendVertexColorsToward(ref VertexColors vertices, Color target, float weight)
		{
			vertices.TopLeftColor = Color.Lerp(vertices.TopLeftColor, target, weight);
			vertices.TopRightColor = Color.Lerp(vertices.TopRightColor, target, weight);
			vertices.BottomLeftColor = Color.Lerp(vertices.BottomLeftColor, target, weight);
			vertices.BottomRightColor = Color.Lerp(vertices.BottomRightColor, target, weight);
		}

		private static void ApplyAlpha(ref VertexColors vertices, byte alphaByte)
		{
			vertices.TopLeftColor.A = alphaByte;
			vertices.TopRightColor.A = alphaByte;
			vertices.BottomLeftColor.A = alphaByte;
			vertices.BottomRightColor.A = alphaByte;
		}
	}
}
