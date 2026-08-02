using System;
using System.Collections.Generic;

namespace customskin.Common.Networking
{
	public sealed class SkinNetworkPolicy
	{
		private readonly HashSet<string> bannedHashes = new(StringComparer.Ordinal);

		public SkinNetworkPolicy(
			bool allowUploads,
			int maxUploadBytes,
			int maxCachedResources,
			long maxCachedBytes,
			TimeSpan uploadCooldown,
			IEnumerable<string>? bannedHashes)
		{
			AllowUploads = allowUploads;
			MaxUploadBytes = Math.Clamp(maxUploadBytes, 1, SkinNetworkResource.MaxSerializedBytes);
			MaxCachedResources = Math.Max(1, maxCachedResources);
			MaxCachedBytes = Math.Max(1, maxCachedBytes);
			UploadCooldown = uploadCooldown < TimeSpan.Zero ? TimeSpan.Zero : uploadCooldown;

			if (bannedHashes == null)
				return;
			foreach (string? value in bannedHashes)
			{
				string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
				if (SkinNetworkResource.IsValidHash(normalized))
					this.bannedHashes.Add(normalized);
			}
		}

		public bool AllowUploads { get; }
		public int MaxUploadBytes { get; }
		public int MaxCachedResources { get; }
		public long MaxCachedBytes { get; }
		public TimeSpan UploadCooldown { get; }

		public bool IsHashBanned(string hash)
			=> !string.IsNullOrWhiteSpace(hash) && bannedHashes.Contains(hash.Trim().ToLowerInvariant());

		public bool CanBeginUpload(string hash, int totalBytes)
			=> AllowUploads && SkinNetworkResource.IsValidHash(hash) && !IsHashBanned(hash) &&
				totalBytes > 0 && totalBytes <= MaxUploadBytes;

		public bool IsUploadCooldownElapsed(DateTime nowUtc, DateTime? lastRequestUtc)
			=> lastRequestUtc == null || nowUtc - lastRequestUtc.Value >= UploadCooldown;

		public bool CanCache(string hash, int payloadBytes, int currentResourceCount, long currentResourceBytes, bool alreadyCached)
		{
			if (!CanBeginUpload(hash, payloadBytes))
				return false;
			if (alreadyCached)
				return true;
			return currentResourceCount < MaxCachedResources &&
				currentResourceBytes <= MaxCachedBytes - payloadBytes;
		}
	}
}
