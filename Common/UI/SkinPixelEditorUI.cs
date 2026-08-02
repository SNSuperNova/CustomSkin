using customskin.Common.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.UI;
using Terraria.Utilities.FileBrowser;

namespace customskin.Common.UI
{
	internal sealed class SkinPixelEditorUI : UIState
	{
		private readonly SkinCreatorProject project;
		private readonly SkinEditorDocument document;
		private readonly SkinEditorSettings settings;
		private SkinEditorPart activePart;
		private SkinEditorCanvas canvas = null!;
		private UIText poseText = null!;
		private UIText slotText = null!;
		private UIText affectedText = null!;
		private UIText colorText = null!;
		private UIText historyText = null!;
		private UIText statusText = null!;
		private UIElement partHost = null!;
		private UIElement toolHost = null!;
		private UIElement paletteHost = null!;
		private UITextPanel<string> onlyPartButton = null!;
		private UIText referenceText = null!;
		private UITextPanel<string> referenceVisibleButton = null!;
		private UITextPanel<string> referenceOverlayButton = null!;
		private UITextPanel<string> referenceReplaceButton = null!;
		private SkinReferenceImage? referenceImage;
		private readonly List<SkinEditorColor> recentColors = new();
		private bool playing;
		private int playbackTicks;
		private bool pendingDiscard;
		private int referenceMoveRepeatTicks;
		private KeyboardState previousKeys;

		private SkinCreatorSystem Creator => ModContent.GetInstance<SkinCreatorSystem>();
		private SkinEditorPose Pose => SkinEditorDocument.Poses[settings.PoseIndex];

		public SkinPixelEditorUI(SkinCreatorProject project, SkinEditorDocument document, SkinEditorSettings settings)
		{
			this.project = project;
			this.document = document;
			this.settings = settings;
			// Terraria stores a few composite body pieces twice for character gender.
			// The editor exposes one skin-owned appearance and synchronizes both cells.
			settings.Gender = SkinEditorGender.Male;
			activePart = Enum.IsDefined(settings.Part) ? settings.Part : SkinEditorPart.Head;
			settings.Part = activePart;
		}

		public override void OnInitialize()
		{
			UIPanel panel = new()
			{
				HAlign = 0.5f,
				VAlign = 0.5f,
				Width = new StyleDimension(-24f, 0.98f),
				Height = new StyleDimension(-18f, 0.98f),
				BackgroundColor = new Color(29, 39, 76, 248)
			};
			Append(panel);

			UIText title = new(Language.GetTextValue("Mods.customskin.UI.PixelEditorTitle", project.DisplayName), 1.05f)
			{
				HAlign = 0.5f,
				Top = new StyleDimension(6f, 0f)
			};
			panel.Append(title);

			canvas = new SkinEditorCanvas(document, settings, () => activePart, () => Pose, () => referenceImage,
				() => settings.GetReferenceFrame(settings.PoseIndex), OnCanvasChanged, PickColor)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(48f, 0f),
				Width = new StyleDimension(-18f, 0.48f),
				Height = new StyleDimension(-128f, 1f)
			};
			panel.Append(canvas);

			UIElement controls = new()
			{
				Left = new StyleDimension(12f, 0.49f),
				Top = new StyleDimension(48f, 0f),
				Width = new StyleDimension(-30f, 0.51f),
				Height = new StyleDimension(-128f, 1f)
			};
			panel.Append(controls);

			UITextPanel<string> previous = PercentButton("◀", 0f, 0.07f, 0f);
			previous.OnLeftClick += (_, _) => ChangePose(-1);
			controls.Append(previous);
			UITextPanel<string> play = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorPlayPause"), 0.08f, 0.17f, 0f);
			play.OnLeftClick += (_, _) => { document.CommitStroke(); playing = !playing; playbackTicks = 0; RefreshLabels(); };
			controls.Append(play);
			UITextPanel<string> next = PercentButton("▶", 0.26f, 0.07f, 0f);
			next.OnLeftClick += (_, _) => ChangePose(1);
			controls.Append(next);
			UITextPanel<string> mirror = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorMirror"), 0.34f, 0.20f, 0f);
			mirror.OnLeftClick += (_, _) => { EndStroke(); settings.MirrorPreview = !settings.MirrorPreview; RefreshLabels(); };
			controls.Append(mirror);
			onlyPartButton = StyledPercentButton(Language.GetTextValue("Mods.customskin.UI.EditorOnlyPart"), 0.55f, 0.25f, 0f, settings.OnlySelectedPart);
			onlyPartButton.OnLeftClick += (_, _) =>
			{
				EndStroke();
				settings.OnlySelectedPart = !settings.OnlySelectedPart;
				ApplySelectedButtonStyle(onlyPartButton, settings.OnlySelectedPart);
			};
			controls.Append(onlyPartButton);

			poseText = Label(0f, 40f, 0.82f);
			controls.Append(poseText);
			slotText = Label(0f, 64f, 0.7f);
			controls.Append(slotText);
				affectedText = Label(0f, 88f, 0.70f, 54f);
			controls.Append(affectedText);

