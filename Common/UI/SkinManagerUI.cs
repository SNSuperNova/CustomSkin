using customskin.Common.Players;
using customskin.Common.Skins;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.UI;
using Terraria.Utilities.FileBrowser;

namespace customskin.Common.UI
{
	internal sealed class SkinManagerUI : UIState
	{
		private UIList skinList = null!;
		private UIElement previewHost = null!;
		private UIText selectedText = null!;
		private UIText statusText = null!;
		private UITextPanel<string> trashButton = null!;
		private string? pendingDeleteHash;
		private string? pendingPermanentDeletePath;
		private bool showingTrash;

		private SkinRepositorySystem Repository => ModContent.GetInstance<SkinRepositorySystem>();

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

			UIText title = new(Language.GetTextValue("Mods.customskin.UI.ManagerTitle"), 1.2f)
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

			selectedText = new UIText(string.Empty, 0.78f)
			{
				Left = new StyleDimension(18f, 0f),
				Top = new StyleDimension(325f, 0f),
				Width = new StyleDimension(285f, 0f),
				IsWrapped = true,
				TextOriginX = 0f
			};
			panel.Append(selectedText);

			skinList = new UIList
			{
				Left = new StyleDimension(320f, 0f),
				Top = new StyleDimension(58f, 0f),
				Width = new StyleDimension(400f, 0f),
				Height = new StyleDimension(365f, 0f),
				ListPadding = 6f
			};
			panel.Append(skinList);

			UIScrollbar scrollbar = new()
			{
				Left = new StyleDimension(728f, 0f),
				Top = new StyleDimension(58f, 0f),
				Height = new StyleDimension(365f, 0f)
			};
			panel.Append(scrollbar);
			skinList.SetScrollbar(scrollbar);

			UITextPanel<string> importButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.ImportSkin"), 18f, 490f, 132f);
			importButton.BackgroundColor = new Color(45, 105, 75, 220);
			importButton.OnLeftClick += (_, _) => ImportSkin();
			panel.Append(importButton);

			UITextPanel<string> creatorButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.OpenCreator"), 160f, 490f, 132f);
			creatorButton.OnLeftClick += (_, _) => IngameFancyUI.OpenUIState(new SkinCreatorUI());
			panel.Append(creatorButton);

			trashButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.ViewTrash"), 302f, 490f, 132f);
			trashButton.OnLeftClick += (_, _) => ToggleTrash();
			panel.Append(trashButton);

			UITextPanel<string> clearButton = CreateButton(Language.GetTextValue("Mods.customskin.UI.ClearSelection"), 444f, 490f, 132f);
			clearButton.OnLeftClick += (_, _) =>
			{
				Repository.ClearSelection();
				pendingDeleteHash = null;
				pendingPermanentDeletePath = null;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.SelectionCleared"), Color.LightGreen);
				RefreshContents();
			};
			panel.Append(clearButton);

			UITextPanel<string> closeButton = CreateButton(Language.GetTextValue("UI.Back"), 586f, 490f, 132f);
			closeButton.OnLeftClick += (_, _) => IngameFancyUI.Close();
			panel.Append(closeButton);

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
			RefreshContents();
			if (string.IsNullOrEmpty(statusText.Text))
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ImportSkinHint"), Color.Silver);
		}

		internal void Refresh()
		{
			pendingDeleteHash = null;
			pendingPermanentDeletePath = null;
			RefreshContents();
			SetStatus(Language.GetTextValue("Mods.customskin.UI.ImportSkinHint"), Color.Silver);
		}

		private void RefreshContents()
		{
			SkinRecord? selected = Repository.SelectedSkin;
			SkinTextureSystem textures = ModContent.GetInstance<SkinTextureSystem>();
			string characterScope = Language.GetTextValue("Mods.customskin.UI.CharacterSelectionScope", Main.LocalPlayer.name);
			if (selected == null)
			{
				selectedText.SetText($"{characterScope}\n{Language.GetTextValue("Mods.customskin.UI.NoSelection")}");
			}
			else
			{
				string details = Language.GetTextValue("Mods.customskin.UI.SelectedDetails",
					selected.Manifest.Name,
					selected.Manifest.Author,
					selected.Manifest.Version,
					selected.ShortHash);
				string diagnostics = Language.GetTextValue("Mods.customskin.UI.TextureDiagnostics",
					textures.LiveTextureCount,
					textures.CreatedTextureCount,
					textures.DisposedTextureCount);
				selectedText.SetText($"{characterScope}\n{details}\n{diagnostics}");
			}

			skinList.Clear();
			if (showingTrash)
			{
				IReadOnlyList<SkinRecord> trashSkins = Repository.TrashSkins;
				foreach (SkinRecord skin in trashSkins)
					skinList.Add(CreateTrashRow(skin));
				if (trashSkins.Count == 0)
					AddEmptyListMessage("Mods.customskin.UI.EmptyTrash");
			}
			else
			{
				foreach (SkinRecord skin in Repository.Skins)
					skinList.Add(CreateSkinRow(skin, selected?.Hash == skin.Hash));
				if (Repository.Skins.Count == 0)
					AddEmptyListMessage("Mods.customskin.UI.EmptyLibrary");
			}

			RefreshPreview();
		}

