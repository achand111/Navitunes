namespace Navitunes;

public class ItunesTrack
{
    public int TrackId { get; set; }
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Location { get; set; } = "";
    public int Rating { get; set; }
    public bool Loved { get; set; }
    public int Stars { get; set; }
}

public class ItunesPlaylist
{
    public string Name { get; set; } = "";
    public List<ItunesPlaylistItem> Items { get; set; } = new();
}

public class ItunesPlaylistItem
{
    public int Position { get; set; }
    public int? TrackId { get; set; }
}

public class NavSong
{
    public string Id { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Title { get; set; } = "";
}

public class NavPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class PreparedPlaylist
{
    public string Name { get; set; } = "";
    public List<string> SongIds { get; set; } = new();
    public int OriginalCount { get; set; }
}

public class UnmatchedPlaylistItem
{
    public string Playlist { get; set; } = "";
    public int Position { get; set; }
    public string TrackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Location { get; set; } = "";
}

public class UnmatchedTrack
{
    public string TrackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Location { get; set; } = "";
}

public class FailedRow
{
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string NavidromeId { get; set; } = "";
    public string Error { get; set; } = "";
}

public class FailedPlaylist
{
    public string Playlist { get; set; } = "";
    public string Error { get; set; } = "";
}

public record MissionMatch(ItunesTrack Track, NavSong Song);