using customskin.Common.Skins;
using customskin.Common.Networking;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

if (args is ["--validate-package", string packagePath])
{
	ValidatedSkinPackage package = SkinPackageImporter.ValidateAndRead(packagePath);
	Console.WriteLine($"VALID {package.Manifest.Id} {package.Manifest.Name}");
	return;
}

string root = Environment.GetEnvironmentVariable("CUSTOMSKIN_TEST_ROOT")
	?? throw new InvalidOperationException("CUSTOMSKIN_TEST_ROOT is required.");
string tempRoot = Path.Combine(Path.GetTempPath(), $"customskin-validation-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);

try
{
	byte[] icon = File.ReadAllBytes(Path.Combine(root, "Assets", "TestSkin", "Stage1ExampleIcon.png"));
	byte[] indexedIcon = WithIndexedColorType(icon);
	byte[] head = File.ReadAllBytes(Path.Combine(root, "Assets", "TestSkin", "Stage0TestSkin_Head.png"));
	byte[] body = File.ReadAllBytes(Path.Combine(root, "Assets", "TestSkin", "Stage0TestSkin_Body.png"));
	byte[] legs = File.ReadAllBytes(Path.Combine(root, "Assets", "TestSkin", "Stage0TestSkin_Legs.png"));
	byte[] manifest = Encoding.UTF8.GetBytes(ValidManifest());

	if (args is ["--emit-import-fixtures", string fixtureDirectory])
	{
		string output = Path.GetFullPath(fixtureDirectory);
		Directory.CreateDirectory(output);
		string missingBody = CreatePackage("INVALID_missing_body.cskin",
			StandardEntries(manifest, icon, head, body, legs).Where(entry => entry.Name != "textures/body.png"));
		string wrongHead = CreatePackage("INVALID_wrong_head_size.cskin",
			StandardEntries(manifest, icon, icon, body, legs));
		string badManifest = CreatePackage("INVALID_unknown_manifest_field.cskin",
			StandardEntries(Encoding.UTF8.GetBytes(ValidManifest().Replace("\"hide\":", "\"unexpected\":true,\"hide\":")), icon, head, body, legs));
		foreach (string source in new[] { missingBody, wrongHead, badManifest })
		{
			string destination = Path.Combine(output, Path.GetFileName(source));
			File.Copy(source, destination, overwrite: true);
			Console.WriteLine($"FIXTURE {destination}");
		}
		return;
	}

	List<(string Name, Action Test)> tests = new()
	{
		("bundled example package", () => ExpectValid(Path.Combine(root, "Assets", "TestSkin", "BundledExample.cskin"), "customskin.bundled_example")),
		("valid package", () => ExpectValid(CreatePackage("valid.cskin", StandardEntries(manifest, icon, head, body, legs)))),
		("wrong extension", () => ExpectError("package.extension", CreatePackage("wrong.zip", StandardEntries(manifest, icon, head, body, legs)))),
		("compressed size limit", () => ExpectError("package.size", WriteBytes("large.cskin", RandomBytes(SkinPackageImporter.MaxCompressedPackageBytes + 1)))),
		("missing file", () => ExpectError("package.missingFile", CreatePackage("missing.cskin", StandardEntries(manifest, icon, head, body, legs).Where(entry => entry.Name != "textures/body.png")), "textures/body.png")),
		("extra file", () => ExpectError("package.extra", CreatePackage("extra.cskin", StandardEntries(manifest, icon, head, body, legs).Append(("script.dll", new byte[] { 1 }))), "script.dll")),
		("path traversal", () => ExpectError("package.path", CreatePackage("traversal.cskin", StandardEntries(manifest, icon, head, body, legs).Prepend(("../manifest.json", manifest))))),
		("backslash path", () => ExpectError("package.path", CreatePackage("backslash.cskin", StandardEntries(manifest, icon, head, body, legs).Prepend(("textures\\head.png", head))))),
		("duplicate file", () => ExpectError("package.duplicate", CreatePackage("duplicate.cskin", StandardEntries(manifest, icon, head, body, legs).Append(("manifest.json", manifest))))),
		("symbolic link", () => ExpectError("package.symlink", CreatePackage("symlink.cskin", StandardEntries(manifest, icon, head, body, legs), symlinkEntry: "icon.png"))),
		("entry expansion limit", () => ExpectError("package.entrySize", CreatePackage("expansion.cskin", StandardEntries(manifest, icon, head, body, legs).Append(("LICENSE.txt", new byte[SkinPackageImporter.MaxLicenseBytes + 1]))))),
		("wrong image dimensions", () => ExpectError("png.dimensions", CreatePackage("dimensions.cskin", StandardEntries(manifest, icon, icon, body, legs)), "textures/head.png", "40x1120")),
		("indexed PNG rejected", () => ExpectError("png.format", CreatePackage("indexed.cskin", StandardEntries(manifest, indexedIcon, head, body, legs)))),
		("PNG CRC rejected", () => ExpectError("png.crc", CreatePackage("crc.cskin", StandardEntries(manifest, CorruptIdat(icon), head, body, legs)))),
		("APNG rejected", () => ExpectError("png.animated", CreatePackage("apng.cskin", StandardEntries(manifest, AddActl(icon), head, body, legs)))),
		("unknown manifest property", () => ExpectManifestError("manifest.property", ValidManifest().Replace("\"hide\":", "\"extra\":1,\"hide\":"), "manifest.extra")),
		("duplicate manifest property", () => ExpectManifestError("manifest.duplicate", ValidManifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"))),
		("optional manifest license", TestOptionalLicense),
		("wrong license type", () => ExpectManifestError("manifest.type", ValidManifest().Replace("\"license\":\"CC0-1.0\"", "\"license\":1"))),
		("missing manifest property", () => ExpectManifestError("manifest.missing", ValidManifest().Replace("\"author\":\"Author\",", string.Empty), "manifest.author")),
		("manifest comments rejected", () => ExpectManifestError("manifest.json", ValidManifest().Replace("{", "{/*comment*/", StringComparison.Ordinal))),
		("manifest trailing comma rejected", () => ExpectManifestError("manifest.json", ValidManifest().Replace("}}", "},}"))),
		("wrong schema", () => ExpectManifestError("manifest.schema", ValidManifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"))),
		("wrong profile", () => ExpectManifestError("manifest.profile", ValidManifest().Replace("terraria-humanoid-v1", "other"))),
		("invalid id", () => ExpectManifestError("manifest.id", ValidManifest().Replace("author.test_skin", "../test"))),
		("invalid version", () => ExpectManifestError("manifest.version", ValidManifest().Replace("1.0.0", "01.0"))),
		("control character", () => ExpectManifestError("manifest.json", ValidManifest().Replace("Test Skin", "Test\u0001Skin"))),
		("content hash stable", () => TestHashStable(manifest, icon, head, body, legs)),
		("content hash detects mutation", () => TestHashMutation(manifest, icon, head, body, legs)),
		("network resource round trip", TestNetworkResourceRoundTrip),
		("network resource detects mutation", TestNetworkResourceMutation),
		("network resource rejects truncated payload", TestNetworkResourceTruncated),
		("chunk transfer out of order", TestChunkTransferOutOfOrder),
		("chunk transfer rejects conflict", TestChunkTransferConflict),
		("chunk transfer rejects wrong length", TestChunkTransferWrongLength),
		("chunk transfer rejects incomplete payload", TestChunkTransferIncomplete),
		("remote skins disabled", TestRemoteSkinsDisabled),
		("blocked player name normalization", TestBlockedPlayerNameNormalization),
		("unblocked player name allowed", TestUnblockedPlayerNameAllowed),
		("server uploads disabled", TestServerUploadsDisabled),
		("server banned hash normalization", TestServerBannedHashNormalization),
		("server upload size limit", TestServerUploadSizeLimit),
		("server cache resource limit", TestServerCacheResourceLimit),
		("server cache byte limit", TestServerCacheByteLimit),
		("server upload cooldown boundary", TestServerUploadCooldownBoundary),
		("import error hint routing", TestImportErrorHintRouting),
		("import error display sanitization", TestImportErrorDisplaySanitization),
		("Chinese/English localization key and placeholder parity", TestLocalizationParity),
		("composite body atlas registration contract", TestCompositeBodyAtlasRegistration),
		("creator project deterministic export", TestCreatorProjectDeterministicExport),
		("creator project rejects wrong PNG", TestCreatorProjectRejectsWrongPng),
		("editor stroke undo redo", TestEditorStrokeUndoRedo),
		("editor gender-specific cells synchronize", TestEditorGenderSynchronization),
		("editor pose composition and mirror", TestEditorPoseCompositionAndMirror),
		("editor selected part composition", TestEditorSelectedPartComposition),
		("editor reference crop mirror and undo", TestEditorReferenceCropMirrorAndUndo),
		("editor reference overlay isolation", TestEditorReferenceOverlayIsolation),
		("editor shared slot reporting", TestEditorSharedSlotReporting),
		("editor undo history bounded", TestEditorUndoHistoryBounded),
		("editor PNG encoding", TestEditorPngEncoding)
	};

	int passed = 0;
	foreach ((string name, Action test) in tests)
	{
		try
		{
			test();
			Console.WriteLine($"PASS {name}");
			passed++;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
		}
	}

	Console.WriteLine($"{passed}/{tests.Count} validation tests passed.");
	if (passed != tests.Count)
		Environment.ExitCode = 1;
}
finally
{
	Directory.Delete(tempRoot, recursive: true);
}

void ExpectValid(string path, string expectedId = "author.test_skin")
{
	ValidatedSkinPackage package = SkinPackageImporter.ValidateAndRead(path);
	if (package.Manifest.Id != expectedId)
		throw new InvalidOperationException("Valid package returned unexpected manifest data.");
}

void ExpectError(string expectedCode, string path, params string[] expectedMessageFragments)
{
	try
	{
		SkinPackageImporter.ValidateAndRead(path);
		throw new InvalidOperationException($"Expected {expectedCode}, but validation succeeded.");
	}
	catch (SkinPackageException exception)
	{
		if (exception.ErrorCode != expectedCode)
			throw new InvalidOperationException($"Expected {expectedCode}, got {exception.ErrorCode}.");
		foreach (string fragment in expectedMessageFragments)
		{
			if (!exception.Message.Contains(fragment, StringComparison.Ordinal))
				throw new InvalidOperationException($"{expectedCode} did not identify expected detail: {fragment}.");
		}
	}
}

void ExpectManifestError(string expectedCode, string json, params string[] expectedMessageFragments)
{
	try
	{
		SkinManifestValidator.ParseAndNormalize(Encoding.UTF8.GetBytes(json));
		throw new InvalidOperationException($"Expected {expectedCode}, but manifest validation succeeded.");
	}
	catch (SkinPackageException exception)
	{
		if (exception.ErrorCode != expectedCode)
			throw new InvalidOperationException($"Expected {expectedCode}, got {exception.ErrorCode}.");
		foreach (string fragment in expectedMessageFragments)
		{
			if (!exception.Message.Contains(fragment, StringComparison.Ordinal))
				throw new InvalidOperationException($"{expectedCode} did not identify expected detail: {fragment}.");
		}
	}
}

void TestOptionalLicense()
{
	string json = ValidManifest().Replace(",\"license\":\"CC0-1.0\"", string.Empty);
	SkinManifest parsed = SkinManifestValidator.ParseAndNormalize(Encoding.UTF8.GetBytes(json));
	if (parsed.License != null || Encoding.UTF8.GetString(SkinManifestValidator.SerializeCanonical(parsed)).Contains("license", StringComparison.Ordinal))
		throw new InvalidOperationException("An omitted license was not preserved as optional.");
}

SkinNetworkResource CreateNetworkResource()
{
	SkinManifest parsed = SkinManifestValidator.ParseAndNormalize(Encoding.UTF8.GetBytes(ValidManifest()));
	byte[] canonical = SkinManifestValidator.SerializeCanonical(parsed);
	byte[] iconRgba = new byte[SkinNetworkResource.IconBytes];
	byte[] headRgba = new byte[SkinNetworkResource.HeadBytes];
	byte[] bodyRgba = new byte[SkinNetworkResource.BodyBytes];
	byte[] legsRgba = new byte[SkinNetworkResource.LegsBytes];
	for (int i = 0; i < iconRgba.Length; i += 4) iconRgba[i + 3] = 255;
	for (int i = 0; i < headRgba.Length; i += 4) headRgba[i + 3] = 255;
	for (int i = 0; i < bodyRgba.Length; i += 4) bodyRgba[i + 3] = 255;
	for (int i = 0; i < legsRgba.Length; i += 4) legsRgba[i + 3] = 255;
	byte[] license = Encoding.UTF8.GetBytes("CC0\n");
	string hash = SkinContentHasher.ComputeHash(canonical, iconRgba, headRgba, bodyRgba, legsRgba, license);
	return new SkinNetworkResource { Hash = hash, Manifest = parsed, ManifestJson = canonical, IconRgba = iconRgba, HeadRgba = headRgba, BodyRgba = bodyRgba, LegsRgba = legsRgba, LicenseText = license };
}

void TestNetworkResourceRoundTrip()
{
	SkinNetworkResource source = CreateNetworkResource();
	byte[] payload = source.Serialize();
	SkinNetworkResource result = SkinNetworkResource.DeserializeAndValidate(payload, source.Hash);
	if (result.Hash != source.Hash || !payload.SequenceEqual(result.Serialize()))
		throw new InvalidOperationException("Network resource did not round-trip canonically.");
}

void TestNetworkResourceMutation()
{
	SkinNetworkResource source = CreateNetworkResource();
	byte[] payload = source.Serialize();
	payload[^1] ^= 1;
	try
	{
		SkinNetworkResource.DeserializeAndValidate(payload, source.Hash);
		throw new InvalidOperationException("Mutated network resource was accepted.");
	}
	catch (SkinPackageException) { }
}

void TestNetworkResourceTruncated()
{
	SkinNetworkResource source = CreateNetworkResource();
	byte[] payload = source.Serialize();
	try
	{
		SkinNetworkResource.DeserializeAndValidate(payload[..^1], source.Hash);
		throw new InvalidOperationException("Truncated network resource was accepted.");
	}
	catch (SkinPackageException exception) when (exception.ErrorCode == "network.length") { }
}

void TestChunkTransferOutOfOrder()
{
	byte[] source = RandomBytes(ChunkTransfer.ChunkSize * 2 + 7);
	ChunkTransfer transfer = new(source.Length, 3, source.Length);
	transfer.AddChunk(2, source[^(7)..]);
	transfer.AddChunk(0, source[..ChunkTransfer.ChunkSize]);
	transfer.AddChunk(0, source[..ChunkTransfer.ChunkSize]);
	transfer.AddChunk(1, source[ChunkTransfer.ChunkSize..(ChunkTransfer.ChunkSize * 2)]);
	if (!transfer.GetPayload().SequenceEqual(source))
		throw new InvalidOperationException("Out-of-order chunks were not reassembled.");
}

void TestChunkTransferConflict()
{
	byte[] source = RandomBytes(10);
	ChunkTransfer transfer = new(source.Length, 1, source.Length);
	transfer.AddChunk(0, source);
	byte[] changed = (byte[])source.Clone();
	changed[0] ^= 1;
	try
	{
		transfer.AddChunk(0, changed);
		throw new InvalidOperationException("Conflicting duplicate chunk was accepted.");
	}
	catch (InvalidDataException) { }
}

void TestChunkTransferWrongLength()
{
	ChunkTransfer transfer = new(10, 1, 10);
	try
	{
		transfer.AddChunk(0, new byte[9]);
		throw new InvalidOperationException("Wrong-length chunk was accepted.");
	}
	catch (InvalidDataException) { }
}

void TestChunkTransferIncomplete()
{
	ChunkTransfer transfer = new(ChunkTransfer.ChunkSize + 1, 2, ChunkTransfer.ChunkSize + 1);
	transfer.AddChunk(0, new byte[ChunkTransfer.ChunkSize]);
	try
	{
		transfer.GetPayload();
		throw new InvalidOperationException("Incomplete transfer produced a payload.");
	}
	catch (InvalidOperationException exception) when (exception.Message == "Transfer is incomplete.") { }
}

void TestRemoteSkinsDisabled()
{
	if (RemoteSkinPrivacyPolicy.IsPlayerAllowed(false, "Player", Array.Empty<string>()))
		throw new InvalidOperationException("Remote skins were allowed while globally disabled.");
}

void TestBlockedPlayerNameNormalization()
{
	if (RemoteSkinPrivacyPolicy.IsPlayerAllowed(true, " Alice ", new[] { "  aLiCe  " }))
		throw new InvalidOperationException("A blocked player name ignored trim/case normalization.");
}

void TestUnblockedPlayerNameAllowed()
{
	if (!RemoteSkinPrivacyPolicy.IsPlayerAllowed(true, "Bob", new[] { "Alice" }))
		throw new InvalidOperationException("An unblocked player name was denied.");
}

void TestServerUploadsDisabled()
{
	SkinNetworkPolicy policy = CreatePolicy(allowUploads: false);
	if (policy.CanBeginUpload(new string('a', 64), 100))
		throw new InvalidOperationException("An upload was allowed while server uploads were disabled.");
}

void TestServerBannedHashNormalization()
{
	string hash = new('b', 64);
	SkinNetworkPolicy policy = CreatePolicy(bannedHashes: new[] { $"  {hash.ToUpperInvariant()}  " });
	if (!policy.IsHashBanned(hash) || policy.CanBeginUpload(hash, 100))
		throw new InvalidOperationException("A normalized banned hash was not rejected.");
}

void TestServerUploadSizeLimit()
{
	SkinNetworkPolicy policy = CreatePolicy(maxUploadBytes: 1000);
	string hash = new('a', 64);
	if (!policy.CanBeginUpload(hash, 1000) || policy.CanBeginUpload(hash, 1001))
		throw new InvalidOperationException("Upload size boundary was not enforced.");
}

void TestServerCacheResourceLimit()
{
	SkinNetworkPolicy policy = CreatePolicy(maxCachedResources: 2);
	string hash = new('a', 64);
	if (!policy.CanCache(hash, 100, 1, 100, alreadyCached: false) ||
		policy.CanCache(hash, 100, 2, 200, alreadyCached: false))
		throw new InvalidOperationException("Cache resource-count boundary was not enforced.");
}

void TestServerCacheByteLimit()
{
	SkinNetworkPolicy policy = CreatePolicy(maxCachedBytes: 1000);
	string hash = new('a', 64);
	if (!policy.CanCache(hash, 400, 0, 600, alreadyCached: false) ||
		policy.CanCache(hash, 401, 0, 600, alreadyCached: false))
		throw new InvalidOperationException("Cache byte boundary was not enforced.");
}

void TestServerUploadCooldownBoundary()
{
	SkinNetworkPolicy policy = CreatePolicy(uploadCooldown: TimeSpan.FromSeconds(5));
	DateTime now = new(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);
	if (policy.IsUploadCooldownElapsed(now, now.AddTicks(-TimeSpan.FromSeconds(5).Ticks + 1)) ||
		!policy.IsUploadCooldownElapsed(now, now.AddSeconds(-5)))
		throw new InvalidOperationException("Upload cooldown boundary was not enforced.");
}

SkinNetworkPolicy CreatePolicy(
	bool allowUploads = true,
	int maxUploadBytes = 1000,
	int maxCachedResources = 2,
	long maxCachedBytes = 1000,
	TimeSpan? uploadCooldown = null,
	IEnumerable<string>? bannedHashes = null)
	=> new(allowUploads, maxUploadBytes, maxCachedResources, maxCachedBytes,
		uploadCooldown ?? TimeSpan.FromSeconds(5), bannedHashes);

void TestImportErrorHintRouting()
{
	(string? Code, string Suffix)[] cases =
	{
		("package.missingFile", "Package"),
		("manifest.id", "Manifest"),
		("png.dimensions", "Image"),
		("reference.decode", "Image"),
		("license.utf8", "License"),
		("project.missingFile", "Creator"),
		("repository.import", "Repository"),
		(null, "Unknown")
	};
	foreach ((string? code, string suffix) in cases)
	{
		string key = SkinImportErrorPresentation.GetHintKey(code);
		if (!key.EndsWith(suffix, StringComparison.Ordinal))
			throw new InvalidOperationException($"Unexpected import hint for {code ?? "null"}: {key}");
	}
}

void TestImportErrorDisplaySanitization()
{
	string cleaned = SkinImportErrorPresentation.SanitizeForDisplay("  textures/body.png\r\n\tbad\u0001detail  ");
	if (cleaned != "textures/body.png bad detail")
		throw new InvalidOperationException($"Import error controls were not sanitized: {cleaned}");
	string truncated = SkinImportErrorPresentation.SanitizeForDisplay(new string('x', 10), 5);
	if (truncated != "xxxx…")
		throw new InvalidOperationException($"Import error text was not bounded: {truncated}");
}

void TestLocalizationParity()
{
	string localization = Path.Combine(root, "Localization");
	string baselinePath = Path.Combine(localization, "en-US_Mods.customskin.hjson");
	string[] baseline = LocalizationShape(baselinePath);
	string chinesePath = Path.Combine(localization, "zh-Hans_Mods.customskin.hjson");
	string[] chinese = LocalizationShape(chinesePath);
	if (!baseline.SequenceEqual(chinese, StringComparer.Ordinal))
		throw new InvalidOperationException("Simplified Chinese localization key/placeholder shape differs from English.");
}

void TestCompositeBodyAtlasRegistration()
{
	string templateSource = File.ReadAllText(Path.Combine(root, "Content", "Rendering", "SkinTemplateSystem.cs"), Encoding.UTF8);
	if (!templateSource.Contains("equipTexture: new SkinTemplateBodyEquipTexture()", StringComparison.Ordinal) ||
		!templateSource.Contains("drawData.texture = runtime.Body;", StringComparison.Ordinal))
		throw new InvalidOperationException("The 360x224 body atlas is not registered for Terraria composite framing.");

	string playerSource = File.ReadAllText(Path.Combine(root, "Common", "Players", "SkinPlayer.cs"), Encoding.UTF8);
	if (!playerSource.Contains("TextureAssets.ArmorBodyComposite[SkinTemplateSystem.BodySlot]", StringComparison.Ordinal))
		throw new InvalidOperationException("Runtime drawing no longer replaces the registered composite body texture.");
}

string[] LocalizationShape(string path)
{
	List<string> shape = new();
	bool inTriple = false;
	int current = -1;
	foreach (string line in File.ReadLines(path, Encoding.UTF8))
	{
		if (!inTriple)
		{
			System.Text.RegularExpressions.Match key = System.Text.RegularExpressions.Regex.Match(line, "^(\\s*)([A-Za-z][A-Za-z0-9]*):");
			if (key.Success)
			{
				current = shape.Count;
				shape.Add($"{key.Groups[1].Value.Length}:{key.Groups[2].Value}:");
			}
		}

		if (current >= 0)
		{
			foreach (System.Text.RegularExpressions.Match placeholder in System.Text.RegularExpressions.Regex.Matches(line, "\\{\\d+\\}"))
				shape[current] += placeholder.Value;
		}

		if (System.Text.RegularExpressions.Regex.Matches(line, "'''").Count % 2 == 1)
			inTriple = !inTriple;
	}
	return shape.Select(entry =>
	{
		string key = System.Text.RegularExpressions.Regex.Replace(entry, "\\{\\d+\\}", string.Empty);
		string placeholders = string.Concat(System.Text.RegularExpressions.Regex.Matches(entry, "\\{\\d+\\}")
			.Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal));
		return key + placeholders;
	}).ToArray();
}

void TestCreatorProjectDeterministicExport()
{
	string project = CreateCreatorProject("creator-valid");
	string references = Path.Combine(project, "references");
	Directory.CreateDirectory(references);
	File.Copy(Path.Combine(root, "Assets", "TestSkin", "Stage1ExampleIcon.png"), Path.Combine(references, "reference.png"));
	string first = SkinProjectPackage.Export(project, Path.Combine(tempRoot, "creator-first.cskin"));
	string second = SkinProjectPackage.Export(project, Path.Combine(tempRoot, "creator-second.cskin"));
	if (!File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)))
		throw new InvalidOperationException("Equivalent creator project exports were not byte-identical.");
	ValidatedSkinPackage package = SkinPackageImporter.ValidateAndRead(first);
	if (package.Manifest.Id != "author.test_skin")
		throw new InvalidOperationException("Creator export manifest changed unexpectedly.");
	using FileStream stream = File.OpenRead(first);
	using ZipArchive archive = new(stream, ZipArchiveMode.Read);
	if (archive.Entries.Any(entry => entry.FullName.StartsWith("references/", StringComparison.Ordinal)))
		throw new InvalidOperationException("Creator export leaked the private reference PNG into the sharing package.");
}

void TestCreatorProjectRejectsWrongPng()
{
	string project = CreateCreatorProject("creator-wrong-png");
	File.Copy(Path.Combine(root, "Assets", "TestSkin", "Stage1ExampleIcon.png"), Path.Combine(project, "textures", "head.png"), overwrite: true);
	try
	{
		SkinProjectPackage.Export(project, Path.Combine(tempRoot, "creator-invalid.cskin"));
		throw new InvalidOperationException("Creator project with wrong head dimensions was accepted.");
	}
	catch (SkinPackageException exception) when (exception.ErrorCode == "png.dimensions")
	{
	}
}

void TestEditorStrokeUndoRedo()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	SkinEditorColor red = new(220, 30, 40, 255);
	editor.BeginStroke(SkinEditorPart.Head, idle, SkinEditorGender.Male);
	if (!editor.ApplyPixel(2, 3, red) || !editor.CommitStroke())
		throw new InvalidOperationException("Editor did not record a changed pixel stroke.");
	if (!editor.IsDirty || editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 2, 3) != red)
		throw new InvalidOperationException("Editor stroke did not update the selected cell.");
	if (!editor.Undo() || editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 2, 3).A != 0)
		throw new InvalidOperationException("Editor undo did not restore the original pixel.");
	if (!editor.Redo() || editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 2, 3) != red)
		throw new InvalidOperationException("Editor redo did not restore the stroke.");
	editor.MarkSaved();
	if (editor.IsDirty)
		throw new InvalidOperationException("Editor remained dirty after marking the current revision saved.");
}