		private void AddEmptyListMessage(string localizationKey)
		{
			skinList.Add(new UIText(Language.GetTextValue(localizationKey), 0.85f)
			{
				Width = new StyleDimension(380f, 0f),
				IsWrapped = true,
				TextOriginX = 0f
			});
		}

		private UIElement CreateSkinRow(SkinRecord skin, bool selected)
		{
			UIPanel row = new()
			{
				Width = new StyleDimension(-4f, 1f),
				Height = new StyleDimension(58f, 0f),
				BackgroundColor = selected ? new Color(55, 120, 90, 230) : new Color(55, 70, 120, 220)
			};

			UITextPanel<string> selectButton = new($"{skin.Manifest.Name}\n{skin.Manifest.Author} · {skin.Manifest.Version} · {skin.ShortHash}", 0.72f)
			{
				Width = new StyleDimension(-76f, 1f),
				Height = new StyleDimension(42f, 0f),
				HAlign = 0f,
				VAlign = 0.5f,
				TextColor = Color.White
			};
			selectButton.WithFadedMouseOver();
			selectButton.OnLeftClick += (_, _) => SelectSkin(skin);
			row.Append(selectButton);

			UITextPanel<string> deleteButton = new(Language.GetTextValue("Mods.customskin.UI.Delete"), 0.7f)
			{
				Left = new StyleDimension(-66f, 1f),
				Width = new StyleDimension(62f, 0f),
				Height = new StyleDimension(34f, 0f),
				VAlign = 0.5f,
				BackgroundColor = new Color(120, 45, 45, 220)
			};
			deleteButton.WithFadedMouseOver(Color.IndianRed, new Color(120, 45, 45, 220));
			deleteButton.OnLeftClick += (_, _) => DeleteSkin(skin);
			row.Append(deleteButton);
			return row;
		}

		private UIElement CreateTrashRow(SkinRecord skin)
		{
			UIPanel row = new()
			{
				Width = new StyleDimension(-4f, 1f),
				Height = new StyleDimension(62f, 0f),
				BackgroundColor = new Color(80, 65, 95, 225)
			};

			UIText details = new($"{skin.Manifest.Name}\n{skin.Manifest.Author} · {skin.Manifest.Version} · {skin.ShortHash}", 0.68f)
			{
				Left = new StyleDimension(8f, 0f),
				Width = new StyleDimension(-154f, 1f),
				VAlign = 0.5f,
				IsWrapped = true,
				TextOriginX = 0f
			};
			row.Append(details);

			UITextPanel<string> restoreButton = new(Language.GetTextValue("Mods.customskin.UI.Restore"), 0.65f)
			{
				Left = new StyleDimension(-142f, 1f),
				Width = new StyleDimension(66f, 0f),
				Height = new StyleDimension(34f, 0f),
				VAlign = 0.5f,
				BackgroundColor = new Color(45, 105, 75, 220)
			};
			restoreButton.WithFadedMouseOver(Color.LightGreen, new Color(45, 105, 75, 220));
			restoreButton.OnLeftClick += (_, _) => RestoreTrashSkin(skin);
			row.Append(restoreButton);

			UITextPanel<string> purgeButton = new(Language.GetTextValue("Mods.customskin.UI.PermanentDelete"), 0.58f)
			{
				Left = new StyleDimension(-72f, 1f),
				Width = new StyleDimension(68f, 0f),
				Height = new StyleDimension(34f, 0f),
				VAlign = 0.5f,
				BackgroundColor = new Color(120, 45, 45, 220)
			};
			purgeButton.WithFadedMouseOver(Color.IndianRed, new Color(120, 45, 45, 220));
			purgeButton.OnLeftClick += (_, _) => PermanentlyDeleteTrashSkin(skin);
			row.Append(purgeButton);
			return row;
		}

		private void RefreshPreview()
		{
			previewHost.RemoveAllChildren();
			if (Repository.SelectedSkin == null || !Main.LocalPlayer.active)
				return;

			Player previewPlayer = Main.LocalPlayer.SerializedClone();
			previewPlayer.dead = false;
			previewPlayer.active = true;
			previewPlayer.GetModPlayer<SkinPlayer>().IsManagerPreview = true;
			UICharacter preview = new(previewPlayer, animated: false, hasBackPanel: true, characterScale: 2.4f)
			{
				HAlign = 0.5f,
				VAlign = 0.45f
			};
			previewHost.Append(preview);
		}

		private void SelectSkin(SkinRecord skin)
		{
			try
			{
				Repository.SelectSkin(skin.Hash);
				pendingDeleteHash = null;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.Selected", skin.Manifest.Name), Color.LightGreen);
				RefreshContents();
			}
			catch (Exception exception)
			{
				SetStatus(SkinErrorText.FormatOperationFailure(exception), Color.OrangeRed);
			}
		}