			partHost = new UIElement { Top = new StyleDimension(146f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(70f, 0f) };
			controls.Append(partHost);
			BuildPartButtons();

			toolHost = new UIElement { Top = new StyleDimension(220f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(34f, 0f) };
			controls.Append(toolHost);
			BuildToolButtons();

			paletteHost = new UIElement { Top = new StyleDimension(260f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(34f, 0f) };
			controls.Append(paletteHost);
			BuildPaletteButtons();

			colorText = Label(0f, 300f, 0.7f);
			controls.Append(colorText);
			SkinEditorColorPalette colorPalette = new(() => settings.Color, SetColor)
			{
				Top = new StyleDimension(326f, 0f),
				Width = new StyleDimension(0f, 1f),
				Height = new StyleDimension(76f, 0f)
			};
			controls.Append(colorPalette);

			UITextPanel<string> grid = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorGrid"), 0f, 0.20f, 410f);
			grid.OnLeftClick += (_, _) => { settings.ShowGrid = !settings.ShowGrid; RefreshLabels(); };
			controls.Append(grid);
			UIText zoomHint = new(Language.GetTextValue("Mods.customskin.UI.EditorZoomHint"), 0.62f)
			{
				Left = new StyleDimension(0f, 0.21f),
				Top = new StyleDimension(418f, 0f),
				Width = new StyleDimension(0f, 0.21f),
				Height = new StyleDimension(24f, 0f),
				TextOriginX = 0.5f
			};
			controls.Append(zoomHint);
			UITextPanel<string> undo = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorUndo"), 0.43f, 0.24f, 410f);
			undo.OnLeftClick += (_, _) => Undo();
			controls.Append(undo);
			UITextPanel<string> redo = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorRedo"), 0.68f, 0.24f, 410f);
			redo.OnLeftClick += (_, _) => Redo();
			controls.Append(redo);

			historyText = Label(0f, 448f, 0.66f);
			controls.Append(historyText);

			referenceText = Label(0f, 478f, 0.66f);
			controls.Append(referenceText);
			BuildReferenceControls(controls, 510f);

			float bottom = -72f;
			UITextPanel<string> save = PercentBottomButton(panel, Language.GetTextValue("Mods.customskin.UI.EditorSave"), 0.02f, 0.16f, bottom);
			save.OnLeftClick += (_, _) => Save(apply: false);
			UITextPanel<string> saveUse = PercentBottomButton(panel, Language.GetTextValue("Mods.customskin.UI.EditorSaveUse"), 0.19f, 0.20f, bottom);
			saveUse.OnLeftClick += (_, _) => Save(apply: true);
			UITextPanel<string> export = PercentBottomButton(panel, Language.GetTextValue("Mods.customskin.UI.ExportPackage"), 0.40f, 0.18f, bottom);
			export.OnLeftClick += (_, _) => Export();
			UITextPanel<string> reload = PercentBottomButton(panel, Language.GetTextValue("Mods.customskin.UI.EditorReload"), 0.59f, 0.17f, bottom);
			reload.OnLeftClick += (_, _) => Reload();
			UITextPanel<string> back = PercentBottomButton(panel, Language.GetTextValue("UI.Back"), 0.77f, 0.15f, bottom);
			back.OnLeftClick += (_, _) => Back();

			statusText = new UIText(string.Empty, 0.65f)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(-30f, 1f),
				Width = new StyleDimension(-36f, 1f),
				IsWrapped = true,
				TextOriginX = 0f
			};
			panel.Append(statusText);
			ReloadReference(showStatus: false);
			RefreshLabels();
		}

		public override void OnActivate()
		{
			previousKeys = Main.keyState;
			SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorHint"), Color.Silver);
		}

		public override void OnDeactivate()
		{
			EndStroke();
			try { Creator.WriteEditorSettings(project, settings); }
			catch (Exception exception) when (exception is not OutOfMemoryException) { ModContent.GetInstance<CustomSkinMod>().Logger.Warn($"Could not save editor state: {exception.Message}"); }
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			Main.LocalPlayer.mouseInterface = true;
			HandleShortcuts();
			if (playing)
			{
				playbackTicks++;
				if (playbackTicks >= 8)
				{
					playbackTicks = 0;
					settings.PoseIndex = (settings.PoseIndex + 1) % 20;
					RefreshLabels();
				}
			}
		}

		private void HandleShortcuts()
		{
			KeyboardState keys = Main.keyState;
			bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
			bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
			if (control && Pressed(keys, Keys.Z)) Undo();
			if (control && Pressed(keys, Keys.Y)) Redo();
			if (control && Pressed(keys, Keys.S)) Save(apply: false);
			if (!control && referenceImage != null)
				HandleReferenceMovement(keys, shift ? 5 : 1);
			else
				referenceMoveRepeatTicks = 0;
			previousKeys = keys;
		}

		private void HandleReferenceMovement(KeyboardState keys, int step)
		{
			Keys? held = keys.IsKeyDown(Keys.Left) ? Keys.Left :
				keys.IsKeyDown(Keys.Right) ? Keys.Right :
				keys.IsKeyDown(Keys.Up) ? Keys.Up :
				keys.IsKeyDown(Keys.Down) ? Keys.Down : null;
			if (held == null)
			{
				referenceMoveRepeatTicks = 0;
				return;
			}

			bool first = Pressed(keys, held.Value);
			bool repeat = referenceMoveRepeatTicks >= 15 && (referenceMoveRepeatTicks - 15) % 3 == 0;
			if (first || repeat)
			{
				switch (held.Value)
				{
					case Keys.Left: MoveReference(-step, 0); break;
					case Keys.Right: MoveReference(step, 0); break;
					case Keys.Up: MoveReference(0, -step); break;
					case Keys.Down: MoveReference(0, step); break;
				}
			}
			referenceMoveRepeatTicks++;
		}

		private bool Pressed(KeyboardState current, Keys key)
			=> current.IsKeyDown(key) && !previousKeys.IsKeyDown(key);

		private void ChangePose(int delta)
		{
			EndStroke();
			playing = false;
			settings.PoseIndex = (settings.PoseIndex + delta + SkinEditorDocument.Poses.Count) % SkinEditorDocument.Poses.Count;
			pendingDiscard = false;
			RefreshLabels();
		}

		private void BuildPartButtons()
		{
			partHost.RemoveAllChildren();
			SkinEditorPart[] parts = Enum.GetValues<SkinEditorPart>();
			for (int index = 0; index < parts.Length; index++)
			{
				SkinEditorPart part = parts[index];
				bool selected = part == activePart;
				float left = (index % 4) * 0.25f;
				float top = (index / 4) * 38f;
				UITextPanel<string> button = StyledPercentButton(PartName(part), left, 0.24f, top, selected);
				button.OnLeftClick += (_, _) => SelectPart(part);
				partHost.Append(button);
			}
			partHost.Recalculate();
		}

		private void SelectPart(SkinEditorPart part)
		{
			EndStroke();
			activePart = part;
			settings.Part = part;
			playing = false;
			BuildPartButtons();
			RefreshLabels();
		}

		private void BuildToolButtons()
		{
			toolHost.RemoveAllChildren();
			SkinEditorTool[] tools = Enum.GetValues<SkinEditorTool>();
			for (int index = 0; index < tools.Length; index++)
			{
				SkinEditorTool tool = tools[index];
				bool selected = tool == settings.Tool;
				UITextPanel<string> button = StyledPercentButton(ToolName(tool), index * 0.33f, 0.31f, 0f, selected);
				button.OnLeftClick += (_, _) => { EndStroke(); settings.Tool = tool; BuildToolButtons(); RefreshLabels(); };
				toolHost.Append(button);
			}
			toolHost.Recalculate();
		}

		private void BuildPaletteButtons()
		{
			paletteHost.RemoveAllChildren();
			IReadOnlyList<SkinEditorColor> palette = recentColors
				.Concat(document.ExtractPalette(10))
				.Where(color => color.A > 0)
				.Distinct()
				.Take(10)
				.ToArray();
			for (int index = 0; index < palette.Count; index++)
			{
				SkinEditorColor color = palette[index];
				UITextPanel<string> button = PercentButton("■", index * 0.095f, 0.085f, 0f);
				button.TextColor = ToColor(color);
				button.OnLeftClick += (_, _) => SetColor(color);
				paletteHost.Append(button);
			}
		}

		private void BuildColorButtons(UIElement controls, float top)
		{
			(string channel, int delta)[] changes = { ("R", -16), ("R", 16), ("G", -16), ("G", 16), ("B", -16), ("B", 16), ("A", -32), ("A", 32) };
			for (int index = 0; index < changes.Length; index++)
			{
				(string channel, int delta) = changes[index];
				UITextPanel<string> button = PercentButton(channel + (delta < 0 ? "−" : "+"), index * 0.12f, 0.11f, top);
				button.OnLeftClick += (_, _) => AdjustColor(channel, delta);
				controls.Append(button);
			}
		}

		private void BuildReferenceControls(UIElement controls, float top)
		{
			UITextPanel<string> import = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceImport"), 0f, 0.22f, top);
			import.OnLeftClick += (_, _) => ImportReference();
			controls.Append(import);
			UITextPanel<string> reload = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceReload"), 0.23f, 0.18f, top);
			reload.OnLeftClick += (_, _) => ReloadReference(showStatus: true);
			controls.Append(reload);
			referenceVisibleButton = StyledPercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceVisible"), 0.42f, 0.18f, top,
				referenceImage != null && settings.ReferenceVisible && settings.GetReferenceFrame(settings.PoseIndex).Visible);
			referenceVisibleButton.OnLeftClick += (_, _) =>
			{
				if (referenceImage == null) return;
				SkinReferenceFrameSettings frame = settings.GetReferenceFrame(settings.PoseIndex);
				frame.Visible = !frame.Visible;
				RefreshLabels();
			};
			controls.Append(referenceVisibleButton);
			UITextPanel<string> opacityDown = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceOpacityDown"), 0.61f, 0.18f, top);
			opacityDown.OnLeftClick += (_, _) => ChangeReferenceOpacity(-16);
			controls.Append(opacityDown);
			UITextPanel<string> opacityUp = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceOpacityUp"), 0.80f, 0.18f, top);
			opacityUp.OnLeftClick += (_, _) => ChangeReferenceOpacity(16);
			controls.Append(opacityUp);

			UIText moveHint = new(Language.GetTextValue("Mods.customskin.UI.EditorReferenceMoveHint"), 0.62f)
			{
				Left = new StyleDimension(0f, 0f),
				Top = new StyleDimension(top + 38f, 0f),
				Width = new StyleDimension(0f, 1f),
				TextOriginX = 0f
			};
			controls.Append(moveHint);

			UITextPanel<string> mirror = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceMirror"), 0f, 0.25f, top + 66f);
			mirror.OnLeftClick += (_, _) => ToggleReferenceMirror();
			controls.Append(mirror);
			UITextPanel<string> reset = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceReset"), 0.26f, 0.25f, top + 66f);
			reset.OnLeftClick += (_, _) => ResetReferencePosition();
			controls.Append(reset);
			UITextPanel<string> folder = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceFolder"), 0.52f, 0.47f, top + 66f);
			folder.OnLeftClick += (_, _) => OpenReferenceFolder();
			controls.Append(folder);

			referenceOverlayButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceOverlay"), 0f, 0.48f, top + 102f);
			referenceOverlayButton.OnLeftClick += (_, _) => ApplyReference(replace: false);
			controls.Append(referenceOverlayButton);
			referenceReplaceButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceReplace"), 0.49f, 0.49f, top + 102f);
			referenceReplaceButton.OnLeftClick += (_, _) => ApplyReference(replace: true);
			controls.Append(referenceReplaceButton);
		}

