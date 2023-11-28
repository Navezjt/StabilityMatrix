﻿using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Models.TagCompletion;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(PromptCard))]
[ManagedService]
[Transient]
public partial class PromptCardViewModel
    : LoadableViewModelBase,
        IParametersLoadableState,
        IComfyStep
{
    private readonly IModelIndexService modelIndexService;

    /// <summary>
    /// Cache of prompt text to tokenized Prompt
    /// </summary>
    private LRUCache<string, Prompt> PromptCache { get; } = new(4);

    public ICompletionProvider CompletionProvider { get; }
    public ITokenizerProvider TokenizerProvider { get; }
    public SharedState SharedState { get; }

    public TextDocument PromptDocument { get; } = new();
    public TextDocument NegativePromptDocument { get; } = new();

    [ObservableProperty]
    private bool isAutoCompletionEnabled;

    /// <inheritdoc />
    public PromptCardViewModel(
        ICompletionProvider completionProvider,
        ITokenizerProvider tokenizerProvider,
        ISettingsManager settingsManager,
        IModelIndexService modelIndexService,
        SharedState sharedState
    )
    {
        this.modelIndexService = modelIndexService;
        CompletionProvider = completionProvider;
        TokenizerProvider = tokenizerProvider;
        SharedState = sharedState;

        settingsManager.RelayPropertyFor(
            this,
            vm => vm.IsAutoCompletionEnabled,
            settings => settings.IsPromptCompletionEnabled,
            true
        );
    }

    /// <summary>
    /// Applies the prompt step.
    /// Requires:
    /// <list type="number">
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.BaseModel"/></item>
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.BaseClip"/></item>
    /// </list>
    /// Provides:
    /// <list type="number">
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.BaseConditioning"/></item>
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.BaseNegativeConditioning"/></item>
    /// </list>
    /// </summary>
    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        // Load prompts
        var positivePrompt = GetPrompt();
        positivePrompt.Process();
        var negativePrompt = GetNegativePrompt();
        negativePrompt.Process();

        // If need to load loras, add a group
        if (positivePrompt.ExtraNetworks.Count > 0)
        {
            var loras = positivePrompt.GetExtraNetworksAsLocalModels(modelIndexService).ToList();
            // Add group to load loras onto model and clip in series
            var lorasGroup = e.Builder.Group_LoraLoadMany(
                "Loras",
                e.Builder.Connections.BaseModel ?? throw new ArgumentException("BaseModel is null"),
                e.Builder.Connections.BaseClip ?? throw new ArgumentException("BaseClip is null"),
                loras
            );

            // Set last outputs as base model and clip
            e.Builder.Connections.BaseModel = lorasGroup.Output1;
            e.Builder.Connections.BaseClip = lorasGroup.Output2;

            // Refiner loras
            if (e.Builder.Connections.RefinerModel is not null)
            {
                // Add group to load loras onto refiner model and clip in series
                var lorasGroupRefiner = e.Builder.Group_LoraLoadMany(
                    "Refiner_Loras",
                    e.Builder.Connections.RefinerModel
                        ?? throw new ArgumentException("RefinerModel is null"),
                    e.Builder.Connections.RefinerClip
                        ?? throw new ArgumentException("RefinerClip is null"),
                    loras
                );

                // Set last outputs as refiner model and clip
                e.Builder.Connections.RefinerModel = lorasGroupRefiner.Output1;
                e.Builder.Connections.RefinerClip = lorasGroupRefiner.Output2;
            }
        }

        // Clips
        var positiveClip = e.Builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "PositiveCLIP",
                Clip = e.Builder.Connections.BaseClip!,
                Text = positivePrompt.ProcessedText
            }
        );
        var negativeClip = e.Builder.Nodes.AddTypedNode(
            new ComfyNodeBuilder.CLIPTextEncode
            {
                Name = "NegativeCLIP",
                Clip = e.Builder.Connections.BaseClip!,
                Text = negativePrompt.ProcessedText
            }
        );

        // Set base conditioning from Clips
        e.Builder.Connections.BaseConditioning = positiveClip.Output;
        e.Builder.Connections.BaseNegativeConditioning = negativeClip.Output;

        // Refiner Clips
        if (e.Builder.Connections.RefinerModel is not null)
        {
            var positiveClipRefiner = e.Builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.CLIPTextEncode
                {
                    Name = "Refiner_PositiveCLIP",
                    Clip =
                        e.Builder.Connections.RefinerClip
                        ?? throw new ArgumentException("RefinerClip is null"),
                    Text = positivePrompt.ProcessedText
                }
            );
            var negativeClipRefiner = e.Builder.Nodes.AddTypedNode(
                new ComfyNodeBuilder.CLIPTextEncode
                {
                    Name = "Refiner_NegativeCLIP",
                    Clip =
                        e.Builder.Connections.RefinerClip
                        ?? throw new ArgumentException("RefinerClip is null"),
                    Text = negativePrompt.ProcessedText
                }
            );

            // Set refiner conditioning from Clips
            e.Builder.Connections.RefinerConditioning = positiveClipRefiner.Output;
            e.Builder.Connections.RefinerNegativeConditioning = negativeClipRefiner.Output;
        }
    }

    /// <summary>
    /// Gets the tokenized Prompt for given text and caches it
    /// </summary>
    private Prompt GetOrCachePrompt(string text)
    {
        // Try get from cache
        if (PromptCache.Get(text, out var cachedPrompt))
        {
            return cachedPrompt!;
        }
        var prompt = Prompt.FromRawText(text, TokenizerProvider);
        PromptCache.Add(text, prompt);
        return prompt;
    }

    /// <summary>
    /// Processes current positive prompt text into a Prompt object
    /// </summary>
    public Prompt GetPrompt() => GetOrCachePrompt(PromptDocument.Text);

    /// <summary>
    /// Processes current negative prompt text into a Prompt object
    /// </summary>
    public Prompt GetNegativePrompt() => GetOrCachePrompt(NegativePromptDocument.Text);

    /// <summary>
    /// Validates both prompts, shows an error dialog if invalid
    /// </summary>
    public async Task<bool> ValidatePrompts()
    {
        var promptText = PromptDocument.Text;
        var negPromptText = NegativePromptDocument.Text;

        try
        {
            var prompt = GetOrCachePrompt(promptText);
            prompt.Process();
            prompt.ValidateExtraNetworks(modelIndexService);
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(e, promptText, modelIndexService);
            await dialog.ShowAsync();
            return false;
        }

        try
        {
            var negPrompt = GetOrCachePrompt(negPromptText);
            negPrompt.Process();
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(e, negPromptText, modelIndexService);
            await dialog.ShowAsync();
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task ShowHelpDialog()
    {
        var md = $"""
                  ## {Resources.Label_Emphasis}
                  ```prompt
                  (keyword)
                  (keyword:1.0)
                  ```
                  
                  ## {Resources.Label_Deemphasis}
                  ```prompt
                  [keyword]
                  ```
                  
                  ## {Resources.Label_EmbeddingsOrTextualInversion}
                  They may be used in either the positive or negative prompts. 
                  Essentially they are text presets, so the position where you place them 
                  could make a difference. 
                  ```prompt
                  <embedding:model>
                  <embedding:model:1.0>
                  ```
                  
                  ## {Resources.Label_NetworksLoraOrLycoris}
                  Unlike embeddings, network tags do not get tokenized to the model, 
                  so the position in the prompt where you place them does not matter.
                  ```prompt
                  <lora:model>
                  <lora:model:1.0>
                  <lyco:model>
                  <lyco:model:1.0>
                  ```
                  
                  ## {Resources.Label_Comments}
                  Inline comments can be marked by a hashtag ‘#’. 
                  All text after a ‘#’ on a line will be disregarded during generation.
                  ```prompt
                  # comments
                  a red cat # also comments
                  detailed
                  ```
                  """;

        var dialog = DialogHelper.CreateMarkdownDialog(
            md,
            "Prompt Syntax",
            TextEditorPreset.Prompt
        );
        dialog.MinDialogWidth = 800;
        dialog.MaxDialogHeight = 1000;
        dialog.MaxDialogWidth = 1000;
        await dialog.ShowAsync();
    }

    [RelayCommand]
    private async Task DebugShowTokens()
    {
        var prompt = GetPrompt();

        try
        {
            prompt.Process();
        }
        catch (PromptError e)
        {
            await DialogHelper
                .CreatePromptErrorDialog(e, prompt.RawText, modelIndexService)
                .ShowAsync();
            return;
        }

        var tokens = prompt.TokenizeResult.Tokens;

        var builder = new StringBuilder();

        builder.AppendLine($"## Tokens ({tokens.Length}):");
        builder.AppendLine("```csharp");
        builder.AppendLine(prompt.GetDebugText());
        builder.AppendLine("```");

        try
        {
            if (prompt.ExtraNetworks is { } networks)
            {
                builder.AppendLine($"## Networks ({networks.Count}):");
                builder.AppendLine("```csharp");
                builder.AppendLine(
                    JsonSerializer.Serialize(
                        networks,
                        new JsonSerializerOptions() { WriteIndented = true, }
                    )
                );
                builder.AppendLine("```");
            }

            builder.AppendLine("## Formatted for server:");
            builder.AppendLine("```csharp");
            builder.AppendLine(prompt.ProcessedText);
            builder.AppendLine("```");
        }
        catch (PromptError e)
        {
            builder.AppendLine($"##{e.GetType().Name} - {e.Message}");
            builder.AppendLine("```csharp");
            builder.AppendLine(e.StackTrace);
            builder.AppendLine("```");
            throw;
        }

        var dialog = DialogHelper.CreateMarkdownDialog(builder.ToString(), "Prompt Tokens");
        dialog.MinDialogWidth = 800;
        dialog.MaxDialogHeight = 1000;
        dialog.MaxDialogWidth = 1000;
        await dialog.ShowAsync();
    }

    [RelayCommand]
    private void EditorCopy(TextEditor? textEditor)
    {
        textEditor?.Copy();
    }

    [RelayCommand]
    private void EditorPaste(TextEditor? textEditor)
    {
        textEditor?.Paste();
    }

    [RelayCommand]
    private void EditorCut(TextEditor? textEditor)
    {
        textEditor?.Cut();
    }

    /// <inheritdoc />
    public override JsonObject SaveStateToJsonObject()
    {
        return SerializeModel(
            new PromptCardModel
            {
                Prompt = PromptDocument.Text,
                NegativePrompt = NegativePromptDocument.Text
            }
        );
    }

    /// <inheritdoc />
    public override void LoadStateFromJsonObject(JsonObject state)
    {
        var model = DeserializeModel<PromptCardModel>(state);

        PromptDocument.Text = model.Prompt ?? "";
        NegativePromptDocument.Text = model.NegativePrompt ?? "";
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        PromptDocument.Text = parameters.PositivePrompt ?? "";
        NegativePromptDocument.Text = parameters.NegativePrompt ?? "";
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with
        {
            PositivePrompt = PromptDocument.Text,
            NegativePrompt = NegativePromptDocument.Text
        };
    }
}
