using customskin.Common.Players;
using customskin.Common.Skins;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.UI;

namespace customskin.Common.UI
{
	internal sealed class SkinCreatorUI : UIState
	{
		private UIList projectList = null!;
		private UIElement previewHost = null!;
		private UIText detailsText = null!;
		private UIText statusText = null!;
		private SkinCreatorProject? selectedProject;
		private string? pendingDeleteProjectPath;

		private SkinCreatorSystem Creator => ModContent.GetInstance<SkinCreatorSystem>();

		public override void OnInitialize()
		{
			UIPanel panel = new()
			{
				HAlign = 0.5f,
				VAlign = 0.5f,
				Width = new StyleDimension(780f, 0f),
				Height = new StyleDimension(600f, 0f),
				BackgroundColor = new Color(35, 45, 85, 245)
			};
			Append(panel);

			UIText title = new(Language.GetTextValue("Mods.customskin.UI.CreatorTitle"), 1.2f)
			{
				HAlign = 0.5f,
				Top = new StyleDimension(8f, 0f)
			};
			panel.Append(title);

			previewHost = new UIElement
			{
				Left = new StyleDimension(28f, 0f),
				Top = new StyleDimension(65f, 0f),
				Width = new StyleDimension(250f, 0f),
				Height = new StyleDimension(250f, 0f)
			};
			panel.Append(previewHost);

			detailsText = new UIText(string.Empty, 0.75f)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(325f, 0f),
				Width = new StyleDimension(285f, 0f),
				IsWrapped = true,
				TextOriginX = 0f
			};
			panel.Append(detailsText);

			projectList = new UIList
			{
				Left = new StyleDimension(320f, 0f),
				Top = new StyleDimension(58f, 0f),
				Width = new StyleDimension(400f, 0f),
				Height = new StyleDimension(365f, 0f),
				ListPadding = 6f
			};
			panel.Append(projectList);

			UIScrollbar scrollbar = new()
			{
				Left = new StyleDimension(728f, 0f),
				Top = new StyleDimension(58f, 0f),
				Height = new StyleDimension(365f, 0f)
			};
			panel.Append(scrollbar);
			projectList.SetScrollbar(scrollbar);

			UITextPanel<string> createButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.CreateTemplate"), 18f, 490f, 96f);
			createButton.OnLeftClick += (_, _) => CreateTemplate();
			panel.Append(createButton);

			UITextPanel<string> editButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.OpenPixelEditor"), 122f, 490f, 96f);
			editButton.OnLeftClick += (_, _) => OpenPixelEditor();
			panel.Append(editButton);

			UITextPanel<string> openButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.OpenProjectFolder"), 226f, 490f, 96f);
			openButton.OnLeftClick += (_, _) => OpenProjectFolder();
			panel.Append(openButton);

			UITextPanel<string> refreshButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.RefreshAndUse"), 330f, 490f, 96f);
			refreshButton.OnLeftClick += (_, _) => RefreshAndUse();
			panel.Append(refreshButton);

			UITextPanel<string> exportButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.ExportPackage"), 434f, 490f, 96f);
			exportButton.OnLeftClick += (_, _) => ExportPackage();
			panel.Append(exportButton);

			UITextPanel<string> deleteButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.DeleteProject"), 538f, 490f, 96f);
			deleteButton.BackgroundColor = new Color(120, 45, 45, 220);
			deleteButton.OnLeftClick += (_, _) => DeleteProject();
			panel.Append(deleteButton);

			UITextPanel<string> backButton = CreateButton(Language.GetTextValue("UI.Back"), 642f, 490f, 96f);
			backButton.OnLeftClick += (_, _) => IngameFancyUI.OpenUIState(new SkinManagerUI());
			panel.Append(backButton);