void TestEditorGenderSynchronization()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	SkinEditorColor edit = new(40, 220, 100, 255);

	editor.BeginStroke(SkinEditorPart.Torso, idle, SkinEditorGender.Male);
	editor.ApplyPixel(3, 3, edit);
	editor.CommitStroke();

	if (editor.ReadTargetPixel(SkinEditorPart.Torso, idle, SkinEditorGender.Male, 3, 3) != edit ||
		editor.ReadTargetPixel(SkinEditorPart.Torso, idle, SkinEditorGender.Female, 3, 3) != edit)
		throw new InvalidOperationException("Gender-specific torso cells did not synchronize from the selected canonical cell.");

	if (!editor.Undo() ||
		editor.ReadTargetPixel(SkinEditorPart.Torso, idle, SkinEditorGender.Male, 3, 3).A != 0 ||
		editor.ReadTargetPixel(SkinEditorPart.Torso, idle, SkinEditorGender.Female, 3, 3).A != 0)
		throw new InvalidOperationException("Synchronized gender-cell edits were not undone as one stroke.");
}

void TestEditorPoseCompositionAndMirror()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	SkinEditorColor blue = new(20, 80, 230, 255);
	editor.BeginStroke(SkinEditorPart.Legs, idle, SkinEditorGender.Male);
	editor.ApplyPixel(1, 10, blue);
	editor.CommitStroke();
	byte[] normal = editor.Compose(idle, SkinEditorGender.Male, mirror: false);
	byte[] mirror = editor.Compose(idle, SkinEditorGender.Male, mirror: true);
	if (ReadEditorPixel(normal, 1, 10) != blue)
		throw new InvalidOperationException("Editor composition omitted the selected runtime cell.");
	if (ReadEditorPixel(mirror, SkinEditorDocument.CellWidth - 2, 10) != blue)
		throw new InvalidOperationException("Editor mirror preview did not flip the composite pixel.");
}

