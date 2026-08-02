namespace customskin.Common.Skins
{
	public sealed class SkinRecord
	{
		public required string Hash { get; init; }
		public required string DirectoryPath { get; init; }
		public required SkinManifest Manifest { get; init; }

		public string ShortHash => Hash[..8];
	}

	public readonly record struct SkinImportResult(string FileName, SkinRecord? Skin, bool WasNew, string? ErrorCode, string? ErrorMessage)
	{
		public bool Success => Skin != null;
	}
}
