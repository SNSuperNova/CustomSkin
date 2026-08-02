using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace customskin.Common.Skins
{
	public sealed class SkinProjectFiles
	{
		public required SkinManifest Manifest { get; init; }
		public required byte[] ManifestJson { get; init; }
		public required byte[] IconPng { get; init; }
		public required byte[] HeadPng { get; init; }
		public required byte[] BodyPng { get; init; }
		public required byte[] LegsPng { get; init; }
		public byte[]? LicenseText { get; init; }
	}

	public static class SkinProjectPackage
	{
		private static readonly DateTimeOffset DeterministicTimestamp =
			new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

		public static SkinProjectFiles Read(string projectDirectory)
		{
			if (!Directory.Exists(projectDirectory))
				throw new SkinPackageException("project.missing", "The creator project directory does not exist.");

			byte[] manifestJson = ReadBounded(Path.Combine(projectDirectory, "manifest.json"), SkinPackageImporter.MaxManifestBytes, "manifest.json");
			byte[] icon = ReadBounded(Path.Combine(projectDirectory, "icon.png"), SkinPackageImporter.MaxImageBytes, "icon.png");
			byte[] head = ReadBounded(Path.Combine(projectDirectory, "textures", "head.png"), SkinPackageImporter.MaxImageBytes, "textures/head.png");
			byte[] body = ReadBounded(Path.Combine(projectDirectory, "textures", "body.png"), SkinPackageImporter.MaxImageBytes, "textures/body.png");
			byte[] legs = ReadBounded(Path.Combine(projectDirectory, "textures", "legs.png"), SkinPackageImporter.MaxImageBytes, "textures/legs.png");

			SkinManifest manifest = SkinManifestValidator.ParseAndNormalize(manifestJson);
			PngInspector.ValidateRgba(icon, 80, 80, "icon.png");
			PngInspector.ValidateRgba(head, 40, 1120, "textures/head.png");
			PngInspector.ValidateRgba(body, 360, 224, "textures/body.png");
			PngInspector.ValidateRgba(legs, 40, 1120, "textures/legs.png");

			string licensePath = Path.Combine(projectDirectory, "LICENSE.txt");
			byte[]? license = File.Exists(licensePath)
				? SkinTextNormalizer.NormalizeLicense(ReadBounded(licensePath, SkinPackageImporter.MaxLicenseBytes, "LICENSE.txt"))
				: null;

			return new SkinProjectFiles
			{
				Manifest = manifest,
				ManifestJson = SkinManifestValidator.SerializeCanonical(manifest),
				IconPng = icon,
				HeadPng = head,
				BodyPng = body,
				LegsPng = legs,
				LicenseText = license
			};
		}

		public static string Export(string projectDirectory, string outputPath)
		{
			SkinProjectFiles files = Read(projectDirectory);
			string fullOutput = Path.GetFullPath(outputPath);
			if (!string.Equals(Path.GetExtension(fullOutput), ".cskin", StringComparison.OrdinalIgnoreCase))
				throw new SkinPackageException("package.extension", "Skin packages must use the .cskin extension.");

			Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
			string temporary = Path.Combine(
				Path.GetDirectoryName(fullOutput)!,
				$".{Path.GetFileNameWithoutExtension(fullOutput)}.tmp-{Guid.NewGuid():N}.cskin");
			try
			{
				using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false))
				{
					WriteEntry(archive, "manifest.json", files.ManifestJson);
					WriteEntry(archive, "icon.png", files.IconPng);
					WriteEntry(archive, "textures/head.png", files.HeadPng);
					WriteEntry(archive, "textures/body.png", files.BodyPng);
					WriteEntry(archive, "textures/legs.png", files.LegsPng);
					if (files.LicenseText != null)
						WriteEntry(archive, "LICENSE.txt", files.LicenseText);
				}

				if (new FileInfo(temporary).Length > SkinPackageImporter.MaxCompressedPackageBytes)
					throw new SkinPackageException("package.size", "The compressed skin package exceeds the 1 MiB limit.");

				_ = SkinPackageImporter.ValidateAndRead(temporary);
				File.Move(temporary, fullOutput, overwrite: true);
				return fullOutput;
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}

		private static byte[] ReadBounded(string path, int maximumBytes, string logicalName)
		{
			FileInfo file = new(path);
			if (!file.Exists)
				throw new SkinPackageException("project.missingFile", $"Required creator file is missing: {logicalName}.");
			if (file.Length <= 0 || file.Length > maximumBytes)
				throw new SkinPackageException("project.fileSize", $"Creator file exceeds its size limit: {logicalName}.");
			return File.ReadAllBytes(file.FullName);
		}

		private static void WriteEntry(ZipArchive archive, string name, byte[] data)
		{
			ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
			entry.LastWriteTime = DeterministicTimestamp;
			using Stream output = entry.Open();
			output.Write(data, 0, data.Length);
		}
	}
}