void TestEditorSelectedPartComposition()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	SkinEditorColor headColor = new(230, 40, 50, 255);
	SkinEditorColor legsColor = new(20, 90, 220, 255);
	editor.BeginStroke(SkinEditorPart.Head, idle, SkinEditorGender.Male);
	editor.ApplyPixel(2, 3, headColor);
	editor.CommitStroke();
	editor.BeginStroke(SkinEditorPart.Legs, idle, SkinEditorGender.Male);
	editor.ApplyPixel(4, 5, legsColor);
	editor.CommitStroke();

	byte[] headOnly = editor.ComposeSelectedPart(SkinEditorPart.Head, idle, SkinEditorGender.Male, mirror: false);
	if (ReadEditorPixel(headOnly, 2, 3) != headColor || ReadEditorPixel(headOnly, 4, 5).A != 0)
		throw new InvalidOperationException("Selected-part composition included another component or omitted the selected component.");
	byte[] mirroredLegs = editor.ComposeSelectedPart(SkinEditorPart.Legs, idle, SkinEditorGender.Male, mirror: true);
	if (ReadEditorPixel(mirroredLegs, SkinEditorDocument.CellWidth - 5, 5) != legsColor || ReadEditorPixel(mirroredLegs, 2, 3).A != 0)
		throw new InvalidOperationException("Selected-part composition did not preserve mirror behavior or isolation.");
}

