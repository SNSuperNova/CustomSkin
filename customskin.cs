using customskin.Common.Networking;
using System.IO;
using Terraria.ModLoader;

namespace customskin
{
	public sealed class CustomSkinMod : Mod
	{
		public override void HandlePacket(BinaryReader reader, int whoAmI)
			=> ModContent.GetInstance<SkinNetworkSystem>().HandlePacket(reader, whoAmI);
	}
}
