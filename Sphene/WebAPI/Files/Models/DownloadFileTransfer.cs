using Sphene.API.Dto.Files;

namespace Sphene.WebAPI.Files.Models;

public class DownloadFileTransfer : FileTransfer
{
    public DownloadFileTransfer(DownloadFileDto dto) : base(dto)
    {
    }

    public override bool CanBeTransferred => Dto.FileExists && !Dto.IsForbidden && Dto.Size > 0;
    public Uri DownloadUri => new(Dto.Url);
    public Uri? FallbackDownloadUri => string.IsNullOrWhiteSpace(Dto.FallbackUrl) ? null : new Uri(Dto.FallbackUrl);
    public Uri? DirectDownloadUri => string.IsNullOrWhiteSpace(Dto.DirectUrl) ? null : new Uri(Dto.DirectUrl);
    public override long Total
    {
        set
        {
            _ = value;
        }
        get => Dto.Size;
    }

    public long TotalRaw => Dto.RawSize;
    private DownloadFileDto Dto => (DownloadFileDto)TransferDto;
}
