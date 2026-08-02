using System;
using System.Text;

namespace customskin.Common.Skins
{
	public static class SkinImportErrorPresentation
	{
		public static string GetHintKey(string? errorCode)
		{
			string prefix = (errorCode ?? string.Empty).Split('.', 2)[0];
			return prefix switch
			{
				"package" => "Mods.customskin.UI.ImportHints.Package",
				"manifest" => "Mods.customskin.UI.ImportHints.Manifest",
				"png" or "reference" => "Mods.customskin.UI.ImportHints.Image",
				"license" => "Mods.customskin.UI.ImportHints.License",
				"project" => "Mods.customskin.UI.ImportHints.Creator",
				"repository" or "selection" => "Mods.customskin.UI.ImportHints.Repository",
				_ => "Mods.customskin.UI.ImportHints.Unknown"
			};
		}

		public static string SanitizeForDisplay(string? value, int maximumCharacters = 360)
		{
			if (maximumCharacters < 2)
				throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

			StringBuilder result = new(Math.Min(value?.Length ?? 0, maximumCharacters));
			bool pendingSpace = false;
			foreach (char character in value ?? string.Empty)
			{
				if (char.IsControl(character) || char.IsWhiteSpace(character))
				{
					pendingSpace = result.Length > 0;
					continue;
				}
				if (pendingSpace && result.Length < maximumCharacters)
					result.Append(' ');
				pendingSpace = false;
				if (result.Length >= maximumCharacters)
					break;
				result.Append(character);
			}

			if (result.Length > maximumCharacters - 1)
			{
				result.Length = maximumCharacters - 1;
				result.Append('…');
			}
			return result.ToString();
		}
	}
}
