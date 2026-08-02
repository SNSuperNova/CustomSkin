CustomSkin native humanoid template and bundled example assets

The three equipment PNG files originated from tModLoader ExampleMod's
ExampleCostume sample. Terraria uses them as native head/body/legs templates;
at draw time CustomSkin replaces only their DrawData texture references with
the selected player's runtime RGBA textures.

BundledExample.cskin is a valid importable stage 1 package used by the in-game
"Install Example" action and by automated package validation tests.

Source of the original equipment layout:
https://github.com/tModLoader/tModLoader/tree/1.4.4/ExampleMod/Content/Items/Armor

The bundled package includes the tModLoader MIT license text and identifies its
example asset license as MIT.
