using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace customskin.Common.Skins
{
	public static partial class SkinManifestValidator
	{
		public const int CurrentSchemaVersion = 1;
		public const string CurrentProfile = "terraria-humanoid-v1";

		private static readonly string[] AllowedRootProperties =
		{
			"schemaVersion", "profile", "id", "name", "author", "version",
			"description", "license", "hide"
		};

		private static readonly string[] RequiredRootProperties =
		{
			"schemaVersion", "profile", "id", "name", "author", "version",
			"description", "hide"
		};

		private static readonly string[] HideProperties =
		{
			"hair", "headArmor", "bodyArmor", "legArmor", "faceAccessories",
			"bodyAccessories", "legAccessories", "wings"
		};

		private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			WriteIndented = false
		};

		private static readonly JsonSerializerOptions EditableJsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			WriteIndented = true
		};

		public static SkinManifest ParseAndNormalize(byte[] jsonBytes)
		{
			if (jsonBytes.Length == 0 || jsonBytes.Length > SkinPackageImporter.MaxManifestBytes)
				throw new SkinPackageException("manifest.size", "manifest.json is empty or too large.");

			try
			{
				using JsonDocument document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 8
				});

				JsonElement root = document.RootElement;
				ValidateObjectShape(root, AllowedRootProperties, RequiredRootProperties, "manifest");

				int schemaVersion = RequireInt(root, "schemaVersion");
				if (schemaVersion != CurrentSchemaVersion)
					throw new SkinPackageException("manifest.schema", $"Unsupported schemaVersion: {schemaVersion}.");

				string profile = RequireText(root, "profile", 32);
				if (!string.Equals(profile, CurrentProfile, StringComparison.Ordinal))
					throw new SkinPackageException("manifest.profile", $"Unsupported profile: {profile}.");

				string id = RequireText(root, "id", 96);
				if (!SkinIdRegex().IsMatch(id))
					throw new SkinPackageException("manifest.id", "id must use the lowercase author.slug format.");

				string version = RequireText(root, "version", 64);
				if (!SemVerRegex().IsMatch(version))
					throw new SkinPackageException("manifest.version", "version must be valid semantic version text.");

				JsonElement hideElement = root.GetProperty("hide");
				ValidateObjectShape(hideElement, HideProperties, HideProperties, "hide");

				return new SkinManifest
				{
					SchemaVersion = schemaVersion,
					Profile = profile,
					Id = id,
					Name = RequireText(root, "name", 80),
					Author = RequireText(root, "author", 64),
					Version = version,
					Description = RequireText(root, "description", 500, allowEmpty: true),
					License = root.TryGetProperty("license", out _) ? RequireText(root, "license", 64) : null,
					Hide = new SkinHideOptions
					{
						Hair = RequireBool(hideElement, "hair"),
						HeadArmor = RequireBool(hideElement, "headArmor"),
						BodyArmor = RequireBool(hideElement, "bodyArmor"),
						LegArmor = RequireBool(hideElement, "legArmor"),
						FaceAccessories = RequireBool(hideElement, "faceAccessories"),
						BodyAccessories = RequireBool(hideElement, "bodyAccessories"),
						LegAccessories = RequireBool(hideElement, "legAccessories"),
						Wings = RequireBool(hideElement, "wings")
					}
				};
			}
			catch (SkinPackageException)
			{
				throw;
			}
			catch (JsonException exception)
			{
				throw new SkinPackageException("manifest.json", "manifest.json is not valid strict JSON.", exception);
			}
		}

		public static byte[] SerializeCanonical(SkinManifest manifest)
			=> JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJsonOptions);

		public static byte[] SerializeEditable(SkinManifest manifest)
			=> JsonSerializer.SerializeToUtf8Bytes(manifest, EditableJsonOptions);

		private static void ValidateObjectShape(JsonElement element, IReadOnlyCollection<string> allowedProperties, IReadOnlyCollection<string> requiredProperties, string path)
		{
			if (element.ValueKind != JsonValueKind.Object)
				throw new SkinPackageException("manifest.type", $"{path} must be a JSON object.");

			HashSet<string> allowed = new(allowedProperties, StringComparer.Ordinal);
			HashSet<string> required = new(requiredProperties, StringComparer.Ordinal);
			HashSet<string> seen = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!allowed.Contains(property.Name))
					throw new SkinPackageException("manifest.property", $"Unknown property: {path}.{property.Name}.");
				if (!seen.Add(property.Name))
					throw new SkinPackageException("manifest.duplicate", $"Duplicate property: {path}.{property.Name}.");
			}

			foreach (string property in required)
			{
				if (!seen.Contains(property))
					throw new SkinPackageException("manifest.missing", $"Missing property: {path}.{property}.");
			}
		}

		private static int RequireInt(JsonElement parent, string name)
		{
			JsonElement element = parent.GetProperty(name);
			if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
				throw new SkinPackageException("manifest.type", $"{name} must be an integer.");
			return value;
		}

		private static bool RequireBool(JsonElement parent, string name)
		{
			JsonElement element = parent.GetProperty(name);
			if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
				throw new SkinPackageException("manifest.type", $"{name} must be a boolean.");
			return element.GetBoolean();
		}

		private static string RequireText(JsonElement parent, string name, int maximumLength, bool allowEmpty = false)
		{
			JsonElement element = parent.GetProperty(name);
			if (element.ValueKind != JsonValueKind.String)
				throw new SkinPackageException("manifest.type", $"{name} must be text.");

			string value = (element.GetString() ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
			if ((!allowEmpty && value.Length == 0) || value.Length > maximumLength)
				throw new SkinPackageException("manifest.length", $"{name} has an invalid length.");

			foreach (char character in value)
			{
				if (char.IsControl(character))
					throw new SkinPackageException("manifest.control", $"{name} contains a control character.");
			}

			return value;
		}

		[GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}\\.[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
		private static partial Regex SkinIdRegex();

		[GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
		private static partial Regex SemVerRegex();
	}
}
