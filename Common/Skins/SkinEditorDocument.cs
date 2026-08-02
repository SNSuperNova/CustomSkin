using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace customskin.Common.Skins
{
	public enum SkinEditorPart
	{
		Head,
		Torso,
		FrontShoulder,
		FrontArm,
		BackShoulder,
		BackArm,
		Legs
	}

	public enum SkinEditorGender
	{
		Male,
		Female
	}

	public enum SkinEditorTool
	{
		Pencil,
		Eraser,
		Eyedropper,
		RectangleMove,
		RectangleCopy
	}

	public readonly record struct SkinEditorColor(byte R, byte G, byte B, byte A)
	{
		public static readonly SkinEditorColor Transparent = new(0, 0, 0, 0);
	}

	public sealed class SkinReferenceImage
	{
		private readonly byte[] rgba;

		public SkinReferenceImage(int width, int height, byte[] rgba)
		{
			if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
			if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
			ArgumentNullException.ThrowIfNull(rgba);
			if (rgba.Length != checked(width * height * 4))
				throw new ArgumentException("Reference image RGBA length does not match its dimensions.", nameof(rgba));
			Width = width;
			Height = height;
			this.rgba = (byte[])rgba.Clone();
		}

		public int Width { get; }
		public int Height { get; }

		public bool TryReadCanvasPixel(int canvasX, int canvasY, int offsetX, int offsetY, bool mirror, out SkinEditorColor color)
		{
			int sourceX = canvasX - offsetX;
			int sourceY = canvasY - offsetY;
			if (mirror)
				sourceX = Width - 1 - sourceX;
			if ((uint)sourceX >= (uint)Width || (uint)sourceY >= (uint)Height)
			{
				color = SkinEditorColor.Transparent;
				return false;
			}

			int offset = (sourceY * Width + sourceX) * 4;
			color = new SkinEditorColor(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
			return true;
		}
	}

	public sealed record SkinEditorPose(
		string Name,
		string State,
		int BodyFrame,
		int LegFrame,
		int? ExplicitFrontArm = null,
		int? ExplicitBackArm = null);

	/// <summary>
	/// CPU-side, profile-specific editing model. It deliberately knows only the
	/// frozen terraria-humanoid-v1 layout; package validation and persistence stay
	/// in SkinCreatorSystem/SkinProjectPackage.
	/// </summary>
	public sealed class SkinEditorDocument
	{
		public const int CellWidth = 40;
		public const int CellHeight = 56;
		public const int HeadWidth = 40;
		public const int HeadHeight = 1120;
		public const int BodyWidth = 360;
		public const int BodyHeight = 224;
		public const int LegsWidth = 40;
		public const int LegsHeight = 1120;
		public const int MaxUndoStrokes = 128;
		public const int MaxUndoBytes = 4 * 1024 * 1024;

		private static readonly int[] FrontArmByFrame = { 2, 3, 4, 5, 6, 11, 12, 13, 13, 13, 13, 12, 12, 12, 14, 15, 15, 14, 12, 12 };
		private static readonly int[] BackArmByFrame = { 20, 21, 22, 23, 24, 29, 30, 31, 31, 31, 31, 30, 30, 30, 32, 33, 33, 32, 30, 30 };
		private static readonly bool[] ShoulderOverFrontArm = { true, false, false, true, true, false, true, true, true, true, true, true, true, true, true, true, true, true, true, true };
		private static readonly ReadOnlyCollection<SkinEditorPose> PoseList = BuildPoses().AsReadOnly();

		private readonly byte[] head;
		private readonly byte[] body;
		private readonly byte[] legs;
		private readonly List<Stroke> undo = new();
		private readonly List<Stroke> redo = new();
		private StrokeBuilder? activeStroke;
		private Target[] activeTargets = Array.Empty<Target>();
		private int undoBytes;
		private long nextStateId;
		private long currentStateId;
		private long savedStateId;
		private long contentRevision;

		public SkinEditorDocument(byte[] headRgba, byte[] bodyRgba, byte[] legsRgba, string? sourceFingerprint = null)
		{
			ValidateAtlas(headRgba, HeadWidth, HeadHeight, nameof(headRgba));
			ValidateAtlas(bodyRgba, BodyWidth, BodyHeight, nameof(bodyRgba));
			ValidateAtlas(legsRgba, LegsWidth, LegsHeight, nameof(legsRgba));
			head = (byte[])headRgba.Clone();
			body = (byte[])bodyRgba.Clone();
			legs = (byte[])legsRgba.Clone();
			SourceFingerprint = sourceFingerprint;
		}

		public static IReadOnlyList<SkinEditorPose> Poses => PoseList;
		public bool IsDirty => currentStateId != savedStateId;
		public bool CanUndo => undo.Count > 0;
		public bool CanRedo => redo.Count > 0;
		public int UndoStrokeCount => undo.Count;
		public int UndoBytes => undoBytes;
		public bool HasActiveStroke => activeStroke != null;
		public string? SourceFingerprint { get; private set; }
		public long ContentRevision => contentRevision;

		public byte[] CopyHeadRgba() => (byte[])head.Clone();
		public byte[] CopyBodyRgba() => (byte[])body.Clone();
		public byte[] CopyLegsRgba() => (byte[])legs.Clone();

		public void BeginStroke(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender)
		{
			CommitStroke();
			Target target = ResolveTarget(part, pose, gender);
			activeStroke = new StrokeBuilder(target.Atlas, target.Slot);
			Target counterpart = ResolveTarget(part, pose,
				gender == SkinEditorGender.Male ? SkinEditorGender.Female : SkinEditorGender.Male);
			activeTargets = counterpart == target ? new[] { target } : new[] { target, counterpart };

			// CustomSkin treats appearance as skin-owned rather than character-gender-owned.
			// When Terraria has separate male/female composite cells, make the selected
			// cell canonical for this stroke and synchronize the entire counterpart cell.
			if (counterpart != target)
			{
				byte[] atlas = GetAtlas(target.Atlas);
				(int width, int sourceX, int sourceY) = GetCellLocation(target.Atlas, target.Slot);
				(_, int targetX, int targetY) = GetCellLocation(counterpart.Atlas, counterpart.Slot);
				for (int y = 0; y < CellHeight; y++)
				{
					for (int x = 0; x < CellWidth; x++)
					{
						int sourceOffset = ((sourceY + y) * width + sourceX + x) * 4;
						int targetOffset = ((targetY + y) * width + targetX + x) * 4;
						SkinEditorColor sourceColor = ReadColor(atlas, sourceOffset);
						SkinEditorColor targetColor = ReadColor(atlas, targetOffset);
						if (sourceColor == targetColor)
							continue;
						activeStroke.Record(targetOffset, targetColor, sourceColor);
						WriteColor(atlas, targetOffset, sourceColor);
						contentRevision++;
					}
				}
			}
		}

		public bool ApplyPixel(int x, int y, SkinEditorColor color)
		{
			if (activeStroke == null)
				throw new InvalidOperationException("BeginStroke must be called before applying pixels.");
			if ((uint)x >= CellWidth || (uint)y >= CellHeight)
				return false;

			bool changed = false;
			foreach (Target target in activeTargets)
			{
				byte[] atlas = GetAtlas(target.Atlas);
				(int atlasWidth, int originX, int originY) = GetCellLocation(target.Atlas, target.Slot);
				int offset = ((originY + y) * atlasWidth + originX + x) * 4;
				SkinEditorColor before = ReadColor(atlas, offset);
				if (before == color)
					continue;

				activeStroke.Record(offset, before, color);
				WriteColor(atlas, offset, color);
				contentRevision++;
				changed = true;
			}
			return changed;
		}

		public SkinEditorColor ReadTargetPixel(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender, int x, int y)
		{
			if ((uint)x >= CellWidth || (uint)y >= CellHeight)
				return SkinEditorColor.Transparent;
			Target target = ResolveTarget(part, pose, gender);
			byte[] atlas = GetAtlas(target.Atlas);
			(int atlasWidth, int originX, int originY) = GetCellLocation(target.Atlas, target.Slot);
			return ReadColor(atlas, ((originY + y) * atlasWidth + originX + x) * 4);
		}

		public bool CopyPartToPose(SkinEditorPart part, SkinEditorPose sourcePose, SkinEditorPose targetPose, SkinEditorGender gender)
		{
			Target source = ResolveTarget(part, sourcePose, gender);
			Target target = ResolveTarget(part, targetPose, gender);
			if (source == target)
				return false;
			if (source.Atlas != target.Atlas)
				throw new InvalidOperationException("A skin part cannot change atlas between poses.");

			SkinEditorColor[] sourcePixels = new SkinEditorColor[CellWidth * CellHeight];
			byte[] atlas = GetAtlas(source.Atlas);
			(int atlasWidth, int sourceX, int sourceY) = GetCellLocation(source.Atlas, source.Slot);
			for (int y = 0; y < CellHeight; y++)
			{
				for (int x = 0; x < CellWidth; x++)
					sourcePixels[y * CellWidth + x] = ReadColor(atlas, ((sourceY + y) * atlasWidth + sourceX + x) * 4);
			}

			BeginStroke(part, targetPose, gender);
			for (int y = 0; y < CellHeight; y++)
			{
				for (int x = 0; x < CellWidth; x++)
					ApplyPixel(x, y, sourcePixels[y * CellWidth + x]);
			}
			return CommitStroke();
		}

		public bool ApplyReference(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender,
			SkinReferenceImage reference, int offsetX, int offsetY, bool referenceMirror, bool previewMirror, bool replace)
		{
			ArgumentNullException.ThrowIfNull(reference);
			bool hasVisiblePixels = false;
			for (int y = 0; y < CellHeight && !hasVisiblePixels; y++)
			{
				for (int x = 0; x < CellWidth; x++)
				{
					if (reference.TryReadCanvasPixel(x, y, offsetX, offsetY, referenceMirror, out SkinEditorColor source) && source.A > 0)
					{
						hasVisiblePixels = true;
						break;
					}
				}
			}
			if (!hasVisiblePixels)
				return false;

			BeginStroke(part, pose, gender);
			for (int visualY = 0; visualY < CellHeight; visualY++)
			{
				for (int visualX = 0; visualX < CellWidth; visualX++)
				{
					int targetX = previewMirror ? CellWidth - 1 - visualX : visualX;
					if (replace)
						ApplyPixel(targetX, visualY, SkinEditorColor.Transparent);

					if (reference.TryReadCanvasPixel(visualX, visualY, offsetX, offsetY, referenceMirror, out SkinEditorColor source) && source.A > 0)
						ApplyPixel(targetX, visualY, source);
				}
			}
			return CommitStroke();
		}

		public bool ApplyFloatingSelection(SkinEditorPart part, SkinEditorPose sourcePose, SkinEditorPose targetPose,
			SkinEditorGender gender, int sourceX, int sourceY, int width, int height,
			IReadOnlyList<SkinEditorColor> pixels, int destinationX, int destinationY,
			bool sourceMirror, bool targetMirror, bool move)
		{
			ArgumentNullException.ThrowIfNull(pixels);
			if (width <= 0 || height <= 0 || pixels.Count != checked(width * height))
				throw new ArgumentException("Floating selection dimensions do not match its pixels.", nameof(pixels));

			CommitStroke();
			Target source = ResolveTarget(part, sourcePose, gender);
			Target target = ResolveTarget(part, targetPose, gender);
			if (source.Atlas != target.Atlas)
				throw new InvalidOperationException("A floating selection cannot change texture atlases.");
			activeStroke = new StrokeBuilder(source.Atlas, source.Slot);
			activeTargets = Array.Empty<Target>();

			Target sourceCounterpart = ResolveTarget(part, sourcePose,
				gender == SkinEditorGender.Male ? SkinEditorGender.Female : SkinEditorGender.Male);
			Target targetCounterpart = ResolveTarget(part, targetPose,
				gender == SkinEditorGender.Male ? SkinEditorGender.Female : SkinEditorGender.Male);
			Target[] sourceTargets = sourceCounterpart == source ? new[] { source } : new[] { source, sourceCounterpart };
			Target[] targetTargets = targetCounterpart == target ? new[] { target } : new[] { target, targetCounterpart };

			if (move)
			{
				foreach (Target sourceTarget in sourceTargets.Distinct())
				{
					for (int y = 0; y < height; y++)
					{
						for (int x = 0; x < width; x++)
						{
							SkinEditorColor selected = pixels[y * width + x];
							if (selected.A == 0) continue;
							int visualX = sourceX + x;
							int atlasX = sourceMirror ? CellWidth - 1 - visualX : visualX;
							ApplyRawPixel(sourceTarget, atlasX, sourceY + y, SkinEditorColor.Transparent);
						}
					}
				}
			}

			foreach (Target targetCell in targetTargets.Distinct())
			{
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						SkinEditorColor selected = pixels[y * width + x];
						if (selected.A == 0) continue;
						int visualX = destinationX + x;
						int atlasX = targetMirror ? CellWidth - 1 - visualX : visualX;
						ApplyRawPixel(targetCell, atlasX, destinationY + y, selected);
					}
				}
			}
			return CommitStroke();
		}

		public bool CommitStroke()
		{
			if (activeStroke == null)
				return false;
			Stroke? stroke = activeStroke.Build(currentStateId, ++nextStateId);
			activeStroke = null;
			activeTargets = Array.Empty<Target>();
			if (stroke == null)
				return false;

			redo.Clear();
			undo.Add(stroke);
			undoBytes += stroke.EstimatedBytes;
			currentStateId = stroke.AfterStateId;
			TrimUndoHistory();
			return true;
		}

		public bool Undo()
		{
			CommitStroke();
			if (undo.Count == 0)
				return false;
			Stroke stroke = undo[^1];
			undo.RemoveAt(undo.Count - 1);
			undoBytes -= stroke.EstimatedBytes;
			ApplyChanges(stroke, useAfter: false);
			contentRevision++;
			redo.Add(stroke);
			currentStateId = stroke.BeforeStateId;
			return true;
		}

		public bool Redo()
		{
			CommitStroke();
			if (redo.Count == 0)
				return false;
			Stroke stroke = redo[^1];
			redo.RemoveAt(redo.Count - 1);
			ApplyChanges(stroke, useAfter: true);
			contentRevision++;
			undo.Add(stroke);
			undoBytes += stroke.EstimatedBytes;
			currentStateId = stroke.AfterStateId;
			TrimUndoHistory();
			return true;
		}

		public void MarkSaved()
		{
			CommitStroke();
			savedStateId = currentStateId;
		}

		public void SetSourceFingerprint(string fingerprint)
			=> SourceFingerprint = string.IsNullOrWhiteSpace(fingerprint) ? throw new ArgumentException("Fingerprint is required.", nameof(fingerprint)) : fingerprint;

		public int GetSlot(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender)
			=> ResolveTarget(part, pose, gender).Slot;

		internal static int GetAtlasSlot(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender)
			=> ResolveTarget(part, pose, gender).Slot;

		internal static bool IsShoulderOverFrontArm(SkinEditorPose pose)
		{
			if ((uint)pose.BodyFrame >= ShoulderOverFrontArm.Length)
				throw new ArgumentOutOfRangeException(nameof(pose));
			return ShoulderOverFrontArm[pose.BodyFrame];
		}

		public string GetAffectedPoseSummary(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender)
		{
			return string.Join(", ", GetAffectedPoses(part, pose, gender).Select(candidate => candidate.Name));
		}

		public IReadOnlyList<SkinEditorPose> GetAffectedPoses(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender)
		{
			int slot = GetSlot(part, pose, gender);
			return PoseList
				.Where(candidate => GetSlot(part, candidate, gender) == slot)
				.Distinct()
				.ToArray();
		}

		public byte[] Compose(SkinEditorPose pose, SkinEditorGender gender, bool mirror)
		{
			byte[] output = new byte[CellWidth * CellHeight * 4];
			CompositeCell(output, AtlasKind.Legs, pose.LegFrame);
			CompositeCell(output, AtlasKind.Body, gender == SkinEditorGender.Male ? 10 : 28);
			CompositeCell(output, AtlasKind.Body, pose.ExplicitBackArm ?? BackArmByFrame[pose.BodyFrame]);
			int torso = gender == SkinEditorGender.Male
				? (pose.BodyFrame == 5 ? 1 : 0)
				: (pose.BodyFrame == 5 ? 19 : 18);
			CompositeCell(output, AtlasKind.Body, torso);
			CompositeCell(output, AtlasKind.Head, pose.BodyFrame);

			int frontArm = pose.ExplicitFrontArm ?? FrontArmByFrame[pose.BodyFrame];
			int frontShoulder = gender == SkinEditorGender.Male ? 9 : 27;
			if (pose.ExplicitFrontArm != null || ShoulderOverFrontArm[pose.BodyFrame])
			{
				CompositeCell(output, AtlasKind.Body, frontArm);
				CompositeCell(output, AtlasKind.Body, frontShoulder);
			}
			else
			{
				CompositeCell(output, AtlasKind.Body, frontShoulder);
				CompositeCell(output, AtlasKind.Body, frontArm);
			}

			if (mirror)
				MirrorInPlace(output);
			return output;
		}

		public byte[] ComposeSelectedPart(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender, bool mirror)
		{
			byte[] output = new byte[CellWidth * CellHeight * 4];
			Target target = ResolveTarget(part, pose, gender);
			CompositeCell(output, target.Atlas, target.Slot);
			if (mirror)
				MirrorInPlace(output);
			return output;
		}

		public IReadOnlyList<SkinEditorColor> ExtractPalette(int maximumColors = 12)
		{
			Dictionary<SkinEditorColor, int> counts = new();
			CountColors(head, counts);
			CountColors(body, counts);
			CountColors(legs, counts);
			return counts.Where(pair => pair.Key.A > 0)
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key.R).ThenBy(pair => pair.Key.G).ThenBy(pair => pair.Key.B)
				.Take(Math.Max(1, maximumColors))
				.Select(pair => pair.Key)
				.ToArray();
		}

		private static List<SkinEditorPose> BuildPoses()
		{
			List<SkinEditorPose> poses = new()
			{
				new("Idle", "idle", 0, 0),
				new("Body Action 1", "body-action", 1, 0),
				new("Body Action 2", "body-action", 2, 0),
				new("Body Action 3", "body-action", 3, 0),
				new("Body Action 4", "body-action", 4, 0),
				new("Jump/Air 1", "jump-air", 5, 5),
				new("Jump/Air 2", "jump-air", 6, 5)
			};
			for (int frame = 7; frame <= 19; frame++)
				poses.Add(new SkinEditorPose($"Ground Move {frame - 6:00}", "ground-move", frame, frame));
			poses.Add(new SkinEditorPose("Weapon Arm Full", "weapon-arm", 0, 0, 7, 8));
			poses.Add(new SkinEditorPose("Weapon Arm 3/4", "weapon-arm", 0, 0, 16, 17));
			poses.Add(new SkinEditorPose("Weapon Arm 1/4", "weapon-arm", 0, 0, 25, 26));
			poses.Add(new SkinEditorPose("Weapon Arm None", "weapon-arm", 0, 0, 34, 35));
			foreach (int slot in new[] { 1, 2, 3, 4, 6 })
				poses.Add(new SkinEditorPose($"Native Legs {slot:00}", "native-special", 0, slot));
			return poses;
		}

		private static Target ResolveTarget(SkinEditorPart part, SkinEditorPose pose, SkinEditorGender gender)
		{
			if ((uint)pose.BodyFrame >= 20 || (uint)pose.LegFrame >= 20)
				throw new ArgumentOutOfRangeException(nameof(pose));
			return part switch
			{
				SkinEditorPart.Head => new(AtlasKind.Head, pose.BodyFrame),
				SkinEditorPart.Legs => new(AtlasKind.Legs, pose.LegFrame),
				SkinEditorPart.Torso => new(AtlasKind.Body, gender == SkinEditorGender.Male
					? (pose.BodyFrame == 5 ? 1 : 0)
					: (pose.BodyFrame == 5 ? 19 : 18)),
				SkinEditorPart.FrontShoulder => new(AtlasKind.Body, gender == SkinEditorGender.Male ? 9 : 27),
				SkinEditorPart.BackShoulder => new(AtlasKind.Body, gender == SkinEditorGender.Male ? 10 : 28),
				SkinEditorPart.FrontArm => new(AtlasKind.Body, pose.ExplicitFrontArm ?? FrontArmByFrame[pose.BodyFrame]),
				SkinEditorPart.BackArm => new(AtlasKind.Body, pose.ExplicitBackArm ?? BackArmByFrame[pose.BodyFrame]),
				_ => throw new ArgumentOutOfRangeException(nameof(part))
			};
		}

		private void CompositeCell(byte[] output, AtlasKind kind, int slot)
		{
			byte[] atlas = GetAtlas(kind);
			(int width, int originX, int originY) = GetCellLocation(kind, slot);
			for (int y = 0; y < CellHeight; y++)
			{
				for (int x = 0; x < CellWidth; x++)
				{
					int source = ((originY + y) * width + originX + x) * 4;
					int destination = (y * CellWidth + x) * 4;
					AlphaComposite(output, destination, atlas, source);
				}
			}
		}

		private static void AlphaComposite(byte[] destination, int d, byte[] source, int s)
		{
			int sourceAlpha = source[s + 3];
			if (sourceAlpha == 0)
				return;
			if (sourceAlpha == 255)
			{
				destination[d] = source[s];
				destination[d + 1] = source[s + 1];
				destination[d + 2] = source[s + 2];
				destination[d + 3] = 255;
				return;
			}

			int destinationAlpha = destination[d + 3];
			int inverse = 255 - sourceAlpha;
			int outputAlpha = sourceAlpha + (destinationAlpha * inverse + 127) / 255;
			if (outputAlpha == 0)
				return;
			for (int channel = 0; channel < 3; channel++)
			{
				int premultiplied = source[s + channel] * sourceAlpha +
					destination[d + channel] * destinationAlpha * inverse / 255;
				destination[d + channel] = (byte)((premultiplied + outputAlpha / 2) / outputAlpha);
			}
			destination[d + 3] = (byte)outputAlpha;
		}

		private static void MirrorInPlace(byte[] pixels)
		{
			for (int y = 0; y < CellHeight; y++)
			{
				for (int x = 0; x < CellWidth / 2; x++)
				{
					int left = (y * CellWidth + x) * 4;
					int right = (y * CellWidth + (CellWidth - 1 - x)) * 4;
					for (int channel = 0; channel < 4; channel++)
					{
						byte temporary = pixels[left + channel];
						pixels[left + channel] = pixels[right + channel];
						pixels[right + channel] = temporary;
					}
				}
			}
		}

		private void ApplyChanges(Stroke stroke, bool useAfter)
		{
			byte[] atlas = GetAtlas(stroke.Atlas);
			foreach (PixelChange change in stroke.Changes)
				WriteColor(atlas, change.Offset, useAfter ? change.After : change.Before);
		}

		private void TrimUndoHistory()
		{
			while (undo.Count > MaxUndoStrokes || undoBytes > MaxUndoBytes)
			{
				undoBytes -= undo[0].EstimatedBytes;
				undo.RemoveAt(0);
			}
		}

		private byte[] GetAtlas(AtlasKind kind) => kind switch
		{
			AtlasKind.Head => head,
			AtlasKind.Body => body,
			AtlasKind.Legs => legs,
			_ => throw new ArgumentOutOfRangeException(nameof(kind))
		};

		private static (int Width, int X, int Y) GetCellLocation(AtlasKind kind, int slot)
		{
			return kind switch
			{
				AtlasKind.Head when (uint)slot < 20 => (HeadWidth, 0, slot * CellHeight),
				AtlasKind.Legs when (uint)slot < 20 => (LegsWidth, 0, slot * CellHeight),
				AtlasKind.Body when (uint)slot < 36 => (BodyWidth, (slot % 9) * CellWidth, (slot / 9) * CellHeight),
				_ => throw new ArgumentOutOfRangeException(nameof(slot))
			};
		}

		private static SkinEditorColor ReadColor(byte[] atlas, int offset)
			=> new(atlas[offset], atlas[offset + 1], atlas[offset + 2], atlas[offset + 3]);

		private static void WriteColor(byte[] atlas, int offset, SkinEditorColor color)
		{
			atlas[offset] = color.R;
			atlas[offset + 1] = color.G;
			atlas[offset + 2] = color.B;
			atlas[offset + 3] = color.A;
		}

		private bool ApplyRawPixel(Target target, int x, int y, SkinEditorColor color)
		{
			if (activeStroke == null)
				throw new InvalidOperationException("A floating-selection stroke is not active.");
			if ((uint)x >= CellWidth || (uint)y >= CellHeight)
				return false;
			byte[] atlas = GetAtlas(target.Atlas);
			(int atlasWidth, int originX, int originY) = GetCellLocation(target.Atlas, target.Slot);
			int offset = ((originY + y) * atlasWidth + originX + x) * 4;
			SkinEditorColor before = ReadColor(atlas, offset);
			if (before == color)
				return false;
			activeStroke.Record(offset, before, color);
			WriteColor(atlas, offset, color);
			contentRevision++;
			return true;
		}

		private static void ValidateAtlas(byte[] pixels, int width, int height, string name)
		{
			ArgumentNullException.ThrowIfNull(pixels);
			if (pixels.Length != checked(width * height * 4))
				throw new ArgumentException($"{name} has an invalid RGBA byte length.", name);
		}

		private static void CountColors(byte[] atlas, Dictionary<SkinEditorColor, int> counts)
		{
			for (int offset = 0; offset < atlas.Length; offset += 4)
			{
				SkinEditorColor color = ReadColor(atlas, offset);
				counts[color] = counts.GetValueOrDefault(color) + 1;
			}
		}

		private enum AtlasKind { Head, Body, Legs }
		private readonly record struct Target(AtlasKind Atlas, int Slot);
		private readonly record struct PixelChange(int Offset, SkinEditorColor Before, SkinEditorColor After);

		private sealed class Stroke
		{
			public required AtlasKind Atlas { get; init; }
			public required PixelChange[] Changes { get; init; }
			public required long BeforeStateId { get; init; }
			public required long AfterStateId { get; init; }
			public int EstimatedBytes => checked(Changes.Length * 16 + 64);
		}

		private sealed class StrokeBuilder
		{
			private readonly Dictionary<int, PixelChange> changes = new();
			public StrokeBuilder(AtlasKind atlas, int slot) { Atlas = atlas; Slot = slot; }
			public AtlasKind Atlas { get; }
			public int Slot { get; }

			public void Record(int offset, SkinEditorColor before, SkinEditorColor after)
			{
				if (changes.TryGetValue(offset, out PixelChange existing))
				{
					if (existing.Before == after)
						changes.Remove(offset);
					else
						changes[offset] = existing with { After = after };
				}
				else
				{
					changes[offset] = new PixelChange(offset, before, after);
				}
			}

			public Stroke? Build(long beforeStateId, long afterStateId)
			{
				if (changes.Count == 0)
					return null;
				return new Stroke
				{
					Atlas = Atlas,
					Changes = changes.Values.OrderBy(change => change.Offset).ToArray(),
					BeforeStateId = beforeStateId,
					AfterStateId = afterStateId
				};
			}
		}
	}

	public static class RgbaPngEncoder
	{
		private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

		public static byte[] Encode(byte[] rgba, int width, int height)
		{
			ArgumentNullException.ThrowIfNull(rgba);
			if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
				throw new ArgumentException("RGBA dimensions do not match the byte buffer.", nameof(rgba));

			using MemoryStream result = new();
			result.Write(Signature);
			byte[] header = new byte[13];
			WriteBigEndian(header, 0, width);
			WriteBigEndian(header, 4, height);
			header[8] = 8;
			header[9] = 6;
			WriteChunk(result, "IHDR", header);

			using MemoryStream compressed = new();
			using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
			{
				int stride = checked(width * 4);
				for (int y = 0; y < height; y++)
				{
					zlib.WriteByte(0);
					zlib.Write(rgba, y * stride, stride);
				}
			}
			WriteChunk(result, "IDAT", compressed.ToArray());
			WriteChunk(result, "IEND", Array.Empty<byte>());
			return result.ToArray();
		}

		private static void WriteChunk(Stream output, string type, byte[] data)
		{
			byte[] typeBytes = Encoding.ASCII.GetBytes(type);
			byte[] length = new byte[4];
			WriteBigEndian(length, 0, data.Length);
			output.Write(length);
			output.Write(typeBytes);
			output.Write(data);
			byte[] crc = new byte[4];
			WriteBigEndian(crc, 0, unchecked((int)Crc32.Compute(typeBytes, data)));
			output.Write(crc);
		}

		private static void WriteBigEndian(byte[] bytes, int offset, int value)
		{
			bytes[offset] = (byte)(value >> 24);
			bytes[offset + 1] = (byte)(value >> 16);
			bytes[offset + 2] = (byte)(value >> 8);
			bytes[offset + 3] = (byte)value;
		}
	}
}
