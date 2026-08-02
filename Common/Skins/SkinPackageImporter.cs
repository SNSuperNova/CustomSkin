using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace customskin.Common.Skins
{
	public sealed class ValidatedSkinPackage
	{
		public required SkinManifest Manifest { get; init; }
		public required byte[] HeadPng { get; init; }
		public required byte[] BodyPng { get; init; }
		public required byte[] LegsPng { get; init; }
		public required byte[] IconPng { get; init; }
		public byte[]? LicenseText { get; init; }
	}

	public static class SkinPackageImporter
	{
		public const int MaxCompressedPackageBytes = 1024 * 1024;
		public const int MaxExpandedPackageBytes = 2 * 1024 * 1024;
		public const int MaxManifestBytes = 16 * 1024;
		public const int MaxLicenseBytes = 64 * 1024;
		public const int MaxImageBytes = 1024 * 1024;
		public const int MaxFileCount = 8;

		private static readonly Dictionary<string, int> AllowedFiles = new(StringComparer.Ordinal)
		{
			["manifest.json"] = MaxManifestBytes,
			["icon.png"] = MaxImageBytes,
			["textures/head.png"] = MaxImageBytes,
			["textures/body.png"] = MaxImageBytes,
			["textures/legs.png"] = MaxImageBytes,
			["LICENSE.txt"] = MaxLicenseBytes
		};

		public static ValidatedSkinPackage ValidateAndRead(string packagePath)
		{
			FileInfo packageFile = new(packagePath);
			if (!packageFile.Exists)
				throw new SkinPackageException("package.missing", "The selected .cskin file does not exist.");
			if (!string.Equals(packageFile.Extension, ".cskin", StringComparison.OrdinalIgnoreCase))
				throw new SkinPackageException("package.extension", "Skin packages must use the .cskin extension.");
			if (packageFile.Length <= 0 || packageFile.Length > MaxCompressedPackageBytes)
				throw new SkinPackageException("package.size", "The compressed skin package exceeds the 1 MiB limit.");

			try
			{
				using FileStream stream = new(packageFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
				using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
				if (archive.Entries.Count == 0 || archive.Entries.Count > MaxFileCount)
					throw new SkinPackageException("package.files", "The skin package contains an invalid number of files.");

				Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
				int expandedBytes = 0;

				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					ValidateEntryName(entry);
					if (!AllowedFiles.TryGetValue(entry.FullName, out int entryLimit))
						throw new SkinPackageException("package.extra", $"Unexpected file in skin package: {entry.FullName}.");
					if (!files.TryAdd(entry.FullName, Array.Empty<byte>()))
						throw new SkinPackageException("package.duplicate", $"Duplicate file in skin package: {entry.FullName}.");
					if (entry.Length < 0 || entry.Length > entryLimit)
						throw new SkinPackageException("package.entrySize", $"{entry.FullName} exceeds its size limit.");

					int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
					if (unixType == 0xa000)
						throw new SkinPackageException("package.symlink", "Symbolic links are not allowed in skin packages.");

					byte[] data = ReadBounded(entry, entryLimit);
					expandedBytes = checked(expandedBytes + data.Length);
					if (expandedBytes > MaxExpandedPackageBytes)
						throw new SkinPackageException("package.expanded", "The expanded skin package exceeds the 2 MiB limit.");
					files[entry.FullName] = data;
				}

				RequireFile(files, "manifest.json");
				RequireFile(files, "icon.png");
				RequireFile(files, "textures/head.png");
				RequireFile(files, "textures/body.png");
				RequireFile(files, "textures/legs.png");

				SkinManifest manifest = SkinManifestValidator.ParseAndNormalize(files["manifest.json"]);
				PngInspector.ValidateRgba(files["icon.png"], 80, 80, "icon.png");
				PngInspector.ValidateRgba(files["textures/head.png"], 40, 1120, "textures/head.png");
				PngInspector.ValidateRgba(files["textures/body.png"], 360, 224, "textures/body.png");
				PngInspector.ValidateRgba(files["textures/legs.png"], 40, 1120, "textures/legs.png");

				return new ValidatedSkinPackage
				{
					Manifest = manifest,
					IconPng = files["icon.png"],
					HeadPng = files["textures/head.png"],
					BodyPng = files["textures/body.png"],
					LegsPng = files["textures/legs.png"],
					LicenseText = files.GetValueOrDefault("LICENSE.txt")
				};
			}
			catch (SkinPackageException)
			{
				throw;
			}
			catch (InvalidDataException exception)
			{
				throw new SkinPackageException("package.zip", "The .cskin file is not a valid ZIP package.", exception);
			}
			catch (IOException exception)
			{
				throw new SkinPackageException("package.io", "The skin package could not be read.", exception);
			}
		}

		private static void ValidateEntryName(ZipArchiveEntry entry)
		{
			string name = entry.FullName;
			if (string.IsNullOrWhiteSpace(name) ||
				name.Contains('\\') ||
				name.StartsWith('/') ||
				name.Contains(':') ||
				name.EndsWith('/') ||
				name.Split('/').AsSpan().Contains(".."))
			{
				throw new SkinPackageException("package.path", $"Unsafe path in skin package: {name}.");
			}
		}

		private static byte[] ReadBounded(ZipArchiveEntry entry, int maximumBytes)
		{
			using Stream input = entry.Open();
			using MemoryStream output = new(entry.Length <= int.MaxValue ? (int)entry.Length : 0);
			byte[] buffer = new byte[16 * 1024];
			int total = 0;
			int read;

			while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
			{
				total = checked(total + read);
				if (total > maximumBytes)
					throw new SkinPackageException("package.entrySize", $"{entry.FullName} exceeds its size limit.");
				output.Write(buffer, 0, read);
			}

			if (total != entry.Length)
				throw new SkinPackageException("package.length", $"{entry.FullName} has an inconsistent length.");
			return output.ToArray();
		}

		private static void RequireFile(IReadOnlyDictionary<string, byte[]> files, string name)
		{
			if (!files.ContainsKey(name))
				throw new SkinPackageException("package.missingFile", $"Required file is missing: {name}.");
		}
	}
}