		private void ImportReference()
		{
			try
			{
				string selected = FileBrowser.OpenFilePanel(Language.GetTextValue("Mods.customskin.UI.EditorReferenceDialogTitle"), "png");
				if (string.IsNullOrWhiteSpace(selected)) return;
				referenceImage = Creator.ImportReferenceImage(project, selected);
				settings.ReferenceVisible = true;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceLoaded", referenceImage.Width, referenceImage.Height), Color.LightGreen);
				RefreshLabels();
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetFailure(exception is SkinPackageException package ? package.ErrorCode : "reference.import", exception.Message);
			}
		}

		private void OpenReferenceFolder()
		{
			try
			{
				Creator.OpenReferenceDirectory(project);
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceFolderOpened", SkinCreatorSystem.ReferenceFileName), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetFailure(exception is SkinPackageException package ? package.ErrorCode : "reference.folder", exception.Message);
			}
		}

		private void ReloadReference(bool showStatus)
		{
			try
			{
				referenceImage = Creator.LoadReferenceImage(project);
				if (referenceImage != null)
				{
					settings.ReferenceVisible = true;
					if (showStatus)
						SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceLoaded", referenceImage.Width, referenceImage.Height), Color.LightGreen);
				}
				else if (showStatus)
				{
					SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceMissing", SkinCreatorSystem.ReferenceFileName), Color.Orange);
				}
				RefreshLabels();
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				referenceImage = null;
				SetFailure(exception is SkinPackageException package ? package.ErrorCode : "reference.load", exception.Message);
				RefreshLabels();
			}
		}

