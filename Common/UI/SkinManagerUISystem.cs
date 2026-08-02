using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace customskin.Common.UI
{
	[Autoload(Side = ModSide.Client)]
	public sealed class SkinManagerUISystem : ModSystem
	{
		private const string OpenKeybindFullName = "customskin/OpenSkinManager";
		internal static ModKeybind? OpenKeybind { get; private set; }
		private static SkinManagerUISystem? Instance { get; set; }
		private bool keybindReady;

		public override void Load()
		{
			Instance = this;
			OpenKeybind = KeybindLoader.RegisterKeybind(Mod, "OpenSkinManager", "K");
		}

		public override void PostSetupContent()
			=> keybindReady = true;

		public override void PostUpdateInput()
		{
			// ModKeybind.JustPressed indexes tML's trigger dictionaries directly and
			// throws when an older input profile has no entry for this mod key. A
			// TryGetValue check also preserves intentional unbinding and begins working
			// immediately if the player binds or restores the default key in-game.
			if (keybindReady && OpenKeybind != null &&
				PlayerInput.Triggers.Current.KeyStatus.TryGetValue(OpenKeybindFullName, out bool current) &&
				PlayerInput.Triggers.Old.KeyStatus.TryGetValue(OpenKeybindFullName, out bool previous) &&
				current && !previous)
				Open();
		}

		public static void Open()
		{
			if (Main.gameMenu || !Main.LocalPlayer.active || Instance == null)
				return;

			// A fresh state avoids retaining a closed Fancy UI instance and stale row callbacks.
			SkinManagerUI managerUI = new();
			IngameFancyUI.OpenUIState(managerUI);
		}

		public override void Unload()
		{
			keybindReady = false;
			OpenKeybind = null;
			Instance = null;
		}
	}
}
