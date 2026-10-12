using Organessonium.Services.Diagnostics;

namespace Organessonium;

internal static class DesktopEntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var diagnostics = DevelopmentDiagnostics.Start(args);
        try
        {
            DevelopmentDiagnostics.Signal("startup.phase", "constructWpf");
            var application = new App();
            DevelopmentDiagnostics.AttachDispatcher(application.Dispatcher);
            application.InitializeComponent();
            DevelopmentDiagnostics.Signal("startup.phase", "runWpf");
            var code = application.Run();
            diagnostics?.Complete(code);
            return code;
        }
        catch (Exception exception)
        {
            DevelopmentDiagnostics.Error("Bootstrap.Failed", exception);
            throw; // Observe, do not replace the application's recovery/exit policy.
        }
    }
}
