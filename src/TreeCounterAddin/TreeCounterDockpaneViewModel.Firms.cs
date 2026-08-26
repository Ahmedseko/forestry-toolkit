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

        // "All Sources" is the recommended default - each satellite passes over at a
        // different time, so one alone can miss a fire the others catch. Originally ported
        // as VIIRS-only (3 sats) from the sgis mobile app's FirmsRefreshWorker, but a real
        // side-by-side test on this same real site (2026-08-26) found a genuine hotspot
        // that only MODIS caught - all 3 VIIRS satellites missed it entirely that day - so
        // MODIS is folded into the default merge too now, not just VIIRS.
        private const string AllViirsSource = "All Sources (VIIRS x3 + MODIS, recommended)";
        private static readonly string[] ViirsSources = { "VIIRS_SNPP_NRT", "VIIRS_NOAA20_NRT", "VIIRS_NOAA21_NRT" };
        private static readonly string[] AllSources = ViirsSources.Append("MODIS_NRT").ToArray();

        public ObservableCollection<string> FirmsSources { get; } = new(
            new[] { AllViirsSource }.Concat(AllSources));

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
                var sourcesToQuery = SelectedFirmsSource == AllViirsSource ? AllSources : new[] { SelectedFirmsSource };

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
                    newLayer.SetRenderer(BuildHeatMapRenderer());
                });

                // Best-effort - a wind lookup failure (network hiccup, Open-Meteo down) is a
                // nice-to-have layer on top of the hotspots that already loaded fine, not
                // worth failing the whole run over.
                var windNote = "";
                try
                {
                    windNote = await AddWindGridAsync(map, extent, project, stamp);
                }
                catch (Exception) { /* soft-fail, see comment above */ }

                var failedNote = failedSources.Count == 0 ? "" :
                    Tr($" ({string.Join(", ", failedSources)} failed, rest OK.)", $" ({string.Join(", ", failedSources)} gagal, sisanya OK.)");
                FirmsStatus = Tr($"Done: {rows.Count} fire hotspot(s) loaded ({string.Join("+", sourcesToQuery)}, last {dayRange} day(s)).{failedNote}{windNote}",
                    $"Selesai: {rows.Count} titik panas dimuat ({string.Join("+", sourcesToQuery)}, {dayRange} hari terakhir).{failedNote}{windNote}");
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

        // Draws density instead of individual dots - a real request after seeing a NASA-
        // style filled heat blob image, vs. this layer's previous discrete colored points.
        // Field = "" counts each point once (not weighted by any numeric column, e.g. FRP -
        // a plain count-density read is what the reference image showed).
        // AutoAdjustPixelIntensity = true instead of a fixed MaxPixelIntensity - lets the
        // renderer self-scale to whatever's actually visible rather than needing this code
        // to pre-compute a density statistic across the loaded points.
        private CIMHeatMapRenderer BuildHeatMapRenderer()
        {
            // Leaving ColorScheme unset turned out to fall back to grayscale, not the
            // red/orange/yellow "hot" look expected (confirmed 2026-08-26 - a real run came
            // back black-and-white) - built explicitly instead of relying on an assumed
            // default. Two linear segments (dark red->orange, orange->yellow) chained via
            // CIMMultipartColorRamp for the classic heat-map gradient.
            CIMColor Rgb(int r, int g, int b) => ColorFactory.Instance.CreateRGBColor(r, g, b);
            var redToOrange = new CIMLinearContinuousColorRamp { FromColor = Rgb(128, 0, 0), ToColor = Rgb(255, 140, 0) };
            var orangeToYellow = new CIMLinearContinuousColorRamp { FromColor = Rgb(255, 140, 0), ToColor = Rgb(255, 255, 0) };

            return new CIMHeatMapRenderer
            {
                Field = "",
                Radius = 15,
                RendererQuality = 8,
                AutoAdjustPixelIntensity = true,
                ColorScheme = new CIMMultipartColorRamp
                {
                    ColorRamps = new CIMColorRamp[] { redToOrange, orangeToYellow },
                    Weights = new[] { 0.5, 0.5 },
                },
                // The Contents pane swatch showed a bare gradient bar with no indication of
                // which end means what (real report, 2026-08-26) - these three are exactly
                // what the CIM model provides for that, no custom legend code needed.
                Heading = Tr("Hotspot point density", "Kepadatan titik panas"),
                MinLabel = Tr("Fewer/sparser points", "Titik lebih sedikit/jarang"),
                MaxLabel = Tr("More/denser points", "Titik lebih banyak/padat"),
            };
        }

        // Open-Meteo (free, no API key - unlike FIRMS) current wind sampled on a regular
        // 5x5 grid spanning the queried extent, bilinear-interpolated (same reasoning as
        // before: averaging the Cartesian u,v components rather than the raw bearing avoids
        // the wrong answer near the 0/360 wrap) to draw a DENSE field of small direction
        // ticks instead of a few streamlines.
        //
        // History: point-symbol rotation (attempt 1) -> arbitrary decorative bow on a
        // straight line (attempt 2) -> real RK4-traced streamlines (attempt 3, correct
        // algorithm, verified against the actual traced vertices in the geodatabase) - all
        // three used real-world line GEOMETRY, which is the wrong tool for this: a line's
        // length is fixed in map units, so it looks fine at the zoom level it was drawn at
        // and enormous once zoomed in further (confirmed with a real zoomed-in screenshot,
        // 2026-08-26). Windy.com's own reference look - many small, densely-packed dashes -
        // is a POINT symbol field: symbol size in ArcGIS is specified in points (screen
        // units), so it stays visually small at any zoom, the way Windy's own dashes do.
        // Reusing the OBJECTID-keyed baked-Angle CIMUniqueValueRenderer technique from the
        // second rotation attempt (confirmed working then) instead of a data-driven
        // expression (confirmed NOT working, twice, earlier in this same investigation).
        private const int WindGridSize = 5;
        private const int WindDashGridX = 14;
        private const int WindDashGridY = 10;

        private async Task<string> AddWindGridAsync(Map map, Envelope extentWgs84, Project project, string stamp)
        {
            var lats = new List<double>();
            var lons = new List<double>();
            for (int iy = 0; iy < WindGridSize; iy++)
            {
                var lat = extentWgs84.YMin + (extentWgs84.YMax - extentWgs84.YMin) * (iy + 0.5) / WindGridSize;
                for (int ix = 0; ix < WindGridSize; ix++)
                {
                    var lon = extentWgs84.XMin + (extentWgs84.XMax - extentWgs84.XMin) * (ix + 0.5) / WindGridSize;
                    lats.Add(lat);
                    lons.Add(lon);
                }
            }

            var latParam = string.Join(",", lats.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));
            var lonParam = string.Join(",", lons.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latParam}&longitude={lonParam}&current=wind_speed_10m,wind_direction_10m&wind_speed_unit=kmh";

            var json = await FirmsHttp.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";

            // Grid alignment matters for the interpolator below (index = iy*WindGridSize+ix),
            // so this fills a fixed-size array by position rather than only appending
            // successfully-parsed entries - a single missing/malformed grid cell aborts the
            // whole layer (soft-fail already wraps this call) instead of silently shifting
            // every later cell into the wrong slot.
            var uGrid = new double[WindGridSize * WindGridSize];
            var vGrid = new double[WindGridSize * WindGridSize];
            double totalSpeed = 0;
            int idx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (idx >= uGrid.Length) break;
                if (!el.TryGetProperty("current", out var current) ||
                    !current.TryGetProperty("wind_speed_10m", out var speedEl) ||
                    !current.TryGetProperty("wind_direction_10m", out var dirEl))
                    return "";
                var speed = speedEl.GetDouble();
                // Open-Meteo's wind_direction is meteorological convention - the direction
                // the wind blows FROM - so smoke drifts the opposite way (+180). Stored as
                // Cartesian (u,v) - east/north components - not the raw bearing, since
                // bilinear-averaging angles directly is wrong close to the 0/360 wrap
                // (averaging 359 deg and 1 deg naively gives 180, the opposite direction);
                // averaging components and re-deriving the angle avoids that.
                var smokeDirRad = ((dirEl.GetDouble() + 180.0) % 360.0) * Math.PI / 180.0;
                uGrid[idx] = speed * Math.Sin(smokeDirRad);
                vGrid[idx] = speed * Math.Cos(smokeDirRad);
                totalSpeed += speed;
                idx++;
            }
            if (idx < uGrid.Length) return "";
            var avgSpeed = totalSpeed / uGrid.Length;

            var lonStepDeg = (extentWgs84.XMax - extentWgs84.XMin) / WindGridSize;
            var latStepDeg = (extentWgs84.YMax - extentWgs84.YMin) / WindGridSize;

            // Bilinear-interpolates the sampled (u,v) grid at an arbitrary lon/lat and
            // returns the local wind bearing there (degrees, same smoke-drift convention as
            // the raw samples).
            double SampleBearingDeg(double lon, double lat)
            {
                var fx = Math.Clamp((lon - extentWgs84.XMin) / lonStepDeg - 0.5, 0, WindGridSize - 1.001);
                var fy = Math.Clamp((lat - extentWgs84.YMin) / latStepDeg - 0.5, 0, WindGridSize - 1.001);
                int ix0 = (int)fx, iy0 = (int)fy;
                int ix1 = Math.Min(ix0 + 1, WindGridSize - 1), iy1 = Math.Min(iy0 + 1, WindGridSize - 1);
                double tx = fx - ix0, ty = fy - iy0;

                double Blend(double[] grid) =>
                    (1 - tx) * (1 - ty) * grid[iy0 * WindGridSize + ix0] + tx * (1 - ty) * grid[iy0 * WindGridSize + ix1] +
                    (1 - tx) * ty * grid[iy1 * WindGridSize + ix0] + tx * ty * grid[iy1 * WindGridSize + ix1];

                var u = Blend(uGrid);
                var v = Blend(vGrid);
                return (Math.Atan2(u, v) * 180.0 / Math.PI + 360.0) % 360.0;
            }

            var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"WindDirection_{stamp}");
            if (!await CreateWindFeatureClassAsync(outputFc, SpatialReferences.WGS84)) return "";

            await QueuedTask.Run(() =>
            {
                using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(project.DefaultGeodatabasePath)));
                using var featureClass = geodatabase.OpenDataset<ArcGIS.Core.Data.FeatureClass>(Path.GetFileName(outputFc));
                using var insertCursor = featureClass.CreateInsertCursor();
                var shapeField = featureClass.GetDefinition().GetShapeField();
                var dirs = new List<double>(WindDashGridX * WindDashGridY);
                for (int iy = 0; iy < WindDashGridY; iy++)
                {
                    var lat = extentWgs84.YMin + (extentWgs84.YMax - extentWgs84.YMin) * (iy + 0.5) / WindDashGridY;
                    for (int ix = 0; ix < WindDashGridX; ix++)
                    {
                        var lon = extentWgs84.XMin + (extentWgs84.XMax - extentWgs84.XMin) * (ix + 0.5) / WindDashGridX;
                        var dirDeg = SampleBearingDeg(lon, lat);
                        dirs.Add(dirDeg);

                        using var rowBuffer = featureClass.CreateRowBuffer();
                        rowBuffer[shapeField] = MapPointBuilderEx.CreateMapPoint(lon, lat, SpatialReferences.WGS84);
                        rowBuffer["WindSpeedKmh"] = avgSpeed;
                        rowBuffer["SmokeDirDeg"] = dirDeg;
                        insertCursor.Insert(rowBuffer);
                    }
                }
                insertCursor.Flush();

                if (LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: Path.GetFileName(outputFc)) is not FeatureLayer newLayer)
                    return;
                newLayer.SetRenderer(BuildWindDashRenderer(dirs));
            });

            return Tr($" Wind: ~{avgSpeed:F0} km/h avg across {WindDashGridX * WindDashGridY} point(s) (smoke drift).",
                $" Angin: ~{avgSpeed:F0} km/h rata-rata dari {WindDashGridX * WindDashGridY} titik (arah asap).");
        }

        // A dense field of small, fixed-screen-size dashes (Windy.com's own look) instead of
        // real-world line geometry - the three earlier attempts (rotated point symbol,
        // decorative bow, RK4-traced streamline) all drew actual map geometry, which looks
        // fine at the zoom level it was drawn at and enormous once zoomed in further
        // (confirmed with a real zoomed-in screenshot, 2026-08-26 - a streamline meant to sit
        // near a hotspot cluster spanned most of the visible map once zoomed in). ArcGIS
        // symbol size is specified in points (screen units, not map units), so this stays
        // visually small at any zoom - structurally fixes the "too long once zoomed in"
        // problem instead of another length tweak.
        //
        // One CIMUniqueValueClass per dash, keyed on OBJECTID with its own baked-in
        // CIMPointSymbol.Angle - the same mechanism already proven to work reliably earlier
        // in this investigation (a data-driven rotation expression was tried twice and
        // silently failed both times; this baked-per-symbol approach didn't) - but
        // constructed the SAME way that earlier confirmed-working version actually built its
        // symbol (a manually-built `new CIMPointSymbol { SymbolLayers = ..., Angle = ... }`
        // in one object initializer), not via SymbolFactory.ConstructPointSymbol(...) with
        // .Angle mutated on the object afterward - that variant came back with every dash
        // pointing the same way regardless of position (real report, 2026-08-26), the same
        // silent-rotation-ignored failure mode as the two data-driven-expression attempts.
        private static CIMUniqueValueRenderer BuildWindDashRenderer(List<double> smokeDirsDeg)
        {
            var color = ColorFactory.Instance.CreateRGBColor(30, 90, 220);

            CIMUniqueValueClass ClassFor(int objectId, double smokeDir)
            {
                // CIMPointSymbol.Angle is arithmetic (counterclockwise from east), not
                // compass bearing (clockwise from north) - standard conversion.
                var mathAngle = (90.0 - smokeDir + 360.0) % 360.0;
                // SymbolFactory.ConstructMarker(...) tried and still failed to rotate (real
                // test, 2026-08-26, both Rod and Triangle) - the marker layer extracted from
                // ConstructPointSymbol(...).SymbolLayers[0] is what the one confirmed-working
                // rotation earlier in this investigation actually used; matched exactly
                // instead of assuming the two factory paths are equivalent.
                var marker = SymbolFactory.Instance.ConstructPointSymbol(color, 9, SimpleMarkerStyle.Triangle).SymbolLayers[0];
                var symbol = new CIMPointSymbol { SymbolLayers = new[] { marker }, Angle = mathAngle };
                return new CIMUniqueValueClass
                {
                    Values = new[] { new CIMUniqueValue { FieldValues = new[] { objectId.ToString(CultureInfo.InvariantCulture) } } },
                    Symbol = symbol.MakeSymbolReference(),
                };
            }

            return new CIMUniqueValueRenderer
            {
                Fields = new[] { "OBJECTID" },
                Groups = new[]
                {
                    new CIMUniqueValueGroup
                    {
                        Classes = smokeDirsDeg.Select((d, i) => ClassFor(i + 1, d)).ToArray(),
                    },
                },
            };
        }

        private static async Task<bool> CreateWindFeatureClassAsync(string fc, SpatialReference sr)
        {
            var gdb = Path.GetDirectoryName(fc);
            var name = Path.GetFileName(fc);
            var createResult = await Geoprocessing.ExecuteToolAsync("management.CreateFeatureclass",
                Geoprocessing.MakeValueArray(gdb, name, "POINT", "", "DISABLED", "DISABLED", sr),
                null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
            if (createResult.IsFailed) return false;

            foreach (var (field, type) in new[] { ("WindSpeedKmh", "DOUBLE"), ("SmokeDirDeg", "DOUBLE") })
            {
                var addResult = await Geoprocessing.ExecuteToolAsync("management.AddField",
                    Geoprocessing.MakeValueArray(fc, field, type),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (addResult.IsFailed) return false;
            }
            return true;
        }


        private sealed record FirmsHotspot(double Lat, double Lon, string AcqDate, string AcqTime, string Confidence, double Frp, string Satellite, string DayNight);

        // VIIRS' confidence column is already categorical (l/n/h) - MODIS' is a 0-100
        // percentage instead. Bucketed here into the same l/n/h scale (FIRMS' own published
        // MODIS tiers: <30 low, 30-80 nominal, >80 high) so the Confidence field stays
        // consistently readable in the attribute table regardless of which source a row
        // came from, even though the layer's own symbology (BuildHeatMapRenderer) is
        // density-based now rather than keyed on this field.
        private static string NormalizeConfidence(string raw)
        {
            var trimmed = raw.Trim().ToLowerInvariant();
            if (trimmed is "h" or "l" or "n") return trimmed;
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return pct > 80 ? "h" : pct < 30 ? "l" : "n";
            return "n";
        }

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
                    NormalizeConfidence(Field(cols, confIdx)), frp, Field(cols, satIdx), Field(cols, dnIdx)));
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