void TestEditorReferenceCropMirrorAndUndo()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	byte[] source = new byte[4 * 3 * 4];
	WriteReferencePixel(source, 4, 1, 1, new SkinEditorColor(220, 30, 40, 255));
	WriteReferencePixel(source, 4, 3, 2, new SkinEditorColor(20, 180, 90, 200));
	SkinReferenceImage reference = new(4, 3, source);

	if (!editor.ApplyReference(SkinEditorPart.Head, idle, SkinEditorGender.Male, reference,
		offsetX: 5, offsetY: 7, referenceMirror: false, previewMirror: false, replace: false))
		throw new InvalidOperationException("Reference overlay did not create an undoable stroke.");
	if (editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 6, 8) != new SkinEditorColor(220, 30, 40, 255) ||
		editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 8, 9) != new SkinEditorColor(20, 180, 90, 200))
		throw new InvalidOperationException("Reference offset/crop mapping was incorrect.");
	if (editor.UndoStrokeCount != 1 || !editor.Undo() || editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 6, 8).A != 0)
		throw new InvalidOperationException("Reference overlay was not reverted as one undo operation.");

	if (!editor.ApplyReference(SkinEditorPart.Head, idle, SkinEditorGender.Male, reference,
		offsetX: 5, offsetY: 7, referenceMirror: true, previewMirror: true, replace: false))
		throw new InvalidOperationException("Mirrored reference overlay did not change pixels.");
	// Source x=3 mirrors to visual x=5, then preview mirroring writes target x=34.
	if (editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 34, 9) != new SkinEditorColor(20, 180, 90, 200))
		throw new InvalidOperationException("Reference-image mirror and preview mirror were not applied in the correct coordinate spaces.");
}

