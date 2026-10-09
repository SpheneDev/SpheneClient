namespace Sphene.Services;

public record SpheneProfileData(bool IsFlagged, bool IsNSFW, string Base64ProfilePicture, string Base64SupporterPicture, string Description, string StatusMessage = "", string Tags = "", string Base64BannerImage = "", string Theme = "", int Visibility = 0, string Base64RowImage = "")
{
    public Lazy<byte[]> ImageData { get; } = new Lazy<byte[]>(Convert.FromBase64String(Base64ProfilePicture));
    public Lazy<byte[]> SupporterImageData { get; } = new Lazy<byte[]>(string.IsNullOrEmpty(Base64SupporterPicture) ? [] : Convert.FromBase64String(Base64SupporterPicture));
    public Lazy<byte[]> BannerImageData { get; } = new Lazy<byte[]>(string.IsNullOrEmpty(Base64BannerImage) ? [] : Convert.FromBase64String(Base64BannerImage));
    public Lazy<byte[]> RowImageData { get; } = new Lazy<byte[]>(string.IsNullOrEmpty(Base64RowImage) ? [] : Convert.FromBase64String(Base64RowImage));
    public Lazy<SpheneProfileTheme> ProfileTheme { get; } = new Lazy<SpheneProfileTheme>(() => SpheneProfileTheme.Parse(Theme));
}
