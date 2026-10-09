// Ported from AGO SavedRowTracks.cs; see Docs/SAVED_ROWS.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AgOpenWeb.Services.SavedRows
{
    public sealed class SavedRowPoint
    {
        public double Easting { get; set; }
        public double Northing { get; set; }
        public double Heading { get; set; }
        public SavedRowPoint() { }
        public SavedRowPoint(double e, double n, double h) { Easting = e; Northing = n; Heading = h; }
    }
    public sealed class SavedRowTrack
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Source { get; set; }
        public bool Simulator { get; set; }
        public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
        public List<SavedRowPoint> Points { get; set; } = new List<SavedRowPoint>();
    }
    public sealed class SavedRowTracks
    {
        public int Version { get; set; } = 1;
        public double OriginLatitude { get; set; }
        public double OriginLongitude { get; set; }
        public bool AutoSelect { get; set; } = true;
        public double CaptureDistanceMetres { get; set; } = .5;
        public List<SavedRowTrack> Rows { get; set; } = new List<SavedRowTrack>();
        public void Validate()
        {
            if (Version != 1 || Rows == null || !Finite(OriginLatitude) || !Finite(OriginLongitude) ||
                Math.Abs(OriginLatitude) > 90 || Math.Abs(OriginLongitude) > 180 ||
                !Finite(CaptureDistanceMetres) || CaptureDistanceMetres < .1 || CaptureDistanceMetres > 2)
                throw new InvalidDataException("Invalid saved rows file");
            var ids = new HashSet<string>();
            foreach (var row in Rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id) || row.Points == null || row.Points.Count < 6)
                    throw new InvalidDataException("Invalid saved row");
                for (int i = 0; i < row.Points.Count; i++)
                {
                    var p = row.Points[i];
                    if (p == null || !Finite(p.Easting) || !Finite(p.Northing) || !Finite(p.Heading)) throw new InvalidDataException("Invalid saved row position");
                    if (i > 0)
                    {
                        double distance = Distance(row.Points[i - 1], p);
                        if (distance < .001 || distance > 2) throw new InvalidDataException("Discontinuous saved row path");
                    }
                }
            }
        }
        public static SavedRowTracks Load(string directory, double latitude, double longitude)
        {
            string path = Path.Combine(directory, "individual-rows.json");
            if (!File.Exists(path) && File.Exists(Path.Combine(directory, "SavedRows.json"))) path = Path.Combine(directory, "SavedRows.json");
            var data = File.Exists(path) ? JsonConvert.DeserializeObject<SavedRowTracks>(File.ReadAllText(path)) :
                new SavedRowTracks { OriginLatitude = latitude, OriginLongitude = longitude };
            if (data == null) throw new InvalidDataException("Empty saved rows file");
            data.Validate();
            if (Math.Abs(data.OriginLatitude - latitude) > 1e-8 || Math.Abs(data.OriginLongitude - longitude) > 1e-8)
                throw new InvalidDataException("Saved rows use a different field origin");
            return data;
        }
        public void Save(string directory)
        {
            Validate(); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "individual-rows.json"), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Formatting.Indented));
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        internal static double Distance(SavedRowPoint a, SavedRowPoint b) => Math.Sqrt(Math.Pow(a.Easting - b.Easting, 2) + Math.Pow(a.Northing - b.Northing, 2));
    }
    public sealed class SavedRowMatch
    {
        public int RowIndex { get; set; }
        public double Distance { get; set; }
        public double Heading { get; set; }
        public int Segment { get; set; }
    }
    public sealed class SavedRowSelector
    {
        public int Selected { get; private set; } = -1;
        private int pending = -1;
        private double pendingSince;
        public void Reset(int selected = -1) { Selected = selected; pending = -1; }
        public SavedRowMatch Update(SavedRowTracks data, double e, double n, double heading, double seconds)
        {
            var current = Match(data, Selected, e, n, heading);
            var candidates = Enumerable.Range(0, data.Rows.Count).Select(i => Match(data, i, e, n, heading)).Where(m => m != null && m.Distance <= data.CaptureDistanceMetres).OrderBy(m => m.Distance).ToArray();
            var best = data.AutoSelect ? candidates.FirstOrDefault() : current;
            if (current != null && current.Distance <= data.CaptureDistanceMetres &&
                (best == null || best.RowIndex == Selected || best.Distance + .1 >= current.Distance))
            { pending = -1; return current; }
            if (best == null || best.Distance > data.CaptureDistanceMetres) { pending = -1; return null; }
            if (best.RowIndex == Selected) { pending = -1; return best; }
            if (pending != best.RowIndex) { pending = best.RowIndex; pendingSince = seconds; return null; }
            if (seconds - pendingSince < .35) return null;
            Selected = best.RowIndex; pending = -1; return best;
        }
        public static SavedRowMatch Match(SavedRowTracks data, int index, double e, double n, double heading)
        {
            if (index < 0 || index >= data.Rows.Count || !SavedRowTracks.Finite(e) || !SavedRowTracks.Finite(n) || !SavedRowTracks.Finite(heading)) return null;
            SavedRowMatch best = null; var points = data.Rows[index].Points;
            for (int i = 0; i < points.Count - 1; i++)
            {
                var a = points[i]; var b = points[i + 1]; double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
                double length2 = dx * dx + dy * dy; if (length2 < .000001) continue;
                double t = ((e - a.Easting) * dx + (n - a.Northing) * dy) / length2;
                // Finite segments: never capture an extrapolated line beyond A or B.
                if (t < 0 || t > 1) continue;
                double direction = Math.Atan2(dx, dy), angle = Math.Abs(Math.Atan2(Math.Sin(heading - direction), Math.Cos(heading - direction)));
                angle = Math.Min(angle, Math.PI - angle); if (angle > 35 * Math.PI / 180) continue;
                double distance = Math.Sqrt(Math.Pow(e - a.Easting - t * dx, 2) + Math.Pow(n - a.Northing - t * dy, 2));
                if (best == null || distance < best.Distance) best = new SavedRowMatch { RowIndex = index, Distance = distance, Heading = direction, Segment = i };
            }
            return best;
        }
    }
    public sealed class SavedRowRecorder
    {
        private readonly List<SavedRowPoint> points = new List<SavedRowPoint>();
        private double lastSeconds;
        public bool Recording { get; private set; }
        public string Source { get; private set; }
        public bool Simulator { get; private set; }
        public int Count => points.Count;
        public IReadOnlyList<SavedRowPoint> Points => points;
        public void Start(double e, double n, double heading, double seconds, string source, bool simulator)
        {
            Cancel(); Recording = true; Source = source; Simulator = simulator;
            Add(e, n, heading, seconds, simulator);
        }
        public void Add(double e, double n, double heading, double seconds, bool simulator)
        {
            if (!Recording) return;
            if (!SavedRowTracks.Finite(e) || !SavedRowTracks.Finite(n) || !SavedRowTracks.Finite(heading) || !SavedRowTracks.Finite(seconds) || simulator != Simulator)
            { Cancel(); throw new InvalidOperationException("Recording stopped: position or position source changed"); }
            var point = new SavedRowPoint(e, n, heading);
            if (points.Count > 0)
            {
                double d = SavedRowTracks.Distance(points.Last(), point);
                if (seconds - lastSeconds > 1.5 || seconds < lastSeconds || d > 2)
                { Cancel(); throw new InvalidOperationException("Recording stopped: position gap or jump. Start a new row with A."); }
                if (d < .25) { lastSeconds = seconds; return; }
            }
            points.Add(point); lastSeconds = seconds;
        }
        public SavedRowTrack Finish(string name)
        {
            if (!Recording || points.Count < 6 || points.Zip(points.Skip(1), SavedRowTracks.Distance).Sum() < 2)
                throw new InvalidOperationException("Row is too short. Record at least 2 m.");
            var row = new SavedRowTrack { Name = name, Source = Source, Simulator = Simulator, Points = points.Select(p => new SavedRowPoint(p.Easting, p.Northing, p.Heading)).ToList() };
            for (int i = 0; i < row.Points.Count; i++)
            {
                var a = row.Points[Math.Max(0, i - 1)]; var b = row.Points[Math.Min(row.Points.Count - 1, i + 1)];
                row.Points[i].Heading = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
            }
            return row;
        }
        public void Cancel() { Recording = false; points.Clear(); }
    }
}
