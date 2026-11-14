using Sphene.API.Dto.Announcement;
using Microsoft.AspNetCore.SignalR.Client;

namespace Sphene.WebAPI;

public partial class ApiController
{
    /// <summary>One page of published announcements for the news surface.</summary>
    public async Task<List<AnnouncementDto>> GetAnnouncements(int skip = 0, int take = 20)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<AnnouncementDto>>(nameof(GetAnnouncements), skip, take).ConfigureAwait(false);
    }

    /// <summary>Staff-only list · every live announcement, drafts included.</summary>
    public async Task<List<AnnouncementDto>> AdminGetAnnouncements(int skip = 0, int take = 50)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<AnnouncementDto>>(nameof(AdminGetAnnouncements), skip, take).ConfigureAwait(false);
    }

    /// <summary>Staff-only create · null when the server rejected the write.</summary>
    public async Task<long?> AdminCreateAnnouncement(AnnouncementUpsertDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<long?>(nameof(AdminCreateAnnouncement), dto).ConfigureAwait(false);
    }

    /// <summary>Staff-only update · a first publish broadcasts to all clients.</summary>
    public async Task<bool> AdminUpdateAnnouncement(AnnouncementUpsertDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(AdminUpdateAnnouncement), dto).ConfigureAwait(false);
    }

    /// <summary>Staff-only soft delete.</summary>
    public async Task<bool> AdminDeleteAnnouncement(long id)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(AdminDeleteAnnouncement), id).ConfigureAwait(false);
    }
}