void TestEditorReferenceOverlayIsolation()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	SkinEditorColor original = new(40, 80, 220, 255);
	editor.BeginStroke(SkinEditorPart.Head, idle, SkinEditorGender.Male);
	editor.ApplyPixel(1, 1, original);
	editor.ApplyPixel(2, 2, original);
	editor.CommitStroke();
	editor.MarkSaved();

	byte[] source = new byte[2 * 2 * 4];
	SkinEditorColor replacement = new(230, 160, 20, 255);
	WriteReferencePixel(source, 2, 0, 0, replacement);
	SkinReferenceImage reference = new(2, 2, source);
	if (!editor.ApplyReference(SkinEditorPart.Head, idle, SkinEditorGender.Male, reference,
		offsetX: 1, offsetY: 1, referenceMirror: false, previewMirror: false, replace: false))
		throw new InvalidOperationException("Reference overlay did not change the selected target.");
	if (editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 1, 1) != replacement ||
		editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 2, 2) != original)
		throw new InvalidOperationException("Overlay did not preserve pixels beneath transparent reference areas.");
	if (editor.ReadTargetPixel(SkinEditorPart.Legs, idle, SkinEditorGender.Male, 1, 1).A != 0)
		throw new InvalidOperationException("Reference overlay escaped the selected component.");

	if (!editor.ApplyReference(SkinEditorPart.Head, idle, SkinEditorGender.Male, reference,
		offsetX: 1, offsetY: 1, referenceMirror: false, previewMirror: false, replace: true))
		throw new InvalidOperationException("Reference replacement did not change the selected target.");
	if (editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 1, 1) != replacement ||
		editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 2, 2).A != 0)
		throw new InvalidOperationException("Replacement did not clear transparent areas in the selected component.");
	if (editor.ApplyReference(SkinEditorPart.Head, idle, SkinEditorGender.Male, reference,
		offsetX: 200, offsetY: 200, referenceMirror: false, previewMirror: false, replace: true) ||
		editor.ReadTargetPixel(SkinEditorPart.Head, idle, SkinEditorGender.Male, 1, 1) != replacement)
		throw new InvalidOperationException("An empty cropped reference area cleared the selected component.");
}

