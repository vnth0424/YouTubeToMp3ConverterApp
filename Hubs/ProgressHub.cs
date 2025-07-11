using Microsoft.AspNetCore.SignalR;

public class ProgressHub : Hub
{
    public async Task SendProgress(string groupId, int progress)
    {
        await Clients.Group(groupId).SendAsync("ReceiveProgress", progress);
    }

    public async Task AddToGroup(string groupId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupId);
    }
}