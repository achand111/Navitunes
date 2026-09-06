using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Navitunes;

public class SubsonicException : Exception
{
    public SubsonicException(string message) : base(message) { }
}

public class SubsonicClient
{
    private const string ApiVersion = "1.16.1";
    private const string ClientName = "Navitunes";
    private const int PageSize = 500;
    private const int RequestDelayMs = 30;

    private readonly string _baseUrl;
    private readonly string _user;
    private readonly string _password;
    private readonly HttpClient _http;

    public SubsonicClient(string baseUrl, string user, string password)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _user = user;
        _password = password;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ClientName);
    }

    private Uri BuildUrl(string endpoint, List<KeyValuePair<string, string>> extra)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_password + salt))).ToLowerInvariant();

        var q = new List<KeyValuePair<string, string>>
        {
            new("u", _user), new("t", token), new("s", salt),
            new("v", ApiVersion), new("c", ClientName), new("f", "json"),
        };
        q.AddRange(extra);

        var sb = new StringBuilder(_baseUrl);
        sb.Append("/rest/").Append(endpoint).Append('?');
        foreach (var kv in q)
        {
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=')
              .Append(Uri.EscapeDataString(kv.Value)).Append('&');
        }
        sb.Length--; // drop trailing &
        return new Uri(sb.ToString());
    }

    private async Task<JsonElement> CallAsync(string endpoint, params KeyValuePair<string, string>[] extra)
    {
        var url = BuildUrl(endpoint, extra.ToList());
        using var resp = await _http.GetAsync(url).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.GetProperty("subsonic-response");
        if (root.GetProperty("status").GetString() != "ok")
        {
            var code = root.TryGetProperty("error", out var err) ? err.GetProperty("code").GetInt32() : 0;
            var msg = root.TryGetProperty("error", out var e2) && e2.TryGetProperty("message", out var m)
                ? m.GetString() ?? "unknown error"
                : "unknown error";
            throw new SubsonicException($"Navidrome API error {code}: {msg}");
        }
        return root.Clone();
    }

    public async Task PingAsync()
    {
        await CallAsync("ping").ConfigureAwait(false);
    }

    public async Task<List<NavPlaylist>> GetPlaylistsAsync()
    {
        var root = await CallAsync("getPlaylists").ConfigureAwait(false);
        var list = new List<NavPlaylist>();
        if (root.TryGetProperty("playlists", out var p) &&
            p.TryGetProperty("playlist", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                list.Add(new NavPlaylist
                {
                    Id = e.GetProperty("id").GetString() ?? "",
                    Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                });
            }
        }
        return list;
    }

    public async Task<List<NavSong>> GetSongsAsync(Action<string>? log = null)
    {
        var albums = new List<(string Id, string Name)>();
        var offset = 0;
        while (true)
        {
            var root = await CallAsync("getAlbumList2",
                new("type", "alphabeticalByName"),
                new("size", PageSize.ToString()),
                new("offset", offset.ToString())).ConfigureAwait(false);

            var batch = new List<(string, string)>();
            if (root.TryGetProperty("albumList2", out var al) &&
                al.TryGetProperty("album", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    batch.Add((e.GetProperty("id").GetString() ?? "",
                               e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
                }
            }
            if (batch.Count == 0) break;
            albums.AddRange(batch);
            if (batch.Count < PageSize) break;
            offset += batch.Count;
        }

        log?.Invoke($"Found {albums.Count} albums.");
        var songs = new List<NavSong>();
        log?.Invoke("Reading songs from Navidrome (this can take a while on large libraries)...");

        for (var i = 0; i < albums.Count; i++)
        {
            var album = albums[i];
            var root = await CallAsync("getAlbum",
                new KeyValuePair<string, string>("id", album.Id)).ConfigureAwait(false);
            if (root.TryGetProperty("album", out var a) &&
                a.TryGetProperty("song", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    songs.Add(new NavSong
                    {
                        Id = e.GetProperty("id").GetString() ?? "",
                        Artist = e.TryGetProperty("artist", out var at) ? at.GetString() ?? "" : "",
                        Album = e.TryGetProperty("album", out var ab) ? ab.GetString() ?? "" : "",
                        Title = e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    });
                }
            }
            if ((i + 1) % 50 == 0 || i + 1 == albums.Count)
                log?.Invoke($"  Read {i + 1}/{albums.Count} albums...");
            await Task.Delay(RequestDelayMs).ConfigureAwait(false);
        }

        log?.Invoke($"Found {songs.Count} Navidrome songs.");
        return songs;
    }

    public async Task CreatePlaylistAsync(string name, List<string> songIds)
    {
        var extra = new List<KeyValuePair<string, string>> { new("name", name) };
        extra.AddRange(songIds.Select(id => new KeyValuePair<string, string>("songId", id)));
        await CallAsync("createPlaylist", extra.ToArray()).ConfigureAwait(false);
    }

    public async Task UpdatePlaylistAsync(string playlistId, List<string> songIds)
    {
        var extra = new List<KeyValuePair<string, string>> { new("playlistId", playlistId) };
        extra.AddRange(songIds.Select(id => new KeyValuePair<string, string>("songId", id)));
        await CallAsync("updatePlaylist", extra.ToArray()).ConfigureAwait(false);
    }

    public async Task StarAsync(string songId)
    {
        await CallAsync("star",
            new KeyValuePair<string, string>("id", songId)).ConfigureAwait(false);
    }

    public async Task SetRatingAsync(string songId, int rating)
    {
        await CallAsync("setRating",
            new KeyValuePair<string, string>("id", songId),
            new KeyValuePair<string, string>("rating", rating.ToString())).ConfigureAwait(false);
    }
}