void TestEditorSharedSlotReporting()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose action = SkinEditorDocument.Poses.Single(pose => pose.Name == "Body Action 2");
	string legsSummary = editor.GetAffectedPoseSummary(SkinEditorPart.Legs, action, SkinEditorGender.Male);
	if (!legsSummary.Contains("Idle", StringComparison.Ordinal) || !legsSummary.Contains("Body Action 4", StringComparison.Ordinal))
		throw new InvalidOperationException("Editor did not report poses sharing legs slot 00.");
	SkinEditorPose move = SkinEditorDocument.Poses.Single(pose => pose.Name == "Ground Move 05");
	string armSummary = editor.GetAffectedPoseSummary(SkinEditorPart.FrontArm, move, SkinEditorGender.Male);
	if (!armSummary.Contains("Ground Move 06", StringComparison.Ordinal) || !armSummary.Contains("Jump/Air 2", StringComparison.Ordinal))
		throw new InvalidOperationException("Editor did not report poses sharing a composite arm cell.");
}

void TestEditorUndoHistoryBounded()
{
	SkinEditorDocument editor = CreateEmptyEditor();
	SkinEditorPose idle = SkinEditorDocument.Poses[0];
	for (int stroke = 0; stroke < SkinEditorDocument.MaxUndoStrokes + 20; stroke++)
	{
		editor.BeginStroke(SkinEditorPart.Head, idle, SkinEditorGender.Male);
		editor.ApplyPixel(0, 0, new SkinEditorColor((byte)(stroke + 1), 0, 0, 255));
		editor.CommitStroke();
	}
	if (editor.UndoStrokeCount > SkinEditorDocument.MaxUndoStrokes || editor.UndoBytes > SkinEditorDocument.MaxUndoBytes)
		throw new InvalidOperationException("Editor undo history exceeded its configured limits.");
}

