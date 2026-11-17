using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Infrastructure;

public static class AotJson
{
    public static void AddNode(this JsonArray array, JsonNode? node)
        => ((IList<JsonNode?>)array).Add(node);
}
