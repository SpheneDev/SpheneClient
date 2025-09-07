using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using System.Numerics;

namespace Sphene.UI;

public class UpdateTestWindow : WindowMediatorSubscriberBase
{
    private readonly ILogger<UpdateTestWindow> _logger;
    private readonly UpdateCheckService _updateCheckService;
    private bool _isChecking = false;

    public UpdateTestWindow(ILogger<UpdateTestWindow> logger, SpheneMediator mediator, UpdateCheckService updateCheckService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Update Test Window", performanceCollectorService)
    {
        _logger = logger;
        _updateCheckService = updateCheckService;
        
        Size = new Vector2(300, 150);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    protected override void DrawInternal()
    {
        ImGui.Text("Update System Test");
        ImGui.Separator();
        
        if (_isChecking)
        {
            ImGui.Text("Checking for updates...");
        }
        else
        {
            if (ImGui.Button("Test Update Check"))
            {
                TestUpdateCheck();
            }
        }
        
        ImGui.Text("Check the logs for update check results.");
    }

    private async void TestUpdateCheck()
    {
        _isChecking = true;
        try
        {
            _logger.LogInformation("Starting manual update check test");
            await _updateCheckService.TestUpdateCheckAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during update check test");
        }
        finally
        {
            _isChecking = false;
        }
    }
}