using Sphene.API.Dto.Files;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

public sealed record FileTransferNotificationMessage(FileTransferNotificationDto Notification) : MessageBase;

