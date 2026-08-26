using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // NASA FIRMS (Fire Information for Resource Management System) active-fire hotspots,
    // for cross-checking Land Clearing Detection results against real fire activity (a
    // common land-clearing method) and general karhutla monitoring. Uses FIRMS' Area API
    // (https://firms.modaps.eosdis.nasa.gov/api/area/) directly over HttpClient - a plain HTTPS
    // GET returning CSV, no arcpy/Python backend needed for the fetch itself. The MAP_KEY
    // is a free per-user token from firms.modaps.eosdis.nasa.gov, reused through ApiKeyStore
    // (same DPAPI-encrypted dictionary the AI Vision Validation keys already use, just
    // under its own "firms" entry) rather than a second credential store.
    internal partial class TreeCounterDockpaneViewModel
    {
        private static readonly HttpClient FirmsHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

        private string _firmsMapKey;
        public string FirmsMapKey
        {
            get => _firmsMapKey;
            set
            {
                if (SetProperty(ref _firmsMapKey, value))
                {
                    _apiKeysByProvider["firms"] = value ?? "";
                    ApiKeyStore.Save(_apiKeysByProvider);
                }
            }
        }

        // "All VIIRS" is the recommended default - ported from the sgis (SQIS) mobile app's
        // own FirmsRefreshWorker, which found querying all three VIIRS satellites and
        // merging the results catches meaningfully more real hotspots than any single one
        // (each satellite's overpass time differs, so one alone can miss a fire the others
        // catch). MODIS isn't in the merge - coarser 1km resolution and a numeric (not
        // l/n/h) confidence scale would need separate handling to combine cleanly - it
        // stays available as its own standalone pick below for a wider-net manual check.
        private const string AllViirsSource = "All VIIRS (SNPP+NOAA20+NOAA21, recommended)";
        private static readonly string[] ViirsSources = { "VIIRS_SNPP_NRT", "VIIRS_NOAA20_NRT", "VIIRS_NOAA21_NRT" };

        public ObservableCollection<string> FirmsSources { get; } = new(
            new[] { AllViirsSource }.Concat(ViirsSources).Append("MODIS_NRT"));

        private string _selectedFirmsSource = AllViirsSource;
        public string SelectedFirmsSource
        {
            get => _selectedFirmsSource;
            set => SetProperty(ref _selectedFirmsSource, value);
        }

        // FIRMS' own Area API limit - values outside 1-10 are rejected server-side.
        private int _firmsDayRange = 1;
        public int FirmsDayRange
        {
            get => _firmsDayRange;
            set => SetProperty(ref _firmsDayRange, value);
        }

        private bool _isLoadingFirms;
        public bool IsLoadingFirms
        {
            get => _isLoadingFirms;
            set => SetProperty(ref _isLoadingFirms, value);
        }

        private string _firmsStatus = "";
        public string FirmsStatus
        {
            get => _firmsStatus;
            set => SetProperty(ref _firmsStatus, value);
        }

        private CancellationTokenSource _firmsCts;

        public ICommand LoadFirmsHotspotsCommand => new RelayCommand(async () => await LoadFirmsHotspotsAsync(), () => !IsLoadingFirms);
        public ICommand CancelFirmsCommand => new RelayCommand(() => _firmsCts?.Cancel(), () => IsLoadingFirms);

        private bool _isTestingFirmsKey;
        public bool IsTestingFirmsKey
        {
            get => _isTestingFirmsKey;
            set => SetProperty(ref _isTestingFirmsKey, value);
        }

        // Separate from "is FirmsMapKey filled in" - same reasoning TestKeyStatus (AI Vision
        // Validation) already documents: a key that's simply wrong looks identical to one
        // that's never been tried without an explicit confirmation.
        private string _firmsTestKeyStatus = "";
        public string FirmsTestKeyStatus
        {
            get => _firmsTestKeyStatus;
            set => SetProperty(ref _firmsTestKeyStatus, value);
        }

        public ICommand TestFirmsKeyCommand => new RelayCommand(async () => await TestFirmsKeyAsync(), () => !IsTestingFirmsKey && !string.IsNullOrWhiteSpace(FirmsMapKey));

        // FIRMS' own key-status endpoint (mapkey_status) - a valid key returns a flat JSON
        // object with a transaction_limit field; an invalid one doesn't, so that field's
        // presence is what "valid" is checked against rather than parsing for a specific
        // error message that isn't documented anywhere.
        private async Task TestFirmsKeyAsync()
        {
            IsTestingFirmsKey = true;
            FirmsTestKeyStatus = Tr("Testing...", "Menguji...");
            try
            {
                var url = $"https://firms.modaps.eosdis.nasa.gov/mapserver/mapkey_status/?MAP_KEY={FirmsMapKey}";
                var json = await FirmsHttp.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("transaction_limit", out var limitEl))
                {
                    var used = doc.RootElement.TryGetProperty("current_transactions", out var usedEl) ? usedEl.ToString() : "?";
                    FirmsTestKeyStatus = Tr($"OK: valid key, {used}/{limitEl} transactions used (resets every 10 min).",
                        $"OK: key valid, {used}/{limitEl} transaksi terpakai (reset tiap 10 menit).");
                }
                else
                {
                    FirmsTestKeyStatus = Tr($"Invalid key - server response: {json.Trim()}", $"Key tidak valid - respons server: {json.Trim()}");
                }
            }
            catch (Exception ex)
            {
                FirmsTestKeyStatus = Tr($"Failed: {ex.Message}", $"Gagal: {ex.Message}");
            }
            finally
            {
                IsTestingFirmsKey = false;
            }
        }

        // AOI is always the current map view's own extent - no separate layer picker, since
        // "hotspots over whatever I'm currently looking at" is the common case and it keeps
        // this feature to one click instead of an extra dropdown.
        private async Task LoadFirmsHotspotsAsync()
        {
            IsLoadingFirms = true;
            FirmsStatus = Tr("Loading fire hotspots...", "Memuat titik panas...");
            _firmsCts = new CancellationTokenSource();
            try
            {
                if (string.IsNullOrWhiteSpace(FirmsMapKey))
                {
                    FirmsStatus = Tr("No FIRMS MAP_KEY set - add one on the Settings tab (free at firms.modaps.eosdis.nasa.gov).",
                        "MAP_KEY FIRMS belum diisi - tambahkan di tab Settings (gratis di firms.modaps.eosdis.nasa.gov).");
                    return;
                }
                if (MapView.Active == null)
                {
                    FirmsStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    FirmsStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var extent = await QueuedTask.Run(() =>
                {
                    var mapExtent = MapView.Active.Extent;
                    if (mapExtent == null || mapExtent.IsEmpty) return null;
                    return GeometryEngine.Instance.Project(mapExtent, SpatialReferences.WGS84) as Envelope;
                });
                // GeometryEngine.Project can come back with a non-null Envelope that's still
                // garbage (NaN, or coordinates outside valid lat/lon range) if the map's own
                // spatial reference makes the current view's extent fall outside where that
                // projection is well-defined (e.g. a UTM-zone map zoomed out to a
                // near-world view - UTM math breaks down far from its own central meridian).
                // Sending that straight to FIRMS would silently come back with zero rows
                // instead of a clear reason why - caught here instead.
                bool ValidLon(double v) => !double.IsNaN(v) && v >= -180 && v <= 180;
                bool ValidLat(double v) => !double.IsNaN(v) && v >= -90 && v <= 90;
                if (extent == null || !ValidLon(extent.XMin) || !ValidLon(extent.XMax) || !ValidLat(extent.YMin) || !ValidLat(extent.YMax)
                    || extent.XMin >= extent.XMax || extent.YMin >= extent.YMax)
                {
                    FirmsStatus = Tr(
                        "Current map extent didn't convert to a valid lat/lon area - zoom to your actual site on the map first (this can happen zoomed out to a world view on a UTM-projected map).",
                        "Extent map saat ini tidak bisa dikonversi jadi area lat/lon yang valid - zoom dulu ke lokasi kerja Anda di map (bisa terjadi kalau map ter-zoom keluar ke tampilan dunia pada map berproyeksi UTM).");
                    return;
                }

                var dayRange = Math.Clamp(FirmsDayRange, 1, 10);
                var bbox = FormattableString.Invariant($"{extent.XMin},{extent.YMin},{extent.XMax},{extent.YMax}");
                var sourcesToQuery = SelectedFirmsSource == AllViirsSource ? ViirsSources : new[] { SelectedFirmsSource };

                var rows = new List<FirmsHotspot>();
                var failedSources = new List<string>();
                foreach (var source in sourcesToQuery)
                {
                    FirmsStatus = Tr($"Loading {source}...", $"Memuat {source}...");
                    var url = $"https://firms.modaps.eosdis.nasa.gov/api/area/csv/{FirmsMapKey}/{source}/{bbox}/{dayRange}";
                    try
                    {
                        var csv = await FirmsHttp.GetStringAsync(url, _firmsCts.Token);
                        // A bad key or bad params comes back as a plain-text one-liner (e.g.
                        // "Invalid MAP_KEY") instead of CSV - catch that before trying to
                        // parse it as data rows. One source erroring shouldn't sink a merged
                        // query over several sources, so this only skips that one source
                        // (same as sgis's FirmsRefreshWorker) rather than aborting outright -
                        // it still aborts below if literally every source failed.
                        if (!csv.Contains(',')) { failedSources.Add(source); continue; }
                        rows.AddRange(ParseFirmsCsv(csv));
                    }
                    catch (Exception) when (sourcesToQuery.Length > 1)
                    {
                        failedSources.Add(source);
                    }
                }
                if (rows.Count == 0 && failedSources.Count == sourcesToQuery.Length)
                {
                    FirmsStatus = Tr($"FIRMS error on every source queried ({string.Join(", ", failedSources)}).",
                        $"Error FIRMS di semua source yang di-query ({string.Join(", ", failedSources)}).");
                    return;
                }

                if (rows.Count == 0)
                {
                    // Shows the actual lat/lon box that was searched - a genuine "no fires
                    // this week" and "the extent used was nowhere near where you meant" look
                    // identical otherwise, and only one of them means try a wider day range.
                    var areaNote = FormattableString.Invariant($" (area searched: {extent.YMin:F2},{extent.XMin:F2} to {extent.YMax:F2},{extent.XMax:F2})");
                    FirmsStatus = Tr($"Done: no fire hotspots found for this area/date range.{areaNote}",
                        $"Selesai: tidak ada titik panas ditemukan untuk area/rentang tanggal ini.{areaNote}");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"FirmsHotspots_{stamp}");
                var map = MapView.Active.Map;

                var created = await CreateFirmsFeatureClassAsync(outputFc, SpatialReferences.WGS84);
                if (!created)
                {
                    FirmsStatus = Tr("Failed to create the hotspot feature class.", "Gagal membuat feature class titik panas.");
                    return;
                }

                await QueuedTask.Run(() =>
                {
                    InsertFirmsRows(outputFc, rows);
                    if (LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: Path.GetFileName(outputFc)) is not FeatureLayer newLayer)
                        return;
                    newLayer.SetRenderer(BuildConfidenceRenderer());
                });

                var failedNote = failedSources.Count == 0 ? "" :
                    Tr($" ({string.Join(", ", failedSources)} failed, rest OK.)", $" ({string.Join(", ", failedSources)} gagal, sisanya OK.)");
                FirmsStatus = Tr($"Done: {rows.Count} fire hotspot(s) loaded ({string.Join("+", sourcesToQuery)}, last {dayRange} day(s)).{failedNote}",
                    $"Selesai: {rows.Count} titik panas dimuat ({string.Join("+", sourcesToQuery)}, {dayRange} hari terakhir).{failedNote}");
            }
            catch (OperationCanceledException)
            {
                FirmsStatus = Tr("Cancelled.", "Dibatalkan.");
            }
            catch (HttpRequestException ex)
            {
                FirmsStatus = Tr($"Network error: {ex.Message}", $"Error jaringan: {ex.Message}");
            }
            catch (Exception ex)
            {
                FirmsStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _firmsCts?.Dispose();
                _firmsCts = null;
                IsLoadingFirms = false;
            }
        }

        // Same 3-tier confidence colors as the sgis (SQIS) mobile app's FirmsParser:
        // high = red (most likely a real fire), low = amber (more likely noise/false
        // detection), nominal/anything else = the original orange as a middle ground -
        // a flat single color couldn't tell those apart, but VIIRS's own "l"/"n"/"h"
        // confidence field already carries this distinction, it just wasn't used yet.
        // MODIS's confidence field is a 0-100 number instead of l/n/h - it falls through
        // to the default (nominal) color here, since MODIS isn't in the merged/default
        // query path anyway (see AllViirsSource above).
        private static CIMUniqueValueRenderer BuildConfidenceRenderer()
        {
            CIMSymbolReference Dot(int r, int g, int b) => SymbolFactory.Instance
                .ConstructPointSymbol(ColorFactory.Instance.CreateRGBColor(r, g, b), 7, SimpleMarkerStyle.Circle)
                .MakeSymbolReference();

            CIMUniqueValueClass Class(string label, string value, CIMSymbolReference symbol) => new()
            {
                Label = label,
                Symbol = symbol,
                Values = new[] { new CIMUniqueValue { FieldValues = new[] { value } } },
            };

            return new CIMUniqueValueRenderer
            {
                Fields = new[] { "Confidence" },
                Groups = new[]
                {
                    new CIMUniqueValueGroup
                    {
                        Classes = new[]
                        {
                            Class("High confidence", "h", Dot(255, 51, 0)),
                            Class("Low confidence", "l", Dot(255, 170, 0)),
                        },
                    },
                },
                UseDefaultSymbol = true,
                DefaultLabel = "Nominal confidence",
                DefaultSymbol = Dot(255, 102, 0),
            };
        }

        private sealed record FirmsHotspot(double Lat, double Lon, string AcqDate, string AcqTime, string Confidence, double Frp, string Satellite, string DayNight);

        // FIRMS' CSV has no quoted/embedded-comma fields, so a plain split is enough - no
        // CSV library needed. Column order differs between VIIRS and MODIS sources, so
        // columns are looked up by header name instead of a fixed index.
        private static List<FirmsHotspot> ParseFirmsCsv(string csv)
        {
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<FirmsHotspot>();
            if (lines.Length < 2) return result;

            var header = lines[0].TrimEnd('\r').Split(',');
            int Idx(string name) => Array.IndexOf(header, name);
            int latIdx = Idx("latitude"), lonIdx = Idx("longitude"), dateIdx = Idx("acq_date"), timeIdx = Idx("acq_time"),
                confIdx = Idx("confidence"), frpIdx = Idx("frp"), satIdx = Idx("satellite"), dnIdx = Idx("daynight");
            if (latIdx < 0 || lonIdx < 0) return result;

            string Field(string[] cols, int idx) => idx >= 0 && idx < cols.Length ? cols[idx] : "";

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].TrimEnd('\r').Split(',');
                if (cols.Length <= Math.Max(latIdx, lonIdx)) continue;
                if (!double.TryParse(cols[latIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
                if (!double.TryParse(cols[lonIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                double.TryParse(Field(cols, frpIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out var frp);

                result.Add(new FirmsHotspot(lat, lon, Field(cols, dateIdx), Field(cols, timeIdx),
                    Field(cols, confIdx), frp, Field(cols, satIdx), Field(cols, dnIdx)));
            }
            return result;
        }

        // Schema only (CreateFeatureclass + AddField GP tools, same pattern
        // FlightMissionPlanner.cs's CreateWaypointFeatureClassAsync already uses) - rows are
        // written afterward with a plain InsertCursor, since GP tools have no way to take
        // "here are N parsed CSV rows" as an input.
        private static async Task<bool> CreateFirmsFeatureClassAsync(string fc, SpatialReference sr)
        {
            var gdb = Path.GetDirectoryName(fc);
            var name = Path.GetFileName(fc);
            var createResult = await Geoprocessing.ExecuteToolAsync("management.CreateFeatureclass",
                Geoprocessing.MakeValueArray(gdb, name, "POINT", "", "DISABLED", "DISABLED", sr),
                null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
            if (createResult.IsFailed) return false;

            foreach (var (field, type) in new[] { ("AcqDate", "TEXT"), ("AcqTime", "TEXT"), ("Confidence", "TEXT"),
                ("FRP", "DOUBLE"), ("Satellite", "TEXT"), ("DayNight", "TEXT") })
            {
                var addResult = await Geoprocessing.ExecuteToolAsync("management.AddField",
                    Geoprocessing.MakeValueArray(fc, field, type),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (addResult.IsFailed) return false;
            }
            return true;
        }

        // Must run on the MCT (QueuedTask).
        private static void InsertFirmsRows(string fc, List<FirmsHotspot> rows)
        {
            using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(Path.GetDirectoryName(fc))));
            using var featureClass = geodatabase.OpenDataset<ArcGIS.Core.Data.FeatureClass>(Path.GetFileName(fc));
            using var insertCursor = featureClass.CreateInsertCursor();
            var shapeField = featureClass.GetDefinition().GetShapeField();
            foreach (var r in rows)
            {
                using var rowBuffer = featureClass.CreateRowBuffer();
                rowBuffer[shapeField] = MapPointBuilderEx.CreateMapPoint(r.Lon, r.Lat, SpatialReferences.WGS84);
                rowBuffer["AcqDate"] = r.AcqDate;
                rowBuffer["AcqTime"] = r.AcqTime;
                rowBuffer["Confidence"] = r.Confidence;
                rowBuffer["FRP"] = r.Frp;
                rowBuffer["Satellite"] = r.Satellite;
                rowBuffer["DayNight"] = r.DayNight;
                insertCursor.Insert(rowBuffer);
            }
            insertCursor.Flush();
        }
    }
}
