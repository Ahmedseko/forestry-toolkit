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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // NASA FIRMS (Fire Information for Resource Management System) active-fire hotspots,
    // for cross-checking Land Clearing Detection results against real fire activity (a
    // common land-clearing method) and general karhutla monitoring. Uses FIRMS' Area API
    // (https://firms.modaps.eosdis.gov/api/area/) directly over HttpClient - a plain HTTPS
    // GET returning CSV, no arcpy/Python backend needed for the fetch itself. The MAP_KEY
    // is a free per-user token from firms.modaps.eosdis.gov, reused through ApiKeyStore
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

        public ObservableCollection<string> FirmsSources { get; } = new()
        {
            "VIIRS_SNPP_NRT", "VIIRS_NOAA20_NRT", "MODIS_NRT"
        };

        private string _selectedFirmsSource = "VIIRS_SNPP_NRT";
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
                    FirmsStatus = Tr("No FIRMS MAP_KEY set - add one on the Settings tab (free at firms.modaps.eosdis.gov).",
                        "MAP_KEY FIRMS belum diisi - tambahkan di tab Settings (gratis di firms.modaps.eosdis.gov).");
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
                if (extent == null)
                {
                    FirmsStatus = Tr("Could not read the current map extent.", "Tidak bisa membaca extent map saat ini.");
                    return;
                }

                var dayRange = Math.Clamp(FirmsDayRange, 1, 10);
                var bbox = FormattableString.Invariant($"{extent.XMin},{extent.YMin},{extent.XMax},{extent.YMax}");
                var url = $"https://firms.modaps.eosdis.gov/api/area/csv/{FirmsMapKey}/{SelectedFirmsSource}/{bbox}/{dayRange}";

                var csv = await FirmsHttp.GetStringAsync(url, _firmsCts.Token);
                // A bad key or bad params comes back as a plain-text one-liner (e.g.
                // "Invalid MAP_KEY") instead of CSV - catch that before trying to parse it
                // as data rows.
                if (!csv.Contains(','))
                {
                    FirmsStatus = Tr($"FIRMS error: {csv.Trim()}", $"Error FIRMS: {csv.Trim()}");
                    return;
                }

                var rows = ParseFirmsCsv(csv);
                if (rows.Count == 0)
                {
                    FirmsStatus = Tr("Done: no fire hotspots found in the current map extent for this date range.",
                        "Selesai: tidak ada titik panas ditemukan di extent map saat ini untuk rentang tanggal ini.");
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
                    var symbol = SymbolFactory.Instance.ConstructPointSymbol(
                        ColorFactory.Instance.CreateRGBColor(255, 80, 0), 7, SimpleMarkerStyle.Circle);
                    newLayer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
                });

                FirmsStatus = Tr($"Done: {rows.Count} fire hotspot(s) loaded ({SelectedFirmsSource}, last {dayRange} day(s)).",
                    $"Selesai: {rows.Count} titik panas dimuat ({SelectedFirmsSource}, {dayRange} hari terakhir).");
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
