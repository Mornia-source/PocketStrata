using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ModLoader;

namespace PocketStrata.Content.IO
{
	// .pstrata 文件的磁盘读写与列表
	public static class PocketStructureLibrary
	{
		// .pstrata 目录：…/ModData/PocketStrata/Structures/
		public static string StructuresDirectory
		{
			get
			{
				string path = Path.Combine(Main.SavePath, "ModData", "PocketStrata", "Structures");
				Directory.CreateDirectory(path);
				return path;
			}
		}

		public static string SanitizeFileName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return "structure";
			char[] invalid = Path.GetInvalidFileNameChars();
			var chars = name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
			string s = new string(chars);
			return string.IsNullOrEmpty(s) ? "structure" : s;
		}

		public static string GetFilePath(string name) =>
			Path.Combine(StructuresDirectory, SanitizeFileName(name) + PocketStructureData.Extension);

		public static IReadOnlyList<string> ListStructureIds()
		{
			if (!Directory.Exists(StructuresDirectory))
				return Array.Empty<string>();

			return Directory
				.GetFiles(StructuresDirectory, "*" + PocketStructureData.Extension)
				.Select(Path.GetFileNameWithoutExtension)
				.Where(n => !string.IsNullOrEmpty(n))
				.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		public static bool TrySave(string name, PocketStructureData data, out string error)
		{
			error = null;
			try
			{
				data.DisplayName = name;
				PocketStructureFile.Write(GetFilePath(name), data);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}

		public static bool TryLoad(string name, out PocketStructureData data, out string error)
		{
			data = null;
			error = null;
			string path = GetFilePath(name);
			if (!File.Exists(path))
			{
				error = $"找不到结构 \"{name}\"。";
				return false;
			}

			try
			{
				data = PocketStructureFile.Read(path);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}
	}
}
