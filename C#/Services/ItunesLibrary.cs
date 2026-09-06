using System.Text;
using System.Xml.Linq;

namespace Navitunes;

public static class Plist
{
    /// Walks a <dict> element and returns each key->value pair.
    public static List<(string Key, XElement Value)> ReadDict(XElement? dict)
    {
        var result = new List<(string, XElement)>();
        if (dict == null) return result;
        var children = dict.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            var key = children[i];
            var val = children[i + 1];
            if (key.Name.LocalName == "key")
                result.Add((key.Value, val));
        }
        return result;
    }

    public static XElement? DictValue(XElement? dict, string key)
    {
        foreach (var (k, v) in ReadDict(dict))
        {
            if (k == key) return v;
        }
        return null;
    }

    public static string Str(XElement? node) => node?.Value ?? "";
    public static string Str(XElement? dict, string key) => Str(DictValue(dict, key));

    public static bool IsTrue(XElement? node) => node != null && node.Name.LocalName == "true";

    public static bool? Bool(XElement? node)
    {
        if (node == null || node.Name.LocalName != "true" && node.Name.LocalName != "false") return null;
        return node.Name.LocalName == "true";
    }

    public static bool Bool(XElement? dict, string key, bool def = false)
    {
        var v = DictValue(dict, key);
        return v == null ? def : Bool(v) ?? def;
    }

    public static int? Int(XElement? node)
    {
        if (node == null) return null;
        return int.TryParse(node.Value.Trim(), out var n) ? n : null;
    }

    public static int? Int(XElement? dict, string key)
    {
        var v = DictValue(dict, key);
        return Int(v);
    }

    public static List<XElement> ArrayItems(XElement? array)
    {
        if (array == null || array.Name.LocalName != "array") return new List<XElement>();
        return array.Elements().ToList();
    }

    public static List<XElement> ArrayDicts(XElement? array)
    {
        return ArrayItems(array).Where(e => e.Name.LocalName == "dict").ToList();
    }
}

public class ItunesLibrary
{
    public Dictionary<int, ItunesTrack> Tracks { get; } = new();
    public List<ItunesPlaylist> Playlists { get; } = new();
    public List<ItunesPlaylist> RawPlaylists { get; } = new();

    public static ItunesLibrary Load(string path)
    {
        var doc = XDocument.Load(path);
        var dict = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "dict");
        var library = new ItunesLibrary();

        foreach (var (key, value) in Plist.ReadDict(dict))
        {
            switch (key)
            {
                case "Tracks":
                    foreach (var (idKey, trackNode) in Plist.ReadDict(
                             value.Name.LocalName == "dict" ? value : null))
                    {
                        if (!int.TryParse(idKey, out var trackId)) continue;
                        var name = Plist.Str(trackNode, "Name");
                        var artist = Plist.Str(trackNode, "Artist");
                        var albumArtist = Plist.Str(trackNode, "Album Artist");
                        var album = Plist.Str(trackNode, "Album");
                        var location = Plist.Str(trackNode, "Location");
                        var rating = Plist.Int(trackNode, "Rating") ?? 0;
                        var loved = Plist.Bool(trackNode, "Loved");

                        library.Tracks[trackId] = new ItunesTrack
                        {
                            TrackId = trackId,
                            Name = name,
                            Artist = artist,
                            AlbumArtist = albumArtist,
                            Album = album,
                            Location = location,
                            Rating = rating,
                            Loved = loved,
                            Stars = MapStars(rating),
                        };
                    }
                    break;

                case "Playlists":
                    foreach (var pl in Plist.ArrayItems(value))
                    {
                        if (pl.Name.LocalName != "dict") continue;
                        var name = Plist.Str(pl, "Name");
                        var isFolder = Plist.Bool(pl, "Folder");
                        var isMaster = Plist.Bool(pl, "Master");
                        var distinguished = Plist.Int(pl, "Distinguished Kind") ?? 0;
                        var smart = Plist.DictValue(pl, "Smart Info") != null;

                        var isSystem =
                            isFolder || isMaster ||
                            Plist.Bool(pl, "Music") || Plist.Bool(pl, "Movies") ||
                            Plist.Bool(pl, "TV Shows") || Plist.Bool(pl, "Podcasts") ||
                            Plist.Bool(pl, "Audiobooks") || Plist.Bool(pl, "Books") ||
                            smart ||
                            distinguished is 2 or 3 or 4 or 5 or 6 or 65 or 66 or 67;

                        var raw = new ItunesPlaylist { Name = name };
                        var itemsNode = Plist.DictValue(pl, "Playlist Items");
                        var position = 0;
                        foreach (var item in Plist.ArrayDicts(itemsNode))
                        {
                            position++;
                            var trackId = Plist.Int(item, "Track ID");
                            raw.Items.Add(new ItunesPlaylistItem { Position = position, TrackId = trackId });
                        }
                        library.RawPlaylists.Add(raw);

                        if (isSystem || string.IsNullOrWhiteSpace(name)) continue;
                        library.Playlists.Add(raw);
                    }
                    break;
            }
        }

        return library;
    }

    public static int MapStars(int rating)
    {
        return rating switch
        {
            20 => 1,
            40 => 2,
            60 => 3,
            80 => 4,
            100 => 5,
            _ => 0,
        };
    }
}