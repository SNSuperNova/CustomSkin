namespace customskin.Common.Skins
{
	public sealed class SkinManifest
	{
		public int SchemaVersion { get; set; }
		public string Profile { get; set; } = string.Empty;
		public string Id { get; set; } = string.Empty;
		public string Name { get; set; } = string.Empty;
		public string Author { get; set; } = string.Empty;
		public string Version { get; set; } = string.Empty;
		public string Description { get; set; } = string.Empty;
		public string? License { get; set; }
		public SkinHideOptions Hide { get; set; } = new();
	}

	public sealed class SkinHideOptions
	{
		public bool Hair { get; set; }
		public bool HeadArmor { get; set; }
		public bool BodyArmor { get; set; }
		public bool LegArmor { get; set; }
		public bool FaceAccessories { get; set; }
		public bool BodyAccessories { get; set; }
		public bool LegAccessories { get; set; }
		public bool Wings { get; set; }
	}
}
