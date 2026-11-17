using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.App.Services;

public static class InProcessSessionState
{
    public static JsonObject ReadSnapshot() => SessionStateWriter.For().ReadSnapshot();
}
