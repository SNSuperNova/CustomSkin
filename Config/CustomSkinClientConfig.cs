using customskin.Common.Networking;
using System.Collections.Generic;
using System.ComponentModel;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace customskin.Config
{
	public enum SkinEnableMode
	{
		[LabelKey("$Mods.customskin.Configs.SkinEnableMode.Always")]
		Always,
		[LabelKey("$Mods.customskin.Configs.SkinEnableMode.AccessoryOnly")]
		AccessoryOnly,
		[LabelKey("$Mods.customskin.Configs.SkinEnableMode.Disabled")]
		Disabled
	}

	public sealed class CustomSkinClientConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		[DefaultValue(SkinEnableMode.Always)]
		public SkinEnableMode EnableMode { get; set; } = SkinEnableMode.Always;

		[DefaultValue(true)]
		public bool ShowRemoteSkins { get; set; } = true;

		public List<string> BlockedPlayerNames { get; set; } = new();

		public bool IsRemotePlayerAllowed(string? playerName)
			=> RemoteSkinPrivacyPolicy.IsPlayerAllowed(ShowRemoteSkins, playerName, BlockedPlayerNames);

		public override void OnChanged()
		{
			// Initial config loading happens before normal ModSystem autoloading. Only
			// reach the live network system when a multiplayer client actually exists.
			if (Main.netMode == NetmodeID.MultiplayerClient)
				ModContent.GetInstance<SkinNetworkSystem>().ApplyClientPrivacySettings();
		}
	}
}