		private void MoveReference(int deltaX, int deltaY)
		{
			if (referenceImage == null) return;
			SkinReferenceFrameSettings frame = settings.GetReferenceFrame(settings.PoseIndex);
			frame.OffsetX = Math.Clamp(frame.OffsetX + deltaX, -SkinCreatorSystem.MaximumReferenceDimension, SkinCreatorSystem.MaximumReferenceDimension);
			frame.OffsetY = Math.Clamp(frame.OffsetY + deltaY, -SkinCreatorSystem.MaximumReferenceDimension, SkinCreatorSystem.MaximumReferenceDimension);
			RefreshLabels();
		}

		private void ChangeReferenceOpacity(int delta)
		{
			if (referenceImage == null) return;
			settings.ReferenceOpacity = (byte)Math.Clamp(settings.ReferenceOpacity + delta, 16, 255);
			RefreshLabels();
		}

		private void ToggleReferenceMirror()
		{
			if (referenceImage == null) return;
			SkinReferenceFrameSettings frame = settings.GetReferenceFrame(settings.PoseIndex);
			frame.Mirror = !frame.Mirror;
			RefreshLabels();
		}

		private void ResetReferencePosition()
		{
			if (referenceImage == null) return;
			SkinReferenceFrameSettings frame = settings.GetReferenceFrame(settings.PoseIndex);
			frame.OffsetX = 0;
			frame.OffsetY = 0;
			frame.Mirror = false;
			RefreshLabels();
		}