			statusText = new UIText(string.Empty, 0.65f)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(532f, 0f),
				Width = new StyleDimension(716f, 0f),
				Height = new StyleDimension(58f, 0f),
				IsWrapped = true,
				TextOriginX = 0f
			};
			panel.Append(statusText);
		}

		public override void OnActivate()
		{
			SetStatus(Language.GetTextValue("Mods.customskin.UI.CreatorHint", Creator.ProjectsPath), Color.Silver);
			RefreshProjects();
		}

		public override void OnDeactivate()
		{
			ModContent.GetInstance<SkinTextureSystem>().ClearCreatorPreview();
			pendingDeleteProjectPath = null;
		}

		private void RefreshProjects(string? preferredDirectory = null)
		{
			string? selectedPath = preferredDirectory ?? selectedProject?.DirectoryPath;
			IReadOnlyList<SkinCreatorProject> projects = Creator.GetProjects();
			selectedProject = projects.FirstOrDefault(project =>
				string.Equals(project.DirectoryPath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? projects.FirstOrDefault();

			projectList.Clear();
			foreach (SkinCreatorProject project in projects)
				projectList.Add(CreateProjectRow(project, selectedProject?.DirectoryPath == project.DirectoryPath));
			if (projects.Count == 0)
			{
				projectList.Add(new UIText(Language.GetTextValue("Mods.customskin.UI.NoCreatorProjects"), 0.85f)
				{
					Width = new StyleDimension(380f, 0f),
					IsWrapped = true,
					TextOriginX = 0f
				});
			}

			RefreshDetailsAndPreview();
		}

		private UIElement CreateProjectRow(SkinCreatorProject project, bool selected)
		{
			string state = project.IsValid
				? Language.GetTextValue(project.LastImportedHash == null ? "Mods.customskin.UI.ProjectNotLoaded" : "Mods.customskin.UI.ProjectLoaded")
				: Language.GetTextValue("Mods.customskin.UI.ProjectHasError");
			UITextPanel<string> row = new($"{project.DisplayName}\n{project.FolderName} · {state}", 0.72f)
			{
				Width = new StyleDimension(-4f, 1f),
				Height = new StyleDimension(52f, 0f),
				BackgroundColor = selected ? new Color(55, 120, 90, 230) : new Color(55, 70, 120, 220),
				TextColor = project.IsValid ? Color.White : Color.OrangeRed
			};
			// WithFadedMouseOver restores the default blue background on mouse-out.
			// Do not attach it to the selected row or its green state only flashes
			// for the duration of the click/hover.
			if (!selected)
				row.WithFadedMouseOver();
			row.OnLeftClick += (_, _) =>
			{
				selectedProject = project;
				pendingDeleteProjectPath = null;
				RefreshProjects(project.DirectoryPath);
				if (!project.IsValid)
					SetProjectFailure(project.ErrorCode, project.ErrorMessage);
			};
			return row;
		}

		private void RefreshDetailsAndPreview()
		{
			previewHost.RemoveAllChildren();
			if (selectedProject == null)
			{
				detailsText.SetText(Language.GetTextValue("Mods.customskin.UI.NoCreatorProjectSelected"));
				return;
			}

			string loaded = selectedProject.LastImportedHash == null
				? Language.GetTextValue("Mods.customskin.UI.ProjectNotLoaded")
				: selectedProject.LastImportedHash[..Math.Min(8, selectedProject.LastImportedHash.Length)];
			detailsText.SetText(Language.GetTextValue("Mods.customskin.UI.CreatorProjectDetails",
				selectedProject.DisplayName,
				selectedProject.FolderName,
				loaded));

			if (!selectedProject.IsValid || !Main.LocalPlayer.active)
				return;

			try
			{
				string previewHash = Creator.LoadPreview(selectedProject);
				Player previewPlayer = Main.LocalPlayer.SerializedClone();
				previewPlayer.dead = false;
				previewPlayer.active = true;
				SkinPlayer previewSkinPlayer = previewPlayer.GetModPlayer<SkinPlayer>();
				previewSkinPlayer.IsManagerPreview = true;
				previewSkinPlayer.IsCreatorPreview = true;
				previewSkinPlayer.ManagerPreviewSkinHash = previewHash;
				UICharacter preview = new(previewPlayer, animated: false, hasBackPanel: true, characterScale: 2.4f)
				{
					HAlign = 0.5f,
					VAlign = 0.45f
				};
				previewHost.Append(preview);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "project.preview", exception.Message);
			}
		}

		private void CreateTemplate()
		{
			try
			{
				pendingDeleteProjectPath = null;
				SkinCreatorProject project = Creator.CreateTemplate();
				RefreshProjects(project.DirectoryPath);
				SetStatus(Language.GetTextValue("Mods.customskin.UI.TemplateCreated", project.DirectoryPath), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "project.create", exception.Message);
			}
		}

		private void DeleteProject()
		{
			if (!RequireSelection())
				return;
			if (!string.Equals(pendingDeleteProjectPath, selectedProject!.DirectoryPath, StringComparison.OrdinalIgnoreCase))
			{
				pendingDeleteProjectPath = selectedProject.DirectoryPath;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ConfirmDeleteProject", selectedProject.DisplayName), Color.OrangeRed);
				return;
			}

			try
			{
				string name = selectedProject.DisplayName;
				string destination = Creator.DeleteProject(selectedProject);
				selectedProject = null;
				pendingDeleteProjectPath = null;
				RefreshProjects();
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ProjectDeleted", name, destination), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "project.delete", exception.Message);
			}
		}

		private void OpenProjectFolder()
		{
			if (!RequireSelection())
				return;
			try
			{
				Creator.OpenProjectDirectory(selectedProject!);
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ProjectFolderOpened", selectedProject!.DirectoryPath), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "project.open", exception.Message);
			}
		}

		private void OpenPixelEditor()
		{
			if (!RequireSelection())
				return;
			try
			{
				SkinEditorDocument document = Creator.LoadEditorDocument(selectedProject!);
				SkinEditorSettings settings = Creator.ReadEditorSettings(selectedProject!);
				IngameFancyUI.OpenUIState(new SkinPixelEditorUI(selectedProject!, document, settings));
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "editor.open", exception.Message);
			}
		}

		private void RefreshAndUse()
		{
			if (!RequireSelection())
				return;
			try
			{
				SkinImportResult result = Creator.RefreshAndUse(selectedProject!);
				if (!result.Success)
				{
					SetProjectFailure(result.ErrorCode, result.ErrorMessage);
					return;
				}
				RefreshProjects(selectedProject!.DirectoryPath);
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ProjectRefreshed", result.Skin!.Manifest.Name), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "project.refresh", exception.Message);
			}
		}

		private void ExportPackage()
		{
			if (!RequireSelection())
				return;
			try
			{
				string output = Creator.ExportForSharing(selectedProject!);
				Creator.OpenExportsDirectory();
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ProjectExported", output), Color.LightGreen);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetProjectFailure(exception is SkinPackageException package ? package.ErrorCode : "project.export", exception.Message);
			}
		}

		private bool RequireSelection()
		{
			if (selectedProject != null)
				return true;
			SetStatus(Language.GetTextValue("Mods.customskin.UI.NoCreatorProjectSelected"), Color.Orange);
			return false;
		}

		private void SetProjectFailure(string? errorCode, string? errorMessage)
		{
			string code = SkinImportErrorPresentation.SanitizeForDisplay(errorCode ?? "unknown", 80);
			string detail = SkinErrorText.LocalizedDetail(errorCode, errorMessage);
			string hint = Language.GetTextValue(SkinImportErrorPresentation.GetHintKey(errorCode));
			SetStatus(Language.GetTextValue("Mods.customskin.UI.ProjectFailed", code, detail, hint), Color.OrangeRed);
		}

		private void SetStatus(string message, Color color)
		{
			statusText.SetText(message);
			statusText.TextColor = color;
		}

		private static UITextPanel<string> CreateButton(string text, float left, float top, float width)
		{
			UITextPanel<string> button = new(text, 0.7f)
			{
				Left = new StyleDimension(left, 0f),
				Top = new StyleDimension(top, 0f),
				Width = new StyleDimension(width, 0f),
				Height = new StyleDimension(38f, 0f)
			};
			button.WithFadedMouseOver();
			return button;
		}
	}
}
