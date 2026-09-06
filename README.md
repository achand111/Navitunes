# Navitunes

Moves your iTunes stuff into Navidrome: playlists, loved tracks, and star ratings, through the Subsonic API. One small Tkinter window does it all — no pip packages, just Python 3.9+.

## What you need

- An exported iTunes library. In iTunes/Music: `File` → `Library` → `Export Library...`, save as XML (the default name is `Library.xml`).
- Your Navidrome URL, username, and password (the same URL you open in the browser).

## Run it

```bat
python navitunes_gui.py
```

Fill in the path to `Library.xml`, click **Connect and load library**, then use the three cards — **Playlists**, **Loved tracks**, **Ratings**. Every card previews first so you can see the match counts before changing anything; the **Preview and import all three** button runs all the previews at once.

Anything that can't be matched (song not in Navidrome, title spelled differently) is written to a CSV next to your `Library.xml`.

Matching is by artist + album + title, with an album-artist fallback.

## What it does NOT touch

- your music files
- your iTunes library
- play counts, album ratings, album art
- anything in Navidrome you didn't explicitly import (importing a playlist with the same name replaces that playlist's contents)

## Notes

- tkinter ships with Python on Windows and macOS; on Linux it's usually `sudo apt install python3-tk`.
- It paces its API calls a little so it doesn't hammer a big library during the first connect.
- CSV output uses a UTF-8 BOM so Excel opens it without mangling anything.