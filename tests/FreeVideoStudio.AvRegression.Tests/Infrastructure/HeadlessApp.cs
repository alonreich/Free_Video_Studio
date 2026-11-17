using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(FreeVideoStudio.AvRegression.Tests.HeadlessAppBuilder))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace FreeVideoStudio.AvRegression.Tests;

public class HeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
