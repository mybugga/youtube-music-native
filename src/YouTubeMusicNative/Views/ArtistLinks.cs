using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.Views;

/// <summary>
/// <c>v:ArtistLinks.Track="{Binding}"</c> on a TextBlock: shows the song's artists, each one a link to the
/// artist's page (underlined on hover). Names YouTube didn't link stay plain text.
/// </summary>
public static class ArtistLinks
{
    /// <summary>Set by the shell: opens an artist page (browse id, name).</summary>
    public static Action<string, string?>? OpenArtist { get; set; }

    /// <summary>Set by the shell: opens an album page (browse id, title, cover art).</summary>
    public static Action<string, string?, string?>? OpenAlbum { get; set; }

    /// <summary><c>v:ArtistLinks.Album="{Binding}"</c>: the song's album name, linking to the album when YouTube gave its id.</summary>
    public static readonly DependencyProperty AlbumProperty = DependencyProperty.RegisterAttached(
        "Album", typeof(Track), typeof(ArtistLinks), new PropertyMetadata(null, OnAlbumChanged));

    public static Track? GetAlbum(DependencyObject d) => (Track?)d.GetValue(AlbumProperty);
    public static void SetAlbum(DependencyObject d, Track? value) => d.SetValue(AlbumProperty, value);

    private static void OnAlbumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not Track { Album: { } album } track) return;
        if (track.AlbumId is { } id) block.Inlines.Add(Link(block, album, $"Go to {album}", () => OpenAlbum?.Invoke(id, album, track.ThumbnailUrl)));
        else block.Inlines.Add(new Run(album));
    }

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(Track), typeof(ArtistLinks), new PropertyMetadata(null, OnTrackChanged));

    public static Track? GetTrack(DependencyObject d) => (Track?)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, Track? value) => d.SetValue(TrackProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not Track track) return;

        if (track.ArtistLinks is not { Count: > 0 } artists || artists.All(a => a.BrowseId is null))
        {
            block.Inlines.Add(new Run(track.Artists));
            return;
        }
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) block.Inlines.Add(new Run(", "));
            var artist = artists[i];
            if (artist.BrowseId is not { } id)
            {
                block.Inlines.Add(new Run(artist.Name));
                continue;
            }
            block.Inlines.Add(Link(block, artist.Name, $"Go to {artist.Name}", () => OpenArtist?.Invoke(id, artist.Name)));
        }
    }

    /// <summary>A link in the surrounding text's colour (not hyperlink blue); white and underlined on hover.</summary>
    private static Hyperlink Link(TextBlock block, string text, string tip, Action open)
    {
        var link = new Hyperlink(new Run(text)) { TextDecorations = null, Cursor = Cursors.Hand, ToolTip = tip };
        link.SetBinding(TextElement.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = block });
        link.MouseEnter += (_, _) =>
        {
            link.TextDecorations = TextDecorations.Underline;
            link.Foreground = System.Windows.Media.Brushes.White;
        };
        link.MouseLeave += (_, _) =>
        {
            link.TextDecorations = null;
            link.SetBinding(TextElement.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = block });
        };
        link.Click += (_, args) =>
        {
            args.Handled = true;
            open();
        };
        return link;
    }
}
