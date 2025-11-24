namespace Sphene.PlayerData.Pairs;

public enum AcknowledgmentErrorCode
{
    None = 0,
    Timeout = 1,
    NetworkError = 2,
    InvalidData = 3,
    UserNotFound = 4,
    ServerError = 5,
    RateLimited = 6,
    AuthenticationFailed = 7,
    DataCorrupted = 8,
    InsufficientPermissions = 9,
    ServiceUnavailable = 10,
    HashVerificationFailed = 11
}
