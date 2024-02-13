﻿using Bugsnag;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.MediaSources;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Interfaces.FFmpeg;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public class ImageFolderScanner : LocalFolderScanner, IImageFolderScanner
{
    private readonly IClient _client;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly ILogger<ImageFolderScanner> _logger;
    private readonly IMediator _mediator;
    private readonly IImageRepository _imageRepository;

    public ImageFolderScanner(
        ILocalFileSystem localFileSystem,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        IMediator mediator,
        IImageRepository imageRepository,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        IClient client,
        ILogger<ImageFolderScanner> logger) : base(
        localFileSystem,
        localStatisticsProvider,
        metadataRepository,
        mediaItemRepository,
        imageCache,
        ffmpegPngService,
        tempFilePool,
        client,
        logger)
    {
        _localFileSystem = localFileSystem;
        _localMetadataProvider = localMetadataProvider;
        _mediator = mediator;
        _imageRepository = imageRepository;
        _libraryRepository = libraryRepository;
        _client = client;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> ScanFolder(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        decimal progressMin,
        decimal progressMax,
        CancellationToken cancellationToken)
    {
        try
        {
            decimal progressSpread = progressMax - progressMin;

            var foldersCompleted = 0;

            var allFolders = new System.Collections.Generic.HashSet<string>();
            var folderQueue = new Queue<string>();

            if (ShouldIncludeFolder(libraryPath.Path) && allFolders.Add(libraryPath.Path))
            {
                folderQueue.Enqueue(libraryPath.Path);
            }

            foreach (string folder in _localFileSystem.ListSubdirectories(libraryPath.Path)
                         .Filter(ShouldIncludeFolder)
                         .Filter(allFolders.Add)
                         .OrderBy(identity))
            {
                folderQueue.Enqueue(folder);
            }

            while (folderQueue.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)foldersCompleted / (foldersCompleted + folderQueue.Count);
                await _mediator.Publish(
                    new ScannerProgressUpdate(
                        libraryPath.LibraryId,
                        null,
                        progressMin + percentCompletion * progressSpread,
                        Array.Empty<int>(),
                        Array.Empty<int>()),
                    cancellationToken);

                string imageFolder = folderQueue.Dequeue();
                foldersCompleted++;

                var filesForEtag = _localFileSystem.ListFiles(imageFolder).ToList();

                var allFiles = filesForEtag
                    .Filter(f => ImageFileExtensions.Contains(Path.GetExtension(f).Replace(".", string.Empty)))
                    .Filter(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (string subdirectory in _localFileSystem.ListSubdirectories(imageFolder)
                             .Filter(ShouldIncludeFolder)
                             .Filter(allFolders.Add)
                             .OrderBy(identity))
                {
                    folderQueue.Enqueue(subdirectory);
                }

                string etag = FolderEtag.Calculate(imageFolder, _localFileSystem);
                Option<LibraryFolder> knownFolder = libraryPath.LibraryFolders
                    .Filter(f => f.Path == imageFolder)
                    .HeadOrNone();

                // skip folder if etag matches
                if (allFiles.Count == 0 ||
                    await knownFolder.Map(f => f.Etag ?? string.Empty).IfNoneAsync(string.Empty) ==
                    etag)
                {
                    continue;
                }

                _logger.LogDebug(
                    "UPDATE: Etag has changed for folder {Folder}",
                    imageFolder);

                var hasErrors = false;

                foreach (string file in allFiles.OrderBy(identity))
                {
                    Either<BaseError, MediaItemScanResult<Image>> maybeVideo = await _imageRepository
                        .GetOrAdd(libraryPath, file)
                        .BindT(video => UpdateStatistics(video, ffmpegPath, ffprobePath))
                        .BindT(UpdateMetadata)
                        //.BindT(video => UpdateThumbnail(video, cancellationToken))
                        //.BindT(UpdateSubtitles)
                        .BindT(FlagNormal);

                    foreach (BaseError error in maybeVideo.LeftToSeq())
                    {
                        _logger.LogWarning("Error processing image at {Path}: {Error}", file, error.Value);
                        hasErrors = true;
                    }

                    foreach (MediaItemScanResult<Image> result in maybeVideo.RightToSeq())
                    {
                        if (result.IsAdded || result.IsUpdated)
                        {
                            await _mediator.Publish(
                                new ScannerProgressUpdate(
                                    libraryPath.LibraryId,
                                    null,
                                    null,
                                    [result.Item.Id],
                                    Array.Empty<int>()),
                                cancellationToken);
                        }
                    }
                }

                // only do this once per folder and only if all files processed successfully
                if (!hasErrors)
                {
                    await _libraryRepository.SetEtag(libraryPath, knownFolder, imageFolder, etag);
                }
            }

            foreach (string path in await _imageRepository.FindImagePaths(libraryPath))
            {
                if (!_localFileSystem.FileExists(path))
                {
                    _logger.LogInformation("Flagging missing image at {Path}", path);
                    List<int> imageIds = await FlagFileNotFound(libraryPath, path);
                    await _mediator.Publish(
                        new ScannerProgressUpdate(
                            libraryPath.LibraryId,
                            null,
                            null,
                            imageIds.ToArray(),
                            Array.Empty<int>()),
                        cancellationToken);
                }
                else if (Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    List<int> imageIds = await _imageRepository.DeleteByPath(libraryPath, path);
                    await _mediator.Publish(
                        new ScannerProgressUpdate(
                            libraryPath.LibraryId,
                            null,
                            null,
                            Array.Empty<int>(),
                            imageIds.ToArray()),
                        cancellationToken);
                }
            }

            await _libraryRepository.CleanEtagsForLibraryPath(libraryPath);

            return Unit.Default;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Image>>> UpdateMetadata(
        MediaItemScanResult<Image> result)
    {
        try
        {
            Image image = result.Item;
            string path = image.GetHeadVersion().MediaFiles.Head().Path;

            bool shouldUpdate = Optional(image.ImageMetadata).Flatten().HeadOrNone().Match(
                m => m.MetadataKind == MetadataKind.Fallback ||
                     m.DateUpdated != _localFileSystem.GetLastWriteTime(path),
                true);

            if (shouldUpdate)
            {
                image.ImageMetadata ??= [];

                _logger.LogDebug("Refreshing {Attribute} for {Path}", "Metadata", path);
                if (await _localMetadataProvider.RefreshTagMetadata(image))
                {
                    result.IsUpdated = true;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }
}