using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Navitunes;

public static class Normalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        s = s.Replace("&", "and");
        s = s.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201a', '\'').Replace('\u0060', '\'');
        s = s.Replace('\u201c', '"').Replace('\u201d', '"');
        s = s.Replace('\u2013', '-').Replace('\u2014', '-');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}

public class SongIndex
{
    private readonly Dictionary<string, List<NavSong>> _index = new();

    public static SongIndex Build(IEnumerable<NavSong> songs)
    {
        var index = new SongIndex();
        foreach (var song in songs)
        {
            var key = Key(song.Artist, song.Album, song.Title);
            if (!index._index.TryGetValue(key, out var list))
            {
                list = new List<NavSong>();
                index._index[key] = list;
            }
            list.Add(song);
        }
        return index;
    }

    public NavSong? Resolve(string artist, string album, string title)
    {
        return _index.TryGetValue(Key(artist, album, title), out var list) ? list[0] : null;
    }

    public NavSong? ResolveNormalized(string artist, string album, string title, string albumArtist)
    {
        if (_index.TryGetValue(Key(artist, album, title), out var list)) return list[0];
        if (!string.IsNullOrWhiteSpace(albumArtist) &&
            _index.TryGetValue(Key(albumArtist, album, title), out var fallback))
            return fallback[0];
        return null;
    }

    public static string Key(string artist, string album, string title)
    {
        return Normalizer.Normalize(artist) + "\u001f" +
               Normalizer.Normalize(album) + "\u001f" +
               Normalizer.Normalize(title);
    }
}