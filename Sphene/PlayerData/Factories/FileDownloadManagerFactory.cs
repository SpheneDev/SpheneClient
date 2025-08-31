using Sphene.FileCache;
using Sphene.Services.Mediator;
using Sphene.WebAPI.Files;
using Microsoft.Extensions.Logging;

namespace Sphene.PlayerData.Factories;

public class FileDownloadManagerFactory
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileCompactor _fileCompactor;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SpheneMediator _mareMediator;

    public FileDownloadManagerFactory(ILoggerFactory loggerFactory, SpheneMediator mareMediator, FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor)
    {
        _loggerFactory = loggerFactory;
        _mareMediator = mareMediator;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
        _fileCompactor = fileCompactor;
    }

    public FileDownloadManager Create()
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _mareMediator, _fileTransferOrchestrator, _fileCacheManager, _fileCompactor);
    }
}
