using System;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
namespace FreeVideoStudio.App.Controls;
public sealed record MemeLanding(bool Ok, double SourceSeconds, double OutputSeconds, string Message);
public sealed record MemePickerContext(
    Func<MemeChoiceViewModel, MemeLanding> Locate,
    Func<MemePlacement, CancellationToken, Task<ExportPayload>>? ComposePreview = null,
    Func<MemePlacement, string, CancellationToken, Action<int>, Task<ExportResult>>? RenderPreview = null);
