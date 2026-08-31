# Dev log

Tuning history and numbers that used to live in the README. Kept here so the
README stays readable; this file is allowed to be a wall of dates and
percentages.

## Road/Trail Extraction accuracy

First real-orthophoto result (2026-08-10): correctly traced a real road,
including a fork into a connected bare clearing, but fragmented into 65
separate line segments. Most fragments were short (<10-15m), a normal
skeletonize artifact on any mask edge that isn't perfectly smooth.

A pixel-level fix (`_prune_skeleton_spurs`, eroding free skeleton endpoints
before vectorizing) barely helped (65 -> 63). Most fragments turned out to be
short bridges between two nearby junctions, both ends already connected to
something else, not free-hanging spurs. A branch pixel diagonally touching
two unrelated columns of a straight line is indistinguishable from a real
3-way junction by simple 8-connected neighbor counting.

Replaced (2026-08-11) with `_drop_short_bridges` in `road_extraction.py`,
operating on the vectorized output instead: `RasterToPolyline` endpoints are
exact float coordinates, so there's no pixel-adjacency ambiguity. It deletes
any line under `--min-dangle-m` whose *both* endpoints are shared with
another line, which `minimum_dangle_length` alone can't catch (it only drops
stubs with one free end).

Second problem, same result: the line wandered off the road surface into
wide bare dirt/quarry/stockpile ground alongside it. Root cause:
`land_clearing.py`'s mask flags all bare ground, road or not, and
skeletonize follows the medial axis of whatever shape it's given — on a
wide irregular blob that's the blob's own shape, not a road.
`_remove_wide_regions` filters the mask to "thin enough to be a road" parts
first, same idea as excluding lakes from a river-centerline mask. It's off
by default (`MAX_ROAD_WIDTH_M = 0`): a 12m threshold wiped a real result to
zero features, because on that site the bare-ground corridor (road + graded
shoulder) runs wider than 24m for long stretches, not just at isolated
quarry pockets. One constant can't tell "wide road" from "wide quarry"
without per-site tuning. It's exposed as **Max road width** on the panel
(wired to `--max-width-m` 2026-08-17).

First quantitative check (2026-08-11), Wiedemann et al. 1998 buffer method
(same metric the `microsoft/RoadDetections` README reports), against a
human-digitized road shapefile (~6.2km/11 segments) over its source
orthophoto (1m/px): at a 10m buffer, the then-current defaults scored
correctness 49.8% / completeness 57.9% (F1 53.5%). Confirmed
`MAX_ROAD_WIDTH_M`'s off-by-default status quantitatively — every value from
15-50m scored worse than 0, F1 dropping to 0-18%. Sweeping `exg_threshold`
found a real improvement: 8 peaked at F1 60.3% vs. 53.5% at the old default
(18). Now `road_extraction.DEFAULT_ROAD_EXG_THRESHOLD`, kept separate from
`land_clearing.py`'s own threshold since a centerline benefits from a
narrower mask more than an area-overlap target does. `min_dangle_m` swept
3-15m with no measurable difference; left at 5m.

A learned road mask was the obvious next lever:
`backend/training/road_segmentation_massachusetts.ipynb` trains a small
U-Net on a resolution-matched public dataset, exported to ONNX
(`road_unet.py` / `road_unet.onnx`, `mask_source="unet"`). Trained
(2026-08-12) and measured against the same ground truth: correctness jumped
to 80.2% (vs. ExG's 49.8%) but completeness dropped to 23.5% (vs. 57.9%), net
F1 36.3% — worse than the ExG baseline, so it stays opt-in. Makes sense: the
base model has only seen Massachusetts roads. Fine-tuning it on local ground
truth is the natural next step, blocked on a Kaggle GPU session plus more
local road ground truth than the one 6.2km shapefile.

## Land Clearing Detection accuracy

Boundary looked "too busy/jagged" on first visual check. Fixed with an
opening+closing smoothing pass plus `RasterToPolygon` `SIMPLIFY`; polygon
count on a real orthophoto dropped 389 -> 317 (noise fragments removed),
total area held steady (~30.7 -> ~30.9 ha).

First quantitative check (2026-08-02) against a real human-digitized
ground-truth shapefile: the opening pass (10 iterations) traded recall for
precision more than intended — 73.1%/92.4% (F1 81.6%) vs. 80.0%/77.2% (F1
78.6%) with opening removed. A sweep over opening=0..10 found opening=6
Pareto-dominates the original 10 (76.2%/88.4%, F1 81.9%), now the default.
The QGIS original's `fresh_color` road/river filter was ported as an opt-in
flag but measured worse on this tile (67.9% recall), so it stays off by
default.

Ponds inside the survey area were flagged as "cleared" (2026-08-03): water
has no vegetation greenness either, but reads dark/blue rather than
bright/reddish. Now excluded unconditionally via `WATER_BRIGHTNESS_MAX`.

The Color Reference Sampler tool accumulated 721 real labeled samples
(2026-08-17), driving two recalibrations: `DEFAULT_EXG_THRESHOLD` 18 -> 26
(recall against confirmed bare-ground samples 48.2% -> 92.7%, precision also
improved) and `WATER_BRIGHTNESS_MAX` 90 -> 150 (the old value caught 0 of 61
real water samples — this site's water reads turbid/silty, not the dark
clean water 90 assumed; 150 catches 70% with no bare-soil samples wrongly
excluded). Re-tested `fresh_color` at the new threshold against an
independent ground truth the same day: still net negative (F1 73.5% ->
57.5%), stays off by default.

## Tree Detection status

Backend confirmed working against a real large drone orthophoto
(54150x36052px, 4-band, ~6.3GB, Natural Forest profile): 7,369 trees over
~660 ha, no memory issues. `detect_clearing.py` on the same orthophoto: 389
cleared/bare-ground polygons, ~30.7 ha.

Visual validation (2026-07-31) found real accuracy issues: false positives
on bare/cleared ground (partially fixed by "Exclude cleared/bare ground" +
a stricter `min_density`, 7,369 -> 7,294 points), one crown sometimes split
into multiple points, real crowns missed, false positives on blurred/
stitching-seam regions. The blur issue has a candidate fix
(`exclude_blurry`, variance-of-Laplacian, opt-in, threshold picked by eye
against a synthetic test — not swept against a real blurred region yet).
The split/missed-crown issue needs Sigma/threshold recalibration against
point-level tree-crown ground truth, which needs 5cm/px imagery — not
available right now (2026-08-17 check); the original orthophoto also
couldn't be relocated.

## FIRMS wind-drift overlay (removed)

A wind-direction/smoke-drift overlay was tried and removed: four renderings
(rotated point symbol, decorative bow on a line, RK4-traced streamline
field, dense Windy.com-style dash field) each hit rotation/scale problems
without looking right, and the payoff didn't justify more time.
`Open-Meteo`'s free current-weather API (no key needed) is still the natural
source if revisited — either build the field properly (bilinear-interpolated
wind grid + RK4 streamline tracing) or just point users at Windy.com/BMKG
directly. Confirmed (2026-08-27) against Open-Meteo's
[Terms](https://open-meteo.com/en/terms) and
[License](https://open-meteo.com/en/license) that the free tier's
non-commercial condition fits this project, but the data is CC BY 4.0 —
needs an attribution line in the app/docs before anything actually calls it.
