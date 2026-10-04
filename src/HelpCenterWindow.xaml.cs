using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Navigation;
using Markdig;

namespace SiscoNet;

public partial class HelpCenterWindow : Window
{
    public HelpCenterWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build();
        LoadArticle(GettingStartedBrowser, "getting-started.md", pipeline);
        LoadArticle(CanvasToolsBrowser, "canvas-and-tools.md", pipeline);
        LoadArticle(DevicesBrowser, "devices-and-interfaces.md", pipeline);
        LoadArticle(RoutingBrowser, "routing-and-cli.md", pipeline);
        LoadArticle(SimulationBrowser, "simulation-and-packets.md", pipeline);
        LoadArticle(ProjectsBrowser, "projects-and-recovery.md", pipeline);
        LoadArticle(DiscordBrowser, "discord-presence.md", pipeline);
        LoadArticle(ShortcutsBrowser, "shortcuts.md", pipeline);
        LoadArticle(ProtocolScopeBrowser, "protocol-scope.md", pipeline);
        LoadArticle(ServicesBrowser, "services-wireless-iot.md", pipeline);
        LoadArticle(MultiplayerBrowser, "multiplayer.md", pipeline);
    }

    private void LoadArticle(System.Windows.Controls.WebBrowser browser, string fileName, MarkdownPipeline pipeline)
    {
        browser.Navigating += Browser_Navigating;
        var path = Path.Combine(AppContext.BaseDirectory, "Help", fileName);
        var markdown = File.Exists(path) ? File.ReadAllText(path) : $"# Help page unavailable\n\nThe help article `{fileName}` could not be found. Check that the Help folder is present beside the application.";
        var html = Markdown.ToHtml(markdown, pipeline);
        browser.NavigateToString($$"""
            <!doctype html>
            <html><head><meta http-equiv="X-UA-Compatible" content="IE=edge" />
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';" />
            <meta charset="utf-8" />
            <style>
            body { font-family: Segoe UI, sans-serif; color: #20292f; background: #ffffff; margin: 28px 34px; max-width: 820px; line-height: 1.55; }
            h1 { font-size: 26px; font-weight: 600; border-bottom: 1px solid #d6dde1; padding-bottom: 9px; margin-top: 0; }
            h2 { font-size: 19px; font-weight: 600; margin-top: 28px; }
            h3 { font-size: 16px; font-weight: 600; margin-top: 22px; }
            p, li { font-size: 14px; }
            a { color: #087e8b; }
            code { font-family: Consolas, monospace; background: #eff3f4; padding: 2px 4px; }
            pre { font-family: Consolas, monospace; font-size: 13px; background: #172126; color: #d4e0e4; padding: 14px; overflow-x: auto; }
            pre code { background: transparent; color: inherit; padding: 0; }
            blockquote { border-left: 3px solid #087e8b; margin-left: 0; padding: 2px 14px; color: #52636b; }
            table { border-collapse: collapse; } th, td { border: 1px solid #d6dde1; padding: 7px 10px; text-align: left; }
            </style></head><body>{{html}}</body></html>
            """);
    }

    private static void Browser_Navigating(object sender, NavigatingCancelEventArgs e)
    {
        if (e.Uri is null) return;
        if (e.Uri.Scheme == "about") return;
        e.Cancel = true;
        if (e.Uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
    }
}