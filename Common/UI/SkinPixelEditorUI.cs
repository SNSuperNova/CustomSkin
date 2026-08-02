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
		private static readonly string[] AnimationStates =
		{
			"idle", "body-action", "jump-air", "ground-move", "weapon-arm", "native-special"
		};
		private readonly SkinCreatorProject project;
		private readonly SkinEditorDocument document;
		private readonly SkinEditorSettings settings;
		private SkinEditorPart activePart;
		private SkinEditorCanvas canvas = null!;
		private UIText slotText = null!;
		private UIText affectedText = null!;
		private UIText colorText = null!;
		private UIText statusText = null!;
		private SkinEditorTextInput projectNameInput = null!;
		private UIElement partHost = null!;
		private UIElement animationGroupHost = null!;
		private UIElement frameHost = null!;
		private UIElement toolHost = null!;
		private UIElement paletteHost = null!;
		private UITextPanel<string> onlyPartButton = null!;
		private UIText referenceText = null!;
		private UITextPanel<string> referenceVisibleButton = null!;
		private UITextPanel<string> referenceOverlayButton = null!;
		private UITextPanel<string> referenceReplaceButton = null!;
		private UITextPanel<string> copyPreviousButton = null!;
		private UITextPanel<string> copyNextButton = null!;
		private UITextPanel<string> confirmSelectionButton = null!;
		private UITextPanel<string> cancelSelectionButton = null!;
		private SkinReferenceImage? referenceImage;
		private readonly List<SkinEditorColor> recentColors = new();
		private readonly List<SkinEditorColor> commonColors = new();
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
			// Keep the common palette stable for this editing session. Reference-image
			// overlay/replacement can alter thousands of pixels and must not displace
			// the author's existing colors merely because the atlas changed.
			commonColors.AddRange(document.ExtractPalette(10).Where(color => color.A > 0));
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

			UIElement titleBar = new()
			{
				HAlign = 0.5f,
				Top = new StyleDimension(3f, 0f),
				Width = new StyleDimension(620f, 0f),
				Height = new StyleDimension(36f, 0f)
			};
			panel.Append(titleBar);
			UIText title = new(Language.GetTextValue("Mods.customskin.UI.PixelEditorHeading"), 0.9f)
			{
				Left = new StyleDimension(0f, 0f),
				VAlign = 0.5f
			};
			titleBar.Append(title);
			projectNameInput = new SkinEditorTextInput(Language.GetTextValue("Mods.customskin.UI.EditorProjectNameHint"), 80)
			{
				Left = new StyleDimension(238f, 0f),
				Width = new StyleDimension(-238f, 1f),
				Height = new StyleDimension(32f, 0f),
				VAlign = 0.5f
			};
			projectNameInput.SetText(project.DisplayName);
			projectNameInput.Submitted += RenameProject;
			projectNameInput.Cancelled += () => projectNameInput.SetText(project.DisplayName);
			titleBar.Append(projectNameInput);

			canvas = new SkinEditorCanvas(document, settings, () => activePart, () => Pose, () => referenceImage,
				() => settings.GetReferenceFrame(settings.PoseIndex), OnCanvasChanged, PickColor, OnSelectionStateChanged)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(48f, 0f),
				Width = new StyleDimension(-18f, 0.48f),
				Height = new StyleDimension(-166f, 1f)
			};
			panel.Append(canvas);

			UIElement controlsViewport = new()
			{
				Left = new StyleDimension(12f, 0.49f),
				Top = new StyleDimension(48f, 0f),
				Width = new StyleDimension(-30f, 0.51f),
				Height = new StyleDimension(-166f, 1f),
				OverflowHidden = true
			};
			panel.Append(controlsViewport);

			UIList controlsList = new()
			{
				Width = new StyleDimension(-22f, 1f),
				Height = new StyleDimension(0f, 1f),
				ListPadding = 0f
			};
			controlsViewport.Append(controlsList);
			UIScrollbar controlsScrollbar = new()
			{
				HAlign = 1f,
				Width = new StyleDimension(18f, 0f),
				Height = new StyleDimension(0f, 1f)
			};
			controlsViewport.Append(controlsScrollbar);
			controlsList.SetScrollbar(controlsScrollbar);

			UIElement controls = new()
			{
				Width = new StyleDimension(0f, 1f),
				Height = new StyleDimension(616f, 0f)
			};
			controlsList.Add(controls);

			animationGroupHost = new UIElement { Width = new StyleDimension(0f, 1f), Height = new StyleDimension(34f, 0f) };
			controls.Append(animationGroupHost);
			BuildAnimationGroupButtons();

			frameHost = new UIElement { Top = new StyleDimension(38f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(70f, 0f) };
			controls.Append(frameHost);
			BuildFrameButtons();

			UITextPanel<string> play = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorPlayPause"), 0f, 0.19f, 76f);
			play.OnLeftClick += (_, _) =>
			{
				if (canvas.HasFloatingSelection)
				{
					SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
					return;
				}
				document.CommitStroke();
				playing = !playing;
				playbackTicks = 0;
				BuildFrameButtons();
				RefreshLabels();
			};
			controls.Append(play);
			UITextPanel<string> mirror = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorMirror"), 0.20f, 0.19f, 76f);
			mirror.OnLeftClick += (_, _) => { EndStroke(); settings.MirrorPreview = !settings.MirrorPreview; RefreshLabels(); };
			controls.Append(mirror);
			onlyPartButton = StyledPercentButton(Language.GetTextValue("Mods.customskin.UI.EditorOnlyPart"), 0.40f, 0.19f, 76f, settings.OnlySelectedPart);
			onlyPartButton.OnLeftClick += (_, _) =>
			{
				EndStroke();
				settings.OnlySelectedPart = !settings.OnlySelectedPart;
				ApplySelectedButtonStyle(onlyPartButton, settings.OnlySelectedPart);
			};
			controls.Append(onlyPartButton);
			slotText = Label(0f, 116f, 0.66f);
			controls.Append(slotText);
			affectedText = Label(0f, 140f, 0.66f, 24f);
			controls.Append(affectedText);

			partHost = new UIElement { Top = new StyleDimension(168f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(32f, 0f) };
			controls.Append(partHost);
			BuildPartButtons();

			toolHost = new UIElement { Top = new StyleDimension(204f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(34f, 0f) };
			controls.Append(toolHost);
			BuildToolButtons();

			paletteHost = new UIElement { Top = new StyleDimension(244f, 0f), Width = new StyleDimension(0f, 1f), Height = new StyleDimension(34f, 0f) };
			controls.Append(paletteHost);
			BuildPaletteButtons();

			colorText = Label(0f, 284f, 0.7f);
			controls.Append(colorText);
			SkinEditorColorPalette colorPalette = new(() => settings.Color, SetColor)
			{
				Top = new StyleDimension(310f, 0f),
				Width = new StyleDimension(0f, 1f),
				Height = new StyleDimension(58f, 0f)
			};
			controls.Append(colorPalette);

			SkinEditorFreeColorPicker freeColorPicker = new(() => settings.Color, SetColor)
			{
				Top = new StyleDimension(374f, 0f),
				Width = new StyleDimension(0f, 1f),
				Height = new StyleDimension(82f, 0f)
			};
			controls.Append(freeColorPicker);

			referenceText = Label(0f, 464f, 0.66f);
			controls.Append(referenceText);
			BuildReferenceControls(controls, 496f);

			UIText operationHint = new(Language.GetTextValue("Mods.customskin.UI.EditorOperationHint"), 0.72f)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(-116f, 1f),
				Width = new StyleDimension(-36f, 1f),
				Height = new StyleDimension(38f, 0f),
				IsWrapped = true,
				TextOriginX = 0.5f
			};
			panel.Append(operationHint);

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
				if (playbackTicks >= 6)
				{
					playbackTicks = 0;
					IReadOnlyList<(int GlobalIndex, SkinEditorPose Pose)> frames = GetGroupFrames(Pose.State);
					int current = frames.ToList().FindIndex(frame => frame.GlobalIndex == settings.PoseIndex);
					settings.PoseIndex = frames[(current + 1) % frames.Count].GlobalIndex;
					BuildFrameButtons();
					RefreshLabels();
				}
			}
		}

		private void HandleShortcuts()
		{
			KeyboardState keys = Main.keyState;
			if (projectNameInput.Focused)
			{
				referenceMoveRepeatTicks = 0;
				previousKeys = keys;
				return;
			}
			bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
			bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
			if (canvas.HasFloatingSelection)
			{
				if (Pressed(keys, Keys.Enter)) CommitFloatingSelection();
				else if (Pressed(keys, Keys.Escape)) CancelFloatingSelection();
				else
				{
					int step = shift ? 5 : 1;
					if (Pressed(keys, Keys.Left)) canvas.MoveFloatingSelection(-step, 0);
					if (Pressed(keys, Keys.Right)) canvas.MoveFloatingSelection(step, 0);
					if (Pressed(keys, Keys.Up)) canvas.MoveFloatingSelection(0, -step);
					if (Pressed(keys, Keys.Down)) canvas.MoveFloatingSelection(0, step);
				}
				previousKeys = keys;
				return;
			}
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

		private IReadOnlyList<(int GlobalIndex, SkinEditorPose Pose)> GetGroupFrames(string state)
			=> SkinEditorDocument.Poses
				.Select((pose, index) => (GlobalIndex: index, Pose: pose))
				.Where(frame => string.Equals(frame.Pose.State, state, StringComparison.Ordinal))
				.ToArray();

		private void BuildAnimationGroupButtons()
		{
			animationGroupHost.RemoveAllChildren();
			for (int index = 0; index < AnimationStates.Length; index++)
			{
				string state = AnimationStates[index];
				UITextPanel<string> button = StyledPercentButton(PoseStateName(state), index / (float)AnimationStates.Length,
					1f / AnimationStates.Length, 0f, string.Equals(Pose.State, state, StringComparison.Ordinal));
				button.OnLeftClick += (_, _) => SelectAnimationGroup(state);
				animationGroupHost.Append(button);
			}
			animationGroupHost.Recalculate();
		}

		private void BuildFrameButtons()
		{
			frameHost.RemoveAllChildren();
			IReadOnlyList<(int GlobalIndex, SkinEditorPose Pose)> frames = GetGroupFrames(Pose.State);
			float width = frames.Count > 10 ? 0.075f : Math.Min(0.12f, 0.72f / frames.Count);
			for (int index = 0; index < frames.Count; index++)
			{
				(int globalIndex, _) = frames[index];
				UITextPanel<string> button = StyledPercentButton(FrameButtonName(frames[index].Pose, index), index * width, width, 0f,
					globalIndex == settings.PoseIndex);
				button.OnLeftClick += (_, _) => SelectFrame(globalIndex);
				frameHost.Append(button);
			}

			int current = frames.ToList().FindIndex(frame => frame.GlobalIndex == settings.PoseIndex);
			copyPreviousButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorCopyPrevious"), 0.60f, 0.19f, 38f);
			copyPreviousButton.OnLeftClick += (_, _) => CopyToAdjacentFrame(-1);
			ApplyReferenceActionStyle(copyPreviousButton, !playing && current > 0);
			frameHost.Append(copyPreviousButton);
			copyNextButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorCopyNext"), 0.80f, 0.19f, 38f);
			copyNextButton.OnLeftClick += (_, _) => CopyToAdjacentFrame(1);
			ApplyReferenceActionStyle(copyNextButton, !playing && current >= 0 && current < frames.Count - 1);
			frameHost.Append(copyNextButton);
			frameHost.Recalculate();
		}

		private void SelectAnimationGroup(string state)
		{
			if (string.Equals(Pose.State, state, StringComparison.Ordinal))
				return;
			EndStroke();
			playing = false;
			settings.PoseIndex = GetGroupFrames(state)[0].GlobalIndex;
			pendingDiscard = false;
			BuildAnimationGroupButtons();
			BuildFrameButtons();
			RefreshLabels();
		}

		private void SelectFrame(int globalIndex)
		{
			if (globalIndex == settings.PoseIndex)
				return;
			EndStroke();
			playing = false;
			settings.PoseIndex = globalIndex;
			pendingDiscard = false;
			BuildFrameButtons();
			RefreshLabels();
		}

		private void CopyToAdjacentFrame(int delta)
		{
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
			EndStroke();
			playing = false;
			IReadOnlyList<(int GlobalIndex, SkinEditorPose Pose)> frames = GetGroupFrames(Pose.State);
			int current = frames.ToList().FindIndex(frame => frame.GlobalIndex == settings.PoseIndex);
			int targetIndex = current + delta;
			if ((uint)targetIndex >= frames.Count)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorCopyUnavailable",
					delta < 0 ? Language.GetTextValue("Mods.customskin.UI.EditorPreviousFrame") : Language.GetTextValue("Mods.customskin.UI.EditorNextFrame")), Color.Orange);
				BuildFrameButtons();
				return;
			}

			SkinEditorPose source = Pose;
			(int globalIndex, SkinEditorPose target) = frames[targetIndex];
			bool sharedSlot = document.GetSlot(activePart, source, settings.Gender) == document.GetSlot(activePart, target, settings.Gender);
			bool changed = !sharedSlot && document.CopyPartToPose(activePart, source, target, settings.Gender);
			settings.PoseIndex = globalIndex;
			pendingDiscard = false;
			BuildFrameButtons();
			BuildPaletteButtons();
			RefreshLabels();
			if (sharedSlot)
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorCopyShared", PartName(activePart), PoseName(source), PoseName(target)), Color.Orange);
			else if (changed)
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorCopiedPart", PartName(activePart), PoseName(source), PoseName(target)), Color.LightGreen);
			else
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorCopyUnchanged", PartName(activePart), PoseName(target)), Color.Silver);
		}

		private void BuildPartButtons()
		{
			partHost.RemoveAllChildren();
			SkinEditorPart[] parts = Enum.GetValues<SkinEditorPart>();
			float width = 1f / parts.Length;
			for (int index = 0; index < parts.Length; index++)
			{
				SkinEditorPart part = parts[index];
				bool selected = part == activePart;
				UITextPanel<string> button = new(PartName(part), 0.66f)
				{
					Left = new StyleDimension(0f, index * width),
					Width = new StyleDimension(-3f, width),
					Height = new StyleDimension(28f, 0f),
					TextColor = Color.White
				};
				ApplySelectedButtonStyle(button, selected);
				button.OnLeftClick += (_, _) => SelectPart(part);
				partHost.Append(button);
			}
			partHost.Recalculate();
		}

		private void SelectPart(SkinEditorPart part)
		{
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
			EndStroke();
			activePart = part;
			settings.Part = part;
			playing = false;
			BuildFrameButtons();
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
				SkinEditorToolIconButton button = new(tool, () => settings.Tool == tool, ToolName(tool))
				{
					Left = new StyleDimension(0f, index * 0.052f),
					Width = new StyleDimension(-3f, 0.048f),
					Height = new StyleDimension(32f, 0f)
				};
				button.OnLeftClick += (_, _) => SelectTool(tool);
				toolHost.Append(button);
			}

			UITextPanel<string> grid = StyledPercentButton(Language.GetTextValue("Mods.customskin.UI.EditorGrid"), 0.27f, 0.18f, 0f, settings.ShowGrid);
			grid.OnLeftClick += (_, _) => { settings.ShowGrid = !settings.ShowGrid; BuildToolButtons(); RefreshLabels(); };
			toolHost.Append(grid);
			UITextPanel<string> background = StyledPercentButton(Language.GetTextValue("Mods.customskin.UI.EditorHideBackground"), 0.46f, 0.21f, 0f, !settings.ShowBackground);
			background.OnLeftClick += (_, _) => { settings.ShowBackground = !settings.ShowBackground; BuildToolButtons(); RefreshLabels(); };
			toolHost.Append(background);
			confirmSelectionButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorSelectionConfirm"), 0.68f, 0.15f, 0f);
			confirmSelectionButton.OnLeftClick += (_, _) => CommitFloatingSelection();
			toolHost.Append(confirmSelectionButton);
			cancelSelectionButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorSelectionCancel"), 0.84f, 0.15f, 0f);
			cancelSelectionButton.OnLeftClick += (_, _) => CancelFloatingSelection();
			toolHost.Append(cancelSelectionButton);
			toolHost.Recalculate();
		}

		private void SelectTool(SkinEditorTool tool)
		{
			if (canvas.HasFloatingSelection && tool != settings.Tool)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
			EndStroke();
			playing = false;
			settings.Tool = tool;
			BuildToolButtons();
			RefreshLabels();
		}

		private void BuildPaletteButtons()
		{
			paletteHost.RemoveAllChildren();
			IReadOnlyList<SkinEditorColor> palette = recentColors
				.Concat(commonColors)
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
				referenceImage != null && settings.ReferenceVisible);
			referenceVisibleButton.OnLeftClick += (_, _) =>
			{
				if (referenceImage == null) return;
				settings.ReferenceVisible = !settings.ReferenceVisible;
				RefreshLabels();
			};
			controls.Append(referenceVisibleButton);
			UITextPanel<string> opacityDown = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceOpacityDown"), 0.61f, 0.18f, top);
			opacityDown.OnLeftClick += (_, _) => ChangeReferenceOpacity(-16);
			controls.Append(opacityDown);
			UITextPanel<string> opacityUp = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceOpacityUp"), 0.80f, 0.18f, top);
			opacityUp.OnLeftClick += (_, _) => ChangeReferenceOpacity(16);
			controls.Append(opacityUp);

			UITextPanel<string> mirror = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceMirror"), 0f, 0.25f, top + 38f);
			mirror.OnLeftClick += (_, _) => ToggleReferenceMirror();
			controls.Append(mirror);
			UITextPanel<string> reset = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceReset"), 0.26f, 0.25f, top + 38f);
			reset.OnLeftClick += (_, _) => ResetReferencePosition();
			controls.Append(reset);
			UITextPanel<string> folder = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceFolder"), 0.52f, 0.47f, top + 38f);
			folder.OnLeftClick += (_, _) => OpenReferenceFolder();
			controls.Append(folder);

			referenceOverlayButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceOverlay"), 0f, 0.48f, top + 74f);
			referenceOverlayButton.OnLeftClick += (_, _) => ApplyReference(replace: false);
			controls.Append(referenceOverlayButton);
			referenceReplaceButton = PercentButton(Language.GetTextValue("Mods.customskin.UI.EditorReferenceReplace"), 0.49f, 0.49f, top + 74f);
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
			if (!settings.ReferenceVisible)
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
				settings.ReferenceVisible = false;
				pendingDiscard = false;
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

		private void OnSelectionStateChanged()
		{
			BuildToolButtons();
			RefreshLabels();
			if (canvas.HasFloatingSelection)
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionReady"), Color.LightSkyBlue);
		}

		private void CommitFloatingSelection()
		{
			if (!canvas.HasFloatingSelection)
				return;
			bool changed = canvas.CommitFloatingSelection();
			pendingDiscard = false;
			BuildPaletteButtons();
			RefreshLabels();
			SetStatus(Language.GetTextValue(changed
				? "Mods.customskin.UI.EditorSelectionCommitted"
				: "Mods.customskin.UI.EditorSelectionUnchanged"), changed ? Color.LightGreen : Color.Silver);
		}

		private void CancelFloatingSelection()
		{
			if (!canvas.CancelFloatingSelection())
				return;
			RefreshLabels();
			SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionCancelled"), Color.Silver);
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
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
			playing = false;
			BuildFrameButtons();
			if (document.Undo()) { BuildPaletteButtons(); RefreshLabels(); }
		}

		private void Redo()
		{
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
			playing = false;
			BuildFrameButtons();
			if (document.Redo()) { BuildPaletteButtons(); RefreshLabels(); }
		}

		private void Save(bool apply)
		{
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
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

		private void RenameProject()
		{
			try
			{
				Creator.RenameProject(project, projectNameInput.CurrentString);
				projectNameInput.SetText(project.DisplayName);
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorProjectRenamed", project.DisplayName), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				projectNameInput.SetText(project.DisplayName);
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorProjectRenameFailed", exception.Message), Color.OrangeRed);
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
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
			if (document.IsDirty)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorReloadDirty"), Color.OrangeRed);
				return;
			}
			IngameFancyUI.OpenUIState(new SkinPixelEditorUI(project, Creator.LoadEditorDocument(project), Creator.ReadEditorSettings(project)));
		}

		private void Back()
		{
			if (canvas.HasFloatingSelection)
			{
				SetStatus(Language.GetTextValue("Mods.customskin.UI.EditorSelectionResolveFirst"), Color.Orange);
				return;
			}
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
			if (slotText == null) return;
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
			colorText.SetText(Language.GetTextValue("Mods.customskin.UI.EditorColor", color.R, color.G, color.B, color.A) + " · " +
				Language.GetTextValue("Mods.customskin.UI.EditorUndo") + $" {document.UndoStrokeCount}/{SkinEditorDocument.MaxUndoStrokes}");
			colorText.TextColor = ToColor(color.A == 0 ? new SkinEditorColor(180, 180, 180, 255) : color);
			if (confirmSelectionButton != null)
			{
				ApplyReferenceActionStyle(confirmSelectionButton, canvas.HasFloatingSelection);
				ApplyReferenceActionStyle(cancelSelectionButton, canvas.HasFloatingSelection);
			}
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
				ApplySelectedButtonStyle(referenceVisibleButton, referenceImage != null && settings.ReferenceVisible);
				bool canApplyReference = referenceImage != null && settings.ReferenceVisible &&
					!playing;
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
				_ => pose.LegFrame == 6
					? Language.GetTextValue("Mods.customskin.UI.EditorPoses.MountLegs")
					: Language.GetTextValue("Mods.customskin.UI.EditorPoses.CompatibilityLegs", pose.LegFrame)
			};
		}

		private static string FrameButtonName(SkinEditorPose pose, int groupIndex)
		{
			if (pose.State != "native-special")
				return (groupIndex + 1).ToString();
			return pose.LegFrame == 6
				? Language.GetTextValue("Mods.customskin.UI.EditorFrameMount")
				: Language.GetTextValue("Mods.customskin.UI.EditorFrameCompatibility", pose.LegFrame);
		}
	}

	internal sealed class SkinEditorToolIconButton : UIElement
	{
		private readonly SkinEditorTool tool;
		private readonly Func<bool> selected;
		private readonly string tooltip;

		public SkinEditorToolIconButton(SkinEditorTool tool, Func<bool> selected, string tooltip)
		{
			this.tool = tool;
			this.selected = selected;
			this.tooltip = tooltip;
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			if (IsMouseHovering)
				Main.LocalPlayer.mouseInterface = true;
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);
			Rectangle bounds = GetDimensions().ToRectangle();
			Color background = selected() ? new Color(40, 112, 102, 245) :
				IsMouseHovering ? new Color(83, 98, 155, 240) : new Color(63, 77, 130, 225);
			Color border = selected() ? new Color(255, 210, 92) : new Color(18, 24, 48);
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, bounds, background);
			DrawOutline(spriteBatch, bounds, border);

			int centerX = bounds.Center.X;
			int centerY = bounds.Center.Y;
			switch (tool)
			{
				case SkinEditorTool.Pencil:
					DrawPixel(spriteBatch, centerX - 9, centerY + 5, 7, new Color(18, 24, 42));
					DrawPixel(spriteBatch, centerX - 5, centerY + 1, 7, new Color(18, 24, 42));
					DrawPixel(spriteBatch, centerX - 1, centerY - 3, 7, new Color(18, 24, 42));
					DrawPixel(spriteBatch, centerX + 3, centerY - 7, 7, new Color(18, 24, 42));
					DrawPixel(spriteBatch, centerX - 7, centerY + 7, 3, new Color(255, 244, 190));
					DrawPixel(spriteBatch, centerX - 3, centerY + 3, 4, new Color(48, 215, 235));
					DrawPixel(spriteBatch, centerX + 1, centerY - 1, 4, new Color(58, 180, 232));
					DrawPixel(spriteBatch, centerX + 5, centerY - 5, 4, new Color(240, 246, 255));
					break;
				case SkinEditorTool.Eraser:
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(centerX - 8, centerY - 5, 16, 11), new Color(240, 102, 146));
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(centerX + 2, centerY - 5, 6, 11), new Color(245, 235, 238));
					DrawOutline(spriteBatch, new Rectangle(centerX - 8, centerY - 5, 16, 11), new Color(34, 25, 50));
					break;
				case SkinEditorTool.Eyedropper:
					DrawPixel(spriteBatch, centerX - 7, centerY + 5, 5, new Color(67, 220, 229));
					DrawPixel(spriteBatch, centerX - 3, centerY + 1, 4, new Color(198, 211, 224));
					DrawPixel(spriteBatch, centerX, centerY - 2, 4, new Color(198, 211, 224));
					DrawPixel(spriteBatch, centerX + 3, centerY - 5, 5, new Color(64, 70, 88));
					break;
				case SkinEditorTool.RectangleMove:
					DrawRectangleIcon(spriteBatch, centerX - 9, centerY - 7, new Color(90, 225, 235));
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(centerX - 2, centerY - 1, 11, 2), Color.White);
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(centerX + 5, centerY - 4, 2, 8), Color.White);
					break;
				case SkinEditorTool.RectangleCopy:
					DrawRectangleIcon(spriteBatch, centerX - 7, centerY - 5, new Color(100, 205, 245));
					DrawRectangleIcon(spriteBatch, centerX - 2, centerY - 9, new Color(255, 230, 110));
					break;
			}

			if (IsMouseHovering)
				UICommon.TooltipMouseText(tooltip);
		}

		private static void DrawPixel(SpriteBatch spriteBatch, int x, int y, int size, Color color) =>
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(x, y, size, size), color);

		private static void DrawRectangleIcon(SpriteBatch spriteBatch, int x, int y, Color color)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, new Rectangle(x, y, 12, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(x, y + 10, 12, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(x, y, 2, 12), color);
			spriteBatch.Draw(pixel, new Rectangle(x + 10, y, 2, 12), color);
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

	internal sealed class SkinEditorFreeColorPicker : UIElement
	{
		private const int SaturationSteps = 32;
		private const int ValueSteps = 12;
		private const int HueSteps = 24;
		private const int AlphaSteps = 16;
		private readonly Func<SkinEditorColor> currentColor;
		private readonly Action<SkinEditorColor> selected;
		private float hue;

		public SkinEditorFreeColorPicker(Func<SkinEditorColor> currentColor, Action<SkinEditorColor> selected)
		{
			this.currentColor = currentColor;
			this.selected = selected;
			(hue, _, _) = ToHsv(currentColor());
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			bool left = Main.mouseLeft;
			if (IsMouseHovering)
			{
				Main.LocalPlayer.mouseInterface = true;
				if (left)
					SelectAtMouse();
			}
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);
			Rectangle bounds = GetDimensions().ToRectangle();
			GetRegions(bounds, out Rectangle spectrum, out Rectangle hueStrip, out Rectangle alphaStrip);
			(float currentHue, float currentSaturation, float currentValue) = ToHsv(currentColor());
			if (currentSaturation > 0.001f)
				hue = currentHue;
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, bounds, new Color(12, 17, 34));

			for (int y = 0; y < ValueSteps; y++)
			{
				float value = 1f - (y + 0.5f) / ValueSteps;
				for (int x = 0; x < SaturationSteps; x++)
				{
					float saturation = (x + 0.5f) / SaturationSteps;
					Rectangle cell = StepRectangle(spectrum, x, y, SaturationSteps, ValueSteps);
					spriteBatch.Draw(TextureAssets.MagicPixel.Value, cell, ToColor(hue, saturation, value, 255));
				}
			}
			for (int y = 0; y < HueSteps; y++)
			{
				Rectangle cell = StepRectangle(hueStrip, 0, y, 1, HueSteps);
				spriteBatch.Draw(TextureAssets.MagicPixel.Value, cell, ToColor((y + 0.5f) / HueSteps, 1f, 1f, 255));
			}
			SkinEditorColor current = currentColor();
			for (int y = 0; y < AlphaSteps; y++)
			{
				Rectangle cell = StepRectangle(alphaStrip, 0, y, 1, AlphaSteps);
				Color checker = y % 2 == 0 ? new Color(205, 205, 205) : new Color(105, 105, 105);
				spriteBatch.Draw(TextureAssets.MagicPixel.Value, cell, checker);
				byte alpha = (byte)Math.Round(255f * (AlphaSteps - y - 0.5f) / AlphaSteps);
				spriteBatch.Draw(TextureAssets.MagicPixel.Value, cell, new Color(current.R, current.G, current.B, alpha));
			}

			DrawOutline(spriteBatch, spectrum, new Color(18, 24, 48));
			DrawOutline(spriteBatch, hueStrip, new Color(18, 24, 48));
			DrawOutline(spriteBatch, alphaStrip, new Color(18, 24, 48));
			int markerX = spectrum.X + (int)Math.Round(currentSaturation * Math.Max(1, spectrum.Width - 1));
			int markerY = spectrum.Y + (int)Math.Round((1f - currentValue) * Math.Max(1, spectrum.Height - 1));
			DrawOutline(spriteBatch, new Rectangle(markerX - 3, markerY - 3, 7, 7), Color.White);
			int hueY = hueStrip.Y + (int)Math.Round(hue * Math.Max(1, hueStrip.Height - 1));
			DrawOutline(spriteBatch, new Rectangle(hueStrip.X - 2, hueY - 2, hueStrip.Width + 4, 5), new Color(255, 210, 70));
			int alphaY = alphaStrip.Y + (int)Math.Round((1f - current.A / 255f) * Math.Max(1, alphaStrip.Height - 1));
			DrawOutline(spriteBatch, new Rectangle(alphaStrip.X - 2, alphaY - 2, alphaStrip.Width + 4, 5), Color.White);
			DrawAlphaLabel(spriteBatch, alphaStrip);
		}

		private void SelectAtMouse()
		{
			Rectangle bounds = GetDimensions().ToRectangle();
			GetRegions(bounds, out Rectangle spectrum, out Rectangle hueStrip, out Rectangle alphaStrip);
			SkinEditorColor current = currentColor();
			(_, float saturation, float value) = ToHsv(current);
			Point mouse = new(Main.mouseX, Main.mouseY);
			if (hueStrip.Contains(mouse))
			{
				hue = Math.Clamp((Main.mouseY - hueStrip.Y) / (float)Math.Max(1, hueStrip.Height - 1), 0f, 1f);
				if (saturation < 0.01f) saturation = 1f;
				if (value < 0.01f) value = 1f;
			}
			else if (spectrum.Contains(mouse))
			{
				saturation = Math.Clamp((Main.mouseX - spectrum.X) / (float)Math.Max(1, spectrum.Width - 1), 0f, 1f);
				value = 1f - Math.Clamp((Main.mouseY - spectrum.Y) / (float)Math.Max(1, spectrum.Height - 1), 0f, 1f);
			}
			else if (alphaStrip.Contains(mouse))
			{
				byte alpha = (byte)Math.Round(255f * (1f - Math.Clamp((Main.mouseY - alphaStrip.Y) /
					(float)Math.Max(1, alphaStrip.Height - 1), 0f, 1f)));
				selected(current with { A = alpha });
				return;
			}
			else
			{
				return;
			}
			selected(FromColor(ToColor(hue, saturation, value, current.A)));
		}

		private static void GetRegions(Rectangle bounds, out Rectangle spectrum, out Rectangle hueStrip, out Rectangle alphaStrip)
		{
			int stripWidth = Math.Max(14, bounds.Width / 15);
			spectrum = new Rectangle(bounds.X + 2, bounds.Y + 2, Math.Max(1, bounds.Width - stripWidth * 2 - 16), Math.Max(1, bounds.Height - 4));
			hueStrip = new Rectangle(spectrum.Right + 6, bounds.Y + 2, stripWidth, Math.Max(1, bounds.Height - 4));
			alphaStrip = new Rectangle(hueStrip.Right + 6, bounds.Y + 2, stripWidth, Math.Max(1, bounds.Height - 4));
		}

		private static Rectangle StepRectangle(Rectangle bounds, int x, int y, int columns, int rows)
		{
			int left = bounds.X + x * bounds.Width / columns;
			int right = bounds.X + (x + 1) * bounds.Width / columns;
			int top = bounds.Y + y * bounds.Height / rows;
			int bottom = bounds.Y + (y + 1) * bounds.Height / rows;
			return new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
		}

		private static (float Hue, float Saturation, float Value) ToHsv(SkinEditorColor color)
		{
			float red = color.R / 255f;
			float green = color.G / 255f;
			float blue = color.B / 255f;
			float maximum = Math.Max(red, Math.Max(green, blue));
			float minimum = Math.Min(red, Math.Min(green, blue));
			float delta = maximum - minimum;
			float calculatedHue = 0f;
			if (delta > 0.0001f)
			{
				if (maximum == red) calculatedHue = ((green - blue) / delta) % 6f;
				else if (maximum == green) calculatedHue = (blue - red) / delta + 2f;
				else calculatedHue = (red - green) / delta + 4f;
				calculatedHue /= 6f;
				if (calculatedHue < 0f) calculatedHue += 1f;
			}
			return (calculatedHue, maximum <= 0f ? 0f : delta / maximum, maximum);
		}

		private static Color ToColor(float hue, float saturation, float value, byte alpha)
		{
			float section = (hue - MathF.Floor(hue)) * 6f;
			float chroma = value * saturation;
			float secondary = chroma * (1f - Math.Abs(section % 2f - 1f));
			(float red, float green, float blue) = section switch
			{
				< 1f => (chroma, secondary, 0f),
				< 2f => (secondary, chroma, 0f),
				< 3f => (0f, chroma, secondary),
				< 4f => (0f, secondary, chroma),
				< 5f => (secondary, 0f, chroma),
				_ => (chroma, 0f, secondary)
			};
			float match = value - chroma;
			return new Color((byte)Math.Round((red + match) * 255f), (byte)Math.Round((green + match) * 255f),
				(byte)Math.Round((blue + match) * 255f), alpha);
		}

		private static SkinEditorColor FromColor(Color color) => new(color.R, color.G, color.B, color.A);

		private static void DrawAlphaLabel(SpriteBatch spriteBatch, Rectangle strip)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			int left = strip.Center.X - 4;
			int top = strip.Y + 4;
			Color color = Color.Black;
			spriteBatch.Draw(pixel, new Rectangle(left, top + 2, 2, 9), color);
			spriteBatch.Draw(pixel, new Rectangle(left + 6, top + 2, 2, 9), color);
			spriteBatch.Draw(pixel, new Rectangle(left + 2, top, 4, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(left + 2, top + 5, 4, 2), color);
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
		public event Action? Cancelled;

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
			{
				Unfocus();
				Submitted?.Invoke();
			}
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
				Cancelled?.Invoke();
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
		private readonly Action selectionChanged;
		private bool previousLeft;
		private bool drawing;
		private bool selecting;
		private bool draggingSelection;
		private int selectionStartX;
		private int selectionStartY;
		private int selectionEndX;
		private int selectionEndY;
		private int selectionDragOffsetX;
		private int selectionDragOffsetY;
		private FloatingSelection? floatingSelection;
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
			Action changed, Action<SkinEditorColor> colorPicked, Action selectionChanged)
		{
			this.document = document;
			this.settings = settings;
			this.part = part;
			this.pose = pose;
			this.reference = reference;
			this.referenceFrame = referenceFrame;
			this.changed = changed;
			this.colorPicked = colorPicked;
			this.selectionChanged = selectionChanged;
			OverflowHidden = true;
		}

		public bool HasFloatingSelection => floatingSelection != null;

		public void MoveFloatingSelection(int deltaX, int deltaY)
		{
			if (floatingSelection == null) return;
			floatingSelection.DestinationX = Math.Clamp(floatingSelection.DestinationX + deltaX,
				-floatingSelection.Width + 1, SkinEditorDocument.CellWidth - 1);
			floatingSelection.DestinationY = Math.Clamp(floatingSelection.DestinationY + deltaY,
				-floatingSelection.Height + 1, SkinEditorDocument.CellHeight - 1);
		}

		public bool CommitFloatingSelection()
		{
			if (floatingSelection == null) return false;
			FloatingSelection selection = floatingSelection;
			bool changedDocument = document.ApplyFloatingSelection(selection.Part, selection.SourcePose, pose(), selection.Gender,
				selection.SourceX, selection.SourceY, selection.Width, selection.Height, selection.Pixels,
				selection.DestinationX, selection.DestinationY, selection.SourceMirror, settings.MirrorPreview, selection.Move);
			floatingSelection = null;
			selecting = draggingSelection = false;
			if (changedDocument) changed();
			selectionChanged();
			return changedDocument;
		}

		public bool CancelFloatingSelection()
		{
			if (floatingSelection == null && !selecting) return false;
			floatingSelection = null;
			selecting = draggingSelection = false;
			selectionChanged();
			return true;
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
			if (settings.Tool is SkinEditorTool.RectangleMove or SkinEditorTool.RectangleCopy)
			{
				HandleSelectionInput(left);
				previousLeft = left;
				return;
			}
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

		private void HandleSelectionInput(bool left)
		{
			if (!Main.hasFocus)
			{
				draggingSelection = false;
				return;
			}
			if (floatingSelection != null)
			{
				if (!IsMouseHovering)
				{
					if (!left) draggingSelection = false;
					return;
				}
				if (left && !previousLeft && TryMouseVisualPixel(out int pressedX, out int pressedY) &&
					pressedX >= floatingSelection.DestinationX && pressedX < floatingSelection.DestinationX + floatingSelection.Width &&
					pressedY >= floatingSelection.DestinationY && pressedY < floatingSelection.DestinationY + floatingSelection.Height)
				{
					draggingSelection = true;
					selectionDragOffsetX = pressedX - floatingSelection.DestinationX;
					selectionDragOffsetY = pressedY - floatingSelection.DestinationY;
				}
				if (left && draggingSelection && TryMouseVisualPixel(out int dragX, out int dragY))
				{
					floatingSelection.DestinationX = Math.Clamp(dragX - selectionDragOffsetX,
						-floatingSelection.Width + 1, SkinEditorDocument.CellWidth - 1);
					floatingSelection.DestinationY = Math.Clamp(dragY - selectionDragOffsetY,
						-floatingSelection.Height + 1, SkinEditorDocument.CellHeight - 1);
				}
				if (!left) draggingSelection = false;
				return;
			}

			if (left && !previousLeft && IsMouseHovering && TryMouseVisualPixel(out int startX, out int startY))
			{
				selecting = true;
				selectionStartX = selectionEndX = startX;
				selectionStartY = selectionEndY = startY;
			}
			else if (left && selecting && TryMouseVisualPixel(out int endX, out int endY))
			{
				selectionEndX = endX;
				selectionEndY = endY;
			}
			else if (!left && selecting)
			{
				CaptureFloatingSelection();
				selecting = false;
			}
		}

		private void CaptureFloatingSelection()
		{
			int left = Math.Min(selectionStartX, selectionEndX);
			int top = Math.Min(selectionStartY, selectionEndY);
			int right = Math.Max(selectionStartX, selectionEndX);
			int bottom = Math.Max(selectionStartY, selectionEndY);
			int width = right - left + 1;
			int height = bottom - top + 1;
			SkinEditorColor[] pixels = new SkinEditorColor[width * height];
			bool hasVisiblePixel = false;
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					int visualX = left + x;
					int sourceX = settings.MirrorPreview ? SkinEditorDocument.CellWidth - 1 - visualX : visualX;
					SkinEditorColor color = document.ReadTargetPixel(part(), pose(), settings.Gender, sourceX, top + y);
					pixels[y * width + x] = color;
					hasVisiblePixel |= color.A > 0;
				}
			}
			if (!hasVisiblePixel)
			{
				selectionChanged();
				return;
			}
			floatingSelection = new FloatingSelection
			{
				Part = part(),
				SourcePose = pose(),
				Gender = settings.Gender,
				SourceMirror = settings.MirrorPreview,
				Move = settings.Tool == SkinEditorTool.RectangleMove,
				SourceX = left,
				SourceY = top,
				Width = width,
				Height = height,
				DestinationX = left,
				DestinationY = top,
				Pixels = pixels
			};
			selectionChanged();
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
					if (settings.ShowBackground)
					{
						Color checker = ((x + y) & 1) == 0 ? new Color(55, 59, 76) : new Color(75, 79, 96);
						spriteBatch.Draw(TextureAssets.MagicPixel.Value, pixel, checker);
					}
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
			DrawSelectionLayer(spriteBatch, canvas, zoom);
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

		private bool TryMouseVisualPixel(out int x, out int y)
		{
			Rectangle canvas = GetCanvasRectangle();
			int zoom = canvas.Width / SkinEditorDocument.CellWidth;
			x = (Main.mouseX - canvas.X) / zoom;
			y = (Main.mouseY - canvas.Y) / zoom;
			return canvas.Contains(Main.mouseX, Main.mouseY);
		}

		private void DrawSelectionLayer(SpriteBatch spriteBatch, Rectangle canvas, int zoom)
		{
			if (floatingSelection != null)
			{
				for (int y = 0; y < floatingSelection.Height; y++)
				{
					for (int x = 0; x < floatingSelection.Width; x++)
					{
						int destinationX = floatingSelection.DestinationX + x;
						int destinationY = floatingSelection.DestinationY + y;
						if ((uint)destinationX >= SkinEditorDocument.CellWidth || (uint)destinationY >= SkinEditorDocument.CellHeight)
							continue;
						SkinEditorColor color = floatingSelection.Pixels[y * floatingSelection.Width + x];
						if (color.A == 0) continue;
						Rectangle pixel = new(canvas.X + destinationX * zoom, canvas.Y + destinationY * zoom, zoom, zoom);
						spriteBatch.Draw(TextureAssets.MagicPixel.Value, pixel, new Color(color.R, color.G, color.B, color.A));
					}
				}
				DrawSelectionOutline(spriteBatch, canvas, zoom, floatingSelection.DestinationX, floatingSelection.DestinationY,
					floatingSelection.Width, floatingSelection.Height, new Color(255, 214, 64));
			}
			else if (selecting)
			{
				int left = Math.Min(selectionStartX, selectionEndX);
				int top = Math.Min(selectionStartY, selectionEndY);
				int width = Math.Abs(selectionEndX - selectionStartX) + 1;
				int height = Math.Abs(selectionEndY - selectionStartY) + 1;
				DrawSelectionOutline(spriteBatch, canvas, zoom, left, top, width, height, Color.White);
			}
		}

		private static void DrawSelectionOutline(SpriteBatch spriteBatch, Rectangle canvas, int zoom,
			int x, int y, int width, int height, Color color)
		{
			Rectangle requested = new(canvas.X + x * zoom, canvas.Y + y * zoom, width * zoom, height * zoom);
			Rectangle rectangle = Rectangle.Intersect(canvas, requested);
			if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Bottom - 2, rectangle.Width, 2), color);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, 2, rectangle.Height), color);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.Right - 2, rectangle.Y, 2, rectangle.Height), color);
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

		private sealed class FloatingSelection
		{
			public required SkinEditorPart Part { get; init; }
			public required SkinEditorPose SourcePose { get; init; }
			public required SkinEditorGender Gender { get; init; }
			public required bool SourceMirror { get; init; }
			public required bool Move { get; init; }
			public required int SourceX { get; init; }
			public required int SourceY { get; init; }
			public required int Width { get; init; }
			public required int Height { get; init; }
			public required SkinEditorColor[] Pixels { get; init; }
			public int DestinationX { get; set; }
			public int DestinationY { get; set; }
		}
	}
}