		private void DeleteSkin(SkinRecord skin)
		{
			if (pendingDeleteHash != skin.Hash)
			{
				pendingDeleteHash = skin.Hash;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ConfirmDelete", skin.Manifest.Name), Color.Orange);
				return;
			}

			try
			{
				Repository.DeleteSkin(skin.Hash);
				pendingDeleteHash = null;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.Deleted", skin.Manifest.Name), Color.LightGreen);
				RefreshContents();
			}
			catch (Exception exception)
			{
				SetStatus(SkinErrorText.FormatOperationFailure(exception), Color.OrangeRed);
			}
		}

		private void ToggleTrash()
		{
			showingTrash = !showingTrash;
			pendingDeleteHash = null;
			pendingPermanentDeletePath = null;
			trashButton.SetText(Language.GetTextValue(showingTrash
				? "Mods.customskin.UI.ViewLibrary"
				: "Mods.customskin.UI.ViewTrash"));
			SetStatus(Language.GetTextValue(showingTrash
				? "Mods.customskin.UI.TrashHint"
				: "Mods.customskin.UI.ImportSkinHint", Repository.TrashPath), Color.Silver);
			RefreshContents();
		}

		private void RestoreTrashSkin(SkinRecord skin)
		{
			try
			{
				Repository.RestoreTrashSkin(skin);
				pendingPermanentDeletePath = null;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.Restored", skin.Manifest.Name), Color.LightGreen);
				RefreshContents();
			}
			catch (Exception exception)
			{
				SetStatus(SkinErrorText.FormatOperationFailure(exception), Color.OrangeRed);
			}
		}

		private void PermanentlyDeleteTrashSkin(SkinRecord skin)
		{
			if (!string.Equals(pendingPermanentDeletePath, skin.DirectoryPath, StringComparison.OrdinalIgnoreCase))
			{
				pendingPermanentDeletePath = skin.DirectoryPath;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ConfirmPermanentDelete", skin.Manifest.Name), Color.OrangeRed);
				return;
			}

			try
			{
				Repository.PermanentlyDeleteTrashSkin(skin);
				pendingPermanentDeletePath = null;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.PermanentlyDeleted", skin.Manifest.Name), Color.LightGreen);
				RefreshContents();
			}
			catch (Exception exception)
			{
				SetStatus(SkinErrorText.FormatOperationFailure(exception), Color.OrangeRed);
			}
		}

		private void ImportSkin()
		{
			try
			{
				string selected = FileBrowser.OpenFilePanel(Language.GetTextValue("Mods.customskin.UI.ImportSkinDialogTitle"), "cskin");
				if (string.IsNullOrWhiteSpace(selected))
					return;

				SkinImportResult result = Repository.ImportPackage(selected);
				if (!result.Success)
				{
					SetImportFailure(result);
					return;
				}

				Repository.SelectSkin(result.Skin!.Hash);
				showingTrash = false;
				trashButton.SetText(Language.GetTextValue("Mods.customskin.UI.ViewTrash"));
				SetStatus(Language.GetTextValue(result.WasNew
					? "Mods.customskin.UI.ImportedSkin"
					: "Mods.customskin.UI.ImportedExistingSkin", result.Skin.Manifest.Name), Color.LightGreen);

				pendingDeleteHash = null;
				pendingPermanentDeletePath = null;
				RefreshContents();
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				SetStatus(SkinErrorText.FormatOperationFailure(exception), Color.OrangeRed);
			}
		}

		private void InstallExample()
		{
			try
			{
				SkinImportResult result = Repository.InstallBundledExample();
				if (!result.Success)
				{
					SetImportFailure(result);
					return;
				}

				Repository.SelectSkin(result.Skin!.Hash);
				pendingDeleteHash = null;
				SetStatus(Language.GetTextValue("Mods.customskin.UI.ExampleReady"), Color.LightGreen);
				RefreshContents();
			}
			catch (Exception exception)
			{
				SetStatus(SkinErrorText.FormatOperationFailure(exception), Color.OrangeRed);
			}
		}

		private void SetStatus(string message, Color color)
		{
			statusText.SetText(message);
			statusText.TextColor = color;
		}

		private void SetImportFailure(SkinImportResult result)
		{
			string code = SkinImportErrorPresentation.SanitizeForDisplay(result.ErrorCode ?? "unknown", 80);
			string file = SkinImportErrorPresentation.SanitizeForDisplay(result.FileName, 120);
			string detail = SkinErrorText.LocalizedDetail(result.ErrorCode, result.ErrorMessage);
			string hint = Language.GetTextValue(SkinImportErrorPresentation.GetHintKey(result.ErrorCode));
			SetStatus(Language.GetTextValue("Mods.customskin.UI.ImportFailed", file, code, detail, hint), Color.OrangeRed);
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
