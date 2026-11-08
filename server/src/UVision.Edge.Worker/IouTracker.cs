using System.Globalization;

namespace UVision.Edge.Worker;

/// <summary>
/// A small greedy tracker: each detection takes the best-overlapping unmatched track of the same class (IoU at or above a floor), otherwise
/// starts a new one. A track that goes unseen is kept for a few frames so one missed detection does not renumber the object, but only objects
/// seen in <em>this</em> frame are reported. Zone counts do not depend on identity, so this is deliberately simple.
/// </summary>
public sealed class IouTracker(double minIou = 0.3, int maxMissedFrames = 3)
{
    private sealed class Entry(int id, string cls, Box box, double score)
    {
        public int Id { get; } = id;
        public string Class { get; } = cls;
        public Box Box { get; set; } = box;
        public double Score { get; set; } = score;
        public int Missed { get; set; }
    }

    private readonly List<Entry> _tracks = [];
    private int _nextId = 1;

    public IReadOnlyList<Track> Update(IReadOnlyList<Detection> detections)
    {
        // Best overlaps first, so a strong match is not taken by a weaker detection that merely came earlier in the list.
        var pairs = new List<(double Iou, int Det, Entry Track)>();
        for (var d = 0; d < detections.Count; d++)
            foreach (var t in _tracks)
                if (t.Class == detections[d].Label && detections[d].Box.Iou(t.Box) is var iou && iou >= minIou)
                    pairs.Add((iou, d, t));

        // The result keeps the order of the detections it was given.
        var seen = new Track?[detections.Count];
        var matched = new HashSet<Entry>();
        foreach (var (_, d, t) in pairs.OrderByDescending(p => p.Iou))
        {
            if (seen[d] is not null || matched.Contains(t)) continue;
            matched.Add(t);
            t.Box = detections[d].Box;
            t.Score = detections[d].Score;
            t.Missed = 0;
            seen[d] = ToTrack(t);
        }

        for (var d = 0; d < detections.Count; d++)
        {
            if (seen[d] is not null) continue;
            var entry = new Entry(_nextId++, detections[d].Label, detections[d].Box, detections[d].Score);
            _tracks.Add(entry);
            matched.Add(entry);
            seen[d] = ToTrack(entry);
        }

        foreach (var t in _tracks)
            if (!matched.Contains(t)) t.Missed++;
        _tracks.RemoveAll(t => t.Missed > maxMissedFrames);
        return seen!;
    }

    private static Track ToTrack(Entry e) => new(e.Id.ToString(CultureInfo.InvariantCulture), e.Class, e.Score, e.Box);
}
