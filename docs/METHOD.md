# Method

## Import

The tool imports `.step` and `.stp` files with SOLIDWORKS. It disables 3D
Interconnect, respects the STEP file units, and requests solid formation for
neutral surfaces. Only part documents are processed.

## Normalization

For an axis-aligned part box:

```text
center = (min + max) / 2
max_dimension = max(x_size, y_size, z_size)
scale = target_max_dimension / max_dimension
p_normalized = scale * (p_original - center)
```

The same transform is applied to every solid and sheet body. No rotation or PCA
alignment is applied.

After drawing export, a
separate STEP is normalized with `target_max_dimension = 0.0018 m` (`1.8 mm`).

## Drawing

```text
sheet:       blank A4 landscape
projection:  third angle
views:       front, top, right
display:     Hidden Lines Visible (HLV; swHIDDEN_GREYED)
tangent:     hidden
```

## Export

The same SOLIDWORKS drawing is exported as PDF and DXF. SVG is saved directly
when supported.

Temporary native parts, intermediate STEP files, and `.SLDDRW` files are removed
after export.
