using customskin.Common.Skins;
using System;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ModLoader;

namespace customskin.Common.Networking
{
	[Autoload(Side = ModSide.Client)]
	public sealed class RemoteSkinCacheSystem : ModSystem
	{
		private const int MaxCacheFiles = 128;
		private const long MaxCacheBytes = 96L * 1024 * 1024;
		public string CachePath { get; private set; } = string.Empty;

		public override void PostSetupContent()
		{
			CachePath = Path.Combine(Main.SavePath, "CustomSkin", "RemoteCache");
			Directory.CreateDirectory(CachePath);
			foreach (string temporary in Directory.EnumerateFiles(CachePath, "*.tmp", SearchOption.TopDirectoryOnly))
			{
				try { File.Delete(temporary); }
				catch (Exception exception) { Mod.Logger.Warn($"Could not remove interrupted remote cache write: {exception.Message}"); }
			}
		}

		public bool TryLoad(string hash, out SkinNetworkResource? resource)
		{
			resource = null;
			if (!SkinNetworkResource.IsValidHash(hash))
				return false;

			string path = GetPath(hash);
			try
			{
				FileInfo file = new(path);
				if (!file.Exists || file.Length <= 0 || file.Length > SkinNetworkResource.MaxSerializedBytes)
					return false;
				resource = SkinNetworkResource.DeserializeAndValidate(File.ReadAllBytes(path), hash);
				File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
				return true;
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				Mod.Logger.Warn($"Ignoring invalid remote skin cache {hash}: {exception.Message}");
				return false;
			}
		}

		public SkinNetworkResource Store(byte[] payload, string expectedHash)
		{
			SkinNetworkResource resource = SkinNetworkResource.DeserializeAndValidate(payload, expectedHash);
			Directory.CreateDirectory(CachePath);
			string finalPath = GetPath(expectedHash);
			string temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
			try
			{
				File.WriteAllBytes(temporaryPath, payload);
				File.Move(temporaryPath, finalPath, overwrite: true);
				Prune(finalPath);
			}
			finally
			{
				if (File.Exists(temporaryPath))
					File.Delete(temporaryPath);
			}
			return resource;
		}

		private string GetPath(string hash)
			=> Path.Combine(CachePath, hash + ".cscache");

		private void Prune(string protectedPath)
		{
			FileInfo[] files = Directory.EnumerateFiles(CachePath, "*.cscache", SearchOption.TopDirectoryOnly)
				.Select(path => new FileInfo(path))
				.OrderBy(file => file.LastWriteTimeUtc)
				.ToArray();
			long bytes = files.Sum(file => file.Length);
			int count = files.Length;
			foreach (FileInfo file in files)
			{
				if (count <= MaxCacheFiles && bytes <= MaxCacheBytes)
					break;
				if (string.Equals(file.FullName, protectedPath, StringComparison.OrdinalIgnoreCase))
					continue;
				try
				{
					long length = file.Length;
					file.Delete();
					bytes -= length;
					count--;
				}
				catch (Exception exception)
				{
					Mod.Logger.Warn($"Could not prune remote skin cache {file.Name}: {exception.Message}");
				}
			}
		}
	}
}
