using customskin.Common.Networking;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace customskin.Config
{
	public sealed class CustomSkinServerConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ServerSide;

		[DefaultValue(true)]
		public bool AllowSkinUploads { get; set; } = true;

		[DefaultValue(800)]
		[Range(700, 800)]
		[Increment(10)]
		public int MaxUploadKiB { get; set; } = 800;

		[DefaultValue(64)]
		[Range(1, 256)]
		public int MaxCachedResources { get; set; } = 64;

		[DefaultValue(64)]
		[Range(1, 512)]
		public int MaxCachedResourceMiB { get; set; } = 64;

		[DefaultValue(5)]
		[Range(1, 60)]
		public int UploadCooldownSeconds { get; set; } = 5;

		public List<string> BannedSkinHashes { get; set; } = new();

		public SkinNetworkPolicy CreatePolicy()
			=> new(
				AllowSkinUploads,
				MaxUploadKiB * 1024,
				MaxCachedResources,
				(long)MaxCachedResourceMiB * 1024 * 1024,
				TimeSpan.FromSeconds(UploadCooldownSeconds),
				BannedSkinHashes);
	}
}
