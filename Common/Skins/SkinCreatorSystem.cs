using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace customskin.Common.Skins
{
	public sealed class SkinCreatorProject
	{
		public required string DirectoryPath { get; init; }
		public required string FolderName { get; init; }
		public required string DisplayName { get; init; }
		public string? LastImportedHash { get; init; }
		public string? ErrorCode { get; init; }
		public string? ErrorMessage { get; init; }
		public bool IsValid => ErrorCode == null;
	}

	public sealed class SkinEditorSettings
	{
		public int PoseIndex { get; set; }
		public SkinEditorPart Part { get; set; } = SkinEditorPart.Head;
		public SkinEditorGender Gender { get; set; } = SkinEditorGender.Male;
		public SkinEditorTool Tool { get; set; } = SkinEditorTool.Pencil;
		public int Zoom { get; set; } = 8;
		public bool ShowGrid { get; set; } = true;
		public bool MirrorPreview { get; set; }
		public bool OnlySelectedPart { get; set; }
		public byte ColorR { get; set; } = 255;
		public byte ColorG { get; set; } = 255;
		public byte ColorB { get; set; } = 255;
		public byte ColorA { get; set; } = 255;
		public bool ReferenceVisible { get; set; } = true;
		public byte ReferenceOpacity { get; set; } = 128;
		public List<SkinReferenceFrameSettings> ReferenceFrames { get; set; } = new();

		public SkinEditorColor Color
		{
			get => new(ColorR, ColorG, ColorB, ColorA);
			set { ColorR = value.R; ColorG = value.G; ColorB = value.B; ColorA = value.A; }
		}

		public SkinReferenceFrameSettings GetReferenceFrame(int poseIndex)
		{
			while (ReferenceFrames.Count <= poseIndex)
				ReferenceFrames.Add(new SkinReferenceFrameSettings());
			return ReferenceFrames[poseIndex];
		}
	}

	public sealed class SkinReferenceFrameSettings
	{
		public int OffsetX { get; set; }
		public int OffsetY { get; set; }
		public bool Mirror { get; set; }
		public bool Visible { get; set; } = true;
	}

	[Autoload(Side = ModSide.Client)]
	public sealed partial class SkinCreatorSystem : ModSystem
	{
		public const string ReferenceFileName = "reference.png";
		public const int MaximumReferenceFileBytes = 16 * 1024 * 1024;
		public const int MaximumReferenceDimension = 4096;
		public const int MaximumReferencePixels = 4 * 1024 * 1024;

		private sealed class ProjectState
		{
			public string? LastImportedHash { get; set; }
		}

		private static readonly JsonSerializerOptions StateJsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true
		};

		public string ProjectsPath { get; private set; } = string.Empty;
		public string CreatorTrashPath { get; private set; } = string.Empty;
		public string ExportsPath { get; private set; } = string.Empty;

		public override void PostSetupContent()
		{
			string root = Path.Combine(Main.SavePath, "CustomSkin");
			ProjectsPath = Path.Combine(root, "CreatorProjects");
			CreatorTrashPath = Path.Combine(root, "CreatorTrash");
			ExportsPath = Path.Combine(root, "Exports");
			Directory.CreateDirectory(ProjectsPath);
			Directory.CreateDirectory(CreatorTrashPath);
			Directory.CreateDirectory(ExportsPath);
		}

		public IReadOnlyList<SkinCreatorProject> GetProjects()
		{
			if (!Directory.Exists(ProjectsPath))
				return Array.Empty<SkinCreatorProject>();

			return Directory.EnumerateDirectories(ProjectsPath, "*", SearchOption.TopDirectoryOnly)
				.Select(LoadProjectRecord)
				.OrderBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(project => project.FolderName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		public SkinCreatorProject CreateTemplate()
		{
			DateTime now = DateTime.Now;
			string suffix = now.ToString("yyyyMMdd-HHmmss");
			string folder = GetUniqueProjectDirectory($"skin-{suffix}");
			try
			{
				Directory.CreateDirectory(Path.Combine(folder, "textures"));
				Directory.CreateDirectory(Path.Combine(folder, "guides"));
				Directory.CreateDirectory(Path.Combine(folder, "references"));

				string author = Main.LocalPlayer.active && !string.IsNullOrWhiteSpace(Main.LocalPlayer.name)
					? Main.LocalPlayer.name.Trim()
					: Language.GetTextValue("Mods.customskin.UI.TemplateDefaultAuthor");
				SkinManifest manifest = new()
				{
					SchemaVersion = SkinManifestValidator.CurrentSchemaVersion,
					Profile = SkinManifestValidator.CurrentProfile,
					Id = $"local.{Path.GetFileName(folder).Replace('-', '_')}",
					Name = Language.GetTextValue("Mods.customskin.UI.TemplateDefaultName", now.ToString("yyyy-MM-dd HH:mm:ss")),
					Author = author,
					Version = "1.0.0",
					Description = Language.GetTextValue("Mods.customskin.UI.TemplateDefaultDescription"),
					Hide = new SkinHideOptions
					{
						Hair = true,
						HeadArmor = true,
						BodyArmor = true,
						LegArmor = true,
						FaceAccessories = true,
						BodyAccessories = true,
						LegAccessories = true,
						Wings = false
					}
				};

				ValidatedSkinPackage template = LoadBundledTemplate(folder);
				File.WriteAllBytes(Path.Combine(folder, "manifest.json"), SkinManifestValidator.SerializeEditable(manifest));
				// icon.png is the skin package's 80x80 identity image, not an author
				// avatar. Until project icons are exposed in the UI, seed new projects
				// with the recognizable CustomSkin mod icon. Creators may still replace
				// it manually before exporting their package.
				File.WriteAllBytes(Path.Combine(folder, "icon.png"), Mod.GetFileBytes("icon.png"));
				File.WriteAllBytes(Path.Combine(folder, "textures", "head.png"), template.HeadPng);
				File.WriteAllBytes(Path.Combine(folder, "textures", "body.png"), template.BodyPng);
				File.WriteAllBytes(Path.Combine(folder, "textures", "legs.png"), template.LegsPng);
				WriteBundledCreatorFiles(folder, overwrite: true);
				WriteState(folder, new ProjectState());

				SkinCreatorProject created = LoadProjectRecord(folder);
				OpenDirectory(folder);
				return created;
			}
			catch
			{
				if (Directory.Exists(folder))
					Directory.Delete(folder, recursive: true);
				throw;
			}
		}

		public SkinImportResult RefreshAndUse(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			string temporary = Path.Combine(ExportsPath, $".refresh-{Guid.NewGuid():N}.cskin");
			try
			{
				SkinProjectPackage.Export(project.DirectoryPath, temporary);
				SkinRepositorySystem repository = ModContent.GetInstance<SkinRepositorySystem>();
				SkinImportResult result = repository.ImportPackage(temporary);
				if (!result.Success)
					return result;

				ProjectState state = ReadState(project.DirectoryPath);
				string? previousHash = state.LastImportedHash;
				repository.SelectSkin(result.Skin!.Hash);
				state.LastImportedHash = result.Skin.Hash;
				WriteState(project.DirectoryPath, state);

				if (previousHash != null && !string.Equals(previousHash, result.Skin.Hash, StringComparison.Ordinal) &&
					!IsReferencedByAnotherProject(previousHash, project.DirectoryPath))
				{
					repository.RemoveCreatorGeneratedSkin(previousHash);
				}

				return result;
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}

		public SkinEditorDocument LoadEditorDocument(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			SkinProjectFiles files = SkinProjectPackage.Read(project.DirectoryPath);
			NormalizedSkinPackage normalized = SkinNormalizer.Normalize(new ValidatedSkinPackage
			{
				Manifest = files.Manifest,
				IconPng = files.IconPng,
				HeadPng = files.HeadPng,
				BodyPng = files.BodyPng,
				LegsPng = files.LegsPng,
				LicenseText = files.LicenseText
			});
			return new SkinEditorDocument(normalized.HeadRgba, normalized.BodyRgba, normalized.LegsRgba,
				ComputeTextureFingerprint(files.HeadPng, files.BodyPng, files.LegsPng));
		}

		public SkinEditorSettings ReadEditorSettings(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			string path = Path.Combine(project.DirectoryPath, "editor-state.json");
			if (!File.Exists(path))
				return new SkinEditorSettings();
			try
			{
				FileInfo file = new(path);
				if (file.Length <= 0 || file.Length > 8192)
					return new SkinEditorSettings();
				SkinEditorSettings settings = JsonSerializer.Deserialize<SkinEditorSettings>(File.ReadAllBytes(path), StateJsonOptions)
					?? new SkinEditorSettings();
				settings.PoseIndex = Math.Clamp(settings.PoseIndex, 0, SkinEditorDocument.Poses.Count - 1);
				settings.Zoom = Math.Clamp(settings.Zoom, 2, 16);
				settings.ReferenceOpacity = (byte)Math.Clamp((int)settings.ReferenceOpacity, 16, 255);
				settings.ReferenceFrames ??= new List<SkinReferenceFrameSettings>();
				if (settings.ReferenceFrames.Count > SkinEditorDocument.Poses.Count)
					settings.ReferenceFrames.RemoveRange(SkinEditorDocument.Poses.Count, settings.ReferenceFrames.Count - SkinEditorDocument.Poses.Count);
				while (settings.ReferenceFrames.Count < SkinEditorDocument.Poses.Count)
					settings.ReferenceFrames.Add(new SkinReferenceFrameSettings());
				foreach (SkinReferenceFrameSettings frame in settings.ReferenceFrames)
				{
					frame.OffsetX = Math.Clamp(frame.OffsetX, -MaximumReferenceDimension, MaximumReferenceDimension);
					frame.OffsetY = Math.Clamp(frame.OffsetY, -MaximumReferenceDimension, MaximumReferenceDimension);
				}
				if (!Enum.IsDefined(settings.Part)) settings.Part = SkinEditorPart.Head;
				if (!Enum.IsDefined(settings.Gender)) settings.Gender = SkinEditorGender.Male;
				if (!Enum.IsDefined(settings.Tool)) settings.Tool = SkinEditorTool.Pencil;
				return settings;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				Mod.Logger.Warn($"Could not read editor state {path}: {exception.Message}");
				return new SkinEditorSettings();
			}
		}

		public void WriteEditorSettings(SkinCreatorProject project, SkinEditorSettings settings)
		{
			EnsureProjectChild(project.DirectoryPath);
			string path = Path.Combine(project.DirectoryPath, "editor-state.json");
			string temporary = path + ".tmp";
			File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(settings, StateJsonOptions));
			File.Move(temporary, path, overwrite: true);
		}

		public string GetReferenceDirectory(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			return Path.Combine(project.DirectoryPath, "references");
		}

		public string GetReferencePath(SkinCreatorProject project)
			=> Path.Combine(GetReferenceDirectory(project), ReferenceFileName);

		public void OpenReferenceDirectory(SkinCreatorProject project)
		{
			string directory = GetReferenceDirectory(project);
			Directory.CreateDirectory(directory);
			OpenDirectory(directory);
		}

		public SkinReferenceImage? LoadReferenceImage(SkinCreatorProject project)
		{
			string path = GetReferencePath(project);
			if (!File.Exists(path))
				return null;
			FileInfo file = new(path);
			if (file.Length <= 0 || file.Length > MaximumReferenceFileBytes)
				throw new SkinPackageException("reference.size", $"{ReferenceFileName} exceeds the {MaximumReferenceFileBytes / 1024 / 1024} MiB limit.");
			return DecodeReferenceImage(File.ReadAllBytes(path));
		}

		public SkinReferenceImage ImportReferenceImage(SkinCreatorProject project, string sourcePath)
		{
			if (string.IsNullOrWhiteSpace(sourcePath))
				throw new SkinPackageException("reference.path", "A reference PNG path is required.");
			string source = Path.GetFullPath(sourcePath);
			if (!string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase))
				throw new SkinPackageException("reference.extension", "The reference image must use the .png extension.");
			FileInfo file = new(source);
			if (!file.Exists)
				throw new SkinPackageException("reference.missing", "The selected reference PNG no longer exists.");
			if (file.Length <= 0 || file.Length > MaximumReferenceFileBytes)
				throw new SkinPackageException("reference.size", $"The selected PNG exceeds the {MaximumReferenceFileBytes / 1024 / 1024} MiB limit.");

			byte[] png = File.ReadAllBytes(source);
			SkinReferenceImage decoded = DecodeReferenceImage(png);
			string directory = GetReferenceDirectory(project);
			Directory.CreateDirectory(directory);
			string destination = Path.Combine(directory, ReferenceFileName);
			string temporary = destination + ".tmp";
			try
			{
				File.WriteAllBytes(temporary, png);
				File.Move(temporary, destination, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary)) File.Delete(temporary);
			}
			return decoded;
		}

		private static SkinReferenceImage DecodeReferenceImage(byte[] png)
		{
			PngInfo info = PngInspector.ValidateStatic(png, MaximumReferenceDimension, MaximumReferenceDimension,
				MaximumReferencePixels, ReferenceFileName);
			try
			{
				using MemoryStream stream = new(png, writable: false);
				using Texture2D texture = Texture2D.FromStream(Main.instance.GraphicsDevice, stream);
				if (texture.Width != info.Width || texture.Height != info.Height)
					throw new SkinPackageException("reference.dimensions", "The decoded reference image dimensions do not match its PNG header.");
				Color[] colors = new Color[checked(info.Width * info.Height)];
				texture.GetData(colors);
				byte[] rgba = new byte[checked(colors.Length * 4)];
				for (int index = 0, output = 0; index < colors.Length; index++)
				{
					Color color = colors[index];
					rgba[output++] = color.R;
					rgba[output++] = color.G;
					rgba[output++] = color.B;
					rgba[output++] = color.A;
				}
				return new SkinReferenceImage(info.Width, info.Height, rgba);
			}
			catch (SkinPackageException)
			{
				throw;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				throw new SkinPackageException("reference.decode", "The static reference PNG could not be decoded.", exception);
			}
		}

		public void SaveEditorDocument(SkinCreatorProject project, SkinEditorDocument document)
		{
			EnsureProjectChild(project.DirectoryPath);
			document.CommitStroke();
			string textures = Path.Combine(project.DirectoryPath, "textures");
			string token = Guid.NewGuid().ToString("N");
			string[] names = { "head.png", "body.png", "legs.png" };
			int[] widths = { SkinEditorDocument.HeadWidth, SkinEditorDocument.BodyWidth, SkinEditorDocument.LegsWidth };
			int[] heights = { SkinEditorDocument.HeadHeight, SkinEditorDocument.BodyHeight, SkinEditorDocument.LegsHeight };
			byte[][] pixels = { document.CopyHeadRgba(), document.CopyBodyRgba(), document.CopyLegsRgba() };
			string[] targets = names.Select(name => Path.Combine(textures, name)).ToArray();
			string[] temporary = names.Select(name => Path.Combine(textures, $".{name}.{token}.tmp")).ToArray();
			string[] backups = names.Select(name => Path.Combine(textures, $".{name}.{token}.bak")).ToArray();
			byte[][] encoded = new byte[names.Length][];
			bool committed = false;
			try
			{
				if (document.SourceFingerprint != null)
				{
					string currentFingerprint = ComputeTextureFingerprint(targets.Select(File.ReadAllBytes).ToArray());
					if (!string.Equals(currentFingerprint, document.SourceFingerprint, StringComparison.Ordinal))
						throw new SkinPackageException("editor.externalChanged", "One or more runtime PNG files changed outside the editor. Reload the project before saving so those changes are not overwritten.");
				}
				// Opening and saving an untouched project must be byte-for-byte lossless,
				// including unusual but valid semi-transparent source pixels. Do not
				// decode/re-encode when the editor revision has not changed.
				if (!document.IsDirty)
				{
					_ = SkinProjectPackage.Read(project.DirectoryPath);
					document.MarkSaved();
					return;
				}
				for (int index = 0; index < names.Length; index++)
				{
					encoded[index] = RgbaPngEncoder.Encode(pixels[index], widths[index], heights[index]);
					PngInspector.ValidateRgba(encoded[index], widths[index], heights[index], $"textures/{names[index]}");
					File.WriteAllBytes(temporary[index], encoded[index]);
				}

				for (int index = 0; index < names.Length; index++)
					File.Move(targets[index], backups[index]);
				for (int index = 0; index < names.Length; index++)
					File.Move(temporary[index], targets[index]);

				_ = SkinProjectPackage.Read(project.DirectoryPath);
				committed = true;
				document.SetSourceFingerprint(ComputeTextureFingerprint(encoded));
				document.MarkSaved();
			}
			catch
			{
				for (int index = 0; index < names.Length; index++)
				{
					if (!File.Exists(backups[index]))
						continue;
					if (File.Exists(targets[index]))
						File.Delete(targets[index]);
					File.Move(backups[index], targets[index]);
				}
				throw;
			}
			finally
			{
				for (int index = 0; index < names.Length; index++)
				{
					if (File.Exists(temporary[index])) File.Delete(temporary[index]);
					if (committed && File.Exists(backups[index])) File.Delete(backups[index]);
				}
			}
		}

		private static string ComputeTextureFingerprint(params byte[][] files)
		{
			using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			foreach (byte[] file in files)
			{
				hash.AppendData(BitConverter.GetBytes(file.Length));
				hash.AppendData(file);
			}
			return Convert.ToHexString(hash.GetHashAndReset());
		}

		public string LoadPreview(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			SkinProjectFiles files = SkinProjectPackage.Read(project.DirectoryPath);
			ValidatedSkinPackage package = new()
			{
				Manifest = files.Manifest,
				IconPng = files.IconPng,
				HeadPng = files.HeadPng,
				BodyPng = files.BodyPng,
				LegsPng = files.LegsPng,
				LicenseText = files.LicenseText
			};
			NormalizedSkinPackage normalized = SkinNormalizer.Normalize(package);
			ModContent.GetInstance<SkinTextureSystem>().SetCreatorPreview(normalized);
			return normalized.Hash;
		}

		public string DeleteProject(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			Directory.CreateDirectory(CreatorTrashPath);
			string trashName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{project.FolderName}-{Guid.NewGuid():N}";
			string destination = Path.Combine(CreatorTrashPath, trashName);
			ModContent.GetInstance<SkinTextureSystem>().ClearCreatorPreview();
			Directory.Move(project.DirectoryPath, destination);
			return destination;
		}

		public string ExportForSharing(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			SkinProjectFiles files = SkinProjectPackage.Read(project.DirectoryPath);
			string fileName = $"{files.Manifest.Id.Replace('.', '-')}-{files.Manifest.Version}.cskin";
			return SkinProjectPackage.Export(project.DirectoryPath, Path.Combine(ExportsPath, fileName));
		}

		public void OpenProjectDirectory(SkinCreatorProject project)
		{
			EnsureProjectChild(project.DirectoryPath);
			OpenDirectory(project.DirectoryPath);
		}

		public void OpenExportsDirectory()
		{
			Directory.CreateDirectory(ExportsPath);
			OpenDirectory(ExportsPath);
		}

		private SkinCreatorProject LoadProjectRecord(string directory)
		{
			string folderName = Path.GetFileName(directory);
			ProjectState state = ReadState(directory);
			try
			{
				if (state.LastImportedHash != null &&
					!ModContent.GetInstance<SkinRepositorySystem>().TryGetSkin(state.LastImportedHash, out _))
				{
					state.LastImportedHash = null;
					WriteState(directory, state);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				Mod.Logger.Warn($"Could not clear stale creator import state in {directory}: {exception.Message}");
			}
			try
			{
				WriteBundledCreatorFiles(directory, overwrite: false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				Mod.Logger.Warn($"Could not update creator guides in {directory}: {exception.Message}");
			}
			try
			{
				SkinProjectFiles files = SkinProjectPackage.Read(directory);
				return new SkinCreatorProject
				{
					DirectoryPath = directory,
					FolderName = folderName,
					DisplayName = files.Manifest.Name,
					LastImportedHash = state.LastImportedHash
				};
			}
			catch (SkinPackageException exception)
			{
				return new SkinCreatorProject
				{
					DirectoryPath = directory,
					FolderName = folderName,
					DisplayName = folderName,
					LastImportedHash = state.LastImportedHash,
					ErrorCode = exception.ErrorCode,
					ErrorMessage = exception.Message
				};
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				return new SkinCreatorProject
				{
					DirectoryPath = directory,
					FolderName = folderName,
					DisplayName = folderName,
					LastImportedHash = state.LastImportedHash,
					ErrorCode = "project.read",
					ErrorMessage = exception.Message
				};
			}
		}

		private void WriteBundledCreatorFiles(string directory, bool overwrite)
		{
			string guides = Path.Combine(directory, "guides");
			Directory.CreateDirectory(guides);
			WriteBundledFile("Assets/Creator/Guides/head-guide.bin", Path.Combine(guides, "head-guide.png"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/body-guide.bin", Path.Combine(guides, "body-guide.png"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/legs-guide.bin", Path.Combine(guides, "legs-guide.png"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/head-state-map.bin", Path.Combine(guides, "head-state-map.png"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/body-state-map.bin", Path.Combine(guides, "body-state-map.png"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/legs-state-map.bin", Path.Combine(guides, "legs-state-map.png"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/frame-map.json", Path.Combine(guides, "frame-map.json"), overwrite);
			WriteBundledFile("Assets/Creator/Guides/animation-map.json", Path.Combine(guides, "animation-map.json"), overwrite);
			WriteBundledFile("Assets/Creator/README.txt", Path.Combine(directory, "README.txt"), overwrite);
		}

		private void WriteBundledFile(string source, string destination, bool overwrite)
		{
			if (overwrite || !File.Exists(destination))
				File.WriteAllBytes(destination, Mod.GetFileBytes(source));
		}

		private ValidatedSkinPackage LoadBundledTemplate(string projectDirectory)
		{
			string packagePath = Path.Combine(projectDirectory, ".bundled-template.cskin");
			try
			{
				File.WriteAllBytes(packagePath, Mod.GetFileBytes("Assets/TestSkin/BundledExample.cskin"));
				return SkinPackageImporter.ValidateAndRead(packagePath);
			}
			finally
			{
				if (File.Exists(packagePath))
					File.Delete(packagePath);
			}
		}

		private ProjectState ReadState(string directory)
		{
			string path = Path.Combine(directory, ".customskin-project.json");
			if (!File.Exists(path))
				return new ProjectState();
			try
			{
				FileInfo file = new(path);
				if (file.Length <= 0 || file.Length > 4096)
					return new ProjectState();
				return JsonSerializer.Deserialize<ProjectState>(File.ReadAllBytes(path), StateJsonOptions) ?? new ProjectState();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				Mod.Logger.Warn($"Could not read creator project state {path}: {exception.Message}");
				return new ProjectState();
			}
		}

		private static void WriteState(string directory, ProjectState state)
		{
			string path = Path.Combine(directory, ".customskin-project.json");
			string temporary = path + ".tmp";
			File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, StateJsonOptions));
			File.Move(temporary, path, overwrite: true);
		}

		private bool IsReferencedByAnotherProject(string hash, string currentDirectory)
		{
			foreach (string directory in Directory.EnumerateDirectories(ProjectsPath, "*", SearchOption.TopDirectoryOnly))
			{
				if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(currentDirectory), StringComparison.OrdinalIgnoreCase))
					continue;
				if (string.Equals(ReadState(directory).LastImportedHash, hash, StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		private string GetUniqueProjectDirectory(string baseName)
		{
			for (int suffix = 0; suffix < 1000; suffix++)
			{
				string name = suffix == 0 ? baseName : $"{baseName}-{suffix}";
				string candidate = Path.Combine(ProjectsPath, name);
				if (!Directory.Exists(candidate) && !File.Exists(candidate))
					return candidate;
			}
			throw new SkinPackageException("project.name", "Could not allocate a unique creator project folder.");
		}

		private void EnsureProjectChild(string directory)
		{
			string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
			string fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectsPath));
			if (!string.Equals(Path.GetDirectoryName(fullDirectory), fullParent, StringComparison.OrdinalIgnoreCase))
				throw new SkinPackageException("project.path", "The creator project path is outside CreatorProjects.");
		}

		private static void OpenDirectory(string directory)
		{
			Directory.CreateDirectory(directory);
			Process.Start(new ProcessStartInfo
			{
				FileName = Path.GetFullPath(directory),
				UseShellExecute = true
			});
		}
	}
}