void TestEditorPngEncoding()
{
	byte[] rgba = new byte[4 * 3 * 4];
	for (int index = 0; index < rgba.Length; index++)
		rgba[index] = (byte)(index * 17);
	byte[] first = RgbaPngEncoder.Encode(rgba, 4, 3);
	byte[] second = RgbaPngEncoder.Encode(rgba, 4, 3);
	PngInspector.ValidateRgba(first, 4, 3, "editor.png");
	if (!first.SequenceEqual(second))
		throw new InvalidOperationException("Editor PNG encoding was not deterministic.");
	if (!DecodeEditorPngPayload(first, 4, 3).SequenceEqual(rgba))
		throw new InvalidOperationException("Editor PNG encoding did not preserve RGBA pixels.");
}

SkinEditorDocument CreateEmptyEditor()
	=> new(new byte[SkinEditorDocument.HeadWidth * SkinEditorDocument.HeadHeight * 4],
		new byte[SkinEditorDocument.BodyWidth * SkinEditorDocument.BodyHeight * 4],
		new byte[SkinEditorDocument.LegsWidth * SkinEditorDocument.LegsHeight * 4]);

void WriteReferencePixel(byte[] rgba, int width, int x, int y, SkinEditorColor color)
{
	int offset = (y * width + x) * 4;
	rgba[offset] = color.R;
	rgba[offset + 1] = color.G;
	rgba[offset + 2] = color.B;
	rgba[offset + 3] = color.A;
}

