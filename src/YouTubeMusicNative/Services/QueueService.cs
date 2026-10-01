using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.Services;

public enum RepeatMode { Off, All, One }

/// <summary>The play queue. UI-thread only (backed by an ObservableCollection).</summary>
public sealed partial class QueueService : ObservableObject
{
    private static readonly Random Rng = new();

    public ObservableCollection<Track> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current))]
    private int _currentIndex = -1;

    [ObservableProperty]
    private RepeatMode _repeat = RepeatMode.Off;

    [ObservableProperty]
    private bool _shuffle;

    public Track? Current => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    public int RemainingAfterCurrent => Items.Count - CurrentIndex - 1;

    /// <summary>Replaces the queue with <paramref name="tracks"/> and selects <paramref name="startIndex"/>.</summary>
    public Track? ReplaceAndPlay(IEnumerable<Track> tracks, int startIndex)
    {
        Items.Clear();
        foreach (var t in tracks) Items.Add(t);
        CurrentIndex = Math.Clamp(startIndex, 0, Items.Count - 1);
        if (Shuffle) ShuffleUpcoming();
        return Current;
    }

    /// <summary>Puts back a saved queue as-is (no reshuffle).</summary>
    public void Restore(IEnumerable<Track> tracks, int index, bool shuffle, RepeatMode repeat)
    {
        Items.Clear();
        foreach (var t in tracks) Items.Add(t);
#pragma warning disable MVVMTK0034 // set the field: the saved queue is already in its shuffled order
        _shuffle = shuffle;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Shuffle));
        Repeat = repeat;
        CurrentIndex = Items.Count == 0 ? -1 : Math.Clamp(index, 0, Items.Count - 1);
    }

    public Track? JumpTo(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        CurrentIndex = index;
        return Current;
    }

    public void AddNext(Track track) => Items.Insert(CurrentIndex + 1, track);

    public void AddToEnd(Track track) => Items.Add(track);

    public void AddRange(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks) Items.Add(t);
    }

    public void RemoveAt(int index)
    {
        // The playing track can't be removed; skip it instead.
        if (index < 0 || index >= Items.Count || index == CurrentIndex) return;
        Items.RemoveAt(index);
        if (index < CurrentIndex) CurrentIndex--;
    }

    public void ClearUpcoming()
    {
        while (Items.Count > CurrentIndex + 1)
            Items.RemoveAt(Items.Count - 1);
    }

    /// <summary>Advances the queue. <paramref name="auto"/> = the previous track finished on its own (honours Repeat One).</summary>
    public Track? MoveNext(bool auto)
    {
        if (Items.Count == 0) return null;
        if (auto && Repeat == RepeatMode.One) return Current;

        if (CurrentIndex + 1 < Items.Count)
            CurrentIndex++;
        else if (Repeat == RepeatMode.All)
            CurrentIndex = 0;
        else
            return null;
        return Current;
    }

    public Track? MovePrevious()
    {
        if (Items.Count == 0) return null;
        if (CurrentIndex > 0) CurrentIndex--;
        return Current;
    }

    public void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    partial void OnShuffleChanged(bool value)
    {
        if (value) ShuffleUpcoming();
    }

    /// <summary>Fisher-Yates over the tracks after the current one; history and the playing track stay put.</summary>
    private void ShuffleUpcoming()
    {
        int start = CurrentIndex + 1;
        var upcoming = Items.Skip(start).ToList();
        for (int i = upcoming.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (upcoming[i], upcoming[j]) = (upcoming[j], upcoming[i]);
        }
        for (int i = 0; i < upcoming.Count; i++)
            Items[start + i] = upcoming[i];
    }
}
