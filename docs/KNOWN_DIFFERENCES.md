# Known Differences And Limitations

## Drawing Edges

The documented pipeline uses SOLIDWORKS engineering-drawing projection, whose
edge-visibility result can differ from a FreeCAD viewport (due to difference of geometric modeling kernel). Tangent, coplanar,
coincident, internal, or occluded edges can therefore be merged or omitted in
the exported drawing. This release explicitly hides tangent edges and applies
no additional DXF edge filter.

## Versions And Templates

Different SOLIDWORKS versions, service packs, templates, fonts, or locale
settings can produce byte-different PDF and DXF files and may classify some
edges differently.

This release defaults to `gb_a4.drwdot` as the drawing seed. Using a different
seed can change DXF layer, line-type, and line-weight definitions even when the
projected geometry is unchanged.

To produce consistent DXF layer names across SOLIDWORKS installations with
different interface languages, the exporter preserves the standard layer `0`
and renames every other layer to a sequential ASCII number (`1`, `2`, `3`, ...)
in stable layer-ID order. This changes layer names only; colors, line types,
line weights, and entity assignments remain unchanged. You may change layer names to other names if necessary to your cases.

## Bounding Boxes

SOLIDWORKS documents `PartDoc.GetPartBox(true)` as approximate. Manifest box
values are diagnostic and should not be used as exact geometric measurements.

## Inputs

STEP files imported as assemblies are skipped. When `-Recursive` is used, input
files in different directories must not share the same filename stem.