		private void ApplyReference(bool replace)
		{
			if (playing)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferencePauseRequired"), Color.OrangeRed);
				return;
			}
			if (referenceImage == null)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceMissing", SkinCreatorSystem.ReferenceFileName), Color.Orange);
				return;
			}
			if (!settings.ReferenceVisible || !settings.GetReferenceFrame(settings.PoseIndex).Visible)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceShowRequired"), Color.OrangeRed);
				return;
			}

			EndStroke();
			SkinReferenceFrameSettings frame = settings.GetReferenceFrame(settings.PoseIndex);
			SkinEditorPart targetPart = activePart;
			if (document.ApplyReference(targetPart, Pose, settings.Gender, referenceImage, frame.OffsetX, frame.OffsetY,
				frame.Mirror, settings.MirrorPreview, replace))
			{
				// Hide the translucent source after committing so the author sees
				// only the pixels that actually entered the runtime atlas.
				frame.Visible = false;
				pendingDiscard = false;
				BuildPaletteButtons();
				int affectedPoseCount = document.GetAffectedPoses(targetPart, Pose, settings.Gender).Count;
				SetStatus(Language.GetTextValue(replace
					? "Mods.customskin.UI.EditorReferenceReplaced"
					: "Mods.customskin.UI.EditorReferenceOverlaid", PartName(targetPart), PoseName(Pose), affectedPoseCount), Color.LightGreen);
			}
			else
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReferenceNoPixels"), Color.Orange);
			}
			RefreshLabels();
		}

		private void AdjustColor(string channel, int delta)
		{
			SkinEditorColor color = settings.Color;
			byte Adjust(byte value) => (byte)Math.Clamp(value + delta, 0, 255);
			SetColor(channel switch
			{
				"R" => color with { R = Adjust(color.R) },
				"G" => color with { G = Adjust(color.G) },
				"B" => color with { B = Adjust(color.B) },
				_ => color with { A = Adjust(color.A) }
			});
		}

		private void SetColor(SkinEditorColor color)
		{
			settings.Color = color;
			RefreshLabels();
		}

		private void PickColor(SkinEditorColor color)
		{
			settings.Color = color;
			RememberRecentColor(color);
			BuildPaletteButtons();
			RefreshLabels();
		}

		private void OnCanvasChanged()
		{
			pendingDiscard = false;
			if (settings.Tool == SkinEditorTool.Pencil)
				RememberRecentColor(settings.Color);
			BuildPaletteButtons();
			RefreshLabels();
		}

		private void RememberRecentColor(SkinEditorColor color)
		{
			if (color.A == 0)
				return;
			recentColors.Remove(color);
			recentColors.Insert(0, color);
			if (recentColors.Count > 10)
				recentColors.RemoveRange(10, recentColors.Count - 10);
		}

		private void EndStroke()
		{
			if (document.CommitStroke())
				BuildPaletteButtons();
		}

		private void Undo()
		{
			playing = false;
			if (document.Undo()) { BuildPaletteButtons(); RefreshLabels(); }
		}

		private void Redo()
		{
			playing = false;
			if (document.Redo()) { BuildPaletteButtons(); RefreshLabels(); }
		}

		private void Save(bool apply)
		{
			try
			{
				playing = false;
				EndStroke();
				Creator.SaveEditorDocument(project, document);
				Creator.WriteEditorSettings(project, settings);
				if (apply)
				{
					SkinImportResult result = Creator.RefreshAndUse(project);
					if (!result.Success) { SetFailure(result.ErrorCode, result.ErrorMessage); return; }
					SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSavedUsed", result.Skin!.Manifest.Name), Color.LightGreen);
				}
				else
				{
					Creator.LoadPreview(project);
					SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSaved"), Color.LightGreen);
				}
				RefreshLabels();
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetFailure(exception is SkinPackageException package ? package.ErrorCode : "editor.save", exception.Message);
			}
		}

		private void Export()
		{
			try
			{
				Save(apply: false);
				if (document.IsDirty) return;
				string output = Creator.ExportForSharing(project);
				Creator.OpenExportsDirectory();
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ProjectExported", output), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetFailure(exception is SkinPackageException package ? package.ErrorCode : "project.export", exception.Message);
			}
		}

		private void Reload()
		{
			if (document.IsDirty)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReloadDirty"), Color.OrangeRed);
				return;
			}
			IngameFancyUI.OpenUIState(new SkinPixelEditorUI(project, Creator.LoadEditorDocument(project), Creator.ReadEditorSettings(project)));
		}

		private void Back()
		{
			EndStroke();
			if (document.IsDirty && !pendingDiscard)
			{
				pendingDiscard = true;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorConfirmDiscard"), Color.OrangeRed);
				return;
			}
			IngameFancyUI.OpenUIState(new SkinCreatorUI());
		}

		private void RefreshLabels()
		{
			if (poseText == null) return;
			poseText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorPoseNoGender", PoseName(Pose), settings.PoseIndex + 1, SkinEditorDocument.Poses.Count,
				settings.MirrorPreview ? Language.GetTextValue("Mods.customskin.UI.EditorLeft") : Language.GetTextValue("Mods.customskin.UI.EditorRight")));
			slotText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorSlots",
				document.GetSlot(SkinEditorPart.Head, Pose, settings.Gender),
				document.GetSlot(SkinEditorPart.Torso, Pose, settings.Gender),
				document.GetSlot(SkinEditorPart.FrontArm, Pose, settings.Gender),
				document.GetSlot(SkinEditorPart.BackArm, Pose, settings.Gender),
				document.GetSlot(SkinEditorPart.Legs, Pose, settings.Gender)));
			IReadOnlyList<SkinEditorPose> affected = document.GetAffectedPoses(activePart, Pose, settings.Gender);
			string affectedGroups = string.Join(" · ", affected
				.GroupBy(candidate => candidate.State, StringComparer.Ordinal)
				.Select(group => $"{PoseStateName(group.Key)} ×{group.Count()}"));
			affectedText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorAffected", PartName(activePart),
				document.GetSlot(activePart, Pose, settings.Gender), affected.Count, affectedGroups));
			SkinEditorColor color = settings.Color;
			colorText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorColor", color.R, color.G, color.B, color.A));
			colorText.TextColor = ToColor(color.A == 0 ? new SkinEditorColor(180, 180, 180, 255) : color);
			historyText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorHistory", document.UndoStrokeCount, SkinEditorDocument.MaxUndoStrokes,
				document.UndoBytes / 1024, SkinEditorDocument.MaxUndoBytes / 1024, settings.Zoom,
				document.IsDirty ? Language.GetTextValue("Mods.customskin.UI.EditorDirty") : Language.GetTextValue("Mods.customskin.UI.EditorClean")));
			if (referenceText != null)
			{
				if (referenceImage == null)
				{
					referenceText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorReferenceNone", SkinCreatorSystem.ReferenceFileName));
				}
				else
				{
					SkinReferenceFrameSettings frame = settings.GetReferenceFrame(settings.PoseIndex);
					referenceText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorReferenceState", referenceImage.Width, referenceImage.Height,
						frame.OffsetX, frame.OffsetY, settings.ReferenceOpacity, frame.Mirror
							? Language.GetTextValue("Mods.customskin.UI.EditorReferenceMirrored")
							: Language.GetTextValue("Mods.customskin.UI.EditorReferenceNormal")));
				}
				ApplySelectedButtonStyle(referenceVisibleButton, referenceImage != null && settings.ReferenceVisible &&
					settings.GetReferenceFrame(settings.PoseIndex).Visible);
				bool canApplyReference = referenceImage != null && settings.ReferenceVisible &&
					settings.GetReferenceFrame(settings.PoseIndex).Visible && !playing;
				ApplyReferenceActionStyle(referenceOverlayButton, canApplyReference);
				ApplyReferenceActionStyle(referenceReplaceButton, canApplyReference);
			}
		}

		private void SetFailure(string? errorCode, string? errorMessage)
		{
			string code = SkinImportErrorPresentation.SanitizeForDisplay(errorCode ?? "unknown", 80);
			string detail = SkinErrorText.LocalizedDetail(errorCode, errorMessage);
			SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorFailed", code, detail), Color.OrangeRed);
		}

		private void SetStatus(string message, Color color)
		{
			statusText.SetText(message);
			statusText.TextColor = color;
		}

		private static UIText Label(float left, float top, float scale, float height = 24f) => new(string.Empty, scale)
		{
			Left = new StyleDimension(left, 0f),
			Top = new StyleDimension(top, 0f),
			Width = new StyleDimension(0f, 1f),
			Height = new StyleDimension(height, 0f),
			IsWrapped = true,
			TextOriginX = 0f
		};

		private static UITextPanel<string> Button(string text, float left, float top, float width)
		{
			UITextPanel<string> button = new(text, 0.66f)
			{
				Left = new StyleDimension(left, 0f),
				Top = new StyleDimension(top, 0f),
				Width = new StyleDimension(width, 0f),
				Height = new StyleDimension(32f, 0f)
			};
			button.WithFadedMouseOver();
			return button;
		}

		private static UITextPanel<string> PercentButton(string text, float leftPercent, float widthPercent, float top)
		{
			UITextPanel<string> button = new(text, 0.66f)
			{
				Left = new StyleDimension(0f, leftPercent),
				Top = new StyleDimension(top, 0f),
				Width = new StyleDimension(-4f, widthPercent),
				Height = new StyleDimension(32f, 0f)
			};
			button.WithFadedMouseOver();
			return button;
		}

		private static UITextPanel<string> StyledPercentButton(string text, float leftPercent, float widthPercent, float top, bool selected)
		{
			UITextPanel<string> button = new(text, 0.66f)
			{
				Left = new StyleDimension(0f, leftPercent),
				Top = new StyleDimension(top, 0f),
				Width = new StyleDimension(-4f, widthPercent),
				Height = new StyleDimension(32f, 0f),
				TextColor = Color.White
			};
			ApplySelectedButtonStyle(button, selected);
			// Do not apply WithFadedMouseOver here. Its default mouse-out handler restores
			// the standard panel color and erases the persistent selected-state color.
			return button;
		}

		private static void ApplySelectedButtonStyle(UITextPanel<string> button, bool selected)
		{
			button.BackgroundColor = selected ? new Color(214, 126, 42, 245) : new Color(63, 77, 130, 225);
			button.BorderColor = selected ? new Color(255, 210, 92, 255) : new Color(18, 24, 48, 255);
			button.TextColor = Color.White;
		}

		private static void ApplyReferenceActionStyle(UITextPanel<string> button, bool enabled)
		{
			button.BackgroundColor = enabled ? new Color(63, 105, 93, 235) : new Color(48, 52, 70, 190);
			button.BorderColor = enabled ? new Color(92, 210, 164, 255) : new Color(18, 24, 48, 220);
			button.TextColor = enabled ? Color.White : Color.Gray;
		}

		private static UITextPanel<string> BottomButton(UIElement panel, string text, float left, float top, float width)
		{
			UITextPanel<string> button = Button(text, left, top, width);
			button.Top = new StyleDimension(top, 1f);
			panel.Append(button);
			return button;
		}

		private static UITextPanel<string> PercentBottomButton(UIElement panel, string text, float leftPercent, float widthPercent, float top)
		{
			UITextPanel<string> button = PercentButton(text, leftPercent, widthPercent, top);
			button.Top = new StyleDimension(top, 1f);
			panel.Append(button);
			return button;
		}

		private static Color ToColor(SkinEditorColor color) => new(color.R, color.G, color.B, color.A);
		private static string PartName(SkinEditorPart part) => Language.GetTextValue($"Mods.customskin.UI.EditorParts.{part}");
		private static string ToolName(SkinEditorTool tool) => Language.GetTextValue($"Mods.customskin.UI.EditorTools.{tool}");
		private static string PoseStateName(string state) => state switch
		{
			"idle" => Language.GetTextValue("Mods.customskin.UI.EditorStates.Idle"),
			"body-action" => Language.GetTextValue("Mods.customskin.UI.EditorStates.BodyAction"),
			"jump-air" => Language.GetTextValue("Mods.customskin.UI.EditorStates.JumpAir"),
			"ground-move" => Language.GetTextValue("Mods.customskin.UI.EditorStates.GroundMove"),
			"weapon-arm" => Language.GetTextValue("Mods.customskin.UI.EditorStates.WeaponArm"),
			_ => Language.GetTextValue("Mods.customskin.UI.EditorStates.NativeSpecial")
		};

		private static string PoseName(SkinEditorPose pose)
		{
			return pose.State switch
			{
				"idle" => Language.GetTextValue("Mods.customskin.UI.EditorPoses.Idle"),
				"body-action" => Language.GetTextValue("Mods.customskin.UI.EditorPoses.BodyAction", pose.BodyFrame),
				"jump-air" => Language.GetTextValue("Mods.customskin.UI.EditorPoses.JumpAir", pose.BodyFrame - 4),
				"ground-move" => Language.GetTextValue("Mods.customskin.UI.EditorPoses.GroundMove", pose.BodyFrame - 6),
				"weapon-arm" => Language.GetTextValue("Mods.customskin.UI.EditorPoses.WeaponArm", pose.Name.Replace("Weapon Arm ", string.Empty)),
				_ => Language.GetTextValue("Mods.customskin.UI.EditorPoses.NativeLegs", pose.LegFrame)
			};
		}
	}

	internal sealed class SkinEditorColorPalette : UIElement
	{
		private const int HueColumns = 19;
		private const int Columns = HueColumns + 1;
		private const int Rows = 6;
		private readonly Func<SkinEditorColor> currentColor;
		private readonly Action<SkinEditorColor> selected;
		private bool previousLeft;
		private int lastColumn = -1;
		private int lastRow = -1;

		public SkinEditorColorPalette(Func<SkinEditorColor> currentColor, Action<SkinEditorColor> selected)
		{
			this.currentColor = currentColor;
			this.selected = selected;
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			bool left = Main.mouseLeft;
			if (IsMouseHovering)
			{
				Main.LocalPlayer.mouseInterface = true;
				if (TryGetCell(out int column, out int row) && left &&
					(!previousLeft || column != lastColumn || row != lastRow))
				{
					selected(GetColor(column, row));
					lastColumn = column;
					lastRow = row;
				}
			}
			if (!left)
			{
				lastColumn = -1;
				lastRow = -1;
			}
			previousLeft = left;
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);
			Rectangle bounds = GetDimensions().ToRectangle();
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, bounds, new Color(12, 17, 34, 255));
			TryGetCell(out int hoverColumn, out int hoverRow);
			SkinEditorColor current = currentColor();

			for (int row = 0; row < Rows; row++)
			{
				for (int column = 0; column < Columns; column++)
				{
					Rectangle cell = CellRectangle(bounds, column, row);
					SkinEditorColor color = GetColor(column, row);
					spriteBatch.Draw(TextureAssets.MagicPixel.Value,
						new Rectangle(cell.X + 1, cell.Y + 1, Math.Max(1, cell.Width - 2), Math.Max(1, cell.Height - 2)),
						new Color(color.R, color.G, color.B, 255));

					bool isCurrent = color == (current with { A = 255 });
					bool isHovered = IsMouseHovering && column == hoverColumn && row == hoverRow;
					if (isCurrent || isHovered)
						DrawOutline(spriteBatch, cell, isCurrent ? new Color(255, 210, 70) : Color.White);
				}
			}
		}

		private bool TryGetCell(out int column, out int row)
		{
			Rectangle bounds = GetDimensions().ToRectangle();
			int localX = Main.mouseX - bounds.X;
			int localY = Main.mouseY - bounds.Y;
			if (localX < 0 || localY < 0 || localX >= bounds.Width || localY >= bounds.Height)
			{
				column = row = -1;
				return false;
			}
			column = Math.Min(Columns - 1, localX * Columns / Math.Max(1, bounds.Width));
			row = Math.Min(Rows - 1, localY * Rows / Math.Max(1, bounds.Height));
			return true;
		}

		private static Rectangle CellRectangle(Rectangle bounds, int column, int row)
		{
			int left = bounds.X + column * bounds.Width / Columns;
			int right = bounds.X + (column + 1) * bounds.Width / Columns;
			int top = bounds.Y + row * bounds.Height / Rows;
			int bottom = bounds.Y + (row + 1) * bounds.Height / Rows;
			return new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
		}

		private static SkinEditorColor GetColor(int column, int row)
		{
			if (column == HueColumns)
			{
				byte gray = (byte)Math.Round(255d * (Rows - 1 - row) / (Rows - 1));
				return new SkinEditorColor(gray, gray, gray, 255);
			}

			float hue = column * 360f / HueColumns;
			float saturation = row switch { 0 => 0.35f, 1 => 0.65f, _ => 1f };
			float value = row switch { 0 or 1 or 2 => 1f, 3 => 0.75f, 4 => 0.5f, _ => 0.25f };
			float chroma = value * saturation;
			float hueSection = hue / 60f;
			float secondary = chroma * (1f - Math.Abs(hueSection % 2f - 1f));
			(float red, float green, float blue) = hueSection switch
			{
				< 1f => (chroma, secondary, 0f),
				< 2f => (secondary, chroma, 0f),
				< 3f => (0f, chroma, secondary),
				< 4f => (0f, secondary, chroma),
				< 5f => (secondary, 0f, chroma),
				_ => (chroma, 0f, secondary)
			};
			float match = value - chroma;
			return new SkinEditorColor(
				(byte)Math.Round((red + match) * 255f),
				(byte)Math.Round((green + match) * 255f),
				(byte)Math.Round((blue + match) * 255f), 255);
		}

		private static void DrawOutline(SpriteBatch spriteBatch, Rectangle rectangle, Color color)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Bottom - 2, rectangle.Width, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, 2, rectangle.Height), color);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.Right - 2, rectangle.Y, 2, rectangle.Height), color);
		}
	}

	internal sealed class SkinEditorTextInput : UIPanel
	{
		private readonly UIText textElement;
		private readonly string hint;
		private readonly int maximumLength;
		private readonly bool digitsOnly;
		private string currentString = string.Empty;
		private KeyboardState previousKeys;
		private bool previousMouseLeft;
		private bool replaceOnNextInput;

		public SkinEditorTextInput(string hint, int maximumLength = 32, bool digitsOnly = false)
		{
			this.hint = hint;
			this.maximumLength = maximumLength;
			this.digitsOnly = digitsOnly;
			PaddingLeft = PaddingRight = 8f;
			PaddingTop = PaddingBottom = 5f;
			textElement = new UIText(hint, 0.66f)
			{
				VAlign = 0.5f,
				TextColor = Color.Silver
			};
			Append(textElement);
			OnLeftClick += (_, _) => Focus();
		}

		public bool Focused { get; private set; }
		public string CurrentString => currentString;
		public event Action? Submitted;

		public void SetText(string value)
		{
			currentString = value.Length <= maximumLength ? value : value[..maximumLength];
			replaceOnNextInput = false;
			RefreshDisplayedText();
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			bool left = Main.mouseLeft;
			if (Focused && left && !previousMouseLeft && !IsMouseHovering)
				Unfocus();
			previousMouseLeft = left;

			if (!Focused)
				return;

			Main.LocalPlayer.mouseInterface = true;
			PlayerInput.WritingText = true;
			Main.instance.HandleIME();
			string input = Main.GetInputText(currentString);
			if (input != currentString)
			{
				if (replaceOnNextInput)
				{
					input = input.Length > currentString.Length && input.StartsWith(currentString, StringComparison.Ordinal)
						? input[currentString.Length..]
						: string.Empty;
					replaceOnNextInput = false;
				}
				if (digitsOnly)
					input = new string(input.Where(char.IsDigit).ToArray());
				if (input.Length > maximumLength)
					input = input[..maximumLength];
				currentString = input;
				RefreshDisplayedText();
			}

			KeyboardState keys = Main.keyState;
			if (Pressed(keys, Keys.Enter))
			{
				Unfocus();
				Submitted?.Invoke();
			}
			else if (Pressed(keys, Keys.Escape))
			{
				Unfocus();
			}
			previousKeys = keys;
		}

		private void Focus()
		{
			Focused = true;
			replaceOnNextInput = true;
			previousKeys = Main.keyState;
			Main.clrInput();
			BorderColor = new Color(255, 210, 92, 255);
			RefreshDisplayedText();
		}

		private void Unfocus()
		{
			Focused = false;
			BorderColor = new Color(18, 24, 48, 255);
			RefreshDisplayedText();
		}

		private bool Pressed(KeyboardState current, Keys key)
			=> current.IsKeyDown(key) && !previousKeys.IsKeyDown(key);

		private void RefreshDisplayedText()
		{
			bool empty = string.IsNullOrEmpty(currentString);
			textElement.SetText(empty ? hint : currentString + (Focused ? "|" : string.Empty));
			textElement.TextColor = empty ? Color.Silver : Focused && replaceOnNextInput ? new Color(255, 230, 128) : Color.White;
		}
	}

	internal sealed class SkinEditorCanvas : UIElement
	{
		private readonly SkinEditorDocument document;
		private readonly SkinEditorSettings settings;
		private readonly Func<SkinEditorPart> part;
		private readonly Func<SkinEditorPose> pose;
		private readonly Func<SkinReferenceImage?> reference;
		private readonly Func<SkinReferenceFrameSettings> referenceFrame;
		private readonly Action changed;
		private readonly Action<SkinEditorColor> colorPicked;
		private bool previousLeft;
		private bool drawing;
		private int lastPixelX = -1;
		private int lastPixelY = -1;
		private int previousWheel;
		private byte[]? cachedComposite;
		private SkinEditorPose? cachedPose;
		private SkinEditorGender cachedGender;
		private bool cachedMirror;
		private bool cachedOnlySelectedPart;
		private SkinEditorPart cachedPart;
		private long cachedRevision = -1;

		public SkinEditorCanvas(SkinEditorDocument document, SkinEditorSettings settings, Func<SkinEditorPart> part, Func<SkinEditorPose> pose,
			Func<SkinReferenceImage?> reference, Func<SkinReferenceFrameSettings> referenceFrame,
			Action changed, Action<SkinEditorColor> colorPicked)
		{
			this.document = document;
			this.settings = settings;
			this.part = part;
			this.pose = pose;
			this.reference = reference;
			this.referenceFrame = referenceFrame;
			this.changed = changed;
			this.colorPicked = colorPicked;
			OverflowHidden = true;
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			if (IsMouseHovering)
			{
				Main.LocalPlayer.mouseInterface = true;
				PlayerInput.LockVanillaMouseScroll("CustomSkinEditor");
				int wheel = PlayerInput.ScrollWheelDeltaForUI;
				if (wheel != 0 && wheel != previousWheel)
					settings.Zoom = Math.Clamp(settings.Zoom + Math.Sign(wheel), 2, 16);
				previousWheel = wheel;
			}
			else
			{
				previousWheel = 0;
			}

			bool left = Main.mouseLeft;
			if (!Main.hasFocus || !IsMouseHovering)
			{
				if (drawing) FinishStroke();
				previousLeft = left;
				return;
			}

			if (left)
			{
				if (TryMousePixel(out int x, out int y))
				{
					if (settings.Tool == SkinEditorTool.Eyedropper)
					{
						if (!previousLeft) colorPicked(document.ReadTargetPixel(part(), pose(), settings.Gender, x, y));
					}
					else
					{
						if (!drawing)
						{
							document.BeginStroke(part(), pose(), settings.Gender);
							drawing = true;
							lastPixelX = lastPixelY = -1;
						}
						DrawLine(lastPixelX, lastPixelY, x, y, settings.Tool == SkinEditorTool.Eraser ? SkinEditorColor.Transparent : settings.Color);
						lastPixelX = x;
						lastPixelY = y;
					}
				}
			}
			else if (drawing)
			{
				FinishStroke();
			}
			previousLeft = left;
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);
			CalculatedStyle dimensions = GetDimensions();
			Rectangle bounds = dimensions.ToRectangle();
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, bounds, new Color(14, 19, 36, 245));
			Rectangle canvas = GetCanvasRectangle();
			int zoom = Math.Max(1, canvas.Width / SkinEditorDocument.CellWidth);
			SkinEditorPose currentPose = pose();
			if (cachedComposite == null || !ReferenceEquals(cachedPose, currentPose) || cachedGender != settings.Gender ||
				cachedMirror != settings.MirrorPreview || cachedOnlySelectedPart != settings.OnlySelectedPart ||
				(settings.OnlySelectedPart && cachedPart != part()) || cachedRevision != document.ContentRevision)
			{
				cachedComposite = settings.OnlySelectedPart
					? document.ComposeSelectedPart(part(), currentPose, settings.Gender, settings.MirrorPreview)
					: document.Compose(currentPose, settings.Gender, settings.MirrorPreview);
				cachedPose = currentPose;
				cachedGender = settings.Gender;
				cachedMirror = settings.MirrorPreview;
				cachedOnlySelectedPart = settings.OnlySelectedPart;
				cachedPart = part();
				cachedRevision = document.ContentRevision;
			}
			byte[] composite = cachedComposite;
			for (int y = 0; y < SkinEditorDocument.CellHeight; y++)
			{
				for (int x = 0; x < SkinEditorDocument.CellWidth; x++)
				{
					Rectangle pixel = new(canvas.X + x * zoom, canvas.Y + y * zoom, zoom, zoom);
					Color checker = ((x + y) & 1) == 0 ? new Color(55, 59, 76) : new Color(75, 79, 96);
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, pixel, checker);
					int offset = (y * SkinEditorDocument.CellWidth + x) * 4;
					if (composite[offset + 3] != 0)
						spriteBatch.Draw(TextureAssets.MagicPixel.Value, pixel,
							new Color(composite[offset], composite[offset + 1], composite[offset + 2], composite[offset + 3]));
				}
			}
			SkinReferenceImage? currentReference = reference();
			if (currentReference != null && settings.ReferenceVisible)
			{
				SkinReferenceFrameSettings frame = referenceFrame();
				if (frame.Visible)
				{
					for (int y = 0; y < SkinEditorDocument.CellHeight; y++)
					{
						for (int x = 0; x < SkinEditorDocument.CellWidth; x++)
						{
							if (!currentReference.TryReadCanvasPixel(x, y, frame.OffsetX, frame.OffsetY, frame.Mirror, out SkinEditorColor source) || source.A == 0)
								continue;
							byte alpha = (byte)((source.A * settings.ReferenceOpacity + 127) / 255);
							if (alpha == 0) continue;
							Rectangle pixel = new(canvas.X + x * zoom, canvas.Y + y * zoom, zoom, zoom);
							spriteBatch.Draw(TextureAssets.MagicPixel.Value, pixel, new Color(source.R, source.G, source.B, alpha));
						}
					}
				}
			}
			if (settings.ShowGrid && zoom >= 4)
			{
				Color grid = new(0, 0, 0, 80);
				for (int x = 0; x <= SkinEditorDocument.CellWidth; x++)
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(canvas.X + x * zoom, canvas.Y, 1, canvas.Height), grid);
				for (int y = 0; y <= SkinEditorDocument.CellHeight; y++)
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(canvas.X, canvas.Y + y * zoom, canvas.Width, 1), grid);
			}
		}

		private Rectangle GetCanvasRectangle()
		{
			Rectangle bounds = GetDimensions().ToRectangle();
			int fit = Math.Max(1, Math.Min((bounds.Width - 16) / SkinEditorDocument.CellWidth, (bounds.Height - 16) / SkinEditorDocument.CellHeight));
			int zoom = Math.Max(1, Math.Min(settings.Zoom, fit));
			int width = SkinEditorDocument.CellWidth * zoom;
			int height = SkinEditorDocument.CellHeight * zoom;
			return new Rectangle(bounds.Center.X - width / 2, bounds.Center.Y - height / 2, width, height);
		}

		private bool TryMousePixel(out int x, out int y)
		{
			Rectangle canvas = GetCanvasRectangle();
			int zoom = canvas.Width / SkinEditorDocument.CellWidth;
			x = (Main.mouseX - canvas.X) / zoom;
			y = (Main.mouseY - canvas.Y) / zoom;
			if (!canvas.Contains(Main.mouseX, Main.mouseY)) return false;
			if (settings.MirrorPreview) x = SkinEditorDocument.CellWidth - 1 - x;
			return true;
		}

		private void DrawLine(int fromX, int fromY, int toX, int toY, SkinEditorColor color)
		{
			if (fromX < 0 || fromY < 0)
			{
				document.ApplyPixel(toX, toY, color);
				return;
			}
			int dx = Math.Abs(toX - fromX), sx = fromX < toX ? 1 : -1;
			int dy = -Math.Abs(toY - fromY), sy = fromY < toY ? 1 : -1;
			int error = dx + dy;
			while (true)
			{
				document.ApplyPixel(fromX, fromY, color);
				if (fromX == toX && fromY == toY) break;
				int twice = error * 2;
				if (twice >= dy) { error += dy; fromX += sx; }
				if (twice <= dx) { error += dx; fromY += sy; }
			}
		}

		private void FinishStroke()
		{
			drawing = false;
			lastPixelX = lastPixelY = -1;
			if (document.CommitStroke()) changed();
		}
	}
}
