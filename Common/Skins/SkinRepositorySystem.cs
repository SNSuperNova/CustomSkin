using customskin.Common.Players;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Terraria;
using Terraria.ModLoader;

namespace customskin.Common.Skins
{
	[Autoload(Side = ModSide.Client)]
	public sealed partial class SkinRepositorySystem : ModSystem
	{
		private sealed class RepositoryState
		{
			public string? SelectedHash { get; set; }
			public bool LegacyMigrated { get; set; }
		}

		private readonly Dictionary<string, SkinRecord> skins = new(StringComparer.Ordinal);
		private string? legacySelectedHash;

		public string RootPath { get; private set; } = string.Empty;
		public string InboxPath { get; private set; } = string.Empty;
		public string SkinsPath { get; private set; } = string.Empty;
		public string TrashPath { get; private set; } = string.Empty;
		public SkinRecord? SelectedSkin { get; private set; }
		public IReadOnlyList<SkinRecord> Skins => skins.Values
			.OrderBy(skin => skin.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(skin => skin.Hash, StringComparer.Ordinal)
			.ToArray();
		public IReadOnlyList<SkinRecord> TrashSkins => LoadTrashRecords();

		private string StatePath => Path.Combine(RootPath, "state.json");

		public override void PostSetupContent()
		{
			RootPath = Path.Combine(Main.SavePath, "CustomSkin");
			InboxPath = Path.Combine(RootPath, "Inbox");
			SkinsPath = Path.Combine(RootPath, "Skins");
			TrashPath = Path.Combine(RootPath, "Trash");
			Directory.CreateDirectory(InboxPath);
			Directory.CreateDirectory(SkinsPath);
			Directory.CreateDirectory(TrashPath);
			CleanupInterruptedImports();
			ReloadLibrary();
			LoadLegacySelection();
		}

		public IReadOnlyList<SkinImportResult> ImportInbox()
		{
			List<SkinImportResult> results = new();
			foreach (string packagePath in Directory.EnumerateFiles(InboxPath, "*.cskin", SearchOption.TopDirectoryOnly)
				.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
			{
				results.Add(ImportPackage(packagePath));
			}
			return results;
		}

		public SkinImportResult InstallBundledExample()
		{
			string packagePath = Path.Combine(InboxPath, "customskin-bundled-example.cskin");
			File.WriteAllBytes(packagePath, Mod.GetFileBytes("Assets/TestSkin/BundledExample.cskin"));
			return ImportPackage(packagePath);
		}

		public SkinImportResult ImportPackage(string packagePath)
		{
			string fileName = Path.GetFileName(packagePath);
			try
			{
				ValidatedSkinPackage validated = SkinPackageImporter.ValidateAndRead(packagePath);
				NormalizedSkinPackage normalized = SkinNormalizer.Normalize(validated);
				if (skins.TryGetValue(normalized.Hash, out SkinRecord? existing))
					return new SkinImportResult(fileName, existing, WasNew: false, null, null);

				string finalDirectory = GetSkinDirectory(normalized.Hash);
				if (Directory.Exists(finalDirectory))
				{
					SkinRecord recovered = LoadRecord(finalDirectory, normalized.Hash);
					skins[recovered.Hash] = recovered;
					return new SkinImportResult(fileName, recovered, WasNew: false, null, null);
				}

				string temporaryDirectory = Path.Combine(SkinsPath, $".import-{Guid.NewGuid():N}");
				try
				{
					Directory.CreateDirectory(Path.Combine(temporaryDirectory, "pixels"));
					File.WriteAllBytes(Path.Combine(temporaryDirectory, "manifest.json"), normalized.ManifestJson);
					File.WriteAllBytes(Path.Combine(temporaryDirectory, "pixels", "icon.rgba"), normalized.IconRgba);
					File.WriteAllBytes(Path.Combine(temporaryDirectory, "pixels", "head.rgba"), normalized.HeadRgba);
					File.WriteAllBytes(Path.Combine(temporaryDirectory, "pixels", "body.rgba"), normalized.BodyRgba);
					File.WriteAllBytes(Path.Combine(temporaryDirectory, "pixels", "legs.rgba"), normalized.LegsRgba);
					if (normalized.LicenseText != null)
						File.WriteAllBytes(Path.Combine(temporaryDirectory, "LICENSE.txt"), normalized.LicenseText);
					Directory.Move(temporaryDirectory, finalDirectory);
				}
				catch
				{
					if (Directory.Exists(temporaryDirectory))
						Directory.Delete(temporaryDirectory, recursive: true);
					throw;
				}

				SkinRecord record = new()
				{
					Hash = normalized.Hash,
					DirectoryPath = finalDirectory,
					Manifest = normalized.Manifest
				};
				skins.Add(record.Hash, record);
				return new SkinImportResult(fileName, record, WasNew: true, null, null);
			}
			catch (SkinPackageException exception)
			{
				return new SkinImportResult(fileName, null, false, exception.ErrorCode, exception.Message);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				return new SkinImportResult(fileName, null, false, "repository.import", exception.Message);
			}
		}

		public void SelectSkin(string hash)
		{
			if (!skins.TryGetValue(hash, out SkinRecord? skin))
				throw new SkinPackageException("repository.missing", "The selected skin is no longer installed.");

			ModContent.GetInstance<SkinTextureSystem>().SetActive(skin);
			SelectedSkin = skin;
			if (Main.LocalPlayer.active)
				Main.LocalPlayer.GetModPlayer<SkinPlayer>().SetSelectedSkinHash(hash);
		}

		public void ClearSelection()
		{
			SelectedSkin = null;
			ModContent.GetInstance<SkinTextureSystem>().ClearActive();
			if (Main.LocalPlayer.active)
				Main.LocalPlayer.GetModPlayer<SkinPlayer>().SetSelectedSkinHash(null);
		}

		public bool TryGetSkin(string hash, out SkinRecord? skin)
			=> skins.TryGetValue(hash, out skin);

		public void ApplyCharacterSelection(SkinPlayer skinPlayer)
		{
			if (!skinPlayer.HasCharacterSelectionRecord)
			{
				string? migrated = legacySelectedHash;
				skinPlayer.SetSelectedSkinHash(migrated);
				if (migrated != null)
					ConsumeLegacySelection();
			}

			string? hash = skinPlayer.SelectedSkinHash;
			if (hash != null && skins.TryGetValue(hash, out SkinRecord? selected))
			{
				ModContent.GetInstance<SkinTextureSystem>().SetActive(selected);
				SelectedSkin = selected;
				return;
			}

			SelectedSkin = null;
			ModContent.GetInstance<SkinTextureSystem>().ClearActive();
			if (hash != null)
				Mod.Logger.Warn($"Character {skinPlayer.Player.name} references a skin that is not installed: {hash}.");
		}

		public bool DeleteSkin(string hash)
		{
			if (!skins.TryGetValue(hash, out SkinRecord? skin))
				return false;

			if (SelectedSkin?.Hash == hash)
				ClearSelection();
			ModContent.GetInstance<SkinTextureSystem>().RemoveCharacterPreview(hash);

			string trashName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{hash}-{Guid.NewGuid():N}";
			string trashDirectory = Path.Combine(TrashPath, trashName);
			Directory.Move(skin.DirectoryPath, trashDirectory);
			skins.Remove(hash);
			return true;
		}

		internal bool RemoveCreatorGeneratedSkin(string hash)
		{
			if (!skins.TryGetValue(hash, out SkinRecord? skin))
				return false;
			if (SelectedSkin?.Hash == hash)
				throw new SkinPackageException("project.active", "The creator skin being replaced is still active.");

			EnsureDirectChild(skin.DirectoryPath, SkinsPath);
			ModContent.GetInstance<SkinTextureSystem>().RemoveCharacterPreview(hash);
			Directory.Delete(skin.DirectoryPath, recursive: true);
			skins.Remove(hash);
			return true;
		}

		public void RestoreTrashSkin(SkinRecord trashSkin)
		{
			EnsureDirectChild(trashSkin.DirectoryPath, TrashPath);
			SkinRecord verified = LoadRecord(trashSkin.DirectoryPath, trashSkin.Hash);
			if (skins.ContainsKey(verified.Hash) || Directory.Exists(GetSkinDirectory(verified.Hash)))
				throw new SkinPackageException("repository.restore_exists", "This skin is already installed.");

			string destination = GetSkinDirectory(verified.Hash);
			Directory.Move(verified.DirectoryPath, destination);
			SkinRecord restored = LoadRecord(destination, verified.Hash);
			skins.Add(restored.Hash, restored);
		}

		public void PermanentlyDeleteTrashSkin(SkinRecord trashSkin)
		{
			EnsureDirectChild(trashSkin.DirectoryPath, TrashPath);
			_ = LoadRecord(trashSkin.DirectoryPath, trashSkin.Hash);
			Directory.Delete(trashSkin.DirectoryPath, recursive: true);
		}

		public override void Unload()
		{
			skins.Clear();
			SelectedSkin = null;
			legacySelectedHash = null;
		}

		private void ReloadLibrary()
		{
			skins.Clear();
			foreach (string directory in Directory.EnumerateDirectories(SkinsPath))
			{
				string hash = Path.GetFileName(directory);
				if (!HashRegex().IsMatch(hash))
					continue;

				try
				{
					SkinRecord record = LoadRecord(directory, hash);
					skins.Add(hash, record);
				}
				catch (Exception exception)
				{
					Mod.Logger.Warn($"Ignoring corrupt stored skin {hash}: {exception.Message}");
				}
			}
		}

		private IReadOnlyList<SkinRecord> LoadTrashRecords()
		{
			List<SkinRecord> records = new();
			foreach (string directory in Directory.EnumerateDirectories(TrashPath))
			{
				Match match = TrashDirectoryRegex().Match(Path.GetFileName(directory));
				if (!match.Success)
					continue;

				string hash = match.Groups["hash"].Value;
				try
				{
					records.Add(LoadRecord(directory, hash));
				}
				catch (Exception exception)
				{
					Mod.Logger.Warn($"Ignoring corrupt trashed skin {Path.GetFileName(directory)}: {exception.Message}");
				}
			}

			return records
				.OrderBy(skin => skin.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(skin => skin.Hash, StringComparer.Ordinal)
				.ToArray();
		}

		private static SkinRecord LoadRecord(string directory, string hash)
		{
			string manifestPath = Path.Combine(directory, "manifest.json");
			FileInfo manifestFile = new(manifestPath);
			if (!manifestFile.Exists || manifestFile.Length <= 0 || manifestFile.Length > SkinPackageImporter.MaxManifestBytes)
				throw new SkinPackageException("repository.manifest", "Stored manifest has an invalid length.");
			byte[] manifestBytes = File.ReadAllBytes(manifestPath);
			SkinManifest manifest = SkinManifestValidator.ParseAndNormalize(manifestBytes);
			byte[] canonicalManifest = SkinManifestValidator.SerializeCanonical(manifest);
			byte[] icon = ReadRgba(Path.Combine(directory, "pixels", "icon.rgba"), 80, 80);
			byte[] head = ReadRgba(Path.Combine(directory, "pixels", "head.rgba"), 40, 1120);
			byte[] body = ReadRgba(Path.Combine(directory, "pixels", "body.rgba"), 360, 224);
			byte[] legs = ReadRgba(Path.Combine(directory, "pixels", "legs.rgba"), 40, 1120);
			string licensePath = Path.Combine(directory, "LICENSE.txt");
			byte[]? license = null;
			if (File.Exists(licensePath))
			{
				FileInfo licenseFile = new(licensePath);
				if (licenseFile.Length > SkinPackageImporter.MaxLicenseBytes)
					throw new SkinPackageException("repository.license", "Stored LICENSE.txt exceeds its size limit.");
				license = File.ReadAllBytes(licensePath);
			}
			string actualHash = SkinContentHasher.ComputeHash(canonicalManifest, icon, head, body, legs, license);
			if (!string.Equals(actualHash, hash, StringComparison.Ordinal))
				throw new SkinPackageException("repository.hash", "Stored skin content does not match its directory hash.");
			return new SkinRecord { Hash = hash, DirectoryPath = directory, Manifest = manifest };
		}

		private static byte[] ReadRgba(string path, int width, int height)
		{
			int expectedBytes = checked(width * height * 4);
			FileInfo file = new(path);
			if (!file.Exists || file.Length != expectedBytes)
				throw new SkinPackageException("repository.rgba", $"Stored RGBA data has invalid length: {path}.");
			return File.ReadAllBytes(path);
		}

		private void LoadLegacySelection()
		{
			if (!File.Exists(StatePath))
				return;

			try
			{
				if (new FileInfo(StatePath).Length > 4096)
					throw new SkinPackageException("repository.state", "Saved selection state is too large.");
				RepositoryState? state = JsonSerializer.Deserialize<RepositoryState>(File.ReadAllBytes(StatePath));
				if (state is { LegacyMigrated: false, SelectedHash: not null } && skins.ContainsKey(state.SelectedHash))
					legacySelectedHash = state.SelectedHash;
			}
			catch (Exception exception)
			{
				Mod.Logger.Warn($"Could not read the legacy global skin selection: {exception.Message}");
				legacySelectedHash = null;
			}
		}

		private void ConsumeLegacySelection()
		{
			legacySelectedHash = null;
			byte[] json = JsonSerializer.SerializeToUtf8Bytes(new RepositoryState { SelectedHash = null, LegacyMigrated = true });
			string temporary = StatePath + ".tmp";
			File.WriteAllBytes(temporary, json);
			File.Move(temporary, StatePath, overwrite: true);
		}

		private void CleanupInterruptedImports()
		{
			foreach (string directory in Directory.EnumerateDirectories(SkinsPath, ".import-*", SearchOption.TopDirectoryOnly))
				Directory.Delete(directory, recursive: true);
		}

		private string GetSkinDirectory(string hash)
		{
			if (!HashRegex().IsMatch(hash))
				throw new ArgumentException("Invalid skin hash.", nameof(hash));
			return Path.Combine(SkinsPath, hash);
		}

		private static void EnsureDirectChild(string directory, string expectedParent)
		{
			string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
			string fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedParent));
			if (!string.Equals(Path.GetDirectoryName(fullDirectory), fullParent, StringComparison.OrdinalIgnoreCase))
				throw new SkinPackageException("repository.path", "The trash entry path is invalid.");
		}

		[GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
		private static partial Regex HashRegex();

		[GeneratedRegex("^[0-9]{8}-[0-9]{6}-(?<hash>[0-9a-f]{64})-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
		private static partial Regex TrashDirectoryRegex();
	}
}
