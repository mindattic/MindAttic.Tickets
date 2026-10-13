// RAZORCONSOLE: Razor components rendered through Spectre.Console. You write markup plus
// @code, call StateHasChanged, and a virtual DOM diff decides when to repaint the live region.
// It keeps the Spectre look (same Markup/Panel/Table vocabulary) and adds components, focus
// (Tab between focusables) and events. Select swallows Esc (it means "cancel highlight"), so
// Back and Quit are rows in the list, the way the Launcher already does "< Back". Ctrl+C quits.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(l => l.ClearProviders()) // host logging would scribble over the live region
    .UseRazorConsole<SpikeRazor.App>()
    .Build();

await host.RunAsync();
