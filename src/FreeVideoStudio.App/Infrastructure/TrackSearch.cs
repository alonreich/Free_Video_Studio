using System;

namespace FreeVideoStudio.App.Infrastructure;

internal static class TrackSearch
{
    internal static string NormalizeSearchQuery(string query)
    {
        return (query ?? string.Empty).Trim().Replace("*", string.Empty, StringComparison.Ordinal);
    }

    internal static bool ContainsIgnoreCase(string source, string query)
    {
        return !string.IsNullOrEmpty(source) &&
            source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TrackMatchesSearch(MusicTrackItem track, string rawQuery)
    {
        string query = NormalizeSearchQuery(rawQuery);
        if (query.Length == 0)
            return true;

        return ContainsIgnoreCase(track.Title, query)
            || ContainsIgnoreCase(Path.GetFileNameWithoutExtension(track.Name), query)
            || ContainsIgnoreCase(track.Name, query)
            || ContainsIgnoreCase(track.Artist, query)
            || ContainsIgnoreCase(track.Album, query);
    }
}
