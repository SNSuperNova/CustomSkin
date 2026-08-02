using customskin.Common.Networking;
using customskin.Common.UI;
using Microsoft.Xna.Framework;
using System;
using Terraria.Localization;
using Terraria.ModLoader;

namespace customskin.Content.Commands
{
	[Autoload(Side = ModSide.Client)]
	public sealed class SkinManagerCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "cskin";
		public override string Usage => "/cskin [reload]";
		public override string Description => Language.GetTextValue("Mods.customskin.Commands.Description");

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (args.Length == 0)
			{
				SkinManagerUISystem.Open();
				return;
			}
			if (args.Length == 1 && string.Equals(args[0], "reload", StringComparison.OrdinalIgnoreCase))
			{
				ModContent.GetInstance<SkinNetworkSystem>().RequestManualReload();
				return;
			}
			caller.Reply(Language.GetTextValue("Mods.customskin.Commands.Usage"), Color.Orange);
		}
	}
}
