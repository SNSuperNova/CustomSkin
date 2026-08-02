using customskin.Common.Skins;
using System;
using Terraria.Localization;

namespace customskin.Common.UI
{
	internal static class SkinErrorText
	{
		public static string LocalizedDetail(string? errorCode, string? fallback)
		{
			string prefix = (errorCode ?? string.Empty).Split('.', 2)[0];
			string category = prefix switch
			{
				"package" => "Package",
				"manifest" => "Manifest",
				"png" or "reference" => "Image",
				"license" => "License",
				"project" => "Project",
				"repository" => "Repository",
				"selection" => "Selection",
				"editor" => "Editor",
				"network" => "Network",
				_ => "Unknown"
			};
			string key = $"Mods.customskin.Errors.{category}";
			string localized = Language.GetTextValue(key);
			return localized == key
				? SkinImportErrorPresentation.SanitizeForDisplay(fallback ?? string.Empty)
				: localized;
		}

		public static string FormatOperationFailure(Exception exception)
		{
			string code = exception is SkinPackageException package ? package.ErrorCode : "unknown";
			return Language.GetTextValue("Mods.customskin.UI.OperationFailed",
				SkinImportErrorPresentation.SanitizeForDisplay(code, 80),
				LocalizedDetail(code, exception.Message));
		}
	}
}