SkinEditorColor ReadEditorPixel(byte[] rgba, int x, int y)
{
	int offset = (y * SkinEditorDocument.CellWidth + x) * 4;
	return new SkinEditorColor(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
}

byte[] DecodeEditorPngPayload(byte[] png, int width, int height)
{
	using MemoryStream compressed = new();
	int offset = 8;
	while (offset < png.Length)
	{
		int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
		string type = Encoding.ASCII.GetString(png, offset + 4, 4);
		if (type == "IDAT") compressed.Write(png, offset + 8, length);
		offset += length + 12;
		if (type == "IEND") break;
	}
	compressed.Position = 0;
	using ZLibStream zlib = new(compressed, CompressionMode.Decompress);
	using MemoryStream scanlines = new();
	zlib.CopyTo(scanlines);
	byte[] filtered = scanlines.ToArray();
	int stride = width * 4;
	if (filtered.Length != (stride + 1) * height)
		throw new InvalidOperationException("Editor PNG scanline length was invalid.");
	byte[] rgba = new byte[stride * height];
	for (int y = 0; y < height; y++)
	{
		if (filtered[y * (stride + 1)] != 0)
			throw new InvalidOperationException("Editor PNG used an unexpected scanline filter.");
		Buffer.BlockCopy(filtered, y * (stride + 1) + 1, rgba, y * stride, stride);
	}
	return rgba;
}

byte[] WithIndexedColorType(byte[] rgbaPng)
{
	byte[] indexed = (byte[])rgbaPng.Clone();
	// PNG signature (8), IHDR length/type (8), then bit depth and color type at data offsets 8/9.
	indexed[25] = 3;
	uint crc = Crc32.Compute(indexed.AsSpan(12, 4), indexed.AsSpan(16, 13));
	BinaryPrimitives.WriteUInt32BigEndian(indexed.AsSpan(29, 4), crc);
	return indexed;
}

string CreateCreatorProject(string name)
{
	string project = Path.Combine(tempRoot, name);
	Directory.CreateDirectory(Path.Combine(project, "textures"));
	File.WriteAllText(Path.Combine(project, "manifest.json"), ValidManifest(), new UTF8Encoding(false));
	File.Copy(Path.Combine(root, "Assets", "TestSkin", "Stage1ExampleIcon.png"), Path.Combine(project, "icon.png"));
	File.Copy(Path.Combine(root, "Assets", "TestSkin", "Stage0TestSkin_Head.png"), Path.Combine(project, "textures", "head.png"));
	File.Copy(Path.Combine(root, "Assets", "TestSkin", "Stage0TestSkin_Body.png"), Path.Combine(project, "textures", "body.png"));
	File.Copy(Path.Combine(root, "Assets", "TestSkin", "Stage0TestSkin_Legs.png"), Path.Combine(project, "textures", "legs.png"));
	return project;
}

void TestHashStable(byte[] manifestBytes, byte[] iconBytes, byte[] headBytes, byte[] bodyBytes, byte[] legsBytes)
{
	string first = SkinContentHasher.ComputeHash(manifestBytes, iconBytes, headBytes, bodyBytes, legsBytes, null);
	string second = SkinContentHasher.ComputeHash((byte[])manifestBytes.Clone(), (byte[])iconBytes.Clone(), (byte[])headBytes.Clone(), (byte[])bodyBytes.Clone(), (byte[])legsBytes.Clone(), null);
	if (first != second || first.Length != 64)
		throw new InvalidOperationException("Equivalent normalized content did not produce a stable SHA-256 hash.");
}

void TestHashMutation(byte[] manifestBytes, byte[] iconBytes, byte[] headBytes, byte[] bodyBytes, byte[] legsBytes)
{
	byte[] changedHead = (byte[])headBytes.Clone();
	changedHead[^1] ^= 1;
	string first = SkinContentHasher.ComputeHash(manifestBytes, iconBytes, headBytes, bodyBytes, legsBytes, null);
	string second = SkinContentHasher.ComputeHash(manifestBytes, iconBytes, changedHead, bodyBytes, legsBytes, null);
	if (first == second)
		throw new InvalidOperationException("A pixel mutation did not change the normalized content hash.");
}

string CreatePackage(string name, IEnumerable<(string Name, byte[] Data)> entries, string? symlinkEntry = null)
{
	string path = Path.Combine(tempRoot, name);
	using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
	using ZipArchive archive = new(file, ZipArchiveMode.Create);
	foreach ((string entryName, byte[] data) in entries)
	{
		ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
		if (entryName == symlinkEntry)
			entry.ExternalAttributes = 0xa000 << 16;
		using Stream output = entry.Open();
		output.Write(data);
	}
	return path;
}

string WriteBytes(string name, byte[] data)
{
	string path = Path.Combine(tempRoot, name);
	File.WriteAllBytes(path, data);
	return path;
}

static byte[] RandomBytes(int length)
{
	byte[] data = new byte[length];
	new Random(12345).NextBytes(data);
	return data;
}

static IEnumerable<(string Name, byte[] Data)> StandardEntries(byte[] manifest, byte[] icon, byte[] head, byte[] body, byte[] legs)
{
	yield return ("manifest.json", manifest);
	yield return ("icon.png", icon);
	yield return ("textures/head.png", head);
	yield return ("textures/body.png", body);
	yield return ("textures/legs.png", legs);
}

static byte[] CorruptIdat(byte[] png)
{
	byte[] result = (byte[])png.Clone();
	int offset = 8;
	while (offset < result.Length)
	{
		int length = BinaryPrimitives.ReadInt32BigEndian(result.AsSpan(offset, 4));
		string type = Encoding.ASCII.GetString(result, offset + 4, 4);
		if (type == "IDAT" && length > 0)
		{
			result[offset + 8] ^= 1;
			return result;
		}
		offset += 12 + length;
	}
	throw new InvalidOperationException("IDAT not found.");
}

static byte[] AddActl(byte[] png)
{
	int insertAt = 8 + 12 + 13;
	byte[] data = new byte[8];
	BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 1);
	BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 0);
	byte[] type = Encoding.ASCII.GetBytes("acTL");
	byte[] chunk = new byte[20];
	BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(0, 4), data.Length);
	type.CopyTo(chunk, 4);
	data.CopyTo(chunk, 8);
	BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16, 4), Crc32.Compute(type, data));
	byte[] result = new byte[png.Length + chunk.Length];
	png.AsSpan(0, insertAt).CopyTo(result);
	chunk.CopyTo(result, insertAt);
	png.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
	return result;
}

static string ValidManifest() => """
{"schemaVersion":1,"profile":"terraria-humanoid-v1","id":"author.test_skin","name":"Test Skin","author":"Author","version":"1.0.0","description":"Validation test","license":"CC0-1.0","hide":{"hair":true,"headArmor":true,"bodyArmor":true,"legArmor":true,"faceAccessories":false,"bodyAccessories":false,"legAccessories":false,"wings":false}}
""";
