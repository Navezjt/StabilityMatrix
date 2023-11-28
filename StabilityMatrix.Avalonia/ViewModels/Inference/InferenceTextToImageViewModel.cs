﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DynamicData.Binding;
using NLog;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Services;
using InferenceTextToImageView = StabilityMatrix.Avalonia.Views.Inference.InferenceTextToImageView;

#pragma warning disable CS0657 // Not a valid attribute location for this declaration

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceTextToImageView), persistent: true)]
[ManagedService]
[Transient]
public class InferenceTextToImageViewModel
    : InferenceGenerationViewModelBase,
        IParametersLoadableState
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notificationService;
    private readonly IModelIndexService modelIndexService;

    [JsonIgnore]
    public StackCardViewModel StackCardViewModel { get; }

    [JsonPropertyName("Modules")]
    public StackEditableCardViewModel ModulesCardViewModel { get; }

    [JsonPropertyName("Model")]
    public ModelCardViewModel ModelCardViewModel { get; }

    [JsonPropertyName("Sampler")]
    public SamplerCardViewModel SamplerCardViewModel { get; }

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("Upscaler")]
    public UpscalerCardViewModel UpscalerCardViewModel { get; }

    [JsonPropertyName("HiresSampler")]
    public SamplerCardViewModel HiresSamplerCardViewModel { get; }

    [JsonPropertyName("HiresUpscaler")]
    public UpscalerCardViewModel HiresUpscalerCardViewModel { get; }

    [JsonPropertyName("FreeU")]
    public FreeUCardViewModel FreeUCardViewModel { get; }

    [JsonPropertyName("BatchSize")]
    public BatchSizeCardViewModel BatchSizeCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    public bool IsFreeUEnabled => false;
    public bool IsHiresFixEnabled => false;
    public bool IsUpscaleEnabled => false;

    /*public bool IsFreeUEnabled
    {
        get => StackCardViewModel.GetCard<StackExpanderViewModel>().IsEnabled;
        set => StackCardViewModel.GetCard<StackExpanderViewModel>().IsEnabled = value;
    }

    public bool IsHiresFixEnabled
    {
        get => StackCardViewModel.GetCard<StackExpanderViewModel>(1).IsEnabled;
        set => StackCardViewModel.GetCard<StackExpanderViewModel>(1).IsEnabled = value;
    }

    public bool IsUpscaleEnabled
    {
        get => StackCardViewModel.GetCard<StackExpanderViewModel>(2).IsEnabled;
        set => StackCardViewModel.GetCard<StackExpanderViewModel>(2).IsEnabled = value;
    }*/

    public InferenceTextToImageViewModel(
        INotificationService notificationService,
        IInferenceClientManager inferenceClientManager,
        ISettingsManager settingsManager,
        ServiceManager<ViewModelBase> vmFactory,
        IModelIndexService modelIndexService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager)
    {
        this.notificationService = notificationService;
        this.modelIndexService = modelIndexService;

        // Get sub view models from service manager

        SeedCardViewModel = vmFactory.Get<SeedCardViewModel>();
        SeedCardViewModel.GenerateNewSeed();

        ModelCardViewModel = vmFactory.Get<ModelCardViewModel>();

        SamplerCardViewModel = vmFactory.Get<SamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDimensionsEnabled = true;
            samplerCard.IsCfgScaleEnabled = true;
            samplerCard.IsSamplerSelectionEnabled = true;
            samplerCard.IsSchedulerSelectionEnabled = true;
        });

        PromptCardViewModel = vmFactory.Get<PromptCardViewModel>();
        HiresSamplerCardViewModel = vmFactory.Get<SamplerCardViewModel>(samplerCard =>
        {
            samplerCard.IsDenoiseStrengthEnabled = true;
        });
        HiresUpscalerCardViewModel = vmFactory.Get<UpscalerCardViewModel>();
        UpscalerCardViewModel = vmFactory.Get<UpscalerCardViewModel>();
        FreeUCardViewModel = vmFactory.Get<FreeUCardViewModel>();
        BatchSizeCardViewModel = vmFactory.Get<BatchSizeCardViewModel>();

        ModulesCardViewModel = vmFactory.Get<StackEditableCardViewModel>(modulesCard =>
        {
            modulesCard.AvailableModules = new[]
            {
                typeof(HiresFixModule),
                typeof(UpscalerModule),
                typeof(SaveImageModule)
            };
            modulesCard.DefaultModules = new[] { typeof(HiresFixModule), typeof(UpscalerModule) };
            modulesCard.InitializeDefaults();
        });

        StackCardViewModel = vmFactory.Get<StackCardViewModel>();
        StackCardViewModel.AddCards(
            ModelCardViewModel,
            SamplerCardViewModel,
            ModulesCardViewModel,
            SeedCardViewModel,
            BatchSizeCardViewModel
        );

        // When refiner is provided in model card, enable for sampler
        ModelCardViewModel
            .WhenPropertyChanged(x => x.IsRefinerSelectionEnabled)
            .Subscribe(e =>
            {
                SamplerCardViewModel.IsRefinerStepsEnabled =
                    e.Sender is { IsRefinerSelectionEnabled: true, SelectedRefiner: not null };
            });
    }

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);

        var builder = args.Builder;

        builder.Connections.Seed = args.SeedOverride switch
        {
            { } seed => Convert.ToUInt64(seed),
            _ => Convert.ToUInt64(SeedCardViewModel.Seed)
        };

        // Load models
        ModelCardViewModel.ApplyStep(args);

        // Setup empty latent
        builder.SetupEmptyLatentSource(
            SamplerCardViewModel.Width,
            SamplerCardViewModel.Height,
            BatchSizeCardViewModel.BatchSize,
            BatchSizeCardViewModel.IsBatchIndexEnabled ? BatchSizeCardViewModel.BatchIndex : null
        );

        // Prompts and loras
        PromptCardViewModel.ApplyStep(args);

        // Setup Sampler and Refiner if enabled
        SamplerCardViewModel.ApplyStep(args);

        // Hires fix if enabled
        foreach (var module in ModulesCardViewModel.Cards.OfType<ModuleBase>())
        {
            module.ApplyStep(args);
        }

        builder.SetupOutputImage();
    }

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        // TODO support hires in some generic way
        return SamplerCardViewModel.ModulesCardViewModel.Cards
            .OfType<ControlNetModule>()
            .SelectMany(m => m.GetInputImages());
    }

    /// <inheritdoc />
    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        // Validate the prompts
        if (!await PromptCardViewModel.ValidatePrompts())
        {
            return;
        }

        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
        {
            return;
        }

        // If enabled, randomize the seed
        var seedCard = StackCardViewModel.GetCard<SeedCardViewModel>();
        if (overrides is not { UseCurrentSeed: true } && seedCard.IsRandomizeEnabled)
        {
            seedCard.GenerateNewSeed();
        }

        var batches = BatchSizeCardViewModel.BatchCount;

        var batchArgs = new List<ImageGenerationEventArgs>();

        for (var i = 0; i < batches; i++)
        {
            var seed = seedCard.Seed + i;

            var buildPromptArgs = new BuildPromptEventArgs
            {
                Overrides = overrides,
                SeedOverride = seed
            };
            BuildPrompt(buildPromptArgs);

            var generationArgs = new ImageGenerationEventArgs
            {
                Client = ClientManager.Client,
                Nodes = buildPromptArgs.Builder.ToNodeDictionary(),
                OutputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray(),
                Parameters = SaveStateToParameters(new GenerationParameters()),
                Project = InferenceProjectDocument.FromLoadable(this),
                // Only clear output images on the first batch
                ClearOutputImages = i == 0
            };

            batchArgs.Add(generationArgs);
        }

        // Run batches
        foreach (var args in batchArgs)
        {
            await RunGeneration(args, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        PromptCardViewModel.LoadStateFromParameters(parameters);
        SamplerCardViewModel.LoadStateFromParameters(parameters);
        ModelCardViewModel.LoadStateFromParameters(parameters);

        SeedCardViewModel.Seed = Convert.ToInt64(parameters.Seed);
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters = SamplerCardViewModel.SaveStateToParameters(parameters);
        parameters = ModelCardViewModel.SaveStateToParameters(parameters);

        parameters.Seed = (ulong)SeedCardViewModel.Seed;

        return parameters;
    }

    // Migration for v2 deserialization
    public override void LoadStateFromJsonObject(JsonObject state, int version)
    {
        if (version > 2)
        {
            LoadStateFromJsonObject(state);
        }

        ModulesCardViewModel.Clear();

        // Add by default the original cards - FreeU, HiresFix, Upscaler
        var hiresFix = ModulesCardViewModel.AddModule<HiresFixModule>();
        var upscaler = ModulesCardViewModel.AddModule<UpscalerModule>();

        hiresFix.IsEnabled = state.GetPropertyValueOrDefault<bool>("IsHiresFixEnabled");
        upscaler.IsEnabled = state.GetPropertyValueOrDefault<bool>("IsUpscaleEnabled");
    }
}
