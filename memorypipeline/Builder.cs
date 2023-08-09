﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticMemory.Core.AI.AzureOpenAI;
using Microsoft.SemanticMemory.Core.AI.OpenAI;
using Microsoft.SemanticMemory.Core.AppBuilders;
using Microsoft.SemanticMemory.Core.Configuration;
using Microsoft.SemanticMemory.Core.ContentStorage.AzureBlobs;
using Microsoft.SemanticMemory.Core.ContentStorage.FileSystemStorage;
using Microsoft.SemanticMemory.Core.Handlers;
using Microsoft.SemanticMemory.Core.MemoryStorage.AzureCognitiveSearch;
using Microsoft.SemanticMemory.Core.MemoryStorage;
using Microsoft.SemanticMemory.Core.Pipeline.Queue;
using Microsoft.SemanticMemory.Core.Pipeline.Queue.AzureQueues;
using Microsoft.SemanticMemory.Core.Pipeline.Queue.FileBasedQueues;
using Microsoft.SemanticMemory.Core.Pipeline.Queue.RabbitMq;
using Microsoft.SemanticMemory.Core.Pipeline;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI.TextEmbedding;

namespace SemanticMemory.Service;

/// <summary>
/// Flexible dependency injection using dependencies defined in appsettings.json
/// </summary>
public static class Builder
{
    private const string ConfigRoot = "SemanticMemory";

    public static WebApplicationBuilder CreateBuilder(out SemanticMemoryConfig config)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        config = builder.Configuration.GetSection(ConfigRoot).Get<SemanticMemoryConfig>()
                 ?? throw new ConfigurationException("Configuration is null");

        builder.Services.AddSingleton<SemanticMemoryConfig>(config);
        builder.Services.AddSingleton<IMimeTypeDetection, MimeTypesDetection>();
        builder.Services.AddSingleton<IPipelineOrchestrator, DistributedPipelineOrchestrator>();
        builder.Services.AddSingleton<DistributedPipelineOrchestrator, DistributedPipelineOrchestrator>();

        ConfigureContentStorage(builder, config);
        ConfigurePipelineHandlers(builder, config);
        ConfigureQueueSystem(builder, config);
        ConfigureEmbeddingGenerator(builder, config);
        ConfigureEmbeddingStorage(builder, config);

        return builder;
    }

    // Service where documents and temporary files are stored
    private static void ConfigureContentStorage(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        switch (config.ContentStorageType)
        {
            case string x when x.Equals("AzureBlobs", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddAzureBlobAsContentStorage(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureBlobs")
                    .Get<AzureBlobConfig>()!);
                break;

            case string x when x.Equals("FileSystemContentStorage", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddFileSystemAsContentStorage(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("FileSystemContentStorage")
                    .Get<FileSystemConfig>()!);
                break;

            default:
                throw new NotSupportedException($"Unknown/unsupported {config.ContentStorageType} content storage");
        }
    }

    // Register pipeline handlers as hosted services
    private static void ConfigurePipelineHandlers(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        builder.Services.AddHandlerAsHostedService<TextExtractionHandler>("extract");
        builder.Services.AddHandlerAsHostedService<TextPartitioningHandler>("partition");
        builder.Services.AddHandlerAsHostedService<GenerateEmbeddingsHandler>("gen_embeddings");
        builder.Services.AddHandlerAsHostedService<SaveEmbeddingsHandler>("save_embeddings");
    }

    // Orchestration dependencies, ie. which queueing system to use
    private static void ConfigureQueueSystem(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        switch (config.DataIngestion.DistributedOrchestration.QueueType)
        {
            case string y when y.Equals("AzureQueue", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddAzureQueue(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureQueue")
                    .Get<AzureQueueConfig>()!);
                break;

            case string y when y.Equals("RabbitMQ", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddRabbitMq(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("RabbitMq")
                    .Get<RabbitMqConfig>()!);
                break;

            case string y when y.Equals("FileBasedQueue", StringComparison.OrdinalIgnoreCase):
                builder.Services.AddFileBasedQueue(builder.Configuration
                    .GetSection(ConfigRoot).GetSection("Services").GetSection("FileBasedQueue")
                    .Get<FileBasedQueueConfig>()!);
                break;

            default:
                throw new NotSupportedException($"Unknown/unsupported {config.DataIngestion.DistributedOrchestration.QueueType} queue type");
        }
    }

    // List of embedding generators to use (multiple generators allowed during ingestion)
    private static void ConfigureEmbeddingGenerator(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        var embeddingGenerationServices = new TypeCollection<ITextEmbeddingGeneration>();
        builder.Services.AddSingleton(embeddingGenerationServices);
        foreach (var type in config.DataIngestion.EmbeddingGeneratorTypes)
        {
            switch (type)
            {
                case string x when x.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase):
                case string y when y.Equals("AzureOpenAIEmbedding", StringComparison.OrdinalIgnoreCase):
                    embeddingGenerationServices.Add<AzureTextEmbeddingGeneration>();
                    builder.Services.AddAzureOpenAIEmbeddingGeneration(builder.Configuration
                        .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureOpenAIEmbedding")
                        .Get<AzureOpenAIConfig>()!);
                    break;

                case string x when x.Equals("OpenAI", StringComparison.OrdinalIgnoreCase):
                    embeddingGenerationServices.Add<OpenAITextEmbeddingGeneration>();
                    builder.Services.AddOpenAITextEmbeddingGeneration(builder.Configuration
                        .GetSection(ConfigRoot).GetSection("Services").GetSection("OpenAI")
                        .Get<OpenAIConfig>()!);
                    break;

                default:
                    throw new NotSupportedException($"Unknown/unsupported {type} text generator");
            }
        }
    }

    // List of Vector DB list where to store embeddings (multiple DBs allowed during ingestion)
    private static void ConfigureEmbeddingStorage(WebApplicationBuilder builder, SemanticMemoryConfig config)
    {
        var vectorDbServices = new TypeCollection<ISemanticMemoryVectorDb>();
        builder.Services.AddSingleton(vectorDbServices);
        foreach (var type in config.DataIngestion.VectorDbTypes)
        {
            switch (type)
            {
                case string x when x.Equals("AzureCognitiveSearch", StringComparison.OrdinalIgnoreCase):
                    vectorDbServices.Add<AzureCognitiveSearchMemory>();
                    builder.Services.AddAzureCognitiveSearchAsVectorDb(builder.Configuration
                        .GetSection(ConfigRoot).GetSection("Services").GetSection("AzureCognitiveSearch")
                        .Get<AzureCognitiveSearchConfig>()!);
                    break;

                default:
                    throw new NotSupportedException($"Unknown/unsupported {type} vector DB");
            }
        }
    }
}
